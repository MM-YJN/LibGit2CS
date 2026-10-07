// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Utils;

namespace LibGit2CS.Transports;

/// <summary>
/// HTTP subtransport stream. Implements <see cref="IGitSubtransportStream"/> over
/// an <see cref="HttpClient"/> request/response pair.
/// </summary>
/// <remarks>
/// Managed port of <c>http_stream</c> in
/// <c>src/libgit2/transports/http.c:43-48</c>. For GET services (ref
/// advertisement), only <see cref="ReadAsync"/> is called — the request is sent
/// lazily on the first read. For POST services (negotiation, push), data is
/// buffered via <see cref="WriteAsync"/> then the response is read via
/// <see cref="ReadAsync"/>. For chunked POST (push, <c>git-receive-pack</c>), the
/// buffer is a temp file on disk instead of in-memory, avoiding large memory
/// spikes for big pushes. For non-chunked POST (fetch negotiation), the buffer
/// is a pooled <see cref="PooledByteBufferWriter"/> (small data, rented from
/// <see cref="ArrayPool{Byte}.Shared"/>).
/// </remarks>
internal sealed class HttpStream : IGitSubtransportStream
{
    private readonly GitHttpTransport _owner;

    /// <summary>The owning transport's library context.</summary>
    private GitContext Context => _owner.Context;
    private readonly HttpService _service;
    private readonly GitRemoteConnectOptions? _options;
    private readonly string _originalUrl;
    private readonly bool _useTempFile;
    private readonly string? _tempFilePath;

    private HttpStreamState _state;
    private int _replayCount;
    private HttpResponseMessage? _response;
    private Stream? _responseStream;
    private readonly PooledByteBufferWriter? _writeBuffer;
    private readonly FileStream? _tempFile;
    private bool _disposed;

    /// <summary>
    /// The owning transport's <see cref="GitHttpTransport.Client"/>, which is
    /// set during <see cref="GitHttpTransport.ActionAsync"/> before any stream
    /// is created.
    /// </summary>
    private HttpClient Client
    {
        get
        {
            HttpClient? client = _owner.Client;
            Debug.Assert(client is not null, "HTTP client is configured during ConnectAsync, before any stream exists.");
            return client;
        }
    }

    /// <summary>
    /// The content type for the current service's request body. Only non-null
    /// for POST services (the call sites are in POST code paths).
    /// </summary>
    private string ServiceRequestContentType
    {
        get
        {
            string? contentType = _service.RequestContentType;
            Debug.Assert(contentType is not null, "RequestContentType is non-null for POST services.");
            return contentType;
        }
    }

    /// <summary>Initialize a new HTTP stream for the given service.</summary>
    public HttpStream(GitHttpTransport owner, HttpService service, GitRemoteConnectOptions? options, string url)
    {
        _owner = owner;
        _service = service;
        _options = options;
        _originalUrl = url;
        _state = HttpStreamState.None;

        // Chunked services (push) use a temp file to avoid holding the entire
        // pack in memory. Non-chunked POSTs (fetch negotiation) are small —
        // keep them in a pooled buffer for simplicity.
        _useTempFile = service.Chunked;
        if (_useTempFile)
        {
            _tempFilePath = Path.Join(Path.GetTempPath(), "libgit2cs_push_" + Guid.NewGuid().ToString("N")[..12]);
            _tempFile = new FileStream(_tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920, FileOptions.Asynchronous);
        }
        else
        {
            _writeBuffer = new PooledByteBufferWriter();
        }
    }

    /// <summary>
    /// Read data from the HTTP response body. For GET requests, sends the
    /// request lazily on first call. For POST requests, must be called after
    /// all writes are complete.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes read (0 = EOF).</returns>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == HttpStreamState.None)
        {
            // GET: send request and read response headers
            await SendRequestAndReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_state == HttpStreamState.SendingRequest)
        {
            // POST: finalize the request body and read response headers
            await FinalizePostAndReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_state != HttpStreamState.ReceivingResponse)
        {
            throw new GitException(GitErrorCode.Invalid, "stream is not in receiving state", GitErrorCategory.Net);
        }

        if (_responseStream is null)
        {
            return 0;
        }

        int read = await _responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            _state = HttpStreamState.Done;
        }

        return read;
    }

    /// <summary>
    /// Write data to the HTTP request body (POST services only).
    /// Data is buffered until <see cref="ReadAsync"/> is called, at which point
    /// the complete POST body is sent. This allows retrying the entire
    /// POST if auth fails or a redirect occurs.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_service.Method != HttpMethod.Post)
        {
            throw new GitException(GitErrorCode.Invalid, "write called on a GET stream", GitErrorCategory.Net);
        }

        if (_state == HttpStreamState.None)
        {
            // First write: send probe if needed (NTLM/Negotiate)
            if (_owner.NeedsProbe)
            {
                await SendProbeAsync(cancellationToken).ConfigureAwait(false);
            }

            _state = HttpStreamState.SendingRequest;
        }

        if (_useTempFile)
        {
            await _tempFile!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _writeBuffer!.Write(data.Span);
        }
    }

    /// <summary>
    /// Send the HTTP request (headers only, no body yet) and read the response.
    /// Handles redirects and auth challenges with retry.
    /// Ported from <c>http_stream_read()</c> in <c>http.c:392-459</c>.
    /// </summary>
    private async Task SendRequestAndReadResponseAsync(CancellationToken cancellationToken)
    {
        _replayCount = 0;

        while (_replayCount < GitHttpTransport.MaxReplays)
        {
            HttpRequestMessage request = BuildRequest();
            _response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (await TryHandleResponseAsync(cancellationToken).ConfigureAwait(false) is { Handled: true })
            {
                _state = HttpStreamState.ReceivingResponse;
                _responseStream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _response.Dispose();
            _response = null;
            _replayCount++;
        }

        throw new GitException(GitErrorCode.Auth, "too many redirects or authentication replays", GitErrorCategory.Net);
    }

    /// <summary>
    /// Finalize a POST: send buffered body, read response. Handles redirects
    /// and auth challenges with retry.
    /// Ported from <c>http_stream_read_response()</c> in <c>http.c:596-630</c>.
    /// </summary>
    /// <remarks>
    /// For chunked services (push), the body is in a temp file — we stream it
    /// via <see cref="StreamContent"/> with a <see cref="NonDisposingStream"/>
    /// wrapper so the temp file survives request disposal during auth retry.
    /// For non-chunked POSTs, the body is in a pooled buffer — we use
    /// <see cref="ReadOnlyMemoryContent"/> to pass the rented memory directly
    /// (no <c>ToArray()</c> copy, small data, simple retry).
    /// </remarks>
    private async Task FinalizePostAndReadResponseAsync(CancellationToken cancellationToken)
    {
        _replayCount = 0;

        // For temp-file mode, prepare the stream for reading from the start
        if (_useTempFile)
        {
            FileStream fs = _tempFile!;
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            fs.Position = 0;
        }

        while (_replayCount < GitHttpTransport.MaxReplays)
        {
            HttpRequestMessage request = BuildRequest();

            HttpContent content;
            if (_useTempFile)
            {
                // Stream the temp file content — NonDisposingStream prevents
                // request.Dispose() from closing the file before a retry.
                _tempFile!.Position = 0;
                content = new StreamContent(new NonDisposingStream(_tempFile));
            }
            else
            {
                // Pooled buffer: pass the rented memory directly to
                // ReadOnlyMemoryContent — no ToArray() copy on the hot path.
                content = new ReadOnlyMemoryContent(_writeBuffer!.WrittenMemory);
            }

            request.Content = content;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ServiceRequestContentType);

            _response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (await TryHandleResponseAsync(cancellationToken).ConfigureAwait(false) is { Handled: true })
            {
                _state = HttpStreamState.ReceivingResponse;
                _responseStream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _response.Dispose();
            _response = null;
            _replayCount++;
        }

        throw new GitException(GitErrorCode.Auth, "too many redirects or authentication replays", GitErrorCategory.Net);
    }

    /// <summary>
    /// Send a probe POST with <c>0000</c> body to establish NTLM/Negotiate auth
    /// before the real chunked POST. Ported from <c>send_probe()</c> in
    /// <c>http.c:469-509</c>.
    /// </summary>
    private async Task SendProbeAsync(CancellationToken cancellationToken)
    {
        int steps = _owner.ServerAuthSchemes == GitAuthSchemeType.Ntlm ? 2 : 1;
        byte[] probeBody = "0000"u8.ToArray();

        for (int step = 0; step < steps; step++)
        {
            HttpRequestMessage request = BuildRequest();
            request.Content = new ByteArrayContent(probeBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ServiceRequestContentType);

            using HttpResponseMessage probeResponse = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            if (await TryHandleResponseAsync(cancellationToken).ConfigureAwait(false) is not { Handled: true })
            {
                // If auth is not complete yet, continue to next step
            }
        }
    }

    /// <summary>
    /// Build an <see cref="HttpRequestMessage"/> for this stream's service.
    /// Ported from <c>generate_request()</c> in <c>http.c:353-384</c>.
    /// </summary>
    private HttpRequestMessage BuildRequest()
    {
        Uri requestUri = _owner.BuildRequestUri(_service);
        var request = new HttpRequestMessage(_service.Method, requestUri);

        // User-Agent header — C (httpclient.c:652-668): product + optional
        // "(comment)", default "git/2.0 (libgit2 1.9.4)". Configurable via
        // GitSettings (GIT_OPT_SET_USER_AGENT[_PRODUCT]).
        string product = Context.Settings.UserAgentProduct; // via Context property below
        string comment = Context.Settings.UserAgentComment;
        string userAgent = comment.Length > 0 ? $"{product} ({comment})" : product;

        // C (httpclient.c:652-673): `if (!*product) return 0;` — with an EMPTY product no User-Agent header is sent.
        if (product.Length > 0)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        // Accept header. C (http.c:375-380): the content type is sent only in the POST branch; GET falls through to "Accept: */*".
        request.Headers.Accept.ParseAdd(_service.Method == HttpMethod.Post
            ? _service.ResponseContentType
            : "*/*");

        // Custom headers
        if (_owner.CanSendCustomHeaders(requestUri) && _options?.CustomHeaders is { } headers)
        {
            foreach (string header in headers)
            {
                int colon = header.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    string name = header[..colon].Trim();
                    string value = header[(colon + 1)..].Trim();
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        // Auth headers
        _owner.ApplyAuthHeaders(request, isProxy: false);
        _owner.ApplyAuthHeaders(request, isProxy: true);

        return request;
    }

    /// <summary>
    /// Process the HTTP response. Handles redirects (301-308), 401, 407, and
    /// validates content type. Ported from <c>handle_response()</c> in
    /// <c>http.c:235-301</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>(Handled, Complete) — True if the response is handled and we should proceed to read body; Complete is true for 200 OK with valid content type.</returns>
    private ValueTask<(bool Handled, bool Complete)> TryHandleResponseAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = _response;
        Debug.Assert(response is not null, "TryHandleResponseAsync is called immediately after assigning _response.");
        int status = (int)response.StatusCode;

        // Redirect?
        if (status is 301 or 302 or 303 or 307 or 308)
        {
            // C (http.c:596-618): the POST path calls handle_response with allow_redirect=false — a POST redirect is ALWAYS "unexpected redirect", even under
            // FollowRedirects=All.
            if (_service.Method == HttpMethod.Post)
            {
                throw new GitException(GitErrorCode.Invalid, $"unexpected redirect to {response.Headers.Location}", GitErrorCategory.Net);
            }

            GitRemoteRedirect follow = _options?.FollowRedirects ?? GitRemoteRedirect.Initial;
            if (!GitHttpTransport.AllowRedirect(follow, _service.IsInitial))
            {
                throw new GitException(GitErrorCode.Invalid, $"unexpected redirect to {response.Headers.Location}", GitErrorCategory.Net);
            }

            if (response.Headers.Location is null)
            {
                throw new GitException(GitErrorCode.Invalid, "redirect response missing Location header", GitErrorCategory.Net);
            }

            // the follow policy also gates offsite host changes
            // (git_net_url_apply_redirect's allow_offsite parameter, net.c:958-966).
            _owner.ApplyRedirect(response.Headers.Location.ToString(), GitHttpTransport.AllowRedirect(follow, _service.IsInitial));
            return ValueTask.FromResult((false, false)); // retry
        }

        // Server auth required (401) / proxy auth required (407) — the only
        // async branches (user credential callbacks may yield).
        if (status == 401)
        {
            return new ValueTask<(bool Handled, bool Complete)>(HandleServerAuthSlowAsync(response, cancellationToken));
        }

        if (status == 407)
        {
            return new ValueTask<(bool Handled, bool Complete)>(HandleProxyAuthSlowAsync(response, cancellationToken));
        }

        // Non-200 error
        if (status != 200)
        {
            throw new GitException(GitErrorCode.Error, $"HTTP {status}: {response.ReasonPhrase}", GitErrorCategory.Http);
        }

        // Validate Content-Type. C (http.c:293): strcmp against the FULL header value — case-sensitive, and any "; charset=" parameter mismatches. The port
        // compared the parameter-stripped MediaType case-insensitively.
        string? contentType = response.Content.Headers.ContentType?.ToString();
        if (contentType is null)
        {
            throw new GitException(GitErrorCode.Invalid, "response missing Content-Type", GitErrorCategory.Net);
        }

        if (!contentType.Equals(_service.ResponseContentType, StringComparison.Ordinal))
        {
            throw new GitException(GitErrorCode.Invalid, $"unexpected Content-Type: {contentType}", GitErrorCategory.Net);
        }

        // 200 OK with a valid Content-Type — the majority synchronous case.
        return ValueTask.FromResult((true, true));
    }

    /// <summary>Slow path of <see cref="TryHandleResponseAsync"/>: resolves 401 server auth (credential callback).</summary>
    private async Task<(bool Handled, bool Complete)> HandleServerAuthSlowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (await _owner.HandleServerAuthAsync(response, _options, cancellationToken).ConfigureAwait(false))
        {
            return (false, false); // retry with auth
        }

        throw new GitException(GitErrorCode.Auth, "authentication required", GitErrorCategory.Net);
    }

    /// <summary>Slow path of <see cref="TryHandleResponseAsync"/>: resolves 407 proxy auth (credential callback).</summary>
    private async Task<(bool Handled, bool Complete)> HandleProxyAuthSlowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (await _owner.HandleProxyAuthAsync(response, _options, cancellationToken).ConfigureAwait(false))
        {
            return (false, false); // retry with proxy auth
        }

        throw new GitException(GitErrorCode.Auth, "proxy authentication required", GitErrorCategory.Net);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _responseStream?.Dispose();
        _response?.Dispose();
        _writeBuffer?.Dispose();
        _tempFile?.Dispose();

        if (_useTempFile && _tempFilePath is not null)
        {
            try
            {
                File.Delete(_tempFilePath);
            }
            catch { }
        }

        _disposed = true;
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// A <see cref="Stream"/> wrapper whose <see cref="Dispose"/> is a no-op.
    /// Used when wrapping a <see cref="FileStream"/> in <see cref="StreamContent"/>
    /// so that <see cref="System.Net.Http.HttpRequestMessage.Dispose()"/> →
    /// <see cref="StreamContent.Dispose"/> does not close the temp file
    /// before an auth-retry can re-read it.
    /// </summary>
    private sealed class NonDisposingStream : Stream
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Intentional — this wrapper's Dispose is a no-op by design; the owner of the underlying stream manages its lifetime (see class doc).")]
        private readonly Stream _inner;

        internal NonDisposingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            // No-op — the owner of the underlying stream manages its lifetime
            base.Dispose(disposing);
        }
    }
}

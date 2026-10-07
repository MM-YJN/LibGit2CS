// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Core;

using LibSsh2CS;

namespace LibGit2CS.Transports;

/// <summary>
/// Smart subtransport stream over an SSH channel. Managed port of
/// <c>ssh_stream</c> + <c>gen_proto</c> + <c>send_command</c> +
/// <c>ssh_stream_read</c> + <c>ssh_stream_write</c> +
/// <c>ssh_stream_free</c> in
/// <c>src/libgit2/transports/ssh_libssh2.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// The exec command (<c>git-upload-pack '&lt;path&gt;'</c>) is sent lazily
/// on the first <see cref="ReadAsync"/>/<see cref="WriteAsync"/> call,
/// matching the C pattern in <c>send_command</c> (lines 90-110). This is
/// critical for the smart protocol's half-duplex negotiation ordering.
/// </para>
/// <para>
/// On EOF (channel.ReadAsync returns 0), the stream probes stderr and, if
/// non-empty, throws <see cref="GitException"/> with the stderr text as the
/// message (parity <c>ssh_stream_read</c> lines 112-150). Without this,
/// server errors like "Repository not found" become silent EOFs.
/// </para>
/// <para>
/// The CALLER must NOT call <c>SendEofAsync</c> — <see cref="DisposeAsync"/>
/// delegates to <c>SshChannel.DisposeAsync</c> which sends EOF + CLOSE and
/// waits for peer CLOSE (parity
/// <c>_libssh2_channel_close</c> at <c>channel.c:2658-2667</c>).
/// </para>
/// </remarks>
internal sealed class SshStream : IGitSubtransportStream
{
    private const int StderrProbeBufferSize = 4096;

    private readonly ISshChannel _channel;
    private readonly GitSmartService _service;
    private readonly string _path;
    private bool _execSent;
    private bool _disposed;

    /// <summary>
    /// Creates a stream over the given SSH channel.
    /// </summary>
    /// <param name="channel">The SSH channel (session type, opened after auth).</param>
    /// <param name="service">The git service to invoke.</param>
    /// <param name="path">
    /// The remote repo path (verbatim from the parsed SSH URL; may begin with
    /// <c>/~</c> for <c>ssh://host/~user/repo</c> URLs).
    /// </param>
    public SshStream(ISshChannel channel, GitSmartService service, string path)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _service = service;
        _path = path;
    }

    /// <inheritdoc/>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await SendExecAsync(cancellationToken).ConfigureAwait(false);

        int n;
        try
        {
            n = await _channel.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex)
        {
            // Parity ssh_stream_read (ssh_libssh2.c:127): a channel read
            // failure surfaces with libgit2's exact message as a GitException.
            throw new GitException(GitErrorCode.Error, "SSH could not read data", GitErrorCategory.Ssh, ex);
        }

        if (n == 0)
        {
            // parity ssh_stream_read (lines 112-150): probe stderr on EOF.
            // A non-empty stderr surface is a server error message ("Repository
            // not found", "access denied", etc.) — surface it as the exception
            // message instead of letting it become a silent EOF.
            byte[] stderrBuf = new byte[StderrProbeBufferSize];
            int sn;
            try
            {
                sn = await _channel.ReadStderrAsync(stderrBuf, cancellationToken).ConfigureAwait(false);
            }
            catch (SshException ex)
            {
                // Parity ssh_libssh2.c:140-141 ("SSH could not read stderr").
                throw new GitException(GitErrorCode.Error, "SSH could not read stderr", GitErrorCategory.Ssh, ex);
            }

            if (sn > 0)
            {
                string message = Encoding.UTF8.GetString(stderrBuf, 0, sn);
                // Parity ssh_libssh2.c:136-139: the probe message carries the
                // GIT_ERROR_SSH class.
                throw new GitException(GitErrorCode.Eof, message, GitErrorCategory.Ssh);
            }

            return 0;
        }

        return n;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await SendExecAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex)
        {
            // Parity ssh_stream_write (ssh_libssh2.c:173): "SSH could not
            // write data".
            throw new GitException(GitErrorCode.Error, "SSH could not write data", GitErrorCategory.Ssh, ex);
        }
    }

    /// <summary>
    /// Signal end-of-file on the write side of the channel. Used by the
    /// push protocol after the pack is written — <c>git-receive-pack</c>
    /// reads the pack until EOF, then sends the report-status response.
    /// The read side stays open for the report. This is the SSH equivalent
    /// of closing the write half of a TCP connection.
    /// </summary>
    public async Task SendWriteEofAsync(CancellationToken cancellationToken)
    {
        await _channel.SendEofAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send the exec command on first I/O. Lazy-send parity with
    /// <c>send_command</c> (<c>ssh_libssh2.c:90-110</c>).
    /// </summary>
    private ValueTask SendExecAsync(CancellationToken cancellationToken)
    {
        if (_execSent)
        {
            // Flag already set — the majority case after the first I/O.
            return ValueTask.CompletedTask;
        }

        return new ValueTask(SendExecSlowAsync(cancellationToken));
    }

    /// <summary>Slow path of <see cref="SendExecAsync"/>: sends the exec command (SSH network IO).</summary>
    private async Task SendExecSlowAsync(CancellationToken cancellationToken)
    {
        string command = BuildExecCommand(_service, _path);
        try
        {
            await _channel.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex)
        {
            // Parity send_command (ssh_libssh2.c:98-101): "SSH could not
            // execute request".
            throw new GitException(GitErrorCode.Error, "SSH could not execute request", GitErrorCategory.Ssh, ex);
        }

        _execSent = true;
    }

    /// <summary>
    /// Build the remote exec command for the given service + path. Pure
    /// function (no instance state) so tests can assert byte-exact output
    /// without driving a channel. Parity with <c>gen_proto</c>
    /// (<c>ssh_libssh2.c:65-88</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Format: <c>&lt;cmd&gt; '&lt;path&gt;'</c> — the path is single-quoted
    /// with <b>no shell escaping</b> of single quotes inside the path (parity
    /// with <c>gen_proto</c>, which does <c>snprintf(buf, " %s '%s'", cmd,
    /// path)</c>).
    /// </para>
    /// <para>
    /// <c>/~</c>-prefix stripping: if the path starts with <c>/~</c>, the
    /// leading <c>/</c> is stripped so the remote shell expands <c>~user</c>
    /// (parity <c>gen_proto</c> lines 71-72: <c>if (repo[0] == '/' &amp;&amp;
    /// repo[1] == '~') repo++</c>). For SCP-style URLs (no leading <c>/</c>),
    /// the path is passed verbatim.
    /// </para>
    /// </remarks>
    /// <param name="service">One of <see cref="GitSmartService.UploadPackLs"/>,
    /// <see cref="GitSmartService.UploadPack"/>,
    /// <see cref="GitSmartService.ReceivePackLs"/>,
    /// <see cref="GitSmartService.ReceivePack"/>.</param>
    /// <param name="path">The remote repo path (verbatim from the parsed SSH URL).</param>
    /// <returns>The exec command string, e.g.
    /// <c>git-upload-pack '~user/repo'</c>.</returns>
    /// <exception cref="GitException">If <paramref name="path"/> starts with
    /// <c>-</c> (cmdline-option injection guard — parity <c>ssh_libssh2.c:801</c>;
    /// only fires for SCP-style paths, since scheme-style paths are
    /// <c>/</c>-prefixed).</exception>
    internal static string BuildExecCommand(GitSmartService service, string path)
    {
        string cmd = service switch
        {
            GitSmartService.UploadPackLs or GitSmartService.UploadPack => "git-upload-pack",
            GitSmartService.ReceivePackLs or GitSmartService.ReceivePack => "git-receive-pack",
            _ => throw new GitException(GitErrorCode.Invalid, $"unknown SSH service: {service}", GitErrorCategory.Net),
        };

        // Parity gen_proto lines 71-72: strip leading '/' if path starts with '/~'
        // so the remote shell expands the '~user' prefix.
        string repo = path.StartsWith("/~", StringComparison.Ordinal) ? path[1..] : path;

        // Cmdline-option injection guard (parity ssh_libssh2.c:801). For
        // scheme-style URLs the path is '/'-prefixed so this never fires; it
        // only fires for SCP-style paths (which have no leading '/').
        if (repo.Length > 0 && repo[0] == '-')
        {
            throw new GitException(GitErrorCode.Invalid, "path begins with '-'", GitErrorCategory.Net);
        }

        // Single-quoted, no shell escaping of inner quotes (parity gen_proto).
        return $"{cmd} '{repo}'";
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Caller must NOT call SendEofAsync — SshChannel.DisposeAsync sends
        // EOF + CLOSE + waits for peer CLOSE.
        await _channel.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}

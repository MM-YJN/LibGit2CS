// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Utils;

namespace LibGit2CS.Transports;

/// <summary>
/// Stream over a TCP socket for the <c>git://</c> protocol.
/// Managed port of <c>git_proto_stream</c> in <c>src/libgit2/transports/git.c</c>.
/// </summary>
/// <remarks>
/// Wraps a <see cref="GitSocket"/>. The git protocol command
/// (<c>git-upload-pack /path\0host=host\0</c>) is sent lazily on the first
/// <see cref="ReadAsync"/> or <see cref="WriteAsync"/> call, matching the C
/// pattern in <c>git_proto_stream_read</c>/<c>git_proto_stream_write</c>.
/// </remarks>
public sealed class GitStream : IGitSubtransportStream
{
    private readonly GitSocket _socket;
    private readonly string _cmd;
    private readonly string _url;
    private readonly string _host;
    private bool _sentCommand;
    private bool _disposed;

    /// <summary>
    /// Creates a stream over the given socket.
    /// </summary>
    /// <param name="socket">The connected TCP socket.</param>
    /// <param name="cmd">The git command (<c>git-upload-pack</c> or <c>git-receive-pack</c>).</param>
    /// <param name="url">The URL path component (e.g. <c>/path/to/repo.git</c>).</param>
    /// <param name="host">The hostname for the <c>host=</c> field.</param>
    public GitStream(GitSocket socket, string cmd, string url, string host)
    {
        _socket = socket;
        _cmd = cmd;
        _url = url;
        _host = host;
    }

    /// <inheritdoc/>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await EnsureCommandSentAsync(cancellationToken).ConfigureAwait(false);
        return await _socket.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await EnsureCommandSentAsync(cancellationToken).ConfigureAwait(false);
        await _socket.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send the initial git protocol request on first I/O.
    /// Matches <c>send_command</c> + <c>gen_proto</c> in <c>git.c:40-90</c>.
    /// </summary>
    private ValueTask EnsureCommandSentAsync(CancellationToken cancellationToken)
    {
        if (_sentCommand)
        {
            // Flag already set — the majority case after the first I/O.
            return ValueTask.CompletedTask;
        }

        return new ValueTask(EnsureCommandSentSlowAsync(cancellationToken));
    }

    /// <summary>Slow path of <see cref="EnsureCommandSentAsync"/>: writes the initial request (network IO).</summary>
    private async Task EnsureCommandSentSlowAsync(CancellationToken cancellationToken)
    {
        using PooledByteBufferWriter request = GenerateProtocol(_cmd, _url, _host);
        await _socket.WriteAsync(request.WrittenMemory, cancellationToken).ConfigureAwait(false);
        _sentCommand = true;
    }

    /// <summary>
    /// Build the git protocol request packet.
    /// Matches <c>gen_proto</c> in <c>git.c:40-72</c>.
    /// </summary>
    /// <remarks>
    /// Format: <c>%04x%s %s%c%s%s%c</c> — 4-hex-digit length (includes itself),
    /// command, space, repo path, NUL, <c>host=hostname</c>, NUL.
    /// Example: <c>0035git-upload-pack /path\0host=example.com\0</c>.
    /// </remarks>
    /// <param name="cmd">The git command (e.g. <c>git-upload-pack</c>).</param>
    /// <param name="url">The URL after the <c>git://</c> prefix (e.g. <c>host/path</c> or <c>/path</c>).</param>
    /// <param name="host">The hostname for the <c>host=</c> field.</param>
    /// <returns>The raw request bytes.</returns>
    internal static PooledByteBufferWriter GenerateProtocol(ReadOnlySpan<char> cmd, ReadOnlySpan<char> url, ReadOnlySpan<char> host)
    {
        // Extract the repo path: find the first '/' in the URL after the host part.
        // C: delim = strchr(url, '/'); repo = delim; if repo[1]=='~' ++repo
        int slashIdx = url.IndexOf('/');
        if (slashIdx < 0)
        {
            throw new GitException(GitErrorCode.Invalid, "malformed git:// URL", GitErrorCategory.Net);
        }

        ReadOnlySpan<char> repo = url[slashIdx..];
        if (repo.Length > 1 && repo[1] == '~')
        {
            repo = repo[1..];
        }

        // Build: "cmd repo\0host=hostname\0"
        // Length = 4 (hex length) + cmd + 1 (space) + repo + 1 (NUL) + "host=" + host + 1 (NUL)
        int payloadLen = cmd.Length + 1 + repo.Length + 1 + "host=".Length + Encoding.UTF8.GetByteCount(host) + 1;
        int totalLen = 4 + payloadLen;

        if (totalLen > 0xFFFF)
        {
            throw new GitException(GitErrorCode.Invalid, "git:// request too large", GitErrorCategory.Net);
        }

        var writer = new PooledByteBufferWriter();
        GitPacketWriter.WriteHexLength(writer, totalLen);
        GitPacketWriter.WriteUtf8(writer, cmd);
        writer.Write(" "u8);
        GitPacketWriter.WriteUtf8(writer, repo);
        writer.Write("\0host="u8);
        GitPacketWriter.WriteUtf8(writer, host);
        writer.Write("\0"u8);

        return writer;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _socket.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}

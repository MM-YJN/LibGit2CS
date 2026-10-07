using LibGit2CS.Core;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Unit tests for <see cref="SshStream"/> exec-command construction, lazy
/// exec behavior, stderr-probe-on-read-zero, and clean close. Uses a
/// <see cref="FakeSshChannel"/> — no real SSH server.
/// </summary>
public sealed class SshStreamTests
{
    // ── BuildExecCommand (pure helper — byte-exact parity gen_proto) ────

    [Theory]
    [InlineData(GitSmartService.UploadPackLs, "/path/to/repo.git", "git-upload-pack '/path/to/repo.git'")]
    [InlineData(GitSmartService.UploadPack, "/path/to/repo.git", "git-upload-pack '/path/to/repo.git'")]
    [InlineData(GitSmartService.ReceivePackLs, "/path/to/repo.git", "git-receive-pack '/path/to/repo.git'")]
    [InlineData(GitSmartService.ReceivePack, "/path/to/repo.git", "git-receive-pack '/path/to/repo.git'")]
    public void BuildExecCommand_BasicPath_IsByteExact(GitSmartService service, string path, string expected)
    {
        Assert.Equal(expected, SshStream.BuildExecCommand(service, path));
    }

    [Fact]
    public void BuildExecCommand_TildePath_StripsLeadingSlash()
    {
        // ssh://host/~user/repo → remote cmd "git-upload-pack '~user/repo'"
        // (parity gen_proto lines 71-72).
        Assert.Equal("git-upload-pack '~user/repo'", SshStream.BuildExecCommand(GitSmartService.UploadPackLs, "/~user/repo"));
    }

    [Fact]
    public void BuildExecCommand_RelativeScpPath_StaysVerbatim()
    {
        // SCP-style has no leading '/', so the /~ stripping doesn't fire.
        Assert.Equal("git-upload-pack 'user/repo.git'", SshStream.BuildExecCommand(GitSmartService.UploadPackLs, "user/repo.git"));
    }

    [Fact]
    public void BuildExecCommand_SingleQuotesNotEscaped()
    {
        // Parity gen_proto: single-quoted path, NO shell escaping of inner
        // single quotes. A path with a single quote is passed verbatim
        // (libgit2's behavior — the remote shell handles it).
        Assert.Equal("git-upload-pack 'path'with'quote'", SshStream.BuildExecCommand(GitSmartService.UploadPackLs, "path'with'quote"));
    }

    [Theory]
    [InlineData("-evil-path")]
    [InlineData("-")]
    public void BuildExecCommand_DashScpPath_Rejected(string path)
    {
        // Cmdline-option injection guard (parity ssh_libssh2.c:801). Fires
        // for SCP-style paths (no leading '/').
        GitException ex = Assert.Throws<GitException>(() => SshStream.BuildExecCommand(GitSmartService.UploadPackLs, path));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("'-'", ex.Message);
    }

    [Theory]
    [InlineData("/-evil")]   // scheme-style paths are '/'-prefixed → guard does NOT fire
    [InlineData("/path/-normal")]  // dash mid-path is fine
    public void BuildExecCommand_DashSchemeStyle_NotRejected(string path)
    {
        // The guard only fires when repo[0] == '-', which can't happen for
        // scheme-style paths (they begin with '/'). Parity is pinned by
        // GitSshUrlTests.Reject_DashPath_SchemeStyle_NotRejected_PathStartsWithSlash.
        string cmd = SshStream.BuildExecCommand(GitSmartService.UploadPackLs, path);
        Assert.StartsWith("git-upload-pack '", cmd);
    }

    [Fact]
    public void BuildExecCommand_UnknownService_Throws()
    {
        GitException ex = Assert.Throws<GitException>(() => SshStream.BuildExecCommand((GitSmartService)999, "/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    // ── Lazy exec (deferred to first I/O) ───────────────────────────────

    [Fact]
    public void Exec_IsLazy_NotSentOnConstruct()
    {
        var channel = new FakeSshChannel();
        _ = new SshStream(channel, GitSmartService.UploadPackLs, "/path");

        // Constructing the stream must not send exec.
        Assert.Empty(channel.ExecCalls);
        Assert.False(channel.SendEofCalled);
    }

    [Fact]
    public async Task Exec_SentOnce_OnFirstRead()
    {
        var channel = new FakeSshChannel();
        channel.StdoutData.Enqueue("hello"u8.ToArray());
        var stream = new SshStream(channel, GitSmartService.UploadPackLs, "/repo");

        byte[] buffer = new byte[64];
        int n = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(5, n);
        Assert.Single(channel.ExecCalls);
        Assert.Equal("git-upload-pack '/repo'", channel.ExecCalls[0]);

        // Second read must not re-send exec.
        channel.StdoutData.Enqueue("world"u8.ToArray());
        _ = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Single(channel.ExecCalls);
    }

    [Fact]
    public async Task Exec_SentOnce_OnFirstWrite()
    {
        var channel = new FakeSshChannel();
        var stream = new SshStream(channel, GitSmartService.ReceivePackLs, "/repo");

        await stream.WriteAsync("abc"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Single(channel.ExecCalls);
        Assert.Equal("git-receive-pack '/repo'", channel.ExecCalls[0]);
    }

    // ── stderr-probe-on-read-zero (parity ssh_stream_read lines 112-150) ──

    [Fact]
    public async Task ReadZero_EmptyStderr_ReturnsZero()
    {
        var channel = new FakeSshChannel
        {
            ReadReturnZeroOnStdout = true // EOF on stdout
        };
        channel.StderrData.Enqueue(Array.Empty<byte>()); // empty stderr
        var stream = new SshStream(channel, GitSmartService.UploadPackLs, "/repo");

        int n = await stream.ReadAsync(new byte[64], TestContext.Current.CancellationToken);

        Assert.Equal(0, n);
    }

    [Fact]
    public async Task ReadZero_NonEmptyStderr_ThrowsEofWithStderrMessage()
    {
        var channel = new FakeSshChannel
        {
            ReadReturnZeroOnStdout = true
        };
        channel.StderrData.Enqueue("Repository not found\n"u8.ToArray());
        var stream = new SshStream(channel, GitSmartService.UploadPackLs, "/repo");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await stream.ReadAsync(new byte[64], TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Eof, ex.Code);
        // Parity ssh_libssh2.c:136-139: the stderr-probe message carries the
        // GIT_ERROR_SSH class.
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
        Assert.Contains("Repository not found", ex.Message);
    }

    // ── Clean close (parity ssh_stream_free) ────────────────────────────

    [Fact]
    public async Task Dispose_DoesNotCallSendEof()
    {
        var channel = new FakeSshChannel();
        var stream = new SshStream(channel, GitSmartService.UploadPackLs, "/repo");

        await stream.DisposeAsync();

        Assert.True(channel.DisposeAsyncCalled);
        // The caller must NOT call SendEofAsync — SshChannel.DisposeAsync
        // sends EOF internally.
        Assert.False(channel.SendEofCalled);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var channel = new FakeSshChannel();
        var stream = new SshStream(channel, GitSmartService.UploadPackLs, "/repo");

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Equal(1, channel.DisposeAsyncCallCount);
    }

    // ── Fake ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scripts channel behavior: queued stdout/stderr data, exec-call
    /// recording, dispose tracking. Does NOT call SendEof (parity: the
    /// transport never calls SendEof; SshChannel.DisposeAsync handles it).
    /// </summary>
    private sealed class FakeSshChannel : ISshChannel
    {
        public List<string> ExecCalls { get; } = [];
        public Queue<byte[]> StdoutData { get; } = new();
        public Queue<byte[]> StderrData { get; } = new();
        public bool ReadReturnZeroOnStdout { get; set; }
        public bool SendEofCalled { get; private set; }
        public bool DisposeAsyncCalled { get; private set; }
        public int DisposeAsyncCallCount { get; private set; }

        public Task ExecAsync(string command, CancellationToken cancellationToken)
        {
            ExecCalls.Add(command);
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (ReadReturnZeroOnStdout && StdoutData.Count == 0)
            {
                return Task.FromResult(0);
            }

            if (StdoutData.Count == 0)
            {
                return Task.FromResult(0);
            }

            byte[] chunk = StdoutData.Dequeue();
            int n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            return Task.FromResult(n);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SendEofAsync(CancellationToken cancellationToken)
        {
            SendEofCalled = true;
            return Task.CompletedTask;
        }

        public Task<int> ReadStderrAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (StderrData.Count == 0)
            {
                return Task.FromResult(0);
            }

            byte[] chunk = StderrData.Dequeue();
            int n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            return Task.FromResult(n);
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            DisposeAsyncCallCount++;
            return ValueTask.CompletedTask;
        }
    }
}

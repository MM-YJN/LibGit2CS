using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

/// <summary> The reflog message is byte-primary end-to-end — C's <c>serialize_reflog_entry</c> (refdb_fs.c:2174-2213) writes the
/// raw <c>msg</c> bytes with <c>git_str_puts</c>, folds <c>'\n'</c> → space over the whole line except the last two bytes (the <c>i &lt; buf->size - 2</c>
/// quirk), then rtrims. Non-UTF-8 message bytes round-trip byte-exact. </summary>
public sealed class ReflogMessageByteTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public ReflogMessageByteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ReflogMsgBytes_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommit(string message)
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = "refs/heads/master",
        });
    }

    private async Task<byte[]> ReadReflogAsync(string refName = "refs/heads/master")
    {
        string logPath = Path.Combine(_tempDir, ".git", "logs", refName.Replace('/', Path.DirectorySeparatorChar));
        return await File.ReadAllBytesAsync(logPath, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NonUtf8CommitMessage_ReflogKeepsRawBytes()
    {
        // The reflog subject is extracted from the message BYTES (C's
        // git_reference__update_for_commit summarizes git_commit_summary
        // over the raw bytes, refs.c:1174-1191) — a raw 0xE9 byte must not
        // degrade to the U+FFFD bytes (EF BF BD).
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        byte[] message = [.. "s"u8.ToArray(), 0xE9, .. "ujet\n"u8.ToArray()];
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "s\uFFFDujet",
            MessageBytes = message,
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();

        // "commit (initial): s\xE9 ujet" — the raw E9 byte survives in the line.
        byte[] expectedTail = [.. "commit (initial): s"u8.ToArray(), 0xE9, .. "ujet\n"u8.ToArray()];
        Assert.True(reflog.AsSpan().EndsWith(expectedTail));
    }

    [Fact]
    public async Task NonUtf8MessageBytes_AppendReflog_ByteExact()
    {
        GitOid oid = await WriteCommit("base\n");

        byte[] message = [.. "fetch "u8.ToArray(), 0xE9, .. " from origin"u8.ToArray()];
        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, TestSig(), message, TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        byte[] expectedTail = [.. "\t"u8.ToArray(), .. "fetch "u8.ToArray(), 0xE9, .. " from origin\n"u8.ToArray()];
        Assert.True(reflog.AsSpan().EndsWith(expectedTail));
    }

    [Fact]
    public async Task EmbeddedNewline_FoldedToSpace()
    {
        GitOid oid = await WriteCommit("base\n");

        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, TestSig(), "two\nlines"u8.ToArray(), TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        // The '\n' inside the message folds to ' ' (refdb_fs.c:2201-2203).
        Assert.True(reflog.AsSpan().IndexOf("\ttwo lines\n"u8) >= 0);
    }

    [Fact]
    public async Task TrailingWhitespace_Rtrimmed()
    {
        GitOid oid = await WriteCommit("base\n");

        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, TestSig(), "msg   \t\n\n"u8.ToArray(), TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        Assert.True(reflog.AsSpan().EndsWith("\tmsg\n"u8));
    }

    [Fact]
    public async Task NewlineTwoBytesFromEnd_SurvivesFold()
    {
        // C quirk pin (refdb_fs.c:2201): the fold loop is `i < buf->size - 2`,
        // so a '\n' at index size-2 (message ending "…\nX") is NOT folded and
        // survives as a literal newline in the reflog line.
        GitOid oid = await WriteCommit("base\n");

        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, TestSig(), "a\nb"u8.ToArray(), TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        // '\t' 'a' '\n' 'b' — the embedded newline is 2 bytes from the end at
        // fold time and escapes the fold.
        Assert.True(reflog.AsSpan().EndsWith("\ta\nb\n"u8));
    }

    [Fact]
    public async Task NonUtf8SignatureName_ReflogKeepsRawBytes()
    {
        // The serializer writes GitSignature.NameBytes verbatim
        // (git_signature__writebuf, signature.c:425-441).
        GitOid oid = await WriteCommit("base\n");
        byte[] name = [.. "T"u8.ToArray(), 0xE9];
        var sig = GitSignature.Create(name, "t@x"u8.ToArray(), new GitTime(1700000000, 0));

        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, sig, "msg"u8.ToArray(), TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        byte[] expected = [.. "T"u8.ToArray(), 0xE9, .. " <t@x> 1700000000 +0000\tmsg"u8.ToArray()];
        Assert.True(reflog.AsSpan().IndexOf(expected) >= 0);
    }

    [Fact]
    public async Task NullMessage_NoTabWritten()
    {
        GitOid oid = await WriteCommit("base\n");

        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, TestSig(), messageBytes: null, cancellationToken: TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        int lastNewline = reflog.AsSpan(..^1).LastIndexOf((byte)'\n');
        byte[] lastLine = reflog[(lastNewline + 1)..];
        Assert.False(lastLine.Contains((byte)'\t'));
    }

    [Fact]
    public async Task StringConvenience_EncodesUtf8AndFolds()
    {
        GitOid oid = await WriteCommit("base\n");

        await _repo.Refs.AppendReflogAsync("refs/heads/master", oid, oid, TestSig(), "caf\u00E9 one\ntwo", TestContext.Current.CancellationToken);

        byte[] reflog = await ReadReflogAsync();
        byte[] expectedTail = [.. "\tcaf"u8.ToArray(), 0xC3, 0xA9, .. " one two\n"u8.ToArray()];
        Assert.True(reflog.AsSpan().EndsWith(expectedTail));
    }
}

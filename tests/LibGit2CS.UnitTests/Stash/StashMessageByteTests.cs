using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

namespace LibGit2CS.UnitTests.Stash;

/// <summary> The stash message pipeline is byte-primary — C builds the msg prefix from the raw branch name + HEAD hex +
/// <c>git_commit_summary</c> bytes (stash.c:328-374, 137, 336, 518-546) and the stash reflog carries the rtrimmed raw bytes (stash.c:745). A non-UTF-8
/// base-commit subject keeps its raw bytes in the stash commit message and the refs/stash reflog — no U+FFFD. </summary>
public sealed class StashMessageByteTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StashMessageByteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashMsgBytes_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task NonUtf8BaseSubject_StashMessageAndReflogKeepRawBytes()
    {
        // Base commit with a raw 0xE9 byte in the subject (via MessageBytes).
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        byte[] baseMessage = [.. "s"u8.ToArray(), 0xE9, .. "ujet\n"u8.ToArray()];
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "s\uFFFDujet\n",
            MessageBytes = baseMessage,
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // Dirty the workdir and stash.
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);
        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // The stash commit message keeps the raw subject bytes:
        // "WIP on master: <hex> s\xE9 ujet" (summary folds nothing here —
        // single line).
        Commit stashCommit = await _repo.ObjectLookupAsync<Commit>(stashOid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("stash commit not found");
        byte[] raw = stashCommit.RawMessageBytes.ToArray();
        Assert.True(raw.AsSpan().StartsWith("WIP on master: "u8));
        byte[] expectedSubject = [.. " s"u8.ToArray(), 0xE9, .. "ujet"u8.ToArray()];
        Assert.True(raw.AsSpan().IndexOf(expectedSubject) >= 0);

        // No U+FFFD bytes anywhere (the raw byte survived, not a decode).
        Assert.True(raw.AsSpan().IndexOf("\uFFFD"u8) < 0);

        // The refs/stash reflog carries the rtrimmed raw bytes too.
        string logPath = Path.Combine(_tempDir, ".git", "logs", "refs", "stash");
        byte[] reflog = await File.ReadAllBytesAsync(logPath, TestContext.Current.CancellationToken);
        byte[] expectedTail = [.. " s"u8.ToArray(), 0xE9, .. "ujet\n"u8.ToArray()];
        Assert.True(reflog.AsSpan().EndsWith(expectedTail));
    }
}

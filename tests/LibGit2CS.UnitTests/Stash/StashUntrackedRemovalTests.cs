using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

namespace LibGit2CS.UnitTests.Stash;

/// <summary>
/// Tests for stash untracked removal via the checkout's
/// RemoveUntracked/RemoveIgnored strategy flags.
/// <see cref="GitRepository.StashSaveAsync"/> sets these flags on the checkout
/// instead of using a separate <c>RemoveUntrackedAndIgnored</c> pass.
/// </summary>
public sealed class StashUntrackedRemovalTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StashUntrackedRemovalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashUntracked_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private List<string> WorkdirFiles()
    {
        var result = new List<string>();
        if (_repo.Workdir is null)
        {
            return result;
        }

        foreach (string f in Directory.EnumerateFiles(_repo.Workdir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_repo.Workdir, f);
            if (!rel.StartsWith(".git/", StringComparison.Ordinal) && rel != ".git")
            {
                result.Add(rel);
            }
        }

        return result;
    }

    [Fact]
    public async Task Stash_IncludeUntracked_RemovesUntrackedAfterSave()
    {
        await WriteCommitWithFile("tracked.txt", "content\n");

        // Add an untracked file and a modified tracked file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "tracked.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.IncludeUntracked, cancellationToken: TestContext.Current.CancellationToken);

        // After stash with IncludeUntracked, the untracked file should be removed.
        List<string> files = WorkdirFiles();
        Assert.DoesNotContain("untracked.txt", files);
        // The tracked file should be restored to HEAD content.
        Assert.Contains("tracked.txt", files);
        Assert.Equal("content\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "tracked.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stash_Default_PreservesUntracked()
    {
        await WriteCommitWithFile("tracked.txt", "content\n");

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "tracked.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Without IncludeUntracked, the untracked file should survive.
        List<string> files = WorkdirFiles();
        Assert.Contains("untracked.txt", files);
    }

    [Fact]
    public async Task Stash_NoChanges_ThrowsNotFound()
    {
        await WriteCommitWithFile("file.txt", "content\n");

        // Clean workdir — nothing to stash.
        await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stash_IncludeUntracked_Pop_RestoresUntracked()
    {
        await WriteCommitWithFile("tracked.txt", "content\n");

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "tracked.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.IncludeUntracked, cancellationToken: TestContext.Current.CancellationToken);

        // Pop the stash — untracked file should be restored.
        await _repo.StashPopAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = WorkdirFiles();
        Assert.Contains("untracked.txt", files);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Tests for the <c>UPDATE_INDEX</c> status flag (stat-cache refresh +
/// index write). Matches <c>test_status_worktree__update_stat_cache_0</c>
/// and <c>test_status_worktree__update_index_with_symlink_doesnt_change_mode</c>
/// in <c>tests/libgit2/status/worktree.c</c>.
/// </summary>
public sealed class StatusUpdateIndexTests : IDisposable
{
    private readonly string _tempDir;

    public StatusUpdateIndexTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_StatusUpdateIndex_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task UpdateIndex_RefreshesStatCache_WritesIndex()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Create + commit a file.
        await File.WriteAllTextAsync(Path.Combine(repoPath, "test.txt"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        GitOid treeId = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Touch the file (update mtime to a clearly different time, same content).
        string filePath = Path.Combine(repoPath, "test.txt");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(10));

        string indexPath = Path.Combine(repoPath, ".git", "index");
        DateTime indexTimeBefore = File.GetLastWriteTimeUtc(indexPath);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        var opts = new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs
                  | GitStatusFlags.UpdateIndex,
        };

        using GitStatusList list = await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        // The index should have been rewritten (stat cache refreshed).
        DateTime indexTimeAfter = File.GetLastWriteTimeUtc(indexPath);
        Assert.True(indexTimeAfter > indexTimeBefore,
            "index file should have been rewritten by UPDATE_INDEX");

        // After refresh, the file's stat matches workdir and OID matches →
        // Unmodified. Without IncludeUnmodified, it doesn't appear.
        Assert.Equal(0, list.EntryCount);
    }

    [Fact]
    public async Task UpdateIndex_NoChanges_IndexNotWritten()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "test.txt"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);

        // Age the file's mtime well into the past before staging so the staged
        // stat and the workdir stat agree, and the index file's own mtime
        // (Stamp) is unambiguously newer than the entry. Without this, the
        // freshly-created file shares the index's whole-second mtime and the
        // nanosecond racy-git check (git_index_entry_newer_than_index)
        // non-deterministically flags the entry racy → stat-cache refresh →
        // index rewrite, flaking the "index not written" assertion.
        File.SetLastWriteTimeUtc(Path.Combine(repoPath, "test.txt"), DateTime.UtcNow.AddSeconds(-10));

        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        GitOid treeId = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(repoPath, ".git", "index");
        DateTime indexTimeBefore = File.GetLastWriteTimeUtc(indexPath);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // No modification — file is clean. UPDATE_INDEX should not write
        // the index because IndexUpdated stays false.
        var opts = new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs
                  | GitStatusFlags.UpdateIndex,
        };

        using GitStatusList list = await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        DateTime indexTimeAfter = File.GetLastWriteTimeUtc(indexPath);
        Assert.Equal(indexTimeBefore, indexTimeAfter);
        Assert.Equal(0, list.EntryCount);
    }

    [Fact]
    public async Task UpdateIndex_WithNoRefresh_Throws()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "test.txt"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        GitOid treeId = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        var opts = new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs
                  | GitStatusFlags.NoRefresh
                  | GitStatusFlags.UpdateIndex,
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public async Task UpdateIndex_PreservesSymlinkMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Create a symlink and a regular file, commit them.
        string linkPath = Path.Combine(repoPath, "link.txt");
        string targetPath = Path.Combine(repoPath, "target.txt");
        await File.WriteAllTextAsync(targetPath, "target content\n", cancellationToken: TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(linkPath, "target.txt");

        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("target.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("link.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        GitOid treeId = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Rewrite the regular file to trigger a status modification.
        await File.WriteAllTextAsync(targetPath, "modified content\n", cancellationToken: TestContext.Current.CancellationToken);

        var opts = new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs
                  | GitStatusFlags.UpdateIndex,
        };

        using GitStatusList list = await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        // Verify the symlink entry in the index still has Symlink mode
        // (not changed to Regular by the stat refresh).
        GitIndexEntry? entry = (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).EntryByPath("link.txt");
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.Symlink, entry!.Value.Mode);
    }
}

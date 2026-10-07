using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitStatusEntry = LibGit2CS.Status.GitStatusEntry;
using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;

namespace LibGit2CS.UnitTests.IO;

// the workdir
// iterator's GetFileMode checked FileAttributes.Directory before
// FileAttributes.ReparsePoint, so a symlink-to-directory (which reports
// BOTH bits on .NET) was classified as a Tree and PushFrameAsync recursed
// into it. Upstream stats each entry with lstat
// (filesystem_iterator_frame_push / git_fs_path_diriter_stat, iterator.c)
// and never follows directory symlinks; on Windows, libgit2's lstat maps
// every reparse point to S_IFLNK (w32_util.c:110-114).
//
// Impact: a workdir containing `leak -> /etc` enumerated /etc as repo
// content (info disclosure, wrong status/diff), and a self-referencing
// symlink (`loop -> .`) recursed until MaxDepth=100 threw GitException.
public sealed class FilesystemIteratorSymlinkRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public FilesystemIteratorSymlinkRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_SymlinkRegression_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static bool TryCreateSymlink(string linkPath, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task SymlinkToDirectory_IsNotRecursedInto_AndIsReportedAsSymlink()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // A real directory with a file inside.
        string realDir = Path.Combine(repoPath, "realdir");
        Directory.CreateDirectory(realDir);
        await File.WriteAllTextAsync(
            Path.Combine(realDir, "inner.txt"), "secret\n",
            cancellationToken: TestContext.Current.CancellationToken);

        // A symlink pointing at that directory.
        string linkPath = Path.Combine(repoPath, "link");
        if (!TryCreateSymlink(linkPath, "realdir"))
        {
            return; // symlinks unavailable (Windows without privileges)
        }

        using GitStatusList list = await repo.StatusNewAsync(
            new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // The target directory's contents must NOT leak into the workdir
        // as "link/..." paths (the symlink is a file, not a tree).
        Assert.DoesNotContain(
            list.Entries,
            e => e.Path.ToUtf8String().StartsWith("link/", StringComparison.Ordinal));

        // The symlink itself is reported as one untracked symlink entry
        // (lstat S_IFLNK — never followed), not as a directory.
        GitStatusEntry linkEntry = list.Entries.Single(e => e.Path.ToUtf8String() == "link");
        Assert.Equal(GitStatusFlags.WorkdirNew, linkEntry.Status);
        Assert.Equal(GitFileMode.Symlink, linkEntry.IndexToWorkdir!.NewFile.Mode);
    }

    [Fact]
    public async Task SelfReferencingSymlink_DoesNotRecurseToMaxDepth()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // A self-referencing symlink (`loop ->.`). If the
        // iterator classifies it as a Tree it recurses until
        // MaxDepth=100 throws "directory nesting too deep". RecurseUntrackedDirs
        // forces the diff to descend into untracked directories, so any
        // such recursion is guaranteed to hit the depth guard.
        string loopPath = Path.Combine(repoPath, "loop");
        if (!TryCreateSymlink(loopPath, "."))
        {
            return; // symlinks unavailable (Windows without privileges)
        }

        using GitStatusList list = await repo.StatusNewAsync(
            new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry loopEntry = list.Entries.Single(e => e.Path.ToUtf8String() == "loop");
        Assert.Equal(GitStatusFlags.WorkdirNew, loopEntry.Status);
        Assert.Equal(GitFileMode.Symlink, loopEntry.IndexToWorkdir!.NewFile.Mode);
    }

    [Fact]
    public async Task WorkdirIterator_SelfLoopSymlink_DoesNotRecurseToMaxDepth()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        string loopPath = Path.Combine(repoPath, "loop");
        if (!TryCreateSymlink(loopPath, "."))
        {
            return; // symlinks unavailable (Windows without privileges)
        }

        // Drive the raw workdir iterator (the recursion is a property
        // of the iterator itself — the diff's untracked-dir collapse masks
        // it in the status path when the target contains a .git entry).
        // A Tree classification for the "loop" entry would push a
        // frame per level until MaxDepth=100 throws GitException.
        IIterator iter = await FilesystemIterator.ForWorkdirAsync(
            repo, null, null,
            cancellationToken: TestContext.Current.CancellationToken);

        var paths = new List<string>();
        while (true)
        {
            GitIndexEntry? entry = await iter.AdvanceAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            if (entry is null)
            {
                break;
            }

            paths.Add(entry.Value.Path.ToUtf8String());
        }

        // The symlink is a single file entry; nothing is reported under it.
        Assert.Contains("loop", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("loop/", StringComparison.Ordinal));
    }
}

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Regression tests for the ignored-directory leak: files inside a directory
/// matched by a <c>.gitignore</c> rule — but whose own basenames do not match
/// any rule — must be classified <c>Ignored</c> (or omitted), never
/// <c>Untracked</c>. The canonical case: a subdirectory <c>.gitignore</c>
/// containing <c>node_modules</c> must not flood status with
/// <c>node_modules/**</c> as untracked.
/// </summary>
/// <remarks>
/// The behavior spans two 1:1 ports from libgit2:
/// <list type="bullet">
/// <item>The <c>FilesystemIterator</c> must evaluate the directory's own
/// ignore status on frame push
/// (<c>filesystem_iterator_frame_push_ignores</c>, iterator.c:1146-1178),
/// so descendants inherit "ignored".</item>
/// <item>The workdir iterator must be created with
/// <c>GIT_ITERATOR_DONT_AUTOEXPAND</c> (diff_generate.c:1487), so
/// <c>handle_unmatched_new_item</c>'s directory branch
/// (diff_generate.c:1064-1148) runs for workdir diffs and the directory
/// collapses instead of expanding.</item>
/// </list>
/// </remarks>
public sealed class IgnoredDirectoryTests : StatusGoldenBase, IAsyncDisposable
{
    private static readonly GitSignature s_sig = new("t", "t@t", new GitTime(1700000000, 0));

    private GitRepository? _repo;
    private string _workdir = null!;

    private async Task<GitRepository> OpenEmptyRepoAsync()
    {
        GitRepository repo = await OpenRepoFixtureAsync("empty_standard");
        _repo = repo;
        _workdir = repo.Workdir!;
        return repo;
    }

    public async ValueTask DisposeAsync()
    {
        if (_repo is not null)
        {
            await _repo.DisposeAsync();
        }
    }

    private void WriteGitignore(string relativePath, string content)
    {
        string fullPath = Path.Combine(_workdir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void CreateFile(string relativePath, string content = "")
    {
        string fullPath = Path.Combine(_workdir, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
    }

    private async Task<GitOid> CommitFileAsync(GitRepository repo, string path, string content, string refName, CancellationToken ct)
    {
        CreateFile(path, content);
        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync(path, ct);
        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = s_sig,
            Committer = s_sig,
            Message = $"add {path}\n",
            UpdateRef = refName,
        }, ct);
    }

    /// <summary>
    /// The canonical leak shape: <c>sub/.gitignore</c> ignores
    /// <c>node_modules</c>; <c>sub/node_modules/pkg/index.js</c> does not
    /// itself match any rule and must not leak as untracked.
    /// </summary>
    [Fact]
    public async Task IgnoredDirectory_Contents_NotReportedAsUntracked()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        WriteGitignore("sub/.gitignore", "node_modules\n");
        CreateFile("sub/node_modules/pkg/index.js", "content");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The ignored directory collapses to a single IGNORED entry; nothing
        // under node_modules/** leaks as untracked.
        Assert.Equal(2, list.EntryCount);
        Assert.Contains(list.Entries, e => e.Path.ToUtf8String() == "sub/.gitignore" && e.Status == GitStatusFlags.WorkdirNew);
        Assert.Contains(list.Entries, e => e.Path.ToUtf8String() == "sub/node_modules/" && e.Status == GitStatusFlags.Ignored);
        Assert.DoesNotContain(list.Entries, e =>
            e.Path.ToUtf8String().StartsWith("sub/node_modules/", StringComparison.Ordinal) &&
            e.Path.ToUtf8String() != "sub/node_modules/");
    }

    /// <summary>
    /// Without <see cref="GitStatusFlags.IncludeIgnored"/> the ignored
    /// directory is omitted entirely (its contents are not visited at all).
    /// </summary>
    [Fact]
    public async Task IgnoredDirectory_WithoutIncludeIgnored_NotReported()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        WriteGitignore("sub/.gitignore", "node_modules\n");
        CreateFile("sub/node_modules/pkg/index.js", "content");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(list.Entries, e =>
            e.Path.ToUtf8String().StartsWith("sub/node_modules/", StringComparison.Ordinal));
        Assert.Contains(list.Entries, e => e.Path.ToUtf8String() == "sub/.gitignore");
    }

    /// <summary>
    /// The diff-path counterpart: <c>IndexToWorkdirAsync</c> must not expand
    /// the ignored directory into per-file deltas.
    /// </summary>
    [Fact]
    public async Task IndexToWorkdir_IgnoredDirectory_CollapsedToSingleDelta()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        WriteGitignore("sub/.gitignore", "node_modules\n");
        CreateFile("sub/node_modules/pkg/index.js", "content");

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUntracked
                  | GitDiffOptionsFlags.IncludeIgnored
                  | GitDiffOptionsFlags.RecurseUntrackedDirs,
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(diff.Deltas, d => d.NewFile.Path?.ToUtf8String() == "sub/node_modules/pkg/index.js");

        GitDiffDelta? dirDelta = diff.Deltas.SingleOrDefault(d => d.NewFile.Path?.ToUtf8String() == "sub/node_modules/");
        Assert.NotNull(dirDelta);
        Assert.Equal(GitDeltaStatus.Ignored, dirDelta!.Status);
    }

    /// <summary>
    /// A tracked file edited inside a directory that a new <c>.gitignore</c>
    /// rule now ignores still reports the edit (exercises
    /// <c>contains_oitem</c>); an ignored sibling inside the same directory
    /// is skipped (exercises <c>current_tree_is_ignored</c>).
    /// </summary>
    [Fact]
    public async Task TrackedFileInIgnoredDirectory_EditStillReported()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        await CommitFileAsync(repo, "sub/node_modules/pkg/index.js", "v1\n", "refs/heads/main", TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        WriteGitignore("sub/.gitignore", "node_modules\n");
        CreateFile("sub/node_modules/pkg/index.js", "v2\n");
        CreateFile("sub/node_modules/pkg/extra.txt", "extra\n");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.RecurseUntrackedDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry modified = list.Entries.Single(e => e.Path.ToUtf8String() == "sub/node_modules/pkg/index.js");
        Assert.Equal(GitStatusFlags.WorkdirModified, modified.Status);

        Assert.DoesNotContain(list.Entries, e => e.Path.ToUtf8String() == "sub/node_modules/pkg/extra.txt");
        Assert.DoesNotContain(list.Entries, e => e.Path.ToUtf8String() == "sub/node_modules/");
    }

    /// <summary>
    /// With <see cref="GitStatusFlags.RecurseIgnoredDirs"/> the ignored
    /// directory IS descended and each file inside is reported as
    /// <c>Ignored</c> (never <c>Untracked</c>).
    /// </summary>
    [Fact]
    public async Task RecurseIgnoredDirs_ExpandsIgnoredDirectory()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        WriteGitignore("sub/.gitignore", "node_modules\n");
        CreateFile("sub/node_modules/pkg/index.js", "content");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs
                  | GitStatusFlags.RecurseIgnoredDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry indexJs = list.Entries.Single(e => e.Path.ToUtf8String() == "sub/node_modules/pkg/index.js");
        Assert.Equal(GitStatusFlags.Ignored, indexJs.Status);
        Assert.DoesNotContain(list.Entries, e => e.Path.ToUtf8String() == "sub/node_modules/");
    }

    /// <summary>
    /// An untracked directory without
    /// <see cref="GitStatusFlags.RecurseUntrackedDirs"/> collapses to a
    /// single untracked entry for the directory (git <c>-unormal</c>
    /// semantics), not per-file entries — exercises the
    /// <c>advance_over</c> placeholder path in
    /// <c>handle_unmatched_new_item</c>.
    /// </summary>
    [Fact]
    public async Task UntrackedDirectory_WithoutRecurseUntrackedDirs_CollapsesToSingleEntry()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        CreateFile("newdir/file1.txt", "a");
        CreateFile("newdir/file2.txt", "b");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry dirEntry = Assert.Single(list.Entries);
        Assert.Equal("newdir/", dirEntry.Path.ToUtf8String());
        Assert.Equal(GitStatusFlags.WorkdirNew, dirEntry.Status);
    }

    /// <summary>
    /// An untracked directory containing only ignored files is reported as
    /// a single <c>Ignored</c> entry for the directory — the
    /// <c>advance_over</c> scan upgrades the untracked placeholder to
    /// ignored (diff_generate.c:1115-1125).
    /// </summary>
    [Fact]
    public async Task UntrackedDirectory_WithOnlyIgnoredContent_ReportedAsIgnored()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        WriteGitignore(".gitignore", "*.log\n");
        CreateFile("logs/a.log", "log");
        CreateFile("logs/b.log", "log");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry dirEntry = list.Entries.Single(e => e.Path.ToUtf8String() == "logs/");
        Assert.Equal(GitStatusFlags.Ignored, dirEntry.Status);
        Assert.DoesNotContain(list.Entries, e => e.Path.ToUtf8String() == "logs/a.log");
        Assert.DoesNotContain(list.Entries, e => e.Path.ToUtf8String() == "logs/b.log");
    }
}

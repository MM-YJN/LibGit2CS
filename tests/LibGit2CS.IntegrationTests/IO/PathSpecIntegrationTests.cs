using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDeltaStatus = LibGit2CS.Diff.GitDeltaStatus;
using GitDiffDelta = LibGit2CS.Diff.GitDiffDelta;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.IO;

/// <summary>
/// Integration tests for the pathspec match engine
/// (<see cref="GitPathSpec"/>/<see cref="GitPathSpecMatchList"/>)
/// exercised end-to-end via <see cref="GitIndex.AddAllAsync"/>/
/// <see cref="GitIndex.RemoveAll"/> and <see cref="GitDiff"/> pathspec
/// filtering against locally-initialized non-bare repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project covers
/// <see cref="GitPathSpec.New"/>, <see cref="GitPathSpec.MatchesPath"/>,
/// and the <see cref="WildMatch"/> primitives in isolation against literal
/// inputs. <see cref="GitPathSpecMatchList"/> had <c>0%</c> integration
/// coverage — no end-to-end path through <see cref="GitIndex.AddAllAsync"/>
/// (which calls <see cref="GitPathSpec.MatchesPath"/> per workdir delta) or
/// <see cref="GitDiff"/> (which compiles <see cref="GitDiffOptions.PathSpecs"/>
/// into a <see cref="GitPathSpec"/> and filters deltas) exercises the
/// match-list construction or the failure-tracking
/// (<see cref="GitPathSpec.MatchFlags.FindFailures"/>) path. These tests
/// build a real workdir with mixed file types and drive both surfaces.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/index/pathspec.c</c>
/// (<c>test_index_pathspec__add_all</c>,
/// <c>test_index_pathspec__remove_all</c>) and
/// <c>tests/libgit2/diff/pathspec.c</c>, adapted to build the sandbox from
/// scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class PathSpecIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-pathspec-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes a workdir file (creating parent dirs) without staging it.
    /// </summary>
    private static async Task WriteWorkdirFileAsync(string workdir, string relPath, string content, CancellationToken ct)
    {
        string full = Path.Combine(workdir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an initial commit on <paramref name="refName"/> whose tree
    /// contains the given (path → content) files. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir, IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        index.Clear();
        foreach ((string p, string c) in files)
        {
            await WriteWorkdirFileAsync(workdir, p, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the set of staged (stage-0) paths in the index, sorted.
    /// </summary>
    private static List<string> StagedPaths(GitIndex index)
    {
        var paths = new List<string>();
        foreach (GitIndexEntry e in index.Entries)
        {
            if (e.Stage == 0)
            {
                paths.Add(e.Path.ToUtf8String());
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    // ── GitIndex.AddAllAsync with pathspec ──────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.AddAllAsync"/> with a glob pathspec
    /// (<c>*.txt</c>) stages only the matching workdir files, leaving
    /// non-matching files unstaged. Exercises the
    /// <see cref="GitPathSpec.MatchesPath"/> per-delta filter in the
    /// add-all path.
    /// </summary>
    [Fact]
    public async Task AddAll_WithGlobPattern_StagesOnlyMatchingFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Initial commit with a.txt already tracked.
            await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["a.txt"] = "a\n" },
                "init\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Create untracked workdir files of mixed extensions.
            await WriteWorkdirFileAsync(path, "b.txt", "b\n", ct).ConfigureAwait(false);
            await WriteWorkdirFileAsync(path, "c.cs", "c\n", ct).ConfigureAwait(false);
            await WriteWorkdirFileAsync(path, "src/d.txt", "d\n", ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            var ps = GitPathSpec.New("*.txt");
            await index.AddAllAsync(ps, GitIndexAddOptions.Default, callback: null, ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);

            List<string> staged = StagedPaths(index);
            Assert.Contains("a.txt", staged);
            Assert.Contains("b.txt", staged);
            Assert.DoesNotContain("c.cs", staged);
            // RecurseUntrackedDirs is on by default in AddAll, so src/d.txt
            // matches the *.txt glob and is staged.
            Assert.Contains("src/d.txt", staged);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitIndex.AddAllAsync"/> with a directory-prefix
    /// pathspec (<c>src/</c>) stages only files under that directory,
    /// exercising the dirname-prefix-match branch of
    /// <see cref="GitPathSpec.MatchOne"/> (pattern with no wildcards →
    /// <c>path.StartsWith(pattern)/</c>).
    /// </summary>
    [Fact]
    public async Task AddAll_WithDirectoryPrefix_StagesOnlyFilesUnderDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await WriteWorkdirFileAsync(path, "top.txt", "top\n", ct).ConfigureAwait(false);
            await WriteWorkdirFileAsync(path, "src/a.cs", "a\n", ct).ConfigureAwait(false);
            await WriteWorkdirFileAsync(path, "src/sub/b.cs", "b\n", ct).ConfigureAwait(false);
            await WriteWorkdirFileAsync(path, "other/c.txt", "c\n", ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            var ps = GitPathSpec.New("src/");
            await index.AddAllAsync(ps, GitIndexAddOptions.Default, callback: null, ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);

            List<string> staged = StagedPaths(index);
            Assert.Contains("src/a.cs", staged);
            Assert.Contains("src/sub/b.cs", staged);
            Assert.DoesNotContain("top.txt", staged);
            Assert.DoesNotContain("other/c.txt", staged);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── GitIndex.RemoveAll with pathspec ────────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.RemoveAll"/> with a glob pathspec removes
    /// only the matching staged entries, leaving siblings intact. Exercises
    /// the <see cref="GitPathSpec.MatchesPath"/> per-entry filter in the
    /// remove-all path (which walks the index, not the workdir).
    /// </summary>
    [Fact]
    public async Task RemoveAll_WithGlobPattern_RemovesOnlyMatchingEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Stage four files across two extensions.
            await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    ["a.txt"] = "a\n",
                    ["b.txt"] = "b\n",
                    ["c.cs"] = "c\n",
                    ["d.cs"] = "d\n",
                },
                "init\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            var ps = GitPathSpec.New("*.cs");
            IReadOnlyList<string> removed = index.RemoveAll(ps, callback: null);
            await index.WriteAsync(ct).ConfigureAwait(false);

            Assert.Equal(["c.cs", "d.cs"], removed.OrderBy(r => r, StringComparer.Ordinal).ToArray());
            List<string> staged = StagedPaths(index);
            Assert.Contains("a.txt", staged);
            Assert.Contains("b.txt", staged);
            Assert.DoesNotContain("c.cs", staged);
            Assert.DoesNotContain("d.cs", staged);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── GitDiff pathspec filtering ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiff.TreeToTreeAsync"/> with
    /// <see cref="GitDiffOptions.PathSpecs"/> set to <c>*.txt</c> emits
    /// deltas only for <c>.txt</c> files, filtering out <c>.cs</c> changes.
    /// Exercises <see cref="DiffGenerator"/>'s
    /// <c>opts.PathSpecs → GitPathSpec.New</c> compile + per-delta
    /// <see cref="GitPathSpec.MatchAt"/> filter.
    /// </summary>
    [Fact]
    public async Task Diff_WithPathSpec_FiltersToFileSubset()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base tree: a.txt + b.cs.
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    ["a.txt"] = "a\n",
                    ["b.cs"] = "b\n",
                },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // New tree: modify both files.
            GitOid newCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    ["a.txt"] = "a-changed\n",
                    ["b.cs"] = "b-changed\n",
                },
                "new\n", "refs/heads/main", baseCommit, ct).ConfigureAwait(false);

            Commit baseC = (await repo.ObjectLookupAsync<Commit>(baseCommit, ct).ConfigureAwait(false))!;
            Commit newC = (await repo.ObjectLookupAsync<Commit>(newCommit, ct).ConfigureAwait(false))!;
            GitTree baseTree = (await repo.ObjectLookupAsync<GitTree>(baseC.Tree, ct).ConfigureAwait(false))!;
            GitTree newTree = (await repo.ObjectLookupAsync<GitTree>(newC.Tree, ct).ConfigureAwait(false))!;

            var opts = new GitDiffOptions { PathSpecStrings = ["*.txt"] };
            using GitDiff diff = await repo.DiffTreeToTreeAsync(baseTree, newTree, opts, ct).ConfigureAwait(false);

            List<string> deltaPaths = new(diff.DeltaCount);
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                GitDiffDelta d = diff.GetDelta(i);
                deltaPaths.Add(d.Path.ToUtf8String());
            }

            Assert.Contains("a.txt", deltaPaths);
            Assert.DoesNotContain("b.cs", deltaPaths);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── GitPathSpec.Match → GitPathSpecMatchList failures ───────────────

    /// <summary>
    /// <see cref="GitPathSpec.Match"/> with
    /// <see cref="GitPathSpec.MatchFlags.FindFailures"/> reports unmatched
    /// patterns in <see cref="GitPathSpecMatchList.FailedEntries"/>, and
    /// <see cref="GitPathSpecMatchList.FailedEntryCount"/> reflects the
    /// count. Exercises the failure-tracking branch
    /// (<c>findFailures → used[] → failures.Add</c>) which is the only path
    /// that populates <see cref="GitPathSpecMatchList"/>'s failure list.
    /// </summary>
    [Fact]
    public async Task Match_FindFailures_ReportsUnmatchedPatterns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Build an index with two .txt files.
            await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    ["a.txt"] = "a\n",
                    ["b.txt"] = "b\n",
                },
                "init\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);

            // Pathspec with three patterns: one matches, two don't.
            var ps = GitPathSpec.New("*.txt", "missing.cs", "gone.md");
            GitPathSpecMatchList result = ps.Match(index.Entries, GitPathSpec.MatchFlags.FindFailures);

            Assert.Equal(2, result.EntryCount);
            Assert.Contains("a.txt", result.Entries);
            Assert.Contains("b.txt", result.Entries);

            // The two non-matching patterns must show up as failures.
            Assert.Equal(2, result.FailedEntryCount);
            List<string> failures = [.. result.FailedEntries];
            Assert.Contains("missing.cs", failures);
            Assert.Contains("gone.md", failures);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitPathSpec.MatchPaths"/> with
    /// <see cref="GitPathSpec.MatchFlags.FailuresOnly"/> skips building the
    /// matches list (exercises the <c>failuresOnly → !matches.Add</c>
    /// branch) and still reports failures when combined with
    /// <see cref="GitPathSpec.MatchFlags.FindFailures"/>.
    /// </summary>
    [Fact]
    public async Task MatchPaths_FailuresOnly_SkipsMatchList_BuildsFailures()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await WriteWorkdirFileAsync(path, "keep.txt", "k\n", ct).ConfigureAwait(false);

            var ps = GitPathSpec.New("keep.txt", "absent.txt");
            GitPathSpecMatchList result = ps.MatchPaths(
                new[] { "keep.txt", "other.bin" },
                GitPathSpec.MatchFlags.FindFailures | GitPathSpec.MatchFlags.FailuresOnly);

            // FailuresOnly → matches list is empty even though keep.txt matched.
            Assert.Equal(0, result.EntryCount);
            Assert.Empty(result.Entries);

            // absent.txt did not match any input → failure.
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal("absent.txt", result.GetFailedEntry(0));
        }
        finally
        {
            Cleanup(path);
        }
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.IntegrationTests.Merge;

/// <summary>
/// Integration tests for the merge-driver dispatch path
/// (<see cref="GitMergeDriverRegistry"/> →
/// <see cref="BuiltinUnionDriver"/>/<see cref="BuiltinBinaryDriver"/>)
/// exercised end-to-end via <see cref="GitRepository.MergeTreesAsync"/>
/// against locally-initialized repos with <c>.gitattributes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>MergeDriverTests</c> exercise
/// the driver registry and the <c>NameForPathAsync</c> attribute lookup
/// against a fixture-extracted <c>merge-resolve.zip</c> repo with
/// pre-existing divergent branches, and call
/// <see cref="BuiltinBinaryDriver.ApplyAsync"/> directly on a constructed
/// source. <see cref="BuiltinUnionDriver.ApplyAsync"/> is not exercised
/// by any test. These integration tests build the repo from scratch
/// (no fixture zip), write <c>.gitattributes</c> with <c>merge=union</c>
/// (set the <c>union</c> driver for a path) or <c>-merge</c> (unset →
/// <c>binary</c> driver), build two divergent trees that conflict on a
/// file, and run <see cref="GitRepository.MergeTreesAsync"/> — driving the
/// full <see cref="GitMergeDriverRegistry.ForSourceAsync"/> → attribute
/// lookup → driver dispatch → <see cref="BuiltinUnionDriver.ApplyAsync"/>
/// / <see cref="BuiltinBinaryDriver.ApplyAsync"/> path. This is the only
/// test that exercises the union driver end-to-end.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/merge/driver.c</c> (<c>test_merge_driver__union</c>,
/// <c>test_merge_driver__binary</c>), adapted to build the sandbox from
/// scratch and use real <c>.gitattributes</c>-driven dispatch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class MergeDriverIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-mergedrv-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates a commit on <paramref name="refName"/> whose tree contains the
    /// given (path → content) files (the index is cleared first, so the tree
    /// contains exactly these files). Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir, IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        GitIndex index = await repo.GetIndexAsync(ct);
        index.Clear();
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(workdir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, ct);
            await index.AddByPathAsync(path, ct);
        }

        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct);
    }

    /// <summary>Resolves a commit OID to its tree.</summary>
    private static async Task<GitTree> TreeOfAsync(GitRepository repo, GitOid commitOid, CancellationToken ct)
    {
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
        return (await repo.ObjectLookupAsync<GitTree>(commit.Tree, ct))!;
    }

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
    /// Reads the stage-0 (merged) entry for a path from the index, or null if
    /// the path is absent or only present at conflict stages.
    /// </summary>
    private static GitIndexEntry? MergedEntry(GitIndex index, string path)
        => index.EntryByPath(path, stage: 0);

    /// <summary>
    /// Returns all index entries for <paramref name="path"/> across all stages.
    /// </summary>
    private static List<GitIndexEntry> EntriesForPath(GitIndex index, string path)
    {
        var result = new List<GitIndexEntry>();
        foreach (GitIndexEntry e in index.Entries)
        {
            if (e.Path.ToUtf8String() == path)
            {
                result.Add(e);
            }
        }

        return result;
    }

    // ── BuiltinUnionDriver (merge=union) ─────────────────────────────────

    /// <summary>
    /// A conflicting file with <c>merge=union</c> in
    /// <c>.gitattributes</c> is merged by the
    /// <see cref="BuiltinUnionDriver"/>, which concatenates our and their
    /// versions (no conflict markers) and produces a stage-0 entry. The
    /// merge result has <see cref="GitIndex.HasConflicts"/> == false for
    /// that path. Mirrors <c>test_merge_driver__union</c> (driver.c).
    /// </summary>
    [Fact]
    public async Task MergeTrees_UnionAttribute_ConcatenatesSides_NoConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Write .gitattributes FIRST, before any index operation. The
            // attribute cache is built lazily on the first attribute lookup
            // (triggered by GitFilterList.LoadAsync during AddByPathAsync),
            // so writing .gitattributes after the commits would leave the
            // cache stale and the union attribute would never be seen.
            string gitattributesPath = Path.Combine(path, ".gitattributes");
            await File.WriteAllTextAsync(
                gitattributesPath,
                "conflict.txt merge=union\n",
                ct);
            Assert.True(File.Exists(gitattributesPath), $".gitattributes not written at {gitattributesPath}");

            // Base: conflict.txt = three lines.
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["conflict.txt"] = "line1\nline2\nline3\n" },
                "base\n", "refs/heads/main", parent: null, ct);

            // Our branch: conflict.txt — middle line changed to "OURS".
            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["conflict.txt"] = "line1\nOURS\nline3\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct);

            // Their branch: conflict.txt — middle line changed to "THEIRS".
            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["conflict.txt"] = "line1\nTHEIRS\nline3\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct);

            // Diagnostic: verify the attribute cache reads the .gitattributes.
            string driverName = await repo.Context.MergeDrivers.NameForPathAsync(repo, "conflict.txt", null, ct);
            Assert.Equal("union", driverName);

            GitTree baseTree = await TreeOfAsync(repo, baseCommit, ct);
            GitTree ourTree = await TreeOfAsync(repo, ourCommit, ct);
            GitTree theirTree = await TreeOfAsync(repo, theirCommit, ct);

            using GitIndex result = await repo.MergeTreesAsync(
                baseTree, ourTree, theirTree, cancellationToken: ct);

            // The union driver resolved the conflict: no conflict stages for
            // conflict.txt, a single stage-0 entry with the union content.
            List<GitIndexEntry> entries = EntriesForPath(result, "conflict.txt");
            Assert.Single(entries);
            Assert.Equal(0, entries[0].Stage);

            // Read the merged blob.
            GitBlob merged = (await repo.ObjectLookupAsync<GitBlob>(entries[0].Id, ct))!;
            string content = Encoding.UTF8.GetString(merged.Content.Span);

            // Union merge concatenates both sides. The exact ordering and
            // whitespace follow xdiff's union favor; both lines are present.
            Assert.Contains("OURS", content);
            Assert.Contains("THEIRS", content);

            // No conflict markers in the union result.
            Assert.DoesNotContain("<<<<<<<", content);
            Assert.DoesNotContain(">>>>>>>", content);

            // The overall index has no conflicts.
            Assert.False(result.HasConflicts);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With <c>merge=union</c>, a file that only one side modified
    /// (no actual conflict) merges cleanly — the union driver sees no
    /// conflicting hunks and produces the modified content unchanged.
    /// </summary>
    [Fact]
    public async Task MergeTrees_UnionAttribute_OneSidedChange_MergesCleanly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Write .gitattributes FIRST (before any index operation) to
            // avoid the attribute-cache staleness described above.
            await File.WriteAllTextAsync(
                Path.Combine(path, ".gitattributes"),
                "file.txt merge=union\n",
                ct);

            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "line1\nline2\nline3\n" },
                "base\n", "refs/heads/main", parent: null, ct);

            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "line1\nMODIFIED\nline3\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct);

            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "line1\nline2\nline3\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct);

            GitTree baseTree = await TreeOfAsync(repo, baseCommit, ct);
            GitTree ourTree = await TreeOfAsync(repo, ourCommit, ct);
            GitTree theirTree = await TreeOfAsync(repo, theirCommit, ct);

            using GitIndex result = await repo.MergeTreesAsync(
                baseTree, ourTree, theirTree, cancellationToken: ct);

            Assert.False(result.HasConflicts);
            GitIndexEntry? entry = MergedEntry(result, "file.txt");
            Assert.NotNull(entry);
            GitBlob merged = (await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct))!;
            Assert.Equal("line1\nMODIFIED\nline3\n", Encoding.UTF8.GetString(merged.Content.Span));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── BuiltinBinaryDriver (-merge) ────────────────────────────────────

    /// <summary>
    /// A conflicting file with <c>-merge</c> (unset →
    /// <see cref="BuiltinBinaryDriver"/>) in <c>.gitattributes</c> always
    /// produces a conflict — binary files cannot be merged. The result
    /// index has the path at stages 1/2/3 (ancestor/ours/theirs) and no
    /// stage-0 entry. Mirrors <c>test_merge_driver__binary</c> (driver.c).
    /// </summary>
    [Fact]
    public async Task MergeTrees_BinaryAttribute_UnmergedFile_Conflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Write .gitattributes FIRST (before any index operation) to
            // avoid the attribute-cache staleness described above.
            // -merge (unset) → "binary" driver per NameForPathAsync.
            await File.WriteAllTextAsync(
                Path.Combine(path, ".gitattributes"),
                "data.bin -merge\n",
                ct);

            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.bin"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct);

            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.bin"] = "ours\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct);

            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.bin"] = "theirs\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct);

            GitTree baseTree = await TreeOfAsync(repo, baseCommit, ct);
            GitTree ourTree = await TreeOfAsync(repo, ourCommit, ct);
            GitTree theirTree = await TreeOfAsync(repo, theirCommit, ct);

            using GitIndex result = await repo.MergeTreesAsync(
                baseTree, ourTree, theirTree, cancellationToken: ct);

            // The binary driver reported a conflict — the index has
            // conflict stages for data.bin.
            Assert.True(result.HasConflicts);
            GitIndexEntry? merged = MergedEntry(result, "data.bin");
            Assert.Null(merged); // no stage-0 entry

            // Stages 1 (ancestor), 2 (ours), 3 (theirs) present.
            List<GitIndexEntry> stages = EntriesForPath(result, "data.bin");
            var stageNumbers = stages.Select(e => e.Stage).ToHashSet();
            Assert.Contains(1, stageNumbers);
            Assert.Contains(2, stageNumbers);
            Assert.Contains(3, stageNumbers);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Default driver via merge.default config ─────────────────────────

    /// <summary>
    /// <see cref="GitMergeOptions.DefaultDriver"/> (set from the
    /// <c>merge.default</c> config inside <see cref="GitRepository.MergeTreesAsync"/>)
    /// selects the union driver for files with no explicit
    /// <c>merge=</c> attribute (unspecified → default). A conflicting file
    /// with no .gitattributes entry is merged via union when
    /// <c>merge.default = union</c>. Mirrors the
    /// <c>test_merge_driver__default</c> path in driver.c.
    /// </summary>
    [Fact]
    public async Task MergeTrees_DefaultDriverConfig_UnionAppliesToUnattributedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct);

            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "ours\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct);

            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct);

            // No .gitattributes — set merge.default = union in config.
            await repo.Config.SetStringAsync("merge.default", "union", ct);

            GitTree baseTree = await TreeOfAsync(repo, baseCommit, ct);
            GitTree ourTree = await TreeOfAsync(repo, ourCommit, ct);
            GitTree theirTree = await TreeOfAsync(repo, theirCommit, ct);

            using GitIndex result = await repo.MergeTreesAsync(
                baseTree, ourTree, theirTree, cancellationToken: ct);

            // merge.default=union applied to the unattributed file →
            // union merge, no conflict markers.
            Assert.False(result.HasConflicts);
            GitIndexEntry? entry = MergedEntry(result, "file.txt");
            Assert.NotNull(entry);
            GitBlob merged = (await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct))!;
            string content = Encoding.UTF8.GetString(merged.Content.Span);
            Assert.Contains("ours", content);
            Assert.Contains("theirs", content);
            Assert.DoesNotContain("<<<<<<<", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Default text driver (no attribute, no config) ──────────────────

    /// <summary>
    /// A conflicting file with no <c>merge=</c> attribute and no
    /// <c>merge.default</c> config falls back to the built-in
    /// <see cref="BuiltinTextDriver"/>, producing conflict markers and a
    /// conflicted index. This is the baseline against which the union and
    /// binary driver tests above contrast. Mirrors the default-merge
    /// behavior in <c>test_merge_trees__conflict</c>.
    /// </summary>
    [Fact]
    public async Task MergeTrees_NoAttribute_NoDefault_TextDriverProducesConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct);

            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "ours\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct);

            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["file.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct);

            // No .gitattributes, no merge.default config.
            GitTree baseTree = await TreeOfAsync(repo, baseCommit, ct);
            GitTree ourTree = await TreeOfAsync(repo, ourCommit, ct);
            GitTree theirTree = await TreeOfAsync(repo, theirCommit, ct);

            using GitIndex result = await repo.MergeTreesAsync(
                baseTree, ourTree, theirTree, cancellationToken: ct);

            // Text driver → conflict markers + conflicted index.
            Assert.True(result.HasConflicts);
            GitIndexEntry? merged = MergedEntry(result, "file.txt");
            Assert.Null(merged);

            // Stage 2 (ours) and stage 3 (theirs) are present.
            List<GitIndexEntry> stages = EntriesForPath(result, "file.txt");
            var stageNumbers = stages.Select(e => e.Stage).ToHashSet();
            Assert.Contains(2, stageNumbers);
            Assert.Contains(3, stageNumbers);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

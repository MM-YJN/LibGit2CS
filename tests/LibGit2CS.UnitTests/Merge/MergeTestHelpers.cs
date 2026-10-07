using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Shared helpers for merge tests. Mirrors the C
/// <c>tests/libgit2/merge/merge_helpers.c</c> utilities.
/// </summary>
internal static class MergeTestHelpers
{
    /// <summary>
    /// Merges two branches via <see cref="GitRepository.MergeTreesAsync(GitTree, GitTree, GitTree, GitMergeOptions, CancellationToken)"/> (tree-level merge
    /// with explicit merge base). Mirrors <c>merge_trees_from_branches</c>
    /// (merge_helpers.c:12-55).
    /// </summary>
    public static async Task<GitIndex> MergeTreesFromBranchesAsync(
        GitRepository repo,
        string oursName,
        string theirsName,
        GitMergeOptions? opts = null)
    {
        GitOid ourOid = await ResolveBranchAsync(repo, oursName);
        GitOid theirOid = await ResolveBranchAsync(repo, theirsName);

        Commit? ourCommit = await repo.ObjectLookupAsync<Commit>(ourOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourCommit);
        Commit? theirCommit = await repo.ObjectLookupAsync<Commit>(theirOid, TestContext.Current.CancellationToken);
        Assert.NotNull(theirCommit);

        // Find the merge base.
        GitOid? baseOid = await repo.MergeBaseFindAsync(ourOid, theirOid, TestContext.Current.CancellationToken);
        GitTree? ancestorTree = null;
        if (baseOid is { } baseId)
        {
            Commit? baseCommit = await repo.ObjectLookupAsync<Commit>(baseId, TestContext.Current.CancellationToken);
            Assert.NotNull(baseCommit);
            ancestorTree = await repo.ObjectLookupAsync<GitTree>(baseCommit.Tree, TestContext.Current.CancellationToken);
        }

        GitTree? ourTree = await repo.ObjectLookupAsync<GitTree>(ourCommit.Tree, TestContext.Current.CancellationToken);
        GitTree? theirTree = await repo.ObjectLookupAsync<GitTree>(theirCommit.Tree, TestContext.Current.CancellationToken);

        return await repo.MergeTreesAsync(ancestorTree, ourTree!, theirTree!, opts);
    }

    /// <summary>
    /// Merges two branches via <see cref="GitRepository.MergeCommitsAsync"/> (commit-level
    /// merge with recursive base computation). Mirrors
    /// <c>merge_commits_from_branches</c> (merge_helpers.c:57-83).
    /// </summary>
    public static async Task<GitIndex> MergeCommitsFromBranchesAsync(
        GitRepository repo,
        string oursName,
        string theirsName,
        GitMergeOptions? opts = null)
    {
        GitOid ourOid = await ResolveBranchAsync(repo, oursName);
        GitOid theirOid = await ResolveBranchAsync(repo, theirsName);

        Commit? ourCommit = await repo.ObjectLookupAsync<Commit>(ourOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourCommit);
        Commit? theirCommit = await repo.ObjectLookupAsync<Commit>(theirOid, TestContext.Current.CancellationToken);
        Assert.NotNull(theirCommit);

        return await repo.MergeCommitsAsync(ourCommit, theirCommit, opts);
    }

    /// <summary>
    /// Performs a full <c>git merge</c> of theirs into ours by OID (not ref).
    /// Sets HEAD to ours, checks out, then creates an annotated commit by OID
    /// lookup (ref_name=null, matching C <c>git_annotated_commit_lookup</c>).
    /// Mirrors <c>merge_simple_branch</c> (workdir/simple.c).
    /// </summary>
    public static async Task MergeBranchByOidAsync(
        GitRepository repo,
        string oursBranch,
        GitOid theirOid,
        GitMergeOptions? mergeOpts = null,
        GitCheckoutOptions? checkoutOpts = null)
    {
        // Set HEAD to ours branch.
        await repo.ReferenceCreateSymbolicAsync(GitReferences.HeadFile, oursBranch, force: true);

        // Checkout HEAD to populate workdir.
        await repo.CheckoutHeadAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        });

        // Create annotated commit by OID lookup (ref_name = null).
        using GitAnnotatedCommit theirsHead = await repo.AnnotatedCommitLookupAsync(theirOid, TestContext.Current.CancellationToken);
        await repo.MergeAsync([theirsHead], mergeOpts, checkoutOpts);
    }

    /// <summary>
    /// Performs a full <c>git merge</c> of theirs into ours. Sets HEAD to
    /// ours, checks out, then calls <see cref="GitRepository.MergeAsync"/>. Mirrors
    /// <c>merge_branches</c> (merge_helpers.c:85-108).
    /// </summary>
    public static async Task MergeBranchesAsync(
        GitRepository repo,
        string oursBranch,
        string theirsBranch,
        GitMergeOptions? mergeOpts = null,
        GitCheckoutOptions? checkoutOpts = null)
    {
        // Set HEAD to ours branch (symbolic ref).
        await repo.ReferenceCreateSymbolicAsync(GitReferences.HeadFile, oursBranch, force: true);

        // Checkout HEAD to populate workdir.
        await repo.CheckoutHeadAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        });

        // Create annotated commit from theirs ref.
        GitReference theirsRef = await repo.ReferenceLookupAsync(theirsBranch)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"branch '{theirsBranch}' not found",
                GitErrorCategory.Reference);

        using GitAnnotatedCommit theirsHead = await repo.AnnotatedCommitFromRefAsync(theirsRef);
        await repo.MergeAsync([theirsHead], mergeOpts, checkoutOpts);
    }

    /// <summary>
    /// Resolves a branch name to its commit OID.
    /// </summary>
    private static async Task<GitOid> ResolveBranchAsync(GitRepository repo, string branchName)
    {
        string refName = branchName.StartsWith(GitReferences.RefsHeadsDir, StringComparison.Ordinal)
            ? branchName
            : GitReferences.RefsHeadsDir + branchName;

        GitReference reference = await repo.ReferenceResolveAsync(refName)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"branch '{branchName}' not found",
                GitErrorCategory.Reference);

        if (reference is not GitDirectReference direct)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"branch '{branchName}' does not resolve to a direct reference",
                GitErrorCategory.Reference);
        }

        return direct.Target;
    }

    // ── Index assertion helpers ─────────────────────────────────────────

    /// <summary>
    /// Expected index entry for merge result verification. Mirrors
    /// <c>struct merge_index_entry</c> (merge_helpers.h:7-12).
    /// </summary>
    public sealed record MergeIndexEntry(
        uint Mode,
        string OidStr,
        int Stage,
        string Path);

    /// <summary>
    /// Expected REUC entry. Mirrors <c>struct merge_reuc_entry</c>
    /// (merge_helpers.h:25-33).
    /// </summary>
    public sealed record MergeReucEntry(
        string Path,
        uint AncestorMode,
        uint OurMode,
        uint TheirMode,
        string AncestorOidStr,
        string OurOidStr,
        string TheirOidStr);

    /// <summary>
    /// Asserts that the index entries match the expected list. Mirrors
    /// <c>merge_test_index</c> (merge_helpers.c:237-258).
    /// </summary>
    public static void AssertIndex(GitIndex index, IReadOnlyList<MergeIndexEntry> expected)
    {
        Assert.Equal(expected.Count, index.EntryCount);

        IReadOnlyList<GitIndexEntry> entries = index.Snapshot();
        for (int i = 0; i < expected.Count; i++)
        {
            MergeIndexEntry exp = expected[i];
            GitIndexEntry act = entries[i];

            Assert.Equal(exp.Stage, act.Stage);
            Assert.Equal(exp.Mode, (uint)act.Mode);
            Assert.Equal(exp.Path, act.Path.ToUtf8String());

            if (exp.OidStr.Length > 0)
            {
                var expOid = GitOid.Parse(exp.OidStr.AsSpan(), GitHashAlgorithmKind.Sha1);
                Assert.Equal(expOid, act.Id);
            }
        }
    }

    /// <summary>
    /// Asserts that the REUC entries match the expected list. Mirrors
    /// <c>merge_test_reuc</c> (merge_helpers.c:283-329).
    /// </summary>
    public static void AssertReuc(GitIndex index, IReadOnlyList<MergeReucEntry> expected)
    {
        Assert.Equal(expected.Count, index.ReucCount);

        IReadOnlyList<GitIndexReucEntry> reucEntries = index.ReucEntries;
        for (int i = 0; i < expected.Count; i++)
        {
            MergeReucEntry exp = expected[i];
            GitIndexReucEntry act = reucEntries[i];

            Assert.Equal(exp.Path, act.Path.ToUtf8String());
            Assert.Equal(exp.AncestorMode, act.Modes[0]);
            Assert.Equal(exp.OurMode, act.Modes[1]);
            Assert.Equal(exp.TheirMode, act.Modes[2]);

            if (exp.AncestorMode > 0)
            {
                var expOid = GitOid.Parse(exp.AncestorOidStr.AsSpan(), GitHashAlgorithmKind.Sha1);
                Assert.Equal(expOid, act.Oids[0]);
            }

            if (exp.OurMode > 0)
            {
                var expOid = GitOid.Parse(exp.OurOidStr.AsSpan(), GitHashAlgorithmKind.Sha1);
                Assert.Equal(expOid, act.Oids[1]);
            }

            if (exp.TheirMode > 0)
            {
                var expOid = GitOid.Parse(exp.TheirOidStr.AsSpan(), GitHashAlgorithmKind.Sha1);
                Assert.Equal(expOid, act.Oids[2]);
            }
        }
    }

    /// <summary>
    /// Asserts that a workdir file matches the expected content.
    /// </summary>
    public static void AssertWorkdirFile(GitRepository repo, string relativePath, string expectedContent)
    {
        string fullPath = Path.Combine(repo.Workdir!, relativePath);
        Assert.True(File.Exists(fullPath), $"workdir file '{relativePath}' should exist");
        string actual = File.ReadAllText(fullPath);
        Assert.Equal(expectedContent, actual);
    }

    /// <summary>
    /// Asserts that the workdir files match the expected list. Mirrors
    /// <c>merge_test_workdir</c> (merge_helpers.c:342-365): creates a blob
    /// from each expected workdir file (applying clean filters) and compares
    /// OIDs.
    /// </summary>
    public static async Task AssertWorkdirAsync(GitRepository repo, IReadOnlyList<MergeIndexEntry> expected)
    {
        Assert.Equal(expected.Count, CountWorkdirFiles(repo));

        foreach (MergeIndexEntry exp in expected)
        {
            string fullPath = Path.Combine(repo.Workdir!, exp.Path);
            Assert.True(File.Exists(fullPath), $"workdir file '{exp.Path}' should exist");

            // Create a blob from the workdir file, applying clean filters.
            // Matches C's git_blob_create_from_workdir.
            (GitOid actualOid, GitIndexEntry _) = await BlobHelper.CreateFromWorkdirAsync(repo, exp.Path);

            if (exp.OidStr.Length > 0)
            {
                var expOid = GitOid.Parse(exp.OidStr.AsSpan(), GitHashAlgorithmKind.Sha1);
                Assert.Equal(expOid, actualOid);
            }
        }
    }

    /// <summary>
    /// Counts the number of non-.git files in the workdir.
    /// </summary>
    public static int CountWorkdirFiles(GitRepository repo)
    {
        string workdir = repo.Workdir!;
        string gitDir = Path.Combine(workdir, ".git") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(workdir, "*", SearchOption.AllDirectories)
            .Count(p => !p.StartsWith(gitDir, StringComparison.Ordinal));
    }
}

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.UnitTests.Merge;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Shared helpers for apply execute tests. Mirrors
/// <c>tests/libgit2/apply/apply_helpers.c</c> — validation functions that
/// compare the repo index and workdir against expected entries.
/// </summary>
internal static class ApplyTestHelpers
{
    /// <summary>
    /// Git file mode 0100644 (octal) = 0x81A4 = 33188 (decimal).
    /// </summary>
    public const uint ModeRegular = 0x81A4;

    /// <summary>
    /// Git file mode 0100755 (octal) = 0x81ED = 33261 (decimal).
    /// </summary>
    public const uint ModeExecutable = 0x81ED;

    /// <summary>
    /// Expected entry for apply result verification. Mirrors
    /// <c>struct merge_index_entry</c> (apply_helpers.h via merge_helpers.h).
    /// </summary>
    public sealed record ExpectedEntry(
        uint Mode,
        string OidStr,
        int Stage,
        string Path);

    /// <summary>
    /// Converts <see cref="ExpectedEntry"/> list to
    /// <see cref="MergeTestHelpers.MergeIndexEntry"/> list for reuse of
    /// <see cref="MergeTestHelpers.AssertIndex"/>.
    /// </summary>
    private static IReadOnlyList<MergeTestHelpers.MergeIndexEntry> ToMergeEntries(
        IReadOnlyList<ExpectedEntry> entries)
    {
        var result = new List<MergeTestHelpers.MergeIndexEntry>(entries.Count);
        foreach (ExpectedEntry e in entries)
        {
            result.Add(new MergeTestHelpers.MergeIndexEntry(
                e.Mode, e.OidStr, e.Stage, e.Path));
        }
        return result;
    }

    /// <summary>
    /// Asserts that the repo index entries match the expected list. Mirrors
    /// <c>validate_apply_index</c> (apply_helpers.c:52-69).
    /// </summary>
    public static async Task AssertIndexAsync(GitRepository repo, IReadOnlyList<ExpectedEntry> expected)
    {
        GitIndex index = await repo.GetIndexAsync();
        MergeTestHelpers.AssertIndex(index, ToMergeEntries(expected));
    }

    /// <summary>
    /// Asserts that the workdir files match the expected list. Mirrors
    /// <c>validate_apply_workdir</c> (apply_helpers.c:30-50). Creates a blob
    /// from each workdir file (applying clean filters) and compares OIDs.
    /// </summary>
    public static Task AssertWorkdirAsync(GitRepository repo, IReadOnlyList<ExpectedEntry> expected)
        => MergeTestHelpers.AssertWorkdirAsync(repo, ToMergeEntries(expected));

    /// <summary>
    /// Asserts that the index is unchanged from HEAD. Mirrors
    /// <c>validate_index_unchanged</c> (apply_helpers.c:86-108). Compares
    /// each index entry against the HEAD tree entries.
    /// </summary>
    public static async Task AssertIndexUnchangedAsync(GitRepository repo)
    {
        GitTree headTree = await ResolveHeadTreeAsync(repo);
        GitIndex index = await repo.GetIndexAsync();
        AssertIndexMatchesTree(index, headTree);
    }

    /// <summary>
    /// Asserts that the workdir is unchanged from HEAD. Mirrors
    /// <c>validate_workdir_unchanged</c> (apply_helpers.c:110-135). Compares
    /// each workdir file OID against the HEAD tree entries.
    /// </summary>
    public static async Task AssertWorkdirUnchangedAsync(GitRepository repo)
    {
        GitTree headTree = await ResolveHeadTreeAsync(repo);
        await AssertWorkdirMatchesTreeAsync(repo, headTree);
    }

    /// <summary>
    /// Resets the repo to the given commit (hard reset), matching the C
    /// test <c>initialize</c> function's <c>git_reset(..., GIT_RESET_HARD)</c>.
    /// </summary>
    public static async Task ResetToAsync(GitRepository repo, string commitOidHex)
    {
        var oid = GitOid.Parse(commitOidHex.AsSpan(), repo.ObjectFormat);
        Commit commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");
        await repo.ResetAsync(commit, LibGit2CS.Reset.GitResetMode.Hard);

        // Force-checkout HEAD to ensure workdir files have LF content
        // (fixture may have CRLF from Windows extraction).
        await repo.CheckoutHeadAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        });
    }

    /// <summary>
    /// Appends content to a workdir file. Mirrors <c>cl_git_append2file</c>.
    /// </summary>
    public static void AppendToFile(GitRepository repo, string relativePath, string content)
    {
        string fullPath = Path.Combine(repo.Workdir!, relativePath);
        File.AppendAllText(fullPath, content);
    }

    /// <summary>
    /// Rewrites a workdir file with the given content. Mirrors
    /// <c>cl_git_rewritefile</c>.
    /// </summary>
    public static void RewriteFile(GitRepository repo, string relativePath, string content)
    {
        string fullPath = Path.Combine(repo.Workdir!, relativePath);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>
    /// Removes a workdir file. Mirrors <c>cl_git_rmfile</c>.
    /// </summary>
    public static void RemoveFile(GitRepository repo, string relativePath)
    {
        string fullPath = Path.Combine(repo.Workdir!, relativePath);
        File.Delete(fullPath);
    }

    /// <summary>
    /// Creates a file in the workdir. Mirrors <c>cl_git_mkfile</c>.
    /// </summary>
    public static void MakeFile(GitRepository repo, string relativePath, string content)
    {
        string fullPath = Path.Combine(repo.Workdir!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>
    /// Adds a file to the index by path (reads from workdir). Mirrors
    /// <c>git_index_add_bypath</c>.
    /// </summary>
    public static async Task AddByPathAsync(GitRepository repo, string relativePath)
    {
        GitIndex index = await repo.GetIndexAsync();
        await index.AddByPathAsync(relativePath);
        await index.WriteAsync();
    }

    /// <summary>
    /// Removes a file from the index by path. Mirrors
    /// <c>git_index_remove</c> + <c>git_index_write</c>.
    /// </summary>
    public static async Task RemoveFromIndexAsync(GitRepository repo, string relativePath)
    {
        GitIndex index = await repo.GetIndexAsync();
        index.Remove(relativePath, stage: 0);
        await index.WriteAsync();
    }

    /// <summary>
    /// Adds a specific entry to the index (OID + mode + path). Mirrors
    /// the C test pattern of building a <c>git_index_entry</c> and calling
    /// <c>git_index_add</c> + <c>git_index_write</c>.
    /// </summary>
    public static async Task AddEntryToIndexAsync(
        GitRepository repo, string relativePath, string oidHex, uint mode)
    {
        var oid = GitOid.Parse(oidHex.AsSpan(), repo.ObjectFormat);
        GitIndex index = await repo.GetIndexAsync();
        index.Add(new GitIndexEntry(relativePath, oid, (GitFileMode)mode));
        await index.WriteAsync();
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private static async Task<GitTree> ResolveHeadTreeAsync(GitRepository repo)
    {
        GitReference head = await repo.ReferenceResolveAsync("HEAD")
            ?? throw new InvalidOperationException("HEAD not found");

        GitOid targetOid;
        if (head is GitDirectReference dr)
        {
            targetOid = dr.Target;
        }
        else if (head is GitSymbolicReference sr)
        {
            GitReference? target = await sr.TargetAsync();
            if (target is GitDirectReference dt)
            {
                targetOid = dt.Target;
            }
            else
            {
                throw new InvalidOperationException("HEAD does not resolve to a commit");
            }
        }
        else
        {
            throw new InvalidOperationException("HEAD does not resolve to a commit");
        }

        Commit commit = await repo.ObjectLookupAsync<Commit>(targetOid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {targetOid} not found");
        return await repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"tree for {targetOid} not found");
    }

    private static void AssertIndexMatchesTree(GitIndex index, GitTree tree)
    {
        var treeEntries = tree.ToList();
        IReadOnlyList<GitIndexEntry> indexEntries = index.Snapshot();

        Assert.Equal(treeEntries.Count, indexEntries.Count);

        for (int i = 0; i < treeEntries.Count; i++)
        {
            GitTreeEntry treeEntry = treeEntries[i];
            GitIndexEntry indexEntry = indexEntries[i];

            Assert.Equal(treeEntry.Name, indexEntry.Path);
            Assert.Equal((uint)treeEntry.Mode, (uint)indexEntry.Mode);
            Assert.Equal(treeEntry.Id, indexEntry.Id);
        }
    }

    private static async Task AssertWorkdirMatchesTreeAsync(GitRepository repo, GitTree tree)
    {
        var treeEntries = tree.ToList();
        var workdirFiles = Directory.EnumerateFiles(repo.Workdir!, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(".git", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(treeEntries.Count, workdirFiles.Count);

        foreach (GitTreeEntry treeEntry in treeEntries)
        {
            string fullPath = Path.Combine(repo.Workdir!, treeEntry.Name.ToUtf8String());
            Assert.True(File.Exists(fullPath),
                $"workdir file '{treeEntry.Name}' should exist");

            (GitOid actualOid, GitIndexEntry _) = await BlobHelper.CreateFromWorkdirAsync(repo, treeEntry.Name);
            Assert.Equal(treeEntry.Id, actualOid);
        }
    }
}

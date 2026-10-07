using LibGit2CS.Core;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeRenameTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public MergeRenameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeRename_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> WriteBlobAsync(string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return await _repo.ObjectWriteAsync(GitObjectType.Blob, bytes, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds a tree from path→OID mappings. Uses TreeBuilder directly,
    /// bypassing the workdir/index for precise control over rename scenarios.
    /// </summary>
    private async Task<GitTree> BuildTreeAsync(params (string Path, GitOid Oid, GitFileMode Mode)[] entries)
    {
        GitTreeBuilder bld = _repo.NewTreeBuilder();
        foreach ((string? path, GitOid oid, GitFileMode mode) in entries)
        {
            await bld.InsertAsync(path, oid, mode);
        }

        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        bld.Dispose();
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        return tree;
    }

    private async Task<GitTree> BuildTreeFromContent(params (string Path, string Content)[] files)
    {
        var entries = new (string, GitOid, GitFileMode)[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            entries[i] = (files[i].Path, await WriteBlobAsync(files[i].Content), GitFileMode.Regular);
        }

        return await BuildTreeAsync(entries);
    }

    // ── Exact OID rename ──────────────────────────────────────────────────

    [Fact]
    public async Task ExactRename_OursModified_NoConflict()
    {
        // Ancestor: file.txt = "hello\n"
        // Ours: renamed.txt = "hello\n" (same OID → exact rename, no content change)
        // Theirs: file.txt = "hello modified\n" (modified)
        GitOid baseOid = await WriteBlobAsync("hello\n");
        GitOid theirOid = await WriteBlobAsync("hello modified\n");

        GitTree ancestor = await BuildTreeAsync(("file.txt", baseOid, GitFileMode.Regular));
        GitTree ours = await BuildTreeAsync(("renamed.txt", baseOid, GitFileMode.Regular));
        GitTree theirs = await BuildTreeAsync(("file.txt", theirOid, GitFileMode.Regular));

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        // Exact rename on ours + modified on theirs → should resolve as a
        // rename+modify: the renamed file gets the modified content.
        Assert.False(result.HasConflicts);
    }

    [Fact]
    public async Task ExactRename_BothRenamedSameTarget_NoConflict()
    {
        // Both sides rename file.txt → renamed.txt (same OID).
        GitOid baseOid = await WriteBlobAsync("hello\n");

        GitTree ancestor = await BuildTreeAsync(("file.txt", baseOid, GitFileMode.Regular));
        GitTree ours = await BuildTreeAsync(("renamed.txt", baseOid, GitFileMode.Regular));
        GitTree theirs = await BuildTreeAsync(("renamed.txt", baseOid, GitFileMode.Regular));

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
    }

    [Fact]
    public async Task TinyFiles_1To2_NoRenameDetected()
    {
        // Ancestor: file.txt = "hello world\n" Ours: A.txt = "hello world ours\n" (renamed + modified, similar) Theirs: B.txt = "hello world theirs\n" (renamed
        // + modified, similar) C (merge.c:1935 + hashsig.c:219-223): the MERGE similarity metric is SMART_WHITESPACE WITHOUT ALLOW_SMALL_FILES (contrast the
        // diff path, diff_tform.c:348) — a 3-line file has < 4 line-hash buckets, so the signature is invalid and the score is 0: NO rename is detected and no
        // 1-to-2 conflict forms.
        string baseContent = "hello world\nthis is a test file\nwith multiple lines\n";
        string ourContent = "hello world ours\nthis is a test file\nwith multiple lines\n";
        string theirContent = "hello world theirs\nthis is a test file\nwith multiple lines\n";

        GitTree ancestor = await BuildTreeFromContent(("file.txt", baseContent));
        GitTree ours = await BuildTreeFromContent(("A.txt", ourContent));
        GitTree theirs = await BuildTreeFromContent(("B.txt", theirContent));

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.NotNull(result.EntryByPath("A.txt", stage: 0));
        Assert.NotNull(result.EntryByPath("B.txt", stage: 0));
    }

    [Fact]
    public async Task ExactRename_RenamedDeleted()
    {
        // Ours renames file.txt → renamed.txt, theirs deletes file.txt.
        GitOid baseOid = await WriteBlobAsync("hello\n");

        GitTree ancestor = await BuildTreeAsync(("file.txt", baseOid, GitFileMode.Regular));
        GitTree ours = await BuildTreeAsync(("renamed.txt", baseOid, GitFileMode.Regular));
        GitTree theirs = await BuildTreeAsync(); // empty tree = deleted

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        // Rename/delete conflict.
        Assert.True(result.HasConflicts);
    }

    [Fact]
    public async Task ExactRename_BothRenamed2To1_Conflict()
    {
        // Ancestor has A.txt and B.txt (different content).
        // Ours renames A.txt → C.txt, keeps B.txt.
        // Theirs renames B.txt → C.txt, keeps A.txt.
        GitOid oidA = await WriteBlobAsync("content A\n");
        GitOid oidB = await WriteBlobAsync("content B\n");

        GitTree ancestor = await BuildTreeAsync(
            ("A.txt", oidA, GitFileMode.Regular),
            ("B.txt", oidB, GitFileMode.Regular));
        GitTree ours = await BuildTreeAsync(
            ("B.txt", oidB, GitFileMode.Regular),
            ("C.txt", oidA, GitFileMode.Regular));
        GitTree theirs = await BuildTreeAsync(
            ("A.txt", oidA, GitFileMode.Regular),
            ("C.txt", oidB, GitFileMode.Regular));

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        // 2-to-1 rename: both renamed to C.txt with different content.
        Assert.True(result.HasConflicts);
    }

    // ── Inexact (content similarity) rename ──────────────────────────────

    [Fact]
    public async Task InexactRename_SimilarContent_Resolves()
    {
        // Ancestor: file.txt has 20 lines.
        // Ours: renamed.txt = ancestor content + 1 new line at end.
        // Theirs: file.txt = ancestor content (unchanged).
        // The renamed file has a DIFFERENT OID (content changed), but
        // similarity > 50% → inexact rename detection should coalesce.
        string[] lines = new string[20];
        for (int i = 0; i < 20; i++)
        {
            lines[i] = $"line {i}";
        }

        string baseContent = string.Join("\n", lines) + "\n";
        string renamedContent = baseContent + "extra line\n";

        GitTree ancestor = await BuildTreeFromContent(("file.txt", baseContent));
        GitTree ours = await BuildTreeFromContent(("renamed.txt", renamedContent));
        GitTree theirs = ancestor; // no change on theirs

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        // Inexact rename: ours renamed + modified, theirs unchanged →
        // should resolve as rename+modify (no conflict).
        Assert.False(result.HasConflicts);
    }

    [Fact]
    public async Task InexactRename_CompletelyDifferent_NoRename()
    {
        // Ancestor: file.txt has unique content A.
        // Ours: renamed.txt has completely different content B (different OID).
        // Theirs: file.txt unchanged.
        // Similarity < 50% → no rename detected. The delete (file.txt on ours)
        // and add (renamed.txt on ours) are resolved independently by
        // ResolveOneRemoved (deleted on ours, unmodified on theirs → deleted;
        // added on ours, absent on theirs → added). No conflict.
        string contentA = "alpha\nbeta\ngamma\ndelta\nepsilon\nzeta\neta\ntheta\n";
        string contentB = "red\ngreen\nblue\nyellow\norange\npurple\npink\nbrown\n";

        GitTree ancestor = await BuildTreeFromContent(("file.txt", contentA));
        GitTree ours = await BuildTreeFromContent(("renamed.txt", contentB));
        GitTree theirs = ancestor;

        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        // No rename detected (similarity < threshold). The add and delete
        // resolve independently → no conflict, but file.txt is gone and
        // renamed.txt is present.
        Assert.False(result.HasConflicts);
        Assert.DoesNotContain(result.Entries, e => e.Path.ToUtf8String() == "file.txt");
        Assert.Contains(result.Entries, e => e.Path.ToUtf8String() == "renamed.txt");
    }

    [Fact]
    public async Task InexactRename_DisabledByThreshold100()
    {
        // With RenameThreshold=100, only exact OID matches count.
        // Ours renames file.txt → renamed.txt with modified content (different OID).
        // Theirs modifies file.txt.
        // Without inexact: file.txt is modified on theirs but deleted on ours
        // → modify/delete conflict. renamed.txt is added on ours → resolved.
        string baseContent = "hello world\nthis is a test\nwith multiple lines\nfor similarity\n";
        string renamedContent = "hello world modified\nthis is a test\nwith multiple lines\nfor similarity\n";
        string theirContent = "hello world theirs\nthis is a test\nwith multiple lines\nfor similarity\n";

        GitTree ancestor = await BuildTreeFromContent(("file.txt", baseContent));
        GitTree ours = await BuildTreeFromContent(("renamed.txt", renamedContent));
        GitTree theirs = await BuildTreeFromContent(("file.txt", theirContent));

        var opts = new GitMergeOptions { RenameThreshold = 100 };
        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, opts, cancellationToken: TestContext.Current.CancellationToken);

        // Threshold 100 → no inexact → file.txt is modify-on-theirs + delete-on-ours
        // → modify/delete conflict.
        Assert.True(result.HasConflicts);
    }

    // ── FindRenames disabled ──────────────────────────────────────────────

    [Fact]
    public async Task FindRenamesDisabled_RenamePlusModify_StaysConflict()
    {
        // With FindRenames off, a rename+modify on ours + modify on theirs
        // cannot be resolved (the rename isn't detected, so the add and delete
        // are separate, but theirs modified the deleted file → conflict).
        GitOid baseOid = await WriteBlobAsync("hello\n");
        GitOid ourOid = await WriteBlobAsync("hello modified by us\n");
        GitOid theirOid = await WriteBlobAsync("hello modified by them\n");

        GitTree ancestor = await BuildTreeAsync(("file.txt", baseOid, GitFileMode.Regular));
        GitTree ours = await BuildTreeAsync(("renamed.txt", ourOid, GitFileMode.Regular));
        GitTree theirs = await BuildTreeAsync(("file.txt", theirOid, GitFileMode.Regular));

        var opts = new GitMergeOptions { Flags = GitMergeFlags.None };
        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, opts, cancellationToken: TestContext.Current.CancellationToken);

        // Without rename detection: file.txt is modified on theirs but
        // deleted on ours → modify/delete conflict. renamed.txt is added on
        // ours, absent on theirs → added (resolved). So there IS a conflict
        // on file.txt.
        Assert.True(result.HasConflicts);
    }

    [Fact]
    public async Task FindRenamesEnabled_RenamePlusModify_Resolves()
    {
        // Same scenario as above but WITH FindRenames (default).
        // The rename is detected (exact OID match since ours has same baseOid
        // … wait, ours has different content). Actually: ours has
        // "renamed.txt" with a DIFFERENT OID (ourOid), so exact match won't
        // work. But inexact similarity should detect the rename since
        // "hello modified by us" is similar to "hello".
        // For a reliable test, use exact OID: ours renames file.txt →
        // renamed.txt (SAME OID as ancestor), theirs modifies file.txt
        // (different OID).
        GitOid baseOid = await WriteBlobAsync("hello\n");
        GitOid theirOid = await WriteBlobAsync("hello modified by them\n");

        GitTree ancestor = await BuildTreeAsync(("file.txt", baseOid, GitFileMode.Regular));
        GitTree ours = await BuildTreeAsync(("renamed.txt", baseOid, GitFileMode.Regular));
        GitTree theirs = await BuildTreeAsync(("file.txt", theirOid, GitFileMode.Regular));

        // With FindRenames (default): exact rename detection matches the
        // renamed.txt entry to the deleted file.txt by OID. Then the conflict
        // is rename on ours + modify on theirs → ResolveOneRenamed handles it.
        GitIndex result = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
    }

    // ── Rename limit config ───────────────────────────────────────────────

    [Fact]
    public async Task RenameLimitExceeded_SkipsInexact()
    {
        // Set a very low rename limit so inexact is skipped.
        // With inexact skipped, the rename is NOT detected, and the
        // add+delete pairs resolve independently (no conflict since theirs
        // is unchanged). But with inexact enabled, the rename+modify would
        // be detected and resolved.
        string baseContent = "line1\nline2\nline3\nline4\nline5\nline6\n";
        string renamedContent = "line1\nline2\nline3\nline4 modified\nline5\nline6\n";

        // Build ancestor with 3 files, ours renames all 3 with modifications,
        // theirs unchanged.
        GitTree ancestor = await BuildTreeFromContent(
            ("a.txt", baseContent),
            ("b.txt", baseContent),
            ("c.txt", baseContent));
        GitTree ours = await BuildTreeFromContent(
            ("a_renamed.txt", renamedContent),
            ("b_renamed.txt", renamedContent),
            ("c_renamed.txt", renamedContent));
        GitTree theirs = ancestor;

        // TargetLimit=1 → 3 conflicts > 1 → inexact skipped.
        var optsLowLimit = new GitMergeOptions { TargetLimit = 1 };
        GitIndex resultLow = await _repo.MergeTreesAsync(ancestor, ours, theirs, optsLowLimit, cancellationToken: TestContext.Current.CancellationToken);

        // Without inexact: the add+delete pairs resolve independently (theirs
        // is unchanged → all deletes resolve as "deleted", all adds resolve
        // as "added"). No conflict, but the rename is NOT attributed.
        Assert.False(resultLow.HasConflicts);

        // With default limit (200), inexact should detect the renames.
        GitIndex resultDefault = await _repo.MergeTreesAsync(ancestor, ours, theirs, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(resultDefault.HasConflicts);

        // Both resolve, but the low-limit version has the original files
        // deleted + new files added, while the default version has the files
        // renamed (the merged content reflects the rename+modify).
        // Verify the low-limit version has the old names deleted.
        Assert.DoesNotContain(resultLow.Entries, e => e.Path.ToUtf8String() == "a.txt");
        Assert.Contains(resultLow.Entries, e => e.Path.ToUtf8String() == "a_renamed.txt");
    }
}

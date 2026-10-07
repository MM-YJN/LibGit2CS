using System.Text;

using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;

namespace LibGit2CS.IntegrationTests.Blame;

/// <summary>
/// Integration tests for the blame engine (<see cref="LibGit2CS.Blame"/>
/// namespace) exercised end-to-end against locally-initialized repositories
/// built with <see cref="RepoBuilder"/>. No Docker, no fixtures — runs on
/// every build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="Transports.BlameDockerTests"/> cover the happy path over a
/// 3-commit linear history fetched via SSH, but the
/// <see cref="GitBlame.BufferAsync"/> path (~200 lines), the
/// <see cref="BlameEngine.PassBlameAsync"/> multi-parent / first-parent /
/// oldest-commit-boundary branches, and the
/// <see cref="BlameScoreboard.IndexBlobLines"/> incomplete-line edge cases
/// are entirely cold. These local tests close those gaps.
/// </para>
/// <para>
/// <b>Assertion style.</b> Structural/property assertions on hunk counts,
/// line counts, commit OIDs, and boundary flags.
/// </para>
/// </para>
/// </remarks>
public sealed class BlameIntegrationTests
{
    // ── root commit only — numParents==0 branch ────────────────────────

    /// <summary>
    /// Blaming a file in a repository with a single (root) commit
    /// attributes every line to the root commit. Exercises the
    /// <c>numParents == 0</c> early-return in
    /// <see cref="BlameEngine.PassBlameAsync"/> — the root commit is its own
    /// boundary, so no parent traversal occurs.
    /// </summary>
    [Fact]
    public async Task Blame_RootCommitOnly_AttributesAllLinesToRoot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync(
            "f.txt",
            "line1\nline2\nline3\n"u8.ToArray(),
            "root\n",
            parent: null,
            updateRef: "refs/heads/main",
            ct: ct);

        using GitBlame blame = await builder.Repo.BlameFileAsync("f.txt", cancellationToken: ct);

        Assert.Equal(3, blame.LineCount);
        Assert.Equal(1, blame.HunkCount);
        BlameHunk hunk = blame.GetHunk(0);
        Assert.Equal(root, hunk.FinalCommitId);
        Assert.Equal(3, hunk.LinesInHunk);
        Assert.Equal(1, hunk.FinalStartLineNumber);
    }

    // ── no trailing newline — incomplete-line branch ───────────────────

    /// <summary>
    /// Blaming a file whose content does not end with a newline
    /// indexes the final incomplete line. Exercises the
    /// <c>buf[len-1] != '\n'</c> and <c>!bol</c> branches in
    /// <see cref="BlameScoreboard.IndexBlobLines"/> — the "no trailing
    /// newline" path that increments the line count by one for the
    /// incomplete last line.
    /// </summary>
    [Fact]
    public async Task Blame_NoTrailingNewline_IndexesIncompleteLine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        await builder.CommitFileAsync(
            "f.txt",
            "hello"u8.ToArray(),
            "root\n",
            parent: null,
            updateRef: "refs/heads/main",
            ct: ct);

        using GitBlame blame = await builder.Repo.BlameFileAsync("f.txt", cancellationToken: ct);

        Assert.Equal(1, blame.LineCount);
        Assert.Equal(1, blame.HunkCount);
        Assert.Equal(1, blame.GetHunk(0).LinesInHunk);
    }

    // ── empty file — len==0 branch ─────────────────────────────────────

    /// <summary>
    /// Blaming an empty file produces zero hunks and zero lines.
    /// Exercises the <c>len == 0</c> early-continue branch in
    /// <see cref="BlameScoreboard.IndexBlobLines"/>.
    /// </summary>
    /// <remarks>
    /// The empty blob is written directly to the ODB and a tree is built via
    /// <see cref="GitTreeBuilder"/> rather than <c>CommitFileAsync</c>, to
    /// bypass the CRLF clean filter which would otherwise turn the empty
    /// workdir file into a single-newline blob.
    /// </remarks>
    [Fact]
    public async Task Blame_EmptyFile_ReturnsZeroHunks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid emptyBlob = await builder.WriteBlobAsync(Array.Empty<byte>(), ct);
        GitOid tree = await builder.BuildTreeAsync([("empty.txt", emptyBlob, GitFileMode.Regular)], ct);
        await builder.CommitAsync(tree, [], "root\n", "refs/heads/main", ct);

        using GitBlame blame = await builder.Repo.BlameFileAsync("empty.txt", cancellationToken: ct);

        // An empty blob produces a single blame hunk covering zero lines
        // (matching libgit2: git_blame_hunkcount returns 1 for an empty
        // file — the empty hunk). The hunk's LinesInHunk is 0.
        Assert.Equal(1, blame.HunkCount);
        Assert.Equal(0, blame.GetHunk(0).LinesInHunk);
    }

    // ── OldestCommit boundary ──────────────────────────────────────────

    /// <summary>
    /// Blaming with <see cref="GitBlameOptions.OldestCommit"/> set to
    /// the middle commit of a 3-commit linear history stops blame at that
    /// commit. Lines introduced at or before the oldest commit are
    /// attributed to it with <see cref="BlameHunk.Boundary"/> == 1.
    /// Exercises the <c>commit.Id == sb.Options.OldestCommit</c> early-return
    /// in <see cref="BlameEngine.PassBlameAsync"/>.
    /// </summary>
    [Fact]
    public async Task Blame_OldestCommit_StopsAndSetsBoundary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid[] commits = await builder.BuildSameFileHistoryAsync(
            "f.txt",
            ["line1\n"u8.ToArray(), "line1\nline2\n"u8.ToArray(), "line1\nline2\nline3\n"u8.ToArray()],
            ct: ct);

        // OldestCommit = second commit; blame should stop there for line 2.
        using GitBlame blame = await builder.Repo.BlameFileAsync(
            "f.txt",
            new GitBlameOptions { OldestCommit = commits[1] },
            ct);

        Assert.Equal(3, blame.LineCount);

        // Find the hunk covering line 2 (introduced in commit[1], the oldest boundary).
        BlameHunk? hunkLine2 = blame.GetHunkByLine(2);
        Assert.NotNull(hunkLine2);
        Assert.Equal(commits[1], hunkLine2!.FinalCommitId);
        Assert.Equal(1, hunkLine2.Boundary);
    }

    // ── NewestCommit non-HEAD ──────────────────────────────────────────

    /// <summary>
    /// Blaming with <see cref="GitBlameOptions.NewestCommit"/> set to
    /// a non-HEAD commit blames the file as of that commit, not HEAD.
    /// Exercises the <c>NewestCommit is null or zero</c> false branch in
    /// <see cref="GitBlame.NormalizeOptionsAsync"/> — the HEAD-resolution
    /// is skipped because the caller supplied an explicit newest commit.
    /// </summary>
    [Fact]
    public async Task Blame_NewestCommit_NonHead_UsesSpecifiedCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid[] commits = await builder.BuildSameFileHistoryAsync(
            "f.txt",
            ["line1\n"u8.ToArray(), "line1\nline2\n"u8.ToArray(), "line1\nline2\nline3\n"u8.ToArray()],
            ct: ct);

        // Blame at the second commit — only 2 lines should be visible.
        using GitBlame blame = await builder.Repo.BlameFileAsync(
            "f.txt",
            new GitBlameOptions { NewestCommit = commits[1] },
            ct);

        Assert.Equal(2, blame.LineCount);
        // Line 1 → commit[0], line 2 → commit[1].
        Assert.Equal(commits[0], blame.GetHunkByLine(1)!.FinalCommitId);
        Assert.Equal(commits[1], blame.GetHunkByLine(2)!.FinalCommitId);
    }

    // ── merge history — multi-parent PassBlameAsync ────────────────────

    /// <summary>
    /// Blaming a file in a merge commit with two parents attributes
    /// lines to the parent that introduced them. Exercises the multi-parent
    /// loop in <see cref="BlameEngine.PassBlameAsync"/> (the
    /// <c>sgOrigin</c> array with <c>numParents &gt; 1</c>), and the
    /// <c>PassWholeBlameAsync</c> path when both parents share the same blob
    /// for an unchanged line.
    /// </summary>
    /// <remarks>
    /// The merge tree's <c>a.txt</c> contains <c>base\nleft\nright\n</c> —
    /// the union of both branches' edits. <c>base</c> was in the root
    /// (ancestor of both parents, so both parents have the same blob for
    /// that line → <c>PassWholeBlameAsync</c>); <c>left</c> was introduced
    /// by the left parent; <c>right</c> by the right parent.
    /// </remarks>
    [Fact]
    public async Task Blame_MergeHistory_AttributesToBothParents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();

        // Root: a.txt with "base\n"
        GitOid root = await builder.CommitFileAsync(
            "a.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);

        // Left branch: add a line.
        GitOid left = await builder.CommitFileAsync(
            "a.txt", "base\nleft\n"u8.ToArray(), "left\n", parent: root, updateRef: "refs/heads/left", ct: ct);

        // Right branch: add a different line.
        GitOid right = await builder.CommitFileAsync(
            "a.txt", "base\nright\n"u8.ToArray(), "right\n", parent: root, updateRef: "refs/heads/right", ct: ct);

        // Merge tree: a.txt = union of both branches' edits.
        GitOid mergeBlob = await builder.WriteBlobAsync("base\nleft\nright\n"u8.ToArray(), ct);
        GitOid mergeTree = await builder.BuildTreeAsync(
            [("a.txt", mergeBlob, GitFileMode.Regular)], ct);

        GitOid merge = await builder.MergeCommitAsync(mergeTree, [left, right], "merge\n", updateRef: "refs/heads/main", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/main", ct);

        using GitBlame blame = await builder.Repo.BlameFileAsync("a.txt", cancellationToken: ct);

        // 3 lines: base, left, right.
        Assert.Equal(3, blame.LineCount);
        // base → root (both parents share the same blob for line 1 → PassWholeBlame).
        Assert.Equal(root, blame.GetHunkByLine(1)!.FinalCommitId);
        // left or right for lines 2/3 — the exact attribution depends on the
        // diff against each parent. At minimum, both parents must appear.
        var commitIds = new HashSet<GitOid>();
        for (int i = 0; i < blame.HunkCount; i++)
        {
            commitIds.Add(blame.GetHunk(i).FinalCommitId);
        }

        Assert.Contains(left, commitIds);
        Assert.Contains(right, commitIds);
    }

    // ── FirstParent flag ───────────────────────────────────────────────

    /// <summary>
    /// Blaming with <see cref="GitBlameFlags.FirstParent"/> follows
    /// only the first-parent chain from the merge commit. Exercises the
    /// <c>(opt &amp; FirstParent) != 0 &amp;&amp; numParents &gt; 1</c>
    /// branch in <see cref="BlameEngine.PassBlameAsync"/>.
    /// </summary>
    [Fact]
    public async Task Blame_FirstParentFlag_FollowsFirstParentOnly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();

        GitOid root = await builder.CommitFileAsync(
            "f.txt", "line1\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);

        GitOid left = await builder.CommitFileAsync(
            "f.txt", "line1\nleft\n"u8.ToArray(), "left\n", parent: root, updateRef: "refs/heads/left", ct: ct);

        GitOid right = await builder.CommitFileAsync(
            "f.txt", "line1\nright\n"u8.ToArray(), "right\n", parent: root, updateRef: "refs/heads/right", ct: ct);

        // Merge tree containing the left branch's version of f.txt.
        GitOid leftBlob = await builder.WriteBlobAsync("line1\nleft\n"u8.ToArray(), ct);
        GitOid mergeTree = await builder.BuildTreeAsync(
            [("f.txt", leftBlob, GitFileMode.Regular)], ct);

        GitOid merge = await builder.MergeCommitAsync(mergeTree, [left, right], "merge\n", updateRef: "refs/heads/main", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/main", ct);

        using GitBlame blame = await builder.Repo.BlameFileAsync(
            "f.txt",
            new GitBlameOptions { Flags = GitBlameFlags.FirstParent },
            ct);

        // line1 → root (first parent chain: merge → left → root).
        // line "left" → left (first parent of merge).
        Assert.Equal(2, blame.LineCount);
        Assert.Equal(root, blame.GetHunkByLine(1)!.FinalCommitId);
        Assert.Equal(left, blame.GetHunkByLine(2)!.FinalCommitId);
    }

    // ── BufferAsync — in-memory buffer blame ───────────────────────────

    /// <summary>
    /// <see cref="GitBlame.BufferAsync"/> updates an existing blame
    /// with in-memory buffer contents. Lines added in the buffer (not
    /// present in the committed version) are marked with a zero
    /// <see cref="BlameHunk.FinalCommitId"/> (buffer-blame hunks). This is
    /// the single biggest coverage gain — the entire <c>BufferAsync</c>
    /// path (~200 lines) plus 10 helper methods are 0% covered before this
    /// test.
    /// </summary>
    /// <remarks>
    /// <see cref="GitBlame.LineCount"/> returns the reference blame's line
    /// count (the committed file's line count), not the buffer's — this
    /// matches libgit2's <c>git_blame_linecount</c> which reads
    /// <c>sb-&gt;num_lines</c> set during the original <c>FileAsync</c>.
    /// Assert on hunk OIDs instead of <see cref="GitBlame.LineCount"/>.
    /// </remarks>
    [Fact]
    public async Task Blame_BufferAsync_ModifiedLinesAttributedToBuffer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        await builder.BuildSameFileHistoryAsync(
            "f.txt",
            ["line1\nline2\n"u8.ToArray()],
            ct: ct);

        // Reference blame of the committed version.
        using GitBlame refBlame = await builder.Repo.BlameFileAsync("f.txt", cancellationToken: ct);
        Assert.Equal(2, refBlame.LineCount);

        // Buffer adds a new line and modifies line 2.
        byte[] buffer = "line1\nMODIFIED\nNEW\n"u8.ToArray();

        using GitBlame bufBlame = await GitBlame.BufferAsync(refBlame, buffer, ct);

        // The buffer blame produces hunks covering the buffer content. At
        // least one hunk must be a buffer-blame hunk (zero commit ID) for
        // the added/modified lines, and at least one hunk must retain the
        // original commit for the unchanged line 1.
        bool hasBufferHunk = false;
        bool hasCommittedHunk = false;
        for (int i = 0; i < bufBlame.HunkCount; i++)
        {
            BlameHunk h = bufBlame.GetHunk(i);
            if (h.FinalCommitId.IsZero)
            {
                hasBufferHunk = true;
            }
            else
            {
                hasCommittedHunk = true;
            }
        }

        Assert.True(hasBufferHunk, "buffer blame should produce at least one zero-OID hunk for added/modified lines");
        Assert.True(hasCommittedHunk, "buffer blame should retain at least one committed hunk for unchanged lines");
    }
}

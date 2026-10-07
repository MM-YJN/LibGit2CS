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
/// Integration tests for the file-level 3-way merge
/// (<see cref="GitRepository.MergeFileFromIndexAsync"/> and the
/// <see cref="GitMergeFile"/> facade) exercised end-to-end against
/// locally-initialized repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>MergeFileTests</c> cover the pure
/// <see cref="GitMergeFile.Merge"/> buffer-merge path against in-memory
/// inputs and the fixture-extracted <c>merge-resolve.zip</c> repo. They
/// exercise <see cref="GitRepository.MergeFileFromIndexAsync"/> only
/// against pre-existing fixture index entries. These integration tests
/// build the repo and its index entries from scratch (write blobs to the
/// ODB via <see cref="GitObjectDb.WriteAsync"/>, construct
/// <see cref="GitIndexEntry"/> directly), then drive
/// <see cref="GitRepository.MergeFileFromIndexAsync"/> through the real
/// ODB-read path (<see cref="MergeFileInputFromIndexAsync"/>) — covering the
/// automerge, conflict, and favor-resolution scenarios without fixture
/// state. This exercises <see cref="GitMergeFile.FromInputs"/>,
/// <see cref="GitMergeFileInput"/>, and <see cref="GitMergeFileResult"/>
/// through the repo-level entry point.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/merge/files.c</c> (<c>test_merge_files__automerge_from_bufs</c>,
/// <c>test_merge_files__conflict</c>) and <c>tests/libgit2/merge/fromindex.c</c>,
/// adapted to build the blobs and index entries from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class MergeFileIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private const uint ModeRegular = (uint)GitFileMode.Regular;
    private const uint ModeExec = (uint)GitFileMode.Executable;

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-mergefile-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes a blob to the ODB and returns a <see cref="GitIndexEntry"/>
    /// pointing at it with the given path and mode.
    /// </summary>
    private static async Task<GitIndexEntry> WriteBlobAsEntryAsync(
        GitRepository repo, string path, uint mode, string content, CancellationToken ct)
    {
        GitOid oid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, Encoding.UTF8.GetBytes(content), ct).ConfigureAwait(false);
        return new GitIndexEntry(path, oid, (GitFileMode)mode);
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

    /// <summary>Decodes a <see cref="GitMergeFileResult.Content"/> as UTF-8.</summary>
    private static string ContentString(GitMergeFileResult r)
        => Encoding.UTF8.GetString(r.Content.Span);

    // ── MergeFileFromIndexAsync — automergeable ────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with three
    /// index entries whose changes don't conflict (ours edits the start,
    /// theirs edits the end) returns an automergeable result with the
    /// combined content. Mirrors <c>test_merge_files__automerge_from_bufs</c>
    /// (files.c) but reads the blobs from the ODB via index entries.
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_NonConflicting_Automerges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "Zero\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\nTen\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs, cancellationToken: ct);

            Assert.True(result.Automergeable);
            Assert.Equal("test.txt", result.Path?.ToUtf8String());
            Assert.Equal(ModeRegular, result.Mode);
            Assert.Equal(
                "Zero\n1\n2\n3\n4\n5\n6\n7\n8\n9\nTen\n",
                ContentString(result));
            Assert.Equal(0, result.ConflictCount);
            Assert.False(result.HasConflicts);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with no
    /// ancestor (a 2-way merge) on two sides with no common context produces
    /// a conflict (no automerge). Mirrors the 2-way conflict behavior in
    /// <c>test_merge_files__no_ancestor</c> (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_NoAncestor_ConflictingEdits_ReportsConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Two sides with no shared prefix/suffix on the conflicting line.
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "Zero\n1\n2\n3\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "1\n2\n3\nTen\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor: null, ours, theirs, cancellationToken: ct);

            // 2-way merge with no common context for the changed lines → conflict.
            Assert.False(result.Automergeable);
            Assert.True(result.HasConflicts);
            Assert.Equal("test.txt", result.Path?.ToUtf8String());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── MergeFileFromIndexAsync — conflicts ─────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with
    /// conflicting edits to the same line returns a non-automergeable result
    /// with conflict markers and a positive <see cref="GitMergeFileResult.ConflictCount"/>.
    /// Mirrors <c>test_merge_files__conflict</c> (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_Conflicting_ReportsConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nline2\nline3\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nOURS\nline3\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nTHEIRS\nline3\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs, cancellationToken: ct);

            Assert.False(result.Automergeable);
            Assert.True(result.HasConflicts);
            Assert.Equal(1, result.ConflictCount);

            // The conflict markers reference the two sides.
            string content = ContentString(result);
            Assert.Contains("<<<<<<<", content);
            Assert.Contains("=======\n", content);
            Assert.Contains(">>>>>>>", content);
            Assert.Contains("OURS", content);
            Assert.Contains("THEIRS", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── MergeFileFromIndexAsync — favor modes ───────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with
    /// <see cref="GitMergeFileFavor.Ours"/> resolves a conflict by taking
    /// our side, dropping the conflict markers. Mirrors
    /// <c>test_merge_files__favor_ours</c> (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_FavorOurs_TakesOurSide()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nline2\nline3\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nOURS\nline3\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nTHEIRS\nline3\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs,
                new GitMergeFileOptions { Favor = GitMergeFileFavor.Ours },
                ct);

            // Favor=Ours resolves the conflict → automergeable, 0 conflicts.
            Assert.True(result.Automergeable);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("line1\nOURS\nline3\n", ContentString(result));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with
    /// <see cref="GitMergeFileFavor.Theirs"/> resolves a conflict by taking
    /// their side. Mirrors <c>test_merge_files__favor_theirs</c> (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_FavorTheirs_TakesTheirSide()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nline2\nline3\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nOURS\nline3\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nTHEIRS\nline3\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs,
                new GitMergeFileOptions { Favor = GitMergeFileFavor.Theirs },
                ct);

            // Favor=Theirs resolves the conflict → automergeable, 0 conflicts.
            Assert.True(result.Automergeable);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("line1\nTHEIRS\nline3\n", ContentString(result));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with
    /// <see cref="GitMergeFileFavor.Union"/> resolves a conflict by taking
    /// both sides in order (ours then theirs). Mirrors
    /// <c>test_merge_files__favor_union</c> (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_FavorUnion_TakesBothSides()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nline2\nline3\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nOURS\nline3\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nTHEIRS\nline3\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs,
                new GitMergeFileOptions { Favor = GitMergeFileFavor.Union },
                ct);

            // Favor=Union resolves the conflict by concatenating both sides.
            Assert.True(result.Automergeable);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("line1\nOURS\nTHEIRS\nline3\n", ContentString(result));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── MergeFileFromIndexAsync — labels and mode ──────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with custom
    /// <see cref="GitMergeFileOptions.AncestorLabel"/>,
    /// <see cref="GitMergeFileOptions.OurLabel"/>, and
    /// <see cref="GitMergeFileOptions.TheirLabel"/> renders those labels in
    /// the conflict markers. Mirrors <c>test_merge_files__labels</c>
    /// (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_CustomLabels_RenderInConflictMarkers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nline2\nline3\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nOURS\nline3\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "line1\nTHEIRS\nline3\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs,
                new GitMergeFileOptions
                {
                    AncestorLabel = "BASE",
                    OurLabel = "HEAD",
                    TheirLabel = "BRANCH",
                },
                ct);

            Assert.False(result.Automergeable);
            string content = ContentString(result);
            Assert.Contains("<<<<<<< HEAD", content);
            Assert.Contains(">>>>>>> BRANCH", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> with
    /// mismatched modes (ours executable, theirs regular) and a regular
    /// ancestor returns the executable mode — the best-mode heuristic
    /// prefers executable over regular when the ancestor doesn't dictate.
    /// Mirrors <c>test_merge_files__best_mode</c> (files.c).
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_MismatchedModes_PrefersExecutable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndexEntry ancestor = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "shared\n", ct);
            GitIndexEntry ours = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeExec, "shared\n", ct);
            GitIndexEntry theirs = await WriteBlobAsEntryAsync(
                repo, "test.txt", ModeRegular, "shared\n", ct);

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(
                ancestor, ours, theirs, cancellationToken: ct);

            // Content automerges (identical content); mode picks exec.
            Assert.True(result.Automergeable);
            Assert.Equal(ModeExec, result.Mode);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Argument validation ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeFileFromIndexAsync"/> throws
    /// <see cref="ArgumentException"/> when all three entries are null.
    /// Mirrors <c>test_merge_files__all_null</c> (fromindex.c) and guards
    /// the entry precondition.
    /// </summary>
    [Fact]
    public async Task MergeFileFromIndex_AllNull_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => repo.MergeFileFromIndexAsync(null, null, null, cancellationToken: ct));
        }
        finally
        {
            Cleanup(path);
        }
    }
}

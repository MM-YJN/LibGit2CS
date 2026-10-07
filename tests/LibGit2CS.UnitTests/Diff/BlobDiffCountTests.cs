using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Count-assertion tests for blob/buffer diffs, ported from libgit2's
/// <c>tests/libgit2/diff/blob.c</c>. These exercise <see cref="GitPatch.FromBlobs"/>
/// / <see cref="GitPatch.FromBlobAndBuffer"/> / <see cref="GitPatch.FromBuffers"/>
/// without workdir or index — pure content comparison. Counts mirror clar's
/// <c>diff_expects</c> (<c>diff_helpers.c</c>): files, file_status, hunks, lines,
/// line_ctxt/line_adds/line_dels (all including their EOFNL counterparts).
/// </summary>
/// <remarks>
/// Blob OIDs resolve against the <c>attr</c> fixture (pre-baked); no runtime
/// mutation. clar uses <c>opts.context_lines = 1</c> unless noted. The 4 text
/// blobs map to: a=<c>root_test1</c>, b=<c>root_test2</c>, c=<c>root_test3</c>,
/// d=<c>root_test4.txt</c>.
/// </remarks>
public sealed class BlobDiffCountTests : DiffGoldenBase
{
    // attr fixture blob OIDs (verified against tests/resources/attr).
    private const string OidA = "45141a79";                  // root_test1
    private const string OidB = "4d713dc4";                  // root_test2
    private const string OidC = "c96bbb2c2557a832";          // root_test3
    private const string OidD = "a0f7217a";                  // root_test4.txt
    private const string OidOldD = "fe773770";               // old root_test4.txt
    private const string OidAlien = "edf3dcee";              // alien.png (binary)
    private const string OidHeart = "de863bff";              // heart.png (binary)
    private const string OidBin = "b435cd56";                // binfile (NUL byte)

    private static readonly GitDiffOptions s_ctx1 = new() { ContextLines = 1 };
    private static readonly GitDiffOptions s_ctx1Unmod = new()
    { ContextLines = 1, Flags = GitDiffOptionsFlags.IncludeUnmodified };

    // binfile content (blob b435cd56): "0123456789\n" + 0x01..0x08,0x09,0x00 + "\n0123456789\n" (33 bytes).
    // The embedded NUL (0x00) triggers binary detection.
    private static readonly byte[] s_binContent =
    [
        .. "0123456789\n"u8,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x00,
        .. "\n0123456789\n"u8,
    ];

    // ━━ can_compare_text_blobs (blob.c) ━━

    // a vs b: MODIFIED, 1 hunk, 6 lines (1 ctxt, 5 adds, 0 dels).
    [Fact]
    public async Task Blobs_a_vs_b_Modified_1hunk_6lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob blobA = await ResolveBlobAsync(repo, OidA);
        GitBlob blobB = await ResolveBlobAsync(repo, OidB);
        using GitPatch patch = repo.PatchFromBlobs(blobA, blobB, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(0, c.FilesBinary);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(6, c.Lines);
        Assert.Equal(1, c.LineContext);
        Assert.Equal(5, c.LineAdds);
        Assert.Equal(0, c.LineDels);
    }

    // buffer equivalent of a vs b: same counts via FromBuffers.
    [Fact]
    public async Task Buffers_a_vs_b_Modified_1hunk_6lines()
    {
        byte[] a = "Hello from the root\n"u8.ToArray();
        byte[] b = "Hello from the root\n\nSome additional lines\n\nDown here below\n\n"u8.ToArray();

        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        using GitPatch patch = repo.PatchFromBuffers(a, b, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(6, c.Lines);
        Assert.Equal(1, c.LineContext);
        Assert.Equal(5, c.LineAdds);
        Assert.Equal(0, c.LineDels);
    }

    // b vs c: MODIFIED, 1 hunk, 15 lines (3 ctxt, 9 adds, 3 dels).
    [Fact]
    public async Task Blobs_b_vs_c_Modified_1hunk_15lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob blobB = await ResolveBlobAsync(repo, OidB);
        GitBlob blobC = await ResolveBlobAsync(repo, OidC);
        using GitPatch patch = repo.PatchFromBlobs(blobB, blobC, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(15, c.Lines);
        Assert.Equal(3, c.LineContext);
        Assert.Equal(9, c.LineAdds);
        Assert.Equal(3, c.LineDels);
    }

    // a vs c: MODIFIED, 1 hunk, 13 lines (0 ctxt, 12 adds, 1 del).
    [Fact]
    public async Task Blobs_a_vs_c_Modified_1hunk_13lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob blobA = await ResolveBlobAsync(repo, OidA);
        GitBlob blobC = await ResolveBlobAsync(repo, OidC);
        using GitPatch patch = repo.PatchFromBlobs(blobA, blobC, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(13, c.Lines);
        Assert.Equal(0, c.LineContext);
        Assert.Equal(12, c.LineAdds);
        Assert.Equal(1, c.LineDels);
    }

    // c vs d: MODIFIED, 2 hunks, 14 lines (4 ctxt, 6 adds, 4 dels).
    [Fact]
    public async Task Blobs_c_vs_d_Modified_2hunks_14lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob blobC = await ResolveBlobAsync(repo, OidC);
        GitBlob blobD = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(blobC, blobD, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(2, c.Hunks);
        Assert.Equal(14, c.Lines);
        Assert.Equal(4, c.LineContext);
        Assert.Equal(6, c.LineAdds);
        Assert.Equal(4, c.LineDels);
    }

    // ━━ comparing_two_text_blobs_honors_interhunkcontext (blob.c) ━━
    // old_d vs d, context=3. Two edit regions separated by limited context.

    // interhunk 0: two separate hunks.
    [Fact]
    public async Task Blobs_oldD_vs_D_Ctx3_Interhunk0_Yields2hunks()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob oldD = await ResolveBlobAsync(repo, OidOldD);
        GitBlob d = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(oldD, d,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 0 });

        Assert.Equal(2, (await DiffCounter.CountAsync(patch)).Hunks);
    }

    // interhunk = 1: the two hunks merge into one.
    [Fact]
    public async Task Blobs_oldD_vs_D_Ctx3_Interhunk1_Yields1hunk()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob oldD = await ResolveBlobAsync(repo, OidOldD);
        GitBlob d = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(oldD, d,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 1 });

        Assert.Equal(1, (await DiffCounter.CountAsync(patch)).Hunks);
    }

    // ━━ can_compare_identical_blobs (blob.c) ━━

    // d vs d: UNMODIFIED (with INCLUDE_UNMODIFIED), 0 hunks, 0 lines.
    [Fact]
    public async Task Blobs_d_vs_d_Identical_Unmodified()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob d = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(d, d,
            new GitDiffOptions { ContextLines = 1, Flags = GitDiffOptionsFlags.IncludeUnmodified });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(0, c.Hunks);
        Assert.Equal(0, c.Lines);
    }

    // ━━ can_compare_buffer_to_buffer (blob.c) ━━
    // context=0, interhunk=0. Forward and reverse.

    // forward: 4 hunks, 9 lines (0 ctxt, 4 adds, 5 dels).
    [Fact]
    public async Task Buffers_forward_4hunks_9lines_4adds_5dels()
    {
        byte[] old = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n"u8.ToArray();
        byte[] neu = "a\nB\nc\nd\nE\nF\nh\nj\nk\n"u8.ToArray();
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        using GitPatch patch = repo.PatchFromBuffers(old, neu,
            new GitDiffOptions { ContextLines = 0, InterHunkLines = 0 });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(4, c.Hunks);
        Assert.Equal(9, c.Lines);
        Assert.Equal(0, c.LineContext);
        Assert.Equal(4, c.LineAdds);
        Assert.Equal(5, c.LineDels);
    }

    // reverse: 4 hunks, 9 lines (0 ctxt, 5 adds, 4 dels — swapped).
    [Fact]
    public async Task Buffers_reverse_4hunks_9lines_5adds_4dels()
    {
        byte[] old = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n"u8.ToArray();
        byte[] neu = "a\nB\nc\nd\nE\nF\nh\nj\nk\n"u8.ToArray();
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        using GitPatch patch = repo.PatchFromBuffers(old, neu,
            new GitDiffOptions { ContextLines = 0, InterHunkLines = 0, Flags = GitDiffOptionsFlags.Reverse });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(4, c.Hunks);
        Assert.Equal(9, c.Lines);
        Assert.Equal(0, c.LineContext);
        Assert.Equal(5, c.LineAdds);
        Assert.Equal(4, c.LineDels);
    }

    // ━━ can_compare_against_null_blobs (blob.c) ━━
    // A null side produces Added/Deleted; Patch.FromBlobs(repo, blob, null)
    // routes through BuildStandaloneDelta which derives status from zero IDs.

    // d vs null: DELETED, 1 hunk, 14 dels (d = root_test4.txt, 14 lines).
    [Fact]
    public async Task Blobs_d_vs_null_Deleted_1hunk_14dels()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob d = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(d, null, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(14, c.Lines);
        Assert.Equal(14, c.LineDels);
    }

    // d vs null, REVERSE: ADDED, 1 hunk, 14 adds.
    [Fact]
    public async Task Blobs_d_vs_null_Reverse_Added_1hunk_14adds()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob d = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(d, null,
            new GitDiffOptions { ContextLines = 1, Flags = GitDiffOptionsFlags.Reverse });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(14, c.Lines);
        Assert.Equal(14, c.LineAdds);
    }

    // alien (binary) vs null: binary DELETED, no hunks/lines.
    [Fact]
    public async Task Blobs_alien_vs_null_Binary_Deleted()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob alien = await ResolveBlobAsync(repo, OidAlien);
        using GitPatch patch = repo.PatchFromBlobs(alien, null, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
        Assert.Equal(0, c.Lines);
    }

    // null vs alien (binary): binary ADDED.
    [Fact]
    public async Task Blobs_null_vs_alien_Binary_Added()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob alien = await ResolveBlobAsync(repo, OidAlien);
        using GitPatch patch = repo.PatchFromBlobs(null, alien, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
    }

    // ━━ can_compare_identical_blobs (blob.c, null + binary variants) ━━

    // null vs null: UNMODIFIED.
    [Fact]
    public async Task Blobs_null_vs_null_Unmodified()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        using GitPatch patch = repo.PatchFromBlobs(null, null, s_ctx1Unmod);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(0, c.Hunks);
        Assert.Equal(0, c.Lines);
    }

    // alien vs alien: UNMODIFIED + binary.
    [Fact]
    public async Task Blobs_alien_vs_alien_Unmodified_Binary()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob alien = await ResolveBlobAsync(repo, OidAlien);
        using GitPatch patch = repo.PatchFromBlobs(alien, alien, s_ctx1Unmod);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
    }

    // ━━ can_compare_two_binary_blobs (blob.c) + binary/text ━━

    // alien vs heart (two distinct binary blobs): MODIFIED + binary, no hunks.
    [Fact]
    public async Task Blobs_alien_vs_heart_Binary_Modified()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob alien = await ResolveBlobAsync(repo, OidAlien);
        GitBlob heart = await ResolveBlobAsync(repo, OidHeart);
        using GitPatch patch = repo.PatchFromBlobs(alien, heart, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
        Assert.Equal(0, c.Lines);
    }

    // alien (binary) vs d (text): MODIFIED + binary.
    [Fact]
    public async Task Blobs_alien_vs_d_Binary_Modified()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob alien = await ResolveBlobAsync(repo, OidAlien);
        GitBlob d = await ResolveBlobAsync(repo, OidD);
        using GitPatch patch = repo.PatchFromBlobs(alien, d, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
    }

    // ━━ binary_data_comparisons (blob.c) ━━
    // NUL-byte binary detection. nonbin = blob a; bin = blob b435cd56.

    // nonbin vs nonbin_content: UNMODIFIED, not binary.
    [Fact]
    public async Task BlobToBuf_nonbin_vs_nonbinContent_Unmodified_NotBinary()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobAndBuffer(
            a, "Hello from the root\n"u8.ToArray(), s_ctx1Unmod);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(0, c.FilesBinary);
        Assert.Equal(0, c.Hunks);
    }

    // bin vs bin_content: UNMODIFIED but BINARY (NUL byte detected).
    [Fact]
    public async Task BlobToBuf_bin_vs_binContent_Unmodified_Binary()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob bin = await ResolveBlobAsync(repo, OidBin);
        using GitPatch patch = repo.PatchFromBlobAndBuffer(
            bin, s_binContent, s_ctx1Unmod);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
    }

    // nonbin vs bin_content: MODIFIED, binary.
    [Fact]
    public async Task BlobToBuf_nonbin_vs_binContent_Modified_Binary()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobAndBuffer(
            a, s_binContent, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
    }

    // bin blob vs nonbin blob: MODIFIED, binary.
    [Fact]
    public async Task Blobs_bin_vs_nonbin_Modified_Binary()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob bin = await ResolveBlobAsync(repo, OidBin);
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobs(bin, a, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.True(c.FilesBinary > 0);
        Assert.Equal(0, c.Hunks);
    }

    // ── FORCE_TEXT variants: binary content forced to text diff ──

    // bin vs bin_content, FORCE_TEXT: UNMODIFIED (clar's assert_identical_blobs_comparison
    // checks only status+hunks+lines, NOT files_binary).
    [Fact]
    public async Task BlobToBuf_bin_vs_binContent_ForceText_Unmodified()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob bin = await ResolveBlobAsync(repo, OidBin);
        using GitPatch patch = repo.PatchFromBlobAndBuffer(
            bin, s_binContent,
            new GitDiffOptions { ContextLines = 1, Flags = GitDiffOptionsFlags.IncludeUnmodified | GitDiffOptionsFlags.ForceText });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(0, c.Hunks);
        Assert.Equal(0, c.Lines);
    }

    // nonbin vs bin_content, FORCE_TEXT: MODIFIED, treated as text, 4 lines.
    [Fact]
    public async Task BlobToBuf_nonbin_vs_binContent_ForceText_Modified_4lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobAndBuffer(
            a, s_binContent,
            new GitDiffOptions { ContextLines = 1, Flags = GitDiffOptionsFlags.ForceText });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(0, c.FilesBinary);
        Assert.Equal(4, c.Lines);
    }

    // bin blob vs nonbin blob, FORCE_TEXT: MODIFIED, text, 4 lines.
    [Fact]
    public async Task Blobs_bin_vs_nonbin_ForceText_Modified_4lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob bin = await ResolveBlobAsync(repo, OidBin);
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobs(bin, a,
            new GitDiffOptions { ContextLines = 1, Flags = GitDiffOptionsFlags.ForceText });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(0, c.FilesBinary);
        Assert.Equal(4, c.Lines);
    }

    // ━━ can_compare_blob_to_buffer (blob.c) ━━

    // null blob vs a_content: ADDED, 1 line.
    [Fact]
    public async Task BlobToBuf_null_vs_aContent_Added_1add()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        using GitPatch patch = repo.PatchFromBlobAndBuffer(null,
            "Hello from the root\n"u8.ToArray(), s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(1, c.Lines);
        Assert.Equal(1, c.LineAdds);
    }

    // blob a vs null (no target): DELETED, 1 line. Semantically blob-vs-nothing;
    // Patch.FromBlobs(repo, a, null) captures the "absent new side" (zero OID).
    [Fact]
    public async Task Blob_a_vs_null_Deleted_1del()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobs(a, null, s_ctx1);

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(1, c.Lines);
        Assert.Equal(1, c.LineDels);
    }

    // blob a vs null, REVERSE: ADDED, 1 add.
    [Fact]
    public async Task Blob_a_vs_null_Reverse_Added_1add()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitBlob a = await ResolveBlobAsync(repo, OidA);
        using GitPatch patch = repo.PatchFromBlobs(a, null,
            new GitDiffOptions { ContextLines = 1, Flags = GitDiffOptionsFlags.Reverse });

        DiffCounter c = await DiffCounter.CountAsync(patch);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c.Hunks);
        Assert.Equal(1, c.LineAdds);
    }
}

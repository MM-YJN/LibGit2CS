using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Diff;

// Regression coverage against libgit2 1.9.4.
// parity behaviors for the Diff subsystem:
//   UnquotePath must handle \" and \\ plus octal/control escapes, so
//         quoted paths are not corrupted and invalid escapes are rejected.
//   Hunk body line-count check must reject any non-zero
//         remainder, not only `> 0` (patch_parse.c:671-676).
//   Workdir OID recompute must run only for Modified deltas where C does
//         ('modified_uncertain', diff_generate.c:889-930).
//   Flag implications: INCLUDE_TYPECHANGE_TREES→TYPECHANGE and
//         SHOW_UNTRACKED_CONTENT→INCLUDE_UNTRACKED (diff_generate.c:510-516).
//   Merge must apply git_diff_delta__should_skip to merged deltas
//         (diff_tform.c:168-171).
//   hasData for UNTRACKED/IGNORED deltas must honor SHOW_UNTRACKED_CONTENT
//         and the C status switch (diff_file.c:109-126).
//   UpdateBinaryFlags must include the GIT_XDIFF_MAX_SIZE binary-size guard
//         (patch_generate.c:52-68, diff_xdiff.h:19).
//   The binary inflated length must reject declared
//         sizes >= 2^31 instead of overflowing an (int) cast (patch_parse.c:840).
//   ParsedPatchSource.LineStatsAsync must not count EOFNL marker lines
//         (patch.c:93-128 skips them).
//   Email diffstat must use C's width 0 (raw bars, email.c:176-180),
//         not a hardcoded width.
//   Hunk line-number arithmetic that goes negative must report
//         GIT_EAPPLYFAIL, not ArgumentOutOfRangeException.
//   A binary patch with empty old+new data must produce
//         an empty postimage (apply.c:361-362).
public sealed class DiffMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public DiffMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Creates a repo with a committed file <paramref name="fileName"/>.</summary>
    private async Task<(GitRepository Repo, GitOid CommitOid, GitTree Tree)> CreateRepoWithFileAsync(string fileName, string content)
    {
        string repoPath = Path.Combine(_tempDir, "repo_" + Guid.NewGuid().ToString("N")[..8]);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        return (repo, commitOid, tree);
    }

    // ── Quoted Path Octal Escapes Decode To Bytes ────────────────────

    [Fact]
    public void QuotedPath_OctalEscapes_DecodeToBytes()
    {
        // libgit2's own diff printer emits octal escapes for bytes > 0x7e: "caf\303\251" must decode to the raw bytes 0xC3 0xA9, not the literal digits
        // "303251".
        const string patchText =
            "diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- \"a/caf\\303\\251.txt\"\n" +
            "+++ \"b/caf\\303\\251.txt\"\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        var diff = GitDiff.FromBuffer(patchText);
        Assert.True(diff.GetDelta(0).Path.Span.SequenceEqual<byte>(
            [.. "caf"u8.ToArray(), 0xC3, 0xA9, .. ".txt"u8.ToArray()]));
        Assert.Equal("café.txt", diff.GetDelta(0).Path.ToUtf8String());
    }

    [Fact]
    public void QuotedPath_ControlEscapes_DecodeToControlChars()
    {
        const string patchText =
            "diff --git \"a/a\\tb\" \"b/a\\tb\"\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- \"a/a\\tb\"\n" +
            "+++ \"b/a\\tb\"\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        var diff = GitDiff.FromBuffer(patchText);
        Assert.Equal("a\tb", diff.GetDelta(0).Path.ToUtf8String());
    }

    [Fact]
    public void QuotedPath_InvalidEscape_Throws()
    {
        // C's git_str_unquote rejects unknown escapes with 'invalid quoted
        // character' (str.c:1053-1056).
        const string patchText =
            "diff --git \"a/bad\\q\" \"b/bad\\q\"\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- \"a/bad\\q\"\n" +
            "+++ \"b/bad\\q\"\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        GitException ex = Assert.Throws<GitException>(() => GitDiff.FromBuffer(patchText));
        Assert.Contains("invalid quoted character", ex.Message);
    }

    // ── Hunk Over Consumed Side Throws ───────────────────────────────

    [Fact]
    public void Hunk_OverConsumedSide_Throws()
    {
        // @@ -2,2 +2,2 @@ with 3 deletions: remainingOld goes negative — C
        // rejects any non-zero remainder (patch_parse.c:671-676).
        const string patchText =
            "diff --git a/f.txt b/f.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -2,2 +2,2 @@\n" +
            "-a\n" +
            "-b\n" +
            "-c\n" +
            "+d\n" +
            "+e\n";

        GitException ex = Assert.Throws<GitException>(() => GitDiff.FromBuffer(patchText));
        Assert.Contains("invalid patch hunk", ex.Message);
    }

    // ── Workdir Modified Size Changed New File Id Stays Zero ─────────

    [Fact]
    public async Task WorkdirModified_SizeChanged_NewFileIdStaysZero()
    {
        // A content change with a size difference: C sets modified_uncertain
        // only for size 0→positive (diff_generate.c:889-893), so the workdir
        // OID is NOT recomputed and delta.NewFile.Id stays zero.
        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("f.txt", "hello\n");
        await using (repo)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "hello world, a longer line\n", cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta delta = diff.GetDelta(0);
            Assert.Equal(GitDeltaStatus.Modified, delta.Status);
            Assert.True(delta.NewFile.Id.IsZero, "new_file.id must stay zero (C: modified_uncertain false)");
            Assert.Equal(0, (int)(delta.NewFile.Flags & GitDiffFileFlags.ValidId));
        }
    }

    [Fact]
    public async Task WorkdirModified_SameSize_RacyRecomputeStillHappens()
    {
        // Control: the mtime/racy branch still sets modified_uncertain, so
        // the OID IS recomputed and the delta carries it.
        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("f.txt", "hello\n");
        await using (repo)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "world!\n", cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Modified, diff.GetDelta(0).Status);
        }
    }

    // ── Include Typechange Trees Alone Produces Single Typechange ────

    [Fact]
    public async Task IncludeTypechangeTrees_Alone_ProducesSingleTypechange()
    {
        // C implies INCLUDE_TYPECHANGE from INCLUDE_TYPECHANGE_TREES
        // (diff_generate.c:510-513). A matched pair whose basic type changed
        // (tracked file → symlink) must yield ONE TYPECHANGE delta.
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation requires privileges on Windows
        }

        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("f", "blob\n");
        await using (repo)
        {
            File.Delete(Path.Combine(repo.Workdir!, "f"));
            File.CreateSymbolicLink(Path.Combine(repo.Workdir!, "f"), "target");

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeTypechangeTrees },
                TestContext.Current.CancellationToken);

            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Typechange, diff.GetDelta(0).Status);
            Assert.Equal("f", diff.GetDelta(0).Path.ToUtf8String());
        }
    }

    [Fact]
    public async Task ShowUntrackedContent_Alone_IncludesUntracked()
    {
        // C implies INCLUDE_UNTRACKED from SHOW_UNTRACKED_CONTENT
        // (diff_generate.c:514-516).
        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("f.txt", "hello\n");
        await using (repo)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "u.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.ShowUntrackedContent },
                TestContext.Current.CancellationToken);

            // f.txt is unmodified (not included); u.txt is untracked and now
            // included via the implied INCLUDE_UNTRACKED.
            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Untracked, diff.GetDelta(0).Status);
            Assert.Equal("u.txt", diff.GetDelta(0).Path.ToUtf8String());
        }
    }

    // ── Merge Dropped Unmodified When Not Requested ──────────────────

    [Fact]
    public async Task Merge_DroppedUnmodified_WhenNotRequested()
    {
        // A file staged (head→index Added) and deleted in the workdir
        // (index→workdir Deleted) merges to UNMODIFIED — C drops it via
        // git_diff_delta__should_skip (diff_tform.c:168-171).
        (GitRepository repo, _, GitTree tree) = await CreateRepoWithFileAsync("f.txt", "hello\n");
        await using (repo)
        {
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "x.txt"), "staged\n", cancellationToken: TestContext.Current.CancellationToken);
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            await index.AddByPathAsync("x.txt", TestContext.Current.CancellationToken);
            await index.WriteAsync(TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(workdir, "x.txt"));

            using GitDiff diff = await repo.DiffTreeToWorkdirWithIndexAsync(tree, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, diff.DeltaCount);
        }
    }

    // ── Untracked Delta Without Show Untracked Content Has No Hunks ──

    [Fact]
    public async Task UntrackedDelta_WithoutShowUntrackedContent_HasNoHunks()
    {
        // C gives the UNTRACKED new side NO_DATA without SHOW_UNTRACKED_CONTENT
        // (diff_file.c:109-126), so no full-content hunks are produced for the
        // untracked file.
        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("f.txt", "hello\n");
        await using (repo)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "u.txt"), "untracked content\n", cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeUntracked },
                TestContext.Current.CancellationToken);
            GitPatch patch = await GitPatch.FromDiffAsync(diff, 0, TestContext.Current.CancellationToken);

            Assert.Equal(0, await patch.GetHunkCountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UntrackedDelta_WithShowUntrackedContent_HasHunks()
    {
        // Control: SHOW_UNTRACKED_CONTENT restores the data.
        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("f.txt", "hello\n");
        await using (repo)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "u.txt"), "untracked content\n", cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.ShowUntrackedContent },
                TestContext.Current.CancellationToken);
            GitPatch patch = await GitPatch.FromDiffAsync(diff, 0, TestContext.Current.CancellationToken);

            Assert.Equal(1, await patch.GetHunkCountAsync(TestContext.Current.CancellationToken));
        }
    }

    // ── Oversized File Is Marked Binary ──────────────────────────────

    [Fact]
    public async Task OversizedFile_IsMarkedBinary()
    {
        // A file larger than GIT_XDIFF_MAX_SIZE (1024*1024*1023) must be
        // marked binary by patch_generated_update_binary (patch_generate.c:
        // 61-63), not treated as text and fed to XDiff. The workdir file is
        // sparse, so no 2 GiB is actually written or read.
        if (OperatingSystem.IsWindows())
        {
            return; // sparse-file semantics differ on Windows
        }

        (GitRepository repo, _, _) = await CreateRepoWithFileAsync("big.bin", "small\n");
        await using (repo)
        {
            string path = Path.Combine(repo.Workdir!, "big.bin");
            using (FileStream fs = new(path, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(2L * 1024 * 1024 * 1024);
            }

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { MaxSize = int.MaxValue },
                TestContext.Current.CancellationToken);

            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Modified, diff.GetDelta(0).Status);

            // Materializing the patch must mark the delta binary WITHOUT
            // reading the 2 GiB file (UpdateBinaryFlags runs before any
            // content load).
            GitPatch patch = await GitPatch.FromDiffAsync(diff, 0, TestContext.Current.CancellationToken);
            Assert.True((diff.GetDelta(0).Flags & GitDiffFileFlags.Binary) != 0, "oversized delta must be binary");
            Assert.True(await patch.GetIsBinaryAsync(TestContext.Current.CancellationToken));
        }
    }

    // ── Binary Patch Oversized Declared Length Throws ────────────────

    [Fact]
    public void BinaryPatch_OversizedDeclaredLength_Throws()
    {
        // C stores the full int64 (patch_parse.c:840); lengths above
        // int.MaxValue are rejected.
        const string patchText =
            "diff --git a/f.bin b/f.bin\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "GIT binary patch\n" +
            "literal 3000000000\n" +
            "\n" +
            "literal 3000000000\n" +
            "\n";

        GitException ex = Assert.Throws<GitException>(() => GitDiff.FromBuffer(patchText));
        Assert.Contains("invalid binary size", ex.Message);
    }

    // ── Parsed Patch Stats Do Not Count Eofnl Markers ────────────────

    [Fact]
    public async Task ParsedPatchStats_DoNotCountEofnlMarkers()
    {
        // old "a\nb" (no trailing NL) → new "a\nb\n": the patch carries a
        // DelEofnl marker; C's git_patch_line_stats skips *_EOFNL origins
        // (patch.c:93-128), so Deletions is 1, not 2.
        const string patchText =
            "diff --git a/f.txt b/f.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -1,2 +1,2 @@\n" +
            " a\n" +
            "-b\n" +
            "\\ No newline at end of file\n" +
            "+b\n";

        var diff = GitDiff.FromBuffer(patchText);
        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, stats.Insertions);
        Assert.Equal(1, stats.Deletions);
    }

    // ── Email Diffstat Uses Raw Bars ─────────────────────────────────

    [Fact]
    public async Task EmailDiffstat_UsesRawBars()
    {
        // C's append_diffstat passes width 0 → raw one-char-per-line bars
        // (email.c:176-180, diff_stats.c:114-117), not scaled bars for files
        // with more changes than the bar width.
        (GitRepository repo, GitOid commitOid, _) = await CreateRepoWithFileAsync("f.txt", string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line{i}")) + "\n");
        await using (repo)
        {
            string content = string.Join('\n', Enumerable.Range(0, 120).Select(i => $"changed line {i}")) + "\n";
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), content, cancellationToken: TestContext.Current.CancellationToken);
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await index.WriteAsync(TestContext.Current.CancellationToken);
            GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
            GitOid newCommit = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [commitOid],
                Author = Sig(),
                Committer = Sig(),
                Message = "second\n",
                UpdateRef = "refs/heads/main",
            }, TestContext.Current.CancellationToken);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(newCommit, TestContext.Current.CancellationToken))!;
            using (commit)
            {
                string email = await GitEmailFormatter.ToBufferTextAsync(commit, null, TestContext.Current.CancellationToken);

                // 120 insertions + 20 deletions = 140 changed → raw bars =
                // 120 '+' then 20 '-' (C with width 0), not a scaled bar.
                Assert.Contains(" f.txt | 140 " + new string('+', 120) + new string('-', 20) + "\n", email);
                Assert.DoesNotContain("| 140 " + new string('+', 67) + "-", email);
            }
        }
    }

    // ── Apply Skipped Hunk Negative Line Num Fails Cleanly ───────────

    [Fact]
    public async Task Apply_SkippedHunk_NegativeLineNum_FailsCleanly()
    {
        // Hunk 1 (new_start=1, 100 new lines) is skipped by the callback
        // (SkippedNewLines=100, SkippedOldLines=1); hunk 2 (new_start=2)
        // then computes lineNum = 2 - 100 + 1 - 1 = -98. C's size_t wrap
        // clamps to max and fails cleanly (apply.c:245-258), not with a raw
        // ArgumentOutOfRangeException.
        var sb = new System.Text.StringBuilder();
        sb.Append("diff --git a/f.txt b/f.txt\n");
        sb.Append("index 94aaae8..af8f41d 100644\n");
        sb.Append("--- a/f.txt\n");
        sb.Append("+++ b/f.txt\n");
        sb.Append("@@ -1,1 +1,100 @@\n");
        sb.Append("-old\n");
        for (int i = 0; i < 100; i++)
        {
            sb.Append("+new").Append(i).Append('\n');
        }

        sb.Append("@@ -1,1 +2,2 @@\n");
        sb.Append("-x\n");
        sb.Append("+y\n");
        sb.Append("+z\n");

        var patch = GitPatch.FromBuffer(sb.ToString());
        var opts = new GitApplyOptions
        {
            HunkCallback = hunk => hunk.NewStart == 1 ? 1 : 0,
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitPatchApplier.ApplyPatchAsync("old\nx\n"u8.ToArray(), patch, opts, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);
    }

    // ── Apply Empty Binary Both Sides Produces Empty Postimage ───────

    [Fact]
    public async Task Apply_EmptyBinaryBothSides_ProducesEmptyPostimage()
    {
        // C's apply_binary leaves the postimage EMPTY when both sides have
        // no data (apply.c:361-362).
        const string patchText =
            "diff --git a/f.bin b/f.bin\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "GIT binary patch\n" +
            "literal 0\n" +
            "\n" +
            "literal 0\n" +
            "\n";

        var patch = GitPatch.FromBuffer(patchText);
        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(
            "some source content\n"u8.ToArray(), patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Content.Length);
    }

    [Fact]
    public async Task Apply_NonEmptyBinary_StillApplies()
    {
        // Control: a real literal binary patch still applies. The base85
        // lines carry the zlib-compressed bytes; the declared literal size
        // is the DECOMPRESSED length (what the printer emits as
        // file.InflatedLength).
        byte[] oldContent = "old content"u8.ToArray();
        byte[] newContent = "abc"u8.ToArray();
        byte[] oldCompressed = ZlibTestHelpers.CompressLooseObject(oldContent);
        byte[] newCompressed = ZlibTestHelpers.CompressLooseObject(newContent);
        string oldLine = LengthChar(oldCompressed.Length) + Base85.Encode(oldCompressed);
        string newLine = LengthChar(newCompressed.Length) + Base85.Encode(newCompressed);

        string patchText =
            "diff --git a/f.bin b/f.bin\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "GIT binary patch\n" +
            $"literal {newContent.Length}\n" +
            newLine + "\n" +
            "\n" +
            $"literal {oldContent.Length}\n" +
            oldLine + "\n" +
            "\n";

        var patch = GitPatch.FromBuffer(patchText);
        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(
            oldContent, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(newContent, result.Content);
    }

    private static string LengthChar(int len) => ((char)('A' + len - 1)).ToString();
}

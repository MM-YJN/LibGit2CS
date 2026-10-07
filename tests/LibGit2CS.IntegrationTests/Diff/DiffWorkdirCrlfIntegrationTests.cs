using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// End-to-end integration tests for the workdir clean-filter fix in
/// <see cref="DiffFileContent.LoadWorkdirFileAsync"/>, exercising the
/// patch/stat pipeline against locally-initialized repos with
/// <c>.gitattributes</c> + <c>core.autocrlf</c>/<c>eol</c> configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The companion unit-test file
/// (<c>DiffWorkdirCleanFilterTests</c>) pins the focused behavior of the
/// workdir-content clean-filter fix with small fixtures. These integration
/// tests mirror the concrete reproduction — larger
/// attributed files where <c>git diff --numstat</c> reports a handful of
/// insertions/deletions rather than collapsing the entire file into one
/// spurious hunk. They verify numstat parity with reference
/// <c>git diff</c>, multi-hunk rendering for non-adjacent edits, and that
/// the clean-filter path does not perturb the fast-path (no attributes) case.
/// </para>
/// <para>
/// <b>Attribute-cache ordering.</b> <c>.gitattributes</c> is written FIRST,
/// before any index operation, to avoid the cache-staleness hazard
/// documented in <see cref="Filters.CrlfFilterIntegrationTests"/>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/diff/diff_workdir.c</c>
/// (<c>test_diff_workdir__with_attributes</c>,
/// <c>test_diff_workdir__numstat_with_crlf</c>), adapted to build the
/// sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class DiffWorkdirCrlfIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-diffwdcrlf-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a repo, writes <paramref name="gitattributes"/> to the workdir
    /// FIRST, commits the given files on <c>refs/heads/main</c>, and sets
    /// HEAD. Returns the commit OID. If <paramref name="coreAutoCrlf"/> is
    /// non-null, sets <c>core.autocrlf</c> before the commit.
    /// </summary>
    private static async Task<GitOid> InitRepoWithAttributesAsync(
        string path, string gitattributes, IReadOnlyDictionary<string, string> files,
        string? coreAutoCrlf, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        if (coreAutoCrlf is not null)
        {
            await repo.Config.SetStringAsync("core.autocrlf", coreAutoCrlf, ct).ConfigureAwait(false);
        }

        // .gitattributes FIRST.
        await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), gitattributes, ct).ConfigureAwait(false);

        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        foreach ((string p, string c) in files)
        {
            string full = Path.Combine(path, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        await index.AddByPathAsync(".gitattributes", ct).ConfigureAwait(false);
        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct).ConfigureAwait(false);
        await repo.SetHeadAsync("refs/heads/main", ct).ConfigureAwait(false);
        await repo.DisposeAsync().ConfigureAwait(false);
        return commitOid;
    }

    /// <summary>Builds an <c>N</c>-line LF-terminated string <c>line1\nline2\n…\n</c>.</summary>
    private static string BuildLfContent(int lineCount)
    {
        var sb = new StringBuilder(lineCount * 8);
        for (int i = 1; i <= lineCount; i++)
        {
            sb.Append("line").Append(i).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Converts every <c>\n</c> in <paramref name="lf"/> to <c>\r\n</c>.</summary>
    private static string ToCrlf(string lf)
    {
        var sb = new StringBuilder(lf.Length + 16);
        foreach (char c in lf)
        {
            if (c == '\n')
            {
                sb.Append('\r');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // ── numstat parity with reference git diff ─────────────────────────

    /// <summary>
    /// With <c>*.cs text eol=crlf</c>, the workdir holds CRLF and the
    /// index/blob holds LF. After committing a 40-line file and editing a
    /// single line in the workdir (still CRLF), an index-to-workdir diff's
    /// numstat must report <c>1 insertion, 1 deletion</c> — matching
    /// reference <c>git diff --numstat</c>. Loading the workdir content raw
    /// (CRLF) and comparing against the LF blob would collapse the whole file
    /// into one hunk and report <c>40 insertions, 40 deletions</c>.
    /// </summary>
    [Fact]
    public async Task EolCrlf_SingleLineEditOnLargeFile_NumstatMatchesGit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        const int lines = 40;
        string lfContent = BuildLfContent(lines);
        await InitRepoWithAttributesAsync(
            path,
            "*.cs text eol=crlf\n",
            new Dictionary<string, string> { ["app.cs"] = ToCrlf(lfContent) },
            coreAutoCrlf: null,
            ct).ConfigureAwait(false);
        try
        {
            // Edit line 20 in the workdir (still CRLF).
            var edit = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                edit.Append(i == 20 ? "line20-EDITED\r\n" : $"line{i}\r\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "app.cs"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);

            GitDiffStats stats = await diff.GetStatsAsync(ct);
            Assert.Equal(1, stats.Insertions);
            Assert.Equal(1, stats.Deletions);
            Assert.Equal(1, stats.FilesChanged);

            // Numstat format is left-justified fixed-width fields:
            // "1       0       app.cs" (or similar). Verify the small counts.
            using var statsWriter = new PooledByteBufferWriter();
            stats.Format(statsWriter, GitDiffStatsFormat.Number);
            string numstat = Encoding.UTF8.GetString(statsWriter.WrittenSpan);
            Assert.Contains("app.cs", numstat);
            Assert.StartsWith("1", numstat.TrimStart());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With <c>core.autocrlf=true</c> + <c>*.txt text</c>, the workdir
    /// holds CRLF (smudge wrote it) and the index/blob holds LF. A 30-line
    /// file with a 2-line edit must numstat-report <c>2 insertions,
    /// 2 deletions</c> — same parity guarantee as the previous test but driven by
    /// <c>core.autocrlf</c> rather than an explicit <c>eol</c> attribute.
    /// </summary>
    [Fact]
    public async Task AutoCrlfTrue_TextAttr_TwoLineEdit_NumstatTwoTwo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        const int lines = 30;
        string lfContent = BuildLfContent(lines);
        await InitRepoWithAttributesAsync(
            path,
            "*.txt text\n",
            new Dictionary<string, string> { ["notes.txt"] = ToCrlf(lfContent) },
            coreAutoCrlf: "true",
            ct).ConfigureAwait(false);
        try
        {
            // Edit lines 10 and 22 (still CRLF).
            var edit = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                edit.Append(i == 10 ? "line10-a\r\n" : i == 22 ? "line22-b\r\n" : $"line{i}\r\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "notes.txt"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);

            GitDiffStats stats = await diff.GetStatsAsync(ct);
            Assert.Equal(2, stats.Insertions);
            Assert.Equal(2, stats.Deletions);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── multiple hunks for non-adjacent edits ──────────────────────────

    /// <summary>
    /// With <c>*.txt text eol=crlf</c>, editing two non-adjacent lines
    /// (line 5 and line 45) in a 50-line CRLF workdir file must produce
    /// two separate hunks (default 3 context lines — the 40-line gap
    /// exceeds <c>2 * context + inter_hunk</c>), not a single mega-hunk.
    /// </summary>
    [Fact]
    public async Task EolCrlf_TwoNonAdjacentEdits_ProduceTwoHunks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        const int lines = 50;
        string lfContent = BuildLfContent(lines);
        await InitRepoWithAttributesAsync(
            path,
            "*.txt text eol=crlf\n",
            new Dictionary<string, string> { ["doc.txt"] = ToCrlf(lfContent) },
            coreAutoCrlf: null,
            ct).ConfigureAwait(false);
        try
        {
            var edit = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                edit.Append(i == 5 ? "line5-aaa\r\n" : i == 45 ? "line45-zzz\r\n" : $"line{i}\r\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "doc.txt"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            using GitPatch patch = await repo.PatchFromDiffAsync(diff, 0, ct);

            // Two separate hunks — the 40-line gap (>> 2*3 context lines)
            // forces a split (not 1 mega-hunk).
            Assert.Equal(2, await patch.GetHunkCountAsync(ct));

            (int context, int additions, int deletions) = await patch.LineStatsAsync(ct);
            Assert.Equal(2, additions);
            Assert.Equal(2, deletions);

            // Verify both edited lines appear as additions.
            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("+line5-aaa", patchText);
            Assert.Contains("+line45-zzz", patchText);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── adjacent edits coalesce into a single hunk ─────────────────────

    /// <summary>
    /// With <c>*.txt text eol=crlf</c>, editing two adjacent lines
    /// (line 10 and line 11) in a 20-line CRLF workdir file must produce a
    /// single hunk (the edits are close enough to coalesce within one
    /// context window). The hunk body should contain both additions and
    /// both deletions.
    /// </summary>
    [Fact]
    public async Task EolCrlf_TwoAdjacentEdits_CoalesceIntoSingleHunk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        const int lines = 20;
        string lfContent = BuildLfContent(lines);
        await InitRepoWithAttributesAsync(
            path,
            "*.txt text eol=crlf\n",
            new Dictionary<string, string> { ["small.txt"] = ToCrlf(lfContent) },
            coreAutoCrlf: null,
            ct).ConfigureAwait(false);
        try
        {
            var edit = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                edit.Append(i == 10 ? "line10-X\r\n" : i == 11 ? "line11-Y\r\n" : $"line{i}\r\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "small.txt"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            using GitPatch patch = await repo.PatchFromDiffAsync(diff, 0, ct);

            Assert.Equal(1, await patch.GetHunkCountAsync(ct));
            (int context, int additions, int deletions) = await patch.LineStatsAsync(ct);
            Assert.Equal(2, additions);
            Assert.Equal(2, deletions);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── fast-path regression (no attributes) ───────────────────────────

    /// <summary>
    /// With no <c>.gitattributes</c> and <c>core.autocrlf=false</c>,
    /// no clean filter applies, so the fast path returns raw workdir bytes.
    /// A single-line edit on a 25-line LF workdir file produces
    /// <c>1 insertion, 1 deletion</c> and a single hunk. Regression guard
    /// that the filter-load call doesn't perturb the no-filter case.
    /// </summary>
    [Fact]
    public async Task NoAttributes_LfWorkdirSingleEdit_NumstatOneOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        const int lines = 25;
        string lfContent = BuildLfContent(lines);
        await InitRepoWithAttributesAsync(
            path,
            gitattributes: string.Empty,
            new Dictionary<string, string> { ["plain.txt"] = lfContent },
            coreAutoCrlf: "false",
            ct).ConfigureAwait(false);
        try
        {
            // Edit line 13 (still LF).
            var edit = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                edit.Append(i == 13 ? "line13-changed\n" : $"line{i}\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "plain.txt"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);

            GitDiffStats stats = await diff.GetStatsAsync(ct);
            Assert.Equal(1, stats.Insertions);
            Assert.Equal(1, stats.Deletions);

            using GitPatch patch = await repo.PatchFromDiffAsync(diff, 0, ct);
            Assert.Equal(1, await patch.GetHunkCountAsync(ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── -text binary passthrough ───────────────────────────────────────

    /// <summary>
    /// With <c>*.dat -text</c> and <c>core.autocrlf=true</c>, the CRLF
    /// filter is disabled for matching paths; the workdir content is loaded
    /// raw. A single-line edit on a CRLF workdir file produces
    /// <c>1 insertion, 1 deletion</c> — both sides retain CRLF.
    /// Exercises the fast-path branch where <see cref="GitFilterList.LoadAsync"/>
    /// returns null.
    /// </summary>
    [Fact]
    public async Task MinusText_CrlfWorkdirSingleEdit_PassthroughNumstatOneOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();

        // Build CRLF content; with -text, the blob stores CRLF verbatim.
        var sb = new StringBuilder();
        for (int i = 1; i <= 10; i++)
        {
            sb.Append($"row{i}\r\n");
        }

        string crlfContent = sb.ToString();

        await InitRepoWithAttributesAsync(
            path,
            "*.dat -text\n",
            new Dictionary<string, string> { ["data.dat"] = crlfContent },
            coreAutoCrlf: "true",
            ct).ConfigureAwait(false);
        try
        {
            // Edit row 5 (still CRLF).
            var edit = new StringBuilder();
            for (int i = 1; i <= 10; i++)
            {
                edit.Append(i == 5 ? "row5-MOD\r\n" : $"row{i}\r\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "data.dat"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);

            GitDiffStats stats = await diff.GetStatsAsync(ct);
            Assert.Equal(1, stats.Insertions);
            Assert.Equal(1, stats.Deletions);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── patch text is parseable (round-trip) ──────────────────────────

    /// <summary>
    /// The rendered patch text from a CRLF-attributed workdir diff must
    /// be parseable by <see cref="GitPatch.FromBuffer(string, GitPatchParseOptions?)"/>
    /// and yield the same line stats as the live diff. Guards that the output
    /// is well-formed patch text (correct <c>@@</c> headers, body
    /// lines) rather than garbled output.
    /// </summary>
    [Fact]
    public async Task EolCrlf_RenderedPatchText_IsParseableAndMatchesStats()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        const int lines = 15;
        string lfContent = BuildLfContent(lines);
        await InitRepoWithAttributesAsync(
            path,
            "*.txt text eol=crlf\n",
            new Dictionary<string, string> { ["r.txt"] = ToCrlf(lfContent) },
            coreAutoCrlf: null,
            ct).ConfigureAwait(false);
        try
        {
            // Edit lines 4 and 12 (still CRLF).
            var edit = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                edit.Append(i == 4 ? "line4-new\r\n" : i == 12 ? "line12-new\r\n" : $"line{i}\r\n");
            }

            await File.WriteAllTextAsync(Path.Combine(path, "r.txt"), edit.ToString(), ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            (int liveContext, int liveAdd, int liveDel) = await (await repo.PatchFromDiffAsync(diff, 0, ct)).LineStatsAsync(ct);

            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            // Round-trip: parse the rendered patch text and verify line stats match.
            using var reparsed = GitPatch.FromBuffer(patchText);
            (int parsedContext, int parsedAdd, int parsedDel) = await reparsed.LineStatsAsync(ct);
            Assert.Equal(liveAdd, parsedAdd);
            Assert.Equal(liveDel, parsedDel);

            // Edits at lines 4 and 12 — with default 3-line context and 0
            // inter-hunk lines, the 1-line gap between the hunk regions
            // (line 8) does not coalesce: two hunks.
            Assert.Equal(2, await reparsed.GetHunkCountAsync(ct));
        }
        finally
        {
            Cleanup(path);
        }
    }
}

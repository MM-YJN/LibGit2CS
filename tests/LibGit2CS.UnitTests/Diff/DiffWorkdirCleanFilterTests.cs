using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Regression tests for the workdir clean-filter behavior in
/// <see cref="DiffFileContent.LoadWorkdirFileAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The managed port of
/// <c>diff_file_content_load_workdir_file</c> (diff_file.c:323-396) was
/// reading workdir bytes raw, skipping the <c>GIT_FILTER_TO_ODB</c> clean
/// filter pipeline that libgit2 applies before diffing workdir content
/// against an ODB/blob side. For any path whose attributes require
/// clean-side normalization (the canonical case being <c>text</c> +
/// <c>eol=crlf</c>, where the workdir holds CRLF and the blob holds LF),
/// every line hashed differently and the diff collapsed the entire file
/// into a single spurious hunk. <see cref="DiffFileContent.ComputeWorkdirOidAsync"/>
/// already applied the filter for OID detection; the content-rendering
/// path did not. These tests exercise the clean-filter path end-to-end through the
/// diff facade (<see cref="GitRepository.DiffIndexToWorkdirAsync"/> /
/// <see cref="GitRepository.DiffTreeToWorkdirAsync"/>) and pin the
/// fast-path (no attributes) behavior as a regression guard.
/// </para>
/// <para>
/// <b>Attribute-cache ordering.</b> <c>.gitattributes</c> is written FIRST,
/// before any index operation, to avoid the cache-staleness hazard
/// documented in <c>CrlfFilterIntegrationTests</c>: the attribute cache is
/// built lazily on the first attribute lookup (triggered during
/// <see cref="GitIndex.AddByPathAsync"/>) and never re-reads the file.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors the workdir-file diff scenarios
/// from <c>tests/libgit2/diff/diff_workdir.c</c>
/// (<c>test_diff_workdir__with_attributes</c>), adapted to build the
/// sandbox from scratch and assert line-stat parity with reference
/// <c>git diff</c>.
/// </para>
/// </remarks>
public sealed class DiffWorkdirCleanFilterTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public DiffWorkdirCleanFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WdFilter_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch (IOException) { }
    }

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// Writes <c>.gitattributes</c> FIRST, then writes <paramref name="path"/>
    /// with <paramref name="workdirContent"/>, stages both, and commits on
    /// <c>refs/heads/main</c>. The blob is stored after clean filtering
    /// (LF for <c>text</c> attributes); the workdir file retains
    /// <paramref name="workdirContent"/>. Optionally sets
    /// <c>core.autocrlf</c> before staging.
    /// </summary>
    private async Task CommitWithAttributesAsync(
        string gitattributes, string path, string workdirContent,
        string? coreAutoCrlf, CancellationToken ct)
    {
        if (coreAutoCrlf is not null)
        {
            await _repo.Config.SetStringAsync("core.autocrlf", coreAutoCrlf, ct).ConfigureAwait(false);
        }

        // .gitattributes FIRST (attribute-cache ordering).
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".gitattributes"), gitattributes, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, path), workdirContent, ct).ConfigureAwait(false);

        GitIndex index = await _repo.GetIndexAsync(ct).ConfigureAwait(false);
        await index.AddByPathAsync(".gitattributes", ct).ConfigureAwait(false);
        await index.AddByPathAsync(path, ct).ConfigureAwait(false);
        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct).ConfigureAwait(false);
        await _repo.SetHeadAsync("refs/heads/main", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the stored blob content for <paramref name="path"/> from the
    /// HEAD tree, returning it as a UTF-8 string.
    /// </summary>
    private async Task<string> ReadStoredBlobAsync(string path, CancellationToken ct)
    {
        GitReference headRef = (await _repo.ReferenceResolveAsync("HEAD", ct).ConfigureAwait(false))!;
        GitOid headId = ((GitDirectReference)headRef).Target;
        Commit head = (await _repo.ObjectLookupAsync<Commit>(headId, ct).ConfigureAwait(false))!;
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(head.Tree, ct).ConfigureAwait(false))!;
        GitTreeEntry? entry = tree[path];
        Assert.NotNull(entry);
        GitBlob blob = (await _repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct).ConfigureAwait(false))!;
        return Encoding.UTF8.GetString(blob.Content.Span);
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

    /// <summary>
    /// Counts the rendered <c>+</c>/<c>-</c> body lines (excluding the
    /// <c>+++</c>/<c>---</c> file headers) in a patch text.
    /// </summary>
    private static (int Added, int Removed) CountBodyAddRemove(string patchText)
    {
        int added = 0, removed = 0;
        foreach (string line in patchText.Split('\n'))
        {
            if (line.Length > 0 && line[0] == '+' && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                added++;
            }
            else if (line.Length > 0 && line[0] == '-' && !line.StartsWith("---", StringComparison.Ordinal))
            {
                removed++;
            }
        }

        return (added, removed);
    }

    // ── The core regression: eol=crlf + CRLF workdir + small edit ───────

    /// <summary>
    /// With <c>*.txt text eol=crlf</c>, the workdir holds CRLF and the index
    /// blob holds LF. After committing a 20-line file and editing one line
    /// in the workdir (still CRLF), an index-to-workdir diff must render a
    /// focused single-line hunk (1 add + 1 del) — not the whole file as one
    /// spurious hunk. Loading the workdir content raw (CRLF) and comparing
    /// against the LF blob would hash every line differently and make Xdiff
    /// produce a 20-line-add / 20-line-del mega-hunk.
    /// </summary>
    [Fact]
    public async Task EolCrlf_WorkdirCrlfWithSingleLineEdit_RendersFocusedHunk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string lfContent = BuildLfContent(lineCount: 20);
        // Stage from a CRLF workdir file: the clean filter stores LF in the blob;
        // the workdir retains CRLF (no smudge runs on commit).
        await CommitWithAttributesAsync(
            "*.txt text eol=crlf\n", "f.txt", ToCrlf(lfContent), coreAutoCrlf: null, ct).ConfigureAwait(false);

        // Sanity: the stored blob is LF-only.
        string stored = await ReadStoredBlobAsync("f.txt", ct).ConfigureAwait(false);
        Assert.Equal(lfContent, stored);
        Assert.DoesNotContain("\r", stored);

        // Edit one line in the workdir (still CRLF line endings).
        var edit = new StringBuilder();
        for (int i = 1; i <= 20; i++)
        {
            edit.Append(i == 10 ? "line10-changed\r\n" : $"line{i}\r\n");
        }

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "f.txt"), edit.ToString(), ct).ConfigureAwait(false);

        using GitDiff diff = await _repo.DiffIndexToWorkdirAsync(cancellationToken: ct).ConfigureAwait(false);
        Assert.Equal(1, diff.DeltaCount);
        Assert.Equal(GitDeltaStatus.Modified, diff.GetDelta(0).Status);

        using GitPatch patch = await _repo.PatchFromDiffAsync(diff, 0, ct).ConfigureAwait(false);
        (int context, int additions, int deletions) = await patch.LineStatsAsync(ct).ConfigureAwait(false);
        Assert.Equal(1, additions);
        Assert.Equal(1, deletions);
        Assert.True(context >= 2);

        // Exactly one hunk for a single-line edit.
        Assert.Equal(1, await patch.GetHunkCountAsync(ct).ConfigureAwait(false));

        // The patch body must contain only the one real edit — no spurious
        // whole-file rewrite. Verify the changed line is present and a far-
        // away unchanged line is NOT in the patch body.
        string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct).ConfigureAwait(false);
        (int bodyAdd, int bodyDel) = CountBodyAddRemove(patchText);
        Assert.Equal(1, bodyAdd);
        Assert.Equal(1, bodyDel);
        Assert.Contains("+line10-changed", patchText);
        Assert.Contains("-line10\n", patchText);
        // Far-away unchanged lines must not appear as +/- body lines.
        Assert.DoesNotContain("+line1\n", patchText);
        Assert.DoesNotContain("-line1\n", patchText);
        Assert.DoesNotContain("+line20\n", patchText);
        Assert.DoesNotContain("-line20\n", patchText);
    }

    // ── Fast-path regression: no attributes → raw bytes used ───────────

    /// <summary>
    /// With no <c>.gitattributes</c> and <c>core.autocrlf=false</c>, no
    /// clean filter applies, so the fast path returns raw workdir bytes.
    /// A single-line edit on an LF workdir file produces 1 add + 1 del.
    /// Regression guard that the filter-load call doesn't perturb the
    /// no-filter case.
    /// </summary>
    [Fact]
    public async Task NoAttributes_LfWorkdirWithSingleLineEdit_ProducesOneAddOneDel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string lfContent = BuildLfContent(lineCount: 10);
        // No .gitattributes, no autocrlf: blob stores LF (raw); workdir retains LF.
        await CommitWithAttributesAsync(
            /* gitattributes: */ string.Empty, "f.txt", lfContent, coreAutoCrlf: "false", ct).ConfigureAwait(false);

        // Edit one line (still LF).
        var edit = new StringBuilder();
        for (int i = 1; i <= 10; i++)
        {
            edit.Append(i == 5 ? "line5-edit\n" : $"line{i}\n");
        }

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "f.txt"), edit.ToString(), ct).ConfigureAwait(false);

        using GitDiff diff = await _repo.DiffIndexToWorkdirAsync(cancellationToken: ct).ConfigureAwait(false);
        Assert.Equal(1, diff.DeltaCount);

        using GitPatch patch = await _repo.PatchFromDiffAsync(diff, 0, ct).ConfigureAwait(false);
        (int context, int additions, int deletions) = await patch.LineStatsAsync(ct).ConfigureAwait(false);
        Assert.Equal(1, additions);
        Assert.Equal(1, deletions);
        Assert.Equal(1, await patch.GetHunkCountAsync(ct).ConfigureAwait(false));
    }

    // ── core.autocrlf=input + text attribute ───────────────────────────

    /// <summary>
    /// With <c>core.autocrlf=input</c> and a <c>text</c> attribute, the
    /// clean direction still converts CRLF&rarr;LF (input = clean only;
    /// smudge is a no-op). A CRLF workdir file with a single-line edit
    /// must render a focused hunk — same parity guarantee as
    /// <see cref="EolCrlf_WorkdirCrlfWithSingleLineEdit_RendersFocusedHunk"/>.
    /// </summary>
    [Fact]
    public async Task AutoCrlfInput_TextAttr_CrlfWorkdirWithEdit_RendersFocusedHunk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string lfContent = BuildLfContent(lineCount: 15);
        await CommitWithAttributesAsync(
            "*.txt text\n", "f.txt", ToCrlf(lfContent), coreAutoCrlf: "input", ct).ConfigureAwait(false);

        // Verify the blob stored LF (clean filter stripped CRLF).
        string stored = await ReadStoredBlobAsync("f.txt", ct).ConfigureAwait(false);
        Assert.DoesNotContain("\r", stored);

        // Edit line 7 in the workdir (still CRLF).
        var edit = new StringBuilder();
        for (int i = 1; i <= 15; i++)
        {
            edit.Append(i == 7 ? "line7-modified\r\n" : $"line{i}\r\n");
        }

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "f.txt"), edit.ToString(), ct).ConfigureAwait(false);

        using GitDiff diff = await _repo.DiffIndexToWorkdirAsync(cancellationToken: ct).ConfigureAwait(false);
        Assert.Equal(1, diff.DeltaCount);

        using GitPatch patch = await _repo.PatchFromDiffAsync(diff, 0, ct).ConfigureAwait(false);
        (int context, int additions, int deletions) = await patch.LineStatsAsync(ct).ConfigureAwait(false);
        Assert.Equal(1, additions);
        Assert.Equal(1, deletions);
        Assert.Equal(1, await patch.GetHunkCountAsync(ct).ConfigureAwait(false));

        string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct).ConfigureAwait(false);
        Assert.Contains("+line7-modified", patchText);
    }

    // ── Pure CRLF↔LF change with no real edit → no delta ───────────────

    /// <summary>
    /// With <c>*.txt text eol=crlf</c>, after staging from a CRLF workdir
    /// file (blob stores LF), re-writing the workdir file with <em>the
    /// same CRLF content</em> must produce no diff. The workdir iterator's
    /// OID computation (<see cref="DiffFileContent.ComputeWorkdirOidAsync"/>)
    /// applies the clean filter, so the normalized hash matches the blob
    /// and the delta is downgraded to Unmodified. This holds both before
    /// and after the workdir-content-load fix and is pinned here as a
    /// guard against future regressions in either path.
    /// </summary>
    [Fact]
    public async Task EolCrlf_WorkdirCrlfNoEdit_NoDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string lfContent = BuildLfContent(lineCount: 8);
        string crlfContent = ToCrlf(lfContent);
        await CommitWithAttributesAsync(
            "*.txt text eol=crlf\n", "f.txt", crlfContent, coreAutoCrlf: null, ct).ConfigureAwait(false);

        // Re-write identical CRLF content (no real edit).
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "f.txt"), crlfContent, ct).ConfigureAwait(false);

        using GitDiff diff = await _repo.DiffIndexToWorkdirAsync(cancellationToken: ct).ConfigureAwait(false);
        Assert.Equal(0, diff.DeltaCount);
    }

    // ── -text attribute: raw passthrough, no normalization ─────────────

    /// <summary>
    /// With <c>*.bin -text</c>, no CRLF filter applies, so the workdir
    /// content is loaded raw. A single-line edit on a CRLF workdir file
    /// produces 1 add + 1 del — both sides retain CRLF. The fast
    /// path (<c>ApplyCleanAsync</c> returns input when
    /// <see cref="GitFilterList.LoadAsync"/> returns null) must keep this
    /// behavior intact.
    /// </summary>
    [Fact]
    public async Task MinusTextAttr_CrlfWorkdirWithEdit_PassthroughFocusedHunk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Build CRLF content; with -text, the blob stores CRLF verbatim.
        var sb = new StringBuilder();
        for (int i = 1; i <= 6; i++)
        {
            sb.Append($"line{i}\r\n");
        }

        string crlfContent = sb.ToString();

        await CommitWithAttributesAsync(
            "*.bin -text\n", "data.bin", crlfContent, coreAutoCrlf: "true", ct).ConfigureAwait(false);

        // Verify the blob kept CRLF (no filtering despite autocrlf=true).
        string stored = await ReadStoredBlobAsync("data.bin", ct).ConfigureAwait(false);
        Assert.Contains("\r\n", stored);

        // Edit line 3 (still CRLF).
        var edit = new StringBuilder();
        for (int i = 1; i <= 6; i++)
        {
            edit.Append(i == 3 ? "line3-CHANGED\r\n" : $"line{i}\r\n");
        }

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "data.bin"), edit.ToString(), ct).ConfigureAwait(false);

        using GitDiff diff = await _repo.DiffIndexToWorkdirAsync(cancellationToken: ct).ConfigureAwait(false);
        Assert.Equal(1, diff.DeltaCount);

        using GitPatch patch = await _repo.PatchFromDiffAsync(diff, 0, ct).ConfigureAwait(false);
        (int context, int additions, int deletions) = await patch.LineStatsAsync(ct).ConfigureAwait(false);
        Assert.Equal(1, additions);
        Assert.Equal(1, deletions);
    }

    // ── Why the tests above use DiffIndexToWorkdirAsync ─────────────────
    //
    // The workdir-content clean filter lives in the index-to-workdir
    // leg (DiffGenerator.IndexToWorkdirAsync → DiffFileContent.LoadWorkdirAsync).
    // DiffTreeToWorkdirAsync uses a direct workdir iterator (matching C's
    // git_diff_tree_to_workdir, diff_generate.c:1508), so it exercises the
    // same LoadWorkdirAsync path; the index-to-workdir tests above are the
    // minimal, focused regression guard for the clean-filter behavior.
}

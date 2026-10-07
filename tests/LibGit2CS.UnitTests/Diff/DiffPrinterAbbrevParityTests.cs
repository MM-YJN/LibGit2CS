using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Regression tests for the diff-printer id_abbrev parity behaviors: the
/// <c>id_abbrev == 0</c> → <c>core.abbrev</c> resolution
/// (diff_print_info_init__common, diff_print.c:52-63 +
/// git_repository__abbrev_length, repository.c:3999-4019), the raw format's
/// mode-gated per-file guard and "..." abbreviation
/// (diff_print_one_raw, diff_print.c:231-257), and the patch header's
/// GIT_ERROR_PATCH guards (diff_print_oid_range, diff_print.c:294-307).
/// Expectations are C-verified against libgit2 1.9.4.
/// </summary>
public sealed class DiffPrinterAbbrevParityTests
{
    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    /// <summary>
    /// A parsed patch whose index line carries only 4 hex characters per OID.
    /// The hunk body's final line must be newline-terminated
    /// (parse_hunk_body, patch_parse.c:587-590).
    /// </summary>
    private const string ShortOidPatchText = """
        diff --git a/file.txt b/file.txt
        index 1111..2222 100644
        --- a/file.txt
        +++ b/file.txt
        @@ -1 +1 @@
        -a
        +b
        """ + "\n";

    private static async Task<string> RenderAsync(GitDiff diff, GitDiffPrintFormat format)
    {
        var sb = new StringBuilder();
        await diff.PrintAsync(format, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        }, TestContext.Current.CancellationToken);
        return sb.ToString();
    }

    /// <summary>Builds two trees, both containing "a.txt" with different content.</summary>
    private static async Task<(GitTree tree1, GitTree tree2, GitOid blob1, GitOid blob2)> TwoTreesAsync(GitRepository repo)
    {
        GitOid blob1 = await repo.ObjectWriteAsync(GitObjectType.Blob, "one\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blob2 = await repo.ObjectWriteAsync(GitObjectType.Blob, "two\n"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder b1 = repo.NewTreeBuilder();
        await b1.InsertAsync("a.txt", blob1, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid t1 = await b1.WriteAsync(CancellationToken.None);

        using GitTreeBuilder b2 = repo.NewTreeBuilder();
        await b2.InsertAsync("a.txt", blob2, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid t2 = await b2.WriteAsync(CancellationToken.None);

        GitTree tree1 = (await repo.ObjectLookupAsync<GitTree>(t1, TestContext.Current.CancellationToken))!;
        GitTree tree2 = (await repo.ObjectLookupAsync<GitTree>(t2, TestContext.Current.CancellationToken))!;
        return (tree1, tree2, blob1, blob2);
    }

    private static async Task<(GitRepository repo, string path)> InitRepoAsync()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffAbbrev_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    // ── id_abbrev == 0 → core.abbrev resolution ─────────────────────────

    [Fact]
    public async Task Patch_IdAbbrevZero_ResolvesCoreAbbrevFromConfig()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            (GitTree tree1, GitTree tree2, GitOid blob1, GitOid blob2) = await TwoTreesAsync(repo);
            await repo.Config.SetStringAsync("core.abbrev", "9", TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(tree1, tree2, new GitDiffOptions { IdAbbrevLength = 0 }, TestContext.Current.CancellationToken);
            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);

            // C: 0 → git_repository__abbrev_length → 9 characters.
            Assert.Contains($"index {blob1.ToString()[..9]}..{blob2.ToString()[..9]} 100644\n", output, StringComparison.Ordinal);
        }
        finally
        {
            await repo.DisposeAsync();
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Patch_IdAbbrevZero_CoreAbbrevFalse_PrintsFullOids()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            (GitTree tree1, GitTree tree2, GitOid blob1, GitOid blob2) = await TwoTreesAsync(repo);
            await repo.Config.SetStringAsync("core.abbrev", "false", TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(tree1, tree2, new GitDiffOptions { IdAbbrevLength = 0 }, TestContext.Current.CancellationToken);
            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);

            // C: GIT_ABBREV_FALSE clamps to the full OID hex size.
            Assert.Contains($"index {blob1}..{blob2} 100644\n", output, StringComparison.Ordinal);
        }
        finally
        {
            await repo.DisposeAsync();
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Patch_IdAbbrevZero_CoreAbbrevBelowMinimum_ThrowsConfigError()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            (GitTree tree1, GitTree tree2, _, _) = await TwoTreesAsync(repo);
            await repo.Config.SetStringAsync("core.abbrev", "2", TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(tree1, tree2, new GitDiffOptions { IdAbbrevLength = 0 }, TestContext.Current.CancellationToken);

            // C (repository.c:4008-4011): len < GIT_ABBREV_MINIMUM →
            // "invalid oid abbreviation setting: '2'", GIT_ERROR_CONFIG, -1.
            GitException ex = await Assert.ThrowsAsync<GitException>(() => RenderAsync(diff, GitDiffPrintFormat.Patch));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Config, ex.Category);
            Assert.Equal("invalid oid abbreviation setting: '2'", ex.Message);
        }
        finally
        {
            await repo.DisposeAsync();
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task PatchHeader_IdAbbrevZero_WithoutRepo_FallsBackToSeven()
    {
        // C (diff_print.c:55-57): repo == NULL → GIT_ABBREV_DEFAULT (7).
        var diff = GitDiff.Buffers("old\n"u8.ToArray(), "new\n"u8.ToArray(), new GitDiffOptions { IdAbbrevLength = 0 });
        string output = await RenderAsync(diff, GitDiffPrintFormat.PatchHeader);

        // The buffer diff OIDs are full hashes; the index line must use 7 hex
        // characters on both sides.
        Assert.Matches("^diff --git a/file b/file\nindex [0-9a-f]{7}\\.\\.[0-9a-f]{7} 100644\n", output);
    }

    // ── Raw format: configured id_abbrev + "..." + guard ─────────────────

    [Fact]
    public async Task Raw_UsesConfiguredIdAbbrevWithEllipsis()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            (GitTree tree1, GitTree tree2, GitOid blob1, GitOid blob2) = await TwoTreesAsync(repo);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(tree1, tree2, new GitDiffOptions { IdAbbrevLength = 12 }, TestContext.Current.CancellationToken);
            string output = await RenderAsync(diff, GitDiffPrintFormat.Raw);

            // C (diff_print.c:248-255): "%s... %s..." with id_strlen chars.
            Assert.Equal($":100644 100644 {blob1.ToString()[..12]}... {blob2.ToString()[..12]}... M\ta.txt\n", output);
        }
        finally
        {
            await repo.DisposeAsync();
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Raw_ParsedPatchWithShortOids_ThrowsPatchError()
    {
        // C (diff_print.c:231-239): pi->id_strlen (7) > the parsed patch's
        // per-file id_abbrev (4) → "the patch input contains 4 id characters
        // (cannot print 7)", GIT_ERROR_PATCH.
        using var diff = GitDiff.FromBuffer(ShortOidPatchText);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => RenderAsync(diff, GitDiffPrintFormat.Raw));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Patch, ex.Category);
        Assert.Equal("the patch input contains 4 id characters (cannot print 7)", ex.Message);
    }

    [Fact]
    public async Task Patch_ParsedPatchWithShortOids_ThrowsPatchError()
    {
        // C (diff_print.c:294-307, diff_print_oid_range): the patch-format
        // index line has the same guard as the raw format.
        using var diff = GitDiff.FromBuffer(ShortOidPatchText);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => RenderAsync(diff, GitDiffPrintFormat.Patch));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Patch, ex.Category);
        Assert.Equal("the patch input contains 4 id characters (cannot print 7)", ex.Message);
    }
}

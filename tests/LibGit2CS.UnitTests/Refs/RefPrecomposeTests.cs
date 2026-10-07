using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

/// <summary> Ref-name precompose tests. Pins the <see cref="GitReferenceFormatFlags.PrecomposeUnicode"/> flag (ports the internal
/// <c>GIT_REFERENCE_FORMAT__PRECOMPOSE_UNICODE</c> at refs.h:56) and the <see cref="GitReferences.NormalizeNameForRepoAsync"/> helper (ports
/// <c>reference_normalize_for_repo</c> at refs.c:201-218). </summary> <remarks> This test project runs with <c>InvariantGlobalization=true</c>, so the actual
/// NFD-&gt;NFC byte conversion inside <see cref="PathPrecompose.PrecomposeCore"/> is a no-op in-process (see <see cref="PathPrecomposeTests"/> for details).
/// The tests here therefore pin the flag plumbing and graceful-degradation behavior (input preserved when normalization is unavailable); the real transcode is
/// exercised only in production. </remarks>
public sealed class RefPrecomposeTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    private static bool NormalizationAvailable
        => "\u0041\u0308".Normalize(NormalizationForm.FormC) == "\u00C4";

    public RefPrecomposeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefPrecompose_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, _context);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void NormalizeName_PrecomposeFlag_AcceptedForValidAsciiName()
    {
        // The PrecomposeUnicode flag must not interfere with ASCII ref names.
        // ASCII short-circuits inside PrecomposeCore, so the normalized output
        // equals the plain normalization.
        string? plain = GitReferences.NormalizeName(
            "refs/heads/main", GitReferenceFormatFlags.Normal);
        string? withFlag = GitReferences.NormalizeName(
            "refs/heads/main", GitReferenceFormatFlags.PrecomposeUnicode);

        Assert.Equal("refs/heads/main", plain);
        Assert.Equal(plain, withFlag);
    }

    [Fact]
    public void NormalizeName_PrecomposeFlag_NfdFollowsGlobalizationMode()
    {
        // A ref name with a decomposed (NFD) segment. With full globalization
        // (production), the flag folds it to NFC before validation. Under
        // invariant globalization (test process), the fold is a no-op and the
        // NFD form is validated as-is.
        string nfdName = "refs/heads/A\u030Astro\u0308m";
        string nfcName = "refs/heads/Åström";

        string? result = GitReferences.NormalizeName(nfdName, GitReferenceFormatFlags.PrecomposeUnicode);

        if (NormalizationAvailable)
        {
            Assert.Equal(nfcName, result);
        }
        else
        {
            // Invariant mode: PrecomposeCore is a no-op, so the NFD input is
            // normalized as-is (the combining chars are valid ref-name chars).
            Assert.Equal(nfdName, result);
        }
    }

    [Fact]
    public void NormalizeName_PrecomposeFlag_InvalidNameStillRejected()
    {
        // The flag does not bypass validation: a name with an illegal char
        // (or space) is still rejected regardless of precompose.
        string? result = GitReferences.NormalizeName(
            "refs/heads/feat: test", GitReferenceFormatFlags.PrecomposeUnicode);
        Assert.Null(result);
    }

    [Fact]
    public async Task NormalizeNameForRepoAsync_ConfigFalse_DoesNotFoldNfd()
    {
        // core.precomposeunicode absent (defaults to false per
        // GIT_PRECOMPOSE_DEFAULT) → the flag is NOT set → NFD input is
        // returned as-is regardless of globalization mode.
        string nfdName = "refs/heads/A\u030Astro\u0308m";
        string? result = await GitReferences.NormalizeNameForRepoAsync(
            _repo, nfdName, validate: true, TestContext.Current.CancellationToken);

        Assert.Equal(nfdName, result);
    }

    [Fact]
    public async Task NormalizeNameForRepoAsync_ConfigTrue_FollowsGlobalizationMode()
    {
        // core.precomposeunicode=true → the flag IS set → behavior depends on
        // globalization mode (fold in production, no-op in test process).
        await _repo.Config.SetBoolAsync(
            "core.precomposeunicode", true, TestContext.Current.CancellationToken);

        string nfdName = "refs/heads/A\u030Astro\u0308m";
        string nfcName = "refs/heads/Åström";
        string? result = await GitReferences.NormalizeNameForRepoAsync(
            _repo, nfdName, validate: true, TestContext.Current.CancellationToken);

        if (NormalizationAvailable)
        {
            Assert.Equal(nfcName, result);
        }
        else
        {
            Assert.Equal(nfdName, result);
        }
    }

    [Fact]
    public async Task NormalizeNameForRepoAsync_NoValidation_PassesThrough()
    {
        // validate=false mirrors GIT_REFERENCE_FORMAT__VALIDATION_DISABLE: the
        // iconv output is returned without the validation loop. For an ASCII
        // name the result equals the input either way.
        string? result = await GitReferences.NormalizeNameForRepoAsync(
            _repo, "refs/heads/main", validate: false, TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/main", result);
    }
}

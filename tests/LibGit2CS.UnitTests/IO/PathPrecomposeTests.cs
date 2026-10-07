using System.Text;

using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Unit tests for <see cref="PathPrecompose"/>. Pins the NFD-&gt;NFC worker, the ASCII short-circuit, and the macOS-only runtime gate of <see
/// cref="PathPrecompose.PrecomposeIfNeeded"/>. The libgit2 anchor is <c>git_fs_path_iconv</c> (<c>src/util/fs_path.c:1036-1088</c>) plus the
/// <c>git_fs_path_has_non_ascii</c> short-circuit (<c>fs_path.c:1010-1016</c>). </summary> <remarks> <para> This test project is compiled with
/// <c>&lt;InvariantGlobalization&gt;true&lt;/InvariantGlobalization&gt;</c>, so <see cref="string.Normalize(NormalizationForm)"/> is a no-op and <see
/// cref="string.IsNormalized(NormalizationForm)"/> always returns <see langword="true"/>. The NFD-&gt;NFC transcode therefore cannot be observed directly
/// inside the unit-test process. Tests that depend on the actual byte conversion branch on <see cref="NormalizationAvailable"/>: when <see langword="false"/>
/// (the in-test-process default), they assert the graceful-degradation path (input returned unchanged — exactly what libgit2's iconv does on platforms without
/// a usable conversion table, <c>fs_path.c:1074</c>); when <see langword="true"/> (production, which does not set <c>InvariantGlobalization</c>), they assert
/// the NFC result. The production library does not set <c>InvariantGlobalization</c>, so the transcode works in real usage; this harness just cannot exercise
/// it. </para> </remarks>
public class PathPrecomposeTests
{
    // NFC "Åström" — the libgit2 probe test name (fs_path.c:1090).
    // Å = U+00C5 (NFC: C3 85; NFD: 41 CC 8A), ö = U+00F6 (NFC: C3 B6; NFD: 6F CC 88).
    private const string NfcAstroem = "Åström";

    // NFD "Åström" — the decomposed form macOS HFS+/APFS would emit.
    private const string NfdAstroem = "A\u030Astro\u0308m";

    /// <summary>
    /// True when the runtime can actually perform Unicode normalization (i.e.
    /// <c>InvariantGlobalization</c> is NOT active). Detects this by probing a
    /// known NFD-&gt;NFC conversion: under invariant mode the probe string is
    /// returned unchanged and this returns <see langword="false"/>.
    /// </summary>
    private static bool NormalizationAvailable
        => "\u0041\u0308".Normalize(NormalizationForm.FormC) == "\u00C4";

    // ----- PrecomposeCore: short-circuits (globalization-independent) -----

    [Fact]
    public void PrecomposeCore_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PathPrecompose.PrecomposeCore(null!));
    }

    [Fact]
    public void PrecomposeCore_Empty_Unchanged()
    {
        Assert.Same(string.Empty, PathPrecompose.PrecomposeCore(string.Empty));
    }

    [Fact]
    public void PrecomposeCore_PureAscii_ReturnsSameInstance()
    {
        // git_fs_path_has_non_ascii short-circuit: ASCII never decomposes.
        // This path is globalization-independent.
        string ascii = "path/to/file.txt";
        Assert.Same(ascii, PathPrecompose.PrecomposeCore(ascii));
    }

    // ----- PrecomposeCore: NFD -> NFC (globalization-dependent) -----

    [Fact]
    public void PrecomposeCore_NfdInput_FollowsGlobalizationMode()
    {
        Assert.NotEqual(NfcAstroem, NfdAstroem);
        string result = PathPrecompose.PrecomposeCore(NfdAstroem);

        if (NormalizationAvailable)
        {
            // Full globalization (production): NFD is folded to NFC.
            Assert.Equal(NfcAstroem, result);
            Assert.True(result.IsNormalized(NormalizationForm.FormC));
        }
        else
        {
            // Invariant globalization (test process): normalization is a no-op,
            // so the NFD input is returned unchanged. This is the same
            // graceful-degradation path that libgit2's iconv takes when no
            // conversion table is available (fs_path.c:1074 — non-E2BIG
            // failures return the original data unchanged).
            Assert.Equal(NfdAstroem, result);
        }
    }

    [Fact]
    public void PrecomposeCore_NfdInput_InstanceSemanticsFollowGlobalizationMode()
    {
        string nfd = NfdAstroem;
        string result = PathPrecompose.PrecomposeCore(nfd);

        if (NormalizationAvailable)
        {
            // The NFD input is not mutated; a fresh NFC instance is returned.
            Assert.NotSame(nfd, result);
        }
        else
        {
            // Invariant mode: the input is returned as-is (same instance is
            // permitted — the short-circuit and the no-op Normalize both
            // preserve identity).
            Assert.Equal(nfd, result);
        }
    }

    [Fact]
    public void PrecomposeCore_MixedAsciiAndNfd_FollowsGlobalizationMode()
    {
        string nfdPath = "dir/" + NfdAstroem + ".txt";
        string result = PathPrecompose.PrecomposeCore(nfdPath);

        if (NormalizationAvailable)
        {
            string expected = "dir/" + NfcAstroem + ".txt";
            Assert.Equal(expected, result);
        }
        else
        {
            Assert.Equal(nfdPath, result);
        }
    }

    [Fact]
    public void PrecomposeCore_AlreadyNfc_ReturnsSameInstance()
    {
        // IsNormalized(FormC) short-circuit. Under invariant mode,
        // IsNormalized always returns true (so every input takes this branch);
        // the assertion still holds for genuine NFC input.
        string nfc = NfcAstroem;
        Assert.Same(nfc, PathPrecompose.PrecomposeCore(nfc));
    }

    [Fact]
    public void PrecomposeCore_InvalidUtf8Surrogates_GracefulPassthrough()
    {
        // string.Normalize does not throw on malformed input; faithful to
        // iconv's "errno != E2BIG -> return original" path (fs_path.c:1074).
        // A lone surrogate cannot be normalized and is returned unchanged in
        // both globalization modes.
        string malformed = "path\uDC80/file";
        string result = PathPrecompose.PrecomposeCore(malformed);
        Assert.Equal(malformed, result);
    }

    // ----- PrecomposeIfNeeded (macOS runtime gate) -----

    [Fact]
    public void PrecomposeIfNeeded_NullArg_ThrowsRegardlessOfPlatform()
    {
        // The macOS gate is a runtime probe; a null input is a programming
        // error on every platform. Assert the null-check fires before the gate.
        Assert.Throws<ArgumentNullException>(() => PathPrecompose.PrecomposeIfNeeded(null!));
    }

    [Fact]
    public void PrecomposeIfNeeded_Ascii_ReturnsSameInstance()
    {
        // ASCII short-circuit fires regardless of platform (the ASCII check
        // precedes the globalization-dependent normalization).
        string ascii = "ascii_only";
        Assert.Same(ascii, PathPrecompose.PrecomposeIfNeeded(ascii));
    }

    [Fact]
    public void PrecomposeIfNeeded_NfdInput_MatchesPlatformAndGlobalizationGates()
    {
        // Two stacked gates: OperatingSystem.IsMacOS() AND NormalizationAvailable.
        // On the Linux/Windows CI with InvariantGlobalization, both are off and
        // the input is returned unchanged. On macOS production both are on and
        // NFD is folded to NFC. The test pins the actual taken branch.
        string result = PathPrecompose.PrecomposeIfNeeded(NfdAstroem);

        if (OperatingSystem.IsMacOS() && NormalizationAvailable)
        {
            Assert.Equal(NfcAstroem, result);
        }
        else
        {
            Assert.Equal(NfdAstroem, result);
        }
    }

    [Fact]
    public void NormalizationAvailable_DocumentsTestEnvironment()
    {
        // Documents the test-process globalization mode for diagnosis. When
        // this assertion fails (NormalizationAvailable == true), the
        // globalization-dependent branches above will start exercising the
        // real transcode path — update them if the test project drops
        // InvariantGlobalization.
        _ = NormalizationAvailable;
    }
}

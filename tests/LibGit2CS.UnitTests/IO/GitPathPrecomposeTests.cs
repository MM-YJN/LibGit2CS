using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Unit tests for the <see cref="GitPath.FromFileSystemString(string, bool)"/> ingress overload. Pins that the <paramref name="precompose"/> flag
/// routes the input through <see cref="PathPrecompose.PrecomposeCore"/> (NFD-&gt;NFC) before UTF-8 encoding, and that the flag-disabled path preserves the
/// original bytes byte-for-byte. </summary> <remarks> This test project runs with <c>InvariantGlobalization=true</c>, so the actual NFD-&gt;NFC byte conversion
/// is a no-op in-process (see <see cref="PathPrecomposeTests"/> for details). The precompose=true branches therefore assert the graceful-degradation path (NFD
/// preserved) in this process; they exercise the real transcode only in production (where the library runs without <c>InvariantGlobalization</c>). </remarks>
public class GitPathPrecomposeTests
{
    // NFC "Åström": C3 85 73 74 72 C3 B6 6D.
    private static readonly byte[] s_nfcAstroemBytes = [0xC3, 0x85, 0x73, 0x74, 0x72, 0xC3, 0xB6, 0x6D];

    // NFD "Åström": 41 CC 8A 73 74 72 6F CC 88 6D.
    private static readonly byte[] s_nfdAstroemBytes = [0x41, 0xCC, 0x8A, 0x73, 0x74, 0x72, 0x6F, 0xCC, 0x88, 0x6D];

    private static string NfdAstroem => "A\u030Astro\u0308m";

    private static string NfcAstroem => "Åström";

    /// <summary>
    /// True when the runtime can actually perform Unicode normalization. See
    /// <see cref="PathPrecomposeTests"/> for the rationale.
    /// </summary>
    private static bool NormalizationAvailable
        => "\u0041\u0308".Normalize(System.Text.NormalizationForm.FormC) == "\u00C4";

    [Fact]
    public void FromFileSystemString_PrecomposeFalse_PreservesNfdBytes()
    {
        var p = GitPath.FromFileSystemString(NfdAstroem, precompose: false);
        Assert.Equal(s_nfdAstroemBytes, p.Span.ToArray());
    }

    [Fact]
    public void FromFileSystemString_PrecomposeTrue_FollowsGlobalizationMode()
    {
        var p = GitPath.FromFileSystemString(NfdAstroem, precompose: true);

        if (NormalizationAvailable)
        {
            // Production (non-invariant globalization): NFD is folded to NFC
            // before UTF-8 encoding.
            Assert.Equal(s_nfcAstroemBytes, p.Span.ToArray());
        }
        else
        {
            // Test process (InvariantGlobalization=true): Normalize is a no-op,
            // so the original NFD bytes are preserved. This is the graceful
            // degradation that matches iconv's passthrough-on-failure behavior.
            Assert.Equal(s_nfdAstroemBytes, p.Span.ToArray());
        }
    }

    [Fact]
    public void FromFileSystemString_PrecomposeTrue_NfcInputUnchanged()
    {
        // Already-NFC input short-circuits inside PrecomposeCore; the resulting
        // bytes must equal the NFC encoding (and the NFD encoding would differ).
        var p = GitPath.FromFileSystemString(NfcAstroem, precompose: true);
        Assert.Equal(s_nfcAstroemBytes, p.Span.ToArray());
    }

    [Fact]
    public void FromFileSystemString_PrecomposeTrue_AsciiShortCircuits()
    {
        // ASCII input never needs transcode; the result equals the plain UTF-8
        // encoding regardless of the precompose flag.
        var precomposed = GitPath.FromFileSystemString("ascii.txt", precompose: true);
        var plain = GitPath.FromFileSystemString("ascii.txt", precompose: false);
        Assert.Equal(plain, precomposed);
    }

    [Fact]
    public void FromFileSystemString_PrecomposeTrue_NfcEquivalenceFollowsGlobalizationMode()
    {
        // The motivating use case: an NFC tree/index entry must match an NFD workdir file once the iterator precomposes on ingress. A GitPath
        // built from the NFC string and a GitPath built from the NFD string with precompose=true must be byte-equal WHEN the runtime can normalize. Under
        // invariant globalization the transcode is a no-op and the two remain distinct (the index/tree side and the workdir side would diverge — precisely the
        // situation core.precomposeunicode exists to resolve on macOS).
        var fromNfc = GitPath.FromUtf8String(NfcAstroem);
        var fromNfd = GitPath.FromFileSystemString(NfdAstroem, precompose: true);

        if (NormalizationAvailable)
        {
            Assert.Equal(fromNfc, fromNfd);
        }
        else
        {
            Assert.NotEqual(fromNfc, fromNfd);
        }
    }

    [Fact]
    public void FromFileSystemString_ParameterlessOverload_IsNoOpAlias()
    {
        // The parameterless overload preserves the original bytes (kept for
        // non-iterator callers).
        var p = GitPath.FromFileSystemString(NfdAstroem);
        Assert.Equal(s_nfdAstroemBytes, p.Span.ToArray());
    }
}

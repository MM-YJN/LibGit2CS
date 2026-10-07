using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Tests for the byte-based <see cref="WildMatch"/>
/// overload. These pin the ASCII-only fold parity fix (non-ASCII bytes do
/// NOT fold under <c>WM_CASEFOLD</c>, matching libgit2's <c>dowild</c> on
/// <c>unsigned char</c>) and the byte-faithful round-trip for non-UTF-8
/// patterns/paths. The <c>ReadOnlySpan&lt;char&gt;</c> overload
/// (<see cref="WildMatchTests"/>) stays for non-path callers (refspecs,
/// branch globs, config includeIf).
/// </summary>
public class WildMatchByteTests
{
    // ASCII fold: [A-Z] glob matches [a-z] under WM_CASEFOLD.
    [Fact]
    public void AsciiFold_LowerMatchesUpper()
    {
        ReadOnlySpan<byte> pattern = "[A-Z]"u8;
        ReadOnlySpan<byte> text = "a"u8;
        Assert.True(WildMatch.IsMatch(pattern, text, WildMatchFlags.CaseInsensitive));
    }

    // ASCII-fold parity fix: non-ASCII bytes do NOT fold under WM_CASEFOLD.
    // Ä = 0xC3 0x84, ä = 0xC3 0xA4. Byte 1 (0x84 vs 0xA4) is outside A-Z,
    // so the fold leaves both unchanged and they do not match.
    [Fact]
    public void NonAsciiBytes_DoNotFoldUnderCaseInsensitive()
    {
        ReadOnlySpan<byte> pattern = [0xC3, 0x84]; // Ä
        ReadOnlySpan<byte> text = [0xC3, 0xA4]; // ä
        Assert.False(WildMatch.IsMatch(pattern, text, WildMatchFlags.CaseInsensitive));
    }

    // Same byte sequence matches itself byte-exact (case-sensitive).
    [Fact]
    public void NonAsciiBytes_MatchSelfByteExact()
    {
        ReadOnlySpan<byte> pattern = [0xC3, 0xA4]; // ä
        ReadOnlySpan<byte> text = [0xC3, 0xA4];
        Assert.True(WildMatch.IsMatch(pattern, text, WildMatchFlags.None));
    }

    // Invalid-UTF-8 path round-trips through wildmatch byte-exact: a glob
    // of 0xFF 0xFE 0x80 matches a text of the same bytes. A
    // char overload would U+FFFD-replace the invalid bytes and corrupt both
    // sides, producing spurious matches or misses.
    [Fact]
    public void NonUtf8Bytes_MatchSelfByteExact()
    {
        ReadOnlySpan<byte> path = [0xFF, 0xFE, 0x80];
        Assert.True(WildMatch.IsMatch(path, path, WildMatchFlags.None));
    }

    // Star matches across non-UTF-8 bytes (byte-domain, not char-domain).
    [Fact]
    public void Star_MatchesAcrossNonUtf8Bytes()
    {
        ReadOnlySpan<byte> pattern = "*.txt"u8;
        ReadOnlySpan<byte> text = [0xFF, 0xFE, 0x80, .. ".txt"u8];
        Assert.True(WildMatch.IsMatch(pattern, text, WildMatchFlags.None));
    }

    // Star+Pathname does not cross '/' even with non-UTF-8 prefix.
    [Fact]
    public void StarPathname_DoesNotCrossSlash_AcrossNonUtf8()
    {
        ReadOnlySpan<byte> pattern = "*.txt"u8;
        ReadOnlySpan<byte> text = [0xFF, 0xFE, 0x80, .. "/a.txt"u8];
        Assert.False(WildMatch.IsMatch(pattern, text, WildMatchFlags.Pathname));
    }

    // Double-star crosses slash with non-UTF-8 segments.
    [Fact]
    public void DoubleStar_Pathname_CrossesSlash_WithNonUtf8()
    {
        ReadOnlySpan<byte> pattern = "**/a.txt"u8;
        ReadOnlySpan<byte> text = [0xFF, 0xFE, 0x80, .. "/a.txt"u8];
        Assert.True(WildMatch.IsMatch(pattern, text, WildMatchFlags.Pathname));
    }

    // Byte-charset matches a byte-range. [0xFF] matches the byte 0xFF.
    // Uses explicit byte arrays because \xFF is invalid in a u8 string literal.
    [Fact]
    public void ByteCharSet_MatchesByte()
    {
        ReadOnlySpan<byte> pattern = [(byte)'[', 0xFF, (byte)']'];
        ReadOnlySpan<byte> text = [0xFF];
        Assert.True(WildMatch.IsMatch(pattern, text, WildMatchFlags.None));
    }

    // Byte-charset negation: [!0xFF] does NOT match 0xFF.
    [Fact]
    public void ByteCharSet_NegationDoesNotMatch()
    {
        ReadOnlySpan<byte> pattern = [(byte)'[', (byte)'!', 0xFF, (byte)']'];
        ReadOnlySpan<byte> text = [0xFF];
        Assert.False(WildMatch.IsMatch(pattern, text, WildMatchFlags.None));
    }

    // Char-domain and byte-domain agree on ASCII (parity with the char overload).
    [Fact]
    public void ByteOverload_AgreesWithCharOverload_OnAscii()
    {
        foreach ((string pat, string txt, bool pathname, bool icase, bool want) in Cases())
        {
            WildMatchFlags flags = (pathname ? WildMatchFlags.Pathname : WildMatchFlags.None)
                | (icase ? WildMatchFlags.CaseInsensitive : WildMatchFlags.None);
            bool charResult = WildMatch.IsMatch(pat, txt, flags);
            bool byteResult = WildMatch.IsMatch(
                System.Text.Encoding.UTF8.GetBytes(pat).AsSpan(),
                System.Text.Encoding.UTF8.GetBytes(txt).AsSpan(),
                flags);
            Assert.Equal(want, charResult);
            Assert.Equal(want, byteResult);
        }
    }

    private static IEnumerable<(string, string, bool, bool, bool)> Cases()
    {
        yield return ("foo", "foo", false, false, true);
        yield return ("foo", "bar", false, false, false);
        yield return ("f*", "foo", false, false, true);
        yield return ("f*", "foo/bar", true, false, false);
        yield return ("f*/*", "foo/bar", true, false, true);
        yield return ("**", "foo/bar/baz", true, false, true);
        yield return ("FOO", "foo", false, true, true);
        yield return ("[a-z]", "M", false, true, true);
        yield return ("[!a-z]", "5", false, false, true);
    }
}

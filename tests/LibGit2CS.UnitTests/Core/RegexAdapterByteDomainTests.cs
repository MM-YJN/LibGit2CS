using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

// The regex byte domain. RegexAdapter maps input bytes to chars via the byte↔char bijection (one char per byte), so '.', character
// classes, anchors and match offsets behave byte-exactly like C's git_regexp over char*; Compile UTF-8-encodes the pattern string into the same domain (the
// byte-domain contract). These tests verify: byte-offset identity, '.'-counts-bytes over invalid sequences,
// Unicode-literal-pattern-over-UTF-8-content parity, ASCII invariance, and the two documented divergences (\xHH raw-byte reading, IgnoreCase fold).
public sealed class RegexAdapterByteDomainTests
{
    [Fact]
    public void Search_ReturnsByteOffsetsByIdentity()
    {
        // "d\xFFf" — invalid UTF-8 in the middle; '.' must consume the FF
        // byte as exactly one unit and offsets must index the input bytes.
        byte[] input = [0x64, 0xFF, 0x66]; // d FF f
        using var regex = RegexAdapter.Compile("^(d.f)");
        Span<RegexMatch> matches = stackalloc RegexMatch[2];

        Assert.True(regex.Search(input, matches));
        Assert.False(matches[0].IsUnset);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(3, matches[0].End);
        Assert.False(matches[1].IsUnset);
        Assert.Equal(0, matches[1].Start);
        Assert.Equal(3, matches[1].End);
    }

    [Fact]
    public void Dot_CountsBytes_OverInvalidSequences()
    {
        // Four raw bytes, one of which is invalid UTF-8: '^....$' matches
        // (four bytes = four chars in the bijection domain).
        byte[] fourBytes = [0x64, 0x65, 0xFF, 0x66];
        using var four = RegexAdapter.Compile("^....$");
        Assert.True(four.IsMatch(fourBytes));

        // "中文" is 6 UTF-8 bytes: '^....$' must NOT match (C counts bytes).
        byte[] sixBytes = "中文"u8.ToArray();
        Assert.False(four.IsMatch(sixBytes));

        // Six dots match the six bytes.
        using var six = RegexAdapter.Compile("^......$");
        Assert.True(six.IsMatch(sixBytes));
    }

    [Fact]
    public void UnicodeLiteralPattern_MatchesUtf8Content()
    {
        // the byte-domain contract: the pattern string is UTF-8-encoded into the byte domain, so a literal 'é' (U+00E9) becomes bytes C3 A9 and matches UTF-8
        // content bytes C3 A9 — exactly as C's pattern bytes would.
        using var regex = RegexAdapter.Compile("café");
        byte[] content = "café"u8.ToArray(); // 63 61 66 C3 A9
        Assert.True(regex.IsMatch(content));

        // Also matches within a longer buffer, with byte offsets.
        byte[] buffer = "order café now"u8.ToArray();
        Span<RegexMatch> matches = stackalloc RegexMatch[1];
        Assert.True(regex.Search(buffer, matches));
        Assert.Equal(6, matches[0].Start);
        Assert.Equal(11, matches[0].End);
    }

    [Fact]
    public void AsciiPatterns_AreByteIdentical()
    {
        // The 99% case: ASCII patterns are unchanged by the UTF-8 encode and
        // the byte/string overloads agree on ASCII input.
        using var anchor = RegexAdapter.Compile("^$");
        Assert.True(anchor.IsMatch([]));
        Assert.False(anchor.IsMatch([0x61]));

        using var klass = RegexAdapter.Compile("^[A-Za-z_][A-Za-z0-9_]*$");
        Assert.True(klass.IsMatch("_foo2"u8.ToArray()));
        Assert.False(klass.IsMatch("2foo"u8.ToArray()));

        using var reflog = RegexAdapter.Compile("checkout: moving from (.*) to .*");
        byte[] msg = "checkout: moving from main to feature"u8.ToArray();
        Span<RegexMatch> matches = stackalloc RegexMatch[2];
        Assert.True(reflog.Search(msg, matches));
        Assert.Equal(22, matches[1].Start);
        Assert.Equal(26, matches[1].End);
    }

    [Fact]
    public void StringAndByteOverloads_AgreeOnAsciiInput()
    {
        using var regex = RegexAdapter.Compile("^(def)");
        Span<RegexMatch> strMatches = stackalloc RegexMatch[2];
        Span<RegexMatch> byteMatches = stackalloc RegexMatch[2];

        Assert.True(regex.Search("define x", strMatches));
        Assert.True(regex.Search("define x"u8.ToArray(), byteMatches));
        Assert.Equal(strMatches[0], byteMatches[0]);
        Assert.Equal(strMatches[1], byteMatches[1]);
    }

    [Fact]
    public void RawByteEscapePattern_Diverges_Documented()
    {
        // Documented divergence (the byte-domain contract): a pattern whose chars encode to the raw byte E9 (e.g. the single char U+00E9) does NOT match a lone
        // raw byte E9 — the pattern is UTF-8-encoded, so it matches the two-byte UTF-8 form C3 A9 instead. C's pattern bytes "E9" would match the raw byte; the
        // port's Unicode-literal reading is the locked choice.
        using var regex = RegexAdapter.Compile("\u00E9"); // 'é' → bytes C3 A9
        Assert.False(regex.IsMatch([0xE9]));
        Assert.True(regex.IsMatch([0xC3, 0xA9]));
    }

    [Fact]
    public void IgnoreCase_FoldDomain_Pinned()
    {
        // Pre-existing engine divergence (documented): .NET's IgnoreCase folds
        // a different domain than C's locale-ASCII-only fold. Pin the current
        // behavior so an engine change trips review. The pattern 'café' is
        // UTF-8-encoded to bytes C3 A9; the input "CAFÉ" is bytes 43 41 46 C3
        // 89 — the C3 byte is not a letter in the bijection domain, so the
        // fold cannot cross it (C's ASCII-only fold behaves the same way).
        using var regex = RegexAdapter.Compile("café", RegexFlags.IgnoreCase);
        Assert.False(regex.IsMatch("CAFÉ"u8.ToArray()));
        Assert.True(regex.IsMatch("café"u8.ToArray()));
        Assert.True(regex.IsMatch("CAFé"u8.ToArray()));
    }

    [Fact]
    public void UnsetGroups_OverByteOverload_AreUnset()
    {
        // shape over the byte domain: non-participating groups are
        // (-1,-1)/IsUnset, and the full match is used when group 1 is unset.
        using var regex = RegexAdapter.Compile("^def(ine)?");
        Span<RegexMatch> matches = stackalloc RegexMatch[2];

        Assert.True(regex.Search("define"u8.ToArray(), matches));
        Assert.False(matches[1].IsUnset);
        Assert.Equal(3, matches[1].Start);
        Assert.Equal(6, matches[1].End);

        Assert.True(regex.Search("def"u8.ToArray(), matches));
        Assert.True(matches[1].IsUnset);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(3, matches[0].End);
    }

    [Fact]
    public void IsMatchBijection_UserCompiledRegex_MatchesRawBytes()
    {
        // Config multivar parity surface: a user-compiled Regex (not
        // pattern-transformed) matches the bijection-decoded bytes — byte E9
        // is char U+00E9, so the \xE9 pattern matches it, exactly as before
        //.
        var userRegex = new System.Text.RegularExpressions.Regex("\\xE9");
        Assert.True(RegexAdapter.IsMatchBijection(userRegex, [0xE9]));
        Assert.False(RegexAdapter.IsMatchBijection(userRegex, [0xC3, 0xA9]));
    }

    [Fact]
    public void IsMatchBijection_StackAndRentedPaths_Agree()
    {
        // Exercise both the stackalloc (≤ 256) and ArrayPool paths.
        var userRegex = new System.Text.RegularExpressions.Regex("^a.*z$");
        byte[] shortInput = "abz"u8.ToArray();
        byte[] longInput = new byte[300];
        longInput[0] = (byte)'a';
        longInput[^1] = (byte)'z';
        for (int i = 1; i < longInput.Length - 1; i++)
        {
            longInput[i] = (byte)'b';
        }

        Assert.True(RegexAdapter.IsMatchBijection(userRegex, shortInput));
        Assert.True(RegexAdapter.IsMatchBijection(userRegex, longInput));
        Assert.False(RegexAdapter.IsMatchBijection(userRegex, "nope"u8.ToArray()));
    }
}

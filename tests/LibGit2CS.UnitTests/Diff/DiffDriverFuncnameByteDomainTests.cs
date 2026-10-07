using LibGit2CS.Core;
using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

// Funcname side: the diff-driver pattern extractor runs the regex over the raw line bytes — RegexAdapter.Search returns byte
// offsets by identity, so the extracted funcname Range slices the line directly. This pins the regression a char→byte GetByteCount back-mapping would
// reintroduce (multi-byte UTF-8 before the match would shift the slice), and the C parity of the trailing-rtrim-only match region (diff_driver.c:455-462:
// consume → truncate → git_str_rtrim).
public sealed class DiffDriverFuncnameByteDomainTests
{
    [Fact]
    public void PatternExtractor_ReturnsByteOffsetsByIdentity()
    {
        // The line has a multi-byte UTF-8 prefix BEFORE the capture: the old
        // code decoded the line to chars, matched, then back-mapped char→byte
        // with GetByteCount — a wrong offset there would now slice the wrong
        // bytes. The byte overload needs no mapping at all. (.+) is used
        // instead of \w: in the bijection domain the C3 byte is the char 'Ã'
        // (a letter — \w would match it) while A9 is '©' (not — \w would
        // stop), so a word-class capture would split the é.
        var driver = new DiffDriver("test")
        {
            Type = DiffDriverType.PatternList,
            FnPatterns =
            [
                new DiffDriverPattern(RegexAdapter.Compile("^public void (.+)\\("), negate: false),
            ],
        };

        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = driver.GetFunctionNameExtractor();
        byte[] line = "public void caf\u00e9M()"u8.ToArray(); // é = 2 bytes at 15-16

        (bool isMatch, Range range) = extractor(line);
        Assert.True(isMatch);

        // The capture "caféM" spans bytes 12..18 (é is C3 A9 — two bytes).
        ReadOnlySpan<byte> name = line.AsSpan(range);
        Assert.Equal("caf\u00e9M"u8.ToArray(), name.ToArray());
        Assert.Equal(12, range.Start.Value);
        Assert.Equal(18, range.End.Value);
    }

    [Fact]
    public void PatternExtractor_TrailingWhitespace_IsRtrimmedOnly()
    {
        // C (diff_driver.c:455-462) consumes to the match start, truncates to
        // the match end, then git_str_rtrim — the region's own trailing
        // whitespace is trimmed, nothing else.
        var driver = new DiffDriver("test")
        {
            Type = DiffDriverType.PatternList,
            FnPatterns =
            [
                new DiffDriverPattern(RegexAdapter.Compile("^static (.+):$"), negate: false),
            ],
        };

        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = driver.GetFunctionNameExtractor();
        byte[] line = "static init   :   "u8.ToArray();

        (bool isMatch, Range range) = extractor(line);
        Assert.True(isMatch);

        // The greedy capture is "init   " (the ':' at index 14 is consumed by
        // the pattern's literal), and the region's trailing spaces are
        // rtrimmed away — matching C's consume→truncate→rtrim.
        ReadOnlySpan<byte> name = line.AsSpan(range);
        Assert.Equal("init"u8.ToArray(), name.ToArray());
        Assert.Equal(7, range.Start.Value);
        Assert.Equal(11, range.End.Value);
    }

    [Fact]
    public void SimpleExtractor_IsByteSafe()
    {
        // The simple (non-pattern) extractor checks the first byte; a
        // high-byte first byte (invalid UTF-8) must not match as a letter.
        var driver = new DiffDriver("test") { Type = DiffDriverType.Auto };

        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = driver.GetFunctionNameExtractor();

        Assert.True(extractor("_private_fn()"u8.ToArray()).IsMatch);
        Assert.True(extractor("$dollar_fn()"u8.ToArray()).IsMatch);
        Assert.True(extractor("public_fn()"u8.ToArray()).IsMatch);
        Assert.False(extractor([0xFF, 0xFE]).IsMatch);
        Assert.False(extractor("   indented"u8.ToArray()).IsMatch);
        Assert.False(extractor([]).IsMatch);
    }

    [Fact]
    public void PatternExtractor_UnicodeLiteralPattern_MatchesUtf8Line()
    {
        // the byte-domain contract end-to-end at the driver level: a pattern containing a literal 'é' (UTF-8-encoded at Compile) matches a line whose bytes
        // contain C3 A9 — exactly as C's pattern bytes would.
        var driver = new DiffDriver("test")
        {
            Type = DiffDriverType.PatternList,
            FnPatterns =
            [
                new DiffDriverPattern(RegexAdapter.Compile("^caf\u00e9"), negate: false),
            ],
        };

        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = driver.GetFunctionNameExtractor();

        (bool isMatch, Range range) = extractor("caf\u00e9 bar"u8.ToArray());
        Assert.True(isMatch);
        Assert.Equal("caf\u00e9"u8.ToArray(), "caf\u00e9 bar"u8.ToArray().AsSpan(range).ToArray());
        Assert.Equal(5, range.End.Value); // é consumed its two bytes
    }

    [Fact]
    public void PatternExtractor_NegatedPattern_ReturnsNoMatch()
    {
        // A lone negated pattern never yields a match (C's
        // diff_context_line__pattern_match returns false when a pattern
        // matches but is negated, and the loop continues to the next
        // pattern). The cpp builtin pairs a negated keyword pattern with a
        // positive fallback.
        var driver = new DiffDriver("test")
        {
            Type = DiffDriverType.PatternList,
            FnPatterns =
            [
                new DiffDriverPattern(RegexAdapter.Compile("^\\s*(do|for|if|else|return|switch|while)"), negate: true),
                new DiffDriverPattern(RegexAdapter.Compile("^((::)?[A-Za-z_].*)$"), negate: false),
            ],
        };

        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = driver.GetFunctionNameExtractor();

        Assert.False(extractor("for (;;) {}"u8.ToArray()).IsMatch);
        Assert.True(extractor("int main() {}"u8.ToArray()).IsMatch);
    }
}

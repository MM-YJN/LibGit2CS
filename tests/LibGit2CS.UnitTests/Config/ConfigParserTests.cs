using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

public class ConfigParserTests
{
    [Fact]
    public async Task SectionName_IsLowercased()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[Core]\n\tValue = 42\n");
        Assert.Single(entries);
        Assert.Equal("core.value", entries[0].name);
    }

    [Fact]
    public async Task QuotedSubsection_PreservesCase()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[remote \"Origin\"]\n\turl = git://example.com\n");
        Assert.Single(entries);
        Assert.Equal("remote.Origin.url", entries[0].name);
    }

    [Fact]
    public async Task DottedSubsection_IsCaseInsensitive()
    {
        // [branch.SuBsection] — section lowercased, subsection lowercased (dotted form)
        List<(string? section, string name, string? value)> entries = await Parse("[branch.master]\n\tmerge = refs/heads/main\n");
        Assert.Single(entries);
        Assert.Equal("branch.master.merge", entries[0].name);
    }

    [Fact]
    public async Task SectionHeader_WithLeadingAsciiWhitespace_Parses()
    {
        List<(string? section, string name, string? value)> entries = await Parse(" \t[core]\n\tkey = value\n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
    }

    [Fact]
    public async Task InvalidSectionNameCharacter_Throws()
    {
        await Assert.ThrowsAsync<GitException>(() => Parse("[co_re]\n\tkey = value\n"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\f")]
    [InlineData("\v")]
    public async Task QuotedSubsection_AcceptsAsciiWhitespaceBeforeQuote(string whitespace)
    {
        List<(string? section, string name, string? value)> entries = await Parse($"[remote{whitespace}\"Origin\"]\n\turl = example\n");
        Assert.Single(entries);
        Assert.Equal("remote.Origin.url", entries[0].name);
    }

    [Fact]
    public async Task QuotedSubsection_DottedMixedCaseBase_NormalizesOnlyBase()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[Foo.Bar \"Baz\"]\n\tkey = value\n");
        Assert.Single(entries);
        Assert.Equal("foo.bar.Baz.key", entries[0].name);
    }

    [Fact]
    public async Task LoneVariable_HasNullValue()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tauto\n");
        Assert.Single(entries);
        Assert.Equal("core.auto", entries[0].name);
        Assert.Null(entries[0].value);
    }

    [Fact]
    public async Task EmptyValue_Parses()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = \n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("", entries[0].value);
    }

    [Fact]
    public async Task MultilineContinuation_JoinsValues()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = one \\\n two \\\n three\n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("one  two  three", entries[0].value);
    }

    [Fact]
    public async Task MultilineAtEof_TrimsTrailingBackslash()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = value \\\n");
        Assert.Single(entries);
        Assert.Equal("value ", entries[0].value);
    }

    [Fact]
    public async Task EscapedChars_InValue_Unescape()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = a\\tb\\nc\n");
        Assert.Single(entries);
        Assert.Equal("a\tb\nc", entries[0].value);
    }

    [Fact]
    public async Task CommentLine_IsIgnored()
    {
        List<(string? section, string name, string? value)> entries = await Parse("# a comment\n; another\n[core]\n\tkey = val\n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
    }

    [Fact]
    public async Task InlineComment_Stripped()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = value ; comment\n");
        Assert.Single(entries);
        Assert.Equal("value", entries[0].value);
    }

    [Fact]
    public async Task InlineComment_InQuotes_NotStripped()
    {
        // Semicolon inside quotes is NOT treated as a comment.
        // Note: the parser strips quote chars from values (matching libgit2's
        // unescape_line which only counts quotes, doesn't preserve them).
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = \"a;b\"\n");
        Assert.Single(entries);
        Assert.Equal("a;b", entries[0].value);
    }

    [Fact]
    public async Task EmptyFile_Parses()
    {
        List<(string? section, string name, string? value)> entries = await Parse("");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Bom_Skipped()
    {
        // The parser now operates on raw bytes; a UTF-8 BOM is the byte
        // sequence EF BB BF (not the U+FEFF char). Latin-1 encoding of U+FEFF
        // would produce '?' (0x3F), which is not a BOM and not a valid name char.
        byte[] content = [0xEF, 0xBB, 0xBF, .. "[core]\n\tkey = val\n"u8];
        List<(string? section, string name, string? value)> entries = await ParseBytes(content);
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
    }

    [Fact]
    public async Task MultipleSections_ParsesAll()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\ta = 1\n[remote \"origin\"]\n\turl = x\n");
        Assert.Equal(2, entries.Count);
        Assert.Equal("core.a", entries[0].name);
        Assert.Equal("remote.origin.url", entries[1].name);
    }

    [Fact]
    public async Task SectionHeaderOnLastLine_NoVariables_Ok()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task NoWhitespaceAroundEquals_Ok()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey=val\n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("val", entries[0].value);
    }

    [Fact]
    public async Task MissingClosingBracket_Throws()
    {
        await Assert.ThrowsAsync<GitException>(() => Parse("[core\n"));
    }

    [Fact]
    public async Task MissingQuoteInSubsection_Throws()
    {
        await Assert.ThrowsAsync<GitException>(() => Parse("[remote \"origin]\n"));
    }

    [Fact]
    public async Task InvalidEscape_Throws()
    {
        await Assert.ThrowsAsync<GitException>(() => Parse("[core]\n\tkey = a\\zb\n"));
    }

    [Fact]
    public async Task InvalidKeyChar_Throws()
    {
        await Assert.ThrowsAsync<GitException>(() => Parse("[core]\n\t_key = val\n"));
    }

    [Fact]
    public async Task VariableBeforeSection_HasNullSection()
    {
        List<(string? section, string name, string? value)> entries = await Parse("key = val\n[core]\n\tx = y\n");
        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].section);
        Assert.Equal("key", entries[0].name);
        Assert.Equal("core", entries[1].section);
    }

    [Fact]
    public async Task DuplicateVariable_BothParses()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey = first\n\tkey = second\n");
        Assert.Equal(2, entries.Count);
        Assert.Equal("first", entries[0].value);
        Assert.Equal("second", entries[1].value);
    }

    [Fact]
    public async Task CrlfLineEndings_Parses()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[core]\r\n\tkey = val\r\n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("val", entries[0].value);
    }

    // ── non-ASCII whitespace is NOT a separator ───────────

    [Fact]
    public async Task NonAsciiWhitespace_IsNotASeparator()
    {
        // C's git__isspace (ctype_compat.h:43-47) is ASCII-only. A key line
        // containing U+00A0 (non-breaking space) is NOT valid whitespace, so
        // C's parse_name stops at it and errors "invalid configuration key".
        // char.IsWhiteSpace would skip it and parse the line successfully.
        await Assert.ThrowsAsync<GitException>(() => Parse("[core]\n\tkey\u00A0=\u00A0value\n"));
    }

    [Fact]
    public async Task AsciiWhitespace_IsASeparator()
    {
        // All ASCII whitespace forms (space, tab, FF, VT) separate key/value.
        List<(string? section, string name, string? value)> entries = await Parse("[core]\n\tkey\t=\tvalue\n");
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("value", entries[0].value);
    }

    // ── subsection header with text after closing quote ────

    [Fact]
    public async Task SubsectionHeader_TextAfterClosingQuote_Throws()
    {
        // `[sec "a"b"]` — C's parser stops at the first closing quote, sees
        // 'b' after it, and raises "unexpected text after closing quotes"
        // (config_parse.c:143-147).
        // and accept the section.
        GitException ex = await Assert.ThrowsAsync<GitException>(() => Parse("[sec \"a\"b\"]\n\tkey = val\n"));
        Assert.Contains("unexpected text after closing quotes", ex.Message);
    }

    [Fact]
    public async Task SubsectionHeader_ValidQuotedSection_Parses()
    {
        List<(string? section, string name, string? value)> entries = await Parse("[sec \"a\"]\n\tkey = val\n");
        Assert.Single(entries);
        Assert.Equal("sec.a.key", entries[0].name);
    }

    // ── parse-error message wrapper (config_parse.c:15-25) ──

    [Fact]
    public async Task ParseError_ColumnZero_OmitsColumnPart()
    {
        // C: set_parse_error appends ", column %d" ONLY when col != 0.
        GitException ex = await Assert.ThrowsAsync<GitException>(() => Parse("[core\n"));
        Assert.Equal(
            "failed to parse config file: missing ']' in section header (in test:1)",
            ex.Message);
    }

    [Fact]
    public async Task ParseError_ColumnNonZero_IncludesColumnPart()
    {
        GitException ex = await Assert.ThrowsAsync<GitException>(() => Parse("[sec \"a\"b\"]\n\tkey = val\n"));
        Assert.StartsWith("failed to parse config file: unexpected text after closing quotes (in test:1, column ", ex.Message);
    }

    [Fact]
    public async Task InvalidConfigurationKey_IsWrapped_WithNoColumn()
    {
        // C (config_parse.c:312-315): parse_name errors use col 0, so the
        // message is wrapped but has no column part.
        GitException ex = await Assert.ThrowsAsync<GitException>(() => Parse("[core]\n\t=value\n"));
        Assert.Equal(
            "failed to parse config file: invalid configuration key (in test:2)",
            ex.Message);
    }

    [Fact]
    public async Task InvalidEscape_MessageMatchesC()
    {
        // C (config_parse.c:402-406): "invalid escape at %s" — NOT wrapped,
        // and %s prints the remainder of the line from the offending char.
        GitException ex = await Assert.ThrowsAsync<GitException>(() => Parse("[core]\n\tkey = val\\x01z\n"));
        Assert.Equal("invalid escape at x01z", ex.Message);
    }

    // ── Byte-preservation: invalid-UTF-8 bytes round-trip unchanged ──

    [Fact]
    public async Task RawE9Byte_InValue_Preserved()
    {
        // 0xE9 (é) is invalid standalone UTF-8; the byte-preserving parser keeps
        // it as a single raw byte instead of producing U+FFFD. Built from
        // explicit bytes — a string literal would UTF-8-encode the char.
        byte[] content = [.. "[core]\n\tkey = caf"u8, 0xE9, .. "\n"u8];
        Assert.Equal((byte)0xE9, content[^2]);
        List<(string? section, string name, string? value)> entries = await ParseBytes(content);
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("caf\uFFFD", entries[0].value);
    }

    [Fact]
    public async Task RawE9Byte_InQuotedSubsection_Preserved()
    {
        // A quoted subsection with a raw non-UTF-8 byte preserves case AND the
        // raw byte.
        byte[] content = [.. "[remote \"caf"u8, 0xE9, .. "\"]\n\turl = x\n"u8];
        Assert.Contains((byte)0xE9, content);
        List<(string? section, string name, string? value)> entries = await ParseBytes(content);
        Assert.Single(entries);
        Assert.Equal("remote.caf\uFFFD.url", entries[0].name);
    }

    [Fact]
    public async Task RawBomBytes_Skipped()
    {
        // The parser strips a leading UTF-8 BOM (EF BB BF) from the byte stream.
        // This complements Bom_Skipped by asserting via the raw-bytes helper.
        byte[] content = [0xEF, 0xBB, 0xBF, .. "[core]\n\tkey = val\n"u8];
        List<(string? section, string name, string? value)> entries = await ParseBytes(content);
        Assert.Single(entries);
        Assert.Equal("core.key", entries[0].name);
        Assert.Equal("val", entries[0].value);
    }

    [Fact]
    public async Task MultilineContinuation_RawE9Byte_Preserved()
    {
        // Continuation lines (trailing backslash) preserve raw bytes.
        byte[] content = [.. "[core]\n\tkey = one \\\n caf"u8, 0xE9, .. " \\\n three\n"u8];
        Assert.Contains((byte)0xE9, content);
        List<(string? section, string name, string? value)> entries = await ParseBytes(content);
        Assert.Single(entries);
        Assert.Equal("one  caf\uFFFD  three", entries[0].value);
    }

    [Fact]
    public async Task EscapeSequence_FollowedByRawE9Byte_Preserved()
    {
        // An escape sequence followed by a raw non-UTF-8 byte: the unescaped
        // char and the raw byte both survive.
        byte[] content = [.. "[core]\n\tkey = a\\tbcaf"u8, 0xE9, .. "\n"u8];
        Assert.Equal((byte)0xE9, content[^2]);
        List<(string? section, string name, string? value)> entries = await ParseBytes(content);
        Assert.Single(entries);
        Assert.Equal("a\tbcaf\uFFFD", entries[0].value);
    }

    private static Task<List<(string? section, string name, string? value)>> Parse(string content)
    {
        // The parser operates on raw bytes; the string input is UTF-8-encoded
        // (identical to the old byte mapping for ASCII; non-ASCII chars become
        // their UTF-8 sequences).
        return ParseBytes(Encoding.UTF8.GetBytes(content));
    }

    private static async Task<List<(string? section, string name, string? value)>> ParseBytes(byte[] content)
    {
        var results = new List<(string?, string, string?)>();
        await ConfigParser.ParseAsync(
            path: "test",
            content: content,
            onSection: (section, line, _) => Task.CompletedTask,
            onVariable: (section, name, value, line, _) =>
            {
                // The parser gives the raw varName as bytes; the backend builds
                // the fully-qualified key (section.lowercased(varName)). Replicate
                // that here for assertion convenience, decoding bytes as UTF-8
                // (U+FFFD for invalid sequences — the display decode contract).
                string? sectionStr = section is { } s ? Encoding.UTF8.GetString(s.Span) : null;
                string nameStr = Encoding.UTF8.GetString(name.Span);
                string? valueStr = value is { } v ? Encoding.UTF8.GetString(v.Span) : null;
                string fqName = sectionStr is null
                    ? nameStr.ToLowerInvariant()
                    : $"{sectionStr}.{nameStr.ToLowerInvariant()}";
                results.Add((sectionStr, fqName, valueStr));
                return Task.CompletedTask;
            },
            onComment: (line, _) => Task.CompletedTask,
            onEof: _ => Task.CompletedTask);
        return results;
    }
}

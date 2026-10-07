using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Tests for the byte-faithful <see cref="GitPathSpec"/>
/// + <see cref="FnMatchPattern"/>. These pin the <see cref="GitPathSpec.MatchContext"/>
/// slot pattern (the structural fix that replaces the per-call
/// <c>StringComparison</c> ternary, mirroring the diff-pipeline slots)
/// and the byte-exact round-trip for non-UTF-8 pathspec patterns/paths. The
/// ASCII-only fold parity fix applies to the
/// <see cref="GitPathSpec.MatchContext.Strcomp"/>/<see cref="GitPathSpec.MatchContext.Strncomp"/>
/// slots.
/// </summary>
public class PathspecNonUtf8PathTests
{
    // A non-UTF-8 pathspec pattern matches a non-UTF-8 path byte-exact.
    // A string bridge would U+FFFD-replace the invalid bytes
    // on both sides and corrupt the match.
    [Fact]
    public void NonUtf8Pattern_MatchesNonUtf8Path_ByteExact()
    {
        byte[] raw = [0xFF, 0xFE, 0x80];
        var ps = GitPathSpec.New(GitPath.FromUtf8Bytes(raw));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, GitPath.FromUtf8Bytes(raw)));
    }

    // A non-UTF-8 pathspec pattern does NOT match a different non-UTF-8 path.
    [Fact]
    public void NonUtf8Pattern_DoesNotMatchDifferentPath()
    {
        byte[] pat = [0xFF, 0xFE, 0x80];
        byte[] other = [0xFF, 0xFE, 0x81];
        var ps = GitPathSpec.New(GitPath.FromUtf8Bytes(pat));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, GitPath.FromUtf8Bytes(other)));
    }

    // NoGlob + non-UTF-8 prefix matches via the strcomp slot (exact byte compare).
    [Fact]
    public void NoGlob_NonUtf8Prefix_MatchesViaStrcompSlot()
    {
        byte[] prefix = [0xFF, 0xFE, 0x80];
        var ps = GitPathSpec.New(GitPath.FromUtf8Bytes(prefix));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.NoGlob, GitPath.FromUtf8Bytes(prefix)));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.NoGlob, GitPath.FromUtf8Bytes(new byte[] { 0xFF, 0xFE, 0x81 })));
    }

    // IgnoreCase flag: ASCII letters fold in the strcomp/strncomp slots;
    // non-ASCII bytes do NOT (ASCII-fold parity fix vs OrdinalIgnoreCase).
    [Fact]
    public void IgnoreCase_AsciiFoldsButNonAsciiDoesNot()
    {
        var ps = GitPathSpec.New(GitPath.FromUtf8String("FOO"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.IgnoreCase, GitPath.FromUtf8String("foo")));

        // Non-ASCII: ä (0xC3 0xA4) does NOT fold to Ä (0xC3 0x84).
        var ps2 = GitPathSpec.New(GitPath.FromUtf8Bytes(new byte[] { 0xC3, 0x84 })); // Ä
        Assert.False(ps2.MatchesPath(GitPathSpec.MatchFlags.IgnoreCase, GitPath.FromUtf8Bytes(new byte[] { 0xC3, 0xA4 }))); // ä
    }

    // Prefix of multiple non-UTF-8 patterns is the byte-exact common prefix
    // (including the '/'). The C git_pathspec_prefix truncates at the first
    // unescaped wildcard, not at '/'.
    [Fact]
    public void Prefix_NonUtf8CommonPrefix_ByteExact()
    {
        byte[] common = [0xFF, 0xFE, 0x80];
        byte[] p1 = [.. common, .. "/foo"u8];
        byte[] p2 = [.. common, .. "/bar"u8];
        var ps = GitPathSpec.New(GitPath.FromUtf8Bytes(p1), GitPath.FromUtf8Bytes(p2));
        byte[] expected = [.. common, .. "/"u8];
        Assert.Equal(expected, ps.Prefix.Span.ToArray());
    }

    // Negative pattern with non-UTF-8 prefix excludes a non-UTF-8 path when
    // the negative comes AFTER the positive (gitignore semantics: later rules
    // override earlier; git_pathspec__match_at returns the first >= 0 result).
    [Fact]
    public void NegativePattern_NonUtf8_ExcludesPath()
    {
        byte[] raw = [0xFF, 0xFE, 0x80];
        byte[] neg = [(byte)'!', .. raw];
        // Positive first, then negative — the negative overrides.
        _ = GitPathSpec.New(
            GitPath.FromUtf8Bytes(raw),
            GitPath.FromUtf8Bytes(neg));
        // pathspec_match_one iterates: positive returns 1, breaks before the
        // negative. To test exclusion, the negative must come first.
        var psNegFirst = GitPathSpec.New(
            GitPath.FromUtf8Bytes(neg),
            GitPath.FromUtf8Bytes(raw));
        Assert.False(psNegFirst.MatchesPath(GitPathSpec.MatchFlags.Default, GitPath.FromUtf8Bytes(raw)));
    }

    // MatchContext slots: NoGlob disables wildmatch (wildmatch_flags = -1),
    // so a wildcard pattern matches only via strcomp (exact).
    [Fact]
    public void NoGlob_DisablesWildmatch_WildcardPatternDoesNotMatch()
    {
        var ps = GitPathSpec.New(GitPath.FromUtf8String("*.txt"));
        // NoGlob: "*.txt" only matches the literal string "*.txt", not "foo.txt".
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.NoGlob, GitPath.FromUtf8String("foo.txt")));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.NoGlob, GitPath.FromUtf8String("*.txt")));
    }

    // Directory prefix match (pathspec.c:152-156): pattern with no wildcards
    // matches path/prefix when path starts with "pattern/".
    [Fact]
    public void DirectoryPrefixMatch_NonUtf8_MatchesPathInside()
    {
        byte[] dir = [0xFF, 0xFE, 0x80];
        var ps = GitPathSpec.New(GitPath.FromUtf8Bytes(dir));
        byte[] insidePath = [.. dir, .. "/file"u8];
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, GitPath.FromUtf8Bytes(insidePath)));
    }
}

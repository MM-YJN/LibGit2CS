using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.UnitTests.Core;

// Parity cases verified against libgit2 1.9.4:
//  - mailmap email scans must stop at '#' like C's advance_until
//    (mailmap.c:81-85) — a line like "Real Name <a#b@c>" is rejected by C
//    (no entry) but was accepted with replace_email "a#b@c".
//  - GitOidShortener.Add must throw GitException (not
//    IndexOutOfRangeException) when the hex string ends before 40 nibbles
//    while descending an existing trie path — C reads the NUL terminator and
//    git__fromhex('\0') returns -1 (oid.c:486-493).
//  - default(GitOid) must equal a parsed all-zero OID — C's
//    git_oid_equal is a fixed-size memcmp with no algorithm field, so a
//    zero-filled oid equals a parsed "000...0" oid.
//  - overlong UTF-8 sequences (E0 80..9F, F0 80..8F) must fall through
//    to the documented Latin-1 byte mapping instead of decoding to U+0000.
public sealed class CoreLowRegressionTests
{
    // ---- mailmap email scan stops at '#' ----

    [Fact]
    public void Mailmap_EmailWithHash_LineIsSkipped()
    {
        // C's advance_until stops at '#' (mailmap.c:81-85), so
        // "Real Name <a#b@c>" is rejected and no entry is added. Adding an
        // entry with replace_email "a#b@c" would make a lookup of a
        // different name with that email resolve to "Real Name".
        using var mm = GitMailmap.FromBuffer("Real Name <a#b@c>\n");

        (string name, string email) = mm.Resolve("Someone Else", "a#b@c");
        Assert.Equal("Someone Else", name);
        Assert.Equal("a#b@c", email);
    }

    [Fact]
    public void Mailmap_TwoEmailForm_SecondEmailWithHash_LineIsSkipped()
    {
        // Same for the two-email form: the second email scan must also stop
        // at '#' (advance_until is used for both emails in C).
        using var mm = GitMailmap.FromBuffer("Real Name <real@x> Replace Name <a#b@c>\n");

        (string name, string email) = mm.Resolve("Someone Else", "a#b@c");
        Assert.Equal("Someone Else", name);
        Assert.Equal("a#b@c", email);
    }

    [Fact]
    public void Mailmap_EmailWithHash_Control_PlainEmailStillParses()
    {
        // Control: a normal line still produces an entry.
        using var mm = GitMailmap.FromBuffer("Real Name <real@x>\n");

        (string name, string email) = mm.Resolve("Someone Else", "real@x");
        Assert.Equal("Real Name", name);
        Assert.Equal("real@x", email);
    }

    // ---- OidShortener short-hex input throws a managed error ----

    [Fact]
    public void OidShortener_ShortHexDescendingExistingPath_ThrowsGitException()
    {
        // Two divergent full OIDs build internal nodes; a short string that
        // is a prefix of an existing path descends to its end, where C reads
        // the NUL terminator (git__fromhex('\0') == -1) and returns -1.
        // The shortener must not throw IndexOutOfRangeException.
        using var shortener = new GitOidShortener();
        shortener.Add("0123456789abcdef0123456789abcdef01234567");
        shortener.Add("0124456789abcdef0123456789abcdef01234567");

        GitException error = Assert.Throws<GitException>(() => shortener.Add("0123"));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - invalid hex value", error.Message);
    }

    [Fact]
    public void OidShortener_SingleNibblePrefixOfExistingPath_ThrowsGitException()
    {
        // "0" is a prefix of the existing OID's path: the trie descends the
        // leaf and the next read is past the end of the string.
        using var shortener = new GitOidShortener();
        shortener.Add("0123456789abcdef0123456789abcdef01234567");

        GitException error = Assert.Throws<GitException>(() => shortener.Add("0"));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - invalid hex value", error.Message);
    }

    [Fact]
    public void OidShortener_ShortHexHittingEmptySlot_StillReturnsMinLength()
    {
        // Control: a short string that lands in an empty slot breaks early
        // and returns the current min length (C returns the same — the
        // NUL-terminator read never happens).
        using var shortener = new GitOidShortener();
        shortener.Add("0123456789abcdef0123456789abcdef01234567");

        Assert.Equal(4, shortener.Add("abc"));
    }

    [Fact]
    public void OidShortener_NonHexCharacter_ThrowsGitException()
    {
        // Control: non-hex characters carry C's error code and category.
        using var shortener = new GitOidShortener();
        shortener.Add("0123456789abcdef0123456789abcdef01234567");

        GitException error = Assert.Throws<GitException>(() => shortener.Add("012z"));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - invalid hex value", error.Message);
    }

    // ---- default(GitOid) equals a parsed all-zero OID ----

    [Fact]
    public void Oid_DefaultEqualsParsedAllZero()
    {
        // C's git_oid_equal is a fixed-size memcmp: a zero-filled oid equals
        // a parsed "000...0" oid. Comparing empty RawBytes
        // against 20 zero bytes (or Algorithm 0 vs Sha1) would not.
        var zero = GitOid.Parse("0000000000000000000000000000000000000000", GitHashAlgorithmKind.Sha1);

        Assert.True(default(GitOid).Equals(zero));
        Assert.True(zero.Equals(default));
        Assert.True(default == zero);
    }

    [Fact]
    public void Oid_DefaultHashCodeMatchesParsedAllZero()
    {
        // Dictionary/equality consistency: equal OIDs must hash alike.
        var zero = GitOid.Parse("0000000000000000000000000000000000000000", GitHashAlgorithmKind.Sha1);

        Assert.Equal(zero.GetHashCode(), default(GitOid).GetHashCode());
    }

    [Fact]
    public void Oid_DefaultCompareToParsedAllZero_IsZero()
    {
        var zero = GitOid.Parse("0000000000000000000000000000000000000000", GitHashAlgorithmKind.Sha1);

        Assert.Equal(0, default(GitOid).CompareTo(zero));
        Assert.Equal(0, zero.CompareTo(default));
    }

    [Fact]
    public void Oid_DefaultDoesNotEqualNonZero()
    {
        var one = GitOid.Parse("0000000000000000000000000000000000000001", GitHashAlgorithmKind.Sha1);

        Assert.False(default(GitOid).Equals(one));
        Assert.NotEqual(default(GitOid).GetHashCode(), one.GetHashCode());
    }

    [Fact]
    public void Oid_DefaultEqualsDefault()
    {
        Assert.True(default(GitOid).Equals(default));
        Assert.Equal(0, default(GitOid).CompareTo(default));
    }

    // ---- overlong UTF-8 sequences map via Latin-1, not U+0000 ----

    // C's git_fs_path_fromurl keeps the leading '/' only on POSIX
    // (fs_path.c:511-513 — the offset-- is #ifndef GIT_WIN32); on Windows
    // the decoded path starts at the first path byte. The C test suite
    // mirrors this with ABS_PATH_MARKER (tests/util/path/core.c).
    private static string PosixRooted(string path)
        => OperatingSystem.IsWindows() ? path : "/" + path;

    [Fact]
    public void UrlUtils_OverlongThreeByteSequence_MapsToSameCodePoint()
    {
        // E0 80 80 is an overlong encoding of U+0000. The documented contract
        // maps invalid UTF-8 bytes to the char with the same code point
        // (U+E0 U+80 U+80); decoding it to an embedded NUL
        // char would hit a code point .NET file APIs reject.
        string path = GitUrlUtils.LocalPathFromUrl("file:///tmp/a%E0%80%80b");

        Assert.DoesNotContain('\0', path);
        Assert.Equal(PosixRooted("tmp/a" + (char)0xE0 + (char)0x80 + (char)0x80 + "b"), path);
    }

    [Fact]
    public void UrlUtils_OverlongFourByteSequence_MapsToSameCodePoint()
    {
        // F0 80 80 80 is an overlong encoding of U+0000; the same
        // code-point mapping applies.
        string path = GitUrlUtils.LocalPathFromUrl("file:///tmp/a%F0%80%80%80b");

        Assert.DoesNotContain('\0', path);
        Assert.Equal(PosixRooted("tmp/a" + (char)0xF0 + (char)0x80 + (char)0x80 + (char)0x80 + "b"), path);
    }

    [Fact]
    public void UrlUtils_ValidUtf8Sequence_StillDecodes()
    {
        // Control: valid UTF-8 (C3 A9 = é) still decodes to the Unicode char.
        string path = GitUrlUtils.LocalPathFromUrl("file:///tmp/a%C3%A9b");

        Assert.Equal(PosixRooted("tmp/aé" + "b"), path);
    }

    [Fact]
    public void UrlUtils_IsolatedHighByte_MapsToSameCodePoint()
    {
        // Control: an isolated E9 (invalid UTF-8) maps to U+00E9 (the char
        // with the same code point).
        string path = GitUrlUtils.LocalPathFromUrl("file:///tmp/a%E9b");

        Assert.Equal(PosixRooted("tmp/a" + (char)0xE9 + "b"), path);
    }
}

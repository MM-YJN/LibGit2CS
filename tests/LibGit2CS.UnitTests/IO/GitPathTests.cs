using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public class GitPathTests
{
    private static GitPath P(params byte[] bytes) => GitPath.FromUtf8Bytes(bytes);

    private static GitPath S(string s) => GitPath.FromUtf8String(s);

    private static int Sign(int v) => v < 0 ? -1 : (v > 0 ? 1 : 0);

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    public void EqualsUtf8_AroundBufferBoundaries_ComparesEncodedBytes(int byteLength)
    {
        string text = new string('é', byteLength / 2) + (byteLength % 2 == 0 ? "" : "a");
        GitPath path = S(text);

        Assert.Equal(byteLength, path.Length);
        Assert.True(path.EqualsUtf8(text));
        Assert.False(path.EqualsUtf8(text.Replace('é', 'ê')));
        Assert.False(path.EqualsUtf8(text + "a"));
        Assert.False(path.EqualsUtf8(text.AsSpan(0, text.Length - 1)));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    public void EndsWithUtf8_AroundBufferBoundaries_ComparesEncodedBytes(int byteLength)
    {
        string text = new string('é', byteLength / 2) + (byteLength % 2 == 0 ? "" : "a");
        GitPath path = S("dir/" + text);

        Assert.Equal(byteLength + 4, path.Length);
        Assert.True(path.EndsWithUtf8(text));
        Assert.True(S(text).EndsWithUtf8(text));
        Assert.False(path.EndsWithUtf8(text.Replace('é', 'ê')));
        Assert.False(path.EndsWithUtf8("longer/" + text));
    }

    // ----- storage / round-trip -----

    [Fact]
    public void FromUtf8String_Ascii_RoundTrips()
    {
        GitPath p = S("hello");
        Assert.Equal(5, p.Length);
        Assert.Equal("hello", p.ToUtf8String());
    }

    [Fact]
    public void FromUtf8String_Utf8_RoundTripsByteForByte()
    {
        // "café" in UTF-8: 63 61 66 C3 A9
        GitPath p = S("café");
        byte[] bytes = p.Span.ToArray();
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9 }, bytes);
        Assert.Equal("café", p.ToUtf8String());
    }

    [Fact]
    public void FromUtf8Bytes_NonUtf8_BytesPreservedThroughRoundTrip()
    {
        // Invalid UTF-8 sequence; must survive byte-identical through storage.
        byte[] raw = [0xFF, 0xFE, 0x80, 0xC0];
        GitPath p = P(raw);
        Assert.Equal(raw, p.Span.ToArray());
        Assert.Equal(raw.Length, p.Length);
    }

    [Fact]
    public void ToUtf8String_InvalidBytes_ReplacementFallback()
    {
        // Invalid UTF-8 -> U+FFFD (display limitation, matches git).
        GitPath p = P(0xFF, 0xFE);
        string decoded = p.ToUtf8String();
        Assert.Equal("\uFFFD\uFFFD", decoded);
    }

    [Fact]
    public void IsEmpty_DefaultAndEmpty_AreEmpty()
    {
        Assert.True(default(GitPath).IsEmpty);
        Assert.True(S("").IsEmpty);
        Assert.False(S("x").IsEmpty);
    }

    // ----- equality / hashing -----

    [Fact]
    public void Equals_ByteIdenticalPaths_AreEqual()
    {
        Assert.True(S("café") == P(0x63, 0x61, 0x66, 0xC3, 0xA9));
        Assert.True(S("café").Equals(P(0x63, 0x61, 0x66, 0xC3, 0xA9)));
    }

    [Fact]
    public void Equals_DifferentBytes_NotEqual()
    {
        Assert.True(S("abc") != S("abd"));
        Assert.True(S("abc") != S("abcd"));
    }

    [Fact]
    public void GetHashCode_EqualPaths_SameHash()
    {
        Assert.Equal(S("café").GetHashCode(), P(0x63, 0x61, 0x66, 0xC3, 0xA9).GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentPaths_VastlyDifferent()
    {
        // Reasonable distribution; not strictly required but a smoke test.
        HashSet<int> hashes = [];
        for (int i = 0; i < 256; i++)
        {
            hashes.Add(S($"path/subdir/file{i:D3}.txt").GetHashCode());
        }

        Assert.True(hashes.Count > 200);
    }

    // ----- Compare (strcmp) -----

    [Fact]
    public void Compare_SameBytes_Zero()
    {
        Assert.Equal(0, GitPath.Compare(S("abc"), S("abc")));
    }

    [Fact]
    public void Compare_FirstDifference_ReturnsSignedByteDiff()
    {
        Assert.Equal(-1, Sign(GitPath.Compare(S("abc"), S("abd"))));
        Assert.Equal(1, Sign(GitPath.Compare(S("abd"), S("abc"))));
    }

    [Fact]
    public void Compare_PrefixShorter_SortsFirst()
    {
        // "abc" < "abcd" (NUL < 'd')
        Assert.Equal(-1, Sign(GitPath.Compare(S("abc"), S("abcd"))));
        Assert.Equal(1, Sign(GitPath.Compare(S("abcd"), S("abc"))));
    }

    [Fact]
    public void Compare_HighBytes_UnsignedOrdering()
    {
        // 0x80 vs 0x7F: unsigned, 0x80 > 0x7F (no sign extension).
        Assert.Equal(1, Sign(GitPath.Compare(P(0x80), P(0x7F))));
    }

    // ----- Compare(a,b,length) (strncmp) -----

    [Fact]
    public void Compare_Bounded_WithinPrefix_Zero()
    {
        Assert.Equal(0, GitPath.Compare(S("abc"), S("abd"), 2));
        Assert.Equal(0, GitPath.Compare(S("ab"), S("abcd"), 2));
    }

    [Fact]
    public void Compare_Bounded_PastPrefix_DetectsDifference()
    {
        Assert.Equal(-1, Sign(GitPath.Compare(S("abc"), S("abd"), 3)));
    }

    [Fact]
    public void Compare_Bounded_StrShorterThanLength_Negative()
    {
        // "ab" vs "abcd" over length 3: a ends at the NUL (0) vs 'c'.
        Assert.Equal(-1, Sign(GitPath.Compare(S("ab"), S("abcd"), 3)));
        Assert.Equal(1, Sign(GitPath.Compare(S("abcd"), S("ab"), 3)));
    }

    [Fact]
    public void Compare_Bounded_BothShorterAndEqual_Zero()
    {
        Assert.Equal(0, GitPath.Compare(S("ab"), S("ab"), 10));
    }

    [Fact]
    public void Compare_Bounded_ZeroLength_Zero()
    {
        Assert.Equal(0, GitPath.Compare(S("abc"), S("xyz"), 0));
    }

    // ----- CompareIgnoreCase (strcasecmp) -----

    [Fact]
    public void CompareIgnoreCase_AsciiLetters_FoldsEqual()
    {
        Assert.Equal(0, GitPath.CompareIgnoreCase(S("ABC"), S("abc")));
        Assert.Equal(0, GitPath.CompareIgnoreCase(S("Hello"), S("hELLo")));
    }

    [Fact]
    public void CompareIgnoreCase_FoldMismatch_ReturnsFoldDiff()
    {
        Assert.Equal(-1, Sign(GitPath.CompareIgnoreCase(S("abc"), S("abd"))));
        Assert.Equal(-1, Sign(GitPath.CompareIgnoreCase(S("ABC"), S("abd"))));
    }

    [Fact]
    public void CompareIgnoreCase_NonAsciiBytes_DoNotFold()
    {
        // PARITY DIVERGENCE: U+00C9 (É, UTF-8 C3 89) vs U+00E9 (é, UTF-8 C3 A9).
        // .NET OrdinalIgnoreCase folds these equal; libgit2 ASCII-fold does not.
        bool frameworkFolds = string.Equals("É", "é", StringComparison.OrdinalIgnoreCase);
        Assert.True(frameworkFolds);
        Assert.NotEqual(0, GitPath.CompareIgnoreCase(S("É"), S("é")));
    }

    // ----- CompareIgnoreCase(a,b,length) (strncasecmp) -----

    [Fact]
    public void CompareIgnoreCase_Bounded_FoldsAndCaps()
    {
        Assert.Equal(0, GitPath.CompareIgnoreCase(S("ABC"), S("abc"), 3));
        Assert.Equal(0, GitPath.CompareIgnoreCase(S("ABC"), S("abd"), 2));
        Assert.Equal(-1, Sign(GitPath.CompareIgnoreCase(S("abc"), S("abd"), 3)));
    }

    [Fact]
    public void CompareIgnoreCase_Bounded_StrShorter_Negative()
    {
        Assert.Equal(-1, Sign(GitPath.CompareIgnoreCase(S("ab"), S("abcd"), 3)));
    }

    // ----- CompareCaseSort (git__strcasesort_cmp) -----

    [Fact]
    public void CompareCaseSort_FoldEqualButCaseDifferent_TiebreakByRawCase()
    {
        // "abc" vs "ABC": fold-equal; tiebreak is first differing raw byte: 'a'-'A' > 0.
        Assert.Equal(1, Sign(GitPath.CompareCaseSort(S("abc"), S("ABC"))));
        Assert.Equal(-1, Sign(GitPath.CompareCaseSort(S("ABC"), S("abc"))));
    }

    [Fact]
    public void CompareCaseSort_FoldMismatch_ReturnsFoldDiff()
    {
        Assert.Equal(-1, Sign(GitPath.CompareCaseSort(S("abc"), S("abd"))));
    }

    [Fact]
    public void CompareCaseSort_ExactEqual_Zero()
    {
        Assert.Equal(0, GitPath.CompareCaseSort(S("abc"), S("abc")));
    }

    // ----- ComparePrefix (git__prefixcmp) -----

    [Fact]
    public void ComparePrefix_StrStartsWithPrefix_Zero()
    {
        Assert.Equal(0, GitPath.ComparePrefix(S("foobar"), S("foo")));
        Assert.Equal(0, GitPath.ComparePrefix(S("foo"), S("foo")));
        Assert.Equal(0, GitPath.ComparePrefix(S("foo"), S(""))); // empty prefix
    }

    [Fact]
    public void ComparePrefix_Mismatch_ReturnsByteDiff()
    {
        Assert.Equal(1, Sign(GitPath.ComparePrefix(S("foobar"), S("bar")))); // 'f' > 'b'
    }

    [Fact]
    public void ComparePrefix_StrShorterThanPrefix_Negative()
    {
        Assert.Equal(-1, Sign(GitPath.ComparePrefix(S("foo"), S("foobar"))));
    }

    // ----- ComparePrefix(str,strLen,prefix) (git__prefixncmp) -----

    [Fact]
    public void ComparePrefix_Bounded_StrLenCapsPrefixMatch_Zero()
    {
        Assert.Equal(0, GitPath.ComparePrefix(S("foobar"), 3, S("foo")));
        Assert.Equal(0, GitPath.ComparePrefix(S("foobar"), 6, S("foo"))); // prefix exhausted first
    }

    [Fact]
    public void ComparePrefix_Bounded_StrLenExhaustedBeforePrefix_Negative()
    {
        // Only 2 bytes of str consumed ("fo"); prefix still has 'o' remaining -> -'o'.
        Assert.True(GitPath.ComparePrefix(S("foobar"), 2, S("foo")) < 0);
    }

    [Fact]
    public void ComparePrefix_Bounded_Mismatch_ReturnsByteDiff()
    {
        Assert.True(GitPath.ComparePrefix(S("foobar"), 6, S("bar")) > 0); // 'f' > 'b'
    }

    // ----- ComparePrefixIgnoreCase / bounded -----

    [Fact]
    public void ComparePrefixIgnoreCase_AsciiFolds()
    {
        Assert.Equal(0, GitPath.ComparePrefixIgnoreCase(S("FoObAr"), S("foo")));
        Assert.Equal(0, GitPath.ComparePrefixIgnoreCase(S("FOOBAR"), 3, S("foo")));
    }

    // ----- CompareLengthAware (git__strlcmp) -----

    [Fact]
    public void CompareLengthAware_ExactPrefixLength_Zero()
    {
        Assert.Equal(0, GitPath.CompareLengthAware(S("foo"), S("foo")));
    }

    [Fact]
    public void CompareLengthAware_LongerThanPrefix_Positive()
    {
        Assert.Equal((int)'b', GitPath.CompareLengthAware(S("foobar"), S("foo")));
    }

    [Fact]
    public void CompareLengthAware_PrefixMismatch_ReturnsCompareResult()
    {
        Assert.True(GitPath.CompareLengthAware(S("foobar"), S("xyz")) < 0);
    }

    [Fact]
    public void CompareLengthAware_StrShorterThanPrefix_Negative()
    {
        Assert.True(GitPath.CompareLengthAware(S("fo"), S("foo")) < 0);
    }

    // ----- CompareTreeOrder (git_fs_path_cmp) -----

    [Fact]
    public void CompareTreeOrder_SameName_BothBlobs_Zero()
    {
        Assert.Equal(0, GitPath.CompareTreeOrder(S("foo"), false, S("foo"), false));
    }

    [Fact]
    public void CompareTreeOrder_BlobSortsBeforeContinuedName()
    {
        // blob "foo" vs blob "foo.bar": NUL(0) < '.'(0x2E) -> foo first
        Assert.Equal(-1, GitPath.CompareTreeOrder(S("foo"), false, S("foo.bar"), false));
    }

    [Fact]
    public void CompareTreeOrder_TreeSortsAfterContinuedName()
    {
        // tree "foo" (virtual '/') vs blob "foo.bar" ('.'): '/'(0x2F) > '.'(0x2E) -> tree after
        Assert.Equal(1, GitPath.CompareTreeOrder(S("foo"), true, S("foo.bar"), false));
    }

    [Fact]
    public void CompareTreeOrder_TreeSortsAfterBlobOfSameName()
    {
        // blob "foo" (NUL) vs tree "foo" ('/'): NUL < '/' -> blob first
        Assert.Equal(-1, GitPath.CompareTreeOrder(S("foo"), false, S("foo"), true));
        Assert.Equal(1, GitPath.CompareTreeOrder(S("foo"), true, S("foo"), false));
    }

    [Fact]
    public void CompareTreeOrder_UnrelatedNames_PlainByteOrder()
    {
        Assert.Equal(-1, GitPath.CompareTreeOrder(S("abc"), true, S("abd"), true));
        Assert.Equal(-1, GitPath.CompareTreeOrder(S("abc"), false, S("abd"), false));
    }

    // ----- CompareHoming (homing_search_cmp) -----

    [Fact]
    public void CompareHoming_PrefixEqual_ZeroRegardlessOfLength()
    {
        // KEY distinction from Compare: homing returns 0 whenever one is a byte-prefix of the other.
        Assert.Equal(0, GitPath.CompareHoming(S("foo"), S("foobar")));
        Assert.Equal(0, GitPath.CompareHoming(S("foobar"), S("foo")));
        Assert.Equal(0, GitPath.CompareHoming(S("foo"), S("foo")));
    }

    [Fact]
    public void CompareHoming_FirstDifference_ReturnsByteDiff()
    {
        Assert.True(GitPath.CompareHoming(S("foo"), S("bar")) > 0);
        Assert.True(GitPath.CompareHoming(S("bar"), S("foo")) < 0);
    }

    // ----- dispatcher -----

    [Fact]
    public void Compare_IgnoreCaseDispatcher_MatchesRespectiveComparator()
    {
        Assert.Equal(0, GitPath.Compare(S("ABC"), S("abc"), ignoreCase: true));
        Assert.NotEqual(0, GitPath.Compare(S("ABC"), S("abc"), ignoreCase: false));
    }

    // ----- implicit conversion bridge -----

    [Fact]
    public void ImplicitConversion_StringToGitPath_RoundTrips()
    {
        GitPath p = S("hello");
        Assert.Equal("hello", p.ToUtf8String());
    }

    [Fact]
    public void ImplicitConversion_GitPathToString_Decodes()
    {
        string s = S("café").ToUtf8String();
        Assert.Equal("café", s);
    }

    [Fact]
    public void ImplicitConversion_NullString_DefaultGitPath()
    {
        GitPath p = default;
        Assert.True(p.IsEmpty);
    }
}

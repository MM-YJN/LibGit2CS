using LibGit2CS.Refs;

namespace LibGit2CS.UnitTests.Refs;

public sealed class ReferenceNameTests
{
    [Theory]
    [InlineData("refs/heads/master", "refs/heads/master")]
    [InlineData("refs///heads///a", "refs/heads/a")]
    [InlineData("refs/heads/foo/bar", "refs/heads/foo/bar")]
    public void Normalize_ValidNames_ReturnsNormalized(string input, string expected)
    {
        string? result = GitReferences.NormalizeName(input, GitReferenceFormatFlags.AllowOneLevel);

        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_AtSignBecomesHead()
    {
        string? result = GitReferences.NormalizeName("@", GitReferenceFormatFlags.AllowOneLevel);

        Assert.Equal("HEAD", result);
    }

    [Fact]
    public void Normalize_AllowOneLevel_Head()
    {
        string? result = GitReferences.NormalizeName("HEAD", GitReferenceFormatFlags.AllowOneLevel);

        Assert.Equal("HEAD", result);
    }

    [Fact]
    public void Normalize_WithoutAllowOneLevel_RejectsOneLevel()
    {
        string? result = GitReferences.NormalizeName("HEAD", GitReferenceFormatFlags.Normal);

        Assert.Null(result);
    }

    [Fact]
    public void Normalize_RefspecPattern_AllowsGlob()
    {
        string? result = GitReferences.NormalizeName("refs/heads/*", GitReferenceFormatFlags.RefspecPattern);

        Assert.NotNull(result);
        Assert.Equal("refs/heads/*", result);
    }

    [Fact]
    public void Normalize_RefspecShorthand_AllowsUppercaseFirstSegment()
    {
        string? result = GitReferences.NormalizeName("HEAD/feature", GitReferenceFormatFlags.RefspecShorthand);

        Assert.NotNull(result);
        Assert.Equal("HEAD/feature", result);
    }

    [Fact]
    public void Normalize_WithoutRefspecShorthand_RejectsUppercaseFirstSegment()
    {
        string? result = GitReferences.NormalizeName("HEAD/feature", GitReferenceFormatFlags.Normal);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("refs/heads/")]
    [InlineData(".refs/heads/master")]
    [InlineData("refs/heads/foo..bar")]
    [InlineData("refs/heads/foo@{bar")]
    [InlineData("refs/heads/foo.lock")]
    [InlineData("refs/heads/a/b.lock/c")]
    [InlineData("refs/heads/foo bar")]
    [InlineData("refs/heads/foo~bar")]
    [InlineData("refs/heads/foo^bar")]
    [InlineData("refs/heads/foo:bar")]
    [InlineData("refs/heads/foo\\bar")]
    [InlineData("refs/heads/foo?bar")]
    [InlineData("refs/heads/foo[bar")]
    [InlineData("refs/heads/foo.bar.")]
    public void Normalize_InvalidNames_ReturnsNull(string input)
    {
        Assert.Null(GitReferences.NormalizeName(input, GitReferenceFormatFlags.AllowOneLevel));
    }

    [Fact]
    public void Normalize_ControlCharacter_ReturnsNull()
    {
        // Use (char)1 to avoid C#'s variable-length \x escape ambiguity.
        Assert.Null(GitReferences.NormalizeName("refs/heads/foo" + (char)1 + "bar", GitReferenceFormatFlags.AllowOneLevel));
    }

    [Fact]
    public void Normalize_Del_ReturnsNull()
    {
        // DEL (0x7F) must be rejected. Using (char)127 avoids C#'s variable-length
        // \x escape ambiguity (\x7fbar would parse as U+7FBA + 'r').
        Assert.Null(GitReferences.NormalizeName("refs/heads/foo" + (char)127 + "bar", GitReferenceFormatFlags.AllowOneLevel));
    }

    [Fact]
    public void IsNameValid_ValidName_ReturnsTrue()
    {
        Assert.True(GitReferences.IsNameValid("refs/heads/master"));
    }

    [Fact]
    public void IsNameValid_InvalidName_ReturnsFalse()
    {
        Assert.False(GitReferences.IsNameValid(""));
    }

    [Fact]
    public void IsNameValid_TrailingUnderscore_Rejected()
    {
        Assert.False(GitReferences.IsNameValid("FOO_", GitReferenceFormatFlags.AllowOneLevel));
    }

    [Fact]
    public void IsNameValid_LeadingUnderscore_Rejected()
    {
        Assert.False(GitReferences.IsNameValid("_FOO", GitReferenceFormatFlags.AllowOneLevel));
    }

    [Fact]
    public void Normalize_NegativeRefSpec_AllowedWithPattern()
    {
        string? result = GitReferences.NormalizeName("^refs/heads/secret", GitReferenceFormatFlags.RefspecPattern);

        Assert.NotNull(result);
    }

    [Fact]
    public void Normalize_RefspecPattern_DoubleGlob_Rejected()
    {
        // Only one '*' per refspec is allowed.
        Assert.Null(GitReferences.NormalizeName("foo/*/*", GitReferenceFormatFlags.RefspecPattern | GitReferenceFormatFlags.AllowOneLevel));
    }

    [Fact]
    public void IsNameValid_MixedCaseFirstSegment_AlwaysAllowed()
    {
        // Mixed case like AaA/b is always valid (not all-caps, so not rejected).
        Assert.True(GitReferences.IsNameValid("AaA/b"));
    }
    [Theory]
    [InlineData(16)]
    [InlineData(1024)]
    public void IsNameValid_Utf8Name_PreservesValidationAndNormalization(int length)
    {
        string name = "refs/heads/" + new string('é', length);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        Assert.True(GitReferences.IsNameValid(name));
        Assert.True(GitReferences.IsNameValid(bytes.AsSpan()));
        Assert.Equal(name, GitReferences.NormalizeName(name));
        Assert.False(GitReferences.IsNameValid(name + "/"));
        Assert.False(GitReferences.IsNameValid(name + "//child"));
        Assert.Equal(name + "/child", GitReferences.NormalizeName(name + "//child"));
    }

    [Fact]
    public void IsNameValid_CommonNames_DoesNotAllocate()
    {
        const string name = "refs/heads/topic";
        // Warm up both paths before measuring only the synchronous validation calls.
        Assert.True(GitReferences.IsNameValid(name));
        Assert.True(GitReferences.IsNameValid("refs/heads/topic"u8));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int i = 0; i < 100; i++)
        {
            valid &= GitReferences.IsNameValid(name);
            valid &= GitReferences.IsNameValid("refs/heads/topic"u8);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        Assert.Equal(0, allocated);
    }
}

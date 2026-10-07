using LibGit2CS.Core;
using LibGit2CS.Refs;

namespace LibGit2CS.UnitTests.Refs;

public sealed class RefSpecTests
{
    [Fact]
    public void Parse_FetchWildcard_SetsFields()
    {
        var rs = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.True(rs.IsFetch);
        Assert.True(rs.IsWildcard);
        Assert.False(rs.Force);
        Assert.False(rs.IsNegative);
        Assert.Equal("refs/heads/*", rs.Source);
        Assert.Equal("refs/remotes/origin/*", rs.Destination);
    }

    [Fact]
    public void Parse_Force_SetsForce()
    {
        var rs = GitRefSpec.Parse("+refs/heads/master:refs/heads/master", isFetch: true);

        Assert.True(rs.Force);
        Assert.False(rs.IsWildcard);
    }

    [Fact]
    public void Parse_FetchNoDst_HasEmptyDst()
    {
        var rs = GitRefSpec.Parse("refs/heads/master", isFetch: true);

        Assert.Equal("refs/heads/master", rs.Source);
        Assert.Equal(string.Empty, rs.Destination);
    }

    [Fact]
    public void Parse_FetchEmptySrc_Allowed()
    {
        // Empty src in fetch means HEAD.
        var rs = GitRefSpec.Parse(":refs/remotes/origin/HEAD", isFetch: true);

        Assert.Equal(string.Empty, rs.Source);
        Assert.Equal("refs/remotes/origin/HEAD", rs.Destination);
    }

    [Fact]
    public void Parse_PushMatching_SetsMatching()
    {
        var rs = GitRefSpec.Parse(":", isFetch: false);

        Assert.True(rs.IsMatching);
        Assert.Equal(string.Empty, rs.Source);
        Assert.Equal(string.Empty, rs.Destination);
    }

    [Fact]
    public void Parse_PushForceMatching_SetsForceAndMatching()
    {
        var rs = GitRefSpec.Parse("+:", isFetch: false);

        Assert.True(rs.IsMatching);
        Assert.True(rs.Force);
    }

    [Fact]
    public void Parse_NegativeRefSpec_SetsNegative()
    {
        var rs = GitRefSpec.Parse("^refs/heads/secret", isFetch: true);

        Assert.True(rs.IsNegative);
        Assert.Equal("^refs/heads/secret", rs.Source);
    }

    [Fact]
    public void Parse_NegativeWildcard_Succeeds()
    {
        var rs = GitRefSpec.Parse("^refs/heads/secret/*", isFetch: true);

        Assert.True(rs.IsNegative);
        Assert.True(rs.IsWildcard);
    }

    [Fact]
    public void Parse_Invalid_Throws()
    {
        Assert.Throws<GitException>(() => GitRefSpec.Parse("refs/heads/*:refs/remotes/origin", isFetch: true));
    }

    [Fact]
    public void SrcMatches_Wildcard_Matches()
    {
        var rs = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.True(rs.SrcMatches("refs/heads/master"));
        Assert.True(rs.SrcMatches("refs/heads/feature/foo"));
        Assert.False(rs.SrcMatches("refs/tags/v1"));
    }

    [Fact]
    public void DstMatches_Wildcard_Matches()
    {
        var rs = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.True(rs.DstMatches("refs/remotes/origin/master"));
        Assert.False(rs.DstMatches("refs/heads/master"));
    }

    [Fact]
    public void SrcMatches_NonWildcard_ExactMatch()
    {
        var rs = GitRefSpec.Parse("refs/heads/master:refs/remotes/origin/master", isFetch: true);

        Assert.True(rs.SrcMatches("refs/heads/master"));
        Assert.False(rs.SrcMatches("refs/heads/other"));
    }

    [Fact]
    public void Transform_Wildcard_SubstitutesCorrectly()
    {
        var rs = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.Equal("refs/remotes/origin/master", rs.Transform("refs/heads/master"));
        Assert.Equal("refs/remotes/origin/feature/foo", rs.Transform("refs/heads/feature/foo"));
    }

    [Fact]
    public void Transform_NonWildcard_ReturnsDst()
    {
        var rs = GitRefSpec.Parse("refs/heads/master:refs/remotes/origin/master", isFetch: true);

        Assert.Equal("refs/remotes/origin/master", rs.Transform("refs/heads/master"));
    }

    [Fact]
    public void Transform_NonMatchingSrc_Throws()
    {
        var rs = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.Throws<GitException>(() => rs.Transform("refs/tags/v1"));
    }

    [Fact]
    public void ReverseTransform_Wildcard_Substitutes()
    {
        var rs = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.Equal("refs/heads/master", rs.ReverseTransform("refs/remotes/origin/master"));
    }

    [Fact]
    public void String_ReturnsOriginalInput()
    {
        var rs = GitRefSpec.Parse("+refs/heads/*:refs/remotes/origin/*", isFetch: true);

        Assert.Equal("+refs/heads/*:refs/remotes/origin/*", rs.String);
    }
}

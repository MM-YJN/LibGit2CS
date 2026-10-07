using LibGit2CS.Core;
using LibGit2CS.Refs;
using LibGit2CS.Remote;

namespace LibGit2CS.UnitTests.Refs;

/// <summary>
/// Differential battery for refspec parsing/matching/transform, generated
/// from a C probe harness (libgit2 1.9.4,
/// git_refspec_parse + git_refspec_src/dst_matches + git_refspec_transform +
/// git_refspec_is_negative/wildcard). Every row below is byte-faithful C
/// output.
/// </summary>
public sealed class RefspecParityBatteryTests
{
    private static readonly (bool IsFetch, string Spec, string Result)[] s_parseRows =
    [
        (true, "refs/heads/*:refs/remotes/origin/*", "force=0 wildcard=1 negative=0 src='refs/heads/*' dst='refs/remotes/origin/*'"),
        (true, "+refs/heads/master:refs/remotes/origin/master", "force=1 wildcard=0 negative=0 src='refs/heads/master' dst='refs/remotes/origin/master'"),
        (true, "refs/heads/master", "force=0 wildcard=0 negative=0 src='refs/heads/master' dst=''"),
        (true, ":", "force=0 wildcard=0 negative=0 src='' dst=''"),
        (true, "refs/heads/master:", "force=0 wildcard=0 negative=0 src='refs/heads/master' dst=''"),
        (true, ":refs/remotes/origin/master", "force=0 wildcard=0 negative=0 src='' dst='refs/remotes/origin/master'"),
        (true, "refs/tags/*", "ERR"),
        (true, "refs/heads/*:refs/remotes/origin", "ERR"),
        (true, "^refs/heads/secret*", "force=0 wildcard=1 negative=1 src='^refs/heads/secret*' dst=''"),
        (true, "^refs/heads/secret*:refs/remotes/origin/secret*", "ERR"),
        (true, "master", "force=0 wildcard=0 negative=0 src='master' dst=''"),
        (true, "foo/bar", "force=0 wildcard=0 negative=0 src='foo/bar' dst=''"),
        (true, "HEAD", "force=0 wildcard=0 negative=0 src='HEAD' dst=''"),
        (true, "+^refs/heads/*", "force=1 wildcard=1 negative=1 src='^refs/heads/*' dst=''"),
        (true, "refs/heads/*:", "ERR"),
        (true, "::", "ERR"),
        (true, "refs/heads/foo:refs/heads/foo:refs/heads/bar", "ERR"),
        (true, "*:refs/remotes/origin/*", "force=0 wildcard=1 negative=0 src='*' dst='refs/remotes/origin/*'"),
        (true, "refs/heads/*:*", "force=0 wildcard=1 negative=0 src='refs/heads/*' dst='*'"),
        (true, "refs/*/foo:refs/*/bar/*", "ERR"),
        (true, "", "force=0 wildcard=0 negative=0 src='' dst=''"),
        (false, "refs/heads/*:refs/remotes/origin/*", "force=0 wildcard=1 negative=0 src='refs/heads/*' dst='refs/remotes/origin/*'"),
        (false, "+refs/heads/master:refs/remotes/origin/master", "force=1 wildcard=0 negative=0 src='refs/heads/master' dst='refs/remotes/origin/master'"),
        (false, "refs/heads/master", "force=0 wildcard=0 negative=0 src='refs/heads/master' dst='refs/heads/master'"),
        (false, ":", "force=0 wildcard=0 negative=0 src='' dst=''"),
        (false, "refs/heads/master:", "ERR"),
        (false, ":refs/remotes/origin/master", "force=0 wildcard=0 negative=0 src='' dst='refs/remotes/origin/master'"),
        (false, "refs/tags/*", "force=0 wildcard=1 negative=0 src='refs/tags/*' dst='refs/tags/*'"),
        (false, "refs/heads/*:refs/remotes/origin", "ERR"),
        (false, "^refs/heads/secret*", "force=0 wildcard=1 negative=0 src='^refs/heads/secret*' dst='^refs/heads/secret*'"),
        (false, "^refs/heads/secret*:refs/remotes/origin/secret*", "ERR"),
        (false, "master", "force=0 wildcard=0 negative=0 src='master' dst='master'"),
        (false, "foo/bar", "force=0 wildcard=0 negative=0 src='foo/bar' dst='foo/bar'"),
        (false, "HEAD", "force=0 wildcard=0 negative=0 src='HEAD' dst='HEAD'"),
        (false, "+^refs/heads/*", "force=1 wildcard=1 negative=0 src='^refs/heads/*' dst='^refs/heads/*'"),
        (false, "refs/heads/*:", "ERR"),
        (false, "::", "ERR"),
        (false, "refs/heads/foo:refs/heads/foo:refs/heads/bar", "force=0 wildcard=0 negative=0 src='refs/heads/foo:refs/heads/foo' dst='refs/heads/bar'"),
        (false, "*:refs/remotes/origin/*", "force=0 wildcard=1 negative=0 src='*' dst='refs/remotes/origin/*'"),
        (false, "refs/heads/*:*", "force=0 wildcard=1 negative=0 src='refs/heads/*' dst='*'"),
        (false, "refs/*/foo:refs/*/bar/*", "ERR"),
        (false, "", "ERR"),
    ];

    [Theory]
    [MemberData(nameof(ParseRows))]
    public void Parse_MatchesC(bool isFetch, string spec, string expected)
    {
        if (expected == "ERR")
        {
            // C: GIT_EINVALIDSPEC, "'%s' is not a valid refspec."
            GitException ex = Assert.Throws<GitException>(() => GitRefSpec.Parse(spec, isFetch));
            Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
            Assert.Equal(GitErrorCategory.Invalid, ex.Category);
            Assert.Equal($"'{spec}' is not a valid refspec.", ex.Message);
            return;
        }

        var rs = GitRefSpec.Parse(spec, isFetch);
        string dst = rs.Destination.Length == 0 ? string.Empty : rs.Destination;
        string actual =
            $"force={(rs.Force ? 1 : 0)} wildcard={(rs.IsWildcard ? 1 : 0)} negative={(rs.IsNegative ? 1 : 0)} src='{rs.Source}' dst='{dst}'";
        Assert.Equal(expected, actual);
    }

    public static TheoryData<bool, string, string> ParseRows()
    {
        var data = new TheoryData<bool, string, string>();
        foreach ((bool isFetch, string spec, string result) in s_parseRows)
        {
            data.Add(isFetch, spec, result);
        }

        return data;
    }

    // ── matches/transform battery (C harness M rows) ────────────────────

    [Fact]
    public void Matches_Transform_MatchesC()
    {
        var heads = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);
        Assert.True(heads.SrcMatches("refs/heads/foo"));
        Assert.False(heads.SrcMatches("refs/tags/foo"));
        Assert.False(heads.DstMatches("refs/heads/foo"));
        Assert.Equal("refs/remotes/origin/foo", heads.Transform("refs/heads/foo"));
        Assert.Throws<GitException>(() => heads.Transform("refs/tags/foo"));

        var neg = GitRefSpec.Parse("^refs/heads/secret*", isFetch: true);
        Assert.True(neg.SrcMatchesNegative("refs/heads/secret1"));
        Assert.False(neg.SrcMatchesNegative("refs/heads/other"));
        Assert.False(neg.SrcMatches("refs/heads/secret1")); // '^' kept in src — C never matches

        var tags = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);
        Assert.True(tags.SrcMatches("refs/tags/v1.0"));
        Assert.True(tags.DstMatches("refs/tags/v1.0"));
        Assert.Equal("refs/tags/v1.0", tags.Transform("refs/tags/v1.0"));
    }

    // ── dwim: the LAST matching shorthand form wins (C loop overwrites) ──

    [Fact]
    public void DwimOne_LastMatchingShorthandWins()
    {
        var spec = GitRefSpec.Parse("master:master", isFetch: true);

        // All three forms advertised → C's loop ends with refs/heads/master.
        string[] all = new[] { "refs/master", "refs/tags/master", "refs/heads/master" };
        Assert.Equal("refs/heads/master", spec.DwimOne(all).Source);

        // Only refs/tags advertised → refs/tags/master.
        string[] tagOnly = new[] { "refs/tags/master" };
        Assert.Equal("refs/tags/master", spec.DwimOne(tagOnly).Source);

        // None advertised → unchanged.
        string[] none = Array.Empty<string>();
        Assert.Equal("master", spec.DwimOne(none).Source);
    }

    // ── MaybeWant: negative refspecs exclude; ALL-tags beats negatives ───

    [Fact]
    public void MaybeWant_NegativeRefspecExcludes()
    {
        var specs = new List<GitRefSpec>
        {
            GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true),
            GitRefSpec.Parse("^refs/heads/secret*", isFetch: true),
        };
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        // C (remote.c matching_refspec): a matching negative refspec returns
        // NULL immediately — the head is NOT wanted even though the positive
        // spec also matches.
        Assert.False(FetchCoordinator.MaybeWant(
            new GitRemoteHead(false, default, default, "refs/heads/secret1", null), specs, tagSpec, GitAutoTagOption.None));
        Assert.True(FetchCoordinator.MaybeWant(
            new GitRemoteHead(false, default, default, "refs/heads/main", null), specs, tagSpec, GitAutoTagOption.None));
        Assert.False(FetchCoordinator.MaybeWant(
            new GitRemoteHead(false, default, default, "refs/tags/v1", null), specs, tagSpec, GitAutoTagOption.None));

        // C (fetch.c:35-41): a tag matched by the ALL-tags tag spec is wanted
        // regardless of negative refspecs.
        Assert.True(FetchCoordinator.MaybeWant(
            new GitRemoteHead(false, default, default, "refs/tags/v1", null), specs, tagSpec, GitAutoTagOption.All));
    }

    [Fact]
    public void MaybeWant_SkipsPushSpecs()
    {
        var specs = new List<GitRefSpec>
        {
            GitRefSpec.Parse("refs/heads/secret1:refs/heads/secret1", isFetch: false),
        };
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        // C (remote.c matching_refspec): spec->push → continue.
        Assert.False(FetchCoordinator.MaybeWant(
            new GitRemoteHead(false, default, default, "refs/heads/secret1", null), specs, tagSpec, GitAutoTagOption.None));
    }
}

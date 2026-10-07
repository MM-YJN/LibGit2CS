using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

// Search reported
// non-participating (or nonexistent) capture groups as (0,0) instead of
// (-1,-1). C's git_regexp_search leaves start=end=-1 for groups that did
// not participate (POSIX regexec semantics, regexp.c:62-66, 140-144).
// Consumers like DiffDriver.PatternExtractor branch on RegexMatch.IsUnset
// (Start < 0 || End < 0), so a funcname pattern without a capture group
// selected the bogus (0,0) group 1 and emitted an empty function name
// instead of falling back to the full match (diff_driver.c:455).
public sealed class RegexAdapterUnmatchedGroupRegressionTests
{
    [Fact]
    public void UnmatchedGroup_IsReportedAsUnset()
    {
        using var regex = RegexAdapter.Compile("^def");
        Span<RegexMatch> matches = stackalloc RegexMatch[2];

        Assert.True(regex.Search("define x", matches));

        // Full match participated.
        Assert.False(matches[0].IsUnset);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(3, matches[0].End);

        // Group 1 did not participate — must be (-1,-1)/IsUnset, not (0,0).
        Assert.True(matches[1].IsUnset);
        Assert.Equal(-1, matches[1].Start);
        Assert.Equal(-1, matches[1].End);
    }

    [Fact]
    public void NonexistentGroup_IsReportedAsUnset()
    {
        using var regex = RegexAdapter.Compile("^(def)");
        Span<RegexMatch> matches = stackalloc RegexMatch[3];

        Assert.True(regex.Search("define x", matches));

        Assert.Equal(0, matches[1].Start);
        Assert.Equal(3, matches[1].End);

        // Index 2 is beyond the pattern's group count.
        Assert.True(matches[2].IsUnset);
        Assert.Equal(-1, matches[2].Start);
        Assert.Equal(-1, matches[2].End);
    }

    [Fact]
    public void ParticipatingGroup_KeepsOffsets()
    {
        using var regex = RegexAdapter.Compile("^def(ine)?");
        Span<RegexMatch> matches = stackalloc RegexMatch[2];

        Assert.True(regex.Search("define", matches));
        Assert.False(matches[1].IsUnset);
        Assert.Equal(3, matches[1].Start);
        Assert.Equal(6, matches[1].End);
    }

    [Fact]
    public void UnsetGroups_AfterNoMatch_AreUnset()
    {
        using var regex = RegexAdapter.Compile("^def");
        Span<RegexMatch> matches = stackalloc RegexMatch[2];

        Assert.False(regex.Search("xyz", matches));
        Assert.True(matches[0].IsUnset);
        Assert.True(matches[1].IsUnset);
    }

    [Fact]
    public void FuncnameExtractor_FallsBackToFullMatch()
    {
        // End-to-end: the diff-driver pattern extractor must pick the full
        // match when group 1 is unset (pattern without a capture group).
        // Mirrors diff_driver.c:455 (pmatch[1].start >= 0 ? group1 : full).
        using var regex = RegexAdapter.Compile("^func");
        Span<RegexMatch> matches = stackalloc RegexMatch[2];
        Assert.True(regex.Search("func main() {", matches));

        int idx = !matches[1].IsUnset ? 1 : 0;
        Assert.Equal(0, idx);
        Assert.Equal(0, matches[idx].Start);
        Assert.Equal(4, matches[idx].End);
    }
}

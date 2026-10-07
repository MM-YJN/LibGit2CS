using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public sealed class PathSpecTests
{
    [Fact]
    public void New_EmptyPatterns_MatchesEverything()
    {
        var ps = GitPathSpec.New(Array.Empty<string>());
        Assert.True(ps.IsEmpty);
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "anything"));
    }

    [Fact]
    public void New_SinglePattern_MatchesExactPath()
    {
        var ps = GitPathSpec.New("foo.txt");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo.txt"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "bar.txt"));
    }

    [Fact]
    public void New_WildcardPattern_MatchesGlob()
    {
        var ps = GitPathSpec.New("*.txt");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo.txt"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "bar.txt"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo.cs"));
    }

    [Fact]
    public void New_NegativePattern_ExcludesPath()
    {
        var ps = GitPathSpec.New("!foo.txt", "*.txt");
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo.txt"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "bar.txt"));
    }

    [Fact]
    public void New_DirectoryPattern_MatchesDirectoryContents()
    {
        var ps = GitPathSpec.New("src/");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "src/foo.c"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "src/sub/bar.c"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "test/foo.c"));
    }

    [Fact]
    public void NoGlob_DisablesWildcards()
    {
        var ps = GitPathSpec.New("*.txt");
        // With NoGlob, only exact string " "*.txt" matches.
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.NoGlob, "*.txt"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.NoGlob, "foo.txt"));
    }

    [Fact]
    public void IgnoreCase_MatchesCaseInsensitively()
    {
        var ps = GitPathSpec.New("foo.txt");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.IgnoreCase, "FOO.TXT"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.IgnoreCase, "Foo.Txt"));
    }

    [Fact]
    public void UseCase_ForcesCaseSensitive()
    {
        var ps = GitPathSpec.New("foo.txt");
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.UseCase, "FOO.TXT"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.UseCase, "foo.txt"));
    }

    [Fact]
    public void Prefix_ComputesCommonNonWildcardPrefix()
    {
        var ps = GitPathSpec.New("src/foo", "src/bar");
        Assert.Equal("src/", ps.Prefix.ToUtf8String());
    }

    [Fact]
    public void Prefix_NoCommonPrefix_ReturnsNull()
    {
        var ps = GitPathSpec.New("foo", "bar");
        // Prefix is now GitPath (byte-faithful); empty == default, not null.
        Assert.True(ps.Prefix.IsEmpty);
    }

    [Fact]
    public void Prefix_WildcardInPrefix_TruncatesAtWildcard()
    {
        var ps = GitPathSpec.New("src/*.c");
        Assert.Equal("src/", ps.Prefix.ToUtf8String());
    }

    [Fact]
    public void MatchPaths_ReturnsMatchedPaths()
    {
        var ps = GitPathSpec.New("*.txt");
        string[] paths = new[] { "foo.txt", "bar.cs", "baz.txt", "qux.py" };
        GitPathSpecMatchList result = ps.MatchPaths(paths, GitPathSpec.MatchFlags.Default);

        Assert.Equal(2, result.EntryCount);
        Assert.Contains("foo.txt", result.Entries);
        Assert.Contains("baz.txt", result.Entries);
    }

    [Fact]
    public void MatchPaths_FindFailures_TracksUnmatchedPatterns()
    {
        var ps = GitPathSpec.New("*.txt", "missing.c");
        string[] paths = new[] { "foo.txt" };
        GitPathSpecMatchList result = ps.MatchPaths(paths, GitPathSpec.MatchFlags.FindFailures);

        Assert.Equal(1, result.EntryCount);
        Assert.Equal(1, result.FailedEntryCount);
        Assert.Equal("missing.c", result.GetFailedEntry(0));
    }

    [Fact]
    public void MatchPaths_StarStar_MatchesRecursively()
    {
        var ps = GitPathSpec.New("**/foo");
        // **/foo matches paths with a directory prefix ending in /foo.
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "a/foo"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "a/b/foo"));
    }

    [Fact]
    public void New_MatchAllPattern_MatchesEverything()
    {
        var ps = GitPathSpec.New("*");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "anything"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "path/to/file"));
    }

    [Fact]
    public void New_MultiplePatterns_MatchesAny()
    {
        var ps = GitPathSpec.New("foo", "bar", "baz");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "bar"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "baz"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "qux"));
    }

    [Fact]
    public void New_ParentDirectoryPath_MatchesChildren()
    {
        // A pattern without a trailing slash that matches a directory also
        // matches files beneath that directory.
        var ps = GitPathSpec.New("src");
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "src"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "src/foo.c"));
    }
}

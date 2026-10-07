using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Regression tests for the pathspec/iterator parity behaviors in
/// libgit2 1.9.4. Expected values were differentially verified against the C reference (libgit2
/// 1.9.4):
///
/// <list type="bullet">
/// <item>pathspec.c:161-166: a negative pattern matches a path
/// literally named <c>!&lt;pattern&gt;</c> — the C# guard
/// <c>pathSpan.Length &gt; match.Pattern.Length + 1</c> made the exact-name
/// branch dead. C: pathspec ["!foo"] matches path "!foo" (1) and "!foo/bar"
/// (1), excludes "foo" and "foo/bar" (0).</item>
/// <item>iterator.c:302-356: <c>iterator_pathlist_search</c>
/// keeps entries <c>p</c> where <i>p</i> starts with the walked path
/// (icase-aware); the C# <c>ComparePrefix(path, p)</c> had the arguments
/// flipped (and was case-sensitive), so pathlist filtering never descended
/// parent directories: pathlist ["foo/bar"] + directory "foo" must yield
/// IS_PARENT, not NONE.</item>
/// </list>
/// </summary>
public sealed class PathSpecParityTests
{
    // ── negative-pattern exact-name match ──

    [Fact]
    public void NegativePattern_BangPrefixedPath_ExactNameMatches()
    {
        // C-verified: git_pathspec_matches_path(["!foo"], "!foo") == 1 —
        // the negation is inverted for a file literally named "!foo".
        var ps = GitPathSpec.New("!foo");

        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "!foo"));
        Assert.True(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "!foo/bar"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo"));
        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "foo/bar"));
    }

    [Fact]
    public void PositivePattern_BangPrefixedPath_DoesNotMatch()
    {
        // C-verified: pathspec ["foo"] does not match path "!foo".
        var ps = GitPathSpec.New("foo");

        Assert.False(ps.MatchesPath(GitPathSpec.MatchFlags.Default, "!foo"));
    }

    // ── pathlist parent-directory descent ──

    /// <summary>
    /// A filesystem iterator restricted to pathlist ["foo/bar.txt"] must
    /// descend into "foo" (IS_PARENT) and yield "foo/bar.txt". The flipped
    /// prefix comparison returned NONE for "foo", so the whole subtree was
    /// skipped.
    /// </summary>
    [Fact]
    public async Task FilesystemIterator_PathList_NestedEntryIsYielded()
    {
        string root = Path.Combine(Path.GetTempPath(), "libgit2cs-pathlist-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "foo"));
            await File.WriteAllTextAsync(Path.Combine(root, "foo", "bar.txt"), "bar\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "other.txt"), "other\n", TestContext.Current.CancellationToken);

            var options = new IteratorOptions
            {
                PathList = [GitPath.FromUtf8String("foo/bar.txt")],
            };
            using IIterator iter = await FilesystemIterator.ForFilesystemAsync(root, options, TestContext.Current.CancellationToken);

            var paths = new List<string>();
            while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
            {
                paths.Add(entry.Path.ToUtf8String());
            }

            Assert.Contains("foo/bar.txt", paths);
            Assert.DoesNotContain("other.txt", paths);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}

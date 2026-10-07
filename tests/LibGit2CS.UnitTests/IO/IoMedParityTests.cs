using LibGit2CS.Index;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Parity tests for io-paths: (wildmatch [:punct:]/[:space:] libc ctype), (NTFS.gitmodules shortname + fallback-hash gitfile
/// forms), (PathlistNextIs trailing-slash entries must not match the bare file). </summary>
public sealed class IoMedParityTests
{
    private const ushort SymlinkMode = 0xA000; // S_IFLNK

    // ── wildmatch POSIX classes use libc ctype, not sane_ctype ──

    [Theory]
    [InlineData("[[:punct:]]", "*")]
    [InlineData("[[:punct:]]", "?")]
    [InlineData("[[:punct:]]", "[")]
    [InlineData("[[:punct:]]", "\\")]
    [InlineData("[[:punct:]]", "]")]
    [InlineData("[[:punct:]]", "_")]
    [InlineData("[[:punct:]]", "{")]
    [InlineData("[[:punct:]]", "~")]
    [InlineData("[[:punct:]]", "!")]
    [InlineData("[[:punct:]]", ".")]
    public void WildMatch_PunctClass_MatchesLibcIspunct(string pattern, string text)
    {
        // libc ispunct in the C locale: printable non-alnum non-space —
        // including the glob specials * ? [ \ (probe-confirmed vs C).
        Assert.True(WildMatch.IsMatch(pattern, text));
    }

    [Theory]
    [InlineData("[[:punct:]]", "a")]
    [InlineData("[[:punct:]]", "0")]
    [InlineData("[[:punct:]]", " ")]
    [InlineData("[[:punct:]]", "\t")]
    public void WildMatch_PunctClass_RejectsNonPunct(string pattern, string text)
    {
        Assert.False(WildMatch.IsMatch(pattern, text));
    }

    [Theory]
    [InlineData("[[:space:]]", "\t")]
    [InlineData("[[:space:]]", "\n")]
    [InlineData("[[:space:]]", "\v")] // vertical tab: libc isspace, not sane_ctype
    [InlineData("[[:space:]]", "\f")] // form feed: libc isspace, not sane_ctype
    [InlineData("[[:space:]]", "\r")]
    [InlineData("[[:space:]]", " ")]
    public void WildMatch_SpaceClass_MatchesLibcIsspace(string pattern, string text)
    {
        Assert.True(WildMatch.IsMatch(pattern, text));
    }

    [Fact]
    public void WildMatch_SpaceClass_RejectsNonSpace()
    {
        Assert.False(WildMatch.IsMatch("[[:space:]]", "a"));
    }

    // Byte-based matcher must agree with the char-based one.

    [Fact]
    public void WildMatch_ByteMatcher_PunctAndSpace_AgreeWithLibc()
    {
        Assert.True(WildMatch.IsMatch("[[:punct:]]"u8, "*"u8));
        Assert.True(WildMatch.IsMatch("[[:punct:]]"u8, "["u8));
        Assert.False(WildMatch.IsMatch("[[:punct:]]"u8, "a"u8));
        Assert.True(WildMatch.IsMatch("[[:space:]]"u8, "\v"u8));
        Assert.True(WildMatch.IsMatch("[[:space:]]"u8, "\f"u8));
        Assert.False(WildMatch.IsMatch("[[:space:]]"u8, "a"u8));
    }

    // ── NTFS.gitmodules symlink gitfile forms ──

    [Theory]
    [InlineData(".gitmodules")]   // literal (polarity must match C: rejected)
    [InlineData(".GITMODULES")]
    [InlineData("gitmod~1")]      // 8.3 shortname
    [InlineData("GITMOD~1")]
    [InlineData("gitmod~4")]
    [InlineData("gitmod~1 ")]     // trailing space is end-of-filename
    [InlineData("gi7eba~1")]      // fallback hash prefix
    [InlineData("GI7EBA~1")]
    [InlineData("gi7eba~9")]
    public async Task IsValid_NtfsGitmodulesSymlinkForms_Rejected(string name)
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            name, GitPathRejectFlags.DotGitNtfs, fileMode: SymlinkMode,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(".gitmodule")]    // too short for the literal
    [InlineData(".gitmodulesx")]  // trailing garbage after the literal
    [InlineData("gitmod~5")]      // 8.3 shortnames only cover ~1..~4
    [InlineData("gitmod~12")]
    [InlineData("gi7eba")]        // no tilde
    [InlineData("gi7ebaX")]
    [InlineData("gi7eba~0")]      // fallback tilde needs 1..9
    [InlineData("gi7eba~12")]     // extra digit after the 8-char fallback form
    [InlineData("foo~1")]
    public async Task IsValid_NtfsGitmodulesLookalikes_Accepted(string name)
    {
        Assert.True(await GitPathValidator.IsValidAsync(
            name, GitPathRejectFlags.DotGitNtfs, fileMode: SymlinkMode,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_NtfsGitmodules_NonSymlink_Accepted()
    {
        // The gitfile check only applies to symlinks (S_ISLNK gate).
        Assert.True(await GitPathValidator.IsValidAsync(
            "gitmod~1", GitPathRejectFlags.DotGitNtfs, fileMode: 0,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── PathlistNextIs trailing-slash semantics ──

    [Theory]
    [InlineData("foo/", "foo")]     // tree blob named foo must NOT match entry "foo/"
    [InlineData("foo/", "foo/")]    // directory foo does match
    [InlineData("foo/", "foo/bar")] // directory prefix matches
    [InlineData("foo", "foo")]      // bare entry matches the file
    [InlineData("foo", "foo/")]     // bare entry matches the directory
    [InlineData("foo", "foo/bar")]  // bare entry matches the prefix
    public void PathlistNextIs_TrailingSlash_MatchesDirectoriesOnly(string entry, string path)
    {
        bool expected = entry != "foo/" || path != "foo";
        Assert.Equal(expected, PathlistNextIs(entry, path));
    }

    /// <summary>Invokes the internal <c>PathlistNextIs</c> through a test subclass.</summary>
    private static bool PathlistNextIs(string entry, string path)
    {
        var iter = new PathlistTestIterator([GitPath.FromUtf8String(entry)]);
        return iter.NextIs(GitPath.FromUtf8String(path));
    }

    /// <summary>Exposes the protected <c>PathlistNextIs</c> for testing.</summary>
    private sealed class PathlistTestIterator : IteratorBase
    {
        public PathlistTestIterator(GitPath[] pathList)
            : base(IteratorType.Tree, new IteratorOptions { PathList = pathList })
        {
        }

        public bool NextIs(GitPath path) => PathlistNextIs(path);

        public override ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<GitIndexEntry?> AdvanceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<GitIndexEntry?> AdvanceIntoAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask ResetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

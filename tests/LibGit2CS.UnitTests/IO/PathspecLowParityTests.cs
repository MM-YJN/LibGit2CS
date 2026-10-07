using System.IO.Pipes;

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

// Parity tests for the empty pathspec, FIND_FAILURES mark-remaining, NO_MATCH_ERROR, negative-match listing, workdir iterator skipping FIFOs/sockets,
// Basename("") = ".", flags==0 accepting NUL, IsAbsolute backslash on POSIX, MakeRelative "../" depth + parent error, and the
// WriteAtomicAsync create-leading-dirs opt-out.
public sealed class PathspecLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public PathspecLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PathspecLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    // ── GitPathSpec.Match/MatchPaths semantics ────────────────────────

    [Fact]
    public void MatchPaths_EmptyPathspec_MatchesNothing()
    {
        // C (pathspec.c:173-193): an empty pathspec matches NOTHING
        // (git_pathspec__match_at leaves result at GIT_ENOTFOUND).
        var ps = GitPathSpec.New(Array.Empty<GitPath>());
        GitPathSpecMatchList result = ps.MatchPaths(
            [GitPath.FromUtf8String("a.txt"), GitPath.FromUtf8String("b.txt")],
            GitPathSpec.MatchFlags.Default);

        Assert.Equal(0, result.EntryCount);
        Assert.Equal(0, result.FailedEntryCount);
    }

    [Fact]
    public void MatchPaths_FindFailures_MarksAllLaterMatchingPatterns()
    {
        // C (pathspec.c:460-464): with FIND_FAILURES, every LATER pattern that
        // also matches the path is marked used (pathspec_mark_remaining), so
        // neither "a*" nor "ab*" is a failure for path "abc".
        var ps = GitPathSpec.New("a*", "ab*");
        GitPathSpecMatchList result = ps.MatchPaths(
            [GitPath.FromUtf8String("abc")],
            GitPathSpec.MatchFlags.FindFailures);

        Assert.Equal(1, result.EntryCount);
        Assert.Equal(0, result.FailedEntryCount);
    }

    [Fact]
    public void MatchPaths_FindFailures_UnmatchedPatternIsAFailure()
    {
        var ps = GitPathSpec.New("a*", "zz*");
        GitPathSpecMatchList result = ps.MatchPaths(
            [GitPath.FromUtf8String("abc")],
            GitPathSpec.MatchFlags.FindFailures);

        Assert.Equal(1, result.FailedEntryCount);
        Assert.Equal("zz*", result.GetFailedEntryPath(0).ToUtf8String());
    }

    [Fact]
    public void MatchPaths_NegativePatternMatch_DoesNotListTheFile()
    {
        // C (pathspec.c:449-453): a NEGATIVE pattern match (result == 0)
        // marks the pattern used but does NOT list the file.
        var ps = GitPathSpec.New("!a*");
        GitPathSpecMatchList result = ps.MatchPaths(
            [GitPath.FromUtf8String("abc")],
            GitPathSpec.MatchFlags.Default);

        Assert.Equal(0, result.EntryCount);
    }

    [Fact]
    public void MatchPaths_NoMatchError_ThrowsNotFound()
    {
        // C (pathspec.c:479-483): NO_MATCH_ERROR with zero matched files →
        // GIT_ENOTFOUND "no matching files were found".
        var ps = GitPathSpec.New("*.txt");
        GitException ex = Assert.Throws<GitException>(() => ps.MatchPaths(
            [GitPath.FromUtf8String("a.bin")],
            GitPathSpec.MatchFlags.NoMatchError));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("no matching files were found", ex.Message);
    }

    // ── workdir iterator skips FIFOs/sockets ──────────────────────────

    [Fact]
    public async Task FilesystemIterator_SkipsSpecialFiles()
    {
        // C (iterator.c:1448-1451): FIFOs/sockets/devices are skipped
        // (!S_ISDIR && !S_ISREG && !S_ISLNK → continue). On Unix a
        // NamedPipeServerStream creates a socket file at the given path.
        string root = Path.Combine(_tempDir, "iter_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        await using var pipe = new NamedPipeServerStream(
            Path.Combine(root, "fifo"),
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using IIterator iter = await FilesystemIterator.ForFilesystemAsync(root, options: null, TestContext.Current.CancellationToken);
        var paths = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            paths.Add(entry.Path.ToUtf8String());
        }

        Assert.Contains("file.txt", paths);
        Assert.DoesNotContain("fifo", paths);
    }

    // ── Basename("") = "." ────────────────────────────────────────────

    [Fact]
    public void Basename_EmptyPath_ReturnsDot()
    {
        // C (fs_path.c:104-114): NULL/empty paths are treated as ".".
        GitPath basename = PathByteHelpers.Basename([]);
        Assert.Equal(".", basename.ToUtf8String());
    }

    // ── flags==0 accepts anything (incl. NUL) ─────────────────────────

    [Fact]
    public async Task Validator_NoFlags_AcceptsEmbeddedNul()
    {
        // C (fs_path.c:1711-1712): with no reject flags every string is
        // valid — even one with an embedded NUL.
        var path = GitPath.FromUtf8Bytes(new byte[] { (byte)'a', 0, (byte)'b' });
        Assert.True(await GitPathValidator.IsValidAsync(path, GitPathRejectFlags.None, repo: null, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── IsAbsolute backslash on POSIX ─────────────────────────────────

    [Fact]
    public void IsAbsolute_BackslashPath_IsNotAbsoluteOnPosix()
    {
        // C (fs_path.c:283-311): git_fs_path_root returns -1 for "\foo" on
        // POSIX (backslash is not a root there).
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(PathHelpers.IsAbsolute("\\foo"));
        }
    }

    // ── MakeRelative "../" depth + parent error ───────────────────────

    [Fact]
    public void MakeRelative_SiblingPath_UsesDotDotDepth()
    {
        // C (fs_path.c:946-1005): a path diverging mid-component gets
        // "../" * depth + the remainder ("c" -> "../other/pack-x.idx").
        Assert.Equal("../other/pack-x.idx", PathHelpers.MakeRelative("/a/b/other/pack-x.idx", "/a/b/c"));
    }

    [Fact]
    public void MakeRelative_ParentPrefix_ReturnsPlainRemainder()
    {
        // C: when the parent is a strict prefix ending at a separator, the
        // remainder is returned as-is (fs_path.c:1003-1004).
        Assert.Equal("other/pack-x.idx", PathHelpers.MakeRelative("/a/b/other/pack-x.idx", "/a/b"));
    }

    [Fact]
    public void MakeRelative_NoCommonSegment_Throws()
    {
        // C: no leading-slash common prefix -> "%s is not a parent of %s"
        // (GIT_ENOTFOUND, fs_path.c:955-960).
        GitException ex = Assert.Throws<GitException>(() => PathHelpers.MakeRelative("x/y", "a/b"));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("is not a parent of", ex.Message);
    }

    // ── WriteAtomicAsync leading-dir opt-out ──────────────────────────

    [Fact]
    public async Task WriteAtomic_NoLeadingDirs_MissingParentThrowsNotFound()
    {
        // C (filebuf.c:54-59, futils.c:80-81): without
        // GIT_FILEBUF_CREATE_LEADING_DIRS a missing parent fails with
        // GIT_ENOTFOUND instead of creating the directory.
        string target = Path.Combine(_tempDir, "missing_dir", "file.txt");
        GitException ex = await Assert.ThrowsAsync<GitException>(() =>
            AsyncFileIO.WriteAtomicAsync(target, "x"u8.ToArray(), createLeadingDirs: false, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task WriteAtomic_WithLeadingDirs_CreatesParent()
    {
        string target = Path.Combine(_tempDir, "made_dir", "file.txt");
        await AsyncFileIO.WriteAtomicAsync(target, "x"u8.ToArray(), createLeadingDirs: true, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(target));
    }
}

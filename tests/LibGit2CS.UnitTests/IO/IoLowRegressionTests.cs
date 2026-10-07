using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.IO;

// Parity cases verified against libgit2 1.9.4:
//  - GIT_FS_PATH_REJECT_LONG_PATHS is never enforced on Windows —
//    C's git_fs_path_str_is_valid_ext applies validate_length (UTF-8 char
//    count <= MAX_PATH 260) under GIT_WIN32 when the flag is set
//    (fs_path.c:1750-1756).
//  - WorkdirReader.GetFileMode never returned Symlink — a symlink
//    fell through to the executable-bit test (Executable) or the Directory
//    bit (Tree), while C's workdir_reader_read uses p_lstat +
//    git_futils_canonical_mode which yields GIT_FILEMODE_LINK (0120000).
//  - JoinPathList always trimmed the second part's leading separators
//    and inserted a separator even when the first part was empty; C's
//    git_str_join (str.c:760-800) skips b's leading separators and inserts
//    a separator only when strlen_a > 0.
public sealed class IoLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public IoLowRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_IoLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ---- LongPaths enforcement on Windows ----

    [Fact]
    public async Task IsValid_LongPaths_RejectsOver260Utf8Chars_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // C enforces MAX_PATH only under GIT_WIN32 (fs_path.c:1750-1756)
        }

        // 261 'a' chars — over MAX_PATH.
        string longPath = new('a', 261);
        Assert.False(await GitPathValidator.IsValidAsync(
            longPath, GitPathRejectFlags.LongPaths,
            cancellationToken: TestContext.Current.CancellationToken));

        // 260 chars — at the limit, still valid.
        string atLimit = new('a', 260);
        Assert.True(await GitPathValidator.IsValidAsync(
            atLimit, GitPathRejectFlags.LongPaths,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_LongPaths_IgnoredOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // On POSIX the flag is cleared (no length limit) — a 1000-char path
        // is still valid.
        string longPath = new('a', 1000);
        Assert.True(await GitPathValidator.IsValidAsync(
            longPath, GitPathRejectFlags.LongPaths,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // ---- WorkdirReader symlink mode ----

    [Fact]
    public async Task ReadAsync_SymlinkToFile_ReportsSymlinkMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation needs privileges
        }

        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "target.txt"), "hello",
            cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            File.CreateSymbolicLink(Path.Combine(repoPath, "link"), "target.txt");
        }
        catch (IOException)
        {
            return; // filesystem without symlink support
        }

        var reader = new WorkdirReader(repo, validateIndex: false);
        ReaderReadResult result = await reader.ReadAsync(
            GitPath.FromUtf8String("link"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFound);
        // C: p_lstat + git_futils_canonical_mode → GIT_FILEMODE_LINK. Reporting
        // Executable would be wrong (symlink mode 0777 has UserExecute).
        Assert.Equal(GitFileMode.Symlink, result.Result!.Mode);
    }

    [Fact]
    public async Task ReadAsync_RegularFile_StillReportsRegularMode()
    {
        string repoPath = Path.Combine(_tempDir, "repo2");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "plain.txt"), "hello",
            cancellationToken: TestContext.Current.CancellationToken);

        var reader = new WorkdirReader(repo, validateIndex: false);
        ReaderReadResult result = await reader.ReadAsync(
            GitPath.FromUtf8String("plain.txt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFound);
        Assert.Equal(GitFileMode.Regular, result.Result!.Mode);
    }

    // ---- JoinPathList empty-first-part semantics ----

    private static char Sep => OperatingSystem.IsWindows() ? ';' : ':';

    [Fact]
    public void Set_PathMagic_EmptyBeforePart_KeepsOldVerbatim()
    {
        // C git_str_join: with an empty first part no separator is inserted
        // and the second part's leading separators are kept. Producing
        // ":/etc" for Set("$PATH") over "/etc" would be wrong.
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/etc");
        ctx.Dirs.Set(GitSystemDir.System, "$PATH");

        Assert.Equal("/etc", ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void Set_PathMagic_EmptyBeforePart_AfterKeepsSeparator()
    {
        // Set("$PATH:") over "/etc" → "/etc:" (not ":/etc:").
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/etc");
        ctx.Dirs.Set(GitSystemDir.System, "$PATH" + Sep);

        Assert.Equal("/etc" + Sep, ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void Set_PathMagic_OldEndsWithSeparator_NoDoubleSeparator()
    {
        // Set("$PATH") over "/etc:" → "/etc:" (not ":/etc:").
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/etc" + Sep);
        ctx.Dirs.Set(GitSystemDir.System, "$PATH");

        Assert.Equal("/etc" + Sep, ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void Set_PathMagic_NonEmptyBefore_JoinsWithSeparator()
    {
        // Control: with a non-empty before part the separator is inserted
        // and the old value's leading separator is skipped.
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/etc");
        ctx.Dirs.Set(GitSystemDir.System, "/usr" + Sep + "$PATH");

        Assert.Equal("/usr" + Sep + "/etc", ctx.Dirs.Get(GitSystemDir.System));
    }
}

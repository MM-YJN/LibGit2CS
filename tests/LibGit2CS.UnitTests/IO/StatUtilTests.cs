using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Tests for <see cref="StatUtil"/> native stat reads — verifies that real
/// dev/ino/uid/gid and nanosecond mtime are read on platforms that support
/// them (Linux/macOS), and that the BCL fallback works on all platforms.
/// </summary>
public sealed class StatUtilTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StatUtilTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StatUtil_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task GetStatInfo_RegularFile_ReturnsNonZeroIdentityFields_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string filePath = Path.Combine(_repo.Workdir!, "test.txt");
        await File.WriteAllTextAsync(filePath, "hello\n", cancellationToken: TestContext.Current.CancellationToken);

        var fi = new FileInfo(filePath);
        (_, _, uint dev, uint ino, uint uid, uint gid, uint size) =
            StatUtil.GetStatInfo(fi);

        // On Unix, dev/ino/uid/gid should be non-zero for a real file.
        Assert.NotEqual(0u, dev);
        Assert.NotEqual(0u, ino);
        Assert.NotEqual(0u, uid);
        Assert.NotEqual(0u, gid);
        Assert.Equal(6u, size);
    }

    [Fact]
    public async Task GetStatInfo_RegularFile_ReturnsCorrectSize()
    {
        string filePath = Path.Combine(_repo.Workdir!, "sizetest.txt");
        string content = new('x', 12345);
        await File.WriteAllTextAsync(filePath, content, cancellationToken: TestContext.Current.CancellationToken);

        var fi = new FileInfo(filePath);
        (_, _, _, _, _, _, uint size) = StatUtil.GetStatInfo(fi);

        Assert.Equal(12345u, size);
    }

    [Fact]
    public async Task GetStatInfo_NanosecondMtime_NotTruncatedTo100ns_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string filePath = Path.Combine(_repo.Workdir!, "ns_test.txt");
        await File.WriteAllTextAsync(filePath, "data\n", cancellationToken: TestContext.Current.CancellationToken);

        // Use touch with nanosecond precision to set a sub-100ns mtime.
        // 963260382 ns is a sub-100ns fractional second (a real-world example).
        var psi = new System.Diagnostics.ProcessStartInfo("touch", $"-d @1786077514.963260382 \"{filePath}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = System.Diagnostics.Process.Start(psi);
        await proc!.WaitForExitAsync(TestContext.Current.CancellationToken);

        var fi = new FileInfo(filePath);
        (_, IndexTime mtime, _, _, _, _, _) = StatUtil.GetStatInfo(fi);

        // The native statx should return the full nanosecond precision.
        // If truncated to 100ns, we'd get 963260300 instead of 963260382.
        Assert.Equal(1786077514, mtime.Seconds);
        Assert.Equal(963260382u, mtime.Nanoseconds);
    }

    [Fact]
    public async Task GetStatInfo_Directory_ReturnsTreeMode()
    {
        string dirPath = Path.Combine(_repo.Workdir!, "subdir");
        Directory.CreateDirectory(dirPath);

        var di = new DirectoryInfo(dirPath);
        GitFileMode mode = StatUtil.GetFileMode(di);

        Assert.Equal(GitFileMode.Tree, mode);
    }

    [Fact]
    public async Task GetStatInfo_ExecutableFile_ReturnsExecutableMode_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string filePath = Path.Combine(_repo.Workdir!, "script.sh");
        await File.WriteAllTextAsync(filePath, "#!/bin/sh\necho hi\n", cancellationToken: TestContext.Current.CancellationToken);

        var fi = new FileInfo(filePath);
        fi.UnixFileMode |= UnixFileMode.UserExecute;

        GitFileMode mode = StatUtil.GetFileMode(fi);

        Assert.Equal(GitFileMode.Executable, mode);
    }

    // ── Status cache regression: clean file should not be rehashed ───────

    [Fact]
    public async Task Status_CleanFile_SecondCallDoesNotRehash()
    {
        // Commit a file, then run status twice. The second call should hit
        // the stat cache (the file hasn't changed) and report Unmodified
        // without rehashing. With the old zero-stat bug, every file would be
        // marked Modified (forcing a rehash) on every call.
        string filePath = Path.Combine(_repo.Workdir!, "clean.txt");
        await File.WriteAllTextAsync(filePath, "unchanged content\n", cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("clean.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);

        var sig = new GitSignature("Test", "test@example.com", new GitTime(1700000000, 0));
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "add clean.txt\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // First status call — should report clean (no changes).
        var opts = new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUnmodified,
        };

        GitStatusList list1 = await GitStatusList.NewAsync(_repo, opts, TestContext.Current.CancellationToken);
        try
        {
            GitStatusEntry? entry1 = list1.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "clean.txt");
            Assert.NotNull(entry1);
            Assert.Equal(GitStatusFlags.Current, entry1!.Status);
        }
        finally
        {
            list1.Dispose();
        }

        // Second status call — should also report clean. If the stat cache
        // is broken (identity fields always 0, mtime truncated), the file
        // would be marked Modified, forcing a content rehash.
        GitStatusList list2 = await GitStatusList.NewAsync(_repo, opts, TestContext.Current.CancellationToken);
        try
        {
            GitStatusEntry? entry2 = list2.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "clean.txt");
            Assert.NotNull(entry2);
            Assert.Equal(GitStatusFlags.Current, entry2!.Status);
        }
        finally
        {
            list2.Dispose();
        }
    }
}

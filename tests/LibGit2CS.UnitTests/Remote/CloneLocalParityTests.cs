using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Regression tests for the clone-local parity behavior in
/// libgit2 1.9.4.
/// C: git_clone__should_clone_local (clone.c:583-594) — under
/// GIT_CLONE_LOCAL_AUTO any URL (even file://) is a remote clone.
/// </summary>
public sealed class CloneLocalParityTests : IDisposable
{
    private readonly string _tempDir;

    public CloneLocalParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CloneLocalParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A file:// URL for <paramref name="path"/>. On Windows the local file
    /// URL form is <c>file:///C:/path</c> (the path starts just past the
    /// third slash); on POSIX it is <c>file:///path</c>.
    /// </summary>
    private static string FileUrl(string path)
        => OperatingSystem.IsWindows()
            ? "file:///" + path.Replace('\\', '/')
            : "file://" + path;

    [Fact]
    public void Auto_FileUrl_IsRemote()
    {
        // C: AUTO → return 0 for ANY url (clone.c:586-588).
        Assert.False(GitClone.ShouldCloneLocal(FileUrl(_tempDir), GitCloneLocal.Auto));
    }

    [Fact]
    public void Auto_HttpUrl_IsRemote()
    {
        Assert.False(GitClone.ShouldCloneLocal("https://example.com/repo.git", GitCloneLocal.Auto));
    }

    [Fact]
    public void Local_FileUrl_IsLocal()
    {
        Assert.True(GitClone.ShouldCloneLocal(FileUrl(_tempDir), GitCloneLocal.Local));
    }

    [Fact]
    public void Auto_Path_IsLocal()
    {
        // C: non-URL inputs are local when the path is a directory.
        Assert.True(GitClone.ShouldCloneLocal(_tempDir, GitCloneLocal.Auto));
    }

    [Fact]
    public void NoLocal_Anything_IsRemote()
    {
        Assert.False(GitClone.ShouldCloneLocal(_tempDir, GitCloneLocal.NoLocal));
        Assert.False(GitClone.ShouldCloneLocal(FileUrl(_tempDir), GitCloneLocal.NoLocal));
    }
}

using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Remote;

/// <summary>
/// End-to-end tests for the clone-local parity behavior in
/// libgit2 1.9.4.
/// </summary>
public sealed class CloneLocalParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public CloneLocalParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CloneLocalParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    [Fact]
    public void Auto_FileUrl_IsRemote_LikeC()
    {
        Assert.False(GitClone.ShouldCloneLocal(FileUrl(_tempDir), GitCloneLocal.Auto));
        Assert.True(GitClone.ShouldCloneLocal(FileUrl(_tempDir), GitCloneLocal.Local));
        Assert.True(GitClone.ShouldCloneLocal(_tempDir, GitCloneLocal.Auto));
    }
}

using System.Reflection;

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

namespace LibGit2CS.UnitTests.Submodule;

// CloneAsync's
// IsNetworkUrl recognizes scp-style SSH URLs
// (git@host:path — supported by GitSshUrl.IsScpStyle and
// C's transport_find_fn strrchr(url,':') → ssh), which must take the network
// path, not the local object-copy fast path.
// AddToIndexAsync must not add a zero-OID gitlink into the parent index
// when the submodule HEAD is unborn; C's git_submodule_add_to_index
// (submodule.c:1059-1064) fails "cannot add submodule without HEAD to
// index".
public sealed class SubmoduleAddRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public SubmoduleAddRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_SubmoduleAdd_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void IsNetworkUrl_ClassifiesScpStyleAndSchemes_LikeTransportFinder()
    {
        // C's transport_find_fn (transport.c:75-103): a known scheme prefix
        // wins; otherwise ANY url containing ':' is SSH (scp-style); the
        // rest are local paths.
        MethodInfo isNetworkUrl = typeof(GitSubmodule).GetMethod(
            "IsNetworkUrl", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsNetworkUrl missing");

        static bool Classify(MethodInfo m, string url)
            => (bool)m.Invoke(null, [url])!;

        // scp-style SSH URLs must be network.
        Assert.True(Classify(isNetworkUrl, "git@github.com:org/lib.git"));
        Assert.True(Classify(isNetworkUrl, "user@host:path/to/repo.git"));

        // Scheme URLs are network.
        Assert.True(Classify(isNetworkUrl, "https://example.com/x.git"));
        Assert.True(Classify(isNetworkUrl, "ssh://git@example.com/x.git"));
        Assert.True(Classify(isNetworkUrl, "git://example.com/x.git"));
        Assert.True(Classify(isNetworkUrl, "file:///path/to/repo"));

        // Genuine filesystem paths are local.
        Assert.False(Classify(isNetworkUrl, "/path/to/repo"));
        Assert.False(Classify(isNetworkUrl, "relative/path/to/repo"));
        Assert.False(Classify(isNetworkUrl, "repo"));
    }

    [Fact]
    public async Task AddToIndex_UnbornHead_ThrowsAndWritesNoZeroOidGitlink()
    {
        string repoPath = Path.Combine(_tempDir, "parent");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // A source repo for the submodule URL (AddSetup only inits the
        // gitlink repo — it does not fetch, so the submodule HEAD is unborn).
        string srcPath = Path.Combine(_tempDir, "src");
        await using GitRepository src = await GitRepository.InitAsync(
            srcPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        GitSubmodule sm = await repo.SubmoduleAddSetupAsync(
            srcPath, "sub", useGitlink: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // C (submodule.c:1059-1064): "cannot add submodule without HEAD to
        // index".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await sm.AddToIndexAsync(writeIndex: true, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Submodule, ex.Category);
        Assert.Contains("without HEAD", ex.Message);

        // The parent index must not contain the zero-OID gitlink.
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal(-1, index.Find("sub"));
    }
}

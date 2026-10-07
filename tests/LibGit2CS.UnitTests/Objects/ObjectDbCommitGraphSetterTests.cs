using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Tests for the <see cref="GitObjectDb.GetCommitGraphAsync"/> getter and
/// <see cref="GitObjectDb.SetCommitGraph"/> setter. Covers the
/// <c>git_odb_set_commit_graph</c> port (odb.c:815-830) and the single-source-of-truth
/// refactor where <see cref="GitRepository.GetCommitGraphAsync"/> delegates to
/// <see cref="GitObjectDb.GetCommitGraphAsync"/>.
/// </summary>
public sealed class ObjectDbCommitGraphSetterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public ObjectDbCommitGraphSetterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CgSetter_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string ExtractRepo()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return path;
    }

    /// <summary>
    /// The getter lazy-loads <c>objects/info/commit-graph</c> from the owning
    /// repository's path when first accessed.
    /// </summary>
    [Fact]
    public async Task CommitGraph_Getter_LazyLoadsFromRepository()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);

        // The fixture has a commit-graph file; the getter should lazy-load it.
        CommitGraph? cg = await repo.GetCommitGraphAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(cg);
        Assert.True(cg!.NumCommits > 0);
    }

    /// <summary>
    /// The setter replaces the cached instance and the getter returns the
    /// explicitly-set value (no lazy-load overwrites an explicit set).
    /// </summary>
    [Fact]
    public async Task CommitGraph_Setter_ExplicitSetOverridesLazyLoad()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);

        // Build a synthetic commit-graph and set it explicitly.
        CommitGraph? synthetic = await CommitGraph.OpenAsync(
            Path.Combine(repoPath, "objects"),
            GitHashAlgorithmKind.Sha1,
            TestContext.Current.CancellationToken);
        Assert.NotNull(synthetic); // the fixture's real commit-graph

        // The setter should store the reference; subsequent gets return it.
        repo.Objects.SetCommitGraph(synthetic);
        Assert.Same(synthetic, await repo.GetCommitGraphAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Passing <c>null</c> to the setter unsets the commit-graph (matches C's
    /// "pass NULL to unset"). The getter returns null after the set, NOT a
    /// lazy-reload (the _commitGraphChecked flag prevents re-loading).
    /// </summary>
    [Fact]
    public async Task CommitGraph_Setter_NullUnsetsAndPreventsReload()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);

        // Force the lazy-load first (proves the fixture has a commit-graph).
        Assert.NotNull(await repo.GetCommitGraphAsync(TestContext.Current.CancellationToken));

        // Unset via null. The _commitGraphChecked flag should prevent re-loading.
        repo.Objects.SetCommitGraph(null);
        Assert.Null(await repo.GetCommitGraphAsync(TestContext.Current.CancellationToken));

        // Even repeated access should not re-load (checked flag stays true).
        Assert.Null(await repo.GetCommitGraphAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A standalone ODB (no owner) returns null from the getter (no path to
    /// lazy-load from). The setter still works.
    /// </summary>
    [Fact]
    public async Task CommitGraph_StandaloneOdb_GetterReturnsNullSetterWorks()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        // No owner → getter cannot lazy-load → returns null.
        Assert.Null(await db.GetCommitGraphAsync(TestContext.Current.CancellationToken));

        // Setter still accepts a value.
        CommitGraph? synthetic = await CommitGraph.OpenAsync(_tempDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Null(synthetic); // no commit-graph file in _tempDir
        db.SetCommitGraph(synthetic);
        Assert.Null(await db.GetCommitGraphAsync(TestContext.Current.CancellationToken)); // we set null, so still null
    }
}

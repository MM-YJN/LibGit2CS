using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Tests for the two <see cref="GitRepository"/> commit-graph facades:
/// <see cref="GitRepository.HasCommitGraphAsync"/> (presence gate over the
/// ODB-owned cache) and <see cref="GitRepository.WriteCommitGraphAsync"/>
/// (<c>refs/*</c>-scoped write + cache refresh).
/// </summary>
/// <remarks>
/// <para>
/// <b>Libgit2 counterparts.</b> The write scope + byte-exactness assertions
/// mirror <c>test_graph_commitgraph__writer</c>
/// (tests/libgit2/graph/commitgraph.c), which pushes <c>refs/*</c> and
/// compares the writer dump with the fixture's pre-generated
/// <c>commit-graph</c>. The presence/corrupt-fallback behavior has no C test
/// — libgit2 has no repository-level presence API (the facade is a
/// LibGit2CS addition); the corrupt→false path exercises the same ODB
/// silent-fallback <c>test_graph_commitgraph__validate</c> relies on for
/// revwalk.
/// </para>
/// <para>
/// The testrepo fixture ships a pre-generated commit-graph covering all 15
/// commits reachable from <c>refs/*</c> (only 7 from HEAD's
/// <c>master</c>); <c>e90810b8df3e80c413d903f631643c716887138d</c> (tip of
/// <c>chomped</c>) is reachable from <c>refs/*</c> but NOT from HEAD — the
/// scope probe for the write facade.
/// </para>
/// </remarks>
public sealed class CommitGraphFacadeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public CommitGraphFacadeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CommitGraphFacade_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string GraphPath(string repoPath)
        => Path.Combine(repoPath, "objects", "info", "commit-graph");

    /// <summary>
    /// The fixture ships a valid commit-graph, so a fresh open reports
    /// presence via the ODB lazy-load.
    /// </summary>
    [Fact]
    public async Task HasCommitGraphAsync_TrueWhenGraphExists()
    {
        string repoPath = Path.Combine(ExtractRepo(), "testrepo.git");

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.True(await repo.HasCommitGraphAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// No commit-graph file → false. The lazy-load's
    /// <c>!File.Exists</c> early-return path.
    /// </summary>
    [Fact]
    public async Task HasCommitGraphAsync_FalseWhenGraphMissing()
    {
        string repoPath = Path.Combine(ExtractRepo(), "testrepo.git");
        File.Delete(GraphPath(repoPath));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.False(await repo.HasCommitGraphAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A corrupt file → false: the ODB lazy-load catches the parse
    /// <see cref="GitException"/> and silently falls back to null (matching
    /// C's revwalk fallback). The public <see cref="CommitGraph.OpenAsync"/>,
    /// by contrast, throws on the same file (mirroring C's public
    /// <c>git_commit_graph_open</c>) — the documented divergence between the
    /// boolean gate and the open API.
    /// </summary>
    [Fact]
    public async Task HasCommitGraphAsync_FalseOnCorruptFile_WhileOpenAsyncThrows()
    {
        string repoPath = Path.Combine(ExtractRepo(), "testrepo.git");
        await File.WriteAllBytesAsync(GraphPath(repoPath), [0x00, 0x01, 0x02, 0x03], TestContext.Current.CancellationToken);

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.False(await repo.HasCommitGraphAsync(TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<GitException>(
            () => CommitGraph.OpenAsync(Path.Combine(repoPath, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>
    /// The write facade over <c>refs/*</c> reproduces the fixture's
    /// pre-generated <c>commit-graph</c> byte-for-byte (the counterpart
    /// of <c>test_graph_commitgraph__writer</c>'s equivalence assertion), and
    /// <see cref="GitRepository.HasCommitGraphAsync"/> flips to true on the
    /// SAME repository handle — the ODB cache-refresh regression (without the
    /// reset, the write would be invisible until a fresh open).
    /// </summary>
    [Fact]
    public async Task WriteCommitGraphAsync_ByteExactAndRefreshesCacheOnSameHandle()
    {
        string repoPath = Path.Combine(ExtractRepo(), "testrepo.git");
        byte[] expected = await File.ReadAllBytesAsync(GraphPath(repoPath), TestContext.Current.CancellationToken);
        File.Delete(GraphPath(repoPath));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.False(await repo.HasCommitGraphAsync(TestContext.Current.CancellationToken));

        await repo.WriteCommitGraphAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(GraphPath(repoPath)));
        Assert.True(await repo.HasCommitGraphAsync(TestContext.Current.CancellationToken));

        byte[] actual = await File.ReadAllBytesAsync(GraphPath(repoPath), TestContext.Current.CancellationToken);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The <c>refs/*</c> scope covers commits NOT reachable from HEAD: the
    /// graph written by the facade contains <c>e90810b8…</c> (tip of
    /// <c>chomped</c>, absent from <c>master</c>'s history) and holds all 15
    /// commits reachable from <c>refs/*</c> (HEAD alone reaches 7). A fresh
    /// repository open sees the file via the lazy-load.
    /// </summary>
    [Fact]
    public async Task WriteCommitGraphAsync_CoversAllRefsNotJustHead()
    {
        string repoPath = Path.Combine(ExtractRepo(), "testrepo.git");
        File.Delete(GraphPath(repoPath));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        await repo.WriteCommitGraphAsync(TestContext.Current.CancellationToken);

        // Fresh open: the ODB lazy-load must pick the new file up.
        await using GitRepository reopened = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.True(await reopened.HasCommitGraphAsync(TestContext.Current.CancellationToken));

        CommitGraph? graph = await CommitGraph.OpenAsync(Path.Combine(repoPath, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(graph);
        Assert.Equal(15, graph!.NumCommits);

        // e90810b is reachable from refs/heads/chomped but not from HEAD.
        var chompedTip = GitOid.Parse("e90810b8df3e80c413d903f631643c716887138d".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.NotNull(graph.FindEntry(chompedTip));
    }
}

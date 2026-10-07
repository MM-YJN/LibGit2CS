using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for the two <see cref="GitRepository"/> commit-graph
/// facades — <see cref="GitRepository.HasCommitGraphAsync"/> and
/// <see cref="GitRepository.WriteCommitGraphAsync"/> — exercised end-to-end
/// against locally-initialized repos with real commit DAGs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The facades sequence public read primitives
/// (<see cref="GitRepository.NewRevWalker"/> +
/// <see cref="Revwalk.GitRevWalker.PushGlobAsync"/>) with the
/// <see cref="CommitGraphWriter"/> and the ODB's cached-graph lazy-load —
/// none of which were previously exercised through the repository-level
/// convenience surface. These tests build sandbox repos via
/// <see cref="RepoBuilder"/>, drive the facades, and verify presence flips,
/// cache refresh on the same handle, <c>refs/*</c> scope (side-branch
/// commits included), and determinism (facade output == an independent
/// writer dump over the same refs).
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> No direct C counterpart — libgit2 has no
/// repository-level commit-graph presence/write API (the facades are
/// documented LibGit2CS additions over the sys-level
/// <c>git_commit_graph_writer_*</c> surface). The scope/determinism
/// assertions mirror the recipe of
/// <c>test_graph_commitgraph__writer</c> (tests/libgit2/graph/commitgraph.c).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class CommitGraphFacadeIntegrationTests : IAsyncDisposable
{
    private readonly RepoBuilder _builder = new();

    /// <summary>
    /// A freshly-initialized repo with commits but no commit-graph
    /// reports <see cref="GitRepository.HasCommitGraphAsync"/> == false —
    /// including when the ODB lazy-load already ran (and cached null) during
    /// the build phase's ODB operations.
    /// </summary>
    [Fact]
    public async Task HasCommitGraphAsync_FalseOnFreshRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await _builder.BuildLinearHistoryAsync(3, ct: ct).ConfigureAwait(false);

        Assert.False(await _builder.Repo.HasCommitGraphAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// <see cref="GitRepository.WriteCommitGraphAsync"/> writes the
    /// file and refreshes the ODB cache: presence flips to true on the SAME
    /// repository handle even though the build phase's lazy-load had already
    /// cached null, and a fresh open of the same path also sees the graph
    /// with the full commit count.
    /// </summary>
    [Fact]
    public async Task WriteCommitGraphAsync_FlipsPresenceOnSameHandleAndFreshOpen()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] commits = await _builder.BuildLinearHistoryAsync(3, ct: ct).ConfigureAwait(false);

        GitRepository repo = _builder.Repo;
        Assert.False(await repo.HasCommitGraphAsync(ct).ConfigureAwait(false));

        await repo.WriteCommitGraphAsync(ct).ConfigureAwait(false);

        // Same handle: the cache refresh makes the write visible.
        Assert.True(await repo.HasCommitGraphAsync(ct).ConfigureAwait(false));
        Assert.True(File.Exists(Path.Combine(_builder.Path, ".git", "objects", "info", "commit-graph")));

        // Fresh open: the lazy-load picks the file up from disk.
        await using GitRepository reopened = await GitRepository.OpenAsync(_builder.Path, new GitContext(), cancellationToken: ct).ConfigureAwait(false);
        Assert.True(await reopened.HasCommitGraphAsync(ct).ConfigureAwait(false));

        CommitGraph? graph = await CommitGraph.OpenAsync(
            Path.Combine(_builder.Path, ".git", "objects"),
            reopened.ObjectFormat,
            ct).ConfigureAwait(false);
        Assert.NotNull(graph);
        Assert.Equal(commits.Length, graph!.NumCommits);
    }

    /// <summary>
    /// The <c>refs/*</c> scope covers side branches: a commit created
    /// on <c>refs/heads/side</c> (not reachable from HEAD's
    /// <c>refs/heads/main</c>) is present in the written graph, and the
    /// graph's commit count equals the total history size, not HEAD's.
    /// </summary>
    [Fact]
    public async Task WriteCommitGraphAsync_CoversSideBranchCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        GitOid root = await _builder.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);
        GitOid mainTip = await _builder.CommitFileAsync("main.txt", "main\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);
        GitOid sideTip = await _builder.CommitFileAsync("side.txt", "side\n"u8.ToArray(), "side\n", parent: root, updateRef: "refs/heads/side", ct: ct).ConfigureAwait(false);

        await _builder.Repo.WriteCommitGraphAsync(ct).ConfigureAwait(false);

        CommitGraph? graph = await CommitGraph.OpenAsync(
            Path.Combine(_builder.Path, ".git", "objects"),
            _builder.Repo.ObjectFormat,
            ct).ConfigureAwait(false);
        Assert.NotNull(graph);

        // root + main tip + side tip — the side branch is included.
        Assert.Equal(3, graph!.NumCommits);
        Assert.NotNull(graph.FindEntry(sideTip));
        Assert.NotNull(graph.FindEntry(mainTip));
    }

    /// <summary>
    /// Determinism: the file written by the facade is byte-identical
    /// to an independent <see cref="CommitGraphWriter"/> dump fed by a
    /// <c>refs/*</c> glob walk over the same repo (the managed counterpart
    /// of <c>test_graph_commitgraph__writer</c>'s equivalence assertion).
    /// </summary>
    [Fact]
    public async Task WriteCommitGraphAsync_MatchesIndependentRefsGlobDump()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await _builder.BuildDivergentFileHistoryAsync("file.txt", "base\n"u8.ToArray(), "main\n"u8.ToArray(), "feature\n"u8.ToArray(), ct: ct).ConfigureAwait(false);

        await _builder.Repo.WriteCommitGraphAsync(ct).ConfigureAwait(false);
        byte[] fromFile = await File.ReadAllBytesAsync(Path.Combine(_builder.Path, ".git", "objects", "info", "commit-graph"), ct).ConfigureAwait(false);

        // Independent writer over the same refs/* glob.
        using GitRevWalker walker = _builder.Repo.NewRevWalker();
        await walker.PushGlobAsync("refs/*", ct).ConfigureAwait(false);
        using CommitGraphWriter independent = new(Path.Combine(_builder.Path, ".git", "objects", "info"));
        await independent.AddRevwalkAsync(walker, ct).ConfigureAwait(false);
        byte[] dumped = independent.Dump();

        Assert.Equal(dumped.Length, fromFile.Length);
        Assert.Equal(dumped, fromFile);
    }

    public async ValueTask DisposeAsync()
    {
        await _builder.DisposeAsync().ConfigureAwait(false);
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for the commit-graph writer + reader
/// (<see cref="CommitGraphWriter"/> → <see cref="CommitGraph"/>)
/// exercised end-to-end against locally-initialized repos with real
/// commit DAGs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> <see cref="CommitGraphWriter"/> and
/// <see cref="CommitGraph"/> (read side) had <c>0%</c> and <c>3.1%</c>
/// coverage respectively — no integration test writes a commit-graph file
/// and reads it back. The unit <c>CommitGraphWriterTests</c> cover the
/// chunk serialization against synthetic inputs, but the
/// <see cref="CommitGraphWriter.AddRevwalkAsync"/> path (which walks a real
/// <see cref="GitRevWalker"/> and looks up each commit via the ODB) and the
/// <see cref="CommitGraph.OpenAsync"/>/<see cref="CommitGraph.FindEntry"/>
/// round-trip were never exercised end-to-end. These tests build a linear
/// history and an octopus-merge DAG via <see cref="RepoBuilder"/>, write a
/// commit-graph from a revwalk over all commits, then verify the read-back
/// entries' generation numbers and parent counts. Also covers the
/// <see cref="GitObjectDb.GetCommitGraphAsync"/> lazy-load path.
/// </para>
/// <para>
/// <see cref="CommitGraphWriter"/> is <c>internal</c> but reachable from
/// integration tests via <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/commitgraph/graph.c</c>
/// (<c>test_commitgraph__write_and_read</c>,
/// <c>test_commitgraph__octopus_merge</c>), adapted to build the sandbox
/// from scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class CommitGraphIntegrationTests : IAsyncDisposable
{
    private readonly RepoBuilder _builder = new();

    /// <summary>
    /// Writing a commit-graph from a revwalk over a 5-commit linear
    /// history, then opening it, yields <see cref="CommitGraph.NumCommits"/>
    /// == 5 and each commit's entry has the correct generation number
    /// (1 for the root, 2..5 for descendants) and parent count (0 for root,
    /// 1 for the rest). Exercises <see cref="CommitGraphWriter.AddRevwalkAsync"/>,
    /// <see cref="CommitGraphWriter.CommitAsync"/>, and
    /// <see cref="CommitGraph.FindEntry"/>.
    /// </summary>
    [Fact]
    public async Task WriteAndOpen_RoundTripsLinearHistory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] commits = await _builder.BuildLinearHistoryAsync(5, ct: ct).ConfigureAwait(false);

        // Walk all commits and feed them to the writer.
        using GitRevWalker walker = _builder.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Time;
        await walker.PushAsync(commits[^1], ct).ConfigureAwait(false);

        string objectsInfoDir = Path.Combine(_builder.Path, ".git", "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);
        using var writer = new CommitGraphWriter(objectsInfoDir);
        await writer.AddRevwalkAsync(walker, ct).ConfigureAwait(false);
        await writer.CommitAsync(ct).ConfigureAwait(false);

        // Read back.
        CommitGraph? graph = await CommitGraph.OpenAsync(
            Path.Combine(_builder.Path, ".git", "objects"),
            _builder.Repo.ObjectFormat,
            ct).ConfigureAwait(false);

        Assert.NotNull(graph);
        Assert.Equal(5, graph!.NumCommits);

        // Root (commits[0]) has generation 1, 0 parents. Each descendant
        // increments generation by 1 and has 1 parent.
        for (int i = 0; i < commits.Length; i++)
        {
            CommitGraphEntry entry = graph.FindEntry(commits[i])!.Value;
            Assert.Equal(i + 1u, entry.Generation);
            Assert.Equal(i == 0 ? 0 : 1, entry.ParentCount);
        }
    }

    /// <summary>
    /// An octopus merge (3-parent commit) in the DAG causes the writer
    /// to emit the EDGE chunk (<c>parent2 high bit set</c>); reading the
    /// merge commit's entry back yields <see cref="CommitGraphEntry.ParentCount"/>
    /// == 3 with all three parent OIDs resolved via the extra-edge list.
    /// Exercises the <c>&gt; 2 parents</c> branch in both writer and reader.
    /// </summary>
    [Fact]
    public async Task WriteWithOctopusMerge_HitsExtraEdgeChunk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Build: root, then three children A/B/C, then an octopus merge
        // with parents [A, B, C].
        GitOid root = await _builder.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);
        GitOid a = await _builder.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: root, updateRef: "refs/heads/a", ct: ct).ConfigureAwait(false);
        GitOid b = await _builder.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: root, updateRef: "refs/heads/b", ct: ct).ConfigureAwait(false);
        GitOid c = await _builder.CommitFileAsync("c.txt", "c\n"u8.ToArray(), "c\n", parent: root, updateRef: "refs/heads/c", ct: ct).ConfigureAwait(false);

        // Octopus merge tree: base + a + b + c.
        GitOid mergeTree = await _builder.BuildTreeAsync(
            [
                ("base.txt", await _builder.WriteBlobAsync("base\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("a.txt", await _builder.WriteBlobAsync("a\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("b.txt", await _builder.WriteBlobAsync("b\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("c.txt", await _builder.WriteBlobAsync("c\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
            ], ct).ConfigureAwait(false);
        GitOid merge = await _builder.MergeCommitAsync(mergeTree, [a, b, c], "octopus\n", updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);

        // Walk from the octopus merge tip.
        using GitRevWalker walker = _builder.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Time;
        await walker.PushAsync(merge, ct).ConfigureAwait(false);

        string objectsInfoDir = Path.Combine(_builder.Path, ".git", "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);
        using var writer = new CommitGraphWriter(objectsInfoDir);
        await writer.AddRevwalkAsync(walker, ct).ConfigureAwait(false);
        await writer.CommitAsync(ct).ConfigureAwait(false);

        CommitGraph? graph = await CommitGraph.OpenAsync(
            Path.Combine(_builder.Path, ".git", "objects"),
            _builder.Repo.ObjectFormat,
            ct).ConfigureAwait(false);

        Assert.NotNull(graph);
        // root, a, b, c, merge = 5 commits.
        Assert.Equal(5, graph!.NumCommits);

        CommitGraphEntry mergeEntry = graph.FindEntry(merge)!.Value;
        Assert.Equal(3, mergeEntry.ParentCount);
        // All three parents present (resolved via EDGE chunk).
        Assert.Contains(a, mergeEntry.Parents);
        Assert.Contains(b, mergeEntry.Parents);
        Assert.Contains(c, mergeEntry.Parents);

        // Generation: root=1, a/b/c=2, merge=3.
        Assert.Equal(3u, mergeEntry.Generation);
    }

    /// <summary>
    /// <see cref="CommitGraph.OpenAsync"/> on a repo with no
    /// commit-graph file returns null (the <c>!File.Exists</c> early-return
    /// branch).
    /// </summary>
    [Fact]
    public async Task OpenAsync_MissingFile_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await _builder.BuildLinearHistoryAsync(2, ct: ct).ConfigureAwait(false);

        CommitGraph? graph = await CommitGraph.OpenAsync(
            Path.Combine(_builder.Path, ".git", "objects"),
            _builder.Repo.ObjectFormat,
            ct).ConfigureAwait(false);

        Assert.Null(graph);
    }

    /// <summary>
    /// After writing a commit-graph via <see cref="CommitGraphWriter"/>,
    /// <see cref="GitObjectDb.GetCommitGraphAsync"/> lazy-loads it (the
    /// <c>_commitGraphChecked</c> + <c>CommitGraph.OpenAsync</c> path) and
    /// returns a non-null graph with the correct commit count. Exercises the
    /// lazy-load + cache path in <see cref="GitObjectDb"/>.
    /// </summary>
    /// <remarks>
    /// The repo is re-opened after writing the graph so the ODB's
    /// <c>_commitGraphChecked</c> flag is fresh (the build phase's ODB ops
    /// may have already triggered the lazy-load and cached null before the
    /// file existed).
    /// </remarks>
    [Fact]
    public async Task GetCommitGraphAsync_LazyLoadsAfterWrite()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] commits = await _builder.BuildLinearHistoryAsync(3, ct: ct).ConfigureAwait(false);

        // Write a commit-graph.
        using GitRevWalker walker = _builder.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Time;
        await walker.PushAsync(commits[^1], ct).ConfigureAwait(false);

        string objectsInfoDir = Path.Combine(_builder.Path, ".git", "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);
        using var writer = new CommitGraphWriter(objectsInfoDir);
        await writer.AddRevwalkAsync(walker, ct).ConfigureAwait(false);
        await writer.CommitAsync(ct).ConfigureAwait(false);

        // Re-open the repo so the ODB lazy-load cache is fresh.
        await using GitRepository repo2 = await GitRepository.OpenAsync(_builder.Path, new GitContext(), cancellationToken: ct).ConfigureAwait(false);
        CommitGraph? loaded = await repo2.Objects.GetCommitGraphAsync(ct).ConfigureAwait(false);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.NumCommits);
    }

    /// <summary>
    /// A commit-graph written by stock git must open and decode. git
    /// &gt;= 2.38 emits the generation-data-v2 <c>GDA2</c> chunk by default
    /// (<c>commitGraph.generationVersion=2</c> is forced on the command line
    /// so user config cannot neuter the test), which the v1.9.4 baseline
    /// rejected as "unrecognized chunk ID" — the recognized-and-skipped
    /// GDA2/GDO2 handling is a forward-port of upstream libgit2 main
    /// commit <c>2e3ec8d</c> (PR #7271). Skips when no git &gt;= 2.38 is
    /// on PATH.
    /// </summary>
    [Fact]
    public async Task StockGitGraph_WithGda2Chunk_OpensAndDecodes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Version? gitVersion = await GetGitVersionAsync(ct).ConfigureAwait(false);
        if (gitVersion is null || gitVersion < new Version(2, 38))
        {
            Assert.Skip($"git >= 2.38 not on PATH (found: {gitVersion}); skipping stock-git GDA2 test.");
        }

        GitOid[] commits = await _builder.BuildLinearHistoryAsync(5, ct: ct).ConfigureAwait(false);

        // Write the graph with stock git (not CommitGraphWriter, which does
        // not emit generation-data chunks).
        (int exit, _, string err) = await RunGitAsync(
            ["-c", "commitGraph.generationVersion=2", "-C", _builder.Path, "commit-graph", "write", "--reachable"],
            ct).ConfigureAwait(false);
        Assert.True(exit == 0, $"git commit-graph write failed ({exit}): {err}");

        byte[] raw = await File.ReadAllBytesAsync(
            Path.Combine(_builder.Path, ".git", "objects", "info", "commit-graph"), ct).ConfigureAwait(false);

        // The graph must actually contain a GDA2 chunk; otherwise the test
        // would silently degrade to a no-GDA2 graph and stop covering the
        // divergence. Header: sig(4) + version(1) + oidVersion(1) +
        // numChunks(1), then (1 + numChunks) chunk-table entries of
        // [id(4 BE) + offset(8 BE)].
        Assert.Contains(GetChunkIds(raw), id => id == 0x47444132u); // "GDA2"

        CommitGraph? graph = await CommitGraph.OpenAsync(
            Path.Combine(_builder.Path, ".git", "objects"),
            _builder.Repo.ObjectFormat,
            ct).ConfigureAwait(false);

        Assert.NotNull(graph);
        Assert.Equal(5, graph!.NumCommits);

        // The revwalk fast path works: the tip decodes with its parent chain.
        CommitGraphEntry tip = graph.FindEntry(commits[^1])!.Value;
        Assert.Equal(1, tip.ParentCount);
        Assert.Equal(commits[^2], tip.Parents[0]);
    }

    /// <summary>Runs git with the given argument list, capturing stdout/stderr.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git");
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Returns the git binary's version, or null when git is not on PATH /
    /// reports an unparseable version.
    /// </summary>
    private static async Task<Version?> GetGitVersionAsync(CancellationToken ct)
    {
        try
        {
            (int exit, string stdout, _) = await RunGitAsync(["--version"], ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return null;
            }

            // "git version 2.43.0" — trim any "-rc0"-style suffix before parsing.
            string v = stdout.Trim().Split(' ')[^1].Split('-')[0];
            return Version.TryParse(v, out Version? version) ? version : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not on PATH
        }
    }

    /// <summary>Enumerates the chunk-table IDs of a commit-graph file.</summary>
    private static IEnumerable<uint> GetChunkIds(byte[] raw)
    {
        byte numChunks = raw[6];
        for (int i = 0; i < numChunks; i++)
        {
            yield return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8 + i * 12, 4));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _builder.DisposeAsync().ConfigureAwait(false);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// Integration tests for the grafts reader (<see cref="Grafts"/>) exercised
/// end-to-end against locally-initialized repos with real commit OIDs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>GraftsTests</c> cover the parser
/// against a synthetic file with hardcoded OIDs that don't exist in any
/// object database — they never exercise the grafts reader against a real
/// repo's <c>.git/info/grafts</c> file with real commit OIDs from a built
/// DAG. These integration tests build a real commit history, write the
/// actual commit OIDs to <c>.git/info/grafts</c>, open the file via
/// <see cref="Grafts.OpenAsync"/>/<see cref="Grafts.OpenOrRefreshAsync"/>,
/// and verify <see cref="Grafts.Get"/>/<see cref="Grafts.Oids"/> return the
/// real grafts. The shallow-file path (<see cref="Grafts.WriteShallowAsync"/>)
/// is also exercised end-to-end: write shallow roots, reopen, verify.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/grafts/grafts.c</c>
/// (<c>test_grafts__repo_with_grafts</c>,
/// <c>test_grafts__parse_single_line</c>,
/// <c>test_grafts__multiple_parents</c>), adapted to build the sandbox from
/// scratch and use real commit OIDs from the on-disk ODB.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// <para>
/// <b>Internal-API access.</b> <see cref="Grafts"/> is <c>internal</c> —
/// reachable here via the <c>InternalsVisibleTo("LibGit2CS.IntegrationTests")</c>
/// declaration in <c>LibGit2CS.csproj</c>, mirroring the unit-test access.
/// </para>
/// </remarks>
public sealed class GraftsIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-grafts-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates a linear 3-commit history (c1 → c2 → c3) on
    /// <c>refs/heads/main</c>, each adding a distinct file. Returns the
    /// commit OIDs in order. HEAD is set to <c>refs/heads/main</c>.
    /// </summary>
    private static async Task<GitOid[]> BuildLinearHistoryAsync(GitRepository repo, string workdir, CancellationToken ct)
    {
        var commits = new GitOid[3];
        GitOid? parent = null;
        for (int i = 0; i < 3; i++)
        {
            string fileName = $"file{i}.txt";
            await File.WriteAllTextAsync(Path.Combine(workdir, fileName), $"content{i}\n", ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync(fileName, ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);

            commits[i] = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = parent is null ? [] : [parent.Value],
                Author = Sig,
                Committer = Sig,
                Message = $"commit {i}\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            parent = commits[i];
        }

        await repo.SetHeadAsync("refs/heads/main", ct);
        return commits;
    }

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Grafts.OpenAsync against a real repo's .git/info/grafts ───────────

    /// <summary>
    /// <see cref="Grafts.OpenAsync"/> reads a <c>.git/info/grafts</c>
    /// file written with real commit OIDs from the repo's ODB and returns
    /// the grafted parents. A graft that declares an alternate parent for
    /// commit c3 is reported back via <see cref="Grafts.Get"/>. Mirrors
    /// <c>test_grafts__repo_with_grafts</c> (grafts.c).
    /// </summary>
    [Fact]
    public async Task Open_RepoGraftsFile_ReturnsRealGraft()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid[] commits = await BuildLinearHistoryAsync(repo, path, ct);

            // Graft: c3 (commits[2]) gets c1 (commits[0]) as its sole parent,
            // hiding c2 (commits[1]) from the grafted history.
            string graftsPath = Path.Combine(repo.Path, "info", "grafts");
            Directory.CreateDirectory(Path.GetDirectoryName(graftsPath)!);
            await File.WriteAllTextAsync(
                graftsPath,
                $"{commits[2]} {commits[0]}\n",
                ct);

            using Grafts grafts = await Grafts.OpenAsync(graftsPath, repo.ObjectFormat, ct);
            Assert.Equal(1, grafts.Count);

            GraftEntry? entry = grafts.Get(commits[2]);
            Assert.NotNull(entry);
            Assert.Equal(commits[2], entry!.Value.Commit);
            Assert.Single(entry.Value.Parents);
            Assert.Equal(commits[0], entry.Value.Parents[0]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Grafts.Oids"/> returns all grafted commit OIDs in the
    /// order they appear in the grafts file, and <see cref="Grafts.Get"/>
    /// returns null for a commit OID not in the file. Mirrors
    /// <c>test_grafts__parse_multiple</c> (grafts.c).
    /// </summary>
    [Fact]
    public async Task Oids_AndGet_ReturnAllEntries_AndNullForMissing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid[] commits = await BuildLinearHistoryAsync(repo, path, ct);

            // Graft c2 and c3 with no parents — both become roots in the
            // grafted history.
            string graftsPath = Path.Combine(repo.Path, "info", "grafts");
            Directory.CreateDirectory(Path.GetDirectoryName(graftsPath)!);
            await File.WriteAllTextAsync(
                graftsPath,
                $"{commits[1]}\n{commits[2]}\n",
                ct);

            using Grafts grafts = await Grafts.OpenAsync(graftsPath, repo.ObjectFormat, ct);
            Assert.Equal(2, grafts.Count);

            IReadOnlyList<GitOid> oids = grafts.Oids();
            Assert.Equal(2, oids.Count);
            Assert.Contains(commits[1], oids);
            Assert.Contains(commits[2], oids);

            // c2 has no parents (root graft).
            GraftEntry? e2 = grafts.Get(commits[1]);
            Assert.NotNull(e2);
            Assert.Empty(e2!.Value.Parents);

            // c1 is not in the file → null.
            Assert.Null(grafts.Get(commits[0]));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A graft entry can declare multiple parents. Mirrors
    /// <c>test_grafts__multiple_parents</c> (grafts.c).
    /// </summary>
    [Fact]
    public async Task Get_MultipleParents_AllStoredInOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid[] commits = await BuildLinearHistoryAsync(repo, path, ct);

            // Graft: c3 has both c1 and c2 as parents (c2 was already its
            // parent; the graft adds c1 as an extra parent).
            string graftsPath = Path.Combine(repo.Path, "info", "grafts");
            Directory.CreateDirectory(Path.GetDirectoryName(graftsPath)!);
            await File.WriteAllTextAsync(
                graftsPath,
                $"{commits[2]} {commits[0]} {commits[1]}\n",
                ct);

            using Grafts grafts = await Grafts.OpenAsync(graftsPath, repo.ObjectFormat, ct);
            GraftEntry? entry = grafts.Get(commits[2]);
            Assert.NotNull(entry);
            Assert.Equal(2, entry!.Value.Parents.Count);
            Assert.Equal(commits[0], entry.Value.Parents[0]);
            Assert.Equal(commits[1], entry.Value.Parents[1]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Grafts.OpenAsync"/> on a missing file returns an empty
    /// grafts object (no entries). A fresh repo with no
    /// <c>.git/info/grafts</c> file is the canonical case.
    /// </summary>
    [Fact]
    public async Task Open_MissingGraftsFile_ReturnsEmpty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // No grafts file written.
            string graftsPath = Path.Combine(repo.Path, "info", "grafts");
            using Grafts grafts = await Grafts.OpenAsync(graftsPath, repo.ObjectFormat, ct);
            Assert.Equal(0, grafts.Count);
            Assert.Empty(grafts.Oids());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Grafts.OpenOrRefreshAsync"/> re-reads the file content
    /// on each call, so editing <c>.git/info/grafts</c> between two opens
    /// reflects the new entries. Mirrors the refresh semantics from
    /// <c>git_grafts_open_or_refresh</c>.
    /// </summary>
    [Fact]
    public async Task OpenOrRefresh_RereadsFile_AfterEdit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid[] commits = await BuildLinearHistoryAsync(repo, path, ct);
            string graftsPath = Path.Combine(repo.Path, "info", "grafts");
            Directory.CreateDirectory(Path.GetDirectoryName(graftsPath)!);

            // First open: one graft for c2.
            await File.WriteAllTextAsync(graftsPath, $"{commits[1]}\n", ct);
            Grafts g1 = await Grafts.OpenOrRefreshAsync(graftsPath, repo.ObjectFormat, ct);
            try
            {
                Assert.Equal(1, g1.Count);
                Assert.NotNull(g1.Get(commits[1]));
            }
            finally
            {
                g1.Dispose();
            }

            // Edit the file: now two grafts (c2 and c3).
            await File.WriteAllTextAsync(graftsPath, $"{commits[1]}\n{commits[2]}\n", ct);

            // Second open reflects the edited file.
            Grafts g2 = await Grafts.OpenOrRefreshAsync(graftsPath, repo.ObjectFormat, ct);
            try
            {
                Assert.Equal(2, g2.Count);
                Assert.NotNull(g2.Get(commits[1]));
                Assert.NotNull(g2.Get(commits[2]));
            }
            finally
            {
                g2.Dispose();
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Shallow file (WriteShallowAsync) ────────────────────────────────

    /// <summary>
    /// <see cref="Grafts.WriteShallowAsync"/> writes the shallow root
    /// OIDs to <c>.git/shallow</c>, one per line, and a subsequent
    /// <see cref="Grafts.OpenAsync"/> on the shallow file reads them back.
    /// An empty OID list deletes the file. Mirrors
    /// <c>test_grafts__shallow_roots_write</c> (grafts.c) and the
    /// <c>git_repository__shallow_roots_write</c> reference path.
    /// </summary>
    [Fact]
    public async Task WriteShallow_WritesOids_RereadReflectsThem_EmptyDeletes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid[] commits = await BuildLinearHistoryAsync(repo, path, ct);
            string shallowPath = Path.Combine(repo.Path, "shallow");

            // Write two shallow roots.
            await Grafts.WriteShallowAsync(repo.Path, [commits[0], commits[1]], ct);
            Assert.True(File.Exists(shallowPath));

            using (Grafts shallow = await Grafts.OpenAsync(shallowPath, repo.ObjectFormat, ct))
            {
                Assert.Equal(2, shallow.Count);
                IReadOnlyList<GitOid> oids = shallow.Oids();
                Assert.Contains(commits[0], oids);
                Assert.Contains(commits[1], oids);
            }

            // Empty OID list → file deleted.
            await Grafts.WriteShallowAsync(repo.Path, [], ct);
            Assert.False(File.Exists(shallowPath));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Grafts.WriteShallowAsync"/> is idempotent: writing the
    /// same shallow roots twice overwrites the file with identical content.
    /// And writing new roots replaces the old content entirely. Mirrors the
    /// overwrite semantics in <c>git_repository__shallow_roots_write</c>.
    /// </summary>
    [Fact]
    public async Task WriteShallow_OverwritesExistingFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid[] commits = await BuildLinearHistoryAsync(repo, path, ct);
            string shallowPath = Path.Combine(repo.Path, "shallow");

            // Write c1 as the only shallow root.
            await Grafts.WriteShallowAsync(repo.Path, [commits[0]], ct);
            Grafts g1 = await Grafts.OpenAsync(shallowPath, repo.ObjectFormat, ct);
            try
            {
                Assert.Equal(1, g1.Count);
                Assert.NotNull(g1.Get(commits[0]));
            }
            finally
            {
                g1.Dispose();
            }

            // Overwrite with c2 and c3 — c1 is no longer present.
            await Grafts.WriteShallowAsync(repo.Path, [commits[1], commits[2]], ct);
            Grafts g2 = await Grafts.OpenAsync(shallowPath, repo.ObjectFormat, ct);
            try
            {
                Assert.Equal(2, g2.Count);
                Assert.Null(g2.Get(commits[0]));
                Assert.NotNull(g2.Get(commits[1]));
                Assert.NotNull(g2.Get(commits[2]));
            }
            finally
            {
                g2.Dispose();
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}

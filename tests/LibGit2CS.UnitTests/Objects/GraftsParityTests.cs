using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Regression tests for the grafts/shallow read-side wiring parity
/// behaviors ("grafts never applied") in libgit2 1.9.4. Expected values
/// were differentially verified against the C reference (libgit2 1.9.4)
/// with a harness on the same 3-commit repo:
/// <code>
/// 6f366e7352bf329907175d92966b986e84f343e3 parents=1  (HEAD)
/// 4709edbf2398ebf0d3148637a726b74175413ffb parents=0  (grafted to root)
/// </code>
/// C consumes grafts in <c>git_commit__parse_ext</c> (commit.c:554-567)
/// from <c>repo-&gt;grafts</c> (<c>&lt;commondir&gt;/info/grafts</c>) or
/// <c>repo-&gt;shallow_grafts</c> (<c>&lt;gitdir&gt;/shallow</c>), both
/// loaded by <c>load_grafts</c> at repository open (repository.c:877-918).
/// </summary>
public sealed class GraftsParityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_GraftsParity_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repoPath;

    public GraftsParityTests()
    {
        Directory.CreateDirectory(_tempDir);
        _repoPath = Path.Combine(_tempDir, "repo");
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

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitOid[]> BuildRepoAsync(CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.InitAsync(_repoPath, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        var oids = new List<GitOid>();
        GitOid? parent = null;
        for (int i = 1; i <= 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), new string('x', i) + "\n", ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("f.txt", ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);
            GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = parent is { } p ? [p] : [],
                Author = Sig,
                Committer = Sig,
                Message = $"c{i}\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            oids.Add(commitOid);
            parent = commitOid;
        }

        await repo.SetHeadAsync("refs/heads/main", ct);
        return [.. oids];
    }

    private static async Task<List<GitOid>> WalkAsync(GitRepository repo, CancellationToken ct)
    {
        var result = new List<GitOid>();
        using GitRevWalker walker = repo.NewRevWalker();
        await walker.PushHeadAsync(ct);
        await foreach (GitOid oid in walker.WalkAsync(ct))
        {
            result.Add(oid);
        }

        return result;
    }

    [Fact]
    public async Task InfoGrafts_ReplaceParents_OnParseAndRevwalk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] oids = await BuildRepoAsync(ct);

        // Graft the middle commit to a root (no parents) — C-verified:
        // parents=0 for the grafted commit, the walk stops there.
        string graftsDir = Path.Combine(_repoPath, ".git", "info");
        Directory.CreateDirectory(graftsDir);
        await File.WriteAllTextAsync(Path.Combine(graftsDir, "grafts"), oids[1] + "\n", ct);

        await using GitRepository repo = await GitRepository.OpenAsync(_repoPath, new GitContext(), cancellationToken: ct);

        Commit? grafted = await repo.ObjectLookupAsync<Commit>(oids[1], ct);
        Assert.NotNull(grafted);
        Assert.Empty(grafted!.Parents);

        Commit? tip = await repo.ObjectLookupAsync<Commit>(oids[2], ct);
        Assert.NotNull(tip);
        Assert.Equal([oids[1]], tip!.Parents);

        // The revwalk must stop at the graft boundary (2 commits, not 3).
        List<GitOid> walked = await WalkAsync(repo, ct);
        Assert.Equal(2, walked.Count);
        Assert.Contains(oids[1], walked);
        Assert.DoesNotContain(oids[0], walked);
    }

    [Fact]
    public async Task ShallowFile_ReplacesParents_OnParseAndRevwalk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] oids = await BuildRepoAsync(ct);

        // .git/shallow marks the middle commit as a shallow boundary.
        await File.WriteAllTextAsync(Path.Combine(_repoPath, ".git", "shallow"), oids[1] + "\n", ct);

        await using GitRepository repo = await GitRepository.OpenAsync(_repoPath, new GitContext(), cancellationToken: ct);

        Commit? grafted = await repo.ObjectLookupAsync<Commit>(oids[1], ct);
        Assert.NotNull(grafted);
        Assert.Empty(grafted!.Parents);

        List<GitOid> walked = await WalkAsync(repo, ct);
        Assert.Equal(2, walked.Count);
        Assert.DoesNotContain(oids[0], walked);
    }

    [Fact]
    public async Task GraftsFile_ParentReplacement_AddsParents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] oids = await BuildRepoAsync(ct);

        // Graft the ROOT commit to have one parent (itself would be cyclic;
        // use the tip — C accepts any OID, the parent just must exist to
        // walk). C-verified behavior: the parent list is replaced verbatim.
        string graftsDir = Path.Combine(_repoPath, ".git", "info");
        Directory.CreateDirectory(graftsDir);
        await File.WriteAllTextAsync(Path.Combine(graftsDir, "grafts"), $"{oids[0]} {oids[2]}\n", ct);

        await using GitRepository repo = await GitRepository.OpenAsync(_repoPath, new GitContext(), cancellationToken: ct);

        Commit? root = await repo.ObjectLookupAsync<Commit>(oids[0], ct);
        Assert.NotNull(root);
        Assert.Equal([oids[2]], root!.Parents);
    }

    // ── Strict parse ──

    [Theory]
    [InlineData("# comment\n")]
    [InlineData("\n")]
    [InlineData("notanoid\n")]
    [InlineData("0123456789012345678901234567890123456789\t0123456789012345678901234567890123456789\n")] // tab separator
    [InlineData("0123456789012345678901234567890123456789 0123456789012345678901234567890123456789 \n")] // trailing space
    [InlineData("0123456789012345678901234567890123456789 0123456789012345678901234567890123456789\r\n")] // CRLF
    public async Task GraftsParse_MalformedContent_FailsWholeParse(string content)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _ = await BuildRepoAsync(ct);

        string graftsDir = Path.Combine(_repoPath, ".git", "info");
        Directory.CreateDirectory(graftsDir);
        await File.WriteAllTextAsync(Path.Combine(graftsDir, "grafts"), content, ct);

        // C: any malformed line aborts git_grafts_parse with GIT_ERROR_GRAFTS
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository repo = await GitRepository.OpenAsync(_repoPath, new GitContext(), cancellationToken: ct);
                await repo.ObjectLookupAsync<Commit>(GitOid.Parse("0123456789012345678901234567890123456789".AsSpan(), GitHashAlgorithmKind.Sha1), ct);
            });

        Assert.Equal(GitErrorCategory.Grafts, ex.Category);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for the grafts/shallow read-side wiring parity
/// behaviors ("grafts never applied") in libgit2 1.9.4, exercised
/// end-to-end against real repositories: commit parsing, revwalks,
/// merge-base and ahead/behind must all honor <c>.git/info/grafts</c> and
/// <c>.git/shallow</c>.
///
/// Expected values were differentially verified against the C reference
/// (libgit2 1.9.4) on the same 3-commit repo with the middle commit
/// grafted to a root:
///
/// <code>
/// merge_base(c1, c3) rc=-3          (no common ancestor)
/// ahead_behind(c1, c3) = (1, 2)
/// </code>
/// </summary>
public sealed class GraftsParityIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_GraftsInt_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repoPath;

    public GraftsParityIntegrationTests()
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

    /// <summary>
    /// With the middle commit grafted to a root, commit parsing, the
    /// revwalk, merge-base and ahead/behind all reflect the grafted
    /// topology (C-verified).
    /// </summary>
    [Fact]
    public async Task InfoGrafts_AffectsParseRevwalkMergeBaseAndAheadBehind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid[] oids = await BuildRepoAsync(ct);

        string graftsDir = Path.Combine(_repoPath, ".git", "info");
        Directory.CreateDirectory(graftsDir);
        await File.WriteAllTextAsync(Path.Combine(graftsDir, "grafts"), oids[1] + "\n", ct);

        await using GitRepository repo = await GitRepository.OpenAsync(_repoPath, new GitContext(), cancellationToken: ct);

        // Parse: the grafted commit has no parents; the tip still has one.
        Commit? grafted = await repo.ObjectLookupAsync<Commit>(oids[1], ct);
        Assert.NotNull(grafted);
        Assert.Empty(grafted!.Parents);

        // Revwalk: stops at the graft boundary — only 2 commits.
        using GitRevWalker walker = repo.NewRevWalker();
        await walker.PushHeadAsync(ct);
        var walked = new List<GitOid>();
        await foreach (GitOid oid in walker.WalkAsync(ct))
        {
            walked.Add(oid);
        }

        Assert.Equal(2, walked.Count);
        Assert.DoesNotContain(oids[0], walked);

        // Merge-base: C returns GIT_ENOTFOUND for the disjoint pair.
        GitOid? base_ = await repo.MergeBaseFindAsync(oids[0], oids[2], ct);
        Assert.Null(base_);

        // Ahead/behind: C-verified (1, 2).
        (int ahead, int behind) = await repo.AheadBehindAsync(oids[0], oids[2], ct);
        Assert.Equal(1, ahead);
        Assert.Equal(2, behind);
    }
}

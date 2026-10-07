using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;

namespace LibGit2CS.IntegrationTests.Refs;

/// <summary>
/// End-to-end tests for <see cref="GitRepository.PackRefsAsync"/>
/// (<c>git_refdb_compress</c>, refdb.c:92-100 → refdb_fs.c:1897-1910)
/// against repos built from scratch with <see cref="RepoBuilder"/>.
/// </summary>
public sealed class PackRefsParityIntegrationTests
{
    [Fact]
    public async Task PackRefs_PacksLooseDirectRefs_SkipsSymbolic()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);
        await bld.CreateBranchAsync("feature", history[0], ct);

        await bld.Repo.PackRefsAsync(ct);

        string packed = await File.ReadAllTextAsync(Path.Combine(bld.Path, ".git", "packed-refs"), ct);
        int featureIdx = packed.IndexOf(history[0] + " refs/heads/feature\n", StringComparison.Ordinal);
        int mainIdx = packed.IndexOf(history[1] + " refs/heads/main\n", StringComparison.Ordinal);
        Assert.True(featureIdx >= 0, "feature not packed");
        Assert.True(mainIdx >= 0, "main not packed");
        Assert.True(featureIdx < mainIdx, "packed refs not sorted");
        Assert.DoesNotContain("HEAD", packed, StringComparison.Ordinal);

        // packed_write
        // ends with packed_remove_loose (refdb_fs.c:1465-1468), so the packed
        // loose ref files are pruned — matches the unit-test expectation and
        // the C reference.
        Assert.False(File.Exists(Path.Combine(bld.Path, ".git", "refs", "heads", "main")));
        Assert.False(File.Exists(Path.Combine(bld.Path, ".git", "refs", "heads", "feature")));
    }

    [Fact]
    public async Task PackRefs_AnnotatedTag_EmitsPeelLine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        using GitObject? target = await bld.Repo.ObjectLookupAsync(history[0], ct).ConfigureAwait(false);
        GitOid tagOid = await bld.Repo.TagCreateAsync("v1.0", target!, RepoBuilder.Sig, "annotated tag\n", cancellationToken: ct);
        using GitTag tag = (await bld.Repo.ObjectLookupAsync<GitTag>(tagOid, ct).ConfigureAwait(false))!;

        await bld.Repo.PackRefsAsync(ct);

        string packed = await File.ReadAllTextAsync(Path.Combine(bld.Path, ".git", "packed-refs"), ct);
        Assert.Contains(tagOid + " refs/tags/v1.0\n^" + tag.Target + "\n", packed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackRefs_StalePackedEntry_OverwrittenByLoose()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        var stale = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);
        await File.WriteAllTextAsync(
            Path.Combine(bld.Path, ".git", "packed-refs"),
            "# pack-refs with: peeled fully-peeled sorted \n" + stale + " refs/heads/main\n",
            ct);

        await bld.Repo.PackRefsAsync(ct);

        string packed = await File.ReadAllTextAsync(Path.Combine(bld.Path, ".git", "packed-refs"), ct);
        Assert.Contains(history[0] + " refs/heads/main\n", packed, StringComparison.Ordinal);
        Assert.DoesNotContain(stale.ToString(), packed, StringComparison.Ordinal);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Merge state file tests. Ported from
/// <c>tests/libgit2/merge/workdir/setup.c</c>. Verifies that
/// <c>MERGE_HEAD</c>, <c>MERGE_MODE</c>, <c>MERGE_MSG</c>, and
/// <c>ORIG_HEAD</c> are written correctly by <see cref="GitRepository.MergeAsync"/>
/// and can be read back via <see cref="GitRepository.MergeHeadForEach"/>.
/// </summary>
public sealed class MergeSetupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    // branch = 7cb63eed597130ba4abb87b3e544b85021905520
    private static readonly GitOid s_branch = GitOid.Parse("7cb63eed597130ba4abb87b3e544b85021905520".AsSpan(), GitHashAlgorithmKind.Sha1);

    public MergeSetupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeSetup_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> OpenMergeResolveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-resolve.zip");
        _extractedPaths.Add(path);
        GitRepository repo = await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), new GitContext());
        await repo.Config.SetStringAsync("merge.conflictstyle", "merge");
        await repo.Config.SetBoolAsync("core.autocrlf", false);
        return repo;
    }

    private static async Task MergeBranchByOidAsync(GitRepository repo, GitOid theirOid)
    {
        await repo.ReferenceCreateSymbolicAsync(GitReferences.HeadFile, "refs/heads/master", force: true);
        await repo.CheckoutHeadAsync(new LibGit2CS.Checkout.GitCheckoutOptions
        {
            Strategy = LibGit2CS.Checkout.GitCheckoutStrategy.Force,
        });

        using GitAnnotatedCommit theirsHead = await repo.AnnotatedCommitLookupAsync(theirOid, TestContext.Current.CancellationToken);
        await repo.MergeAsync([theirsHead]);
    }

    // ── MERGE_HEAD / ORIG_HEAD / MERGE_MODE / MERGE_MSG ─────────────────

    [Fact]
    public async Task MergeHead_ContainsCorrectOid()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeBranchByOidAsync(repo, s_branch);

        List<GitOid> mergeHeads = await repo.MergeHeadForEachAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(mergeHeads);
        Assert.Equal(s_branch, mergeHeads[0]);
    }

    [Fact]
    public async Task OrigHead_ContainsOurHeadOid()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        // master = bd593285fc7fe4ca18ccdbabf027f5d689101452
        var masterOid = GitOid.Parse("bd593285fc7fe4ca18ccdbabf027f5d689101452".AsSpan(), GitHashAlgorithmKind.Sha1);
        await MergeBranchByOidAsync(repo, s_branch);

        string origHeadPath = Path.Combine(repo.Path, "ORIG_HEAD");
        Assert.True(File.Exists(origHeadPath));
        string content = (await File.ReadAllTextAsync(origHeadPath, cancellationToken: TestContext.Current.CancellationToken)).Trim();
        Assert.Equal(masterOid.ToString(), content);
    }

    [Fact]
    public async Task MergeMode_ContainsNoFf()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeBranchByOidAsync(repo, s_branch);

        string mergeModePath = Path.Combine(repo.Path, "MERGE_MODE");
        Assert.True(File.Exists(mergeModePath));
        string content = await File.ReadAllTextAsync(mergeModePath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("no-ff", content);
    }

    [Fact]
    public async Task MergeMsg_ContainsMergeMessage()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeBranchByOidAsync(repo, s_branch);

        string? msg = await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(msg);
        Assert.StartsWith("Merge", msg);
    }

    [Fact]
    public async Task MergeMsg_ContainsConflictsSection()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeBranchByOidAsync(repo, s_branch);

        string? msg = await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(msg);
        // The merge has conflicts on conflicting.txt.
        Assert.Contains("#Conflicts:", msg);
        Assert.Contains("conflicting.txt", msg);
    }

    [Fact]
    public async Task StateCleanup_RemovesMergeStateFiles()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeBranchByOidAsync(repo, s_branch);

        // Verify files exist.
        Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_HEAD")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_MODE")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));

        // Clean up.
        repo.StateCleanup();

        // Verify files are removed.
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_HEAD")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MODE")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));
    }

    // ── Multiple heads ──────────────────────────────────────────────────

    [Fact]
    public async Task MergeHeadForEach_IteratesAllHeads()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        // Manually write MERGE_HEAD with multiple OIDs.
        string masterOid = "bd593285fc7fe4ca18ccdbabf027f5d689101452";
        string branchOid = "7cb63eed597130ba4abb87b3e544b85021905520";
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), $"{masterOid}\n{branchOid}\n", cancellationToken: TestContext.Current.CancellationToken);

        List<GitOid> heads = await repo.MergeHeadForEachAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, heads.Count);
        Assert.Equal(GitOid.Parse(masterOid.AsSpan(), GitHashAlgorithmKind.Sha1), heads[0]);
        Assert.Equal(GitOid.Parse(branchOid.AsSpan(), GitHashAlgorithmKind.Sha1), heads[1]);
    }

    [Fact]
    public async Task MergeHeadForEach_EmptyWhenNoMergeHead()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        List<GitOid> heads = await repo.MergeHeadForEachAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(heads);
    }

    [Fact]
    public async Task MergeHeadForEach_NoTrailingNewline_ThrowsNoEol()
    {
        // C (merge.c:588-641) requires every line to end with a newline — leftover bytes abort with GIT_ERROR_MERGE "no EOL at line N".
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        string oid = "bd593285fc7fe4ca18ccdbabf027f5d689101452";
        // Write without trailing newline.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), oid, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitOid _ in repo.MergeHeadForEachAsync(cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Merge, ex.Category);
        Assert.Contains("no EOL at line 1", ex.Message);
    }

    // ── Merge message formatting ────────────────────────────────────────

    [Fact]
    public async Task MergeMessage_BranchRef()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        // Merge from a ref (not OID) — the message should say "Merge branch 'branch'".
        await repo.ReferenceCreateSymbolicAsync(GitReferences.HeadFile, "refs/heads/master", force: true, cancellationToken: TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new LibGit2CS.Checkout.GitCheckoutOptions
        {
            Strategy = LibGit2CS.Checkout.GitCheckoutStrategy.Force,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitReference theirRef = (await repo.ReferenceLookupAsync("refs/heads/branch", cancellationToken: TestContext.Current.CancellationToken))!;
        using GitAnnotatedCommit theirsHead = await repo.AnnotatedCommitFromRefAsync(theirRef, cancellationToken: TestContext.Current.CancellationToken);
        await repo.MergeAsync([theirsHead], cancellationToken: TestContext.Current.CancellationToken);

        string? msg = await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(msg);
        Assert.StartsWith("Merge branch 'branch'", msg);
    }

    [Fact]
    public async Task MergeMessage_OidCommit()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        // Merge by OID — the message should say "Merge commit '<oid>'".
        await MergeBranchByOidAsync(repo, s_branch);

        string? msg = await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(msg);
        Assert.StartsWith($"Merge commit '{s_branch}'", msg);
    }
}

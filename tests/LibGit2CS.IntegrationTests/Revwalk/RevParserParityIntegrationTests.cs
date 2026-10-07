using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// End-to-end tests for the revparse parity behaviors (libgit2 1.9.4)
/// against repos built from scratch with <see cref="RepoBuilder"/>:
/// empty-spec rejection, the
/// allowed <c>"..."</c> range, the <c>maybe_describe</c> fallback, error
/// codes for unresolvable/ambiguous specs, the unimplemented bare
/// <c>:path</c> form, annotated-tag peeling through commits, and the reflog
/// time-search edge cases.
/// </summary>
public sealed class RevParserParityIntegrationTests
{
    [Fact]
    public async Task EmptySpec_ThrowsInvalidSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await bld.Repo.RevparseSingleAsync("", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("failed to parse revision specifier - Invalid pattern ''", ex.Message);
    }

    [Fact]
    public async Task EmptySymmetricDifference_ResolvesToHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await bld.Repo.RevparseRangeAsync("...", ct);

        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(history[1], from!.Id);
        Assert.Equal(history[1], to!.Id);
        Assert.Equal(GitRevSpecFlags.Range | GitRevSpecFlags.MergeBase, flags);
    }

    [Fact]
    public async Task DescribeStyleSpec_ResolvesAbbreviatedTail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        string abbrev = history[1].ToString()[..8];
        using GitObject? obj = await bld.Repo.RevparseSingleAsync($"v1.0-5-g{abbrev}", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[1], obj!.Id);
    }

    [Fact]
    public async Task UnresolvableSpec_ThrowsNotFoundWithCMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await bld.Repo.RevparseSingleAsync("does-not-exist-xyz", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("revspec 'does-not-exist-xyz' not found", ex.Message);
    }

    [Fact]
    public async Task ShortHexAbbreviation_ThrowsAmbiguous()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await bld.Repo.RevparseSingleAsync("ab", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Ambiguous, ex.Code);
        Assert.Equal("ambiguous lookup - OID prefix is too short", ex.Message);
    }

    [Fact]
    public async Task BareColonPath_ThrowsUnimplemented()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await bld.Repo.RevparseSingleAsync(":some-file.txt", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("unimplemented", ex.Message);
    }

    [Fact]
    public async Task AnnotatedTag_CaretTree_PeelsThroughCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        using GitObject? commit = await bld.Repo.ObjectLookupAsync(history[0], ct).ConfigureAwait(false);
        Assert.NotNull(commit);

        _ = await bld.Repo.TagCreateAsync("v1.0", commit!, new GitSignature("t", "t@t", new GitTime(1700000000, 0)), "release v1.0\n", cancellationToken: ct);

        using GitObject? tree = await bld.Repo.RevparseSingleAsync("v1.0^{tree}", ct);

        Assert.NotNull(tree);
        Assert.Equal(GitObjectType.Tree, tree!.Type);
    }

    [Fact]
    public async Task AnnotatedTag_ColonPath_ResolvesThroughPeel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid commitOid = await bld.CommitFileAsync("hello.txt", "hello\n"u8.ToArray(), "commit\n", updateRef: "refs/heads/main", ct: ct);
        using GitObject? commit = await bld.Repo.ObjectLookupAsync(commitOid, ct).ConfigureAwait(false);
        Assert.NotNull(commit);

        _ = await bld.Repo.TagCreateAsync("v1.0", commit!, new GitSignature("t", "t@t", new GitTime(1700000000, 0)), "release v1.0\n", cancellationToken: ct);

        using GitObject? blob = await bld.Repo.RevparseSingleAsync("v1.0:hello.txt", ct);

        Assert.NotNull(blob);
        Assert.Equal(GitObjectType.Blob, blob!.Type);
    }

    [Fact]
    public async Task CaretParentMissing_ThrowsNotFoundWithCMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await bld.Repo.RevparseSingleAsync("HEAD^2", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("parent 1 does not exist", ex.Message);
    }

    [Fact]
    public async Task LeadingSlashTreePath_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await bld.Repo.RevparseSingleAsync("HEAD:/x", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ReflogTimestampBeyondInt32_ResolvesViaTimeSearch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        // 3000000000 > int32.MaxValue; C keeps the 64-bit timestamp for the
        // time-based reflog search, which matches the newest entry (the tip).
        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD@{3000000000}", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[1], obj!.Id);
    }

    [Fact]
    public async Task ReflogDateOlderThanAllEntries_ReturnsOldestEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(2, ct: ct);

        // C (revparse.c:247-255): no entry at or before the date → fall back
        // to the oldest entry (the last one examined in most-recent-first
        // order). Only the tip commit has a HEAD reflog entry here.
        GitRefLog? log = await bld.Repo.ReferenceReadLogAsync("HEAD", ct);
        Assert.NotNull(log);
        GitOid oldest = log![log.EntryCount - 1].NewId;

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD@{2000-01-01 00:00:00 +0000}", ct);

        Assert.NotNull(obj);
        Assert.Equal(oldest, obj!.Id);
    }

    [Fact]
    public async Task RevparseExt_BranchRefName_ReturnsBranchRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        (GitObject? obj, GitReference? reference) = await bld.Repo.RevparseExtAsync("refs/heads/main", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[0], obj!.Id);
        Assert.NotNull(reference);
        Assert.Equal("refs/heads/main", reference!.Name);
    }

    [Fact]
    public async Task RevparseExt_HeadSymbolic_ReturnsBranchRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        // "HEAD" is a symbolic ref; DWIM resolves it through to the branch,
        // so git_revparse_ext reports the branch ref (not HEAD itself).
        (GitObject? obj, GitReference? reference) = await bld.Repo.RevparseExtAsync("HEAD", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[0], obj!.Id);
        Assert.NotNull(reference);
        Assert.Equal("refs/heads/main", reference!.Name);
    }
}

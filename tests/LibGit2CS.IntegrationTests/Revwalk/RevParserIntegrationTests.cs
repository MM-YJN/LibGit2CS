using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// Integration tests for <see cref="GitRevParser"/> exercised end-to-end
/// against locally-initialized non-bare repos built from scratch via
/// <see cref="RepoBuilder"/>. Covers <see cref="GitRevParser.ParseSingleAsync"/>
/// (HEAD, <c>~N</c>, <c>^N</c>, <c>^{type}</c>, DWIM refs, abbreviated OIDs)
/// and <see cref="GitRevParser.ParseRangeAsync"/> (<c>A..B</c>,
/// <c>A...B</c>, bare specs).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project (<c>RevParserTests</c>)
/// runs against <c>testrepo.zip</c> with a fixed commit graph and covers the
/// full grammar but not against dynamically-built multi-commit history. The
/// integration coverage was zero — <see cref="GitRevParser"/> is at 0% line
/// coverage. These tests close the gap without Docker by building repos
/// locally and exercising the parser through the public API.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build. Mirrors
/// <see cref="Transports.LocalTransportTests"/> and
/// <see cref="Objects.TagIntegrationTests"/>.
/// </para>
/// </remarks>
public sealed class RevParserIntegrationTests
{
    // ── A. ParseSingleAsync ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with <c>"HEAD"</c>
    /// resolves to the commit at HEAD. After <see cref="RepoBuilder.BuildLinearHistoryAsync"/>,
    /// HEAD is the tip commit.
    /// </summary>
    [Fact]
    public async Task ParseSingle_Head_ReturnsHeadCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD", ct);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Commit, obj!.Type);
        Assert.Equal(history[2], obj.Id);
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with <c>"HEAD~2"</c>
    /// resolves to the grandparent (root) of a 3-commit linear history.
    /// </summary>
    [Fact]
    public async Task ParseSingle_TildeN_ReturnsNthAncestor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD~2", ct);
        Assert.NotNull(obj);
        Assert.Equal(history[0], obj!.Id);
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with <c>"HEAD^1"</c>
    /// resolves to the first parent of HEAD (the middle commit in a
    /// 3-commit linear history).
    /// </summary>
    [Fact]
    public async Task ParseSingle_CaretN_ReturnsNthParent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD^1", ct);
        Assert.NotNull(obj);
        Assert.Equal(history[1], obj!.Id);
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with
    /// <c>"HEAD^{tree}"</c> dereferences HEAD to its tree object.
    /// </summary>
    [Fact]
    public async Task ParseSingle_CaretTreeType_DereferencesToTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        // Resolve HEAD commit to get its tree OID for comparison.
        Commit head = await bld.Repo.ObjectLookupAsync<Commit>(history[0], ct) ?? throw new InvalidOperationException("HEAD missing");
        GitOid treeOid = head.Tree;
        head.Dispose();

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD^{tree}", ct);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Tree, obj!.Type);
        Assert.Equal(treeOid, obj.Id);
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with a bare branch name
    /// resolves via DWIM (Direct With Implicit Meaning) to
    /// <c>refs/heads/&lt;name&gt;</c>. After <see cref="RepoBuilder.BuildLinearHistoryAsync"/>,
    /// <c>"main"</c> resolves to the tip.
    /// </summary>
    [Fact]
    public async Task ParseSingle_DwimRef_ResolvesBranchName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("main", ct);
        Assert.NotNull(obj);
        Assert.Equal(history[2], obj!.Id);
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with an abbreviated OID
    /// (8-char prefix) resolves to the full commit. A 3-commit history
    /// yields distinct commits; the prefix is unique.
    /// </summary>
    [Fact]
    public async Task ParseSingle_AbbreviatedOid_ResolvesUniquely()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        string abbrev = history[1].ToString().AsSpan(0, 8).ToString();
        using GitObject? obj = await bld.Repo.RevparseSingleAsync(abbrev, ct);
        Assert.NotNull(obj);
        Assert.Equal(history[1], obj!.Id);
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseSingleAsync"/> with a nonexistent ref
    /// throws <see cref="GitException"/> with <see cref="GitErrorCode.NotFound"/>
    /// — C's <c>git_revparse_single</c> propagates GIT_ENOTFOUND from
    /// <c>git_revparse_ext</c> (revparse.c:889-903), and negative codes become
    /// exceptions per the AGENTS.md error convention. The message matches C's
    /// <c>"revspec '%s' not found"</c>.
    /// </summary>
    [Fact]
    public async Task ParseSingle_NonexistentRef_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => bld.Repo.RevparseSingleAsync("does-not-exist", ct));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("revspec 'does-not-exist' not found", ex.Message);
    }

    // ── B. ParseRangeAsync ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevParser.ParseRangeAsync"/> with <c>"A..B"</c>
    /// returns <see cref="GitRevSpecFlags.Range"/> with <c>From=A</c> and
    /// <c>To=B</c>.
    /// </summary>
    [Fact]
    public async Task ParseRange_ATwoDotsB_ReturnsRangeFlag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await bld.Repo.RevparseRangeAsync($"{history[0]}..{history[2]}", ct);

        Assert.Equal(GitRevSpecFlags.Range, flags);
        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(history[0], from!.Id);
        Assert.Equal(history[2], to!.Id);
        from?.Dispose();
        to?.Dispose();
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseRangeAsync"/> with <c>"A...B"</c>
    /// (three dots) returns <see cref="GitRevSpecFlags.Range"/> |
    /// <see cref="GitRevSpecFlags.MergeBase"/>.
    /// </summary>
    [Fact]
    public async Task ParseRange_AThreeDotsB_ReturnsMergeBaseFlag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await bld.Repo.RevparseRangeAsync($"{history[0]}...{history[2]}", ct);

        Assert.Equal(GitRevSpecFlags.Range | GitRevSpecFlags.MergeBase, flags);
        Assert.NotNull(from);
        Assert.NotNull(to);
        from?.Dispose();
        to?.Dispose();
    }

    /// <summary>
    /// <see cref="GitRevParser.ParseRangeAsync"/> with a bare spec (no
    /// <c>..</c> or <c>...</c>) returns <see cref="GitRevSpecFlags.Single"/>
    /// with the object in <c>To</c> and <c>From</c> null.
    /// </summary>
    [Fact]
    public async Task ParseRange_BareSpec_ReturnsSingleFlag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await bld.Repo.RevparseRangeAsync("HEAD", ct);

        Assert.Equal(GitRevSpecFlags.Single, flags);
        Assert.NotNull(from);
        Assert.Null(to);
        Assert.Equal(history[0], from!.Id);
        from?.Dispose();
    }
}

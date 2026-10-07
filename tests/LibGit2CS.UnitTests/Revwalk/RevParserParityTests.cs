using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Revwalk;

/// <summary>
/// Regression tests for the revparse parity behaviors in
/// libgit2 1.9.4. All expected values
/// (oids, error codes, messages) were differentially verified against the C
/// reference (libgit2 1.9.4) with a git_revparse_single/git_revparse harness
/// against the same testrepo fixture.
/// </summary>
public sealed class RevParserParityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RevParserParity_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _extractedPaths = [];

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

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }

    // C-verified fixture oids.
    private static readonly GitOid s_master = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_readmeBlob = GitOid.Parse("a8233120f6ad708f843d861ce2b7228ec4e3dec6".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_e90810bTag = GitOid.Parse("7b4384978d2493e851f9cca7858815fac9b10980".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_e90810bTree = GitOid.Parse("53fc32d17276939fc79ed05badaef2db09990016".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_blob1385 = GitOid.Parse("1385f264afb75a56a5bec74243be9b367ba4ca08".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_5b5b025 = GitOid.Parse("5b5b025afb0b4c913b4c338a42934a3863bf3644".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_be3563ae = GitOid.Parse("be3563ae3f795b2b4353bcce3a527ad0a4f7f644".AsSpan(), GitHashAlgorithmKind.Sha1);

    private static GitException AssertGitException(Func<Task> action, GitErrorCode code)
    {
        GitException ex = Assert.ThrowsAsync<GitException>(action).GetAwaiter().GetResult();
        Assert.Equal(code, ex.Code);
        return ex;
    }

    // ── empty spec → GIT_EINVALIDSPEC (was: resolves to HEAD) ──

    [Fact]
    public async Task EmptySpec_ThrowsInvalidSpec()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync("", TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("failed to parse revision specifier - Invalid pattern ''", ex.Message);
    }

    // ── "..." allowed, ".." rejected ──

    [Fact]
    public async Task EmptySymmetricDifference_ResolvesToHead()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await repo.RevparseRangeAsync("...", TestContext.Current.CancellationToken);

        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(s_master, from!.Id);
        Assert.Equal(s_master, to!.Id);
        Assert.Equal(GitRevSpecFlags.Range | GitRevSpecFlags.MergeBase, flags);
    }

    [Fact]
    public async Task BareDoubleDot_ThrowsInvalidSpec()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { _ = await repo.RevparseRangeAsync("..", TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("invalid pattern '..'", ex.Message);
    }

    // ── maybe_describe fallback ──

    [Fact]
    public async Task DescribeStyleSpec_ResolvesAbbreviatedTail()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("v1.0-5-g5b5b025af", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_5b5b025, obj!.Id);
    }

    [Fact]
    public async Task DescribeStyleSpec_UnknownTail_ThrowsNotFound()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync("v1.0-5-g1234567", TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("revspec 'v1.0-5-g1234567' not found", ex.Message);
    }

    // ── unresolvable specs → GIT_ENOTFOUND "revspec '%s' not found" ──

    [Theory]
    [InlineData("nonexistent-ref-xyz")]
    [InlineData("deadbeef")]
    [InlineData("dead")]
    public async Task UnresolvableSpec_ThrowsNotFoundWithCMessage(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal($"revspec '{spec}' not found", ex.Message);
    }

    // ── 1-3 hex char abbreviations → GIT_EAMBIGUOUS ──

    [Theory]
    [InlineData("a")]
    [InlineData("a6")]
    [InlineData("dea")]
    public async Task ShortAbbreviation_ThrowsAmbiguous(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.Ambiguous, ex.Code);
        Assert.Equal("ambiguous lookup - OID prefix is too short", ex.Message);
    }

    // ── Odd-length abbreviations resolve (C probe: 7 chars → object) ─────

    [Fact]
    public async Task OddLengthAbbreviation_Resolves()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // C probe: git_oid__fromstrn parses odd-length
        // prefixes (last byte's low nibble zeroed) and the lookup resolves.
        using GitObject head = await repo.RevparseSingleAsync("HEAD", TestContext.Current.CancellationToken)
            ?? throw new Xunit.Sdk.XunitException("HEAD must resolve");
        string prefix = head.Id.ToString()[..7];

        using GitObject? obj = await repo.RevparseSingleAsync(prefix, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(head.Id, obj!.Id);
    }

    // ── bare :path / :stage:path → "unimplemented" GIT_ERROR ──

    [Theory]
    [InlineData(":README")]
    [InlineData(":2:README")]
    public async Task BareColonPath_ThrowsUnimplemented(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("unimplemented", ex.Message);
    }

    // ── ^{tree}/^{blob}/tag:path on annotated tags peel through ──

    [Fact]
    public async Task AnnotatedTagCaretTree_PeelsThroughCommit()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("e90810b^{tree}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Tree, obj!.Type);
        Assert.Equal(s_e90810bTree, obj.Id);
    }

    [Fact]
    public async Task AnnotatedTagToBlob_CaretBlob_ResolvesBlob()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("annotated_tag_to_blob^{blob}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
        Assert.Equal(s_blob1385, obj.Id);
    }

    [Fact]
    public async Task WrappedTagCaretCommit_Resolves()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("wrapped_tag^{commit}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task TagColonPath_ResolvesThroughPeel()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("wrapped_tag:README", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_readmeBlob, obj!.Id);
    }

    [Fact]
    public async Task PeelToWrongType_ThrowsPeel()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // tag→commit→tree, then deref(tree) fails with GIT_EPEEL (object.c:374).
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync("e90810b^{blob}", TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.Peel, ex.Code);
    }

    // ── cross-type peel → GIT_EINVALIDSPEC (was GIT_EPEEL) ──

    [Theory]
    [InlineData("master^{blob}")]
    [InlineData("master^{tag}")]
    [InlineData("944c0f6e4dfa41595e6eb3ceecdb14f50fe18162^{commit}")]
    public async Task CrossTypePeel_ThrowsInvalidSpec(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal($"failed to parse revision specifier - Invalid pattern '{spec}'", ex.Message);
    }

    // ── @{date} older than the reflog returns the oldest entry ──

    [Fact]
    public async Task ReflogDateOlderThanAllEntries_ReturnsOldestEntry()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("HEAD@{2000-01-01 00:00:00 +0000}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_be3563ae, obj!.Id);
    }

    [Fact]
    public async Task ReflogIndexBeyondEntries_ThrowsNotFoundWithCMessage()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync("HEAD@{100000000}", TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("reflog for 'HEAD' has only 7 entries, asked for 100000000", ex.Message);
    }

    // ── ^N past the last parent → GIT_ENOTFOUND ──

    [Theory]
    [InlineData("master^2", "parent 1 does not exist")]
    [InlineData("master^3", "parent 2 does not exist")]
    public async Task CaretParentMissing_ThrowsNotFoundWithCMessage(string spec, string expectedMessage)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(expectedMessage, ex.Message);
    }

    // ── leading/doubled slashes in treeish:path fail ──

    [Theory]
    [InlineData("HEAD:/x")]
    [InlineData("HEAD://README")]
    public async Task LeadingSlashTreePath_ThrowsNotFound(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── EINVALIDSPEC messages wrapped like C ──

    [Theory]
    [InlineData("^")]
    [InlineData("~")]
    public async Task BareOperator_ThrowsWrappedInvalidSpec(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal($"failed to parse revision specifier - Invalid pattern '{spec}'", ex.Message);
    }

    // ── @{N} numeric detection rejects whitespace ──

    [Fact]
    public async Task AtNumericWithWhitespace_GoesThroughDatePath()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // " 1 " is not numeric in C → date/approxidate path → newest entry.
        GitObject? obj = await repo.RevparseSingleAsync("HEAD@{ 1 }", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    // ── @{<timestamp>} must not narrow to int32 ──

    [Fact]
    public async Task AtTimestampBeyondInt32_ResolvesViaTimeSearch()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("HEAD@{3000000000}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    // ── invalid ^{/regex} / :/regex → GIT_EINVALIDSPEC ──

    [Theory]
    [InlineData("HEAD^{/[}")]
    [InlineData("HEAD^{/}")]
    public async Task InvalidGrepPattern_ThrowsWrappedInvalidSpec(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync(spec, TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal($"failed to parse revision specifier - Invalid pattern '{spec}'", ex.Message);
    }

    // ── RevparseExt: git_revparse_ext reference_out ──

    [Fact]
    public async Task RevparseExt_AtUpstream_ReturnsUpstreamRef()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await repo.Config.SetMultiAsync("remote.origin.fetch", new Regex("^$"), "+refs/heads/*:refs/remotes/origin/*", ct);
        await repo.Config.SetStringAsync("branch.master.remote", "origin", ct);
        await repo.Config.SetStringAsync("branch.master.merge", "refs/heads/master", ct);
        _ = await repo.ReferenceCreateAsync("refs/remotes/origin/master", s_master, cancellationToken: ct);

        (GitObject? obj, GitReference? reference) = await repo.RevparseExtAsync("HEAD@{u}", ct);

        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
        Assert.NotNull(reference);
        Assert.Equal("refs/remotes/origin/master", reference!.Name);
    }

    [Fact]
    public async Task RevparseExt_PlainRefName_ReturnsBranchRef()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        (GitObject? obj, GitReference? reference) = await repo.RevparseExtAsync("master", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
        Assert.NotNull(reference);
        Assert.Equal("refs/heads/master", reference!.Name);
    }

    [Fact]
    public async Task RevparseExt_HeadReflogIndex_ReturnsNullReference()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // C (revparse.c:271-307): retrieve_revobject_from_reflog never sets
        // *base_ref when it looked the ref up itself — HEAD@{1} yields a
        // null reference_out even though the object resolves via the HEAD
        // reflog.
        (GitObject? obj, GitReference? reference) = await repo.RevparseExtAsync("HEAD@{1}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Null(reference);
    }

    [Theory]
    [InlineData("HEAD~1")]
    [InlineData("a65fedf3")]
    public async Task RevparseExt_OperatorOrAbbrev_ReturnsNullReference(string spec)
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        (GitObject? obj, GitReference? reference) = await repo.RevparseExtAsync(spec, TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Null(reference);
        if (spec == "a65fedf3")
        {
            Assert.Equal(s_master, obj!.Id);
        }
    }

    // ── Guards: revparse behaviors that must stay fixed ──

    [Fact]
    public async Task Guard_TagColonPath_MissingPath_ThrowsNotFound()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // e90810b's tree has no README (C: "the path 'README' does not exist...").
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { using GitObject? _ = await repo.RevparseSingleAsync("e90810b:README", TestContext.Current.CancellationToken).ConfigureAwait(false); });

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Guard_ReflogDateInRange_Resolves()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("HEAD@{2005-04-07 22:40:00 +0000}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_be3563ae, obj!.Id);
    }

    [Fact]
    public async Task Guard_AnnotatedTagCaretTag_ReturnsTagItself()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitObject? obj = await repo.RevparseSingleAsync("e90810b^{tag}", TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(s_e90810bTag, obj!.Id);
    }
}

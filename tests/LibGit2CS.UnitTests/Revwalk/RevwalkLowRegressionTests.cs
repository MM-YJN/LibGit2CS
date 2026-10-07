using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using GitIndex = LibGit2CS.Index.GitIndex;
using RebaseOps = LibGit2CS.Rebase.GitRebase;

namespace LibGit2CS.UnitTests.Revwalk;

// Parity cases verified against libgit2 1.9.4:
//  - ExtractHowMany's unchecked int accumulation wrapped ~N negative
//    and silently resolved to the base commit instead of failing
//    (revparse.c:591-620).
//  - the walk's enumerating-phase ODB/commit-graph reads ran with a
//    default CancellationToken (GitRevWalker.cs:415, 435).
//  - rewritten-file parsing accepted abbreviated OIDs and threw
//    unhandled FormatException on corrupt rebase state (rebase.c:1334-1350).
//  - DereferenceToNonTagAsync capped tag-chain peeling at depth 50,
//    returning a tag where C peels to the end (revparse.c:400-406).
public sealed class RevwalkLowRegressionTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RevwalkLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RevwalkLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitSignature TestSig() => new("T", "t@x.com", new GitTime(100, 0));

    private async Task<GitOid> CommitFileAsync(string fileName, string content, string message, string refName)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync(refName, TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    // ---- ~N overflow is rejected ----

    [Fact]
    public async Task Revparse_OverflowingTildeCount_ThrowsInvalidSpec()
    {
        GitOid c1 = await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        // ~2147483647~2147483647 wraps the accumulated count negative; C
        // passes the wrapped value to git_commit_nth_gen_ancestor as a huge
        // unsigned count and fails with GIT_ENOTFOUND — a signed
        // compare would execute zero iterations and return HEAD.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.RevparseSingleAsync("HEAD~2147483647~2147483647", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task Revparse_NormalTildeCount_StillWorks()
    {
        // Control: a normal ~N spec still resolves.
        await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        GitObject? obj = await _repo.RevparseSingleAsync("HEAD~0", TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
    }

    // ---- the walk honors the caller's token ----

    [Fact]
    public async Task Walk_PreCancelledToken_Throws()
    {
        GitOid c1 = await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        using GitRevWalker walk = _repo.NewRevWalker();
        await walk.PushAsync(c1, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (GitOid _ in walk.WalkAsync(cts.Token))
            {
            }
        });
    }

    // ---- rewritten-file OIDs must be exact-hexsize ----

    [Fact]
    public async Task Finish_AbbreviatedRewrittenOid_Throws()
    {
        GitOid c2 = await SetupRebaseAsync();
        await _repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/commits", TestContext.Current.CancellationToken);

        using RebaseOps rebase = await StartRebaseAsync();
        _ = await rebase.CommitAsync(null, TestSig(), cancellationToken: TestContext.Current.CancellationToken); // writes the rewritten file

        // Tamper: replace the rewritten file's OIDs with abbreviated ones.
        // C's rebase_copy_notes requires exact hexsize and fails with
        // 'invalid rewritten file at line %d' (rebase.c:1334-1350);
        // silently padding would finish successfully.
        string rewrittenPath = Path.Combine(_repo.Path, "rebase-merge", "rewritten");
        string content = await File.ReadAllTextAsync(rewrittenPath, TestContext.Current.CancellationToken);
        string[] parts = content.Split(' ', 2);
        await File.WriteAllTextAsync(
            rewrittenPath, parts[0][..7] + " " + parts[1][..7] + "\n",
            TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("invalid rewritten file at line 1", ex.Message);
    }

    [Fact]
    public async Task Finish_NonHexRewrittenOid_ThrowsRebaseException()
    {
        GitOid c2 = await SetupRebaseAsync();
        await _repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/commits", TestContext.Current.CancellationToken);

        using RebaseOps rebase = await StartRebaseAsync();
        _ = await rebase.CommitAsync(null, TestSig(), cancellationToken: TestContext.Current.CancellationToken);

        // Non-hex content must surface as a Rebase-category GitException,
        // not an unhandled FormatException.
        string rewrittenPath = Path.Combine(_repo.Path, "rebase-merge", "rewritten");
        string content = await File.ReadAllTextAsync(rewrittenPath, TestContext.Current.CancellationToken);
        string[] parts = content.Split(' ', 2);
        string bad = new('z', 40);
        await File.WriteAllTextAsync(
            rewrittenPath, bad + " " + parts[1] + "\n",
            TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Rebase, ex.Category);
    }

    // ---- tag chains deeper than 50 peel to the end ----

    [Fact]
    public async Task Revparse_DeepTagChain_PeelsToCommit()
    {
        GitOid c1 = await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");

        // 51 nested tags: t0 → commit, t1 → t0, …, t50 → t49.
        GitObject target = (await _repo.ObjectLookupAsync(c1, TestContext.Current.CancellationToken))!;
        for (int i = 0; i < 51; i++)
        {
            GitOid tagOid = await _repo.TagCreateAsync(
                $"t{i}", target, TestSig(), $"tag {i}\n", cancellationToken: TestContext.Current.CancellationToken);
            target.Dispose();
            target = (await _repo.ObjectLookupAsync(tagOid, TestContext.Current.CancellationToken))!;
        }

        using (target)
        {
            // C's dereference_to_non_tag peels unboundedly (revparse.c:400-406);
            // stopping at depth 50 would return a GitTag.
            GitObject? result = await _repo.RevparseSingleAsync("refs/tags/t50^{}", TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.IsType<Commit>(result);
        }
    }

    // ---- ^{/regex} matches in the byte domain ---- handle_grep_syntax runs git_regexp_match over the raw message bytes (revparse.c:488); RegexAdapter now
    // matches bytes via the byte↔char bijection and Compile UTF-8-encodes the pattern (the byte-domain contract), so a Unicode-literal pattern matches UTF-8
    // message bytes exactly as C's pattern bytes would.

    [Fact]
    public async Task Revparse_Grep_AsciiPattern_MatchStillWorks()
    {
        // Control: an ASCII pattern still finds the commit (byte/string
        // overloads are identical on ASCII).
        await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        GitObject? obj = await _repo.RevparseSingleAsync("HEAD^{/base}", TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.IsType<Commit>(obj);
    }

    [Fact]
    public async Task Revparse_Grep_UnicodeLiteralPattern_MatchesUtf8Message()
    {
        // The pattern string "café" is UTF-8-encoded into the byte domain, so it matches the UTF-8 message bytes C3 A9 (the byte-domain contract — C's pattern
        // bytes would match the same raw bytes).
        await CommitFileAsync("f.txt", "f\n", "caf\u00e9\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        GitObject? obj = await _repo.RevparseSingleAsync("HEAD^{/caf\u00e9}", TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.IsType<Commit>(obj);
    }

    [Fact]
    public async Task Revparse_Grep_NonMatchingPattern_ThrowsNotFound()
    {
        await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.RevparseSingleAsync("HEAD^{/no-such-message-xyz}", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ---- rebase helpers (mirror RebaseLowParityTests) ----

    private async Task<GitOid> SetupRebaseAsync()
    {
        GitOid c1 = await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");
        await _repo.BranchCreateAsync("feature", c1, force: false, TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/feature", TestContext.Current.CancellationToken);
        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);
        return await CommitFileAsync("g.txt", "g\n", "feature message\n", "refs/heads/feature");
    }

    private async Task<RebaseOps> StartRebaseAsync()
    {
        GitAnnotatedCommit branch = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/feature", TestContext.Current.CancellationToken))!);
        GitAnnotatedCommit upstream = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken))!);
        RebaseOps rebase = await RebaseOps.InitAsync(_repo, branch, upstream, onto: null, options: null, cancellationToken: TestContext.Current.CancellationToken);
        _ = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        return rebase;
    }
}

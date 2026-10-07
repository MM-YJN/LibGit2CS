using System.Reflection;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Refs;

/// <summary> Parity tests for refs/revwalk/notes: (DwimOne expands an empty destination to refs/heads/), (ref@{ N}
/// leading-whitespace numeric spec), (error categories for ~N / ^N /:path / @{-N}), (SetUpstream remote derived by refspec matching with ambiguity
/// detection). Expectations are C-verified against libgit2 1.9.4 (refspec.c:425-437; revparse.c:344-350, 147-212; commit.c:704-708; tree.c:937-939;
/// branch.c:557-626). </summary>
public sealed class RefsLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public RefsLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefsLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateRepoAsync(string name)
        => await GitRepository.InitAsync(Path.Combine(_tempDir, name), isBare: false, new GitContext(), TestContext.Current.CancellationToken);

    private static async ValueTask<GitOid> CommitFileAsync(GitRepository repo, string path, string content, string message)
    {
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync(path, blob, GitFileMode.Regular);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);

        // Chain onto the current tip so the reflog grows and ^N has parents.
        GitReference? tip = await repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken).ConfigureAwait(false);
        GitOid[] parents = tip is GitDirectReference d ? [d.Target] : [];

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/master",
        });
    }

    // ── DwimOne empty destination stays empty (C-equivalent) ──

    [Fact]
    public void DwimOne_EmptyDestination_StaysEmpty()
    {
        // C (refspec.c:54-58, 425-437): the parse leaves dst NULL for a fetch spec with no RHS OR an explicit empty RHS, and dwim_one expands only a NON-NULL
        // dst — so "refs/heads/x:" keeps its empty destination after dwim.
        var spec = GitRefSpec.Parse("refs/heads/x:", isFetch: true);
        var noColon = GitRefSpec.Parse("refs/heads/x", isFetch: true);

        Assert.Equal("", spec.DwimOne(Array.Empty<string>()).Destination);
        Assert.Equal("", noColon.DwimOne(Array.Empty<string>()).Destination);

        // A NON-empty shorthand RHS still expands.
        var rhs = GitRefSpec.Parse("refs/heads/x:dev", isFetch: true);
        Assert.Equal("refs/heads/dev", rhs.DwimOne(Array.Empty<string>()).Destination);
    }

    // ── ref@{<space>N} leading-whitespace numeric spec ─────────

    [Fact]
    public async Task Revparse_AtWithLeadingSpaceNumeric_Resolves()
    {
        // C (revparse.c:344-350, util.c:36-124): try_parse_numeric → git__strntol32 skips leading whitespace, so "@{ 1}" IS numeric.
        // int.TryParse(AllowLeadingSign) rejected the space and the spec fell through to date parsing.
        await using GitRepository repo = await CreateRepoAsync("repo0");
        GitOid first = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");
        _ = await CommitFileAsync(repo, "f.txt", "v2\n", "m2\n");

        GitObject? plain = await repo.RevparseSingleAsync("HEAD@{1}", TestContext.Current.CancellationToken);
        GitObject? spaced = await repo.RevparseSingleAsync("HEAD@{ 1}", TestContext.Current.CancellationToken);

        Assert.NotNull(plain);
        Assert.NotNull(spaced);
        Assert.Equal(first, spaced!.Id);
    }

    [Fact]
    public void TryParseStrntol32_WhitespaceSignAndFullConsumption()
    {
        // C (revparse.c:344-350, util.c:36-124): try_parse_numeric skips leading ASCII whitespace, accepts a sign, and requires the digits to consume the whole
        // string.
        Type parserType = typeof(GitRevParser);
        MethodInfo method = parserType.GetMethod("TryParseStrntol32", BindingFlags.NonPublic | BindingFlags.Static)!;
        TryParseNumericDelegate del = method.CreateDelegate<TryParseNumericDelegate>()!;

        Assert.True(del(" 5", out int v1) && v1 == 5);      // leading space
        Assert.True(del("\t+7", out int v2) && v2 == 7);    // tab + sign
        Assert.True(del("-3", out int v3) && v3 == -3);      // sign only
        Assert.False(del("5 ", out _));                      // trailing junk
        Assert.False(del("", out _));                        // empty
        Assert.False(del("99999999999", out _));             // int32 overflow
    }

    private delegate bool TryParseNumericDelegate(string content, out int value);

    // ── error categories ───────────────────────────────────────

    [Fact]
    public async Task Revparse_TildePastRoot_InvalidCategory()
    {
        // C (commit.c:704-708): git_commit_parent → GIT_ERROR_INVALID "parent 0 does not exist" (GIT_ENOTFOUND).
        await using GitRepository repo = await CreateRepoAsync("repo1");
        _ = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RevparseSingleAsync("HEAD~1", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    [Fact]
    public async Task Revparse_CaretPastLastParent_InvalidCategory()
    {
        // C (commit.c:704-708): "parent N does not exist" with GIT_ERROR_INVALID.
        await using GitRepository repo = await CreateRepoAsync("repo2");
        _ = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");
        _ = await CommitFileAsync(repo, "f.txt", "v2\n", "m2\n"); // 1 parent

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RevparseSingleAsync("HEAD^2", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    [Fact]
    public async Task Revparse_ColonMissingPath_TreeCategory()
    {
        // C (tree.c:937-939): git_tree_entry_bypath → GIT_ERROR_TREE "the path '...' does not exist in the given tree".
        await using GitRepository repo = await CreateRepoAsync("repo3");
        _ = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RevparseSingleAsync("HEAD:missing.txt", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Tree, ex.Category);
        Assert.Contains("does not exist in the given tree", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revparse_AtMinusNNoReflog_InvalidCategory()
    {
        // C (revparse.c:147-212): with no previous checkout the parse ends with GIT_ERROR_INVALID.
        await using GitRepository repo = await CreateRepoAsync("repo4");
        _ = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RevparseSingleAsync("@{-5}", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    // ── SetUpstream remote via refspec matching ────────────────

    [Fact]
    public async Task SetUpstream_RemoteDerivedByRefspecMatch()
    {
        // C (branch.c:557-626): the remote is found by matching the upstream ref against each remote's fetch destination refspec. Two remotes mapping to the
        // same upstream are GIT_EAMBIGUOUS.
        await using GitRepository repo = await CreateRepoAsync("repo5");
        GitOid commit = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");
        await repo.ReferenceCreateAsync("refs/remotes/origin/master", commit, cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("remote.origin.url", "file:///tmp/origin.git", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);

        await repo.BranchCreateAsync("trackme", commit, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? branch = await repo.BranchLookupAsync("trackme", GitBranchType.Local, TestContext.Current.CancellationToken);
        Assert.NotNull(branch);

        await branch!.SetUpstreamAsync("origin/master", TestContext.Current.CancellationToken);

        Assert.Equal("origin", await repo.Config.GetStringAsync("branch.trackme.remote", TestContext.Current.CancellationToken));
        Assert.Equal("refs/heads/master", await repo.Config.GetStringAsync("branch.trackme.merge", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetUpstream_AmbiguousRemote_ThrowsAmbiguous()
    {
        // C (branch.c:605-610): two remotes whose fetch refspecs both match the upstream ref → GIT_EAMBIGUOUS "reference '...' is ambiguous".
        await using GitRepository repo = await CreateRepoAsync("repo6");
        GitOid commit = await CommitFileAsync(repo, "f.txt", "v1\n", "m1\n");
        await repo.ReferenceCreateAsync("refs/remotes/origin/master", commit, cancellationToken: TestContext.Current.CancellationToken);

        // Both remotes fetch into refs/remotes/origin/* — both match the
        // upstream ref.
        await repo.Config.SetStringAsync("remote.a.url", "file:///tmp/a.git", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("remote.a.fetch", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("remote.b.url", "file:///tmp/b.git", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("remote.b.fetch", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);

        await repo.BranchCreateAsync("trackme", commit, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? branch = await repo.BranchLookupAsync("trackme", GitBranchType.Local, TestContext.Current.CancellationToken);
        Assert.NotNull(branch);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await branch!.SetUpstreamAsync("origin/master", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Ambiguous, ex.Code);
        Assert.Contains("is ambiguous", ex.Message, StringComparison.Ordinal);
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Patch-id computation tests. Matches <c>git patch-id</c> byte-exact.

/// </summary>
/// <remarks>
/// Reference patch-ids were computed via system <c>git</c>:
/// <code>
/// git init &amp;&amp; printf 'hello\nworld\n' &gt; a.txt &amp;&amp; git add. &amp;&amp; git commit -m first
/// printf 'hello\nworld\nmore\n' &gt; a.txt &amp;&amp; git add. &amp;&amp; git commit -m second
/// git diff HEAD~1 HEAD | git patch-id
/// # -&gt; 2965928b3bcaf17bc4510c683af635bf3f806902
/// </code>
/// Tests use a real repository (not <see cref="GitDiff.Buffers"/>) because
/// <see cref="GitPatch.FromDiffAsync"/> requires a generator-backed diff (blob/buffer diffs
/// have no <c>PatchSource</c>).
/// </remarks>
public sealed class PatchIdTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private GitTree _tree1 = null!;
    private GitTree _tree2 = null!;

    public PatchIdTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PatchId_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        _tree1 = await CommitFileContentAsync("hello\nworld\n", "first");
        _tree2 = await CommitFileContentAsync("hello\nworld\nmore\n", "second");
    }

    public async ValueTask DisposeAsync()
    {
        _tree1.Dispose();
        _tree2.Dispose();
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    // Reference patch-id of `git diff commit1 commit2` (forward: adds "more\n" line).
    private const string ExpectedForwardPatchId = "2965928b3bcaf17bc4510c683af635bf3f806902";

    // Reference patch-id of the reverse diff (removes "more\n").
    private const string ExpectedReversePatchId = "287cef048adddb4fbc80a4bedf1447765cfd6125";

    [Fact]
    public async Task Compute_SimpleAddition_MatchesGitPatchId()
    {
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(_tree1, _tree2, cancellationToken: TestContext.Current.CancellationToken);

        GitOid patchId = await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitHashAlgorithmKind.Sha1, patchId.Algorithm);
        Assert.Equal(ExpectedForwardPatchId, patchId.ToString());
    }

    [Fact]
    public async Task Compute_ParsedSimpleCommit_MatchesLibgit2Reference()
    {
        // Reference from libgit2 tests/libgit2/diff/patchid.c:21
        // git_diff_patchid on PATCH_SIMPLE_COMMIT = 06094b1948b878b7d9ff7560b4eae672a014b0ec
        // The patch text is from tests/libgit2/patch/patch_common.h:420-440.
        // Note: the commit header (Author/Date/message) is skipped by the patch parser;
        // only the "diff --git ..." part is parsed.
        string patchText = new StringBuilder()
            .Append("diff --git a/CHANGELOG.md b/CHANGELOG.md\n")
            .Append("index 1b9e0c90a..24ecba426 100644\n")
            .Append("--- a/CHANGELOG.md\n")
            .Append("+++ b/CHANGELOG.md\n")
            .Append("@@ -96,6 +96,9 @@ v0.26\n")
            .Append(" * `git_transport_smart_proxy_options()' enables you to get the proxy options for\n")
            .Append("   smart transports.\n")
            .Append('\n')
            .Append("+* The `GIT_FILTER_INIT` macro and the `git_filter_init` function are provided\n")
            .Append("+  to initialize a `git_filter` structure.\n")
            .Append("+\n")
            .Append(" ### Breaking API changes\n")
            .Append('\n')
            .Append(" * `clone_checkout_strategy` has been removed from\n")
            .ToString();

        var diff = GitDiff.FromBuffer(patchText);
        GitOid patchId = await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("06094b1948b878b7d9ff7560b4eae672a014b0ec", patchId.ToString());
    }

    [Fact]
    public async Task Compute_ReverseDiff_ProducesDifferentPatchId()
    {
        // Reverse: tree2 -> tree1 (removes "more\n"). git patch-id is different
        // because the @@ header line numbers differ and +more becomes -more.
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(_tree2, _tree1, cancellationToken: TestContext.Current.CancellationToken);

        GitOid patchId = await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedReversePatchId, patchId.ToString());
    }

    [Fact]
    public async Task Compute_NullDiff_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await GitPatchId.ComputeAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Compute_SameContent_ProducesEmptySha1()
    {
        // A diff of identical trees produces no deltas → no hunks → no content
        // hashed. The final flush_hunk finalizes the hash of empty input
        // (SHA-1 of "" = da39a3ee5e6b4b0d3255bfef95601890afd80709) and
        // carry-adds it into the zero result. So the patch-id of an empty
        // diff is the SHA-1 of empty input, NOT zero.
        // (git patch-id emits nothing for empty diffs, but git_diff_patchid
        // always calls flush_hunk once.)
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(_tree1, _tree1, cancellationToken: TestContext.Current.CancellationToken);

        GitOid patchId = await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", patchId.ToString());
    }

    [Fact]
    public async Task Compute_OptionsParameter_AcceptedAndEquivalent()
    {
        using GitDiff diff1 = await _repo.DiffTreeToTreeAsync(_tree1, _tree2, cancellationToken: TestContext.Current.CancellationToken);
        GitOid withNull = await GitPatchId.ComputeAsync(diff1, options: null, cancellationToken: TestContext.Current.CancellationToken);

        using GitDiff diff2 = await _repo.DiffTreeToTreeAsync(_tree1, _tree2, cancellationToken: TestContext.Current.CancellationToken);
        GitOid withOptions = await GitPatchId.ComputeAsync(diff2, new GitPatchIdOptions(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(withNull, withOptions);
        Assert.Equal(ExpectedForwardPatchId, withNull.ToString());
    }

    [Fact]
    public async Task Compute_IsDeterministic_SameDiffSameId()
    {
        using GitDiff diff1 = await _repo.DiffTreeToTreeAsync(_tree1, _tree2, cancellationToken: TestContext.Current.CancellationToken);
        GitOid id1 = await GitPatchId.ComputeAsync(diff1, cancellationToken: TestContext.Current.CancellationToken);

        using GitDiff diff2 = await _repo.DiffTreeToTreeAsync(_tree1, _tree2, cancellationToken: TestContext.Current.CancellationToken);
        GitOid id2 = await GitPatchId.ComputeAsync(diff2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(id1, id2);
    }

    // ── Helpers ──

    private async Task<GitTree> CommitFileContentAsync(string content, string message)
    {
        string relativePath = "a.txt";
        string fullPath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync();
        GitOid blobOid = await _repo.BlobCreateFromWorkdirAsync(relativePath);
        index.Add(new GitIndexEntry
        {
            Path = GitPath.FromUtf8String(relativePath),
            Mode = GitFileMode.Regular,
            Id = blobOid,
        });
        await index.WriteAsync();

        GitOid treeOid = await index.WriteTreeAsync();
        GitTree tree = await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("written tree not found");
        var sig = new GitSignature("T", "t@t", new GitTime(1577836800, 0));
        GitOid commitOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = message + "\n",
        });
        await _repo.ReferenceCreateAsync("refs/heads/master", commitOid, force: true);

        return tree;
    }
}

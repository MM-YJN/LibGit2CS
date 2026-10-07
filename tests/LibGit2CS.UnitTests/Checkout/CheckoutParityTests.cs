using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Regression tests for the merge and checkout parity behaviors (dirty-but-
/// unmodified workdir, tree-checkout baseline) in
/// libgit2 1.9.4. Expectations
/// C-verified against libgit2 1.9.4.
/// </summary>
public sealed class CheckoutParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task CommitFileAsync(string name, string content, CancellationToken ct)
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, name), content, cancellationToken: ct);
        await idx.AddByPathAsync(name, cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);

        GitReference? head = await _repo.ReferenceResolveAsync("HEAD", cancellationToken: ct);
        GitOid[] parents = [];
        if (head is GitDirectReference dr)
        {
            parents = [dr.Target];
        }

        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "commit " + name + "\n",
            UpdateRef = "refs/heads/master",
        }, ct);
    }

    private async Task<string> ReadWorkdirFileAsync(string name)
        => await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, name), TestContext.Current.CancellationToken);

    [Fact]
    public async Task SafeCheckout_DirtyUnmodifiedWorkdir_ProceedsAndPreserves()
    {
        // C (checkout.c:503-510): UNMODIFIED delta + modified workdir →
        // CHECKOUT_ACTION_IF(FORCE, UPDATE_BLOB, NONE). A SAFE checkout must
        // succeed and keep the local edits.
        await CommitFileAsync("file.txt", "one", TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "LOCAL EDIT", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CheckoutHeadAsync(cancellationToken: TestContext.Current.CancellationToken); // SAFE default — must NOT throw

        Assert.Equal("LOCAL EDIT", await ReadWorkdirFileAsync("file.txt"));
    }

    [Fact]
    public async Task SafeCheckout_DirtyUnmodifiedWorkdir_ForceOverwrites()
    {
        await CommitFileAsync("file.txt", "one", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "LOCAL EDIT", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Assert.Equal("one", await ReadWorkdirFileAsync("file.txt"));
    }

    [Fact]
    public async Task SafeCheckout_StagedChange_Preserved()
    {
        // the baseline for git_checkout_tree is the HEAD tree, not the
        // index (checkout.c:2476-2484). HEAD == target → empty diff → no
        // actions: a staged (index) change is neither applied nor lost.
        await CommitFileAsync("file.txt", "one", TestContext.Current.CancellationToken);

        // Stage v2 ("three") in the index; leave the workdir at "two".
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "two", cancellationToken: TestContext.Current.CancellationToken);
        GitOid staged = await _repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("three"), TestContext.Current.CancellationToken);
        await idx.AddFromBufferAsync(new GitIndexEntry("file.txt", GitOid.Empty, GitFileMode.Regular), Encoding.UTF8.GetBytes("three"), TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        await _repo.CheckoutHeadAsync(cancellationToken: TestContext.Current.CancellationToken); // SAFE default — must NOT throw

        Assert.Equal("two", await ReadWorkdirFileAsync("file.txt"));
        GitIndexEntry? entry = (await _repo.GetIndexAsync(TestContext.Current.CancellationToken)).EntryByPath("file.txt");
        Assert.NotNull(entry);
        Assert.Equal(staged, entry.Value.Id);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Shared base for checkout golden tests. Creates a fresh repo with 2
/// commits (matching the <c>generate-goldens.sh</c> checkout setup),
/// then restores the workdir to the first commit's state so the checkout
/// test can transition to the target tree.
/// </summary>
public abstract class CheckoutGoldenBase : IDisposable
{
    private readonly string _tempDir;
    private readonly GitContext _context = new();
    protected GitContext Context => _context;

    protected CheckoutGoldenBase()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_CheckoutGolden_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public string TempDir => _tempDir;

    public void Dispose()
    {
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Creates a fresh repo matching the golden script's
    /// <c>checkout_setup_repo</c>: 2 commits with a multi-file tree
    /// (first: a.txt, b.txt, c.txt, root.txt; second: a.txt modified,
    /// b.txt deleted, d.txt added). After setup, HEAD is at the second
    /// commit, and both the index and workdir reflect the second commit's
    /// state.
    /// </summary>
    protected async ValueTask<GitRepository> CreateRepo()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, Context);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync();

        // First commit: 4 files across 2 directories.
        string workdir = repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "dir1"));
        Directory.CreateDirectory(Path.Combine(workdir, "dir2"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir1", "a.txt"), "alpha content\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir1", "b.txt"), "beta content\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir2", "c.txt"), "gamma content\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "root.txt"), "root file\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("dir1/a.txt");
        await idx.AddByPathAsync("dir1/b.txt");
        await idx.AddByPathAsync("dir2/c.txt");
        await idx.AddByPathAsync("root.txt");
        await idx.WriteAsync();
        GitOid treeOid1 = await idx.WriteTreeAsync();
        var sig1 = new GitSignature("Test User", "test@example.com", new GitTime(946684800, 0));
        GitOid firstOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid1,
            Author = sig1,
            Committer = sig1,
            Message = "first commit\n",
            UpdateRef = "refs/heads/master",
        });

        // Second commit: modify a.txt, delete b.txt, add d.txt.
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir1", "a.txt"), "alpha modified\n", cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(workdir, "dir1", "b.txt"));
        idx.RemoveByPath("dir1/b.txt");
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir1", "d.txt"), "delta content\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("dir1/a.txt");
        await idx.AddByPathAsync("dir1/d.txt");
        await idx.WriteAsync();
        GitOid treeOid2 = await idx.WriteTreeAsync();
        var sig2 = new GitSignature("Test User", "test@example.com", new GitTime(946771200, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Author = sig2,
            Committer = sig2,
            Message = "second commit\n",
            UpdateRef = "refs/heads/master",
            Parents = [firstOid],
        });

        // HEAD is now at the second commit. Workdir + index reflect the
        // second commit's state. No restoration needed.
        return repo;
    }

    /// <summary>
    /// Loads the byte-exact golden output for the given checkout case.
    /// </summary>
    protected static string LoadExpected(string caseName)
        => FixtureLoader.LoadText($"Fixtures/checkout/expected/{caseName}.txt");
}

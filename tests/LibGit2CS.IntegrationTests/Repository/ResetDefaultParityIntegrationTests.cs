using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary> End-to-end tests for <c>git reset HEAD -- &lt;path&gt;</c> computes its diff with GIT_DIFF_REVERSE (reset.c:55-58), so a path staged into
/// the index is removed and a path removed from the index is restored. </summary>
public sealed class ResetDefaultParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ResetDefaultParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ResetDefaultInt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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

    [Fact]
    public async Task ResetDefault_StagedNewFile_IsUnstaged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitContext ctx = new();
        string path = Path.Combine(_tempDir, "r");
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, ct);
        string workdir = repo.Workdir!;

        // Commit one file.
        await File.WriteAllTextAsync(Path.Combine(workdir, "tracked.txt"), "x\n", ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("tracked.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, ct);

        // Stage a NEW file (in the index, absent from the tree).
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "new\n"u8.ToArray(), ct);
        idx.Add(new GitIndexEntry("staged.txt", blobOid, GitFileMode.Regular));
        await idx.WriteAsync(ct);
        Assert.NotNull(idx.EntryByPath("staged.txt"));

        // `git reset HEAD -- staged.txt` must unstage it.
        Commit target = (await repo.ObjectLookupAsync<Commit>(commit, ct))!;
        await repo.ResetDefaultAsync(target, ["staged.txt"], ct);

        Assert.Null(idx.EntryByPath("staged.txt"));
        // The tracked file is untouched.
        Assert.NotNull(idx.EntryByPath("tracked.txt"));
    }

    [Fact]
    public async Task ResetDefault_RmCachedFile_IsRestoredFromTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitContext ctx = new();
        string path = Path.Combine(_tempDir, "reset-default");
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, ct);
        string workdir = repo.Workdir!;

        await File.WriteAllTextAsync(Path.Combine(workdir, "one.txt"), "one\n", ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("one.txt", ct);
        await idx.WriteAsync(ct);
        GitOid tree1 = await idx.WriteTreeAsync(ct);
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree1,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, ct);

        // Second commit adds two.txt (the target tree has it).
        await File.WriteAllTextAsync(Path.Combine(workdir, "two.txt"), "two\n", ct);
        await idx.AddByPathAsync("two.txt", ct);
        await idx.WriteAsync(ct);
        GitOid tree2 = await idx.WriteTreeAsync(ct);
        head = await repo.ReferenceResolveAsync("HEAD", ct);
        parents = head is GitDirectReference dr2 ? [dr2.Target] : [];
        GitOid commit2 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree2,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c2\n",
            UpdateRef = "refs/heads/master",
        }, ct);

        // Remove two.txt from the index only.
        idx.Remove("two.txt", 0);
        await idx.WriteAsync(ct);
        Assert.Null(idx.EntryByPath("two.txt"));

        // `git reset HEAD -- two.txt` restores it from the tree.
        Commit target = (await repo.ObjectLookupAsync<Commit>(commit2, ct))!;
        await repo.ResetDefaultAsync(target, ["two.txt"], ct);

        GitIndexEntry? restored = idx.EntryByPath("two.txt");
        Assert.NotNull(restored);
        Assert.Equal(GitFileMode.Regular, restored!.Value.Mode);
        Assert.False(restored.Value.Id.IsZero);
    }
}

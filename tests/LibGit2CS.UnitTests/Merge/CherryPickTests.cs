using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Cherry-pick tests. Mirrors libgit2's <c>tests/libgit2/cherrypick/</c>
/// test suite (<c>bare.c</c> + <c>workdir.c</c>).
/// </summary>
public sealed class CherryPickTests : IDisposable
{
    private const uint Mode0644 = 33188; // octal 0100644

    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public CherryPickTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CherryPick_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> OpenCherryPickRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/cherrypick.zip");
        _extractedPaths.Add(path);
        GitRepository repo = await GitRepository.OpenAsync(Path.Combine(path, "cherrypick"), new GitContext());
        await repo.Config.SetStringAsync("merge.conflictstyle", "merge");
        await repo.Config.SetBoolAsync("core.autocrlf", false);
        return repo;
    }

    private static GitOid Oid(string hex) => GitOid.Parse(hex.AsSpan(), GitHashAlgorithmKind.Sha1);

    private static async Task<Commit> LookupCommitAsync(GitRepository repo, string hex)
    {
        Commit? commit = await repo.ObjectLookupAsync<Commit>(Oid(hex), TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        return commit;
    }

    private static async Task ResetHardAsync(GitRepository repo, Commit target)
    {
        await repo.ResetAsync(target, GitResetMode.Hard);
        // Force-checkout to ensure workdir files are updated (the stat-cache
        // may incorrectly report files as up-to-date after fixture extraction).
        await repo.CheckoutHeadAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        });
    }

    // ── bare.c ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Bare_Automerge()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();
        Commit head = await LookupCommitAsync(repo, "d3d77487660ee3c0194ee01dc5eaf478782b1c7e");
        Commit commit = await LookupCommitAsync(repo, "cfc4f0999a8367568e049af4f72e452d40828a15");

        using LibGit2CS.Index.GitIndex index = await repo.CherryPickCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "38c05a857e831a7e759d83778bfc85d003e21c45", 0, "file1.txt"),
            new(Mode0644, "a661b5dec1004e2c62654ded3762370c27cf266b", 0, "file2.txt"),
            new(Mode0644, "df6b290e0bd6a89b01d69f66687e8abf385283ca", 0, "file3.txt"),
        ]);
    }

    [Fact]
    public async Task Bare_Conflicts()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();
        Commit head = await LookupCommitAsync(repo, "bafbf6912c09505ac60575cd43d3f2aba3bd84d8");
        Commit commit = await LookupCommitAsync(repo, "e9b63f3655b2ad80c0ff587389b5a9589a3a7110");

        using LibGit2CS.Index.GitIndex index = await repo.CherryPickCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "242e7977ba73637822ffb265b46004b9b0e5153b", 0, "file1.txt"),
            new(Mode0644, "a58ca3fee5eb68b11adc2703e5843f968c9dad1e", 1, "file2.txt"),
            new(Mode0644, "bd6ffc8c6c41f0f85ff9e3d61c9479516bac0024", 2, "file2.txt"),
            new(Mode0644, "563f6473a3858f99b80e5f93c660512ed38e1e6f", 3, "file2.txt"),
            new(Mode0644, "28d9eb4208074ad1cc84e71ccc908b34573f05d2", 1, "file3.txt"),
            new(Mode0644, "1124c2c1ae07b26fded662d6c3f3631d9dc16f88", 2, "file3.txt"),
            new(Mode0644, "e233b9ed408a95e9d4b65fec7fc34943a556deb2", 3, "file3.txt"),
        ]);
    }

    [Fact]
    public async Task Bare_Orphan()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();
        Commit head = await LookupCommitAsync(repo, "d3d77487660ee3c0194ee01dc5eaf478782b1c7e");
        Commit commit = await LookupCommitAsync(repo, "74f06b5bfec6d33d7264f73606b57a7c0b963819");

        using LibGit2CS.Index.GitIndex index = await repo.CherryPickCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "38c05a857e831a7e759d83778bfc85d003e21c45", 0, "file1.txt"),
            new(Mode0644, "a661b5dec1004e2c62654ded3762370c27cf266b", 0, "file2.txt"),
            new(Mode0644, "85a4a1d791973644f24c72f5e89420d3064cc452", 0, "file3.txt"),
            new(Mode0644, "9ccb9bf50c011fd58dcbaa65df917bf79539717f", 0, "orphan.txt"),
        ]);
    }

    // ── workdir.c ───────────────────────────────────────────────────────

    [Fact]
    public async Task Workdir_Automerge()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        string[] cherrypickOids = new[]
        {
            "cfc4f0999a8367568e049af4f72e452d40828a15",
            "964ea3da044d9083181a88ba6701de9e35778bf4",
            "a43a050c588d4e92f11a6b139680923e9728477d",
        };

        MergeTestHelpers.MergeIndexEntry[][] expectedIndexes = [
            [
                new(Mode0644, "38c05a857e831a7e759d83778bfc85d003e21c45", 0, "file1.txt"),
                new(Mode0644, "a661b5dec1004e2c62654ded3762370c27cf266b", 0, "file2.txt"),
                new(Mode0644, "df6b290e0bd6a89b01d69f66687e8abf385283ca", 0, "file3.txt"),
            ],
            [
                new(Mode0644, "38c05a857e831a7e759d83778bfc85d003e21c45", 0, "file1.txt"),
                new(Mode0644, "bd8fc3c59fb52d3c8b5907ace7defa5803f82419", 0, "file2.txt"),
                new(Mode0644, "df6b290e0bd6a89b01d69f66687e8abf385283ca", 0, "file3.txt"),
            ],
            [
                new(Mode0644, "f06427bee380364bc7e0cb26a9245158e4726ce0", 0, "file1.txt"),
                new(Mode0644, "bd8fc3c59fb52d3c8b5907ace7defa5803f82419", 0, "file2.txt"),
                new(Mode0644, "df6b290e0bd6a89b01d69f66687e8abf385283ca", 0, "file3.txt"),
            ],
        ];

        GitOid headOid = Oid("d3d77487660ee3c0194ee01dc5eaf478782b1c7e");
        var signature = new GitSignature("Picker", "picker@example.org", new GitTime(1234567890, 0));

        for (int i = 0; i < 3; i++)
        {
            Commit? head = await repo.ObjectLookupAsync<Commit>(headOid, TestContext.Current.CancellationToken);
            Assert.NotNull(head);
            await ResetHardAsync(repo, head);

            Commit commit = await LookupCommitAsync(repo, cherrypickOids[i]);
            await repo.CherryPickAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(repo.Path, "CHERRY_PICK_HEAD")));
            Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));

            // Write the index as a tree and create a commit so the next
            // cherry-pick has a new HEAD.
            GitOid treeOid = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
            GitOid cherrypickedOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Author = signature,
                Committer = signature,
                Message = "Cherry picked!",
                Parents = [head.Id],
                UpdateRef = "HEAD",
            }, cancellationToken: TestContext.Current.CancellationToken);

            MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), expectedIndexes[i]);

            headOid = cherrypickedOid;
        }
    }

    [Fact]
    public async Task Workdir_EmptyResult()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "cfc4f0999a8367568e049af4f72e452d40828a15");
        Commit commit = await LookupCommitAsync(repo, "a43a050c588d4e92f11a6b139680923e9728477d");

        // Create an untracked file that should not conflict.
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file4.txt"), "", cancellationToken: TestContext.Current.CancellationToken);

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        // The resulting tree should not have changed; the change was already on HEAD.
        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "19c5c7207054604b69c84d08a7571ef9672bb5c2", 0, "file1.txt"),
            new(Mode0644, "a58ca3fee5eb68b11adc2703e5843f968c9dad1e", 0, "file2.txt"),
            new(Mode0644, "28d9eb4208074ad1cc84e71ccc908b34573f05d2", 0, "file3.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_Conflicts()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "bafbf6912c09505ac60575cd43d3f2aba3bd84d8");
        Commit commit = await LookupCommitAsync(repo, "e9b63f3655b2ad80c0ff587389b5a9589a3a7110");

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(repo.Path, "CHERRY_PICK_HEAD")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "242e7977ba73637822ffb265b46004b9b0e5153b", 0, "file1.txt"),
            new(Mode0644, "a58ca3fee5eb68b11adc2703e5843f968c9dad1e", 1, "file2.txt"),
            new(Mode0644, "bd6ffc8c6c41f0f85ff9e3d61c9479516bac0024", 2, "file2.txt"),
            new(Mode0644, "563f6473a3858f99b80e5f93c660512ed38e1e6f", 3, "file2.txt"),
            new(Mode0644, "28d9eb4208074ad1cc84e71ccc908b34573f05d2", 1, "file3.txt"),
            new(Mode0644, "1124c2c1ae07b26fded662d6c3f3631d9dc16f88", 2, "file3.txt"),
            new(Mode0644, "e233b9ed408a95e9d4b65fec7fc34943a556deb2", 3, "file3.txt"),
        ]);

        // Verify MERGE_MSG content.
        string mergeMsg = await File.ReadAllTextAsync(Path.Combine(repo.Path, "MERGE_MSG"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "Change all files\n" +
            "\n" +
            "#Conflicts:\n" +
            "#\tfile2.txt\n" +
            "#\tfile3.txt\n",
            mergeMsg);

        // Verify conflicted file2.txt content.
        string file2 = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "file2.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "!File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2!!\n" +
            "File 2\n" +
            "File 2\n" +
            "File 2\n" +
            "<<<<<<< HEAD\n" +
            "File 2\n" +
            "=======\n" +
            "File 2!\n" +
            "File 2\n" +
            "File 2!\n" +
            ">>>>>>> e9b63f3... Change all files\n",
            file2);
    }

    [Fact]
    public async Task Workdir_ConflictUseOurs()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "bafbf6912c09505ac60575cd43d3f2aba3bd84d8");
        Commit commit = await LookupCommitAsync(repo, "e9b63f3655b2ad80c0ff587389b5a9589a3a7110");

        // Leave the index in a conflicted state, but checkout "ours" to the workdir.
        var opts = new GitCherryPickOptions
        {
            CheckoutOptions = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.UseOurs,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "242e7977ba73637822ffb265b46004b9b0e5153b", 0, "file1.txt"),
            new(Mode0644, "a58ca3fee5eb68b11adc2703e5843f968c9dad1e", 1, "file2.txt"),
            new(Mode0644, "bd6ffc8c6c41f0f85ff9e3d61c9479516bac0024", 2, "file2.txt"),
            new(Mode0644, "563f6473a3858f99b80e5f93c660512ed38e1e6f", 3, "file2.txt"),
            new(Mode0644, "28d9eb4208074ad1cc84e71ccc908b34573f05d2", 1, "file3.txt"),
            new(Mode0644, "1124c2c1ae07b26fded662d6c3f3631d9dc16f88", 2, "file3.txt"),
            new(Mode0644, "e233b9ed408a95e9d4b65fec7fc34943a556deb2", 3, "file3.txt"),
        ]);

        await MergeTestHelpers.AssertWorkdirAsync(repo, [
            new(Mode0644, "242e7977ba73637822ffb265b46004b9b0e5153b", 0, "file1.txt"),
            new(Mode0644, "bd6ffc8c6c41f0f85ff9e3d61c9479516bac0024", 0, "file2.txt"),
            new(Mode0644, "1124c2c1ae07b26fded662d6c3f3631d9dc16f88", 0, "file3.txt"),
        ]);

        // Now resolve conflicts in the index by taking "ours" via file_favor.
        opts = new GitCherryPickOptions
        {
            MergeOptions = new GitMergeOptions { Favor = GitMergeFileFavor.Ours },
            CheckoutOptions = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.UseOurs,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "242e7977ba73637822ffb265b46004b9b0e5153b", 0, "file1.txt"),
            new(Mode0644, "bd6ffc8c6c41f0f85ff9e3d61c9479516bac0024", 0, "file2.txt"),
            new(Mode0644, "1124c2c1ae07b26fded662d6c3f3631d9dc16f88", 0, "file3.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_Rename()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "cfc4f0999a8367568e049af4f72e452d40828a15");
        Commit commit = await LookupCommitAsync(repo, "2a26c7e88b285613b302ba76712bc998863f3cbc");

        var opts = new GitCherryPickOptions
        {
            MergeOptions = new GitMergeOptions
            {
                Flags = GitMergeFlags.FindRenames,
                RenameThreshold = 50,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "19c5c7207054604b69c84d08a7571ef9672bb5c2", 0, "file1.txt"),
            new(Mode0644, "a58ca3fee5eb68b11adc2703e5843f968c9dad1e", 0, "file2.txt"),
            new(Mode0644, "28d9eb4208074ad1cc84e71ccc908b34573f05d2", 0, "file3.txt.renamed"),
        ]);
    }

    [Fact]
    public async Task Workdir_BothRenamed()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "44cd2ed2052c9c68f9a439d208e9614dc2a55c70");
        Commit commit = await LookupCommitAsync(repo, "2a26c7e88b285613b302ba76712bc998863f3cbc");

        var opts = new GitCherryPickOptions
        {
            MergeOptions = new GitMergeOptions
            {
                Flags = GitMergeFlags.FindRenames,
                RenameThreshold = 50,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "19c5c7207054604b69c84d08a7571ef9672bb5c2", 0, "file1.txt"),
            new(Mode0644, "a58ca3fee5eb68b11adc2703e5843f968c9dad1e", 0, "file2.txt"),
            new(Mode0644, "e233b9ed408a95e9d4b65fec7fc34943a556deb2", 1, "file3.txt"),
            new(Mode0644, "e233b9ed408a95e9d4b65fec7fc34943a556deb2", 3, "file3.txt.renamed"),
            new(Mode0644, "28d9eb4208074ad1cc84e71ccc908b34573f05d2", 2, "file3.txt.renamed_on_branch"),
        ]);
    }

    [Fact]
    public async Task Workdir_NonmergeFailsMainlineSpecified()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        // HEAD is a non-merge commit.
        var headRef = (await repo.ReferenceResolveAsync(GitReferences.HeadFile, cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(headRef);
        Commit? head = await repo.ObjectLookupAsync<Commit>(headRef.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(head);

        var opts = new GitCherryPickOptions { Mainline = 1 };

        await Assert.ThrowsAsync<GitException>(async () => await repo.CherryPickAsync(head, opts, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(repo.Path, "CHERRY_PICK_HEAD")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));
    }

    [Fact]
    public async Task Workdir_MergeFailsWithoutMainlineSpecified()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "cfc4f0999a8367568e049af4f72e452d40828a15");
        Commit commit = await LookupCommitAsync(repo, "abe4603bc7cd5b8167a267e0e2418fd2348f8cff");

        await ResetHardAsync(repo, head);

        await Assert.ThrowsAsync<GitException>(async () => await repo.CherryPickAsync(commit, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(repo.Path, "CHERRY_PICK_HEAD")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));
    }

    [Fact]
    public async Task Workdir_MergeFirstParent()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "cfc4f0999a8367568e049af4f72e452d40828a15");
        Commit commit = await LookupCommitAsync(repo, "abe4603bc7cd5b8167a267e0e2418fd2348f8cff");

        var opts = new GitCherryPickOptions { Mainline = 1 };

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "f90f9dcbdac2cce5cc166346160e19cb693ef4e8", 0, "file1.txt"),
            new(Mode0644, "563f6473a3858f99b80e5f93c660512ed38e1e6f", 0, "file2.txt"),
            new(Mode0644, "e233b9ed408a95e9d4b65fec7fc34943a556deb2", 0, "file3.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_MergeSecondParent()
    {
        await using GitRepository repo = await OpenCherryPickRepoAsync();

        Commit head = await LookupCommitAsync(repo, "cfc4f0999a8367568e049af4f72e452d40828a15");
        Commit commit = await LookupCommitAsync(repo, "abe4603bc7cd5b8167a267e0e2418fd2348f8cff");

        var opts = new GitCherryPickOptions { Mainline = 2 };

        await ResetHardAsync(repo, head);
        await repo.CherryPickAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "487434cace79238a7091e2220611d4f20a765690", 0, "file1.txt"),
            new(Mode0644, "e5183bfd18e3a0a691fadde2f0d5610b73282d31", 0, "file2.txt"),
            new(Mode0644, "409a1bec58bf35348e8b62b72bb9c1f45cf5a587", 0, "file3.txt"),
        ]);
    }
}

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
/// Revert tests. Mirrors libgit2's <c>tests/libgit2/revert/</c>
/// test suite (<c>bare.c</c> + <c>rename.c</c> + <c>workdir.c</c>).
/// </summary>
public sealed class RevertTests : IDisposable
{
    private const uint Mode0644 = 33188; // octal 0100644

    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RevertTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_Revert_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> OpenRevertRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/revert.zip");
        _extractedPaths.Add(path);
        GitRepository repo = await GitRepository.OpenAsync(Path.Combine(path, "revert"), new GitContext());
        await repo.Config.SetStringAsync("merge.conflictstyle", "merge");
        await repo.Config.SetBoolAsync("core.autocrlf", false);
        return repo;
    }

    private async Task<GitRepository> OpenRevertRenameRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/revert-rename.zip");
        _extractedPaths.Add(path);
        // revert-rename.git is a bare repo — the zip contains the repo contents directly.
        return await GitRepository.OpenBareAsync(Path.Combine(path, "revert-rename.git"), new GitContext());
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

    private static async Task<Commit> HeadCommitAsync(GitRepository repo)
    {
        var headRef = (await repo.ReferenceResolveAsync(GitReferences.HeadFile)) as GitDirectReference;
        Assert.NotNull(headRef);
        Commit? commit = await repo.ObjectLookupAsync<Commit>(headRef.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        return commit;
    }

    // ── bare.c ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Bare_Automerge()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();
        Commit head = await LookupCommitAsync(repo, "72333f47d4e83616630ff3b0ffe4c0faebcc3c45");
        Commit commit = await LookupCommitAsync(repo, "d1d403d22cbe24592d725f442835cf46fe60c8ac");

        using LibGit2CS.Index.GitIndex index = await repo.RevertCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "caf99de3a49827117bb66721010eac461b06a80c", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Bare_Conflicts()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();
        Commit head = await HeadCommitAsync(repo);
        Commit commit = await LookupCommitAsync(repo, "72333f47d4e83616630ff3b0ffe4c0faebcc3c45");

        using LibGit2CS.Index.GitIndex index = await repo.RevertCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(index.HasConflicts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "7731926a337c4eaba1e2187d90ebfa0a93659382", 1, "file1.txt"),
            new(Mode0644, "4b8fcff56437e60f58e9a6bc630dd242ebf6ea2c", 2, "file1.txt"),
            new(Mode0644, "3a3ef367eaf3fe79effbfb0a56b269c04c2b59fe", 3, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Bare_Orphan()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();
        Commit head = await LookupCommitAsync(repo, "39467716290f6df775a91cdb9a4eb39295018145");
        Commit commit = await LookupCommitAsync(repo, "ebb03002cee5d66c7732dd06241119fe72ab96a5");

        using LibGit2CS.Index.GitIndex index = await repo.RevertCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "296a6d3be1dff05c5d1f631d2459389fa7b619eb", 0, "file-mainline.txt"),
        ]);
    }

    // ── rename.c ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_Automerge()
    {
        await using GitRepository repo = await OpenRevertRenameRepoAsync();
        Commit head = await HeadCommitAsync(repo);
        Commit commit = await LookupCommitAsync(repo, "7b4d7c3789b3581973c04087cb774c3c3576de2f");

        using LibGit2CS.Index.GitIndex index = await repo.RevertCommitAsync(commit, head, 0, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "f0f64c618e1646d2948a456ed7c4bcfad5536d68", 0, "goodmode"),
        ]);
    }

    // ── workdir.c ──────────────────────────────────────────────────────

    [Fact]
    public async Task Workdir_Automerge()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "72333f47d4e83616630ff3b0ffe4c0faebcc3c45");
        Commit commit = await LookupCommitAsync(repo, "d1d403d22cbe24592d725f442835cf46fe60c8ac");

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "caf99de3a49827117bb66721010eac461b06a80c", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_Conflicts()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await HeadCommitAsync(repo);
        Commit commit = await LookupCommitAsync(repo, "72333f47d4e83616630ff3b0ffe4c0faebcc3c45");

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "7731926a337c4eaba1e2187d90ebfa0a93659382", 1, "file1.txt"),
            new(Mode0644, "4b8fcff56437e60f58e9a6bc630dd242ebf6ea2c", 2, "file1.txt"),
            new(Mode0644, "3a3ef367eaf3fe79effbfb0a56b269c04c2b59fe", 3, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);

        // Verify conflicted file1.txt content.
        string file1 = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "file1.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "!File one!\n" +
            "!File one!\n" +
            "File one!\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            "<<<<<<< HEAD\n" +
            "File one!\n" +
            "!File one!\n" +
            "!File one!\n" +
            "!File one!\n" +
            "=======\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            "File one\n" +
            ">>>>>>> parent of 72333f4... automergeable changes\n",
            file1);

        // Verify MERGE_MSG content.
        Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));
        string mergeMsg = await File.ReadAllTextAsync(Path.Combine(repo.Path, "MERGE_MSG"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "Revert \"automergeable changes\"\n" +
            "\n" +
            "This reverts commit 72333f47d4e83616630ff3b0ffe4c0faebcc3c45.\n" +
            "\n" +
            "#Conflicts:\n" +
            "#\tfile1.txt\n",
            mergeMsg);
    }

    [Fact]
    public async Task Workdir_Orphan()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "39467716290f6df775a91cdb9a4eb39295018145");
        Commit commit = await LookupCommitAsync(repo, "ebb03002cee5d66c7732dd06241119fe72ab96a5");

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "296a6d3be1dff05c5d1f631d2459389fa7b619eb", 0, "file-mainline.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_Again()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit origHead = await HeadCommitAsync(repo);
        await ResetHardAsync(repo, origHead);
        await repo.RevertAsync(origHead, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "7731926a337c4eaba1e2187d90ebfa0a93659382", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);

        // Commit the first revert, then revert again.
        GitOid treeOid = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var signature = new GitSignature("Reverter", "reverter@example.org", new GitTime(1234567890, 0));
        GitOid revertedOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = signature,
            Committer = signature,
            Message = "Reverted!",
            Parents = [origHead.Id],
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        await repo.RevertAsync(origHead, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "7731926a337c4eaba1e2187d90ebfa0a93659382", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_AgainAfterAutomerge()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "72333f47d4e83616630ff3b0ffe4c0faebcc3c45");
        Commit commit = await LookupCommitAsync(repo, "d1d403d22cbe24592d725f442835cf46fe60c8ac");

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "caf99de3a49827117bb66721010eac461b06a80c", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);

        // Commit the first revert, then revert the same commit again.
        GitOid treeOid = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var signature = new GitSignature("Reverter", "reverter@example.org", new GitTime(1234567890, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = signature,
            Committer = signature,
            Message = "Reverted!",
            Parents = [head.Id],
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        // Second revert produces conflicts.
        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "3a3ef367eaf3fe79effbfb0a56b269c04c2b59fe", 1, "file1.txt"),
            new(Mode0644, "caf99de3a49827117bb66721010eac461b06a80c", 2, "file1.txt"),
            new(Mode0644, "747726e021bc5f44b86de60e3032fd6f9f1b8383", 3, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_AgainAfterEdit()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit origHead = await LookupCommitAsync(repo, "399fb3aba3d9d13f7d40a9254ce4402067ef3149");
        Commit commit = await LookupCommitAsync(repo, "2d440f2b3147d3dc7ad1085813478d6d869d5a4d");

        await ResetHardAsync(repo, origHead);
        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "3721552e06c4bdc7d478e0674e6304888545d5fd", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);

        // Commit the first revert, then revert again.
        GitOid treeOid = await (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var signature = new GitSignature("Reverter", "reverter@example.org", new GitTime(1234567890, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = signature,
            Committer = signature,
            Message = "Reverted!",
            Parents = [origHead.Id],
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "3721552e06c4bdc7d478e0674e6304888545d5fd", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_AgainAfterEditTwo()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();
        await repo.Config.SetBoolAsync("core.autocrlf", false, cancellationToken: TestContext.Current.CancellationToken);

        Commit headCommit = await LookupCommitAsync(repo, "75ec9929465623f17ff3ad68c0438ea56faba815");
        Commit revertCommit = await LookupCommitAsync(repo, "97e52d5e81f541080cd6b92829fb85bc4d81d90b");

        await ResetHardAsync(repo, headCommit);
        await repo.RevertAsync(revertCommit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "a8c86221b400b836010567cc3593db6e96c1a83a", 1, "file.txt"),
            new(Mode0644, "46ff0854663aeb2182b9838c8da68e33ac23bc1e", 2, "file.txt"),
            new(Mode0644, "21a96a98ed84d45866e1de6e266fd3a61a4ae9dc", 3, "file.txt"),
        ]);

        string fileContent = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "a\n" +
            "<<<<<<< HEAD\n" +
            "=======\n" +
            "a\n" +
            ">>>>>>> parent of 97e52d5... Revert me\n" +
            "a\n" +
            "a\n" +
            "a\n" +
            "a\n" +
            "ab",
            fileContent);
    }

    [Fact]
    public async Task Workdir_ConflictUseOurs()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "72333f47d4e83616630ff3b0ffe4c0faebcc3c45");
        Commit commit = await LookupCommitAsync(repo, "d1d403d22cbe24592d725f442835cf46fe60c8ac");

        var opts = new GitRevertOptions
        {
            CheckoutOptions = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.UseOurs,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "caf99de3a49827117bb66721010eac461b06a80c", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);

        await MergeTestHelpers.AssertWorkdirAsync(repo, [
            new(Mode0644, "caf99de3a49827117bb66721010eac461b06a80c", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_RenameOneOfTwo()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "cef56612d71a6af8d8015691e4865f7fece905b5");
        Commit commit = await LookupCommitAsync(repo, "55568c8de5322ff9a95d72747a239cdb64a19965");

        var opts = new GitRevertOptions
        {
            MergeOptions = new GitMergeOptions
            {
                Flags = GitMergeFlags.FindRenames,
                RenameThreshold = 50,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "747726e021bc5f44b86de60e3032fd6f9f1b8383", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "55acf326a69f0aab7a974ec53ffa55a50bcac14e", 3, "file4.txt"),
            new(Mode0644, "55acf326a69f0aab7a974ec53ffa55a50bcac14e", 1, "file5.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 2, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_Rename()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "55568c8de5322ff9a95d72747a239cdb64a19965");
        Commit commit = await LookupCommitAsync(repo, "0aa8c7e40d342fff78d60b29a4ba8e993ed79c51");

        var opts = new GitRevertOptions
        {
            MergeOptions = new GitMergeOptions
            {
                Flags = GitMergeFlags.FindRenames,
                RenameThreshold = 50,
            },
        };

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "55acf326a69f0aab7a974ec53ffa55a50bcac14e", 1, "file4.txt"),
            new(Mode0644, "55acf326a69f0aab7a974ec53ffa55a50bcac14e", 2, "file5.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_Head()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit commit = await HeadCommitAsync(repo);
        await ResetHardAsync(repo, commit);
        await repo.RevertAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "7731926a337c4eaba1e2187d90ebfa0a93659382", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);

        await MergeTestHelpers.AssertWorkdirAsync(repo, [
            new(Mode0644, "7731926a337c4eaba1e2187d90ebfa0a93659382", 0, "file1.txt"),
            new(Mode0644, "0ab09ea6d4c3634bdf6c221626d8b6f7dd890767", 0, "file2.txt"),
            new(Mode0644, "f4e107c230d08a60fb419d19869f1f282b272d9c", 0, "file3.txt"),
            new(Mode0644, "0f5bfcf58c558d865da6be0281d7795993646cee", 0, "file6.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_NonmergeFailsMainlineSpecified()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit commit = await HeadCommitAsync(repo);

        var opts = new GitRevertOptions { Mainline = 1 };

        await Assert.ThrowsAsync<GitException>(async () => await repo.RevertAsync(commit, opts, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "REVERT_HEAD")));
    }

    [Fact]
    public async Task Workdir_MergeFailsWithoutMainlineSpecified()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "5acdc74af27172ec491d213ee36cea7eb9ef2579");

        await ResetHardAsync(repo, head);

        await Assert.ThrowsAsync<GitException>(async () => await repo.RevertAsync(head, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "REVERT_HEAD")));
    }

    [Fact]
    public async Task Workdir_MergeFirstParent()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "5acdc74af27172ec491d213ee36cea7eb9ef2579");

        var opts = new GitRevertOptions { Mainline = 1 };

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(head, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "296a6d3be1dff05c5d1f631d2459389fa7b619eb", 0, "file-mainline.txt"),
            new(Mode0644, "0cdb66192ee192f70f891f05a47636057420e871", 0, "file1.txt"),
            new(Mode0644, "73ec36fa120f8066963a0bc9105bb273dbd903d7", 0, "file2.txt"),
        ]);
    }

    [Fact]
    public async Task Workdir_MergeSecondParent()
    {
        await using GitRepository repo = await OpenRevertRepoAsync();

        Commit head = await LookupCommitAsync(repo, "5acdc74af27172ec491d213ee36cea7eb9ef2579");

        var opts = new GitRevertOptions { Mainline = 2 };

        await ResetHardAsync(repo, head);
        await repo.RevertAsync(head, opts, cancellationToken: TestContext.Current.CancellationToken);

        MergeTestHelpers.AssertIndex((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), [
            new(Mode0644, "33c6fd981c49a2abf2971482089350bfc5cda8ea", 0, "file-branch.txt"),
            new(Mode0644, "0cdb66192ee192f70f891f05a47636057420e871", 0, "file1.txt"),
            new(Mode0644, "73ec36fa120f8066963a0bc9105bb273dbd903d7", 0, "file2.txt"),
        ]);
    }
}

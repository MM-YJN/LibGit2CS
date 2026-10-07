using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

// Parity cases verified against libgit2 1.9.4:
//  - MaybeAppendHeadReflogAsync must resolve the whole symref chain
//    of HEAD (refdb.c:348-390).
//  - the reflog serializer must sanitize newlines in both the message and
//    the signature portion (refdb_fs.c:2197-2202).
//  - ReadIndex must zero the source entry's stat fields when OID/mode
//    differ (index.c:3471, index_entry_cpy_nocache).
//  - Add/ConflictAdd must reject index entries C rejects (invalid
//    modes, tree stages) (index.c:1700-1703, 1840-1845).
//  - WritePackedRefsAsync must not recompute peels for refs C marks
//    PACKREF_CANNOT_PEEL (refdb_fs.c:197-202, 1271, 1329-1331).
//  - loose-ref enumeration must use byte-wise strcmp ordering, not
//    UTF-16 ordinal (iterator.c:1085).
//  - LockAsync must remove the whole empty sub-tree, not
//    only the top-level colliding directory (refdb_fs.c:1155-1160).
public sealed class RefsLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RefsLowRegressionTests()
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
        catch (IOException)
        {
        }
    }

    private async Task<(GitRepository Repo, string RepoPath)> CreateRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static GitSignature TestSig() => new("T", "t@x.com", new GitTime(100, 0));

    private static async Task<GitOid> CreateCommitAsync(GitRepository repo, string message = "m\n")
    {
        GitOid blobOid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("f.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitOid? parent = await repo.ReferenceResolveAsync("refs/heads/main", TestContext.Current.CancellationToken) is GitDirectReference d
            ? d.Target
            : null;
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is { } p ? [p] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
    }

    // ---- HEAD reflog decision resolves the whole symref chain ----

    [Fact]
    public async Task HeadReflog_ChainHeadToStable_HeadReflogOnStableUpdate()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("i07");
        GitOid oid = await CreateCommitAsync(repo);
        await repo.ReferenceCreateAsync("refs/heads/stable", oid, cancellationToken: TestContext.Current.CancellationToken);
        await repo.ReferenceCreateSymbolicAsync("refs/heads/master", "refs/heads/stable", cancellationToken: TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        // Update stable — C's git_refdb_should_write_head_reflog resolves the
        // WHOLE chain (HEAD → master → stable) and writes the HEAD reflog
        // (refdb.c:360-369), not just the first level (master ≠ stable).
        GitOid oid2 = await CreateCommitAsync(repo, "m2\n");
        await repo.ReferenceCreateAsync("refs/heads/stable", oid2, force: true, cancellationToken: TestContext.Current.CancellationToken);

        string headLog = Path.Combine(repoPath, ".git", "logs", "HEAD");
        Assert.True(File.Exists(headLog), "a chained HEAD must get a reflog entry when the leaf ref is updated");
    }

    [Fact]
    public async Task HeadReflog_DirectHead_HeadReflogOnBranchUpdate()
    {
        // Control: HEAD → master (direct) DOES get a HEAD reflog on master
        // updates.
        (GitRepository repo, string repoPath) = await CreateRepoAsync("i07b");
        await CreateCommitAsync(repo);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        GitOid oid2 = await CreateCommitAsync(repo, "m2\n");
        await repo.ReferenceCreateAsync("refs/heads/main", oid2, force: true, cancellationToken: TestContext.Current.CancellationToken);

        string headLog = Path.Combine(repoPath, ".git", "logs", "HEAD");
        Assert.True(File.Exists(headLog));
    }

    // ---- reflog serializer sanitizes newlines in the signature ----

    [Fact]
    public async Task Reflog_NewlineInUserName_SingleLineEntry()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("i08");
        await repo.Config.SetStringAsync("user.name", "A\nB", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("user.email", "a@b.c", TestContext.Current.CancellationToken);

        GitOid oid = await CreateCommitAsync(repo);
        await repo.ReferenceCreateAsync("refs/heads/x", oid, cancellationToken: TestContext.Current.CancellationToken);

        // C's serialize_reflog_entry scans the ENTIRE buffer for newlines
        // (refdb_fs.c:2197-2202) — a user.name containing a newline (legal
        // in config values) must not split the reflog line.
        string logPath = Path.Combine(repoPath, ".git", "logs", "refs", "heads", "x");
        string content = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
        Assert.Equal(1, content.Count(c => c == '\n'));
        Assert.DoesNotContain("A\nB", content);
        Assert.Contains("A B", content);
    }

    // ---- ReadIndex zeroes stat fields for differing entries ----

    [Fact]
    public void ReadIndex_DifferingEntry_StatFieldsZeroed()
    {
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        var source = GitIndex.New(GitHashAlgorithmKind.Sha1);

        var oidA = GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1);
        var oidB = GitOid.Parse("2222222222222222222222222222222222222222", GitHashAlgorithmKind.Sha1);

        index.Add(new GitIndexEntry("f.txt", oidA, GitFileMode.Regular)
        {
            Ctime = new IndexTime(100, 0),
            Mtime = new IndexTime(200, 0),
            Dev = 1,
            Ino = 2,
            Uid = 3,
            Gid = 4,
            FileSize = 5,
        });

        // The source entry differs (different OID) — C's
        // index_entry_dup_nocache copies only id/mode/flags and zeroes the
        // stat fields (index.c:3471, 1104-1112).
        source.Add(new GitIndexEntry("f.txt", oidB, GitFileMode.Regular)
        {
            Ctime = new IndexTime(300, 0),
            Mtime = new IndexTime(400, 0),
            Dev = 6,
            Ino = 7,
            Uid = 8,
            Gid = 9,
            FileSize = 10,
        });

        index.ReadIndex(source);

        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.Equal(oidB, entry.Id);
        Assert.Equal(0, entry.Ctime.Seconds);
        Assert.Equal(0, entry.Mtime.Seconds);
        Assert.Equal(0u, entry.Dev);
        Assert.Equal(0u, entry.Ino);
        Assert.Equal(0u, entry.Uid);
        Assert.Equal(0u, entry.Gid);
        Assert.Equal(0u, entry.FileSize);
    }

    [Fact]
    public void ReadIndex_MatchingEntry_StatFieldsPreserved()
    {
        // Control: a matching entry (same path/OID/mode) keeps the target's
        // stat fields.
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        var source = GitIndex.New(GitHashAlgorithmKind.Sha1);

        var oid = GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1);
        index.Add(new GitIndexEntry("f.txt", oid, GitFileMode.Regular)
        {
            Ctime = new IndexTime(100, 0),
            FileSize = 5,
        });
        source.Add(new GitIndexEntry("f.txt", oid, GitFileMode.Regular)
        {
            Ctime = new IndexTime(300, 0),
            FileSize = 10,
        });

        index.ReadIndex(source);

        Assert.Equal(100, index.EntryByIndex(0).Ctime.Seconds);
        Assert.Equal(5u, index.EntryByIndex(0).FileSize);
    }

    // ---- Add/ConflictAdd reject invalid modes ----

    [Fact]
    public void Add_TreeMode_Throws()
    {
        // C's git_index_add rejects non-(file/link/commit) modes with
        // 'invalid entry mode' (index.c:1700-1703).
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        var entry = new GitIndexEntry("dir", GitOid.Empty, GitFileMode.Tree);
        Assert.Throws<GitException>(() => index.Add(entry));
    }

    [Fact]
    public void Add_ZeroMode_Throws()
    {
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        var entry = new GitIndexEntry("x", GitOid.Empty, (GitFileMode)0);
        Assert.Throws<GitException>(() => index.Add(entry));
    }

    [Fact]
    public void Add_ValidMode_StillWorks()
    {
        // Control: a regular entry still adds.
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        index.Add(new GitIndexEntry("f.txt", GitOid.Empty, GitFileMode.Regular));
        Assert.Single(index.Entries);
    }

    [Fact]
    public void ConflictAdd_TreeMode_Throws()
    {
        // C's conflict_add uses valid_filemode which rejects Tree
        // (index.c:1840-1845).
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        var tree = new GitIndexEntry("dir", GitOid.Empty, GitFileMode.Tree);
        Assert.Throws<GitException>(() => index.ConflictAdd(null, tree, null));
    }

    // ---- CANNOT_PEEL refs get no ^ line on rewrite ----

    [Fact]
    public async Task PackedRefs_CannotPeel_NoPeelLineOnRewrite()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("i12");

        // An annotated tag object (so a peel COULD be computed).
        GitOid commitOid = await CreateCommitAsync(repo);
        GitOid tagOid;
        GitOid otherOid;
        GitObject commit = (await repo.ObjectLookupAsync(commitOid, TestContext.Current.CancellationToken))!;
        using (commit)
        {
            tagOid = await repo.TagCreateAsync(
                "v1", commit, TestSig(), "tag msg\n", cancellationToken: TestContext.Current.CancellationToken);

            // A second ref to delete (triggers the packed-refs rewrite).
            otherOid = await repo.ObjectWriteAsync(
                GitObjectType.Blob, "y\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await repo.ReferenceCreateAsync("refs/heads/x", otherOid, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Write a packed-refs file with a peeled header and the tag ref
        // WITHOUT a peel line — C marks it PACKREF_CANNOT_PEEL
        // (refdb_fs.c:197-202) and emits no ^ line on rewrite.
        string packedPath = Path.Combine(repoPath, ".git", "packed-refs");
        await File.WriteAllTextAsync(
            packedPath,
            "# pack-refs with: peeled fully-peeled sorted\n" +
            $"{tagOid} refs/tags/v1\n" +
            $"{otherOid} refs/heads/x\n",
            TestContext.Current.CancellationToken);

        // Delete the packed ref — this rewrites packed-refs.
        await repo.Refs.DeleteAsync("refs/heads/x", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(packedPath, TestContext.Current.CancellationToken);
        Assert.Contains("refs/tags/v1", content);
        Assert.DoesNotContain("^", content);
    }

    // ---- loose-ref enumeration is byte-wise ----

    [Fact]
    public async Task Enumerate_NonBmpRefs_ByteWiseOrder()
    {
        (GitRepository repo, _) = await CreateRepoAsync("i13");
        GitOid oid = await CreateCommitAsync(repo);

        // U+E000 (BMP, UTF-8 EE 80 80) vs 😀 (non-BMP, UTF-8 F0 9F 98 80):
        // UTF-16 ordinal puts the emoji first (0xD83D < 0xE000); byte-wise
        // strcmp puts U+E000 first (0xEE < 0xF0).
        await repo.ReferenceCreateAsync("refs/heads/\uE000", oid, cancellationToken: TestContext.Current.CancellationToken);
        await repo.ReferenceCreateAsync("refs/heads/\uD83D\uDE00", oid, cancellationToken: TestContext.Current.CancellationToken);

        var names = new List<string>();
        await foreach (GitReference r in repo.ReferenceListAsync("refs/heads/*", TestContext.Current.CancellationToken))
        {
            names.Add(r.Name);
        }

        // Byte-wise strcmp: U+E000 (EE 80 80) sorts before the emoji
        // (F0 9F 98 80); UTF-16 ordinal would put the emoji first.
        int e000 = names.IndexOf("refs/heads/\uE000");
        int emoji = names.IndexOf("refs/heads/\uD83D\uDE00");
        Assert.True(e000 >= 0 && emoji >= 0);
        Assert.True(e000 < emoji, $"expected U+E000 before emoji, got {string.Join(", ", names)}");
    }

    // ---- LockAsync removes the whole empty colliding sub-tree ----

    [Fact]
    public async Task CreateRef_OverNestedEmptyDirs_Succeeds()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("i14");
        GitOid oid = await CreateCommitAsync(repo);

        // Leftover empty dirs from deleted refs/x/y/z: refs/heads/x/y.
        Directory.CreateDirectory(Path.Combine(repoPath, ".git", "refs", "heads", "x", "y"));

        // C's loose_lock removes the whole empty tree (refdb_fs.c:1155-1160),
        // so the write succeeds cleanly instead of failing with a raw
        // IOException from File.Move over the remaining directory.
        await repo.ReferenceCreateAsync("refs/heads/x", oid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? created = await repo.ReferenceLookupAsync("refs/heads/x", TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(oid, ((GitDirectReference)created).Target);
    }
}

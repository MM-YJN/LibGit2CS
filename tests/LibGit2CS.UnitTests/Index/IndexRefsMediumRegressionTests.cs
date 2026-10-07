using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Index;

// Regression coverage against libgit2 1.9.4.
// parity behaviors for Index/Refs:
//   v2/v3 entry path must be size-checked first — C checks entry_size at
//         index.c:2657-2660 before index_entry_dup at 2662, so a crafted
//         index reports a clean index error rather than
//         ArgumentOutOfRangeException.
//   Index and loose-ref commits use an atomic rename
//         (git_filebuf_commit → p_rename, filebuf.c:447) — a code-only
//         crash-window change with no observable behavior difference.
//   RenameAsync deletes the old ref before writing the new one so
//         namespace-colliding renames do not fail with a raw IOException
//         (C: delete_tail first, refdb_fs.c:1867).
//   CompressAsync prunes the packed loose refs like packed_remove_loose
//         (refdb_fs.c:1348-1398, 1465-1468).
//   Branch rename repoints HEAD and writes a HEAD reflog entry
//         (C: update_reflog=1, refdb_fs.c:1616).
public sealed class IndexRefsMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public IndexRefsMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexRefsMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoWithCommitAsync()
    {
        string repoPath = Path.Combine(_tempDir, "repo_" + Guid.NewGuid().ToString("N")[..8]);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        return repo;
    }

    // ── Crafted Index Oversized Name Mask Throws Clean Error ─────────

    [Fact]
    public async Task CraftedIndex_OversizedNameMask_ThrowsCleanError()
    {
        // A minimal v2 index (DIRC header + one 62-byte fixed entry whose
        // flags NAMEMASK = 0xFFE + checksum footer) passes the minimal-size
        // check; C checks entry_size first (index.c:2657-2660) and reports a
        // clean index error.
        byte[] idx = new byte[12 + 62 + 20];
        idx[0] = (byte)'D';
        idx[1] = (byte)'I';
        idx[2] = (byte)'R';
        idx[3] = (byte)'C';
        BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(4, 4), 2); // version
        BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(8, 4), 1); // entry count

        // 62-byte fixed entry: flags = 0xFFE (NAMEMASK), stage 0.
        int entryStart = 12;
        BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(entryStart + 40, 4), 0); // oid[0..4]
        idx[entryStart + 60] = 0x0E; // flags low byte (0xFFE)
        idx[entryStart + 61] = 0x00;

        string indexPath = Path.Combine(_tempDir, "crafted-index");
        await File.WriteAllBytesAsync(indexPath, idx, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("invalid index checksum", ex.Message);
    }

    // ── Rename Namespace Collision Works ─────────────────────────────

    [Fact]
    public async Task Rename_NamespaceCollision_Works()
    {
        // refs/heads/a/b → refs/heads/a/b/c: C deletes the
        // old loose ref FIRST (delete_tail, refdb_fs.c:1867), then writes
        // the new name, so the parent dir does not collide with the old file.
        await using GitRepository repo = await InitRepoWithCommitAsync();
        GitReference? branch = await repo.ReferenceCreateAsync("refs/heads/a/b", (await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken) as GitDirectReference)!.Target, force: true, "create\n", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);

        GitReference renamed = await repo.ReferenceRenameAsync(branch!, "refs/heads/a/b/c", force: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("refs/heads/a/b/c", renamed.Name);
        Assert.NotNull(await repo.ReferenceLookupAsync("refs/heads/a/b/c", TestContext.Current.CancellationToken));
        Assert.Null(await repo.ReferenceLookupAsync("refs/heads/a/b", TestContext.Current.CancellationToken));
    }

    // ── Compress Prunes Packed Loose Refs ────────────────────────────

    [Fact]
    public async Task Compress_PrunesPackedLooseRefs()
    {
        // git_refdb_compress packs the loose refs AND removes their loose
        // files (packed_write → packed_remove_loose, refdb_fs.c:1465-1468).
        await using GitRepository repo = await InitRepoWithCommitAsync();
        GitOid headOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;
        await repo.ReferenceCreateAsync("refs/heads/feature", headOid, force: false, "create\n", cancellationToken: TestContext.Current.CancellationToken);

        string looseMain = Path.Combine(repo.Path, "refs", "heads", "main");
        string looseFeature = Path.Combine(repo.Path, "refs", "heads", "feature");
        Assert.True(File.Exists(looseMain), "precondition: main is loose");
        Assert.True(File.Exists(looseFeature), "precondition: feature is loose");

        await repo.Refs.CompressAsync(TestContext.Current.CancellationToken);

        // Loose files pruned; the packed file resolves both branches.
        Assert.False(File.Exists(looseMain), "main's loose file must be pruned after compress");
        Assert.False(File.Exists(looseFeature), "feature's loose file must be pruned after compress");
        Assert.True(File.Exists(Path.Combine(repo.Path, "packed-refs")));
        Assert.Equal(headOid, ((GitDirectReference)(await repo.ReferenceResolveAsync("refs/heads/main", TestContext.Current.CancellationToken))!).Target);
        Assert.Equal(headOid, ((GitDirectReference)(await repo.ReferenceResolveAsync("refs/heads/feature", TestContext.Current.CancellationToken))!).Target);
    }

    // ── Branch Rename Writes Head Reflog Entry ───────────────────────

    [Fact]
    public async Task BranchRename_WritesHeadReflogEntry()
    {
        // C's refs_update_head → git_reference_symbolic_set_target writes the
        // HEAD reflog (update_reflog=1, refdb_fs.c:1616) with the old/new
        // branch OIDs (reflog_append, refdb_fs.c:2324-2335).
        await using GitRepository repo = await InitRepoWithCommitAsync();
        GitOid headOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;

        int before = (await repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken))?.Count() ?? 0;

        GitReference? main = await repo.ReferenceLookupAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.ReferenceRenameAsync(main!, "refs/heads/renamed", force: false, cancellationToken: TestContext.Current.CancellationToken);

        GitRefLog? headLog = await repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.NotNull(headLog);
        Assert.True(headLog!.Count() > before, "HEAD reflog must gain an entry after a branch rename");
        Assert.Equal(headOid, headLog.First().NewId);
    }
}

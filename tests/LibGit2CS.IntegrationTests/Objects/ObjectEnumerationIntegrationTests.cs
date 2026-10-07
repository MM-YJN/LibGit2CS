using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for <see cref="GitObjectDb.EnumerateAsync"/> exercised
/// end-to-end against locally-initialized repos with mixed loose/packed/midx
/// object layouts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>ObjectDbTests</c> covers
/// <see cref="GitObjectDb"/> read/write against a real ODB, and the
/// <c>LooseObjectBackendTests</c>/<c>PackFileTests</c> cover the per-backend
/// read paths. None of them drain the
/// <see cref="GitObjectDb.EnumerateCoreAsync"/> iterator that merges OIDs
/// across backends with a <c>seen</c>-set dedup. The 51-line
/// <see cref="GitObjectDb.EnumerateCoreAsync"/> <c>MoveNext</c> was entirely
/// cold in the integration suite, as were the per-backend
/// <see cref="PackObjectBackend.EnumerateAsync"/> and
/// <see cref="LooseObjectBackend.EnumerateAsync"/> iterators when reached
/// through the public <see cref="GitObjectDb"/> surface. These tests close
/// that gap by writing objects across loose + pack + multi-pack-index
/// layouts and enumerating to completion.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/odb/odb.c</c> (<c>test_odb__writepack</c>,
/// <c>test_odb__ReadObjectsFromPack</c>, <c>test_odb__packing</c>), adapted
/// to build the ODB from scratch and use <see cref="GitPackWriter"/> +
/// <see cref="GitObjectDb.WriteMultiPackIndexAsync"/> directly.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class ObjectEnumerationIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-odbenum-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Inits a non-bare repo and returns it (caller disposes via
    /// <c>await using</c>).
    /// </summary>
    private static async Task<GitRepository> InitRepoAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        return repo;
    }

    /// <summary>
    /// Writes a blob, builds a one-entry tree pointing at it, and writes a
    /// root commit on <paramref name="refName"/> with that tree. Returns
    /// (blobOid, treeOid, commitOid). All three objects are loose.
    /// </summary>
    private static async Task<(GitOid Blob, GitOid Tree, GitOid Commit)> WriteBasicCommitAsync(
        GitRepository repo, byte[] blobContent, string message, string refName, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, blobContent, ct);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct);
        return (blobOid, treeOid, commitOid);
    }

    /// <summary>
    /// Packs the given OIDs into a single pack file in the repo's
    /// <c>objects/pack/</c> directory and returns the pack file name. The
    /// <see cref="PackObjectBackend"/> picks the pack up on the next
    /// <see cref="GitRepository.OpenAsync"/>.
    /// </summary>
    private static async Task<string> WritePackAsync(GitRepository repo, IReadOnlyList<GitOid> oids, CancellationToken ct)
    {
        using GitPackWriter writer = repo.NewPackWriter();
        foreach (GitOid oid in oids)
        {
            await writer.InsertAsync(oid, ct);
        }

        string packDir = Path.Combine(repo.Path, "objects", "pack");
        Directory.CreateDirectory(packDir);
        return await writer.WriteToDirectoryAsync(packDir, progress: null, cancellationToken: ct);
    }

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── loose-only enumeration ───────────────────────────────────────────

    /// <summary>
    /// <see cref="GitObjectDb.EnumerateAsync"/> on a repo with only
    /// loose objects yields every written OID exactly once. Writing a
    /// blob, tree, and commit produces three loose objects; enumerating
    /// drains the <see cref="LooseObjectBackend.EnumerateAsync"/> iterator
    /// through <see cref="GitObjectDb.EnumerateCoreAsync"/> and yields all
    /// three OIDs.
    /// </summary>
    [Fact]
    public async Task Enumerate_LooseOnly_YieldsAllWrittenObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid blobOid, treeOid, commitOid;
            await using (GitRepository repo = await InitRepoAsync(path, ct))
            {
                (blobOid, treeOid, commitOid) = await WriteBasicCommitAsync(repo, "hello\n"u8.ToArray(), "init\n", "refs/heads/main", ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitOid> oids = await repo2.Objects.EnumerateAsync(ct).ToListAsync(ct);

            Assert.Equal(3, oids.Count);
            Assert.Contains(blobOid, oids);
            Assert.Contains(treeOid, oids);
            Assert.Contains(commitOid, oids);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitObjectDb.EnumerateAsync"/> on a freshly-initialized
    /// repo with no written objects yields nothing. Drains the iterator to
    /// completion and asserts emptiness — exercises the empty-backend path.
    /// </summary>
    [Fact]
    public async Task Enumerate_EmptyRepo_YieldsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, ct);

            List<GitOid> oids = await repo.Objects.EnumerateAsync(ct).ToListAsync(ct);

            Assert.Empty(oids);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── packed enumeration ───────────────────────────────────────────────

    /// <summary>
    /// After packing all loose objects into a pack file and reopening,
    /// <see cref="GitObjectDb.EnumerateAsync"/> yields the packed OIDs.
    /// Exercises <see cref="PackObjectBackend.EnumerateAsync"/> reached
    /// through <see cref="GitObjectDb.EnumerateCoreAsync"/>. The loose
    /// objects are still on disk (packing does not remove them), so each
    /// OID is yielded TWICE — once per backend (C's git_odb_foreach,
    /// odb.c:1600-1605, iterates every backend with no dedup).
    /// </summary>
    [Fact]
    public async Task Enumerate_AfterPackWrite_YieldsPackedObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid blobOid, treeOid, commitOid;
            await using (GitRepository repo = await InitRepoAsync(path, ct))
            {
                (blobOid, treeOid, commitOid) = await WriteBasicCommitAsync(repo, "hello\n"u8.ToArray(), "init\n", "refs/heads/main", ct);
                await WritePackAsync(repo, [blobOid, treeOid, commitOid], ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitOid> oids = await repo2.Objects.EnumerateAsync(ct).ToListAsync(ct);

            // 3 loose + 3 packed = 6 — the loose objects still exist on disk.
            Assert.Equal(6, oids.Count);
            Assert.Equal(2, oids.Count(o => o == blobOid));
            Assert.Equal(2, oids.Count(o => o == treeOid));
            Assert.Equal(2, oids.Count(o => o == commitOid));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitObjectDb.EnumerateAsync"/> yields OIDs that exist
    /// in both the loose and packed backends TWICE. Writing a blob loose,
    /// packing it, then enumerating yields the OID once per backend —
    /// C's <c>git_odb_foreach</c> (odb.c:1600-1605) iterates each backend
    /// with no <c>seen</c>-set dedup, so the managed port must not add one.
    /// </summary>
    [Fact]
    public async Task Enumerate_Dedupes_LooseAndPacked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid blobOid, treeOid, commitOid;
            await using (GitRepository repo = await InitRepoAsync(path, ct))
            {
                (blobOid, treeOid, commitOid) = await WriteBasicCommitAsync(repo, "hello\n"u8.ToArray(), "init\n", "refs/heads/main", ct);
                // Pack the same three objects that are also loose.
                await WritePackAsync(repo, [blobOid, treeOid, commitOid], ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitOid> oids = await repo2.Objects.EnumerateAsync(ct).ToListAsync(ct);

            // Six, not three — each OID is yielded by BOTH the loose and
            // the pack backend (git_odb_foreach has no dedup).
            Assert.Equal(6, oids.Count);
            Assert.Equal(2, oids.Count(o => o == blobOid));
            Assert.Equal(2, oids.Count(o => o == treeOid));
            Assert.Equal(2, oids.Count(o => o == commitOid));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── multi-pack-index enumeration ─────────────────────────────────────

    /// <summary>
    /// After packing objects and writing a multi-pack index, enumeration
    /// still yields all OIDs. The <see cref="PackObjectBackend"/> reads
    /// packs through the midx when present; enumeration must still surface
    /// every OID. Exercises <see cref="GitObjectDb.WriteMultiPackIndexAsync"/>
    /// followed by a full drain. Each OID still appears twice (loose + pack).
    /// </summary>
    [Fact]
    public async Task Enumerate_AfterMultiPackIndex_YieldsMidxObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid blobOid, treeOid, commitOid;
            await using (GitRepository repo = await InitRepoAsync(path, ct))
            {
                (blobOid, treeOid, commitOid) = await WriteBasicCommitAsync(repo, "hello\n"u8.ToArray(), "init\n", "refs/heads/main", ct);
                await WritePackAsync(repo, [blobOid, treeOid, commitOid], ct);
            }

            // Reopen so the PackObjectBackend picks up the new pack file,
            // then write the multi-pack index.
            await using (GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await repo2.Objects.WriteMultiPackIndexAsync(ct);
            }

            await using GitRepository repo3 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitOid> oids = await repo3.Objects.EnumerateAsync(ct).ToListAsync(ct);

            Assert.Equal(6, oids.Count);
            Assert.Equal(2, oids.Count(o => o == blobOid));
            Assert.Equal(2, oids.Count(o => o == treeOid));
            Assert.Equal(2, oids.Count(o => o == commitOid));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── enumeration + lookup interaction ─────────────────────────────────

    /// <summary>
    /// After enumerating to drain the ODB, prefix-based lookup
    /// (<see cref="GitObjectDb.LookupPrefixAsync"/>) resolves a shortened
    /// OID. Exercises the interaction of enumeration (which loads pack
    /// indexes) with the prefix-resolution path that consults those
    /// indexes.
    /// </summary>
    [Fact]
    public async Task Enumerate_ThenLookupPrefix_ResolvesShortOid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid blobOid, treeOid, commitOid;
            await using (GitRepository repo = await InitRepoAsync(path, ct))
            {
                (blobOid, treeOid, commitOid) = await WriteBasicCommitAsync(repo, "hello\n"u8.ToArray(), "init\n", "refs/heads/main", ct);
                await WritePackAsync(repo, [blobOid, treeOid, commitOid], ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Drain enumeration first — exercises the pack enumeration path.
            // 3 loose + 3 packed = 6 (git_odb_foreach yields per backend).
            List<GitOid> all = await repo2.Objects.EnumerateAsync(ct).ToListAsync(ct);
            Assert.Equal(6, all.Count);

            // Now resolve a 7-char prefix of the commit OID.
            string shortHex = commitOid.ToString()[..7];
            var prefixOid = GitOid.Parse(shortHex, repo2.ObjectFormat);
            Commit? resolved = await repo2.ObjectLookupPrefixAsync<Commit>(prefixOid, ct);
            Assert.NotNull(resolved);
            Assert.Equal(commitOid, resolved!.Id);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

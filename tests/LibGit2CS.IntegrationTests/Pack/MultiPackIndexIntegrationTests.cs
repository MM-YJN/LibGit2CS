using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Pack;

/// <summary>
/// Integration tests for the multi-pack-index writer + reader
/// (<see cref="MultiPackIndexWriter"/> via
/// <see cref="GitObjectDb.WriteMultiPackIndexAsync"/> →
/// <see cref="MultiPackIndex"/>) exercised end-to-end against
/// locally-initialized repos with real pack files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> <see cref="MultiPackIndexWriter"/> and
/// <see cref="MultiPackIndex"/> (read side) had <c>0%</c> and <c>3.4%</c>
/// coverage respectively — no integration test writes a MIDX file and
/// reads it back. The unit <c>MidxWriterTests</c> cover chunk
/// serialization against synthetic pack entries, but the
/// <see cref="PackObjectBackend.WriteMultiPackIndexAsync"/> path (which
/// enumerates the pack backends, constructs a
/// <see cref="MultiPackIndexWriter"/>, adds each pack's <c>.idx</c>, and
/// commits) and the <see cref="MultiPackIndex.OpenAsync"/>/
/// <see cref="MultiPackIndex.FindEntry"/> round-trip were never exercised
/// end-to-end. These tests create packs via a local <c>file://</c> clone
/// (the <see cref="Transports.LocalTransportTests"/> pattern with
/// <see cref="GitCloneLocal.NoLocal"/>), then write a MIDX and verify the
/// read-back resolves a known blob OID. Also covers the
/// <see cref="GitErrorCode.NotSupported"/> path when no packs exist.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/midx/midx.c</c>
/// (<c>test_midx__write_and_read</c>,
/// <c>test_midx__no_packs</c>), adapted to build the sandbox from scratch
/// (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class MultiPackIndexIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Converts a filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath(string tag)
        => Path.Combine(Path.GetTempPath(), "libgit2cs-midx-" + tag + "-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// Builds a non-bare source repo with a single commit containing
    /// <c>hello.txt</c>, sets HEAD to <c>refs/heads/master</c>, and returns
    /// the path + the commit's tree blob OID (for later MIDX lookup).
    /// </summary>
    private static async Task<(string Path, GitOid BlobOid)> BuildSourceRepoAsync(string sourcePath, CancellationToken ct)
    {
        await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello-midx\n"u8.ToArray(), ct).ConfigureAwait(false);
        using GitTreeBuilder treeBld = source.NewTreeBuilder();
        await treeBld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct).ConfigureAwait(false);
        GitOid treeOid = await treeBld.WriteAsync(ct).ConfigureAwait(false);
        await source.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "midx-init\n",
            UpdateRef = "refs/heads/master",
        }, ct).ConfigureAwait(false);
        await source.SetHeadAsync("refs/heads/master", ct).ConfigureAwait(false);
        return (sourcePath, blobOid);
    }

    // ── write + read round-trip ─────────────────────────────────────────

    /// <summary>
    /// After a local <c>file://</c> clone (which creates a pack in the
    /// target's <c>objects/pack/</c>), calling
    /// <see cref="GitObjectDb.WriteMultiPackIndexAsync"/> writes a MIDX
    /// file, and <see cref="MultiPackIndex.OpenAsync"/> reads it back with
    /// <see cref="MultiPackIndex.NumPacks"/> == 1 and
    /// <see cref="MultiPackIndex.FindEntry"/> resolving the source blob's
    /// OID. Exercises the full
    /// <see cref="PackObjectBackend.WriteMultiPackIndexAsync"/> →
    /// <see cref="MultiPackIndexWriter"/> → <see cref="MultiPackIndex"/>
    /// round-trip.
    /// </summary>
    [Fact]
    public async Task WriteMultiPackIndex_AfterLocalClone_RoundTrips()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("src");
        string targetPath = NewRepoPath("dst");
        try
        {
            (_, GitOid blobOid) = await BuildSourceRepoAsync(sourcePath, ct).ConfigureAwait(false);

            // Clone via file:// with NoLocal to force the smart-local path
            // (creates a pack in the target's objects/pack/).
            string posixPath = sourcePath.Replace('\\', '/');
            string url = FileUrl(posixPath);
            var cloneOpts = new GitCloneOptions { CloneLocal = GitCloneLocal.NoLocal };

            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);
            string packDir = Path.Combine(cloned.Path, "objects", "pack");
            Assert.NotEmpty(Directory.EnumerateFiles(packDir, "pack-*.pack"));

            // Write the MIDX.
            await cloned.Objects.WriteMultiPackIndexAsync(ct).ConfigureAwait(false);

            // The MIDX file must exist.
            string midxPath = Path.Combine(packDir, "multi-pack-index");
            Assert.True(File.Exists(midxPath), "multi-pack-index file not written");

            // Read it back and verify the blob OID resolves.
            MultiPackIndex? midx = await MultiPackIndex.OpenAsync(packDir, cloned.ObjectFormat, ct).ConfigureAwait(false);
            Assert.NotNull(midx);
            Assert.Equal(1, midx!.NumPacks);
            MultiPackIndexEntry? entry = midx.FindEntry(blobOid);
            Assert.NotNull(entry);
            Assert.True(entry!.Value.Offset >= 0, "MIDX entry offset must be non-negative");
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(targetPath);
        }
    }

    // ── error / edge paths ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitObjectDb.WriteMultiPackIndexAsync"/> on a fresh
    /// repo with no packs throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.NotSupported"/> — the <c>writes == 0</c>
    /// branch (no pack backend wrote a MIDX because there are no packs).
    /// </summary>
    [Fact]
    public async Task WriteMultiPackIndex_NoPacks_ThrowsNotSupported()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath("empty");
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);

            // Write a loose blob (no pack is created).
            await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct).ConfigureAwait(false);

            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.Objects.WriteMultiPackIndexAsync(ct));
            Assert.Equal(GitErrorCode.NotSupported, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="MultiPackIndex.OpenAsync"/> on a pack directory with
    /// no MIDX file returns null (the <c>!File.Exists</c> early-return
    /// branch).
    /// </summary>
    [Fact]
    public async Task OpenAsync_MissingFile_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("src2");
        string targetPath = NewRepoPath("dst2");
        try
        {
            await BuildSourceRepoAsync(sourcePath, ct).ConfigureAwait(false);

            string posixPath = sourcePath.Replace('\\', '/');
            string url = FileUrl(posixPath);
            var cloneOpts = new GitCloneOptions { CloneLocal = GitCloneLocal.NoLocal };

            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);
            string packDir = Path.Combine(cloned.Path, "objects", "pack");

            // No MIDX written yet → OpenAsync returns null.
            MultiPackIndex? midx = await MultiPackIndex.OpenAsync(packDir, cloned.ObjectFormat, ct).ConfigureAwait(false);
            Assert.Null(midx);
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(targetPath);
        }
    }
}

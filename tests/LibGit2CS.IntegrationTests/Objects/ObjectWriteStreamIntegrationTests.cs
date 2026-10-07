using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for the internal streaming object writer
/// (<see cref="GitObjectDb.OpenWriteStreamAsync"/> → <see cref="ObjectWriteStream"/>
/// + <see cref="OdbWriteStream"/>) exercised end-to-end against
/// locally-initialized repos with a real ODB backend chain (loose + pack).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>ObjectDbTests</c> cover
/// <see cref="GitObjectDb.OpenWriteStreamAsync"/> against a standalone
/// <see cref="GitObjectDb"/> with a single hand-rolled
/// <see cref="LooseObjectBackend"/> and no owner repository. The integration
/// <c>BlobWriteStreamIntegrationTests</c> cover the <i>public</i> blob
/// streaming façade (<see cref="GitBlob.CreateWriteStream"/>), but that path
/// funnels through <see cref="GitObjectDb.WriteAsync"/>(<c>byte[]</c>) — the
/// buffer-based write — and never opens an <see cref="ObjectWriteStream"/>.
/// As a result the streaming-write path through a real
/// <see cref="GitRepository"/> (full backend chain, real on-disk loose-object
/// file layout, hash-verification + atomic temp-rename) was entirely cold in
/// the default (no-Docker) integration run.
/// </para>
/// <para>
/// These tests call <see cref="GitObjectDb.OpenWriteStreamAsync"/> directly
/// (the integration-test assembly is <c>InternalsVisibleTo</c> the source)
/// and assert: round-trip via <see cref="GitObjectDb.LookupAsync"/>,
/// chunked writes accumulate, the loose object file lands at the expected
/// <c>objects/xx/yyyy...</c> path, the size/hash mismatch guards throw, and
/// streaming produces the same OID as the buffer-based write.
/// </para>
/// <para>
/// <b>Hardcoded empty objects.</b> The zero-length and empty-blob cases also exercise the
/// empty-object handling in <see cref="GitObjectDb.LookupAsync"/> (and the
/// typed <see cref="GitObjectDb.LookupAsync{T}"/> overload). C hardcodes
/// ONLY the empty tree (odb.c:60-84); the empty blob is not special-cased
/// anywhere and misses with <c>GIT_ENOTFOUND</c> until it is actually
/// written. <b>Note:</b> <see cref="RawGitObject"/> is documented as the
/// fallback type for these objects, but the dispatch at
/// <see cref="GitObjectDb.LookupAsync"/> actually routes them through
/// <see cref="GitObject.Parse"/>, returning a
/// <see cref="GitBlob"/>/<see cref="GitTree"/> instance — the
/// <see cref="RawGitObject"/> constructor has no live caller in
/// <c>source/</c> (only direct unit-test construction in
/// <c>GitObjectTests.cs</c>). This file does not assert on the runtime type
/// of the returned object; it asserts only the parsed <c>Type</c>, <c>Size</c>,
/// and body emptiness — which are stable regardless of dispatch.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/object/odb/stream.c</c> (<c>test_odb__stream_write</c>,
/// <c>test_odb__stream_write_in_chunks</c>,
/// <c>test_odb__stream_write_invalid_hash</c>) and the hardcoded
/// empty-tree behavior of <c>git_odb_read</c>/<c>git_odb_read_header</c>
/// (odb.c:60-84, 1235-1244, 1342-1348), adapted to build the sandbox from
/// scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class ObjectWriteStreamIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-odbws-" + Guid.NewGuid().ToString("N"));

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

    // ── streaming write round-trip via LookupAsync ──────────────────────

    /// <summary>
    /// Writing a known blob body to an <see cref="ObjectWriteStream"/>
    /// obtained from <see cref="GitObjectDb.OpenWriteStreamAsync"/> and
    /// finalizing produces an object whose round-trip via
    /// <see cref="GitObjectDb.LookupAsync"/> yields byte-identical content
    /// and the correct <see cref="GitObjectType.Blob"/> type.
    /// </summary>
    /// <remarks>
    /// This exercises the full real-repo chain
    /// (<see cref="GitObjectDb.OpenWriteStreamAsync"/> → backend
    /// <see cref="LooseObjectBackend"/>'s <c>LooseWriteStream</c> → zlib
    /// compress + atomic temp-rename via
    /// <see cref="AsyncFileIO.WriteAtomicIfMissingAsync"/>) and the
    /// <see cref="OdbWriteStream"/> hash-and-size verification.
    /// </remarks>
    [Fact]
    public async Task StreamWrite_Blob_RoundTripsThroughLookup()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("streamed blob body\n");
            GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

            await using ObjectWriteStream stream = await repo.Objects.OpenWriteStreamAsync(GitObjectType.Blob, body.Length, ct).ConfigureAwait(false);
            stream.Write(body);
            GitOid oid = await stream.FinalizeAsync(expectedOid, ct).ConfigureAwait(false);

            Assert.Equal(expectedOid, oid);

            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(oid, ct).ConfigureAwait(false))!;
            Assert.NotNull(blob);
            Assert.Equal(body, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Multiple <see cref="ObjectWriteStream.Write"/> calls on the
    /// same stream concatenate the chunks before finalize; the resulting
    /// object matches a single-shot write of the assembled buffer. Verifies
    /// the in-memory <see cref="PooledByteBufferWriter"/> accumulates across
    /// writes and that <see cref="OdbWriteStream.ReceivedBytes"/> matches the
    /// declared size after the final chunk.
    /// </summary>
    [Fact]
    public async Task StreamWrite_Chunked_WritesAccumulateCorrectly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] part1 = Encoding.UTF8.GetBytes("chunk-1\n");
            byte[] part2 = Encoding.UTF8.GetBytes("chunk-2\n");
            byte[] part3 = Encoding.UTF8.GetBytes("chunk-3\n");
            byte[] assembled = [.. part1, .. part2, .. part3];

            GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Blob, assembled, GitHashAlgorithmKind.Sha1);

            await using ObjectWriteStream stream = await repo.Objects.OpenWriteStreamAsync(GitObjectType.Blob, assembled.Length, ct).ConfigureAwait(false);
            stream.Write(part1);
            stream.Write(part2);
            stream.Write(part3);
            GitOid oid = await stream.FinalizeAsync(expectedOid, ct).ConfigureAwait(false);

            Assert.Equal(expectedOid, oid);

            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(oid, ct).ConfigureAwait(false))!;
            Assert.Equal(assembled, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── on-disk loose-object layout for non-blob types ──────────────────

    /// <summary>
    /// Streaming-write of a non-blob object type (here:
    /// <see cref="GitObjectType.Tree"/>) lands a zlib-compressed loose
    /// object file at the canonical <c>objects/xx/yyyy...</c> path inside
    /// the repo's gitdir. The streaming path is type-agnostic — any body
    /// bytes hash to an OID and persist identically regardless of the
    /// <see cref="GitObjectType"/> label.
    /// </summary>
    /// <remarks>
    /// The body here is not a semantically valid tree (we don't try to
    /// <see cref="GitObjectDb.LookupAsync"/> it as a tree); the assertion is
    /// purely on the on-disk side effect — that the loose backend performed
    /// the atomic temp-rename to the expected path. Mirrors
    /// <c>test_odb__stream_write</c> in <c>tests/libgit2/object/odb/stream.c</c>.
    /// </remarks>
    [Fact]
    public async Task StreamWrite_TreeBody_WritesLooseObjectFileAtExpectedPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // A minimal but valid empty tree body (zero entries). Its sha1 is
            // the well-known empty-tree OID 4b825dc642cb6eb9a060e54bf8d69288fbee4904.
            byte[] body = Array.Empty<byte>();
            GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Tree, body, GitHashAlgorithmKind.Sha1);
            Assert.Equal(GitOid.EmptyTreeSha1, expectedOid);

            await using ObjectWriteStream stream = await repo.Objects.OpenWriteStreamAsync(GitObjectType.Tree, body.Length, ct).ConfigureAwait(false);
            // No Write calls — DeclaredSize is 0 and ReceivedBytes is 0.
            GitOid oid = await stream.FinalizeAsync(expectedOid, ct).ConfigureAwait(false);

            Assert.Equal(expectedOid, oid);

            // The loose object file MUST exist on disk at objects/xx/yyyy...
            string expectedFile = Path.Combine(repo.Path, "objects", oid.ToPathString());
            Assert.True(File.Exists(expectedFile), $"expected loose object file at {expectedFile}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// The streaming-write path and the buffer-based
    /// <see cref="GitObjectDb.WriteAsync"/>(<c>byte[]</c>) path produce the
    /// same OID for the same content — both compute the hash over
    /// <c>"&lt;type&gt; &lt;size&gt;\0"</c> + body. This is the parity
    /// invariant the managed port relies on (the C port shares a single
    /// <c>git_odb_stream</c> hash-ctx between both paths).
    /// </summary>
    [Fact]
    public async Task StreamWrite_AndBufferWrite_ProduceSameOid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pathStream = NewRepoPath();
        string pathBuffer = NewRepoPath();
        await using GitRepository streamRepo = await GitRepository.InitAsync(pathStream, isBare: false, new GitContext(), cancellationToken: ct);
        await using GitRepository bufferRepo = await GitRepository.InitAsync(pathBuffer, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("parity check body\n");

            // Streaming path.
            GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);
            await using (ObjectWriteStream stream = await streamRepo.Objects.OpenWriteStreamAsync(GitObjectType.Blob, body.Length, ct).ConfigureAwait(false))
            {
                stream.Write(body);
                GitOid streamedOid = await stream.FinalizeAsync(expectedOid, ct).ConfigureAwait(false);
                Assert.Equal(expectedOid, streamedOid);
            }

            // Buffer path.
            GitOid bufferedOid = await bufferRepo.ObjectWriteAsync(GitObjectType.Blob, body, ct).ConfigureAwait(false);

            Assert.Equal(expectedOid, bufferedOid);
        }
        finally
        {
            Cleanup(pathStream);
            Cleanup(pathBuffer);
        }
    }

    // ── mismatch guards (size + hash) via real repo ─────────────────────

    /// <summary>
    /// <see cref="ObjectWriteStream.FinalizeAsync"/> throws
    /// <see cref="GitErrorCode.Error"/> when
    /// <see cref="ObjectWriteStream.ReceivedBytes"/> !=
    /// <see cref="ObjectWriteStream.DeclaredSize"/> — here, by declaring 100
    /// bytes but writing only 5. Exercises the
    /// <see cref="OdbWriteStream.FinalizeAsync"/> size-check branch through
    /// the real-repo backend chain (unit tests cover it against a standalone
    /// ODB; integration confirms the wrapper composes correctly inside a
    /// <see cref="GitRepository"/>).
    /// </summary>
    [Fact]
    public async Task StreamWrite_SizeMismatch_ThrowsMismatch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] body = "short"u8.ToArray();
            GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

            await using ObjectWriteStream stream = await repo.Objects.OpenWriteStreamAsync(GitObjectType.Blob, declaredSize: 100, ct).ConfigureAwait(false);
            stream.Write(body);

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await stream.FinalizeAsync(expectedOid, ct).ConfigureAwait(false));
            Assert.Equal(GitErrorCode.Error, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="ObjectWriteStream.FinalizeAsync"/> throws
    /// <see cref="GitErrorCode.Mismatch"/> when the expected OID passed in
    /// does not match the hash computed over the header + body bytes. The
    /// <see cref="OdbWriteStream"/> hash-ctx is fed the
    /// <c>"&lt;type&gt; &lt;size&gt;\0"</c> header on construction and each
    /// chunk on <see cref="OdbWriteStream.Write"/>; at finalize it compares
    /// the result to the caller-supplied <c>expectedOid</c>.
    /// </summary>
    [Fact]
    public async Task StreamWrite_HashMismatch_ThrowsMismatch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] body = "content"u8.ToArray();
            var wrongOid = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

            await using ObjectWriteStream stream = await repo.Objects.OpenWriteStreamAsync(GitObjectType.Blob, body.Length, ct).ConfigureAwait(false);
            stream.Write(body);

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await stream.FinalizeAsync(wrongOid, ct).ConfigureAwait(false));
            Assert.Equal(GitErrorCode.Mismatch, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── hardcoded empty objects via real-repo LookupAsync ───────────────

    /// <summary>
    /// Looking up the well-known empty-blob OID
    /// (<see cref="GitOid.EmptyBlobSha1"/>) in a real repo with no on-disk
    /// object file fails — C hardcodes ONLY the empty tree
    /// (<c>odb_hardcoded_type</c>, odb.c:60-84), so the empty blob falls
    /// through to the backends and misses with <c>GIT_ENOTFOUND</c>. After
    /// the empty blob has been written (via
    /// <see cref="GitObjectDb.WriteAsync"/>), the lookup succeeds with
    /// <see cref="GitObjectType.Blob"/>, <see cref="GitObject.Size"/> 0, and
    /// an empty body.
    /// </summary>
    /// <remarks>
    /// Unit coverage exists (<c>ObjectDbParityTests.Lookup_EmptyBlob_NotStored_ReturnsNull</c>,
    /// <c>Exists_EmptyObjects_AfterWrite_ReturnsTrue</c>) but runs against a
    /// standalone <see cref="GitObjectDb"/> with <c>owner: null</c>. This
    /// test runs the path through a real <see cref="GitRepository"/> so the
    /// owner-bearing branch is also hot.
    /// </remarks>
    [Fact]
    public async Task EmptyBlobOid_Lookup_NotStored_ReturnsNull_AfterWrite_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // C: git_odb_read(&empty_blob) on a repo without the object → GIT_ENOTFOUND.
            Assert.Null(await repo.ObjectLookupAsync(GitOid.EmptyBlobSha1, ct).ConfigureAwait(false));
            Assert.Null(await repo.ObjectLookupAsync<GitBlob>(GitOid.EmptyBlobSha1, ct).ConfigureAwait(false));

            // Once written, the empty blob is a normal loose object.
            GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Blob, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            Assert.Equal(GitOid.EmptyBlobSha1, oid);

            GitObject? obj = await repo.ObjectLookupAsync(GitOid.EmptyBlobSha1, ct).ConfigureAwait(false);
            Assert.NotNull(obj);
            Assert.Equal(GitObjectType.Blob, obj!.Type);
            Assert.Equal(0, obj.Size);
            Assert.True(obj.Raw.IsEmpty);

            // The typed overload must agree and yield a GitBlob.
            GitBlob? typed = await repo.ObjectLookupAsync<GitBlob>(GitOid.EmptyBlobSha1, ct).ConfigureAwait(false);
            Assert.NotNull(typed);
            Assert.True(typed!.Content.IsEmpty);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Looking up the well-known empty-tree OID
    /// (<see cref="GitOid.EmptyTreeSha1"/>) in a real repo succeeds without
    /// any on-disk object — C hardcodes the empty tree
    /// (<c>odb_hardcoded_type</c>, odb.c:60-84; <c>odb_read_hardcoded</c>,
    /// odb.c:68-84), so the lookup short-circuits to
    /// <see cref="GitObject.Parse"/> on the empty-bytes body without
    /// consulting any backend. The resulting object has
    /// <see cref="GitObjectType.Tree"/>, <see cref="GitObject.Size"/>
    /// 0, and an empty body.
    /// </summary>
    [Fact]
    public async Task EmptyTreeOid_Lookup_ReturnsTreeWithEmptyBody()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitObject? obj = await repo.ObjectLookupAsync(GitOid.EmptyTreeSha1, ct).ConfigureAwait(false);

            Assert.NotNull(obj);
            Assert.Equal(GitObjectType.Tree, obj!.Type);
            Assert.Equal(0, obj.Size);
            Assert.True(obj.Raw.IsEmpty);

            // The typed overload must agree and yield a GitTree.
            GitTree? typed = await repo.ObjectLookupAsync<GitTree>(GitOid.EmptyTreeSha1, ct).ConfigureAwait(false);
            Assert.NotNull(typed);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitObjectDb.ExistsAsync"/> reports <c>false</c> for
    /// both well-known empty OIDs when no loose/pack object is present on
    /// disk. C's <c>git_odb_exists_ext</c> (odb.c:1031-1054) has no
    /// hardcoded check — only <c>read</c>/<c>read_header</c> special-case
    /// the empty tree — so both OIDs miss until actually written.
    /// </summary>
    [Fact]
    public async Task EmptyOids_ExistsAsync_ReturnsFalseWithoutDiskObject()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            Assert.False(await repo.Objects.ExistsAsync(GitOid.EmptyBlobSha1, ct).ConfigureAwait(false));
            Assert.False(await repo.Objects.ExistsAsync(GitOid.EmptyTreeSha1, ct).ConfigureAwait(false));
        }
        finally
        {
            Cleanup(path);
        }
    }
}

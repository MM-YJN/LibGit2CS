using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

// CA1849 (call async- Dispose/Write on Stream): GitBlobWriteStream.Write is
// the synchronous Stream override under test here — there is no async
// equivalent to exercise. Suppressed at file scope to keep the build clean.
#pragma warning disable CA1849

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for the streaming blob writer
/// (<see cref="GitBlobWriteStream"/> via
/// <see cref="GitBlob.CreateWriteStream"/> + <see cref="GitBlobWriteStream.CommitAsync"/>)
/// exercised end-to-end against locally-initialized repos with a real ODB
/// and the filter pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>BlobCreateTests</c> cover
/// <see cref="GitBlobWriteStream"/> against an in-memory ODB and with
/// filter lists injected directly. <see cref="GitBlobWriteStream"/> had
/// <c>0%</c> integration coverage — no end-to-end path through
/// <see cref="GitBlob.CreateWriteStream"/> → multiple
/// <see cref="GitBlobWriteStream.Write"/> calls →
/// <see cref="GitBlobWriteStream.CommitAsync"/> (which applies clean
/// filters via <see cref="GitFilterList.LoadAsync"/> when a hint path is
/// given) → <see cref="GitObjectDb.WriteAsync"/> ever ran. These tests
/// also cover the double-commit / write-after-commit / write-after-dispose
/// guard branches.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/blob/stream.cpp</c>
/// (<c>test_blob_write_from_stream__buf</c>,
/// <c>test_blob_write_from_stream__filter</c>), adapted to build the
/// sandbox from scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class BlobWriteStreamIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-blobws-" + Guid.NewGuid().ToString("N"));

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

    // ── basic write + commit ─────────────────────────────────────────────

    /// <summary>
    /// Writing known bytes to a <see cref="GitBlobWriteStream"/> and
    /// committing produces a blob whose <see cref="GitBlob.Content"/>
    /// matches exactly, and <see cref="GitBlob.IsBinary"/> is false for
    /// text content.
    /// </summary>
    [Fact]
    public async Task WriteAndCommit_ProducesBlobWithExactContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] content = Encoding.UTF8.GetBytes("hello blob stream\n");
            using GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: (string?)null);
            stream.Write(content, 0, content.Length);
            GitOid oid = await stream.CommitAsync(ct).ConfigureAwait(false);

            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(oid, ct).ConfigureAwait(false))!;
            Assert.Equal(content, blob.Content.ToArray());
            Assert.False(blob.IsBinary);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Multiple <see cref="GitBlobWriteStream.Write"/> calls
    /// concatenate the chunks; <see cref="GitBlobWriteStream.CommitAsync"/>
    /// writes the assembled buffer. Verifies the MemoryStream backing the
    /// stream accumulates across writes.
    /// </summary>
    [Fact]
    public async Task WriteInChunks_ThenCommit_AssemblesCorrectly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            byte[] part1 = Encoding.UTF8.GetBytes("first\n");
            byte[] part2 = Encoding.UTF8.GetBytes("second\n");
            byte[] part3 = Encoding.UTF8.GetBytes("third\n");
            byte[] expected = [.. part1, .. part2, .. part3];

            using GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: (string?)null);
            stream.Write(part1, 0, part1.Length);
            stream.Write(part2, 0, part2.Length);
            stream.Write(part3, 0, part3.Length);
            GitOid oid = await stream.CommitAsync(ct).ConfigureAwait(false);

            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(oid, ct).ConfigureAwait(false))!;
            Assert.Equal(expected, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── guard branches ──────────────────────────────────────────────────

    /// <summary>
    /// Calling <see cref="GitBlobWriteStream.CommitAsync"/> twice
    /// throws <see cref="InvalidOperationException"/> on the second call —
    /// the <c>_committed</c> guard.
    /// </summary>
    [Fact]
    public async Task CommitTwice_ThrowsInvalidOperationException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            using GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: (string?)null);
            stream.Write("x\n"u8.ToArray(), 0, 2);
            await stream.CommitAsync(ct).ConfigureAwait(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stream.CommitAsync(ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Writing to a disposed <see cref="GitBlobWriteStream"/> throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task WriteAfterDispose_ThrowsObjectDisposedException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: (string?)null);
            stream.Dispose();
            Assert.Throws<ObjectDisposedException>(() => stream.Write("x\n"u8.ToArray(), 0, 2));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Writing after <see cref="GitBlobWriteStream.CommitAsync"/>
    /// throws <see cref="InvalidOperationException"/> — the
    /// <c>_committed</c> guard on the write path.
    /// </summary>
    [Fact]
    public async Task WriteAfterCommit_ThrowsInvalidOperationException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            using GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: (string?)null);
            stream.Write("x\n"u8.ToArray(), 0, 2);
            await stream.CommitAsync(ct).ConfigureAwait(false);
            Assert.Throws<InvalidOperationException>(() => stream.Write("y\n"u8.ToArray(), 0, 2));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── filter pipeline via hintPath ────────────────────────────────────

    /// <summary>
    /// With <c>core.autocrlf=true</c> and a hint path, the clean
    /// filter (CRLF → LF) runs during <see cref="GitBlobWriteStream.CommitAsync"/>.
    /// Writing CRLF-terminated content and committing with a hint path
    /// produces a blob whose stored content has LF only. Exercises the
    /// <c>_hintPath is not null → GitFilterList.LoadAsync →
    /// ApplyToBufferAsync</c> branch.
    /// </summary>
    [Fact]
    public async Task WithHintPath_AppliesCleanFilters_CrlfStripped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Enable core.autocrlf=true so the CRLF clean filter converts
            // workdir CRLF → ODB LF on the ToOdb (clean) path.
            await repo.Config.SetStringAsync("core.autocrlf", "true", ct).ConfigureAwait(false);

            byte[] crlfContent = Encoding.UTF8.GetBytes("line1\r\nline2\r\n");
            byte[] lfContent = Encoding.UTF8.GetBytes("line1\nline2\n");

            using GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: "file.txt");
            stream.Write(crlfContent, 0, crlfContent.Length);
            GitOid oid = await stream.CommitAsync(ct).ConfigureAwait(false);

            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(oid, ct).ConfigureAwait(false))!;
            Assert.Equal(lfContent, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Without a hint path, <see cref="GitBlobWriteStream.CommitAsync"/>
    /// skips the filter pipeline entirely (<c>_hintPath is null</c> guard),
    /// so CRLF content is stored verbatim. Documents the no-filter branch
    /// and confirms the hinted-path CRLF stripping is genuinely filter-driven.
    /// </summary>
    [Fact]
    public async Task WithoutHintPath_SkipsFilters_ContentStoredVerbatim()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await repo.Config.SetStringAsync("core.autocrlf", "true", ct).ConfigureAwait(false);

            byte[] crlfContent = Encoding.UTF8.GetBytes("line1\r\nline2\r\n");

            using GitBlobWriteStream stream = repo.BlobCreateWriteStream(hintPath: (string?)null);
            stream.Write(crlfContent, 0, crlfContent.Length);
            GitOid oid = await stream.CommitAsync(ct).ConfigureAwait(false);

            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(oid, ct).ConfigureAwait(false))!;
            // No hint path → no filter → CRLF preserved in the ODB.
            Assert.Equal(crlfContent, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }
}

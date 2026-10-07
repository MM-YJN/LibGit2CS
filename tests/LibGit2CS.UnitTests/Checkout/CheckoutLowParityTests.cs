using System.Buffers.Binary;
using System.Security.Cryptography;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary> Parity tests for the checkout subsystem. </summary>
public sealed class CheckoutLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public CheckoutLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> InitRepoAsync()
        => await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());

    private static async Task<GitOid> WriteBlobAsync(GitRepository repo, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return await repo.ObjectWriteAsync(GitObjectType.Blob, bytes, TestContext.Current.CancellationToken);
    }

    private static GitIndexEntry Entry(string path, GitOid id, GitFileMode mode = GitFileMode.Regular)
        => new(path, id, mode);

    // ── NAME-entry errors use -1 (GIT_ERROR), not GIT_EINVALID ───────

    [Fact]
    public async Task NameEntryWithoutAncestor_ThrowsErrorCode()
    {
        // C (checkout.c:1060-1065): "a NAME entry exists without an ancestor"
        // with error -1 (GIT_ERROR) and GIT_ERROR_INDEX class, not
        // GIT_EINVALID (-21).
        await using GitRepository repo = await InitRepoAsync();

        GitOid blob = await WriteBlobAsync(repo, "content\n");
        byte[] body = BuildIndex(
            version: 2,
            [
                BuildEntry(path: "file.txt", mode: 0x81A4u, oid: blob, flags: 0x1009), // stage 1
                BuildEntry(path: "file.txt", mode: 0x81A4u, oid: blob, flags: 0x2009), // stage 2
                BuildEntry(path: "file.txt", mode: 0x81A4u, oid: blob, flags: 0x3009), // stage 3
            ],
            [("NAME", "\0ours.txt\0theirs.txt\0"u8.ToArray())]); // ancestor = NULL
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "index"), body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex idx = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(idx.HasConflicts, "precondition: crafted index has conflicts");
        Assert.Single(idx.NameEntries);
        Assert.Null(idx.NameEntries[0].Ancestor);

        var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.AllowConflicts };
        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.CheckoutIndexAsync(idx, opts, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Index, ex.Category);
        Assert.Contains("a NAME entry exists without an ancestor", ex.Message);
    }

    [Fact]
    public async Task NameEntryWithoutOursOrTheirs_ThrowsErrorCode()
    {
        // C (checkout.c:1067-1071): "a NAME entry exists without an ours or
        // theirs", error -1 (GIT_ERROR), GIT_ERROR_INDEX.
        await using GitRepository repo = await InitRepoAsync();

        GitOid blob = await WriteBlobAsync(repo, "content\n");
        byte[] body = BuildIndex(
            version: 2,
            [
                BuildEntry(path: "file.txt", mode: 0x81A4u, oid: blob, flags: 0x1009), // stage 1
                BuildEntry(path: "file.txt", mode: 0x81A4u, oid: blob, flags: 0x2009), // stage 2
                BuildEntry(path: "file.txt", mode: 0x81A4u, oid: blob, flags: 0x3009), // stage 3
            ],
            [("NAME", "file.txt\0\0\0"u8.ToArray())]); // ancestor only
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "index"), body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex idx = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.AllowConflicts };
        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.CheckoutIndexAsync(idx, opts, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Index, ex.Category);
        Assert.Contains("a NAME entry exists without an ours or theirs", ex.Message);
    }

    // ── MarkDirectoryFile scans the MAIN index, not just conflicts ───

    [Fact]
    public async Task SingleSidedConflict_IndexEntryUnderPath_SuffixesOurs()
    {
        // C (checkout.c:1177-1231, checkout_conflicts_mark_directoryfile):
        // for a single-sided conflict, ANY LATER index entry under the
        // conflict path (not just other conflicts) marks directoryfile, and
        // the side is written to a suffixed path ("dir.ours"). Comparing only
        // conflict paths would miss "dir/sub" (a plain stage-0 entry).
        await using GitRepository repo = await InitRepoAsync();
        GitOid baseOid = await WriteBlobAsync(repo, "base\n");
        GitOid ourOid = await WriteBlobAsync(repo, "ours\n");
        GitOid subOid = await WriteBlobAsync(repo, "sub\n");

        using var idx = GitIndex.New(repo.ObjectFormat);
        // Single-sided modify/delete conflict at "dir" (ours present).
        idx.ConflictAdd(Entry("dir", baseOid), Entry("dir", ourOid), null);
        // A plain (stage-0) index entry under the conflict path.
        idx.Add(Entry("dir/sub", subOid));

        var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.AllowConflicts };
        await repo.CheckoutIndexAsync(idx, opts, TestContext.Current.CancellationToken);

        // The conflict side is written to the suffixed path "dir~ours"
        // (checkout_path_suffixed, checkout.c:1957-1985 - "~" + label).
        string oursPath = Path.Combine(repo.Workdir!, "dir~ours");
        Assert.True(File.Exists(oursPath), "expected suffixed conflict output dir~ours");
        Assert.Equal("ours\n", await File.ReadAllTextAsync(oursPath, TestContext.Current.CancellationToken));

        // ...and the plain entry is still checked out under dir/.
        Assert.True(File.Exists(Path.Combine(repo.Workdir!, "dir", "sub")), "expected dir/sub checked out");
    }

    [Fact]
    public async Task SingleSidedConflict_NoEntryUnderPath_NoSuffix()
    {
        // Regression guard for without an index entry under the conflict
        // path, the modify/delete side is written to the plain path.
        await using GitRepository repo = await InitRepoAsync();
        GitOid baseOid = await WriteBlobAsync(repo, "base\n");
        GitOid ourOid = await WriteBlobAsync(repo, "ours\n");

        using var idx = GitIndex.New(repo.ObjectFormat);
        idx.ConflictAdd(Entry("dir", baseOid), Entry("dir", ourOid), null);

        var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.AllowConflicts };
        await repo.CheckoutIndexAsync(idx, opts, TestContext.Current.CancellationToken);

        string oursPath = Path.Combine(repo.Workdir!, "dir");
        Assert.True(File.Exists(oursPath), "expected conflict output at plain path dir");
        Assert.Equal("ours\n", await File.ReadAllTextAsync(oursPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(repo.Workdir!, "dir~ours")), "no suffix without a directory entry");
    }

    // ── byte-crafting helpers (same layout as IndexLowParityTests) ────────

    private static byte[] BuildIndex(int version, List<byte[]> entries, List<(string Sig, byte[] Data)> extensions)
    {
        using var ms = new MemoryStream();
        WriteBE32(ms, 0x44495243u); // "DIRC"
        WriteBE32(ms, (uint)version);
        WriteBE32(ms, (uint)entries.Count);
        foreach (byte[] entry in entries)
        {
            ms.Write(entry);
        }

        foreach ((string sig, byte[] data) in extensions)
        {
            ms.Write(System.Text.Encoding.ASCII.GetBytes(sig));
            WriteBE32(ms, (uint)data.Length);
            ms.Write(data);
        }

        byte[] body = ms.ToArray();
        byte[] checksum = SHA1.HashData(body);
        return [.. body, .. checksum];
    }

    private static byte[] BuildEntry(string path, uint mode, GitOid oid, ushort flags)
    {
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
        int len = 62 + pathBytes.Length;
        int total = ((len + 8) / 8) * 8;
        byte[] entry = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(24, 4), mode);
        oid.RawBytes.CopyTo(entry.AsSpan(40));
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(60, 2), flags);
        pathBytes.CopyTo(entry, 62);
        return entry;
    }

    private static void WriteBE32(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }
}

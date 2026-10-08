using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Index;

/// <summary>
/// Regression tests for the index parity behaviors (extension write order,
/// REUC zero-mode bytes, core.ignorecase default) in
/// libgit2 1.9.4. Expectations C-verified
/// against libgit2 1.9.4 (index.c:3149-3153, 3255-3265).
/// </summary>
public sealed class IndexParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public IndexParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, new GitContext()).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _repo.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string IndexFilePath => Path.Combine(_tempDir, ".git", "index");

    /// <summary>
    /// Finds the byte offset of the first occurrence of an extension signature.
    /// </summary>
    private static int FindExtension(byte[] data, string signature)
    {
        byte[] sig = System.Text.Encoding.ASCII.GetBytes(signature);
        for (int i = 0; i + sig.Length <= data.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sig.Length; j++)
            {
                if (data[i + j] != sig[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task Write_ReucAndNameExtensions_NameBeforeReuc()
    {
        // C writes TREE → NAME → REUC (index.c:3255-3265).
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("duel.txt", (uint)GitFileMode.Regular, oid, 0, default, 0, default);
        index.NameAdd("anc.txt", "our.txt", "their.txt");
        await index.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(IndexFilePath, TestContext.Current.CancellationToken);
        int reucOffset = FindExtension(data, "REUC");
        int nameOffset = FindExtension(data, "NAME");
        Assert.True(reucOffset >= 0, "REUC extension missing");
        Assert.True(nameOffset >= 0, "NAME extension missing");
        Assert.True(nameOffset < reucOffset, $"expected NAME before REUC, got NAME@{nameOffset} REUC@{reucOffset}");
    }

    [Fact]
    public async Task Write_ReucZeroMode_IsZeroNulBytes()
    {
        // C emits "%o" + NUL for each REUC mode — for mode 0 that is the
        // two bytes 0x30 0x00 ("0\0"), not a bare NUL (index.c:3149-3153).
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("duel.txt", (uint)GitFileMode.Regular, oid, 0, default, 0, default);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(IndexFilePath, TestContext.Current.CancellationToken);
        int reucOffset = FindExtension(data, "REUC");
        Assert.True(reucOffset >= 0, "REUC extension missing");

        // The path "duel.txt\0" is followed by "100644\0" then "0\0" twice.
        byte[] expected = "duel.txt\0"u8.ToArray()
            .Concat("100644\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .ToArray();
        int dataStart = reucOffset + 8;
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], data[dataStart + i]);
        }
    }

    [Fact]
    public async Task ReucZeroMode_RoundTrips()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("duel.txt", (uint)GitFileMode.Regular, oid, 0, default, 0, default);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        // Re-open from disk with a fresh repository instance.
        await using GitRepository fresh = await GitRepository.OpenAsync(_tempDir, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex reopened = await fresh.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitIndexReucEntry? reuc = reopened.ReucByPath("duel.txt");
        Assert.NotNull(reuc);
        Assert.Equal((uint)GitFileMode.Regular, reuc!.Modes[0]);
        Assert.Equal(0u, reuc.Modes[1]);
        Assert.Equal(0u, reuc.Modes[2]);
    }

    [Fact]
    public async Task IgnoreCase_AfterInit_FollowsFilesystem()
    {
        // GIT_IGNORECASE_DEFAULT = GIT_CONFIGMAP_FALSE on every platform
        // (repository.h:96) — no core.ignorecase in the config. BUT the init
        // probe (is_filesystem_case_insensitive, repository.c:2140-2150)
        // writes core.ignorecase=true on case-insensitive filesystems (APFS
        // on macOS or NTFS on Windows), so the effective default follows the filesystem.
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(File.Exists(Path.Combine(_repo.Path, "CoNfIg")), index.IgnoreCase);
    }

    [Fact]
    public async Task IgnoreCase_ConfigTrue_Enables()
    {
        // core.ignorecase=true in the repo config makes the index
        // case-insensitive (git_index_set_caps, index.c:602-603).
        await _repo.Config.SetBoolAsync("core.ignorecase", true, TestContext.Current.CancellationToken);
        await using GitRepository fresh = await GitRepository.OpenAsync(_tempDir, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex index = await fresh.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(index.IgnoreCase);
    }

    // ── git_index_checksum ──

    [Fact]
    public void Checksum_FreshInMemoryIndex_IsZero()
    {
        using var index = GitIndex.New(GitHashAlgorithmKind.Sha1);

        // C's freshly-allocated git_index has a zero checksum (index.c:630-634).
        Assert.True(index.Checksum.IsZero);
        Assert.Equal(GitOid.Empty, index.Checksum);
    }

    [Fact]
    public async Task Checksum_AfterWrite_MatchesComputedHash()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", oid, GitFileMode.Regular));
        await index.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(IndexFilePath, TestContext.Current.CancellationToken);
        var expected = GitOid.FromRaw(SHA1.HashData(data.AsSpan(0, data.Length - 20)), GitHashAlgorithmKind.Sha1);
        Assert.Equal(expected, index.Checksum);
    }

    [Fact]
    public async Task Checksum_AfterRead_MatchesFileTrailer()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", oid, GitFileMode.Regular));
        await index.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(IndexFilePath, TestContext.Current.CancellationToken);
        using GitIndex reopened = await GitIndex.OpenAsync(IndexFilePath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(GitOid.FromRaw(data.AsSpan(data.Length - 20), GitHashAlgorithmKind.Sha1), reopened.Checksum);
    }
}

using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Index;

/// <summary>
/// End-to-end tests for the index parity behaviors
/// (extension write order, REUC zero-mode bytes, and the core.ignorecase
/// default) in libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class IndexParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public IndexParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Write_RoundTrip_ReucAndName_MatchesC()
    {
        // + end-to-end: the written index carries NAME before REUC and
        // zero REUC modes as "0\0", and re-opens with both extensions intact
        // (a C-written index is byte-compatible with the managed reader).
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken))
        {
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
            index.ReucAdd("duel.txt", (uint)GitFileMode.Regular, oid, 0, default, 0, default);
            index.NameAdd("anc.txt", "our.txt", "their.txt");
            await index.WriteAsync(TestContext.Current.CancellationToken);
        }

        byte[] data = await File.ReadAllBytesAsync(Path.Combine(path, ".git", "index"), TestContext.Current.CancellationToken);
        int reucOffset = FindExtension(data, "REUC");
        int nameOffset = FindExtension(data, "NAME");
        Assert.True(reucOffset >= 0 && nameOffset >= 0);
        Assert.True(nameOffset < reucOffset, $"expected NAME before REUC, got NAME@{nameOffset} REUC@{reucOffset}");

        byte[] expected = "duel.txt\0"u8.ToArray()
            .Concat("100644\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], data[reucOffset + 8 + i]);
        }

        // Fresh repo instance re-reads the index from disk.
        await using GitRepository reopened = await GitRepository.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        GitIndex fresh = await reopened.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fresh.ReucCount);
        Assert.Equal(1, fresh.NameCount);
        GitIndexReucEntry? reuc = fresh.ReucByPath("duel.txt");
        Assert.NotNull(reuc);
        Assert.Equal(0u, reuc!.Modes[1]);
        Assert.Equal(0u, reuc.Modes[2]);
    }

    [Fact]
    public async Task IgnoreCase_AfterInit_FollowsFilesystem_LikeC()
    {
        // GIT_IGNORECASE_DEFAULT = GIT_CONFIGMAP_FALSE on every platform.
        // BUT the init probe (is_filesystem_case_insensitive,
        // repository.c:2140-2150) writes core.ignorecase=true on
        // case-insensitive filesystems (APFS on macOS or NTFS on Windows), so the effective
        // default follows the filesystem.
        string path = Path.Combine(_tempDir, "ignorecase-default");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal(File.Exists(Path.Combine(repo.Path, "CoNfIg")), index.IgnoreCase);
    }

    [Fact]
    public async Task Checksum_AfterRead_MatchesFileTrailer()
    {
        // git_index_checksum (index.c:630-634) returns the trailing hash
        // stored in the index file after a read.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.CommitFileAsync("file.txt", "hello\n"u8.ToArray(), "commit\n", updateRef: "refs/heads/main", ct: ct);

        GitIndex idx = await bld.Repo.GetIndexAsync(ct);
        byte[] fileBytes = await File.ReadAllBytesAsync(Path.Combine(bld.Path, ".git", "index"), ct);
        Assert.Equal(GitOid.FromRaw(fileBytes.AsSpan(fileBytes.Length - 20), GitHashAlgorithmKind.Sha1), idx.Checksum);
    }

    [Fact]
    public async Task Checksum_AfterWrite_MatchesComputedHash()
    {
        // git_index_checksum (index.c:630-634) returns the SHA-1 of the
        // serialized index body after a write; re-opening the file yields
        // the same value.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitIndex idx = await bld.Repo.GetIndexAsync(ct);
        idx.Clear();
        GitOid blob = await bld.Repo.ObjectWriteAsync(GitObjectType.Blob, "staged\n"u8.ToArray(), ct);
        idx.Add(new GitIndexEntry("staged.txt", blob, GitFileMode.Regular));
        await idx.WriteAsync(ct);

        byte[] fileBytes = await File.ReadAllBytesAsync(Path.Combine(bld.Path, ".git", "index"), ct);
        var expected = GitOid.FromRaw(SHA1.HashData(fileBytes.AsSpan(0, fileBytes.Length - 20)), GitHashAlgorithmKind.Sha1);
        Assert.Equal(expected, idx.Checksum);

        using GitIndex reopened = await GitIndex.OpenAsync(Path.Combine(bld.Path, ".git", "index"), bld.Repo.ObjectFormat, ct);
        Assert.Equal(expected, reopened.Checksum);
    }
}

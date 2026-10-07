using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

/// <summary> The packed-refs store is byte-keyed on the raw refname bytes (C's sortedcache stores inline <c>char name[]</c> bytes
/// compared with <c>strcmp</c>, refdb_fs.c:46-51, 107-110) — a non-UTF-8 refname round-trips byte-exact through parse, lookup, enumeration (U+FFFD display
/// decode at egress), delete and rewrite. </summary>
public sealed class PackedRefsByteParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public PackedRefsByteParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackedRefsByte_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string GitDir => Path.Combine(_tempDir, ".git");

    private string PackedRefsPath => Path.Combine(GitDir, "packed-refs");

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommit(string refName)
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "base\n",
            UpdateRef = refName,
        });
    }

    // ── 1: non-UTF-8 refname round-trips byte-exact ────────────────

    [Fact]
    public async Task NonUtf8Refname_EnumerateDeleteWrite_ByteExact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid oid = await WriteCommit("refs/heads/ascii");

        // A packed-refs file with a raw 0xE9 byte in a refname.
        byte[] refNameBytes = [.. "refs/heads/caf"u8.ToArray(), 0xE9];
        await File.WriteAllBytesAsync(
            PackedRefsPath,
            [.. "# pack-refs with: peeled fully-peeled sorted \n"u8.ToArray(), .. Encoding.UTF8.GetBytes(oid.ToString()), (byte)' ', .. refNameBytes, (byte)'\n'],
            ct);

        // Enumeration surfaces the display decode (U+FFFD) at egress.
        var names = new List<string>();
        await foreach (string name in _repo.Refs.ListNamesAsync(cancellationToken: ct))
        {
            names.Add(name);
        }

        Assert.Contains("refs/heads/caf\uFFFD", names);

        // The string tier (U+FFFD) cannot reach the raw-byte refname — the byte-keyed store matches C's strcmp over the raw bytes; the parity surface for
        // non-UTF-8 refnames is the raw file.
        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/caf\uFFFD", ct));

        // Delete the ASCII sibling: the rewrite preserves the raw 0xE9
        // refname bytes verbatim (C's packed_write emits the raw name bytes,
        // refdb_fs.c:1308-1335).
        await _repo.Refs.DeleteAsync("refs/heads/ascii", ct);
        byte[] onDisk = await File.ReadAllBytesAsync(PackedRefsPath, ct);
        Assert.True(ContainsSubsequence(onDisk, refNameBytes), "non-UTF-8 refname must survive the rewrite byte-exact");
        Assert.False(ContainsSubsequence(onDisk, "refs/heads/ascii"u8.ToArray()), "deleted ref must be gone");
    }

    // ── 2: byte-ordinal enumeration order (C's strcmp) ─────────────

    [Fact]
    public async Task Enumeration_ByteOrdinalOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid oid = await WriteCommit("refs/heads/ascii");

        // Mixed ASCII/non-ASCII names: byte-ordinal (strcmp) order puts the
        // raw 0xE9 byte (0xE9 > 'z' = 0x7A) after all ASCII names.
        byte[] highName = [.. "refs/heads/caf"u8.ToArray(), 0xE9];
        await File.WriteAllBytesAsync(
            PackedRefsPath,
            [.. "# pack-refs with: peeled fully-peeled sorted \n"u8.ToArray(),
             .. Encoding.UTF8.GetBytes(oid.ToString()), (byte)' ', .. "refs/heads/ascii\n"u8.ToArray(),
             .. Encoding.UTF8.GetBytes(oid.ToString()), (byte)' ', .. highName, (byte)'\n'],
            ct);

        var names = new List<string>();
        await foreach (string name in _repo.Refs.ListNamesAsync(cancellationToken: ct))
        {
            names.Add(name);
        }

        Assert.Equal(["refs/heads/ascii", "refs/heads/caf\uFFFD"], names);
    }

    // ── 3: prefix-collision scan is byte-domain ────────────────────

    [Fact]
    public async Task PrefixCollision_ValidUtf8_ByteDomain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid oid = await WriteCommit("refs/heads/ascii");

        // A packed name with a valid-UTF-8 multi-byte char: the byte prefix
        // scan (C's ref_is_available strcmps the raw bytes, refdb_fs.c:1083-
        // 1144) must collide with a new ref whose name is a byte prefix
        // followed by '/'.
        await File.WriteAllBytesAsync(
            PackedRefsPath,
            [.. "# pack-refs with: peeled fully-peeled sorted \n"u8.ToArray(), .. Encoding.UTF8.GetBytes(oid.ToString()), (byte)' ', .. "refs/heads/café/x\n"u8.ToArray()],
            ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceCreateAsync("refs/heads/café", oid, force: false, cancellationToken: ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("collides", ex.Message);
    }

    [Fact]
    public async Task PrefixCollision_NonUtf8_NoFalsePositive()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid oid = await WriteCommit("refs/heads/ascii");

        // A raw 0xE9 packed name: a caller's U+FFFD string encodes to EF BF BD, which does NOT byte-prefix the raw E9 — no false collision.
        byte[] refNameBytes = [.. "refs/heads/caf"u8.ToArray(), 0xE9];
        await File.WriteAllBytesAsync(
            PackedRefsPath,
            [.. "# pack-refs with: peeled fully-peeled sorted \n"u8.ToArray(), .. Encoding.UTF8.GetBytes(oid.ToString()), (byte)' ', .. refNameBytes, (byte)'\n'],
            ct);

        await _repo.ReferenceCreateAsync("refs/heads/caf\uFFFD", oid, force: false, cancellationToken: ct);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}

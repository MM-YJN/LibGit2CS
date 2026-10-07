using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

/// <summary>
/// Tests for <see cref="GitRepository.PackRefsAsync"/> — the managed port of
/// <c>git_refdb_compress</c> (refdb.c:92-100 → refdb_fs.c:1897-1910):
/// loose direct refs are merged into packed-refs, symbolic refs are skipped,
/// annotated tags get a <c>^&lt;peel&gt;</c> line, stale packed entries are
/// overwritten, and — per <c>packed_write</c>'s <c>packed_remove_loose</c>
/// (refdb_fs.c:1465-1468 in libgit2 1.9.4)
/// — the packed loose files are pruned (symbolic refs are never packed and
/// stay loose).
/// </summary>
public sealed class PackRefsParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public PackRefsParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackRefsParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task PackRefs_MergesLooseRefs_IntoPackedRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid alpha = await WriteCommit("refs/heads/alpha");
        GitOid beta = await WriteCommit("refs/heads/beta");
        await _repo.ReferenceCreateSymbolicAsync("refs/heads/sym", "refs/heads/alpha", cancellationToken: ct);

        using GitObject? commit = await _repo.ObjectLookupAsync(alpha, ct).ConfigureAwait(false);
        GitOid tagOid = await _repo.TagCreateAsync("v1.0", commit!, TestSig(), "annotated tag\n", cancellationToken: ct);
        using GitTag tag = (await _repo.ObjectLookupAsync<GitTag>(tagOid, ct).ConfigureAwait(false))!;

        var stale = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);
        await File.WriteAllTextAsync(
            PackedRefsPath,
            "# pack-refs with: peeled fully-peeled sorted \n" + stale + " refs/heads/alpha\n",
            ct);

        await _repo.PackRefsAsync(ct);

        string packed = await File.ReadAllTextAsync(PackedRefsPath, ct);
        Assert.StartsWith("# pack-refs with: peeled fully-peeled sorted \n", packed, StringComparison.Ordinal);
        Assert.Contains(alpha + " refs/heads/alpha\n", packed, StringComparison.Ordinal);
        Assert.Contains(beta + " refs/heads/beta\n", packed, StringComparison.Ordinal);
        Assert.Contains(tagOid + " refs/tags/v1.0\n^" + tag.Target + "\n", packed, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/heads/sym", packed, StringComparison.Ordinal);
        Assert.DoesNotContain(stale.ToString(), packed, StringComparison.Ordinal);

        // packed_write ends with packed_remove_loose (refdb_fs.c:1465-
        // 1468) — the packed loose files are pruned (the old comment claiming
        // "C's compress has no unlink" was wrong). Symbolic refs are never
        // packed and stay loose.
        Assert.False(File.Exists(Path.Combine(GitDir, "refs", "heads", "alpha")));
        Assert.False(File.Exists(Path.Combine(GitDir, "refs", "heads", "beta")));
        Assert.True(File.Exists(Path.Combine(GitDir, "refs", "heads", "sym")));
        Assert.False(File.Exists(Path.Combine(GitDir, "refs", "tags", "v1.0")));
    }
}

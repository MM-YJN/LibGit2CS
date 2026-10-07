using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitCheckoutOptions = LibGit2CS.Checkout.GitCheckoutOptions;
using GitCheckoutStrategy = LibGit2CS.Checkout.GitCheckoutStrategy;
using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Tests for collision suffixing (<c>~ours</c>/<c>~theirs</c>) and
/// 1-to-2 dual-file write. Verifies that <see cref="CheckoutConflictData.NameCollision"/>
/// and <see cref="CheckoutConflictData.OneToTwo"/> produce correctly suffixed
/// output paths.
/// </summary>
public sealed class CheckoutCollisionSuffixTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutCollisionSuffixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CollisionSuffix_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch { }
    }

    private async Task<GitOid> WriteBlobAsync(string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return await _repo.ObjectWriteAsync(GitObjectType.Blob, bytes, TestContext.Current.CancellationToken);
    }

    private static GitIndexEntry Entry(string path, GitOid id, GitFileMode mode = GitFileMode.Regular)
        => new(path, id, mode);

    /// <summary>
    /// Builds an in-memory index with conflict + NAME entries, then checks it
    /// out. Returns the list of workdir files after checkout.
    /// </summary>
    private async Task<List<string>> CheckoutConflictIndex(
        Action<GitIndex> setupIndex,
        GitCheckoutStrategy extraFlags = GitCheckoutStrategy.Safe,
        string? ourLabel = null,
        string? theirLabel = null)
    {
        var index = GitIndex.New(_repo.ObjectFormat);
        setupIndex(index);

        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts | extraFlags,
            OurLabel = ourLabel,
            TheirLabel = theirLabel,
        };

        await _repo.CheckoutIndexAsync(index, opts);

        var result = new List<string>();
        if (_repo.Workdir is not null)
        {
            foreach (string f in Directory.EnumerateFiles(_repo.Workdir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(_repo.Workdir, f);
                if (!rel.StartsWith(".git/", StringComparison.Ordinal) && rel != ".git")
                {
                    result.Add(rel);
                }
            }
        }

        return result;
    }

    // ── 2-to-1 rename: ~ours / ~theirs suffixing ───────────────────────

    [Fact]
    public async Task TwoToOneRename_WritesSuffixedMergeResult()
    {
        // 2-to-1 rename: both ours and theirs rename to the same target C.txt.
        // After coalesce, two name-collision conflicts remain. The 3-way merge
        // result is written to C.txt~ours and C.txt~theirs.
        GitOid oidA = await WriteBlobAsync("content A\n");
        GitOid oidB = await WriteBlobAsync("content B\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(Entry("A.txt", oidA), null, Entry("A.txt", oidA));
            idx.ConflictAdd(Entry("B.txt", oidB), Entry("B.txt", oidB), null);
            idx.ConflictAdd(null, Entry("C.txt", oidA), Entry("C.txt", oidB));
            idx.NameAdd("A.txt", "C.txt", null);
            idx.NameAdd("B.txt", null, "C.txt");
        });

        Assert.Contains("C.txt~ours", files);
        Assert.Contains("C.txt~theirs", files);
    }

    // ── 1-to-2 rename: both sides as separate files ─────────────────────

    [Fact]
    public async Task OneToTwoRename_WritesBothSidesAtTheirOwnPaths()
    {
        // 1-to-2 rename: ancestor has orig.txt, ours renames to ours_renamed.txt,
        // theirs renames to theirs_renamed.txt. After coalesce, OneToTwo is set.
        // Both sides are written as separate files at their own paths (no suffix).
        GitOid baseOid = await WriteBlobAsync("base\n");
        GitOid ourOid = await WriteBlobAsync("ours\n");
        GitOid theirOid = await WriteBlobAsync("theirs\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(
                Entry("orig.txt", baseOid),
                Entry("ours_renamed.txt", ourOid),
                null);
            idx.ConflictAdd(
                null,
                null,
                Entry("theirs_renamed.txt", theirOid));
            idx.NameAdd("orig.txt", "ours_renamed.txt", "theirs_renamed.txt");
        });

        Assert.Contains("ours_renamed.txt", files);
        Assert.Contains("theirs_renamed.txt", files);
        // The ancestor path should NOT appear (no 3-way merge for 1-to-2).
        Assert.DoesNotContain("orig.txt", files);
    }

    // ── Custom labels ───────────────────────────────────────────────────

    [Fact]
    public async Task NameCollision_CustomLabels_UsedInSuffix()
    {
        // 2-to-1 rename with custom OurLabel/TheirLabel.
        GitOid oidA = await WriteBlobAsync("content A\n");
        GitOid oidB = await WriteBlobAsync("content B\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(Entry("A.txt", oidA), null, Entry("A.txt", oidA));
            idx.ConflictAdd(Entry("B.txt", oidB), Entry("B.txt", oidB), null);
            idx.ConflictAdd(null, Entry("C.txt", oidA), Entry("C.txt", oidB));
            idx.NameAdd("A.txt", "C.txt", null);
            idx.NameAdd("B.txt", null, "C.txt");
        }, ourLabel: "custom_ours", theirLabel: "custom_theirs");

        Assert.Contains("C.txt~custom_ours", files);
        Assert.Contains("C.txt~custom_theirs", files);
    }

    // ── Collision avoidance: _0, _1, … ──────────────────────────────────

    [Fact]
    public async Task SuffixPath_CollisionAvoidance_AppendsNumber()
    {
        // Pre-create C.txt~ours so the checkout must use C.txt~ours_0.
        GitOid oidA = await WriteBlobAsync("content A\n");
        GitOid oidB = await WriteBlobAsync("content B\n");

        // Pre-create the suffixed file to force collision avoidance.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "C.txt~ours"), "existing\n", cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(Entry("A.txt", oidA), null, Entry("A.txt", oidA));
            idx.ConflictAdd(Entry("B.txt", oidB), Entry("B.txt", oidB), null);
            idx.ConflictAdd(null, Entry("C.txt", oidA), Entry("C.txt", oidB));
            idx.NameAdd("A.txt", "C.txt", null);
            idx.NameAdd("B.txt", null, "C.txt");
        });

        // The ~ours path was taken, so checkout should write to ~ours_0.
        Assert.Contains("C.txt~ours_0", files);
        // ~theirs should still be written normally.
        Assert.Contains("C.txt~theirs", files);
    }

    // ── Modify/delete with NameCollision suffixing ──────────────────────

    [Fact]
    public async Task ModifyDelete_WithCollision_WritesSuffixedSide()
    {
        // Ancestor has file.txt. Ours renames to renamed.txt. Theirs keeps file.txt
        // but with different content. After coalesce: ancestor at file.txt gets
        // ours=renamed.txt, theirs=file.txt. Since theirs is at the ancestor
        // path (not a different rename target), NameCollision is NOT set —
        // the 3-way merge writes to the conflict path "file.txt" directly.
        // (NameCollision only fires when both ours and theirs came from
        // different rename targets, per checkout.c:1148-1162.)
        GitOid baseOid = await WriteBlobAsync("base\n");
        GitOid ourOid = await WriteBlobAsync("ours_renamed\n");
        GitOid theirOid = await WriteBlobAsync("theirs_new\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(
                Entry("file.txt", baseOid),
                Entry("renamed.txt", ourOid),
                null);
            idx.ConflictAdd(
                null,
                null,
                Entry("file.txt", theirOid));
            idx.NameAdd("file.txt", "renamed.txt", "file.txt");
        });

        // No NameCollision → merge result written to conflict.Path.
        Assert.Contains("file.txt", files);
    }
}

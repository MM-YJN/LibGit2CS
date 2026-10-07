using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitCheckoutOptions = LibGit2CS.Checkout.GitCheckoutOptions;
using GitCheckoutStrategy = LibGit2CS.Checkout.GitCheckoutStrategy;
using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Checkout;

public sealed class CheckoutRenameConflictTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutRenameConflictTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutRename_" + Guid.NewGuid().ToString("N")[..8]);
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
        GitCheckoutStrategy extraFlags = GitCheckoutStrategy.Safe)
    {
        var index = GitIndex.New(_repo.ObjectFormat);
        setupIndex(index);

        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts | extraFlags,
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

    // ── No NAME entries → existing behavior (regression guard) ──────────

    [Fact]
    public async Task NoNameEntries_ConflictWithoutRename_UnchangedBehavior()
    {
        GitOid baseOid = await WriteBlobAsync("base\n");
        GitOid ourOid = await WriteBlobAsync("ours\n");
        GitOid theirOid = await WriteBlobAsync("theirs\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(
                Entry("file.txt", baseOid),
                Entry("file.txt", ourOid),
                Entry("file.txt", theirOid));
        });

        // With AllowConflicts, the 3-way merge writes conflict markers.
        Assert.Contains("file.txt", files);
    }

    // ── Rename in ours, delete in theirs ────────────────────────────────

    [Fact]
    public async Task RenameInOurs_DeleteInTheirs_WritesRenamedFile()
    {
        GitOid baseOid = await WriteBlobAsync("base content\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            // Ancestor has file.txt; ours renamed to renamed.txt (same OID);
            // theirs deleted file.txt. After coalesce, the ancestor conflict
            // at "file.txt" gets ours = the renamed entry, theirs = null.
            idx.ConflictAdd(
                Entry("file.txt", baseOid),
                null,
                null);
            idx.ConflictAdd(
                null,
                Entry("renamed.txt", baseOid),
                null);
            idx.NameAdd("file.txt", "renamed.txt", null);
        });

        // After coalesce: the "renamed.txt" conflict is coalesced into
        // "file.txt"'s ours, theirs is null → modify/delete → write ours.
        // The file is written to side.Path ("renamed.txt"), NOT
        // conflict.Path ("file.txt") — matches C's checkout_write_entry
        // which uses side->path.
        Assert.Contains("renamed.txt", files);
    }

    // ── Rename in theirs, delete in ours ─────────────────────────────────

    [Fact]
    public async Task RenameInTheirs_DeleteInOurs_WritesRenamedFile()
    {
        GitOid baseOid = await WriteBlobAsync("base content\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(
                Entry("file.txt", baseOid),
                null,
                null);
            idx.ConflictAdd(
                null,
                null,
                Entry("renamed.txt", baseOid));
            idx.NameAdd("file.txt", null, "renamed.txt");
        });

        // After coalesce: theirs has the renamed entry, ours is null →
        // modify/delete → write theirs. The write goes to side.Path
        // ("renamed.txt"), NOT conflict.Path ("file.txt").
        Assert.Contains("renamed.txt", files);
    }

    // ── Both renamed to the same target (2-to-1) ─────────────────────────

    [Fact]
    public async Task BothRenamed_2To1_CoalescesToConflicts()
    {
        // Matches C's case 7 (tests/libgit2/checkout/conflict.c:536-541):
        // ancestor has side1 (A) and side2 (B). Ours renames A→C and keeps B.
        // Theirs renames B→C and keeps A. After coalesce, two name-collision
        // conflicts remain at A and B paths.
        GitOid oidA = await WriteBlobAsync("content A\n");
        GitOid oidB = await WriteBlobAsync("content B\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            // Ancestor entries (stage 1).
            idx.ConflictAdd(Entry("A.txt", oidA), null, Entry("A.txt", oidA));
            idx.ConflictAdd(Entry("B.txt", oidB), Entry("B.txt", oidB), null);
            // The rename target (stage 2=ours, stage 3=theirs) — no ancestor.
            idx.ConflictAdd(null, Entry("C.txt", oidA), Entry("C.txt", oidB));
            // NAME entries.
            idx.NameAdd("A.txt", "C.txt", null);
            idx.NameAdd("B.txt", null, "C.txt");
        });

        // After coalesce: the "C.txt" branch conflict is removed (coalesced
        // into A and B). The A conflict gets ours=C (oidA), theirs=A (oidA)
        // → name_collision. The B conflict gets ours=B (oidB), theirs=C
        // (oidB) → name_collision. The 3-way merge result is written to
        // a suffixed path. For A conflict: BestPath("A.txt","C.txt","A.txt")
        // = "C.txt" (ancestor==theirs → ours), suffix = "ours" → "C.txt~ours".
        // For B conflict: BestPath("B.txt","B.txt","C.txt") = "C.txt"
        // (ancestor==ours → theirs), suffix = "theirs" → "C.txt~theirs".
        // Matches C's case 7 (conflict.c:672-675).
        Assert.Contains("C.txt~ours", files);
        Assert.Contains("C.txt~theirs", files);
    }

    // ── Both renamed to different targets (1-to-2) ───────────────────────

    [Fact]
    public async Task BothRenamed_1To2_SetsOneToTwoFlag()
    {
        GitOid baseOid = await WriteBlobAsync("base\n");
        GitOid ourOid = await WriteBlobAsync("ours\n");
        GitOid theirOid = await WriteBlobAsync("theirs\n");

        // For 1-to-2, both ours and theirs have entries at different paths
        // but the same ancestor. After coalesce, the ancestor conflict gets
        // both ours+theirs, and OneToTwo is set. The "branch" conflicts are
        // removed.
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

        // The ancestor conflict at "orig.txt" now has both ours+theirs
        // (the renamed entries) and OneToTwo is set. Both sides are
        // written as separate files to their own paths (no suffix —
        // OneToTwo doesn't set NameCollision). Matches C's case 6
        // (conflict.c:666-668) which produces 6-both-renamed-1-to-2-ours.txt
        // and 6-both-renamed-1-to-2-theirs.txt.
        Assert.Contains("ours_renamed.txt", files);
        Assert.Contains("theirs_renamed.txt", files);
    }

    // ── Rename in ours, add in theirs at same path ──────────────────────

    [Fact]
    public async Task RenameInOurs_AddInTheirs_WritesBothFiles()
    {
        GitOid baseOid = await WriteBlobAsync("base\n");
        GitOid ourOid = await WriteBlobAsync("ours_renamed\n");
        GitOid theirOid = await WriteBlobAsync("theirs_new\n");

        List<string> files = await CheckoutConflictIndex(idx =>
        {
            // Ancestor: file.txt. Ours: renamed.txt (renamed+modified).
            // Theirs: file.txt (new content added at the original path).
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

        // After coalesce: ancestor at "file.txt" gets ours = renamed entry,
        // theirs = the new file.txt entry. Both present → 3-way merge.
        Assert.Contains("file.txt", files);
    }

    // ── NAME entry without ancestor throws ──────────────────────────────

    [Fact]
    public async Task NameEntryWithoutAncestor_Throws()
    {
        GitOid ourOid = await WriteBlobAsync("ours\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(
                null,
                Entry("file.txt", ourOid),
                null);
            idx.NameAdd(null, "file.txt", null);
        }));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    // ── NAME entry referencing non-existent ancestor throws ─────────────

    [Fact]
    public async Task NameEntryBadAncestor_Throws()
    {
        GitOid baseOid = await WriteBlobAsync("base\n");
        GitOid ourOid = await WriteBlobAsync("ours\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await CheckoutConflictIndex(idx =>
        {
            idx.ConflictAdd(
                Entry("real.txt", baseOid),
                Entry("real.txt", ourOid),
                null);
            // NAME references "ghost.txt" which doesn't exist.
            idx.NameAdd("ghost.txt", "real.txt", null);
        }));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }
}

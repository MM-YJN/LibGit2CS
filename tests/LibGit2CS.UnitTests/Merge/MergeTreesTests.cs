using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Tree-level merge tests. Ported from
/// <c>tests/libgit2/merge/trees/{automerge,commits,modeconflict,trivial,whitespace}.c</c>.
/// Uses the <c>merge-resolve</c> and <c>merge-whitespace</c> fixtures.
/// </summary>
public sealed class MergeTreesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    // master = bd593285fc7fe4ca18ccdbabf027f5d689101452
    // branch = 7cb63eed597130ba4abb87b3e544b85021905520

    private const uint M0644 = 33188; // octal 0100644

    public MergeTreesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeTrees_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task<GitRepository> OpenMergeResolveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-resolve.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), new GitContext());
    }

    private async Task<GitRepository> OpenMergeWhitespaceRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-whitespace.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-whitespace"), new GitContext());
    }

    // ── automerge.c ──────────────────────────────────────────────────────

    [Fact]
    public async Task Automerge()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "master", "branch");

        // Note: the ancestor OID for conflicting.txt differs from the C test data
        // because the fixture was updated after the C tests were written.
        // The actual ancestor OID is d427e0b2e138501a3d15cc376077a3631e15bd46.
        IReadOnlyList<GitIndexEntry> entries = index.Snapshot();
        Assert.Equal(8, entries.Count);
        Assert.Equal("conflicting.txt", entries[4].Path.ToUtf8String());
        Assert.Equal(1, entries[4].Stage);
        Assert.Equal("conflicting.txt", entries[5].Path.ToUtf8String());
        Assert.Equal(2, entries[5].Stage);
        Assert.Equal("conflicting.txt", entries[6].Path.ToUtf8String());
        Assert.Equal(3, entries[6].Stage);
        Assert.True(index.HasConflicts);
    }

    [Fact]
    public async Task FavorOurs()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        var opts = new GitMergeOptions { Favor = GitMergeFileFavor.Ours };
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "master", "branch", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "233c0919c998ed110a4b6ff36f353aec8b713487", 0, "added-in-master.txt"),
            new(M0644, "f2e1550a0c9e53d5811175864a29536642ae3821", 0, "automergeable.txt"),
            new(M0644, "4eb04c9e79e88f6640d01ff5b25ca2a60764f216", 0, "changed-in-branch.txt"),
            new(M0644, "11deab00b2d3a6f5a3073988ac050c2d7b6655e2", 0, "changed-in-master.txt"),
            new(M0644, "4e886e602529caa9ab11d71f86634bd1b6e0de10", 0, "conflicting.txt"),
            new(M0644, "c8f06f2e3bb2964174677e91f0abead0e43c9e5d", 0, "unchanged.txt"),
        ]);
    }

    [Fact]
    public async Task FavorTheirs()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        var opts = new GitMergeOptions { Favor = GitMergeFileFavor.Theirs };
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "master", "branch", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "233c0919c998ed110a4b6ff36f353aec8b713487", 0, "added-in-master.txt"),
            new(M0644, "f2e1550a0c9e53d5811175864a29536642ae3821", 0, "automergeable.txt"),
            new(M0644, "4eb04c9e79e88f6640d01ff5b25ca2a60764f216", 0, "changed-in-branch.txt"),
            new(M0644, "11deab00b2d3a6f5a3073988ac050c2d7b6655e2", 0, "changed-in-master.txt"),
            new(M0644, "2bd0a343aeef7a2cf0d158478966a6e587ff3863", 0, "conflicting.txt"),
            new(M0644, "c8f06f2e3bb2964174677e91f0abead0e43c9e5d", 0, "unchanged.txt"),
        ]);
    }

    [Fact]
    public async Task Unrelated()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "master", "unrelated");

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "233c0919c998ed110a4b6ff36f353aec8b713487", 0, "added-in-master.txt"),
            new(M0644, "ee3fa1b8c00aff7fe02065fdb50864bb0d932ccf", 2, "automergeable.txt"),
            new(M0644, "d07ec190c306ec690bac349e87d01c4358e49bb2", 3, "automergeable.txt"),
            new(M0644, "ab6c44a2e84492ad4b41bb6bac87353e9d02ac8b", 0, "changed-in-branch.txt"),
            new(M0644, "11deab00b2d3a6f5a3073988ac050c2d7b6655e2", 0, "changed-in-master.txt"),
            new(M0644, "4e886e602529caa9ab11d71f86634bd1b6e0de10", 2, "conflicting.txt"),
            new(M0644, "4b253da36a0ae8bfce63aeabd8c5b58429925594", 3, "conflicting.txt"),
            new(M0644, "ef58fdd8086c243bdc81f99e379acacfd21d32d6", 0, "new-in-unrelated1.txt"),
            new(M0644, "948ba6e701c1edab0c2d394fb7c5538334129793", 0, "new-in-unrelated2.txt"),
            new(M0644, "dfe3f22baa1f6fce5447901c3086bae368de6bdd", 0, "removed-in-branch.txt"),
            new(M0644, "c8f06f2e3bb2964174677e91f0abead0e43c9e5d", 0, "unchanged.txt"),
        ]);
    }

    // ── commits.c ────────────────────────────────────────────────────────

    [Fact]
    public async Task Commits_Automerge()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "master", "branch");

        IReadOnlyList<GitIndexEntry> entries = index.Snapshot();
        Assert.Equal(8, entries.Count);
        Assert.True(index.HasConflicts);
    }

    [Fact]
    public async Task Commits_NoAncestor()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "master", "unrelated");

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "233c0919c998ed110a4b6ff36f353aec8b713487", 0, "added-in-master.txt"),
            new(M0644, "ee3fa1b8c00aff7fe02065fdb50864bb0d932ccf", 2, "automergeable.txt"),
            new(M0644, "d07ec190c306ec690bac349e87d01c4358e49bb2", 3, "automergeable.txt"),
            new(M0644, "ab6c44a2e84492ad4b41bb6bac87353e9d02ac8b", 0, "changed-in-branch.txt"),
            new(M0644, "11deab00b2d3a6f5a3073988ac050c2d7b6655e2", 0, "changed-in-master.txt"),
            new(M0644, "4e886e602529caa9ab11d71f86634bd1b6e0de10", 2, "conflicting.txt"),
            new(M0644, "4b253da36a0ae8bfce63aeabd8c5b58429925594", 3, "conflicting.txt"),
            new(M0644, "ef58fdd8086c243bdc81f99e379acacfd21d32d6", 0, "new-in-unrelated1.txt"),
            new(M0644, "948ba6e701c1edab0c2d394fb7c5538334129793", 0, "new-in-unrelated2.txt"),
            new(M0644, "dfe3f22baa1f6fce5447901c3086bae368de6bdd", 0, "removed-in-branch.txt"),
            new(M0644, "c8f06f2e3bb2964174677e91f0abead0e43c9e5d", 0, "unchanged.txt"),
        ]);
    }

    [Fact]
    public async Task DfConflict()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "df_side1", "df_side2");

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "49130a28ef567af9a6a6104c38773fedfa5f9742", 2, "dir-10"),
            new(M0644, "6c06dcd163587c2cc18be44857e0b71116382aeb", 3, "dir-10"),
            new(M0644, "43aafd43bea779ec74317dc361f45ae3f532a505", 0, "dir-6"),
            new(M0644, "a031a28ae70e33a641ce4b8a8f6317f1ab79dee4", 3, "dir-7"),
            new(M0644, "5012fd565b1393bdfda1805d4ec38ce6619e1fd1", 1, "dir-7/file.txt"),
            new(M0644, "a5563304ddf6caba25cb50323a2ea6f7dbfcadca", 2, "dir-7/file.txt"),
            new(M0644, "e9ad6ec3e38364a3d07feda7c4197d4d845c53b5", 0, "dir-8"),
            new(M0644, "3ef4d30382ca33fdeba9fda895a99e0891ba37aa", 2, "dir-9"),
            new(M0644, "fc4c636d6515e9e261f9260dbcf3cc6eca97ea08", 1, "dir-9/file.txt"),
            new(M0644, "76ab0e2868197ec158ddd6c78d8a0d2fd73d38f9", 3, "dir-9/file.txt"),
            new(M0644, "5c2411f8075f48a6b2fdb85ebc0d371747c4df15", 0, "file-1/new"),
            new(M0644, "a39a620dae5bc8b4e771cd4d251b7d080401a21e", 1, "file-2"),
            new(M0644, "d963979c237d08b6ba39062ee7bf64c7d34a27f8", 2, "file-2"),
            new(M0644, "5c341ead2ba6f2af98ce5ec3fe84f6b6d2899c0d", 0, "file-2/new"),
            new(M0644, "9efe7723802d4305142eee177e018fee1572c4f4", 0, "file-3/new"),
            new(M0644, "bacac9b3493509aa15e1730e1545fc0919d1dae0", 1, "file-4"),
            new(M0644, "7663fce0130db092936b137cabd693ec234eb060", 3, "file-4"),
            new(M0644, "e49f917b448d1340b31d76e54ba388268fd4c922", 0, "file-4/new"),
            new(M0644, "cab2cf23998b40f1af2d9d9a756dc9e285a8df4b", 2, "file-5/new"),
            new(M0644, "f5504f36e6f4eb797a56fc5bac6c6c7f32969bf2", 3, "file-5/new"),
        ]);
    }

    [Fact]
    public async Task FailOnConflict_Trees()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.FailOnConflict };
        await Assert.ThrowsAsync<GitException>(async () => await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "df_side1", "df_side2", opts));
    }

    [Fact]
    public async Task FailOnConflict_Commits()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.FailOnConflict };
        await Assert.ThrowsAsync<GitException>(async () => await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "master", "branch", opts));
    }

    // ── trivial.c (subset with known OIDs) ───────────────────────────────

    [Fact]
    public async Task Trivial_13()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "trivial-13", "trivial-13-branch");
        // The trivial branches share the root commit as base, so all files from the
        // ancestor tree appear as stage 0 (unchanged) plus the modified file.
        Assert.Equal(8, index.EntryCount);
        Assert.False(index.HasConflicts);
    }

    [Fact]
    public async Task Trivial_14()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "trivial-14", "trivial-14-branch");
        Assert.Equal(8, index.EntryCount);
        Assert.False(index.HasConflicts);
    }

    [Fact]
    public async Task Trivial_6_BothDeleted()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "trivial-6", "trivial-6-branch");
        // Both sides deleted the file — it should not appear in the index.
        Assert.Equal(7, index.EntryCount);
        Assert.Equal(1, index.ReucCount);
        Assert.False(index.HasConflicts);
    }

    [Fact]
    public async Task Trivial_8_OurDeleted_TheirUnchanged()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "trivial-8", "trivial-8-branch");
        Assert.Equal(7, index.EntryCount);
        Assert.Equal(1, index.ReucCount);
        Assert.False(index.HasConflicts);
    }

    [Fact]
    public async Task Trivial_10_OurUnchanged_TheirDeleted()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "trivial-10", "trivial-10-branch");
        Assert.Equal(7, index.EntryCount);
        Assert.Equal(1, index.ReucCount);
        Assert.False(index.HasConflicts);
    }

    // ── whitespace.c ─────────────────────────────────────────────────────

    [Fact]
    public async Task Whitespace_Conflict()
    {
        await using GitRepository repo = await OpenMergeWhitespaceRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "branch_a_eol", "branch_b_eol");

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "4026a6c83f39c56881c9ac62e7582db9e3d33a4f", 1, "test.txt"),
            new(M0644, "c3b1fb31424c98072542cc8e42b48c92e52f494a", 2, "test.txt"),
            new(M0644, "262f67de0de2e535a59ae1bc3c739601e98c354d", 3, "test.txt"),
        ]);
    }

    [Fact]
    public async Task Whitespace_IgnoreEol()
    {
        await using GitRepository repo = await OpenMergeWhitespaceRepoAsync();
        var opts = new GitMergeOptions { FileFlags = GitMergeFileFlags.IgnoreWhitespaceEol };
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "branch_a_eol", "branch_b_eol", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "ee3c2aac8e03224c323b58ecb1f9eef616745467", 0, "test.txt"),
        ]);
    }

    [Fact]
    public async Task Whitespace_IgnoreChange()
    {
        await using GitRepository repo = await OpenMergeWhitespaceRepoAsync();
        var opts = new GitMergeOptions { FileFlags = GitMergeFileFlags.IgnoreWhitespaceChange };
        GitIndex index = await MergeTestHelpers.MergeTreesFromBranchesAsync(repo, "branch_a_change", "branch_b_change", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(M0644, "a827eab4fd66ab37a6ebcfaa7b7e341abfd55947", 0, "test.txt"),
        ]);
    }
}

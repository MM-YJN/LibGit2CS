using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

// Parity tests for the cache eviction count, per-repo ODB sharing, alternates depth + dot-prefix resolution, pack backend always added,
// no tmp_pack cleanup (corrupt pack fails the refresh), and loose exists on a directory path.
public sealed class OdbLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public OdbLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_OdbLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir(string name) => Path.Combine(_tempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);

    // ── one ODB per repository (C shares the repo-owned cache) ────────

    [Fact]
    public async Task Repo_Objects_IsASingleSharedInstance()
    {
        // C (odb.c:46-54): the repo-owned ODB's cache is the REPOSITORY's
        // cache — a second odb over the same repo shares it, so the
        // repository exposes a single GitObjectDb.
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        Assert.Same(repo.Objects, repo.Objects);
    }

    // ── alternates depth and dot-prefix resolution ────────────────────

    [Fact]
    public async Task Alternates_SixLevelsDeep_AreLoaded()
    {
        // C (odb.c:763-766): alternates files at depths 0..5 are read
        // (alternate_depth > GIT_ALTERNATES_MAX_DEPTH stops) — SIX levels.
        // The chain must exist BEFORE the top repo opens (the ODB is
        // constructed eagerly at open).
        string blobDir = NewDir("blobrepo");
        string objectsG;
        GitOid oid;
        await using (GitRepository blobRepo = await GitRepository.InitAsync(blobDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken))
        {
            oid = await blobRepo.ObjectWriteAsync(GitObjectType.Blob, "deep"u8.ToArray(), TestContext.Current.CancellationToken);
            objectsG = Path.Combine(blobDir, ".git", "objects");
        }

        // Wire A → B → C → D → E → F → G via alternates files (A is the top
        // repo's objects dir).
        string top = NewDir("top");
        string objectsA = Path.Combine(top, ".git", "objects");
        string prev = objectsA;
        for (int i = 0; i < 6; i++)
        {
            string next = i == 5 ? objectsG : Path.Combine(_tempDir, $"chain_{Guid.NewGuid().ToString("N")[..8]}");
            if (i < 5)
            {
                Directory.CreateDirectory(Path.Combine(next, "info"));
            }

            string altFile = Path.Combine(prev, "info", "alternates");
            Directory.CreateDirectory(Path.GetDirectoryName(altFile)!);
            await File.WriteAllTextAsync(altFile, next + "\n", cancellationToken: TestContext.Current.CancellationToken);
            prev = next;
        }

        await using GitRepository repo = await GitRepository.InitAsync(top, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        Assert.True(await repo.Objects.ExistsAsync(oid, TestContext.Current.CancellationToken), "object in the 6th alternates level should be visible");
    }

    [Fact]
    public async Task Alternates_NonDotRelativePath_IsNotResolvedAgainstObjectsDir()
    {
        // C (odb.c:791-798): ONLY dot-prefixed relative paths resolve against
        // the objects dir; "subdir" stays CWD-relative (a C quirk) and thus
        // does not find objects/subdir.
        // relative path against objectsDir.
        string top = NewDir("top");
        string objectsA = Path.Combine(top, ".git", "objects");

        // Place an object at objects/subdir/<oid> and reference "subdir"
        // BEFORE the repo opens (the ODB is constructed eagerly at open).
        string subdir = Path.Combine(objectsA, "subdir");
        Directory.CreateDirectory(subdir);
        GitOid oid = OdbTestHelpers.WriteLooseObject(subdir, GitObjectType.Blob, "sub"u8.ToArray());

        string altFile = Path.Combine(objectsA, "info", "alternates");
        Directory.CreateDirectory(Path.GetDirectoryName(altFile)!);
        await File.WriteAllTextAsync(altFile, "subdir\n", cancellationToken: TestContext.Current.CancellationToken);

        await using GitRepository repo = await GitRepository.InitAsync(top, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        Assert.False(await repo.Objects.ExistsAsync(oid, TestContext.Current.CancellationToken));
    }

    // ── pack backend always added ─────────────────────────────────────

    [Fact]
    public async Task Refresh_NoPackDir_IsANoOp()
    {
        // C (odb_pack.c:524-526): the pack backend exists even without a
        // pack folder — its refresh returns 0 immediately.
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        await repo.Objects.RefreshPackBackendsAsync(TestContext.Current.CancellationToken); // must not throw
    }

    // ── refresh extras ────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_LeavesTmpPackFilesAlone()
    {
        // C never deletes leftover tmp_pack_* files (odb_pack.c has no such
        // cleanup).
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        string packDir = Path.Combine(repo.Path, "objects", "pack");
        Directory.CreateDirectory(packDir);
        string tmpFile = Path.Combine(packDir, "tmp_pack_abcdef");
        await File.WriteAllBytesAsync(tmpFile, [1, 2, 3], cancellationToken: TestContext.Current.CancellationToken);

        await repo.Objects.RefreshPackBackendsAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(tmpFile), "tmp_pack_* files must not be deleted");
    }

    [Fact]
    public async Task Refresh_CorruptPack_FailsTheRefresh()
    {
        // C (odb_pack.c:258-260, odb.c:1416-1417): a pack that fails to open
        // (other than ENOTFOUND) propagates and FAILS the whole refresh; the
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        string packDir = Path.Combine(repo.Path, "objects", "pack");
        Directory.CreateDirectory(packDir);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "corrupt.pack"), [1, 2, 3, 4], cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "corrupt.idx"), [9, 9, 9], cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(
            () => repo.Objects.RefreshPackBackendsAsync(TestContext.Current.CancellationToken));
    }

    // ── loose exists when the object path is a directory ──────────────

    [Fact]
    public async Task LooseExists_DirectoryAtObjectPath_ReturnsTrue()
    {
        // C (odb_loose.c:466-472): git_fs_path_exists is stat-based — a
        // DIRECTORY at the object path makes exists() return true (File.Exists
        // would be false for directories).
        string objectsDir = NewDir("objects");
        Directory.CreateDirectory(objectsDir);
        var oid = GitOid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa".AsSpan(), GitHashAlgorithmKind.Sha1);
        string path = Path.Combine(objectsDir, oid.ToPathString());
        Directory.CreateDirectory(path); // a DIRECTORY at the object path

        await using var backend = new LooseObjectBackend(objectsDir, GitHashAlgorithmKind.Sha1);
        Assert.True(backend.Exists(oid));

        // Read fails with a read error (not a silent not-found).
        await Assert.ThrowsAnyAsync<Exception>(() => backend.ReadAsync(oid, TestContext.Current.CancellationToken));
    }
}

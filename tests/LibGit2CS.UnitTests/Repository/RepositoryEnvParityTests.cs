using System.Runtime.Versioning;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary>
/// Regression tests for the repository behaviors (repository +
/// cross-cutting): the FROM_ENV env surface (GIT_INDEX_FILE,
/// GIT_ALTERNATE_OBJECT_DIRECTORIES), the shallow-grafts refresh behind
/// git_repository__shallow_roots, and the init config/file-mode byte
/// parity. Expectations are C-verified against libgit2 1.9.4 via
/// a C probe harness.
/// </summary>
public sealed class RepositoryEnvParityTests
{
    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<(GitRepository repo, string path)> InitRepoAsync(string tag)
    {
        string repoPath = Path.Combine(Path.GetTempPath(), $"LibGit2CS_RepoEnv_{tag}_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static async Task CommitAsync(GitRepository repo, string path, string content)
    {
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync(path, blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task CleanupAsync(GitRepository repo, string repoPath)
    {
        await repo.DisposeAsync();
        try
        {
            Directory.Delete(repoPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── FROM_ENV: GIT_INDEX_FILE (probe) ─────────────────────────────

    [Fact]
    public async Task OpenFromEnv_GitIndexFile_UsesCustomIndexPath()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync("idxfile");
        try
        {
            await CommitAsync(repo, "f.txt", "x\n");
            GitIndex aIndex = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            GitOid fBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
            aIndex.Add(new GitIndexEntry("f.txt", fBlob, GitFileMode.Regular));
            await aIndex.WriteAsync(TestContext.Current.CancellationToken);
            await repo.DisposeAsync();

            // A valid index at the custom path: a second repo's index whose
            // tree contains a marker entry.
            (GitRepository repoC, string pathC) = await InitRepoAsync("custom");
            try
            {
                await CommitAsync(repoC, "marker.txt", "marker\n");
                GitIndex cIndex = await repoC.GetIndexAsync(TestContext.Current.CancellationToken);
                GitOid markerBlob = await repoC.ObjectWriteAsync(GitObjectType.Blob, "marker\n"u8.ToArray(), TestContext.Current.CancellationToken);
                cIndex.Add(new GitIndexEntry("marker.txt", markerBlob, GitFileMode.Regular));
                await cIndex.WriteAsync(TestContext.Current.CancellationToken);
                await repoC.DisposeAsync();
                string customPath = Path.Combine(pathC, ".git", "index");

                // C (repository.c:1638-1649): with FROM_ENV the index path comes
                // from GIT_INDEX_FILE — reads AND writes.
                var ctx = new GitContext();
                ctx.Env["GIT_INDEX_FILE"] = customPath;
                await using GitRepository opened = await GitRepository.OpenExtAsync(repoPath, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

                GitIndex index = await opened.GetIndexAsync(TestContext.Current.CancellationToken);
                Assert.True(index.Find("marker.txt") >= 0);

                // A write goes back to the CUSTOM path, not .git/index.
                GitOid extra = await opened.ObjectWriteAsync(GitObjectType.Blob, "extra\n"u8.ToArray(), TestContext.Current.CancellationToken);
                index.Add(new GitIndexEntry("second.txt", extra, GitFileMode.Regular));
                await index.WriteAsync(TestContext.Current.CancellationToken);

                GitIndex reread = await GitIndex.OpenAsync(customPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
                Assert.True(reread.Find("second.txt") >= 0);

                GitIndex repoIndex = await GitIndex.OpenAsync(Path.Combine(repoPath, ".git", "index"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
                Assert.True(repoIndex.Find("f.txt") >= 0);
                Assert.True(repoIndex.Find("marker.txt") < 0);
            }
            finally
            {
                try
                {
                    Directory.Delete(pathC, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── FROM_ENV: GIT_ALTERNATE_OBJECT_DIRECTORIES (probe) ───────────

    [Fact]
    public async Task OpenFromEnv_AlternateObjectDirectories_FindsAlternateObjects()
    {
        (GitRepository repoA, string pathA) = await InitRepoAsync("altA");
        (GitRepository repoB, string pathB) = await InitRepoAsync("altB");
        try
        {
            await CommitAsync(repoA, "f.txt", "x\n");
            GitOid special = await repoB.ObjectWriteAsync(GitObjectType.Blob, "special-object\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await repoB.DisposeAsync();
            await repoA.DisposeAsync();

            // C (repository.c:1499-1522): with FROM_ENV, every token of
            // GIT_ALTERNATE_OBJECT_DIRECTORIES is added as a disk alternate.
            var ctx = new GitContext();
            ctx.Env["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(pathB, ".git", "objects");
            await using GitRepository opened = await GitRepository.OpenExtAsync(pathA, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

            GitBlob blob = (await opened.ObjectLookupAsync<GitBlob>(special, TestContext.Current.CancellationToken))!;
            Assert.Equal("special-object\n", Encoding.UTF8.GetString(blob.Content.Span));
        }
        finally
        {
            await CleanupAsync(repoA, pathA);
            await CleanupAsync(repoB, pathB);
        }
    }

    // ── shallow grafts refresh (probe) ───────────────────────────────

    [Fact]
    public async Task ShallowGrafts_RefreshAsync_SeesFileRewrites()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync("shallow");
        try
        {
            await CommitAsync(repo, "f.txt", "x\n");
            string shallowPath = Path.Combine(repoPath, ".git", "shallow");
            const string one = "1111111111111111111111111111111111111111\n";
            const string two = "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n";
            await File.WriteAllTextAsync(shallowPath, one, cancellationToken: TestContext.Current.CancellationToken);
            await repo.DisposeAsync();

            await using GitRepository opened = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
            Assert.Equal(1, opened.ShallowGrafts!.Count);

            // C (repository.c:3793-3797): git_repository__shallow_roots
            // refreshes the grafts — the rewrite is visible.
            await File.WriteAllTextAsync(shallowPath, two, cancellationToken: TestContext.Current.CancellationToken);
            await opened.ShallowGrafts.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, opened.ShallowGrafts.Count);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── init config bytes (probe) ────────────────────────────────────

    [Fact]
    public async Task Init_ConfigBytes_MatchC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync("cfg");
        try
        {
            await repo.DisposeAsync();
            string config = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "config"), TestContext.Current.CancellationToken);

            // Preserve the C probe's key order and exact bytes while deriving
            // filesystem-dependent values from independent filename checks.
            bool ignoreCase = File.Exists(Path.Combine(repoPath, ".git", "CoNfIg"));
            string expected = "[core]\n\tbare = false\n\trepositoryformatversion = 0\n"
                + (OperatingSystem.IsWindows() ? "\tfilemode = false\n\tsymlinks = false\n" : "\tfilemode = true\n")
                + (ignoreCase ? "\tignorecase = true\n" : "");
            if (OperatingSystem.IsMacOS())
            {
                string probe = "unicode_" + Guid.NewGuid().ToString("N");
                string composed = Path.Combine(repoPath, probe + "\u00e9");
                await File.WriteAllBytesAsync(composed, [], TestContext.Current.CancellationToken);
                try
                {
                    bool decomposes = File.Exists(Path.Combine(repoPath, probe + "e\u0301"));
                    expected += decomposes ? "\tprecomposeunicode = true\n" : "\tprecomposeunicode = false\n";
                }
                finally
                {
                    File.Delete(composed);
                }
            }

            Assert.Equal(expected + "\tlogallrefupdates = true\n", config);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── file-mode policy (probe) ─────────────────────────────────────

    [Fact]
    public async Task FileModes_MatchCPolicy()
    {
        // Unix file modes do not exist on Windows (PlatformNotSupportedException).
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (GitRepository repo, string repoPath) = await InitRepoAsync("modes");
        try
        {
            // C probe files 0666 & ~umask, dirs 0777 & ~umask — the .NET
            // defaults are the same policy. Same-process probes share the
            // ambient umask, so the assertions are umask-independent rather
            // than hardcoding the umask-022 result (0644/0755) that fails
            // under other ambient umasks (e.g. 002 → 0664/0775). The perm-bit
            // mask strips any inherited setgid/sticky bits so the comparison
            // is independent of the parent directory's special bits.
            string probeFile = Path.Combine(repoPath, "probe_file");
            await File.WriteAllBytesAsync(probeFile, [], TestContext.Current.CancellationToken);
            string probeDir = Path.Combine(repoPath, "probe_dir");
            Directory.CreateDirectory(probeDir);

            const int PermMask = 0x1FF;
            int expectedFile = (int)File.GetUnixFileMode(probeFile) & PermMask;
            int expectedDir = (int)File.GetUnixFileMode(probeDir) & PermMask;

            await CommitAsync(repo, "f.txt", "x\n");
            await (await repo.GetIndexAsync(TestContext.Current.CancellationToken)).WriteAsync(TestContext.Current.CancellationToken);
            string gitdir = Path.Combine(repoPath, ".git");

            Assert.Equal(expectedFile, FileMode(gitdir, "config") & PermMask);
            Assert.Equal(expectedFile, FileMode(gitdir, "index") & PermMask);
            Assert.Equal(expectedFile, FileMode(gitdir, "HEAD") & PermMask);
            Assert.Equal(expectedFile, FileMode(Path.Combine(gitdir, "refs", "heads"), "master") & PermMask);
            Assert.Equal(expectedDir, FileMode(gitdir, "refs") & PermMask);
            Assert.Equal(expectedDir, FileMode(gitdir, "objects") & PermMask);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static int FileMode(string dir, string name)
        => (int)File.GetUnixFileMode(Path.Combine(dir, name));
}

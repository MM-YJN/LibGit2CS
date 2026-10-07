using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitStatusEntry = LibGit2CS.Status.GitStatusEntry;
using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;

namespace LibGit2CS.IntegrationTests.IO;

/// <summary>
/// End-to-end regression tests for the behaviors of
/// libgit2 1.9.4 in the
/// IO area: (workdir iterator recurses through symlinks-to-directories),
/// (byte matcher infinite-loops on malformed POSIX class patterns),
/// (<c>WorkdirReader.ComputeOid</c> stackallocs the whole file content).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit tests pin the primitives
/// (<see cref="LibGit2CS.IO.WildMatch"/> byte matcher,
/// <see cref="LibGit2CS.IO.WorkdirReader"/>, the filesystem iterator's mode
/// classification); these tests drive the public surfaces that reach them —
/// <see cref="GitRepository.StatusNewAsync"/>,
/// <see cref="GitIndex.AddAllAsync"/>, and
/// <see cref="GitRepository.ApplyAsync"/> with
/// <see cref="GitApplyLocation.Workdir"/>.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build; symlink tests skip on
/// platforms without symlink support.
/// </para>
/// </remarks>
public sealed class IoHighRegressionIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath(string kind)
        => Path.Combine(Path.GetTempPath(), $"libgit2cs-iohigh-{kind}-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryCreateSymlink(string linkPath, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string workdir, string path, byte[] content, CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content, ct);
        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync(path, ct);
        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = Sig,
            Committer = Sig,
            Message = $"add {path}\n",
            UpdateRef = "HEAD",
        }, ct);
    }

    // ── symlinks-to-directories ───────────────────────────────────

    [Fact]
    public async Task AddAll_WithSymlinkToDirectory_AddsSymlinkNotTargetContents()
    {
        string path = NewRepoPath("addall");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);

            // A real directory (tracked, so the diff recurses into it) and a
            // symlink pointing at it (untracked — the recursion target).
            string realDir = Path.Combine(repoPath, "realdir");
            Directory.CreateDirectory(realDir);
            await File.WriteAllTextAsync(
                Path.Combine(realDir, "inner.txt"), "secret\n",
                cancellationToken: TestContext.Current.CancellationToken);
            string linkPath = Path.Combine(repoPath, "link");
            if (!TryCreateSymlink(linkPath, "realdir"))
            {
                return; // symlinks unavailable (Windows without privileges)
            }

            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<string> processed = await index.AddAllAsync(
                null, cancellationToken: TestContext.Current.CancellationToken);

            // The symlink is added as a single Symlink entry; the target
            // directory's contents must NOT be added under "link/".
            GitIndexEntry? linkEntry = index.EntryByPath("link", stage: 0);
            Assert.NotNull(linkEntry);
            Assert.Equal(GitFileMode.Symlink, linkEntry!.Value.Mode);
            Assert.Equal(-1, index.FindPrefix("link/"));
            Assert.DoesNotContain(processed, p => p.StartsWith("link/", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── malformed POSIX classes in .gitignore ─────────────────────

    [Fact]
    public async Task Gitignore_MalformedPosixClass_DoesNotHangStatus()
    {
        string path = NewRepoPath("gitignore");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);

            // The hang pattern, as a plausible user typo in .gitignore.
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, ".gitignore"), "[[:alpha]]\n",
                cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "x"), "content\n",
                cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "[]"), "content\n",
                cancellationToken: TestContext.Current.CancellationToken);

            // Regression: the malformed character class must not hang (100% CPU).
            using GitStatusList list = await repo.StatusNewAsync(
                new GitStatusOptions { Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored },
                cancellationToken: TestContext.Current.CancellationToken);

            // C behavior: the malformed class degrades to a literal set
            // { '[', ':', 'a', 'l', 'p', 'h', 'a' } with the trailing ']'
            // consuming a second char — "[]" is ignored, "x" is not.
            GitStatusEntry ignored = list.Entries.Single(e => e.Path.ToUtf8String() == "[]");
            Assert.Equal(GitStatusFlags.Ignored, ignored.Status);
            GitStatusEntry x = list.Entries.Single(e => e.Path.ToUtf8String() == "x");
            Assert.Equal(GitStatusFlags.WorkdirNew, x.Status);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── WorkdirReader on large files ──────────────────────────────

    [Fact]
    public async Task Apply_WithLargeWorkdirFile_DoesNotStackOverflow()
    {
        string path = NewRepoPath("apply");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);

            // 16 MiB — far beyond the 8 MB default thread stack; the
            // stackalloc of the whole content crashed the host process.
            byte[] original = new byte[16 * 1024 * 1024];
            new Random(42).NextBytes(original);
            await CommitFileAsync(repo, repoPath, "big.bin", original, TestContext.Current.CancellationToken);

            // Workdir edit → diff (old side = index content, new side = edit).
            byte[] edited = (byte[])original.Clone();
            edited[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(
                Path.Combine(repoPath, "big.bin"), edited,
                cancellationToken: TestContext.Current.CancellationToken);
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeTypechange },
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, diff.DeltaCount);

            // Restore the workdir so the patch applies cleanly, then dry-run
            // apply (CHECK): the preimage read goes through WorkdirReader →
            // ComputeOid, the crash site.
            await File.WriteAllBytesAsync(
                Path.Combine(repoPath, "big.bin"), original,
                cancellationToken: TestContext.Current.CancellationToken);

            await repo.ApplyAsync(
                diff,
                GitApplyLocation.Workdir,
                new GitApplyOptions { Flags = GitApplyFlags.Check },
                cancellationToken: TestContext.Current.CancellationToken);

            // Nothing was written in CHECK mode.
            byte[] onDisk = await File.ReadAllBytesAsync(
                Path.Combine(repoPath, "big.bin"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(original, onDisk);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

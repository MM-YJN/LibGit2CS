using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitStatusEntry = LibGit2CS.Status.GitStatusEntry;
using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// End-to-end regression tests for libgit2 1.9.4 diff/checkout behaviors:
/// (patch print skips untracked/ignored deltas), (checkout rejects invalid
/// paths — no workdir escape), (should_remove_existing — no writes through
/// pre-existing symlinks).
/// </summary>
/// <remarks>
/// <b>Not Docker-gated.</b> Runs on every build; symlink tests skip on
/// platforms without symlink support.
/// </remarks>
public sealed class DiffCheckoutHighRegressionIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private static string NewRepoPath(string kind)
        => Path.Combine(Path.GetTempPath(), $"libgit2cs-diffcheckout-{kind}-" + Guid.NewGuid().ToString("N"));

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

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string workdir, string path, string content, CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);
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

    // ── patch printer status filter ────────────────────────────────

    [Fact]
    public async Task PatchPrint_UntrackedAndIgnored_AreNotPrinted()
    {
        string path = NewRepoPath("patchprint-untracked");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);
            await CommitFileAsync(repo, repoPath, "tracked.txt", "base\n", TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "untracked.txt"), "new\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repoPath, ".gitignore"), "ignored.log\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repoPath, "ignored.log"), "noise\n", TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions
                {
                    Flags = GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.IncludeIgnored,
                },
                cancellationToken: TestContext.Current.CancellationToken);

            var sb = new StringBuilder();
            await diff.PrintAsync(GitDiffPrintFormat.Patch, (_, _, line) =>
            {
                if (line.Origin is GitDiffLineOrigin.Addition or GitDiffLineOrigin.Deletion or GitDiffLineOrigin.Context)
                {
                    sb.Append((char)line.Origin);
                }

                sb.Append(Encoding.UTF8.GetString(line.Content.Span));
            }, TestContext.Current.CancellationToken);

            // C's diff_print_patch_file (diff_print.c:626-632) skips
            // UNTRACKED (without SHOW_UNTRACKED_CONTENT) and IGNORED deltas.
            Assert.DoesNotContain("untracked.txt", sb.ToString());
            Assert.DoesNotContain("ignored.log", sb.ToString());

            // With SHOW_UNTRACKED_CONTENT the untracked file IS printed.
            using GitDiff diff2 = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions
                {
                    Flags = GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.ShowUntrackedContent,
                },
                cancellationToken: TestContext.Current.CancellationToken);
            var sb2 = new StringBuilder();
            await diff2.PrintAsync(GitDiffPrintFormat.Patch, (_, _, line) =>
            {
                if (line.Origin is GitDiffLineOrigin.Addition or GitDiffLineOrigin.Deletion or GitDiffLineOrigin.Context)
                {
                    sb2.Append((char)line.Origin);
                }

                sb2.Append(Encoding.UTF8.GetString(line.Content.Span));
            }, TestContext.Current.CancellationToken);
            Assert.Contains("+new", sb2.ToString());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── checkout path validation ───────────────────────────────────

    [Fact]
    public async Task Checkout_MaliciousTreeEntry_ThrowsAndDoesNotEscape()
    {
        string path = NewRepoPath("malicious-entry");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);

            // A raw tree whose entry name traverses out of the workdir
            // (GitTree parsing is structure-only — tree.c:394-443).
            GitOid blobOid = await repo.ObjectWriteAsync(
                GitObjectType.Blob, "payload\n"u8.ToArray(),
                TestContext.Current.CancellationToken);
            using var ms = new MemoryStream();
            ms.Write("100644 ../evil\0"u8);
            ms.Write(blobOid.RawBytes.ToArray());
            GitOid treeOid = await repo.ObjectWriteAsync(
                GitObjectType.Tree, ms.ToArray(),
                TestContext.Current.CancellationToken);
            GitTree? evilTree = await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

            // C: "cannot checkout to invalid path '../evil'" (checkout_verify_paths,
            // checkout.c:1288-1310), GIT_ERROR_CHECKOUT.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.CheckoutTreeAsync(
                    evilTree,
                    new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                    TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCategory.Checkout, ex.Category);
            Assert.Contains("invalid path", ex.Message);

            string escapeTarget = Path.Combine(Path.GetDirectoryName(repoPath)!, "evil");
            Assert.False(File.Exists(escapeTarget), $"checkout escaped the workdir and wrote {escapeTarget}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── should_remove_existing ─────────────────────────────────────

    [Fact]
    public async Task ForceCheckout_Ignorecase_DoesNotWriteThroughSymlink()
    {
        string path = NewRepoPath("symlink-ignorecase");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);
            await CommitFileAsync(repo, repoPath, "foo.txt", "base\n", TestContext.Current.CancellationToken);
            await repo.Config.SetBoolAsync("core.ignorecase", true, TestContext.Current.CancellationToken);

            string victimPath = Path.Combine(path, "victim.txt");
            await File.WriteAllTextAsync(victimPath, "victim-original\n", TestContext.Current.CancellationToken);
            string fooPath = Path.Combine(repoPath, "foo.txt");
            File.Delete(fooPath);
            if (!TryCreateSymlink(fooPath, "../victim.txt"))
            {
                return;
            }

            // File.WriteAllBytesAsync (O_CREAT|O_TRUNC) would follow the
            // symlink and overwrite the victim; C's mkpath2file
            // (should_remove_existing, checkout.c:1407-1417, 1449-1483)
            // removes the link first.
            await repo.CheckoutTreeAsync(
                null,
                new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                TestContext.Current.CancellationToken);

            string victimContent = await File.ReadAllTextAsync(victimPath, TestContext.Current.CancellationToken);
            Assert.Equal("victim-original\n", victimContent);
            Assert.Null(new FileInfo(fooPath).LinkTarget);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

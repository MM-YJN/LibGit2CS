using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

// the patch-format
// printers emitted output for deltas that C's diff_print_patch_file
// (diff_print.c:626-632) skips entirely: S_ISDIR(new mode), UNMODIFIED,
// IGNORED, UNREADABLE, and UNTRACKED-without-SHOW_UNTRACKED_CONTENT.
// The C# per-delta loop printed untracked files as all-added patches and
// ignored files as spurious mode headers.
public sealed class DiffPrinterPatchSkipRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public DiffPrinterPatchSkipRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_DiffPrinterSkip_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<(GitRepository Repo, string RepoPath)> CreateRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static async Task CommitFileAsync(GitRepository repo, string repoPath, string path, string content)
    {
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, path), content,
            cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(path, TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeId = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = $"add {path}\n",
            UpdateRef = "HEAD",
        }, TestContext.Current.CancellationToken);
    }

    private static async Task<string> RenderAsync(GitDiff diff, GitDiffPrintFormat format)
    {
        var sb = new StringBuilder();
        await diff.PrintAsync(format, (_, _, line) =>
        {
            // Like git_diff_print_callback__to_buf (diff_print.c:800-818):
            // prepend the origin char for content lines.
            if (line.Origin is GitDiffLineOrigin.Addition
                or GitDiffLineOrigin.Deletion
                or GitDiffLineOrigin.Context)
            {
                sb.Append((char)line.Origin);
            }

            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        }, TestContext.Current.CancellationToken);
        return sb.ToString();
    }

    [Fact]
    public async Task PatchPrint_UntrackedDelta_IsSkippedWithoutShowUntrackedContent()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("repo1");
        await using (repo)
        {
            await CommitFileAsync(repo, repoPath, "tracked.txt", "base\n");

            // Modify the tracked file + add an untracked file.
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "tracked.txt"), "edited\n",
                cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "untracked.txt"), "new stuff\n",
                cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeUntracked },
                cancellationToken: TestContext.Current.CancellationToken);

            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);

            // The tracked modification IS printed...
            Assert.Contains("tracked.txt", output);
            // ...but the untracked file must NOT produce a patch (C:
            // diff_print_patch_file returns 0 for UNTRACKED without
            // SHOW_UNTRACKED_CONTENT).
            Assert.DoesNotContain("untracked.txt", output);
        }
    }

    [Fact]
    public async Task PatchPrint_UntrackedDelta_IsPrintedWithShowUntrackedContent()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("repo2");
        await using (repo)
        {
            await CommitFileAsync(repo, repoPath, "tracked.txt", "base\n");
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "untracked.txt"), "new stuff\n",
                cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions
                {
                    Flags = GitDiffOptionsFlags.IncludeUntracked
                          | GitDiffOptionsFlags.ShowUntrackedContent,
                },
                cancellationToken: TestContext.Current.CancellationToken);

            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);

            // With SHOW_UNTRACKED_CONTENT the untracked file is printed as
            // an added patch, matching C.
            Assert.Contains("untracked.txt", output);
            Assert.Contains("+new stuff", output);
        }
    }

    [Fact]
    public async Task PatchPrint_IgnoredDelta_IsSkipped()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("repo3");
        await using (repo)
        {
            await CommitFileAsync(repo, repoPath, "tracked.txt", "base\n");
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, ".gitignore"), "ignored.log\n",
                cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "ignored.log"), "noise\n",
                cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions
                {
                    Flags = GitDiffOptionsFlags.IncludeUntracked
                          | GitDiffOptionsFlags.IncludeIgnored,
                },
                cancellationToken: TestContext.Current.CancellationToken);

            // The ignored delta must exist in the diff...
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Ignored);

            // ...but must not be printed by the patch printer.
            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);
            Assert.DoesNotContain("ignored.log", output);
        }
    }

    [Fact]
    public async Task PatchHeaderPrint_UntrackedDelta_IsSkipped()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("repo4");
        await using (repo)
        {
            await CommitFileAsync(repo, repoPath, "tracked.txt", "base\n");
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "untracked.txt"), "new stuff\n",
                cancellationToken: TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(
                new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeUntracked },
                cancellationToken: TestContext.Current.CancellationToken);

            // C's PATCH_HEADER format uses the same diff_print_patch_file
            // skip — the untracked delta yields no header either.
            string output = await RenderAsync(diff, GitDiffPrintFormat.PatchHeader);
            Assert.DoesNotContain("untracked.txt", output);
        }
    }
}

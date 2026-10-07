using System.Reflection;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

// Parity cases verified against libgit2 1.9.4:
//  - patch-level PrintPatchAsync omitted the binary body (C's
//    git_patch_print routes through diff_print_patch_binary,
//    diff_print.c:882-885).
//  - DELETED deltas missed new_file VALID_ID (diff_generate.c:211-214).
//  - raw/name-status/name-only printers ignored SHOW_UNMODIFIED
//    (diff_print.c:154-156, 182-183, 226-227).
//  - GetStatsAsync reported binary deltas as -1 where C counts 0
//    (diff_stats.c:231).
//  - blob/buffer diff factories hardcoded "blob"/"buffer" paths
//    instead of C's "file" default (patch_generate.c:490-497).
//  - workdir symlink read failure silently yielded empty content
//    where C errors (diff_file.c:313-317).
//  - TYPECHANGE_TREES + workdir blob→dir: the old item was always
//    advanced, dropping C's second plain DELETED record
//    (diff_generate.c:1232-1234).
public sealed class DiffLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public DiffLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(GitRepository Repo, string RepoPath)> CreateRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static async Task CommitFileAsync(GitRepository repo, string repoPath, string path, byte[] content)
    {
        await File.WriteAllBytesAsync(
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

    // ---- patch-level printing emits the binary body ----

    [Fact]
    public async Task PrintPatch_BinaryPatch_EmitsBinaryBody()
    {
        (GitRepository repo, _) = await CreateRepoAsync("binary-body");
        var oldBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, new byte[] { 0x00, 0x01, 0x02 }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var newBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, new byte[] { 0x00, 0x01, 0x03 }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;

        var patch = GitPatch.FromBlobs(repo, oldBlob, newBlob, oldPath: "a.bin", newPath: "a.bin",
            new GitDiffOptions { Flags = GitDiffOptionsFlags.ShowBinary });

        var sb = new StringBuilder();
        await DiffPrinter.PrintPatchAsync(patch, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        }, TestContext.Current.CancellationToken);

        // C's git_patch_print routes through diff_print_patch_binary
        // (diff_print.c:882-885): the "GIT binary patch" body is emitted.
        // Printing only the file header would omit the body.
        Assert.Contains("GIT binary patch", sb.ToString());
        Assert.Contains("literal 3", sb.ToString());
    }

    [Fact]
    public async Task PrintPatch_BinaryPatch_NoShowBinary_EmitsDifferLine()
    {
        (GitRepository repo, _) = await CreateRepoAsync("binary-noshow");
        var oldBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, new byte[] { 0x00, 0x01, 0x02 }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var newBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, new byte[] { 0x00, 0x01, 0x03 }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;

        var patch = GitPatch.FromBlobs(repo, oldBlob, newBlob, oldPath: "a.bin", newPath: "a.bin");

        var sb = new StringBuilder();
        await DiffPrinter.PrintPatchAsync(patch, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Binary files", sb.ToString());
        Assert.Contains("differ", sb.ToString());
    }

    // ---- DELETED deltas carry new_file VALID_ID ----

    [Fact]
    public void CreateDeltaFromOne_Deleted_HasNewFileValidId()
    {
        // C's diff_delta__from_one sets VALID_ID on the new_file whenever
        // has_old (diff_generate.c:211-214) — a DELETED delta's empty new
        // side carries VALID_ID (and VALID_SIZE via FlagKnownSizes).
        var entry = new GitIndexEntry("gone.txt", GitOid.Empty, GitFileMode.Regular);
        GitDiffDelta delta = DiffGenerator.CreateDeltaFromOne(GitDeltaStatus.Deleted, entry, hasOld: true);

        Assert.NotEqual(0, (int)(delta.NewFile.Flags & GitDiffFileFlags.ValidId));
        Assert.NotEqual(0, (int)(delta.NewFile.Flags & GitDiffFileFlags.ValidSize));
    }

    [Fact]
    public void CreateDeltaFromOne_Added_NonZeroId_HasNewFileValidId()
    {
        // Control: an ADDED delta with a real OID already carried VALID_ID.
        var oid = GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1);
        var entry = new GitIndexEntry("new.txt", oid, GitFileMode.Regular);
        GitDiffDelta delta = DiffGenerator.CreateDeltaFromOne(GitDeltaStatus.Added, entry, hasOld: false);

        Assert.NotEqual(0, (int)(delta.NewFile.Flags & GitDiffFileFlags.ValidId));
    }

    // ---- one-delta printers honor SHOW_UNMODIFIED ----

    [Fact]
    public async Task Print_NameOnly_ShowUnmodified_PrintsUnmodified()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("nameonly-unmodified");
        await CommitFileAsync(repo, repoPath, "same.txt", "x\n"u8.ToArray());

        GitDiff diff = await repo.DiffIndexToWorkdirAsync(new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUnmodified | GitDiffOptionsFlags.ShowUnmodified,
        }, TestContext.Current.CancellationToken);

        string output = await RenderAsync(diff, GitDiffPrintFormat.NameOnly);
        Assert.Contains("same.txt", output);
    }

    [Fact]
    public async Task Print_NameStatus_ShowUnmodified_PrintsUnmodified()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("namestatus-unmodified");
        await CommitFileAsync(repo, repoPath, "same.txt", "x\n"u8.ToArray());

        GitDiff diff = await repo.DiffIndexToWorkdirAsync(new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUnmodified | GitDiffOptionsFlags.ShowUnmodified,
        }, TestContext.Current.CancellationToken);

        string output = await RenderAsync(diff, GitDiffPrintFormat.NameStatus);
        Assert.Contains("same.txt", output);
    }

    [Fact]
    public async Task Print_Raw_ShowUnmodified_PrintsUnmodified()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("raw-unmodified");
        await CommitFileAsync(repo, repoPath, "same.txt", "x\n"u8.ToArray());

        GitDiff diff = await repo.DiffIndexToWorkdirAsync(new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUnmodified | GitDiffOptionsFlags.ShowUnmodified,
        }, TestContext.Current.CancellationToken);

        string output = await RenderAsync(diff, GitDiffPrintFormat.Raw);
        Assert.Contains("same.txt", output);
    }

    [Fact]
    public async Task Print_NameOnly_WithoutShowUnmodified_SkipsUnmodified()
    {
        // Control: without SHOW_UNMODIFIED the unmodified line is skipped.
        (GitRepository repo, string repoPath) = await CreateRepoAsync("nameonly-skip");
        await CommitFileAsync(repo, repoPath, "same.txt", "x\n"u8.ToArray());

        GitDiff diff = await repo.DiffIndexToWorkdirAsync(new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUnmodified,
        }, TestContext.Current.CancellationToken);

        string output = await RenderAsync(diff, GitDiffPrintFormat.NameOnly);
        Assert.DoesNotContain("same.txt", output);
    }

    // ---- binary deltas count 0/0 in stats ----

    [Fact]
    public async Task GetStats_BinaryDelta_CountsZero()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("stats-binary");
        await CommitFileAsync(repo, repoPath, "bin.dat", new byte[] { 0x00, 0x01, 0x02, 0x03 });
        await File.WriteAllBytesAsync(
            Path.Combine(repoPath, "bin.dat"), new byte[] { 0x00, 0x01, 0x02, 0x04 },
            cancellationToken: TestContext.Current.CancellationToken);

        GitDiff diff = await repo.DiffIndexToWorkdirAsync(null, TestContext.Current.CancellationToken);
        GitDiffStats stats = await diff.GetStatsAsync(TestContext.Current.CancellationToken);

        // C's git_diff_get_stats always calls git_patch_line_stats
        // (diff_stats.c:231), which counts 0/0 for binary patches — the
        // reporting -1/-1 would be wrong. The per-file counts are internal, so
        // pin them via reflection.
        FieldInfo perFileField = typeof(GitDiffStats).GetField(
            "_perFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("_perFile field missing");
        var perFile = (Array)perFileField.GetValue(stats)!;
        Assert.Single(perFile);
        object stat = perFile.GetValue(0)!;
        Assert.Equal(0, (int)stat.GetType().GetProperty("Insertions")!.GetValue(stat)!);
        Assert.Equal(0, (int)stat.GetType().GetProperty("Deletions")!.GetValue(stat)!);
        // The delta's binary flag still drives the "- -" numstat rendering.
        Assert.True((bool)stat.GetType().GetProperty("IsBinary")!.GetValue(stat)!);
    }

    // ---- blob/buffer factories default to "file" ----

    [Fact]
    public async Task PatchFromBlobs_NoPaths_HeaderUsesFile()
    {
        (GitRepository repo, _) = await CreateRepoAsync("blobs-nopaths");
        var oldBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, "a\n"u8.ToArray(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var newBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, "b\n"u8.ToArray(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;

        var patch = GitPatch.FromBlobs(repo, oldBlob, newBlob);

        var sb = new StringBuilder();
        await DiffPrinter.PrintPatchAsync(patch, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        }, TestContext.Current.CancellationToken);

        // C's patch_generated_from_sources defaults both paths to "file"
        // (patch_generate.c:490-497); printing "blob" would be wrong.
        Assert.Contains("diff --git a/file b/file", sb.ToString());
    }

    [Fact]
    public void Buffers_NoPaths_DeltaPathsAreFile()
    {
        var diff = GitDiff.Buffers("a\n"u8.ToArray(), "b\n"u8.ToArray());
        Assert.Equal("file", diff.GetDelta(0).OldFile.Path!.Value.ToUtf8String());
        Assert.Equal("file", diff.GetDelta(0).NewFile.Path!.Value.ToUtf8String());
    }

    // ---- workdir symlink read failure errors ----

    [Fact]
    public async Task WorkdirSymlink_ReplacedByRegularFile_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink semantics
        }

        (GitRepository repo, string repoPath) = await CreateRepoAsync("symlink-replaced");
        // The workdir entry is a REGULAR file, but the diff file claims
        // Symlink mode — the race where a symlink was replaced between delta
        // generation and content load. C's readlink fails and errors
        // ("failed to read symlink", diff_file.c:313-317); silently hashing
        // empty content would hide the race.
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "link"), "not a symlink anymore",
            cancellationToken: TestContext.Current.CancellationToken);

        var file = new GitDiffFile
        {
            Path = GitPath.FromUtf8String("link"),
            Mode = GitFileMode.Symlink,
            Size = 0,
        };
        var fc = DiffFileContent.FromDiff(
            repo, file, IteratorType.Workdir, new GitDiffOptions(),
            repo.Context.DiffDrivers.Auto, hasData: true);

        await Assert.ThrowsAsync<GitException>(
            () => fc.LoadAsync(new GitDiffOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkdirSymlink_StillASymlink_Loads()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (GitRepository repo, string repoPath) = await CreateRepoAsync("symlink-still");
        string linkPath = Path.Combine(repoPath, "link");
        try
        {
            File.CreateSymbolicLink(linkPath, "target.txt");
        }
        catch (IOException)
        {
            return;
        }

        var file = new GitDiffFile
        {
            Path = GitPath.FromUtf8String("link"),
            Mode = GitFileMode.Symlink,
            Size = 0,
        };
        var fc = DiffFileContent.FromDiff(
            repo, file, IteratorType.Workdir, new GitDiffOptions(),
            repo.Context.DiffDrivers.Auto, hasData: true);

        await fc.LoadAsync(new GitDiffOptions(), TestContext.Current.CancellationToken);
        Assert.Equal("target.txt", Encoding.UTF8.GetString(fc.Data.Span));
    }

    // ---- TYPECHANGE_TREES blob→dir yields [TYPECHANGE, DELETED] ----

    [Fact]
    public async Task Diff_TypechangeTrees_BlobToDir_YieldsTypechangeAndDeleted()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink-free path still works, but keep POSIX-only for determinism
        }

        (GitRepository repo, string repoPath) = await CreateRepoAsync("typechange-tree");
        await CommitFileAsync(repo, repoPath, "dir", "x\n"u8.ToArray());

        // Replace the tracked blob "dir" with a directory of untracked files.
        File.Delete(Path.Combine(repoPath, "dir"));
        Directory.CreateDirectory(Path.Combine(repoPath, "dir"));
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "dir", "inner.txt"), "y\n",
            cancellationToken: TestContext.Current.CancellationToken);

        GitDiff diff = await repo.DiffIndexToWorkdirAsync(new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeTypechangeTrees,
        }, TestContext.Current.CancellationToken);

        // C yields [TYPECHANGE, DELETED] (diff_generate.c:1232-1234 +
        // 1206-1212); yielding [TYPECHANGE] only would drop the delete.
        Assert.Equal(2, diff.DeltaCount);
        Assert.Equal(GitDeltaStatus.Typechange, diff.GetDelta(0).Status);
        Assert.Equal(GitDeltaStatus.Deleted, diff.GetDelta(1).Status);
    }
}

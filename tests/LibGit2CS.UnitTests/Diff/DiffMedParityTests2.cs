using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

namespace LibGit2CS.UnitTests.Diff;

/// <summary> Parity tests for diff-patch-blame: (apply failures → GitException ApplyFail/Patch), (diff.ignoresubmodules config),
/// (find-options ByConfig default), (CONFLICTED status char ' ' + skip), (submodule -dirty via git_submodule_status), (raw format path-pointer
/// identity). </summary>
public sealed class DiffMedParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public DiffMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed2_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async ValueTask<GitRepository> InitRepoAsync(string dir)
        => await GitRepository.InitAsync(dir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

    private static async Task<GitOid> CommitAllAsync(GitRepository repo, string message, GitOid? firstParent = null, string updateRef = "refs/heads/master")
    {
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = firstParent is { } p ? [p] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = updateRef,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── apply failures are GitException(ApplyFail, Patch) ─────

    [Fact]
    public async Task PatchApply_RemovalPatchOnNonEmptySource_ApplyFailCategory()
    {
        string patchText =
            "diff --git a/f.txt b/f.txt\n" +
            "deleted file mode 100644\n" +
            "index e69de29..0000000\n" +
            "--- a/f.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-removed\n";

        var patch = GitPatch.FromBuffer(patchText);

        // Source does not match the deleted content → "removal patch leaves file contents" → GIT_EAPPLYFAIL / GIT_ERROR_PATCH.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitPatchApplier.ApplyPatchAsync("other\n"u8.ToArray(), patch, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);
        Assert.Equal(GitErrorCategory.Patch, ex.Category);
    }

    [Fact]
    public async Task PatchApply_MatchingDeletion_Succeeds()
    {
        string patchText =
            "diff --git a/f.txt b/f.txt\n" +
            "deleted file mode 100644\n" +
            "index e69de29..0000000\n" +
            "--- a/f.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-removed\n";

        var patch = GitPatch.FromBuffer(patchText);
        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync("removed\n"u8.ToArray(), patch, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Content.Length);
    }

    // ── find-options default is BY_CONFIG ──────────────────────

    [Fact]
    public void DiffFindOptions_DefaultFlags_ByConfig()
    {
        // C: GIT_DIFF_FIND_OPTIONS_INIT zero-initializes flags to GIT_DIFF_FIND_BY_CONFIG (0) — diff.renames decides.
        Assert.Equal(GitDiffFindFlags.ByConfig, new GitDiffFindOptions().Flags);
        Assert.Equal(GitDiffFindFlags.ByConfig, GitDiffFindOptions.Default.Flags);
    }

    [Fact]
    public async Task FindSimilar_ByConfigRenamesFalse_NoRename()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        string workdir = repo.Workdir!;

        await File.WriteAllTextAsync(Path.Combine(workdir, "a.txt"), "content\n", TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("a.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree1 = await CommitAllAsync(repo, "one");

        File.Move(Path.Combine(workdir, "a.txt"), Path.Combine(workdir, "b.txt"));
        idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.RemoveByPath("a.txt");
        await idx.AddByPathAsync("b.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree2 = await CommitAllAsync(repo, "two", firstParent: tree1);

        await repo.Config.SetStringAsync("diff.renames", "false", TestContext.Current.CancellationToken);

        using GitTree t1 = (await repo.ObjectLookupAsync<GitTree>((await repo.ObjectLookupAsync<Commit>(tree1, TestContext.Current.CancellationToken))!.Tree, TestContext.Current.CancellationToken))!;
        using GitTree t2 = (await repo.ObjectLookupAsync<GitTree>((await repo.ObjectLookupAsync<Commit>(tree2, TestContext.Current.CancellationToken))!.Tree, TestContext.Current.CancellationToken))!;
        using GitDiff diff = await repo.DiffTreeToTreeAsync(t1, t2, cancellationToken: TestContext.Current.CancellationToken);

        // ByConfig default + diff.renames=false → NO rename detection
        // (C: diff_tform.c:263-283).
        using GitDiff found = await diff.FindSimilarAsync(new GitDiffFindOptions(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(found.Deltas, d => d.Status == GitDeltaStatus.Renamed);
        Assert.Contains(found.Deltas, d => d.Status == GitDeltaStatus.Deleted);
        Assert.Contains(found.Deltas, d => d.Status == GitDeltaStatus.Added);
    }

    // ── CONFLICTED status char + raw/name-status skip ─────────

    [Fact]
    public void StatusChar_Conflicted_IsSpace()
    {
        // C (diff_print.c:126-144): no GIT_DELTA_CONFLICTED arm → default ' '.
        Assert.Equal(' ', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Conflicted));
        Assert.Equal('A', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Added));
    }

    [Fact]
    public async Task RawPrint_ConflictedDelta_Skipped()
    {
        var delta = new GitDiffDelta(
            GitDeltaStatus.Conflicted, 2,
            new GitDiffFile(default, GitPath.FromUtf8String("foo"), -1, GitFileMode.Regular),
            new GitDiffFile(default, GitPath.FromUtf8String("foo"), -1, GitFileMode.Regular));
        using GitDiff diff = new([delta], new GitDiffOptions());

        // C: code==' ' → the delta is omitted from raw/name-status output
        // (diff_print.c:226-230).
        string raw = await diff.ToBufferTextAsync(GitDiffPrintFormat.Raw, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, raw);
        string nameStatus = await diff.ToBufferTextAsync(GitDiffPrintFormat.NameStatus, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, nameStatus);
    }

    // ── parsed-diff raw output prints both paths ──────────────

    [Fact]
    public async Task RawPrint_ParsedModifiedDiff_PrintsBothPaths()
    {
        // C (patch_parse.c:1033-1044 + diff_print.c:261-267): a parsed diff's
        // old/new paths are separate allocations — the pointer comparison
        // differs even though the strings are equal → "M\tfoo foo".
        string patchText =
            "diff --git a/foo b/foo\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/foo\n" +
            "+++ b/foo\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n";

        using var diff = GitDiff.FromBuffer(patchText);

        string raw = await diff.ToBufferTextAsync(GitDiffPrintFormat.Raw, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("\tfoo foo", raw);
    }

    // ── diff.ignoresubmodules config ───────────────────────────

    [Fact]
    public async Task Diff_IgnoresSubmodulesConfig_Respected()
    {
        // A gitlink whose workdir HEAD differs from the index.
        string subDir = NewDir();
        GitOid subOid1, subOid2;
        await using (GitRepository sub = await InitRepoAsync(subDir))
        {
            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v1\n", TestContext.Current.CancellationToken);
            GitIndex sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            subOid1 = await CommitAllAsync(sub, "sub1");

            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v2\n", TestContext.Current.CancellationToken);
            sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            subOid2 = await CommitAllAsync(sub, "sub2", firstParent: subOid1);
        }

        string parentDir = NewDir();
        await using GitRepository repo = await InitRepoAsync(parentDir);
        string workdir = repo.Workdir!;

        // Point the workdir "sub" at the submodule repo via a .git file. The
        // submodule HEAD is subOid2 (the second commit) — the gitlink workdir
        // side differs from the index's subOid1.
        Directory.CreateDirectory(Path.Combine(workdir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "sub", ".git"), "gitdir: " + subDir + "/.git\n", TestContext.Current.CancellationToken);

        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid1, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        // Without config: the gitlink is Modified (subOid1 → subOid2).
        using (GitDiff diff = await repo.DiffIndexToWorkdirAsync(null, cancellationToken: TestContext.Current.CancellationToken))
        {
            GitDiffDelta subDelta = Assert.Single(diff.Deltas, d => d.NewFile.Path?.ToUtf8String() == "sub");
            Assert.NotEqual(GitDeltaStatus.Unmodified, subDelta.Status);
        }

        // C (diff_generate.c:560-569): diff.ignoresubmodules = all makes the gitlink unmodified — and unmodified deltas are not emitted without
        // INCLUDE_UNMODIFIED.
        await repo.Config.SetStringAsync("diff.ignoresubmodules", "all", TestContext.Current.CancellationToken);
        using (GitDiff diff = await repo.DiffIndexToWorkdirAsync(null, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.DoesNotContain(diff.Deltas, d => d.NewFile.Path?.ToUtf8String() == "sub");
        }
    }

    // ── submodule -dirty honors the ignore policy ──────────────

    [Fact]
    public async Task SubmoduleDirty_IgnoreUntrackedConfig_NoDirtySuffix()
    {
        // A submodule with untracked files: with ignore=untracked in
        // .gitmodules, C's GIT_SUBMODULE_STATUS_IS_WD_DIRTY (via
        // git_submodule_status with IGNORE_UNSPECIFIED) is false; the raw
        // index→workdir diff would report -dirty.
        string subDir = NewDir();
        GitOid subOid1;
        await using (GitRepository sub = await InitRepoAsync(subDir))
        {
            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v1\n", TestContext.Current.CancellationToken);
            GitIndex sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            subOid1 = await CommitAllAsync(sub, "sub1");
        }

        string parentDir = NewDir();
        await using GitRepository repo = await InitRepoAsync(parentDir);
        string workdir = repo.Workdir!;

        Directory.CreateDirectory(Path.Combine(workdir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "sub", ".git"), "gitdir: " + subDir + "/.git\n", TestContext.Current.CancellationToken);

        // .gitmodules: ignore = untracked.
        await File.WriteAllTextAsync(
            Path.Combine(workdir, ".gitmodules"),
            "[submodule \"sub\"]\n\tpath = sub\n\turl = " + subDir + "\n\tignore = untracked\n",
            TestContext.Current.CancellationToken);

        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry(".gitmodules", GitOid.Empty, GitFileMode.Regular));
        idx.Add(new GitIndexEntry("sub", subOid1, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        // Untracked file inside the submodule workdir.
        await File.WriteAllTextAsync(Path.Combine(subDir, "untracked.txt"), "u\n", TestContext.Current.CancellationToken);

        // C: with ignore=untracked, untracked files do NOT make the submodule dirty → the placeholder has NO -dirty suffix.
        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(null, cancellationToken: TestContext.Current.CancellationToken);
        string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("-dirty", patch);
    }
}

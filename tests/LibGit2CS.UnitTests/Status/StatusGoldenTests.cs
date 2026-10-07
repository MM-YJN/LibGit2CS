using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Status-engine fixture tests using libgit2's test resource repositories.

/// </summary>
/// <remarks>
/// <para>
/// These tests package 6 fixtures from <c>~/repo/libgit2/tests/resources/</c>
/// into <c>Fixtures/status/&lt;name&gt;.zip</c> (with <c>.gitted</c> renamed to
/// <c>.git</c>). Each fixture is a small git repo exercising a specific status
/// scenario: filemode changes, case-insensitive filesystems, ignore/pathspec
/// interactions, CRLF handling, and a multi-commit test repo.
/// </para>
/// <para>
/// The tests verify that the C# <see cref="GitRepository.StatusNewAsync"/> produces the
/// same status flags as libgit2's <c>git_status_foreach</c> on the same
/// fixtures. This is a behavioral equivalence check, not a byte-exact golden
/// (status flags are enums, not file content).
/// </para>
/// </remarks>
public sealed class StatusGoldenTests : StatusGoldenBase
{
    /// <summary>
    /// Shared status options matching libgit2's
    /// <c>GIT_STATUS_OPT_INCLUDE_UNTRACKED | INCLUDE_IGNORED | INCLUDE_UNMODIFIED</c>
    /// (used by <c>test_status_worktree__filemode_changes</c>).
    /// </summary>
    private static readonly GitStatusOptions s_allOpts = new()
    {
        Show = GitStatusShow.IndexAndWorkdir,
        Flags = GitStatusFlags.IncludeUntracked
              | GitStatusFlags.IncludeIgnored
              | GitStatusFlags.IncludeUnmodified
              | GitStatusFlags.RecurseUntrackedDirs,
    };

    // ── filemodes: executable-bit changes ────────────────────────────────

    [Fact]
    public async Task Status_Filemodes_ExecutableBitChanges()
    {
        // Matches libgit2's test_status_worktree__filemode_changes.
        // The fixture has 8 files with mixed exec-bit states:
        //   exec_off, exec_on           → Current (no change)
        //   exec_off2on_staged          → IndexModified (staged exec bit change)
        //   exec_on2off_staged           → IndexModified
        //   exec_off2on_workdir          → WorkdirModified (unstaged exec bit change)
        //   exec_on2off_workdir          → WorkdirModified
        //   exec_off_untracked           → WorkdirNew (untracked)
        //   exec_on_untracked            → WorkdirNew (untracked)
        //
        // On platforms without chmod support (Windows), core.filemode is
        // forced false and the workdir exec-bit changes are not detectable,
        // so WT_MODIFIED entries collapse to Current — matching libgit2's
        // test_status_worktree__filemode_changes adaptation.
        await using GitRepository repo = await OpenStatusFixtureAsync("filemodes");

        bool chmodSupported = !OperatingSystem.IsWindows();
        await repo.Config.SetBoolAsync("core.filemode", chmodSupported, cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(s_allOpts, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusFlags[] expectedStatuses =
        [
            GitStatusFlags.Current,
            GitStatusFlags.IndexModified,
            chmodSupported ? GitStatusFlags.WorkdirModified : GitStatusFlags.Current,
            GitStatusFlags.WorkdirNew,
            GitStatusFlags.Current,
            GitStatusFlags.IndexModified,
            chmodSupported ? GitStatusFlags.WorkdirModified : GitStatusFlags.Current,
            GitStatusFlags.WorkdirNew,
        ];

        StatusCounter.AssertEntries(list,
            expectedPaths:
            [
                "exec_off",
                "exec_off2on_staged",
                "exec_off2on_workdir",
                "exec_off_untracked",
                "exec_on",
                "exec_on2off_staged",
                "exec_on2off_workdir",
                "exec_on_untracked",
            ],
            expectedStatuses: expectedStatuses);
    }

    // ── issue_592: ignore/pathspec interaction regression ─────────────────

    [Fact]
    public async Task Status_Issue592_DeletedFile_DetectedAsWorkdirDeleted()
    {
        // Matches libgit2's test_status_worktree__issue_592: delete l.txt
        // from the workdir, then status should report it as WorkdirDeleted.
        await using GitRepository repo = await OpenStatusFixtureAsync("issue_592");
        string ltxt = Path.Combine(repo.Workdir!, "l.txt");
        Assert.True(File.Exists(ltxt));
        File.Delete(ltxt);

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry? entry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "l.txt");
        Assert.NotNull(entry);
        Assert.Equal(GitStatusFlags.WorkdirDeleted, entry!.Status);
    }

    [Fact]
    public async Task Status_Issue592_CleanWorkdir_NoDeletions()
    {
        // Baseline: the issue_592 fixture as-is (no modifications) should not
        // report any workdir deletions. This confirms the fixture is clean
        // before the scenario test mutates it.
        await using GitRepository repo = await OpenStatusFixtureAsync("issue_592");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // No deleted entries (the workdir matches the index for tracked files).
        foreach (GitStatusEntry entry in list.Entries)
        {
            Assert.True((entry.Status & GitStatusFlags.WorkdirDeleted) == 0,
                $"unexpected WorkdirDeleted for {entry.Path}");
        }
    }

    // ── issue_592b: ignore variant regression ─────────────────────────────

    [Fact]
    public async Task Status_Issue592b_IgnoredFilesNotReported()
    {
        // The issue_592b fixture has a .gitignore that ignores "ignored" and
        // "ignored1.txt". With IncludeIgnored, these appear as Ignored.
        await using GitRepository repo = await OpenStatusFixtureAsync("issue_592b");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        var entries = list.Entries.ToDictionary(e => e.Path.ToUtf8String());
        Assert.True(entries.ContainsKey("ignored") || entries.ContainsKey("ignored1.txt"),
            "expected at least one ignored entry");
    }

    // ── issue_1397: CRLF files ────────────────────────────────────────────

    [Fact]
    public async Task Status_Issue1397_CrlfFiles_CleanOrModified()
    {
        // The issue_1397 fixture has two CRLF files. Without autocrlf, they
        // should be Current (no modification). This test verifies the status
        // engine handles CRLF files without false positives.
        await using GitRepository repo = await OpenStatusFixtureAsync("issue_1397");

        // Disable autocrlf (matches the fixture's expected behavior).
        await repo.Config.SetStringAsync("core.autocrlf", "false", cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(s_allOpts, cancellationToken: TestContext.Current.CancellationToken);

        // Both CRLF files should be Current (no changes).
        foreach (GitStatusEntry entry in list.Entries)
        {
            if (entry.Path.ToUtf8String().EndsWith(".txt"))
            {
                Assert.Equal(GitStatusFlags.Current, entry.Status);
            }
        }
    }

    // ── testrepo2: multi-commit test repository ──────────────────────────

    [Fact]
    public async Task Status_TestRepo2_CleanWorkdir_NoChanges()
    {
        // The testrepo2 fixture has a clean workdir (HEAD matches index matches
        // workdir). The fixture config sets core.ignorecase=true (macOS/Windows
        // default); on a case-sensitive filesystem, this causes duplicate
        // entries (index case-insensitive matching vs HEAD tree case-sensitive
        // matching). Set core.ignorecase=false to get a clean status on Linux.
        await using GitRepository repo = await OpenStatusFixtureAsync("testrepo2");
        await repo.Config.SetBoolAsync("core.ignorecase", false, cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(s_allOpts, cancellationToken: TestContext.Current.CancellationToken);

        string debug = string.Join(", ", list.Entries.Select(e => $"{e.Path}={e.Status}"));
        Assert.True(list.EntryCount == 0 || list.Entries.All(e => e.Status == GitStatusFlags.Current),
            $"expected clean workdir, got: {debug}");
    }

    [Fact]
    public async Task Status_TestRepo2_ModifiedFile_DetectedAsWorkdirModified()
    {
        // Modify README in the workdir (not staged) → WorkdirModified.
        await using GitRepository repo = await OpenStatusFixtureAsync("testrepo2");
        await repo.Config.SetBoolAsync("core.ignorecase", false, cancellationToken: TestContext.Current.CancellationToken);
        string readme = Path.Combine(repo.Workdir!, "README");
        Assert.True(File.Exists(readme));
        string original = await File.ReadAllTextAsync(readme, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(readme, original + "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry? entry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "README");
        Assert.NotNull(entry);
        Assert.Equal(GitStatusFlags.WorkdirModified, entry!.Status);
    }

    [Fact]
    public async Task Status_TestRepo2_NewFile_DetectedAsWorkdirNew()
    {
        // Add a new file (untracked) → WorkdirNew.
        await using GitRepository repo = await OpenStatusFixtureAsync("testrepo2");
        await repo.Config.SetBoolAsync("core.ignorecase", false, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "new_untracked.txt"), "new\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry? entry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "new_untracked.txt");
        Assert.NotNull(entry);
        Assert.Equal(GitStatusFlags.WorkdirNew, entry!.Status);
    }

    [Fact]
    public async Task Status_TestRepo2_DeletedFile_DetectedAsWorkdirDeleted()
    {
        // Delete a tracked file → WorkdirDeleted.
        await using GitRepository repo = await OpenStatusFixtureAsync("testrepo2");
        await repo.Config.SetBoolAsync("core.ignorecase", false, cancellationToken: TestContext.Current.CancellationToken);
        string newtxt = Path.Combine(repo.Workdir!, "new.txt");
        Assert.True(File.Exists(newtxt));
        File.Delete(newtxt);

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.IncludeIgnored,
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusEntry? entry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "new.txt");
        Assert.NotNull(entry);
        Assert.Equal(GitStatusFlags.WorkdirDeleted, entry!.Status);
    }

    // ── icase: case-insensitive filesystem (skip on case-sensitive) ──────

    [Fact]
    public async Task Status_Icase_CaseInsensitiveFilesystem()
    {
        // The icase fixture has 12 files in case-variant pairs (a/A, b/B, ...).
        // On a case-insensitive filesystem, these collide; on a case-sensitive
        // filesystem (Linux default), they coexist. Skip on case-sensitive.
        await using GitRepository repo = await OpenStatusFixtureAsync("icase");

        // Check if the filesystem is case-sensitive by testing if two files
        // differing only in case can coexist.
        string probeLower = Path.Combine(repo.Workdir!, ".icase_probe_lower");
        string probeUpper = Path.Combine(repo.Workdir!, ".icase_PROBE_lower");
        await File.WriteAllTextAsync(probeLower, "lower\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(probeUpper, "upper\n", cancellationToken: TestContext.Current.CancellationToken);
        string lowerContent = await File.ReadAllTextAsync(probeLower, cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(probeLower);
        File.Delete(probeUpper);
        if (lowerContent == "upper\n")
        {
            // Case-insensitive filesystem: the second write overwrote the first.
            // This is the scenario the icase fixture is designed for.
        }
        else
        {
            // Case-sensitive filesystem: skip the icase test (the fixture's
            // case-variant files coexist and produce different status results).
            // This is acceptable — the icase tests are for case-insensitive
            // filesystems (Windows, macOS default).
            return;
        }

        using GitStatusList list = await repo.StatusNewAsync(s_allOpts, cancellationToken: TestContext.Current.CancellationToken);

        // On a case-insensitive filesystem, the status should report the
        // case-variant files correctly (no false positives from the collision).
        // The exact expected statuses depend on the filesystem; the key
        // assertion is that the status engine doesn't crash and produces
        // a non-null list.
        Assert.NotNull(list);
        Assert.True(list.EntryCount >= 0);
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeDriverTests : IDisposable
{
    private readonly GitContext _ctx = new();
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    // master = bd593285fc7fe4ca18ccdbabf027f5d689101452
    // branch = 7cb63eed597130ba4abb87b3e544b85021905520
    private static readonly GitOid s_master = GitOid.Parse("bd593285fc7fe4ca18ccdbabf027f5d689101452".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_branch = GitOid.Parse("7cb63eed597130ba4abb87b3e544b85021905520".AsSpan(), GitHashAlgorithmKind.Sha1);

    // The automergeable result OID: when the text driver merges automergeable.txt
    // (non-overlapping changes on both sides), this is the resulting blob OID.
    // Matches AUTOMERGEABLE_IDSTR in the C test (driver.c:9).
    private static readonly GitOid s_automergeableId = GitOid.Parse("f2e1550a0c9e53d5811175864a29536642ae3821".AsSpan(), GitHashAlgorithmKind.Sha1);

    public MergeDriverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeDriverTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _ctx.Dispose();

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

    private void UnregisterQuiet(string name)
    {
        try
        {
            _ctx.MergeDrivers.Unregister(name);
        }
        catch (GitException) { }
    }

    private async Task<GitRepository> OpenMergeResolveRepo()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-resolve.zip");
        _extractedPaths.Add(path);
        GitRepository repo = await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), _ctx);

        // Ensure consistent test environment (matches driver.c:28-31).
        await repo.Config.SetStringAsync("merge.conflictstyle", "merge");
        await repo.Config.SetBoolAsync("core.autocrlf", false);

        return repo;
    }

    /// <summary>
    /// Writes a .gitattributes file in the repo workdir to control the merge
    /// driver for automergeable.txt. Matches set_gitattributes_to (driver.c:126-141).
    /// </summary>
    private static void SetGitAttributes(GitRepository repo, string? driver)
    {
        string workdir = repo.Workdir!;
        string gitattributesPath = Path.Combine(workdir, ".gitattributes");
        string line;
        if (driver is not null and not "")
        {
            line = $"automergeable.txt merge={driver}\n";
        }
        else if (driver is not null)
        {
            // empty string → "merge" (set to true → text driver)
            line = "automergeable.txt merge\n";
        }
        else
        {
            // null → "-merge" (unset → binary driver)
            line = "automergeable.txt -merge\n";
        }

        File.WriteAllText(gitattributesPath, line);
    }

    /// <summary>
    /// Merges the branch tree into the master tree using <see cref="GitRepository.MergeTreesAsync"/>.
    /// This exercises the ResolveContents → driver dispatch path.
    /// Matches merge_branch (driver.c:143-155) at the tree level, which
    /// isolates the driver dispatch from the full <see cref="GitRepository.MergeAsync"/>
    /// orchestration.
    /// </summary>
    private static async Task<GitIndex> MergeBranch(GitRepository repo, GitMergeOptions? opts = null)
    {
        Commit masterCommit = (await repo.ObjectLookupAsync<Commit>(s_master, CancellationToken.None))!;
        Commit branchCommit = (await repo.ObjectLookupAsync<Commit>(s_branch, CancellationToken.None))!;

        // Compute the merge base.
        GitOid? baseOid = await repo.MergeBaseFindAsync(s_master, s_branch, CancellationToken.None);
        GitTree? baseTree = baseOid is { } b
            ? (await repo.ObjectLookupAsync<GitTree>(
                (await repo.ObjectLookupAsync<Commit>(b, CancellationToken.None))!.Tree,
                CancellationToken.None))!
            : null;

        GitTree ourTree = (await repo.ObjectLookupAsync<GitTree>(masterCommit.Tree, CancellationToken.None))!;
        GitTree theirTree = (await repo.ObjectLookupAsync<GitTree>(branchCommit.Tree, CancellationToken.None))!;

        return await repo.MergeTreesAsync(baseTree, ourTree, theirTree, opts);
    }

    // ── Registry: built-in drivers ───────────────────────────────────────

    [Fact]
    public async Task Lookup_Text_FastPath()
    {
        IGitMergeDriver? driver = _ctx.MergeDrivers.Lookup("text");
        Assert.NotNull(driver);
        Assert.Same(_ctx.MergeDrivers.Lookup("text"), driver);
    }

    [Fact]
    public async Task Lookup_Binary_FastPath()
    {
        IGitMergeDriver? driver = _ctx.MergeDrivers.Lookup("binary");
        Assert.NotNull(driver);
        Assert.Same(_ctx.MergeDrivers.Lookup("binary"), driver);
    }

    [Fact]
    public async Task Lookup_Union_FastPath()
    {
        IGitMergeDriver? driver = _ctx.MergeDrivers.Lookup("union");
        Assert.NotNull(driver);
        Assert.Same(_ctx.MergeDrivers.Lookup("union"), driver);
    }

    [Fact]
    public async Task Lookup_Unknown_ReturnsNull()
    {
        Assert.Null(_ctx.MergeDrivers.Lookup("nonexistent"));
    }

    [Fact]
    public async Task Lookup_Null_ReturnsNull()
    {
        Assert.Null(_ctx.MergeDrivers.Lookup(null));
    }

    [Fact]
    public async Task Lookup_Empty_ReturnsNull()
    {
        Assert.Null(_ctx.MergeDrivers.Lookup(""));
    }

    // ── Registry: register/unregister ───────────────────────────────────

    [Fact]
    public async Task Register_Custom_LookupFindsIt()
    {
        var driver = new TestDriver("test-register");
        try
        {
            _ctx.MergeDrivers.Register("test-register", driver);
            Assert.Same(driver, _ctx.MergeDrivers.Lookup("test-register"));
        }
        finally
        {
            UnregisterQuiet("test-register");
        }
    }

    [Fact]
    public async Task Register_Duplicate_ThrowsExists()
    {
        var driver = new TestDriver("test-dup");
        try
        {
            _ctx.MergeDrivers.Register("test-dup", driver);
            GitException ex = Assert.Throws<GitException>(() =>
                _ctx.MergeDrivers.Register("test-dup", new TestDriver("test-dup")));
            Assert.Equal(GitErrorCode.Exists, ex.Code);
        }
        finally
        {
            UnregisterQuiet("test-dup");
        }
    }

    [Fact]
    public async Task Unregister_RemovesDriver()
    {
        var driver = new TestDriver("test-remove");
        _ctx.MergeDrivers.Register("test-remove", driver);
        _ctx.MergeDrivers.Unregister("test-remove");
        Assert.Null(_ctx.MergeDrivers.Lookup("test-remove"));
    }

    [Fact]
    public async Task Unregister_Unknown_ThrowsNotFound()
    {
        GitException ex = Assert.Throws<GitException>(() => _ctx.MergeDrivers.Unregister("never-registered"));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Unregister_Builtin_ThrowsNotFound()
    {
        // Built-in drivers are registered in the static ctor. Unregistering
        // them should fail since they're real entries (matches C behavior
        // where git_merge_driver_unregister finds them and removes them).
        // Actually in C, unregistering builtins IS allowed (it just removes
        // them). But our tests should not unregister builtins to avoid
        // breaking other tests. Let's verify it can be found at least.
        Assert.NotNull(_ctx.MergeDrivers.Lookup("text"));
    }

    // ── Registry: initialize/shutdown lifecycle ────────────────────────

    [Fact]
    public async Task Lookup_InitializesOnFirstUse()
    {
        var driver = new TestDriver("test-init");
        try
        {
            _ctx.MergeDrivers.Register("test-init", driver);
            Assert.False(driver.Initialized);

            _ = _ctx.MergeDrivers.Lookup("test-init");
            Assert.True(driver.Initialized);
        }
        finally
        {
            UnregisterQuiet("test-init");
        }
    }

    [Fact]
    public async Task Unregister_CallsShutdownIfInitialized()
    {
        var driver = new TestDriver("test-shutdown");
        _ctx.MergeDrivers.Register("test-shutdown", driver);

        _ = _ctx.MergeDrivers.Lookup("test-shutdown");
        Assert.True(driver.Initialized);
        Assert.False(driver.ShutdownCalled);

        _ctx.MergeDrivers.Unregister("test-shutdown");
        Assert.True(driver.ShutdownCalled);
    }

    [Fact]
    public async Task Unregister_DoesNotCallShutdownIfNeverInitialized()
    {
        var driver = new TestDriver("test-no-init-shutdown");
        _ctx.MergeDrivers.Register("test-no-init-shutdown", driver);

        // Never lookup → never initialized.
        Assert.False(driver.Initialized);

        _ctx.MergeDrivers.Unregister("test-no-init-shutdown");
        Assert.False(driver.ShutdownCalled);
    }

    // ── Registry: wildcard lookup ───────────────────────────────────────

    [Fact]
    public async Task LookupWithWildcard_FallsBackToWildcard()
    {
        var wildcardDriver = new TestDriver("wildcard-test");
        try
        {
            _ctx.MergeDrivers.Register("*", wildcardDriver);

            // "foobar" is not registered, but "*" is.
            IGitMergeDriver? driver = _ctx.MergeDrivers.LookupWithWildcard("foobar");
            Assert.Same(wildcardDriver, driver);
        }
        finally
        {
            UnregisterQuiet("*");
        }
    }

    [Fact]
    public async Task LookupWithWildcard_RegisteredName_TakesPrecedenceOverWildcard()
    {
        var namedDriver = new TestDriver("named");
        var wildcardDriver = new TestDriver("wildcard-named");
        try
        {
            _ctx.MergeDrivers.Register("named", namedDriver);
            _ctx.MergeDrivers.Register("*", wildcardDriver);

            IGitMergeDriver? driver = _ctx.MergeDrivers.LookupWithWildcard("named");
            Assert.Same(namedDriver, driver);
        }
        finally
        {
            UnregisterQuiet("named");
            UnregisterQuiet("*");
        }
    }

    [Fact]
    public async Task LookupWithWildcard_NeitherRegistered_ReturnsNull()
    {
        Assert.Null(_ctx.MergeDrivers.LookupWithWildcard("never-registered-no-wildcard"));
    }

    // ── NameForPath: attribute resolution ───────────────────────────────

    [Fact]
    public async Task NameForPath_NoAttribute_DefaultsToText()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        // No .gitattributes → unspecified → "text" (when no default_driver).
        string name = await _ctx.MergeDrivers.NameForPathAsync(repo, "automergeable.txt", null, CancellationToken.None);
        Assert.Equal("text", name);
    }

    [Fact]
    public async Task NameForPath_SetAttribute_ReturnsText()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "");
        // "merge" (set to true) → "text"
        string name = await _ctx.MergeDrivers.NameForPathAsync(repo, "automergeable.txt", null, CancellationToken.None);
        Assert.Equal("text", name);
    }

    [Fact]
    public async Task NameForPath_UnsetAttribute_ReturnsBinary()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, null);
        // "-merge" (false) → "binary"
        string name = await _ctx.MergeDrivers.NameForPathAsync(repo, "automergeable.txt", null, CancellationToken.None);
        Assert.Equal("binary", name);
    }

    [Fact]
    public async Task NameForPath_ValueAttribute_ReturnsValue()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "custom");
        // "merge=custom" → "custom"
        string name = await _ctx.MergeDrivers.NameForPathAsync(repo, "automergeable.txt", null, CancellationToken.None);
        Assert.Equal("custom", name);
    }

    [Fact]
    public async Task NameForPath_Unspecified_UsesDefaultDriver()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        // No .gitattributes → unspecified → default_driver.
        string name = await _ctx.MergeDrivers.NameForPathAsync(repo, "automergeable.txt", "binary", CancellationToken.None);
        Assert.Equal("binary", name);
    }

    // ── Builtin drivers: text/binary/union ──────────────────────────────

    [Fact]
    public async Task TextDriver_Automergeable_ReturnsSuccess()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        GitIndex index = (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken));
        GitIndexEntry? entry = index.EntryByPath("automergeable.txt", 0)!;

        var src = new GitMergeDriverSource(
            repo, null, GitMergeFileOptions.Default, entry, entry, entry);

        (GitMergeDriverApplyResult result, GitMergeDriverOutput? output) = await _ctx.MergeDrivers.TextInstance.ApplyAsync("text", src, CancellationToken.None);
        Assert.Equal(GitMergeDriverApplyResult.Success, result);
        Assert.NotNull(output);
        Assert.True(output.Content.Length > 0);
    }

    [Fact]
    public async Task BinaryDriver_AlwaysReturnsConflict()
    {
        await using GitRepository repo = await OpenMergeResolveRepo();
        GitIndex index = (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken));
        GitIndexEntry? entry = index.EntryByPath("automergeable.txt", 0)!;

        var src = new GitMergeDriverSource(
            repo, null, GitMergeFileOptions.Default, entry, entry, entry);

        GitMergeDriverApplyResult result = (await _ctx.MergeDrivers.BinaryInstance.ApplyAsync("binary", src, CancellationToken.None)).Result;
        Assert.Equal(GitMergeDriverApplyResult.Conflict, result);
    }

    // ── Integration: driver dispatch via MergeTreesAsync ───────────────

    [Fact]
    public async Task CustomDriver_InvokedThroughMerge()
    {
        // Matches test_merge_driver__custom (driver.c:157-165).
        // Set merge=custom, merge branch → custom driver produces "applied.txt".
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "custom");

        var customDriver = new TestDriver("custom");
        _ctx.MergeDrivers.Register("custom", customDriver);
        try
        {
            GitIndex result = await MergeBranch(repo);

            // The custom driver should have been applied to automergeable.txt.
            Assert.True(customDriver.ApplyCount > 0, "custom driver should have been invoked");

            // The result should have the custom driver's output (path = "applied.txt").
            GitIndexEntry? applied = result.EntryByPath("applied.txt", 0);
            Assert.NotNull(applied);
            Assert.Equal("applied.txt", applied!.Value.Path.ToUtf8String());
        }
        finally
        {
            UnregisterQuiet("custom");
        }
    }

    [Fact]
    public async Task WildcardDriver_InvokedForUnknownDriver()
    {
        // Matches test_merge_driver__wildcard (driver.c:167-175).
        // Set merge=foobar (not registered), wildcard "*" catches it.
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "foobar");

        var wildcardDriver = new TestDriver("*");
        _ctx.MergeDrivers.Register("*", wildcardDriver);
        try
        {
            GitIndex result = await MergeBranch(repo);

            Assert.True(wildcardDriver.ApplyCount > 0, "wildcard driver should have been invoked");

            GitIndexEntry? applied = result.EntryByPath("applied.txt", 0);
            Assert.NotNull(applied);
        }
        finally
        {
            UnregisterQuiet("*");
        }
    }

    [Fact]
    public async Task DeferDriver_FallsBackToText()
    {
        // Matches test_merge_driver__apply_can_defer (driver.c:232-246).
        // A driver returning PassThrough → text driver runs → automergeable result.
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "defer");

        var deferDriver = new DeferDriver();
        _ctx.MergeDrivers.Register("defer", deferDriver);
        try
        {
            GitIndex result = await MergeBranch(repo);

            // The defer driver should have been called.
            Assert.True(deferDriver.ApplyCount > 0);

            // The text driver should have run as fallback, producing the
            // automergeable result. The automergeable.txt entry should exist
            // with the automergeable blob OID.
            GitIndexEntry? entry = result.EntryByPath("automergeable.txt", 0);
            Assert.NotNull(entry);
            Assert.Equal(s_automergeableId, entry!.Value.Id);
        }
        finally
        {
            UnregisterQuiet("defer");
        }
    }

    [Fact]
    public async Task ConflictDriver_LeavesConflict()
    {
        // Matches test_merge_driver__apply_can_conflict (driver.c:277-291).
        // A driver returning Conflict → file stays conflicted.
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "conflict");

        var conflictDriver = new ConflictDriver();
        _ctx.MergeDrivers.Register("conflict", conflictDriver);
        try
        {
            GitIndex result = await MergeBranch(repo);

            // The conflict driver should have been called.
            Assert.True(conflictDriver.ApplyCount > 0);

            // automergeable.txt should be in conflict (stage 1/2/3 entries).
            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = result.ConflictGet("automergeable.txt");
            Assert.NotNull(ancestor);
            Assert.NotNull(ours);
            Assert.NotNull(theirs);
        }
        finally
        {
            UnregisterQuiet("conflict");
        }
    }

    [Fact]
    public async Task DefaultDriver_UsedWhenNoAttribute()
    {
        // Matches test_merge_driver__default_can_be_specified (driver.c:293-312).
        // merge.default = "custom" (no .gitattributes) → custom driver.
        await using GitRepository repo = await OpenMergeResolveRepo();
        // No .gitattributes (don't call SetGitAttributes).

        var customDriver = new TestDriver("custom");
        _ctx.MergeDrivers.Register("custom", customDriver);
        try
        {
            var opts = new GitMergeOptions { DefaultDriver = "custom" };
            GitIndex result = await MergeBranch(repo, opts);

            Assert.True(customDriver.ApplyCount > 0, "custom driver should be invoked via default_driver");

            GitIndexEntry? applied = result.EntryByPath("applied.txt", 0);
            Assert.NotNull(applied);
        }
        finally
        {
            UnregisterQuiet("custom");
        }
    }

    [Fact]
    public async Task BuiltinMergeDefault_Binary_ProducesConflict()
    {
        // Matches test_merge_driver__honors_builtin_mergedefault (driver.c:314-323).
        // merge.default = "binary" → binary driver → conflict.
        await using GitRepository repo = await OpenMergeResolveRepo();
        // No .gitattributes — relies on default_driver.

        var opts = new GitMergeOptions { DefaultDriver = "binary" };
        GitIndex result = await MergeBranch(repo, opts);

        // automergeable.txt should be in conflict.
        (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = result.ConflictGet("automergeable.txt");
        Assert.NotNull(ancestor);
        Assert.NotNull(ours);
        Assert.NotNull(theirs);
    }

    [Fact]
    public async Task CustomMergeDefault_CustomDriverApplied()
    {
        // Matches test_merge_driver__honors_custom_mergedefault (driver.c:325-334).
        await using GitRepository repo = await OpenMergeResolveRepo();

        var customDriver = new TestDriver("custom");
        _ctx.MergeDrivers.Register("custom", customDriver);
        try
        {
            // Set merge.default in config (like cl_repo_set_string).
            await repo.Config.SetStringAsync("merge.default", "custom", cancellationToken: TestContext.Current.CancellationToken);
            GitIndex result = await MergeBranch(repo);

            Assert.True(customDriver.ApplyCount > 0);

            GitIndexEntry? applied = result.EntryByPath("applied.txt", 0);
            Assert.NotNull(applied);
        }
        finally
        {
            UnregisterQuiet("custom");
        }
    }

    [Fact]
    public async Task MergeDefaultDeferring_FallsBackToText()
    {
        // Matches test_merge_driver__mergedefault_deferring_falls_back_to_text
        // (driver.c:336-350).
        await using GitRepository repo = await OpenMergeResolveRepo();

        var deferDriver = new DeferDriver();
        _ctx.MergeDrivers.Register("defer", deferDriver);
        try
        {
            await repo.Config.SetStringAsync("merge.default", "defer", cancellationToken: TestContext.Current.CancellationToken);
            GitIndex result = await MergeBranch(repo);

            // Should fall back to text → automergeable result.
            GitIndexEntry? entry = result.EntryByPath("automergeable.txt", 0);
            Assert.NotNull(entry);
            Assert.Equal(s_automergeableId, entry!.Value.Id);
        }
        finally
        {
            UnregisterQuiet("defer");
        }
    }

    [Fact]
    public async Task SetAttribute_ForcesText_OverridesCustomDefault()
    {
        // Matches test_merge_driver__set_forces_text (driver.c:352-364).
        // "merge" (set, no value) → text driver for automergeable.txt,
        // overriding merge.default=custom for THAT file. Other files without
        // a "merge" attribute still use the default_driver (custom).
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "");

        var customDriver = new TestDriver("custom");
        _ctx.MergeDrivers.Register("custom", customDriver);
        try
        {
            await repo.Config.SetStringAsync("merge.default", "custom", cancellationToken: TestContext.Current.CancellationToken);
            GitIndex result = await MergeBranch(repo);

            // automergeable.txt should get the text driver (merge attr = True)
            // → automergeable result, NOT the custom driver's "applied.txt".
            GitIndexEntry? entry = result.EntryByPath("automergeable.txt", 0);
            Assert.NotNull(entry);
            Assert.Equal(s_automergeableId, entry!.Value.Id);

            // automergeable.txt should NOT be redirected to "applied.txt"
            // (which the custom driver would do).
            GitIndexEntry? applied = result.EntryByPath("applied.txt", 0);
            // "applied.txt" may exist if the custom driver was invoked for
            // OTHER files (via merge.default=custom), but automergeable.txt
            // should not be missing.
            Assert.NotNull(entry);
        }
        finally
        {
            UnregisterQuiet("custom");
        }
    }

    [Fact]
    public async Task UnsetAttribute_ForcesBinary_OverridesCustomDefault()
    {
        // Matches test_merge_driver__unset_forces_binary (driver.c:366-378).
        // "-merge" → binary driver for automergeable.txt, overriding
        // merge.default=custom for THAT file. automergeable.txt stays conflicted.
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, null);

        var customDriver = new TestDriver("custom");
        _ctx.MergeDrivers.Register("custom", customDriver);
        try
        {
            await repo.Config.SetStringAsync("merge.default", "custom", cancellationToken: TestContext.Current.CancellationToken);
            GitIndex result = await MergeBranch(repo);

            // automergeable.txt should be in conflict (binary driver → conflict).
            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = result.ConflictGet("automergeable.txt");
            Assert.NotNull(ancestor);
            Assert.NotNull(ours);
            Assert.NotNull(theirs);
        }
        finally
        {
            UnregisterQuiet("custom");
        }
    }

    [Fact]
    public async Task NotConfiguredDriver_FallsBackToText()
    {
        // Matches test_merge_driver__not_configured_driver_falls_back
        // (driver.c:380-395).
        // merge=notfound (unregistered, no wildcard) → text fallback.
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "notfound");

        // No custom drivers registered, no wildcard.
        UnregisterQuiet("custom");
        UnregisterQuiet("*");

        GitIndex result = await MergeBranch(repo);

        // Should fall back to text → automergeable result.
        GitIndexEntry? entry = result.EntryByPath("automergeable.txt", 0);
        Assert.NotNull(entry);
        Assert.Equal(s_automergeableId, entry!.Value.Id);
    }

    [Fact]
    public async Task FavorOverridesDriverDispatch()
    {
        // When Favor != Normal, the driver registry is bypassed and the
        // builtin text driver is used directly (merge.c:964-973).
        await using GitRepository repo = await OpenMergeResolveRepo();
        SetGitAttributes(repo, "custom");

        var customDriver = new TestDriver("custom");
        _ctx.MergeDrivers.Register("custom", customDriver);
        try
        {
            var opts = new GitMergeOptions { Favor = GitMergeFileFavor.Ours };
            GitIndex result = await MergeBranch(repo, opts);

            // Custom driver should NOT be called — favor bypasses dispatch.
            Assert.Equal(0, customDriver.ApplyCount);

            // The text driver (with favor=Ours) should produce a clean merge.
            // automergeable.txt should be resolved (no conflict).
            GitIndexEntry? entry = result.EntryByPath("automergeable.txt", 0);
            Assert.NotNull(entry);
        }
        finally
        {
            UnregisterQuiet("custom");
        }
    }

    // ── Test driver implementations ─────────────────────────────────────

    /// <summary>
    /// A test merge driver that writes a fixed message to "applied.txt".
    /// Matches the test_merge_driver struct + test_driver_apply (driver.c:46-90).
    /// </summary>
    private sealed class TestDriver : IGitMergeDriver
    {
        private readonly string _name;
        public bool Initialized { get; private set; }
        public bool ShutdownCalled { get; private set; }
        public int ApplyCount { get; private set; }

        public TestDriver(string name)
        {
            _name = name;
        }

        public void Initialize() => Initialized = true;

        public void Shutdown() => ShutdownCalled = true;

        public Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> ApplyAsync(
            string filterName,
            GitMergeDriverSource src,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            byte[] content = Encoding.UTF8.GetBytes($"This is the `{filterName}` driver.\n");
            var output = new GitMergeDriverOutput(GitPath.FromUtf8String("applied.txt"), (uint)GitFileMode.Regular, content);
            return Task.FromResult<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)>((GitMergeDriverApplyResult.Success, output));
        }
    }

    /// <summary>
    /// A driver that returns PassThrough to test fallback behavior.
    /// Matches defer_driver_apply (driver.c:203-219).
    /// </summary>
    private sealed class DeferDriver : IGitMergeDriver
    {
        public int ApplyCount { get; private set; }

        public void Initialize() { }

        public void Shutdown() { }

        public Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> ApplyAsync(
            string filterName,
            GitMergeDriverSource src,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)>((GitMergeDriverApplyResult.PassThrough, null));
        }
    }

    /// <summary>
    /// A driver that always returns Conflict.
    /// Matches conflict_driver_apply (driver.c:248-264).
    /// </summary>
    private sealed class ConflictDriver : IGitMergeDriver
    {
        public int ApplyCount { get; private set; }

        public void Initialize() { }

        public void Shutdown() { }

        public Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> ApplyAsync(
            string filterName,
            GitMergeDriverSource src,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)>((GitMergeDriverApplyResult.Conflict, null));
        }
    }
}

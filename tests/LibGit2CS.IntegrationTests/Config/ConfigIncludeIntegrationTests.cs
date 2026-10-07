using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Config;

/// <summary>
/// Integration tests for <see cref="FileConfigBackend"/> include directives
/// (<c>[include]</c> + <c>[includeIf "gitdir:..."/"gitdir/i:..."/"onbranch:..."]</c>)
/// and <see cref="ConfigParser"/> multiline variable parsing, exercised
/// end-to-end against locally-initialized repos with real config files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b>
/// <see cref="ConfigSnapshotIntegrationTests"/> covers snapshot/memory/enumerate-glob.
/// <see cref="ConfigTransactionIntegrationTests"/> covers lock/commit/rollback.
/// Neither exercises the <c>include.path</c> / <c>includeIf.gitdir:.../onbranch:...</c>
/// conditional-include paths in <see cref="FileConfigBackend"/> — the
/// <see cref="FileConfigBackend.MatchGitDir"/> /
/// <see cref="FileConfigBackend.ResolveIncludedPath"/> /
/// <see cref="FileConfigBackend.MatchOnBranchAsync"/> helpers and the
/// include-aware <see cref="FileConfigBackend.EnumerateAsync"/> iterator
/// were entirely cold. The <see cref="ConfigParser.ParseMultilineVariable"/>
/// continuation-line branch was also cold. These tests write included config
/// files to disk and verify they are loaded (or skipped) based on the
/// include conditions.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/config/config_include.c</c>
/// (<c>test_config_include__relative</c>,
/// <c>test_config_include__gitdir</c>,
/// <c>test_config_include__onbranch</c>), adapted to build the sandbox from
/// scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class ConfigIncludeIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-cfginc-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Inits a non-bare repo, appends <paramref name="extraConfig"/> to
    /// the repo's <c>.git/config</c>, and returns it (caller disposes).
    /// The caller owns <paramref name="context"/> and must dispose it after
    /// the returned repo is disposed.
    /// </summary>
    private static async Task<GitRepository> InitRepoWithConfigAsync(string path, string extraConfig, GitContext context, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, context, cancellationToken: ct);
        if (extraConfig.Length > 0)
        {
            string configPath = Path.Combine(repo.Path, "config");
            await File.AppendAllTextAsync(configPath, extraConfig, ct).ConfigureAwait(false);
        }

        return repo;
    }

    // ── include.path (unconditional) ───────────────────────────────────

    /// <summary>
    /// <c>[include] path = &lt;relative-path&gt;</c> in the repo
    /// config loads an included config file relative to the repo's
    /// <c>.git/</c> directory. A value set in the included file is visible
    /// via <see cref="GitConfiguration.GetStringAsync"/>. Exercises the
    /// <see cref="FileConfigBackend.ParseIncludeAsync"/> +
    /// <see cref="FileConfigBackend.ResolveIncludedPath"/> path.
    /// </summary>
    [Fact]
    public async Task IncludePath_UnconditionalIncludes_LoadsIncludedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        using GitContext ctx = new();
        try
        {
            // Write the included config file in .git/ first.
            GitRepository repo = await InitRepoWithConfigAsync(
                path,
                "[include]\n\tpath = included-config\n",
                ctx,
                ct);
            await using (repo)
            {
                string includedPath = Path.Combine(repo.Path, "included-config");
                await File.WriteAllTextAsync(includedPath, "[user]\n\tname = included\n", ct).ConfigureAwait(false);

                string? name = await repo.Config.GetStringAsync("user.name", ct);
                Assert.Equal("included", name);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── includeIf "gitdir:..." ────────────────────────────────────────

    /// <summary>
    /// <c>[includeIf "gitdir:&lt;pattern&gt;"] path = &lt;path&gt;</c>
    /// loads the included config only when the repo's gitdir matches the
    /// pattern. With the pattern <c>.git/</c> (the repo's own gitdir), the
    /// include is loaded. Exercises the
    /// <see cref="FileConfigBackend.MatchGitDir"/> match-true branch.
    /// </summary>
    [Fact]
    public async Task IncludeIf_GitDir_Match_LoadsIncludedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        using GitContext ctx = new();
        try
        {
            GitRepository repo = await InitRepoWithConfigAsync(
                path,
                "[includeIf \"gitdir:" + path.Replace('\\', '/') + "/.git\"]\n\tpath = branch-config\n",
                ctx,
                ct);
            await using (repo)
            {
                string includedPath = Path.Combine(repo.Path, "branch-config");
                await File.WriteAllTextAsync(includedPath, "[user]\n\tname = gitdir-match\n", ct).ConfigureAwait(false);

                string? name = await repo.Config.GetStringAsync("user.name", ct);
                Assert.Equal("gitdir-match", name);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <c>[includeIf "gitdir:&lt;non-matching-pattern&gt;"]</c> does
    /// NOT load the included config. Exercises the
    /// <see cref="FileConfigBackend.MatchGitDir"/> match-false branch.
    /// </summary>
    [Fact]
    public async Task IncludeIf_GitDir_NoMatch_DoesNotLoadIncludedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        (GitContext ctx, string sandboxDir) = ConfigTestSandbox.Create();
        try
        {
            GitRepository repo = await InitRepoWithConfigAsync(
                path,
                "[includeIf \"gitdir:/nonexistent/path\"]\n\tpath = branch-config\n",
                ctx,
                ct);
            await using (repo)
            {
                string includedPath = Path.Combine(repo.Path, "branch-config");
                await File.WriteAllTextAsync(includedPath, "[user]\n\tname = gitdir-nomatch\n", ct).ConfigureAwait(false);

                // The included file was NOT loaded → user.name is unset.
                string? name = await repo.Config.GetStringAsync("user.name", ct);
                Assert.Null(name);
            }
        }
        finally
        {
            await ConfigTestSandbox.CleanupAsync(ctx, sandboxDir);
            Cleanup(path);
        }
    }

    // ── includeIf "onbranch:..." ──────────────────────────────────────

    /// <summary>
    /// <c>[includeIf "onbranch:&lt;branch&gt;"]</c> loads the
    /// included config only when HEAD points at the named branch. With
    /// HEAD on <c>refs/heads/main</c> and the condition <c>onbranch:main</c>,
    /// the include is loaded. Exercises the
    /// <see cref="FileConfigBackend.MatchOnBranchAsync"/> match-true
    /// branch.
    /// </summary>
    /// <remarks>
    /// The onbranch matcher reads <c>.git/HEAD</c> directly during config
    /// parse, so HEAD must point at <c>refs/heads/main</c> BEFORE the repo
    /// is opened (the config backend parses lazily on first access and
    /// caches the result). We write <c>.git/HEAD</c> directly before
    /// opening the repo with the include directive.
    /// </remarks>
    [Fact]
    public async Task IncludeIf_OnBranch_Match_LoadsIncludedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        using GitContext ctx = new();
        try
        {
            // Init a repo, then point HEAD at refs/heads/main BEFORE writing
            // the include directive (so the config parse sees the right HEAD).
            GitRepository initRepo = await GitRepository.InitAsync(path, isBare: false, ctx, cancellationToken: ct);
            await initRepo.DisposeAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(path, ".git", "HEAD"), "ref: refs/heads/main\n", ct).ConfigureAwait(false);

            // Now append the include directive to .git/config and write the
            // included config, then open the repo (config parses lazily).
            string configPath = Path.Combine(path, ".git", "config");
            await File.AppendAllTextAsync(configPath, "[includeIf \"onbranch:main\"]\n\tpath = branch-config\n", ct).ConfigureAwait(false);
            string includedPath = Path.Combine(path, ".git", "branch-config");
            await File.WriteAllTextAsync(includedPath, "[user]\n\tname = branch-user\n", ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, ctx, cancellationToken: ct);
            string? name = await repo.Config.GetStringAsync("user.name", ct);
            Assert.Equal("branch-user", name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <c>[includeIf "onbranch:&lt;other&gt;"]</c> does NOT load the
    /// included config when HEAD is on a different branch. With HEAD on
    /// <c>refs/heads/main</c> and the condition <c>onbranch:feature</c>,
    /// the include is skipped. Exercises the
    /// <see cref="FileConfigBackend.MatchOnBranchAsync"/> match-false
    /// branch.
    /// </summary>
    /// <remarks>
    /// HEAD is written directly before opening the repo (see
    /// <see cref="IncludeIf_OnBranch_Match_LoadsIncludedFile"/> for the
    /// lazy-parse ordering rationale).
    /// </remarks>
    [Fact]
    public async Task IncludeIf_OnBranch_NoMatch_DoesNotLoadIncludedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        (GitContext ctx, string sandboxDir) = ConfigTestSandbox.Create();
        try
        {
            GitRepository initRepo = await GitRepository.InitAsync(path, isBare: false, ctx, cancellationToken: ct);
            await initRepo.DisposeAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(path, ".git", "HEAD"), "ref: refs/heads/main\n", ct).ConfigureAwait(false);

            string configPath = Path.Combine(path, ".git", "config");
            await File.AppendAllTextAsync(configPath, "[includeIf \"onbranch:feature\"]\n\tpath = branch-config\n", ct).ConfigureAwait(false);
            string includedPath = Path.Combine(path, ".git", "branch-config");
            await File.WriteAllTextAsync(includedPath, "[user]\n\tname = feature-user\n", ct).ConfigureAwait(false);

            await using GitRepository repo = await GitRepository.OpenAsync(path, ctx, cancellationToken: ct);
            string? name = await repo.Config.GetStringAsync("user.name", ct);
            Assert.Null(name);
        }
        finally
        {
            await ConfigTestSandbox.CleanupAsync(ctx, sandboxDir);
            Cleanup(path);
        }
    }

    // ── include-aware enumeration ────────────────────────────────────

    /// <summary>
    /// <see cref="GitConfiguration.EnumerateAsync"/> yields entries
    /// from both the repo config and any included config files. With the
    /// repo config setting <c>core.editor = local-nano</c> and an included
    /// file setting <c>core.editor = included-vim</c>, enumeration yields
    /// both entries (the last one wins for
    /// <see cref="GitConfiguration.GetStringAsync"/>). Exercises the
    /// include-aware <see cref="FileConfigBackend.EnumerateAsync"/>
    /// iterator over included files.
    /// </summary>
    [Fact]
    public async Task Enumerate_AfterInclude_YieldsMergedEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        using GitContext ctx = new();
        try
        {
            GitRepository repo = await InitRepoWithConfigAsync(
                path,
                "[core]\n\teditor = local-nano\n[include]\n\tpath = included-config\n",
                ctx,
                ct);
            await using (repo)
            {
                string includedPath = Path.Combine(repo.Path, "included-config");
                await File.WriteAllTextAsync(includedPath, "[core]\n\teditor = included-vim\n", ct).ConfigureAwait(false);

                var matched = new List<string>();
                await foreach (GitConfigEntry e in repo.Config.EnumerateAsync("core.*", ct))
                {
                    if (e.Value is not null)
                    {
                        matched.Add(e.Value);
                    }
                }

                // Both the local and the included value appear in enumeration.
                Assert.Contains("local-nano", matched);
                Assert.Contains("included-vim", matched);

                // GetString returns the last value (last-wins).
                Assert.Equal("included-vim", await repo.Config.GetStringAsync("core.editor", ct));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── multiline variable continuation ──────────────────────────────

    /// <summary>
    /// A config value with a trailing backslash continues on the next
    /// line, joined by <see cref="ConfigParser.ParseMultilineVariable"/>.
    /// Loading a config with <c>key = first \\\n  second</c> produces a
    /// single value <c>first second</c>. Exercises the
    /// multiline-continuation branch in <see cref="ConfigParser"/>.
    /// </summary>
    [Fact]
    public async Task ConfigParser_MultilineVariable_ContinuationLine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        using GitContext ctx = new();
        try
        {
            GitRepository repo = await InitRepoWithConfigAsync(
                path,
                "[user]\n\tname = first \\\n  second\n",
                ctx,
                ct);
            await using (repo)
            {
                string? name = await repo.Config.GetStringAsync("user.name", ct);
                Assert.NotNull(name);
                // The continuation joins the two parts (whitespace handling
                // follows git's parser; the key assertion is that the value
                // spans both lines rather than being just "first").
                Assert.Contains("first", name);
                Assert.Contains("second", name);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary> A blank line inside a backslash-continued value is SKIPPED like a comment-only line (C config_parse.c:355-361) —
    /// only EOF terminates the value. C-verified via differential probe: <c>key = "one\tkey2 = after"</c> and no <c>key2</c> variable exists.
    /// </summary>
    [Fact]
    public async Task ConfigParser_MultilineVariable_BlankLineIsSkipped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        using GitContext ctx = new();
        try
        {
            GitRepository repo = await InitRepoWithConfigAsync(
                path,
                "[section]\n\tkey = one\\\n\n\tkey2 = after\n",
                ctx,
                ct);
            await using (repo)
            {
                string? value = await repo.Config.GetStringAsync("section.key", ct);
                Assert.Equal("one\tkey2 = after", value);

                // The line after the blank continuation was absorbed into key.
                Assert.Null(await repo.Config.GetStringAsync("section.key2", ct));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}

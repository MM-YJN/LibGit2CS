using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// Integration tests for the diff-driver dispatch path
/// (<see cref="DiffDriverRegistry"/> → built-in drivers → custom drivers
/// loaded from config) exercised end-to-end via
/// <see cref="GitDiff.TreeToTreeAsync"/> against locally-initialized repos
/// with <c>.gitattributes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="DiffIntegrationTests"/> exercise the diff engine with the
/// default driver (Auto) for every delta — the
/// <see cref="DiffDriverRegistry.LookupAsync"/> attribute-driven dispatch
/// always returns <c>Auto</c>, so the built-in driver table
/// (<see cref="DiffDriverRegistry.s_builtinDefs"/> + <c>LoadBuiltin</c>),
/// the config-driven custom driver path (<c>LoadAsync</c>), and the
/// <c>AddPatterns</c> funcname-pattern parser were entirely cold in the
/// integration suite. These tests write
/// <c>.gitattributes</c> with <c>diff=&lt;driver&gt;</c> for various
/// built-in and custom drivers, build two commits whose trees differ on
/// an attributed file, and run <see cref="GitDiff.TreeToTreeAsync"/> +
/// <see cref="GitDiff.ToBufferAsync"/> — verifying the rendered patch
/// reflects the driver's funcname hunk-header selection.
/// </para>
/// <para>
/// <b>Attribute-cache ordering.</b> <c>.gitattributes</c> is written FIRST,
/// before any index operation, to avoid the cache-staleness hazard
/// documented in <see cref="Merge.MergeDriverIntegrationTests"/>: the
/// attribute cache is built lazily on the first attribute lookup
/// (triggered during <see cref="GitIndex.AddByPathAsync"/>) and never
/// re-reads the file.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/diff/diff_driver.c</c>
/// (<c>test_diff_driver__userdiff</c>,
/// <c>test_diff_driver__custom</c>), adapted to build the sandbox from
/// scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class DiffDriverIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-diffdrv-" + Guid.NewGuid().ToString("N"));

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
    /// Commits the given (path → content) files to <paramref name="refName"/>
    /// on top of <paramref name="parent"/> (or as a root commit if parent is
    /// null). Returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir,
        IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        index.Clear();
        foreach ((string p, string c) in files)
        {
            string full = Path.Combine(workdir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inits a repo, writes <paramref name="gitattributes"/> to the workdir
    /// FIRST (before any index op), then commits the initial set of files
    /// on <c>refs/heads/main</c>. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> InitRepoWithAttributesAsync(
        string path, string gitattributes, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        // .gitattributes FIRST.
        await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), gitattributes, ct).ConfigureAwait(false);

        var allFiles = new Dictionary<string, string>(files) { [".gitattributes"] = gitattributes };
        GitOid commitOid = await CommitFilesAsync(repo, path, allFiles, "init\n", "refs/heads/main", null, ct).ConfigureAwait(false);
        await repo.SetHeadAsync("refs/heads/main", ct).ConfigureAwait(false);
        await repo.DisposeAsync().ConfigureAwait(false);
        return commitOid;
    }

    /// <summary>Resolves a commit OID to its tree.</summary>
    private static async Task<GitTree> TreeOfAsync(GitRepository repo, GitOid commitOid, CancellationToken ct)
    {
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct).ConfigureAwait(false))!;
        return (await repo.ObjectLookupAsync<GitTree>(commit.Tree, ct).ConfigureAwait(false))!;
    }

    // ── Built-in driver dispatch ───────────────────────────────────────

    /// <summary>
    /// A <c>.gitattributes</c> with <c>diff=cpp</c> causes the diff
    /// engine to dispatch to the built-in C++ driver for <c>.cpp</c>
    /// files. The rendered patch's <c>@@</c> hunk header carries the
    /// function-name context matched by the cpp funcname regex. Exercises
    /// <see cref="DiffDriverRegistry.LoadBuiltin"/> for <c>cpp</c> + the
    /// <see cref="DiffDriverRegistry.s_builtinDefs"/> static initializer
    /// (<c>.cctor</c>) + <see cref="DiffDriverRegistry.AddPatterns"/> on
    /// the cpp patterns.
    /// </summary>
    [Fact]
    public async Task Diff_BuiltinDriver_Cpp_DispatchesToCppDriver()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.cpp diff=cpp\n",
            new Dictionary<string, string> { ["foo.cpp"] = "int old_func() {\n    return 1;\n}\n" },
            ct);
        try
        {
            // Second commit: add a new function above old_func.
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.cpp diff=cpp\n",
                    ["foo.cpp"] = "int new_func() {\n    return 0;\n}\nint old_func() {\n    return 1;\n}\n",
                },
                "add new_func\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            // The cpp driver's funcname regex matches `int (...) {` lines.
            Assert.Contains("@@ ", patch);
            Assert.Contains("diff --git a/foo.cpp b/foo.cpp", patch);
            // The hunk header should carry a function context line.
            Assert.Contains("int new_func()", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A <c>.gitattributes</c> with <c>diff=python</c> dispatches to
    /// the built-in Python driver. Adding a new <c>def</c> function above
    /// an existing one produces a patch whose hunk header carries the
    /// <c>def</c> line as context.
    /// </summary>
    [Fact]
    public async Task Diff_BuiltinDriver_Python_FuncnameRegex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.py diff=python\n",
            new Dictionary<string, string> { ["app.py"] = "def old():\n    return 1\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.py diff=python\n",
                    ["app.py"] = "def new():\n    return 0\n\ndef old():\n    return 1\n",
                },
                "add new def\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/app.py b/app.py", patch);
            Assert.Contains("def new()", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Attribute tri-state dispatch ───────────────────────────────────

    /// <summary>
    /// <c>.gitattributes</c> with <c>-diff</c> (attribute set to
    /// false) dispatches to the <c>Binary</c> driver — no line-level
    /// patch is generated, just the <c>Binary files differ</c> header.
    /// Exercises the <c>value.IsFalse → Binary</c> branch in
    /// <see cref="DiffDriverRegistry.LookupAsync"/>.
    /// </summary>
    [Fact]
    public async Task Diff_AttributeFalse_BinaryDriver_NoTextDiff()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.bin -diff\n",
            new Dictionary<string, string> { ["data.bin"] = "binary-content-v1\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.bin -diff\n",
                    ["data.bin"] = "binary-content-v2-different\n",
                },
                "modify binary\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/data.bin b/data.bin", patch);
            Assert.Contains("Binary files", patch);
            // No line-level hunks.
            Assert.DoesNotContain("@@", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <c>.gitattributes</c> with <c>diff</c> (attribute set to true,
    /// no value) dispatches to the <c>Text</c> driver, forcing text
    /// treatment even for files that look binary. Exercises the
    /// <c>value.IsTrue → Text</c> branch in
    /// <see cref="DiffDriverRegistry.LookupAsync"/>.
    /// </summary>
    [Fact]
    public async Task Diff_AttributeTrue_TextDriver_ForcesText()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        // File with a NUL byte (would normally be classified binary).
        string v1 = "before\x00binary\n";
        string v2 = "after\x00binary\n";
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.dat diff\n",
            new Dictionary<string, string> { ["data.dat"] = v1 },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.dat diff\n",
                    ["data.dat"] = v2,
                },
                "modify\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/data.dat b/data.dat", patch);
            // The Text driver forces a text diff even with a NUL byte.
            Assert.DoesNotContain("Binary files", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Custom driver from config ─────────────────────────────────────

    /// <summary>
    /// A custom diff driver loaded from config:
    /// <c>.gitattributes</c> with <c>diff=mydriver</c> + repo config
    /// <c>diff.mydriver.funcname</c> set to a regex. The diff engine
    /// loads the driver via <see cref="DiffDriverRegistry.LoadAsync"/>'s
    /// config path and applies the custom funcname regex to hunk
    /// headers. Exercises the config-driven driver-load path
    /// (<c>cfg.GetMultiAsync("diff.mydriver.funcname")</c> +
    /// <c>AddPatterns</c>).
    /// </summary>
    [Fact]
    public async Task Diff_CustomDriver_FromConfig_Funcname()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.x diff=mydriver\n",
            new Dictionary<string, string> { ["f.x"] = "section old\n  body\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Configure the custom driver's funcname regex.
            await repo.Config.SetStringAsync("diff.mydriver.funcname", "^section .*$", ct);

            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.x diff=mydriver\n",
                    ["f.x"] = "section new\n  body\n",
                },
                "modify\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/f.x b/f.x", patch);
            // The custom funcname regex matches `section ...` lines, so the
            // hunk header carries that as context.
            Assert.Contains("section new", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A custom driver with <c>diff.mydriver.wordregex</c> configured
    /// loads successfully via <see cref="DiffDriverRegistry.LoadAsync"/>'s
    /// config path and produces a normal line-level patch. Exercises the
    /// <c>wordRegex</c> config-read + <see cref="RegexAdapter.Compile"/>
    /// branch in <c>LoadAsync</c> (the wordregex is loaded into the driver
    /// even though the default patch printer doesn't render word-level
    /// diffs without an explicit flag).
    /// </summary>
    [Fact]
    public async Task Diff_CustomDriver_WordRegex_LoadsAndRenders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.w diff=mydriver\n",
            new Dictionary<string, string> { ["f.w"] = "alpha beta gamma\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.Config.SetStringAsync("diff.mydriver.funcname", "^.*$", ct);
            await repo.Config.SetStringAsync("diff.mydriver.wordregex", "[A-Za-z_]+", ct);

            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.w diff=mydriver\n",
                    ["f.w"] = "alpha delta gamma\n",
                },
                "modify\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/f.w b/f.w", patch);
            Assert.Contains("alpha", patch);
            Assert.Contains("delta", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <c>diff.mydriver.binary = false</c> forces text treatment for
    /// files that would otherwise be classified binary. Exercises the
    /// <c>diff.&lt;name&gt;.binary</c> config branch in
    /// <see cref="DiffDriverRegistry.LoadAsync"/> (false →
    /// <c>ForceText</c> flags).
    /// </summary>
    [Fact]
    public async Task Diff_CustomDriver_BinaryFalse_ForcesText()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.dat diff=mydriver\n",
            new Dictionary<string, string> { ["data.dat"] = "before\x00binary\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.Config.SetStringAsync("diff.mydriver.binary", "false", ct);

            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.dat diff=mydriver\n",
                    ["data.dat"] = "after\x00binary\n",
                },
                "modify\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/data.dat b/data.dat", patch);
            // binary=false forces text treatment despite the NUL byte.
            Assert.DoesNotContain("Binary files", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Driver-load edge cases ─────────────────────────────────────────

    /// <summary>
    /// <c>diff.&lt;name&gt;.xfuncname</c> is an alias for
    /// <c>diff.&lt;name&gt;.funcname</c> in
    /// <see cref="DiffDriverRegistry.LoadAsync"/> (both feed the same
    /// <c>AddPatterns</c> path). Setting <c>xfuncname</c> (without setting
    /// <c>funcname</c>) still loads the funcname regex and produces hunk
    /// headers carrying the matched context. Exercises the cold
    /// <c>cfg.GetMultiAsync("diff.&lt;name&gt;.xfuncname")</c> branch at
    /// <c>DiffDriverRegistry.cs:259</c>.
    /// </summary>
    [Fact]
    public async Task Diff_CustomDriver_XfuncName_IsAliasForFuncname()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.x diff=mydriver\n",
            new Dictionary<string, string> { ["f.x"] = "section old\n  body\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // xfuncname (NOT funcname) — exercises the alias branch.
            await repo.Config.SetStringAsync("diff.mydriver.xfuncname", "^section .*$", ct);

            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.x diff=mydriver\n",
                    ["f.x"] = "section new\n  body\n",
                },
                "modify\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            Assert.Contains("diff --git a/f.x b/f.x", patch);
            // The xfuncname regex matched `section ...` and is reflected in
            // the hunk header (same behavior as funcname).
            Assert.Contains("section new", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A syntactically invalid <c>diff.&lt;name&gt;.funcname</c> regex is
    /// silently ignored by <see cref="DiffDriverRegistry.AddPatterns"/>
    /// (the <c>catch (ArgumentException)</c> path at
    /// <c>DiffDriverRegistry.cs:401</c>) — matching libgit2's "ignore bad
    /// patterns" behavior (diff_driver.c:108-111). The driver falls back to
    /// no funcname patterns, and the diff still renders a normal patch
    /// without throwing. Also covers the analogous bad-regex path in
    /// <c>LoadBuiltin</c>'s wordregex loader (line 345) and
    /// <c>LoadAsync</c>'s wordregex loader (line 285).
    /// </summary>
    [Fact]
    public async Task Diff_CustomDriver_BadFuncnameRegex_FallsBackToNoPatterns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid first = await InitRepoWithAttributesAsync(
            path,
            "*.x diff=mydriver\n",
            new Dictionary<string, string> { ["f.x"] = "line one\nline two\n" },
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Unclosed character class — invalid regex; AddPatterns catches
            // ArgumentException and skips the pattern.
            await repo.Config.SetStringAsync("diff.mydriver.funcname", "[unclosed", ct);
            // Also exercise the wordregex bad-regex catch path (line 285).
            await repo.Config.SetStringAsync("diff.mydriver.wordregex", "[alsobad", ct);

            GitOid second = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string>
                {
                    [".gitattributes"] = "*.x diff=mydriver\n",
                    ["f.x"] = "line one\nline two\nline three\n",
                },
                "modify\n", "refs/heads/main", first, ct).ConfigureAwait(false);

            GitTree oldTree = await TreeOfAsync(repo, first, ct);
            GitTree newTree = await TreeOfAsync(repo, second, ct);
            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            // The bad regexes didn't throw; the diff still produces output.
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("diff --git a/f.x b/f.x", patch);
            Assert.Contains("line three", patch);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

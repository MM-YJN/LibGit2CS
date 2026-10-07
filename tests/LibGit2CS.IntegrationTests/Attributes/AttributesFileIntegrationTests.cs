using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Attributes;

/// <summary>
/// Integration tests for <see cref="AttributesFile"/> load paths and the
/// <see cref="AttributeCache"/> multi-source lookup precedence, exercised
/// end-to-end against locally-initialized repos with <c>.gitattributes</c>
/// files in multiple locations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>AttributeCacheTests</c> cover
/// <see cref="AttributeCache.LookupOne"/> against a workdir
/// <c>.gitattributes</c> file and the <c>info/attributes</c> override.
/// However, the full set of <see cref="AttributesFile"/> load entry points
/// (<see cref="AttributesFile.LoadFromIndexAsync"/>,
/// <see cref="AttributesFile.LoadFromPathAsync"/>,
/// <see cref="AttributesFile.LoadFromHeadAsync"/>,
/// <see cref="AttributesFile.LoadFromCommitAsync"/>,
/// <see cref="AttributesFile.LoadFromGlobalAsync"/>,
/// <see cref="AttributesFile.LoadFromSystemAsync"/>) and the macro
/// expansion path in <see cref="AttributesFile.ParseBuffer"/> were only
/// partially exercised. The <c>NameHash</c> helper (used for case-insensitive
/// path lookup) was entirely cold. These tests drive each load entry point
/// against a freshly-built repo and verify the parsed file returns the
/// expected attribute values via <see cref="AttributesFile.LookupOne"/>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/attr/attr.c</c>
/// (<c>test_attr__lookup</c>,
/// <c>test_attr__macros</c>,
/// <c>test_attr__prepend</c>), adapted to build the sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class AttributesFileIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-attrfile-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a non-bare repo and returns it (caller disposes). Optionally
    /// writes <paramref name="workdirAttributes"/> to the workdir
    /// <c>.gitattributes</c> and stages it.
    /// </summary>
    private static async Task<GitRepository> InitRepoAsync(string path, string? workdirAttributes, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        if (workdirAttributes is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), workdirAttributes, ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync(".gitattributes", ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);
        }

        return repo;
    }

    /// <summary>
    /// Builds an <see cref="AttrPath"/> for a relative path (no base dir).
    /// </summary>
    private static AttrPath MakeAttrPath(string path)
    {
        var ap = new AttrPath();
        ap.Init(path, string.Empty, AttrPath.DirFlag.False);
        return ap;
    }

    // ── LoadFromWorkdir ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="AttributesFile.LoadFromWorkdirAsync"/> parses a
    /// workdir <c>.gitattributes</c> file and the parsed file's
    /// <see cref="AttributesFile.LookupOne"/> returns the expected value.
    /// Exercises the full read + <see cref="AttributesFile.ParseBuffer"/>
    /// path.
    /// </summary>
    [Fact]
    public async Task LoadFromWorkdir_ParsesSimpleAttributes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, "*.txt text\n", ct);
            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromWorkdirAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
            Assert.NotNull(file);

            GitAttrValue value = file!.LookupOne(MakeAttrPath("hello.txt"), "text");
            Assert.True(value.IsTrue);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="AttributesFile.LoadFromWorkdirAsync"/> on a repo
    /// with no <c>.gitattributes</c> in the workdir returns <c>null</c>.
    /// Exercises the file-not-found branch.
    /// </summary>
    [Fact]
    public async Task LoadFromWorkdir_NoFile_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, workdirAttributes: null, ct);
            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromWorkdirAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
            Assert.Null(file);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── macro expansion ────────────────────────────────────────────────

    /// <summary>
    /// A <c>.gitattributes</c> file with a macro definition
    /// (<c>[attr]binary -text -diff</c>) followed by a pattern that uses
    /// the macro (<c>*.bin binary</c>) is parsed with macro expansion.
    /// <see cref="AttributesFile.LookupOne"/> for a <c>*.bin</c> path
    /// returns <c>text=false</c> and <c>diff=false</c>. Exercises the
    /// <see cref="AttributesFile.AddMacro"/> + macro-resolution path in
    /// <see cref="AttributesFile.ParseBuffer"/>.
    /// </summary>
    [Fact]
    public async Task LoadFromWorkdir_MacroDefinition_Expands()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, "[attr]binary -text -diff\n*.bin binary\n", ct);
            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromWorkdirAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
            Assert.NotNull(file);

            GitAttrValue text = file!.LookupOne(MakeAttrPath("data.bin"), "text");
            Assert.True(text.IsFalse);
            GitAttrValue diff = file.LookupOne(MakeAttrPath("data.bin"), "diff");
            Assert.True(diff.IsFalse);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── LoadFromIndex ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="AttributesFile.LoadFromIndexAsync"/> parses a
    /// <c>.gitattributes</c> blob staged in the index (no commit needed).
    /// Exercises the index-blob read path distinct from the workdir-file
    /// read path.
    /// </summary>
    [Fact]
    public async Task LoadFromIndex_ParsesStagedAttributes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            await using (repo)
            {
                // Stage .gitattributes in the index (no commit).
                await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), "*.txt text\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync(".gitattributes", ct);
                await idx.WriteAsync(ct);

                var macros = new AttrMacroRegistry();
                AttributesFile? file = await AttributesFile.LoadFromIndexAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
                Assert.NotNull(file);

                GitAttrValue value = file!.LookupOne(MakeAttrPath("a.txt"), "text");
                Assert.True(value.IsTrue);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── LoadFromPath ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="AttributesFile.LoadFromPathAsync"/> parses a
    /// <c>.gitattributes</c> file at an arbitrary absolute path (used for
    /// the global <c>core.attributesfile</c> path). Exercises the
    /// standalone-file read path with no repository context.
    /// </summary>
    [Fact]
    public async Task LoadFromPath_ExternalFile_ParsesRules()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            Directory.CreateDirectory(path);
            string attrPath = Path.Combine(path, "myattrs");
            await File.WriteAllTextAsync(attrPath, "*.md text\n", ct);

            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromPathAsync(macros, ignoreCase: false, fullPath: attrPath, cancellationToken: ct);
            Assert.NotNull(file);

            GitAttrValue value = file!.LookupOne(MakeAttrPath("readme.md"), "text");
            Assert.True(value.IsTrue);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── LoadFromInfo ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="AttributesFile.LoadFromInfoAsync"/> parses
    /// <c>.git/info/attributes</c> from the repository's git directory.
    /// Exercises the info-file read path (highest-precedence source in
    /// libgit2's attribute resolution).
    /// </summary>
    [Fact]
    public async Task LoadFromInfo_ParsesInfoAttributes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, workdirAttributes: null, ct);
            // Write .git/info/attributes.
            string infoPath = Path.Combine(repo.Path, "info", "attributes");
            Directory.CreateDirectory(Path.GetDirectoryName(infoPath)!);
            await File.WriteAllTextAsync(infoPath, "*.txt text\n", ct);

            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromInfoAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
            Assert.NotNull(file);

            GitAttrValue value = file!.LookupOne(MakeAttrPath("a.txt"), "text");
            Assert.True(value.IsTrue);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── LoadFromGlobal (via core.attributesfile config) ────────────────

    /// <summary>
    /// <see cref="AttributesFile.LoadFromGlobalAsync"/> reads the
    /// <c>core.attributesfile</c> config key and parses the referenced
    /// file. Exercises the config-driven global-attributes path.
    /// </summary>
    [Fact]
    public async Task LoadFromGlobal_ConfigSet_ParsesGlobalFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, workdirAttributes: null, ct);

            // Write a global attributes file in a sibling temp dir.
            string globalPath = Path.Combine(Path.GetDirectoryName(path)!, "global-attrs-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(globalPath, "*.txt text\n", ct);

            // Point core.attributesfile at it.
            await repo.Config.SetStringAsync("core.attributesfile", globalPath, ct);

            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromGlobalAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
            try
            {
                Assert.NotNull(file);
                GitAttrValue value = file!.LookupOne(MakeAttrPath("a.txt"), "text");
                Assert.True(value.IsTrue);
            }
            finally
            {
                try
                {
                    File.Delete(globalPath);
                }
                catch (IOException) { }
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── LoadFromHead / LoadFromCommit ──────────────────────────────────

    /// <summary>
    /// <see cref="AttributesFile.LoadFromHeadAsync"/> parses the
    /// <c>.gitattributes</c> blob from the HEAD commit's tree. Exercises
    /// the tree-blob read path.
    /// </summary>
    [Fact]
    public async Task LoadFromHead_ParsesHeadTreeAttributes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, "*.txt text\n", ct);
            var macros = new AttrMacroRegistry();
            AttributesFile? file = await AttributesFile.LoadFromHeadAsync(repo, macros, ignoreCase: false, cancellationToken: ct);
            Assert.NotNull(file);

            GitAttrValue value = file!.LookupOne(MakeAttrPath("a.txt"), "text");
            Assert.True(value.IsTrue);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="AttributesFile.LoadFromCommitAsync"/> parses the
    /// <c>.gitattributes</c> blob from a specific commit's tree. Exercises
    /// the per-commit tree-blob read path.
    /// </summary>
    [Fact]
    public async Task LoadFromCommit_ParsesCommitTreeAttributes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository repo = await InitRepoAsync(path, "*.txt text\n", ct);
            await using (repo)
            {
                GitReference head = (await repo.ReferenceResolveAsync("HEAD", ct))!;
                GitOid commitOid = Assert.IsType<GitDirectReference>(head).Target;

                var macros = new AttrMacroRegistry();
                AttributesFile? file = await AttributesFile.LoadFromCommitAsync(repo, macros, ignoreCase: false, commitId: commitOid, cancellationToken: ct);
                Assert.NotNull(file);

                GitAttrValue value = file!.LookupOne(MakeAttrPath("a.txt"), "text");
                Assert.True(value.IsTrue);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}

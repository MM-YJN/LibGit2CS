using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary> Parity tests for config/attr/filter/status and index-side items: HEAD source tree-OID stamp + negative
/// cache, ScanAttrs ASCII whitespace, a leading slash consuming exactly one, . /.. resolution before the ignore walk, status option
/// validation ordering, and index lookup stage masking. Expectations are C-verified against libgit2 1.9.4 (attr_file.c:180-189, 243-244, 772-777;
/// filter.c:83-89; ignore.c:322-326; status.c:246-256, 278-299; index.c:890). </summary>
public sealed class StatusAttrLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public StatusAttrLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StatusAttrLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── HEAD source tree-OID stamp + negative cache ────────────

    [Fact]
    public async Task HeadAttr_StampIsTreeOid()
    {
        // C (attr_file.c:243-244): HEAD files are stamped with the TREE oid (git_oid_cpy(..., git_tree_id(tree))), so two
        // commits sharing a tree do not re-resolve on every lookup.
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        GitOid attrBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "*.txt text\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync(".gitattributes", attrBlob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        AttributesFile? file = await AttributesFile.LoadFromHeadAsync(repo, new AttrMacroRegistry(), ignoreCase: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal(treeOid, file.HeadStamp);
    }

    [Fact]
    public async Task HeadAttr_MissingFile_NegativeCachedEmpty()
    {
        // C (attr_file.c:180-189): a missing .gitattributes in the HEAD tree is cached as an EMPTY file (with the tree stamp) so future lookups do not re-walk
        // the tree until it changes.
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = repo.NewTreeBuilder();
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None); // empty tree, no .gitattributes
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        AttributesFile? file = await AttributesFile.LoadFromHeadAsync(repo, new AttrMacroRegistry(), ignoreCase: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Empty(file.Rules);
        Assert.Equal(treeOid, file.HeadStamp);
    }

    // ── ScanAttrs uses the ASCII whitespace set ────────────────

    [Fact]
    public void FilterRegistry_ScanAttrs_NonAsciiWhitespaceIsNotSeparator()
    {
        // C (filter.c:83-89): filter_def_scan_attrs tokenizes with git__isspace — the ASCII set. char.IsWhiteSpace would split on NBSP too, turning "a\u00A0b"
        // into two attributes instead of one.
        var registry = new FilterRegistry();
        registry.Register("nbspfilter", new NbspAttrFilter(), priority: 500);

        FilterDef? def = registry.GetAll().SingleOrDefault(f => f.Name == "nbspfilter");
        Assert.NotNull(def);
        Assert.Equal(1, def.Nattrs);
        Assert.Equal("a\u00A0b", def.Specs[0].Name);
    }

    private sealed class NbspAttrFilter : IFilter
    {
        public string Name => "nbspfilter";
        public string Attributes => "a\u00A0b";
        public ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitFilterResult.Passthrough);
        public ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitApplyResult.Passthrough);
    }

    // ── exactly one leading slash is consumed ──────────────────

    [Theory]
    [InlineData("/foo", "foo")]
    [InlineData("//foo", "/foo")]
    [InlineData("///foo", "//foo")]
    public void FnMatchPattern_LeadingSlashes_ConsumesExactlyOne(string input, string expectedPattern)
    {
        // C (attr_file.c:772-777): `if (slash_count == 1 && pattern == scan) pattern++;` fires once — exactly ONE leading '/' is consumed.
        var pattern = FnMatchPattern.Parse(Encoding.UTF8.GetBytes(input), default, FnMatchPattern.Flag.AllowNeg | FnMatchPattern.Flag.AllowMacro);
        Assert.NotNull(pattern);
        Assert.Equal(expectedPattern, Encoding.UTF8.GetString(pattern.Pattern.Span));
        Assert.True((pattern.Flags & FnMatchPattern.Flag.FullPath) != 0);
    }

    // ── . / .. resolved before the ignore walk ─────────────────

    [Fact]
    public async Task Ignore_PathWithDotDot_DoesNotVisitIntermediateDir()
    {
        // C (ignore.c:322-326): the directory portion of the path is resolved
        // via git_fs_path_resolve_relative BEFORE the walk — for
        // "a/../b/file.txt" the walk loads only "b/.gitignore" and the root
        // file, never the intermediate "a/.gitignore". Its paired NEGATIVE
        // rule "!file.txt" (kept by does_negate_rule) would otherwise
        // un-ignore the path at the first walk level; C ignores it via the
        // internal ".." rule at the "a/.." level (probe-verified:
        // git_ignore_path_is_ignored("a/../b/zzz") == 1 with a/.gitignore
        // "zzz\n!zzz\n").
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(repo.Workdir!, "a"));
        Directory.CreateDirectory(Path.Combine(repo.Workdir!, "a", "b"));
        Directory.CreateDirectory(Path.Combine(repo.Workdir!, "b"));
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a", ".gitignore"), "file.txt\n!file.txt\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "b", "file.txt"), "x\n", TestContext.Current.CancellationToken);

        bool ignored = await IgnoreContext.PathIsIgnoredAsync(repo, "a/../b/file.txt", TestContext.Current.CancellationToken);
        Assert.True(ignored);

        // Control: without the ".." segment no intermediate rules apply —
        // the path is not ignored.
        bool ignoredB = await IgnoreContext.PathIsIgnoredAsync(repo, "b/file.txt", TestContext.Current.CancellationToken);
        Assert.False(ignoredB);
    }

    // ── status option validation precedes the bare check ───────

    [Fact]
    public async Task Status_BareRepoWithInvalidShow_OptionErrorFirst()
    {
        // C (status.c:278-299): status_validate_options runs BEFORE git_repository__ensure_not_bare — a bare repo with an invalid option reports the OPTION
        // error.
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        var options = new GitStatusOptions { Show = (GitStatusShow)99 };
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.StatusNewAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("unknown status 'show' option", ex.Message, StringComparison.Ordinal);
    }

    // ── index lookup stage masked to 2 bits ────────────────────

    [Fact]
    public async Task Index_EntryByPath_StageMaskedToTwoBits()
    {
        // C (index.c:890): git_index_get_bypath masks the stage via GIT_INDEX_ENTRY_STAGE_SET — stage 4 looks up stage 0.
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddFromBufferAsync(new GitIndexEntry("file.txt", blob, GitFileMode.Regular), "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);

        GitIndexEntry? entry = index.EntryByPath("file.txt", stage: 4);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Value.Stage);
    }
}

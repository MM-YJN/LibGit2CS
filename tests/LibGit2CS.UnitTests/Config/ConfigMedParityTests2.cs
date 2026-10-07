using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Config;

/// <summary> Parity tests for config, attributes, filters, and status: (empty includeIf gitdir pattern), (bare-repo attr walk + INDEX source),
/// ([attr] macro dropped below root), (ATTRIBUTES_FROM_COMMIT plumbing), (ident $XY$ fall-through), (stable filter sort), (CRLF apply-phase
/// flags), (ApplyToBlobAsync source OID), (corrupt index in has_cr_in_index), (does_negate_pattern off-by-one), (empty core.excludesfile),
/// (info/exclude from commondir). </summary>
public sealed class ConfigMedParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public ConfigMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigMed2_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> InitRepoAsync(bool bare = false)
    {
        string repoDir = NewDir();
        return await GitRepository.InitAsync(repoDir, isBare: bare, new GitContext(), TestContext.Current.CancellationToken);
    }

    private static async ValueTask<GitOid> WriteTreeAsync(GitRepository repo, params (string Path, GitOid Id, GitFileMode Mode)[] entries)
    {
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        foreach ((string path, GitOid id, GitFileMode mode) in entries)
        {
            await bld.InsertAsync(path, id, mode, TestContext.Current.CancellationToken);
        }

        return await bld.WriteAsync(CancellationToken.None);
    }

    // ── empty gitdir: includeIf must not crash ────────────────

    [Fact]
    public async Task Config_EmptyGitdirIncludeIfPattern_SilentlySkipped()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        string extra = Path.Combine(_tempDir, "extra.config");
        await File.WriteAllTextAsync(extra, "[user]\n\tname = Extra\n", TestContext.Current.CancellationToken);

        // C: an empty gitdir: pattern is skipped (the include is not applied).
        // The path value must be config-escaped: backslashes (Windows paths)
        // are written as "\\" so the parser unescapes them back to "\".
        string cfg = $"[includeIf \"gitdir:\"]\n\tpath = {extra.Replace("\\", "\\\\")}\n";
        string cfgPath = Path.Combine(_tempDir, "inc.config");
        await File.WriteAllTextAsync(cfgPath, cfg, TestContext.Current.CancellationToken);

        await using GitConfiguration config = new(repo.Context);
        await config.AddFileOnDiskAsync(cfgPath, GitConfigLevel.Local, repoGitDirPath: repo.Path, cancellationToken: TestContext.Current.CancellationToken);

        // Must not throw IndexOutOfRangeException; the include is not applied.
        string? name = await config.GetStringAsync("user.name", TestContext.Current.CancellationToken);
        Assert.Null(name);
    }

    // ── bare repo attrs from the INDEX source ──────────────────

    [Fact]
    public async Task Attrs_BareRepo_IndexSourceAttributes_Work()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        // Stage .gitattributes + a target file in the index.
        GitOid attrBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "*.txt text\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid fileBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddFromBufferAsync(new GitIndexEntry(".gitattributes", attrBlob, GitFileMode.Regular), "*.txt text\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await index.AddFromBufferAsync(new GitIndexEntry("file.txt", fileBlob, GitFileMode.Regular), "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        // C (attr.c:650-656, 682-685 + attr_decide_sources): in a bare repo
        // the per-directory walk still runs and the INDEX source contributes.
        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        var attrPath = new AttrPath();
        attrPath.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(
            attrPath, "text",
            GitAttrCheckFlags.FileThenIndex, TestContext.Current.CancellationToken);

        Assert.True(value.IsTrue);
    }

    // ── [attr] macro line below the root is dropped ────────────

    [Fact]
    public async Task Attrs_MacroLineInSubdirectoryFile_Dropped()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        string subDir = Path.Combine(repo.Workdir!, "sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, ".gitattributes"), "[attr]x y\n", TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        var attrPath2 = new AttrPath();
        attrPath2.Init("sub/x", string.Empty, AttrPath.DirFlag.Unknown);
        GitAttrValue value = await cache.LookupOneAsync(
            attrPath2, "y",
            GitAttrCheckFlags.FileThenIndex, TestContext.Current.CancellationToken);

        // C (attr_file.c:389-392): macros are not allowed below the root —
        // the line is dropped entirely, so the file "sub/x" gets nothing.
        Assert.False(value.IsTrue);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    // ── ident trailing-$XY$ fall-through ───────────────────────

    [Fact]
    public async Task IdentFilter_TrailingDollarXY_Expanded()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt ident\n", TestContext.Current.CancellationToken);
        GitOid blobId = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // "$XY$" (3 bytes after the '$') is matched by C's post-loop
        // fall-through and expanded to "$Id: <oid> $".
        GitFilterList? list = await repo.FilterListLoadAsync(
            "a.txt", blobId, GitFilterMode.ToWorktree,
            GitFilterListFlags.AllowUnsafe, attrCommitId: null, TestContext.Current.CancellationToken);
        Assert.NotNull(list);

        byte[] output = await list!.ApplyToBufferAsync("x$XY$"u8.ToArray(), TestContext.Current.CancellationToken);
        string expected = $"x$Id: {blobId} $";
        Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(output));
    }

    // ── equal-priority filter registration order is stable ────

    [Fact]
    public void FilterRegistry_EqualPriority_KeepsRegistrationOrder()
    {
        GitContext ctx = new();
        FilterRegistry registry = ctx.Filters;

        // Register 30 equal-priority filters; C's stable timsort keeps registration order (List<T>.Sort is unstable).
        for (int i = 0; i < 30; i++)
        {
            string name = $"f{i:D2}";
            registry.Register(name, new RecordingFilter(name), FilterRegistry.DriverPriority);
        }

        // Built-ins (crlf@0, ident@100) sort first; the 30 equal-priority
        // custom drivers must keep their registration order.
        string[] names = [.. registry.GetAll().Select(d => d.Name)];
        Assert.Equal(FilterRegistry.CrlfName, names[0]);
        Assert.Equal(FilterRegistry.IdentName, names[1]);
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal($"f{i:D2}", names[i + 2]);
        }
    }

    private sealed class RecordingFilter(string name) : IFilter
    {
        public string Name { get; } = name;
        public string Attributes => string.Empty;

        public ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitFilterResult.Apply);

        public ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitApplyResult.Passthrough);
    }

    // ── CRLF apply re-resolves attrs with the list's flags ────

    [Fact]
    public async Task CrlfFilter_ApplyUsesHeadAttributes()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // HEAD commit: .gitattributes says *.txt text. The workdir has NO
        // .gitattributes, so a default-flags apply-phase lookup sees nothing.
        GitOid attrBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "*.txt text\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid tree = await WriteTreeAsync(repo, (".gitattributes", attrBlob, GitFileMode.Regular));
        _ = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Point HEAD at the commit (the init default branch is
        // environment-dependent — e.g. init.defaultBranch in the host's
        // global config — so the commit's branch must be made the HEAD
        // explicitly for the HEAD-attribute source to resolve).
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        // Load the clean list with AttributesFromHead: Check sees text=true.
        GitFilterList? list = await repo.FilterListLoadAsync(
            "a.txt", blobId: null, GitFilterMode.ToOdb,
            GitFilterListFlags.AttributesFromHead, attrCommitId: null, TestContext.Current.CancellationToken);
        Assert.NotNull(list);

        // Apply must use the SAME flags (HEAD): text=true → CRLF→LF.
        byte[] output = await list!.ApplyToBufferAsync("a\r\nb\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("a\nb\n", System.Text.Encoding.ASCII.GetString(output));
    }

    // ── ApplyToBlobAsync sets the source OID from the blob ────

    [Fact]
    public async Task FilterList_ApplyToBlob_SetsSourceOid()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt ident\n", TestContext.Current.CancellationToken);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(blobOid, TestContext.Current.CancellationToken))!;

        // Loaded with a NULL blob id — the apply must pick the OID up from
        // the blob (C: git_filter_list_stream_blob copies git_blob_id).
        GitFilterList? list = await repo.FilterListLoadAsync(
            "a.txt", blobId: null, GitFilterMode.ToWorktree,
            GitFilterListFlags.AllowUnsafe, attrCommitId: null, TestContext.Current.CancellationToken);
        Assert.NotNull(list);

        byte[] output = await list!.ApplyToBlobAsync(blob, TestContext.Current.CancellationToken);
        Assert.Equal($"$Id: {blob.Id} $\n", System.Text.Encoding.ASCII.GetString(output));
    }

    // ── corrupt index → has_cr_in_index returns false ─────────

    [Fact]
    public async Task CrlfFilter_CorruptIndex_ProceedsWithoutCrInIndex()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // text=true so the AUTO heuristic path (which calls has_cr_in_index)
        // is reached with autocrlf on.
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt text\n", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("core.autocrlf", "true", TestContext.Current.CancellationToken);

        // Corrupt the index AFTER the repo is open.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "index"), "GARBAGE NOT AN INDEX", TestContext.Current.CancellationToken);

        GitFilterList? list = await repo.FilterListLoadAsync(
            "a.txt", blobId: null, GitFilterMode.ToOdb,
            GitFilterListFlags.AllowUnsafe, attrCommitId: null, TestContext.Current.CancellationToken);
        Assert.NotNull(list);

        // C (crlf.c:73-90): the corrupt-index error is cleared → "no CR in
        // index" → the CRLF→LF conversion still happens.
        byte[] output = await list!.ApplyToBufferAsync("a\r\nb\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("a\nb\n", System.Text.Encoding.ASCII.GetString(output));
    }

    // ── does_negate_pattern keeps "/foo" + "!foo" ─────────────

    [Fact]
    public async Task Ignore_AnchoredRuleWithBasenameNegation_Unignores()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitignore"), "/foo\n!foo\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "foo"), "x\n", TestContext.Current.CancellationToken);

        // C (ignore.c:66-87): "/bar" vs "bar" keeps the negative → foo is NOT ignored (it shows as untracked). An off-by-one guard must not drop the
        // negative.
        Assert.False(await repo.IsIgnoredAsync("foo", TestContext.Current.CancellationToken));
    }

    // ── empty core.excludesfile does NOT fall back to XDG ─────

    [Fact]
    public async Task Ignore_EmptyCoreExcludesFile_NoXdgFallback()
    {
        string repoDir = NewDir();
        GitContext ctx = new();
        string xdgDir = Path.Combine(_tempDir, "xdg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(xdgDir, "git"));
        ctx.Dirs.Set(GitSystemDir.Xdg, xdgDir);
        ctx.Env["XDG_CONFIG_HOME"] = xdgDir;
        await File.WriteAllTextAsync(Path.Combine(xdgDir, "git", "ignore"), "xdgignored.txt\n", TestContext.Current.CancellationToken);

        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, ctx, TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("core.excludesfile", "", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "xdgignored.txt"), "x\n", TestContext.Current.CancellationToken);

        // C (attrcache.c:334-347): a present-but-empty value is literal (no global file) — the XDG fallback applies only when the key is ABSENT, so no
        // fallback.
        Assert.False(await repo.IsIgnoredAsync("xdgignored.txt", TestContext.Current.CancellationToken));
    }

    // ── info/exclude comes from the commondir ─────────────────

    [Fact]
    public async Task Ignore_LinkedWorktree_UsesCommondirInfoExclude()
    {
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // A commit is required for worktree creation (branch from HEAD).
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid tree = await WriteTreeAsync(repo, ("f.txt", blob, GitFileMode.Regular));
        _ = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Shared info/exclude in the MAIN repo's .git.
        string infoDir = Path.Combine(repo.Path, "info");
        Directory.CreateDirectory(infoDir);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "exclude"), "shared.txt\n", TestContext.Current.CancellationToken);

        string wtPath = Path.Combine(_tempDir, "wt-" + Guid.NewGuid().ToString("N")[..8]);
        LibGit2CS.Repository.Worktree worktree = await repo.WorktreeAddAsync("wt", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(wtPath, "shared.txt"), "x\n", TestContext.Current.CancellationToken);

        await using GitRepository wtRepo = await GitRepository.OpenAsync(wtPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.NotEqual(wtRepo.Path, wtRepo.CommonDir);

        // C (ignore.c:355): GIT_REPOSITORY_ITEM_INFO is commondir-parented — the linked worktree must see the MAIN repo's info/exclude.
        Assert.True(await wtRepo.IsIgnoredAsync("shared.txt", TestContext.Current.CancellationToken));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Combine(string a, string b) => Path.Combine(a, b);
}

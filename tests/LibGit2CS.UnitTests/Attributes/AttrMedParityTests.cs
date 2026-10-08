using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Filters;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Attributes;

/// <summary> Parity tests for the attr/filters subsystem. </summary>
public sealed class AttrMedParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _cleanupDirs = [];

    public AttrMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AttrMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string dir in _cleanupDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewRepoDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);
        return dir;
    }

    private static AttrPath PathFor(string path)
    {
        var ap = new AttrPath();
        ap.Init(path, string.Empty, AttrPath.DirFlag.False);
        return ap;
    }

    private static GitContext NewContext()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Env["XDG_CONFIG_HOME"] = null;
        ctx.Dirs.Reset();
        ctx.Dirs.Set(GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }

    // ── macro registration order (info/attributes parsed BEFORE workdir) ─

    [Fact]
    public async Task Macro_DefinedInInfo_ExpandsInWorkdirFile()
    {
        // C (attr.c:418-424): info/attributes is preloaded before the workdir
        // root file, so a macro defined there expands in the workdir file.
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);

        string infoDir = Path.Combine(repo.Path, "info");
        Directory.CreateDirectory(infoDir);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "attributes"), "[attr]mymacro text\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt mymacro\n", cancellationToken: TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.True, value.Kind);
    }

    [Fact]
    public async Task Macro_DefinedInWorkdir_DoesNotExpandInInfo()
    {
        // C preloads info BEFORE the workdir root file, so a macro defined in
        // the workdir is NOT registered when info/attributes is parsed — the
        // rule in info does not expand it.
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "[attr]mymacro text\n", cancellationToken: TestContext.Current.CancellationToken);
        string infoDir = Path.Combine(repo.Path, "info");
        Directory.CreateDirectory(infoDir);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "attributes"), "*.txt mymacro\n", cancellationToken: TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    // ── XDG global attributes fallback ────────────────────────────────

    [Fact]
    public async Task Global_XdgFallback_IsLoaded()
    {
        // C (attrcache.c:322-353): with core.attributesfile unset, the XDG
        // file $XDG_CONFIG_HOME/git/attributes is loaded as the global source.
        string home = Path.Combine(_tempDir, "home_" + Guid.NewGuid().ToString("N")[..8]);
        string xdgDir = Path.Combine(home, ".config", "git");
        Directory.CreateDirectory(xdgDir);
        await File.WriteAllTextAsync(Path.Combine(xdgDir, "attributes"), "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);
        _cleanupDirs.Add(home);

        GitContext ctx = NewContext();
        ctx.Env["HOME"] = home;
        ctx.Dirs.Reset();

        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, ctx, TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.True, value.Kind);
    }

    // ── system attributes loaded for bare repositories ────────────────

    [Fact]
    public async Task System_LoadedForBareRepo()
    {
        // C (attr.c:701-708): the system file is pushed unconditionally when
        // NO_SYSTEM is clear — bare repos only change the per-path dir flag.
        string systemDir = Path.Combine(_tempDir, "system_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(systemDir);
        await File.WriteAllTextAsync(Path.Combine(systemDir, "gitattributes"), "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);
        _cleanupDirs.Add(systemDir);

        GitContext ctx = NewContext();
        ctx.Dirs.Set(GitSystemDir.System, systemDir);

        string repoPath = NewRepoDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.True, value.Kind);
    }

    // ── attribute files are revalidated on every lookup ───────────────

    [Fact]
    public async Task WorkdirFile_Edit_PickedUpOnNextLookup()
    {
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        string attrPath = Path.Combine(repo.Workdir!, ".gitattributes");
        await File.WriteAllTextAsync(attrPath, "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);

        GitAttrValue first = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.True, first.Kind);

        // Edit the file (different length so the stamp provably differs).
        await File.WriteAllTextAsync(attrPath, "*.txt -text\n", cancellationToken: TestContext.Current.CancellationToken);

        // C re-stats the file on every lookup (attrcache.c:284-286) — the
        // edit must be visible immediately ("*.txt -text" → text is FALSE).
        GitAttrValue second = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.False, second.Kind);
    }

    // ── core.ignorecase default is FALSE everywhere ───────────────────

    [Fact]
    public async Task IgnoreCase_Default_CaseSensitive()
    {
        // With core.ignorecase unset, the configmap default is false on
        // every platform. Remove any value written by the init probe.
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        if (await repo.Config.GetBoolAsync("core.ignorecase", defaultValue: false, TestContext.Current.CancellationToken))
        {
            await repo.Config.DeleteAsync("core.ignorecase", TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.TXT text\n", cancellationToken: TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    // ── ident binary gate is git_str_is_binary ───────────────────────

    [Fact]
    public async Task Ident_BareCrInput_IsNotBinary_ExpandsId()
    {
        // C (ident.c:106-108 → str.c:1253-1279): bare CRs are whitespace, not
        // a binary signal — $Id$ expands. GitTextStats.IsBinary (cr != crlf)
        // would call this binary (passthrough).
        var filter = new IdentFilter();
        var oid = GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1);
        var source = new GitFilterSource(null, null, oid, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);

        GitApplyResult result = await filter.ApplyAsync(source, "abc\rdef\r$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.True(result.Applied);
        Assert.Contains("$Id: 0123456789abcdef0123456789abcdef01234567 $", System.Text.Encoding.ASCII.GetString(result.Output!));
    }

    [Fact]
    public async Task Ident_Utf16Bom_IsBinary_Passthrough()
    {
        // C: a UTF-16/32 BOM is immediately binary (str.c:1261-1262).
        var filter = new IdentFilter();
        var oid = GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1);
        var source = new GitFilterSource(null, null, oid, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);

        byte[] input = [0xFF, 0xFE, (byte)'a', 0, (byte)'$', 0, (byte)'I', 0, (byte)'d', 0, (byte)'$', 0];
        GitApplyResult result = await filter.ApplyAsync(source, input, TestContext.Current.CancellationToken);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Ident_TextWithNul_IsBinary_Passthrough()
    {
        var filter = new IdentFilter();
        var oid = GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1);
        var source = new GitFilterSource(null, null, oid, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);

        GitApplyResult result = await filter.ApplyAsync(source, "a\0b$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.False(result.Applied);
    }

    // ── buffer-path filter order matches the stream chain ─────────────

    private sealed class MarkerFilter(string name, byte marker) : IFilter
    {
        public string Name => name;
        public string Attributes => name;

        public ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitFilterResult.Apply);

        public ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            byte[] output = new byte[input.Length + 1];
            input.Span.CopyTo(output);
            output[^1] = marker;
            return ValueTask.FromResult(GitApplyResult.WithOutput(output));
        }
    }

    [Fact]
    public async Task ApplyToBuffer_Clean_HighestPriorityFirst()
    {
        // C (filter.c:1078-1079): on clean (TO_ODB) the chain is built
        // forward, so the data flows through the HIGHEST-priority filter
        // first. Entries sorted by priority: [markA(10), markB(20)] → the
        // output appends B then A.
        GitContext ctx = NewContext();
        ctx.Filters.Register("markA", new MarkerFilter("markA", (byte)'A'), 10);
        ctx.Filters.Register("markB", new MarkerFilter("markB", (byte)'B'), 20);

        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt markA markB\n", cancellationToken: TestContext.Current.CancellationToken);

        GitFilterList? filters = await repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);

        byte[] result = await filters!.ApplyToBufferAsync("x"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("xBA"u8.ToArray(), result);
    }

    [Fact]
    public async Task ApplyToBuffer_Smudge_LowestPriorityFirst()
    {
        // C: on smudge (TO_WORKTREE) the chain is built in reverse, so the
        // data flows through the LOWEST-priority filter first: A then B.
        GitContext ctx = NewContext();
        ctx.Filters.Register("markA", new MarkerFilter("markA", (byte)'A'), 10);
        ctx.Filters.Register("markB", new MarkerFilter("markB", (byte)'B'), 20);

        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt markA markB\n", cancellationToken: TestContext.Current.CancellationToken);

        GitFilterList? filters = await repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);

        byte[] result = await filters!.ApplyToBufferAsync("x"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("xAB"u8.ToArray(), result);
    }
}

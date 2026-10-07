using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Filters;

/// <summary>
/// Integration tests for the CRLF filter (<see cref="CrlfFilter"/>)
/// exercised end-to-end through the filter pipeline on commit (clean)
/// and checkout (smudge) against locally-initialized repos with
/// <c>.gitattributes</c> and <c>core.autocrlf</c>/<c>eol</c> configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> <see cref="IdentFilterIntegrationTests"/>
/// covers the <c>ident</c> filter through the filter pipeline, but the
/// <see cref="CrlfFilter"/> — which resolves <c>core.autocrlf</c>,
/// <c>core.eol</c>, the <c>text</c>/<c>eol</c> attributes, and binary
/// detection — had only its apply direction exercised by the existing
/// integration suite. The clean (workdir → ODB) and smudge (ODB → workdir)
/// paths driven by <c>.gitattributes</c> + <c>core.autocrlf</c>, the
/// <c>-text</c> attribute-disable branch, the <c>text=auto</c> binary
/// passthrough branch, the <c>eol=crlf</c>/<c>eol=lf</c> attribute overrides,
/// and the <see cref="GitFilterList.LoadAsync"/>/<see cref="GitFilterList.Contains"/>
/// lookup path were all cold or partially-covered. These tests drive both
/// directions via <see cref="GitIndex.AddByPathAsync"/> (clean) and
/// <see cref="GitRepository.CheckoutHeadAsync"/> (smudge).
/// </para>
/// <para>
/// <b>Attribute-cache ordering.</b> <c>.gitattributes</c> is written FIRST,
/// before any index operation, to avoid the cache-staleness hazard
/// documented in <see cref="Merge.MergeDriverIntegrationTests"/>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/filter/crlf.c</c>
/// (<c>test_filter_crlf__autocrlf_true</c>,
/// <c>test_filter_crlf__attribute</c>,
/// <c>test_filter_crlf__eol</c>), adapted to build the sandbox from
/// scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class CrlfFilterIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-crlf-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a repo, writes <paramref name="gitattributes"/> to the workdir
    /// FIRST, commits the given files on <c>refs/heads/main</c>, and sets
    /// HEAD. Returns the commit OID. If <paramref name="coreAutoCrlf"/> is
    /// non-null, sets <c>core.autocrlf</c> before the commit.
    /// </summary>
    private static async Task<GitOid> InitRepoWithAttributesAsync(
        string path, string gitattributes, IReadOnlyDictionary<string, string> files,
        string? coreAutoCrlf, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        if (coreAutoCrlf is not null)
        {
            await repo.Config.SetStringAsync("core.autocrlf", coreAutoCrlf, ct).ConfigureAwait(false);
        }

        // .gitattributes FIRST.
        await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), gitattributes, ct).ConfigureAwait(false);

        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        foreach ((string p, string c) in files)
        {
            string full = Path.Combine(path, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        await index.AddByPathAsync(".gitattributes", ct).ConfigureAwait(false);
        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct).ConfigureAwait(false);
        await repo.SetHeadAsync("refs/heads/main", ct).ConfigureAwait(false);
        await repo.DisposeAsync().ConfigureAwait(false);
        return commitOid;
    }

    /// <summary>
    /// Reads a blob from the HEAD tree by path, returning its content as a
    /// UTF-8 string.
    /// </summary>
    private static async Task<string> ReadStoredBlobAsync(GitRepository repo, string path, CancellationToken ct)
    {
        GitReference headRef = (await repo.ReferenceResolveAsync("HEAD", ct).ConfigureAwait(false))!;
        GitOid headId = ((GitDirectReference)headRef).Target;
        Commit head = (await repo.ObjectLookupAsync<Commit>(headId, ct).ConfigureAwait(false))!;
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(head.Tree, ct).ConfigureAwait(false))!;
        GitTreeEntry? entry = tree[path];
        Assert.NotNull(entry);
        GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct).ConfigureAwait(false))!;
        return Encoding.UTF8.GetString(blob.Content.Span);
    }

    // ── autocrlf=true clean/smudge round-trip ──────────────────────────

    /// <summary>
    /// With <c>core.autocrlf=true</c>, staging a workdir file containing
    /// CRLF runs the CRLF clean filter, which converts CRLF to LF in the
    /// stored blob. Exercises the clean path + <see cref="CrlfFilter.ApplyAsync"/>
    /// with <see cref="GitFilterMode.ToOdb"/>.
    /// </summary>
    [Fact]
    public async Task Clean_AutoCrlfTrue_CrlfInWorkdir_StoresLf()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithAttributesAsync(
            path,
            "*.txt text\n",
            new Dictionary<string, string> { ["a.txt"] = "line1\r\nline2\r\n" },
            coreAutoCrlf: "true",
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string stored = await ReadStoredBlobAsync(repo, "a.txt", ct);
            // The stored blob has LF only.
            Assert.Contains("line1\nline2\n", stored);
            Assert.DoesNotContain("\r\n", stored);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With <c>core.autocrlf=true</c>, checking out a commit whose stored
    /// blob contains LF runs the smudge filter, which converts LF to CRLF in
    /// the workdir file. Exercises the smudge path +
    /// <see cref="CrlfFilter.ApplyAsync"/> with
    /// <see cref="GitFilterMode.ToWorktree"/>.
    /// </summary>
    [Fact]
    public async Task Smudge_AutoCrlfTrue_Checkout_WritesCrlf()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithAttributesAsync(
            path,
            "*.txt text\n",
            new Dictionary<string, string> { ["a.txt"] = "line1\nline2\n" },
            coreAutoCrlf: "true",
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Delete the workdir file, then force-checkout HEAD to re-run the smudge filter.
            File.Delete(Path.Combine(path, "a.txt"));
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            string workdirContent = await File.ReadAllTextAsync(Path.Combine(path, "a.txt"), ct).ConfigureAwait(false);
            Assert.Contains("line1\r\nline2\r\n", workdirContent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── -text attribute disables filtering ─────────────────────────────

    /// <summary>
    /// <c>.gitattributes</c> with <c>-text</c> disables the CRLF filter
    /// for that path, even with <c>core.autocrlf=true</c>. Staging a CRLF
    /// file with <c>-text</c> stores the blob unchanged (CRLF preserved).
    /// Exercises the attribute-disables-filter branch in
    /// <see cref="CrlfFilter.CheckAsync"/>.
    /// </summary>
    [Fact]
    public async Task TextAttribute_False_NoFiltering_Passthrough()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithAttributesAsync(
            path,
            "*.bin -text\n",
            new Dictionary<string, string> { ["data.bin"] = "line1\r\nline2\r\n" },
            coreAutoCrlf: "true",
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string stored = await ReadStoredBlobAsync(repo, "data.bin", ct);
            // -text: stored unchanged.
            Assert.Contains("line1\r\nline2\r\n", stored);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── text=auto binary detection ────────────────────────────────────

    /// <summary>
    /// <c>.gitattributes</c> with <c>text=auto</c> enables the filter
    /// for text files but passes through binary files. Staging a file with
    /// NUL bytes stores the blob unchanged (no CRLF conversion). Exercises
    /// the binary-detection branch in <see cref="CrlfFilter.CheckAsync"/>.
    /// </summary>
    [Fact]
    public async Task TextAttribute_Auto_DetectsBinary_Passthrough()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        // Binary content: NUL byte.
        byte[] binary = [0x00, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b'];
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // .gitattributes FIRST.
            await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), "* text=auto\n", ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(path, "data.bin"), binary, ct).ConfigureAwait(false);
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            await index.AddByPathAsync("data.bin", ct).ConfigureAwait(false);
            await index.AddByPathAsync(".gitattributes", ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);
            GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/main",
            }, ct).ConfigureAwait(false);
            await repo.SetHeadAsync("refs/heads/main", ct).ConfigureAwait(false);

            GitReference headRef = (await repo.ReferenceResolveAsync("HEAD", ct).ConfigureAwait(false))!;
            GitOid headId = ((GitDirectReference)headRef).Target;
            Commit head = (await repo.ObjectLookupAsync<Commit>(headId, ct).ConfigureAwait(false))!;
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(head.Tree, ct).ConfigureAwait(false))!;
            GitTreeEntry? entry = tree["data.bin"];
            Assert.NotNull(entry);
            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct).ConfigureAwait(false))!;
            // Binary passthrough: stored unchanged.
            Assert.Equal(binary, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── eol attribute overrides ───────────────────────────────────────

    /// <summary>
    /// <c>.gitattributes</c> with <c>eol=crlf</c> forces CRLF in the
    /// workdir on checkout regardless of <c>core.autocrlf</c>. Committing a
    /// LF blob and checking it out produces CRLF in the workdir. Exercises
    /// the <c>eol</c> attribute override in <see cref="CrlfFilter.CheckAsync"/>.
    /// </summary>
    [Fact]
    public async Task EolAttribute_Crlf_ForcesCrlfOnCheckout()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithAttributesAsync(
            path,
            "*.txt eol=crlf\n",
            new Dictionary<string, string> { ["a.txt"] = "line1\nline2\n" },
            coreAutoCrlf: "false",
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            File.Delete(Path.Combine(path, "a.txt"));
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            string workdirContent = await File.ReadAllTextAsync(Path.Combine(path, "a.txt"), ct).ConfigureAwait(false);
            Assert.Contains("line1\r\nline2\r\n", workdirContent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <c>.gitattributes</c> with <c>eol=lf</c> forces LF in the workdir
    /// on checkout. Committing a CRLF blob and checking it out produces LF
    /// in the workdir.
    /// </summary>
    [Fact]
    public async Task EolAttribute_Lf_ForcesLfOnCheckout()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithAttributesAsync(
            path,
            "*.txt eol=lf\n",
            new Dictionary<string, string> { ["a.txt"] = "line1\r\nline2\r\n" },
            coreAutoCrlf: "false",
            ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            File.Delete(Path.Combine(path, "a.txt"));
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            string workdirContent = await File.ReadAllTextAsync(Path.Combine(path, "a.txt"), ct).ConfigureAwait(false);
            Assert.Contains("line1\nline2\n", workdirContent);
            Assert.DoesNotContain("\r\n", workdirContent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── GitFilterList.LoadAsync + Contains ────────────────────────────

    /// <summary>
    /// <see cref="GitFilterList.LoadAsync"/> for a path with the
    /// <c>text</c> attribute set returns a non-null filter list that
    /// <see cref="GitFilterList.Contains"/> recognizes as containing the
    /// <c>crlf</c> filter. Exercises the
    /// <see cref="GitFilterList.LoadAsync"/> + <see cref="GitFilterList.Contains"/>
    /// path directly without going through a full checkout.
    /// </summary>
    [Fact]
    public async Task GitFilterList_Load_TextAttribute_ContainsCrlf()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            // .gitattributes FIRST.
            await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), "*.txt text\n", ct).ConfigureAwait(false);
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            await index.AddByPathAsync(".gitattributes", ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);
            GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/main",
            }, ct).ConfigureAwait(false);
            await repo.SetHeadAsync("refs/heads/main", ct).ConfigureAwait(false);

            GitFilterList? list = await repo.FilterListLoadAsync(
                GitPath.FromUtf8String("a.txt"), blobId: null, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: ct).ConfigureAwait(false);
            Assert.NotNull(list);
            Assert.True(list!.Contains("crlf"));
            list.Dispose();
        }
        finally
        {
            Cleanup(path);
        }
    }
}

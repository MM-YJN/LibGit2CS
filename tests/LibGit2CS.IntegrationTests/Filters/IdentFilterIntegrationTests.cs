using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Filters;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Filters;

/// <summary>
/// Integration tests for the <c>$Id$</c> keyword expansion filter
/// (<see cref="IdentFilter"/>) exercised end-to-end through the filter
/// pipeline on commit (clean) and checkout (smudge) against
/// locally-initialized repos with <c>.gitattributes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> <see cref="IdentFilter"/> had
/// <c>0.9%</c> coverage — the unit <c>IdentFilterTests</c> cover the pure
/// <c>FindId</c>/<c>InsertId</c>/<c>RemoveId</c> helpers against literal
/// byte buffers, but never exercise the filter through the real
/// <see cref="GitFilterList.LoadAsync"/> → <see cref="IFilter.ApplyAsync"/>
/// pipeline driven by <c>.gitattributes</c>. The smudge (ODB→workdir)
/// path, the clean (workdir→ODB) path, the binary-passthrough branch, and
/// the no-OID passthrough branch were all cold in the integration suite.
/// These tests drive both directions via <see cref="GitIndex.AddByPathAsync"/>
/// (clean) and <see cref="GitRepository.CheckoutHeadAsync"/> (smudge) with
/// a <c>.gitattributes</c> file that enables <c>ident</c> for a path, plus
/// a direct <see cref="IdentFilter.ApplyAsync"/> test for the no-OID
/// branch that the checkout path cannot produce.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/filter/ident.c</c>
/// (<c>test_filter_ident__expands</c>,
/// <c>test_filter_ident__contracts</c>), adapted to build the sandbox from
/// scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class IdentFilterIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-ident-" + Guid.NewGuid().ToString("N"));

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
    /// Creates an initial commit on <paramref name="refName"/> whose tree
    /// contains the given (path → content) files. Writes
    /// <paramref name="gitattributes"/> FIRST (before any index operation)
    /// to avoid the attribute-cache staleness hazard documented in
    /// <c>MergeDriverIntegrationTests</c>.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir,
        string gitattributes,
        IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        // .gitattributes must be written before the first AddByPathAsync so
        // the attribute cache picks up the ident attribute.
        string attrPath = Path.Combine(workdir, ".gitattributes");
        await File.WriteAllTextAsync(attrPath, gitattributes, ct).ConfigureAwait(false);

        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        index.Clear();
        foreach ((string p, string c) in files)
        {
            string full = Path.Combine(workdir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        // Stage .gitattributes itself too (so the tree is complete).
        await index.AddByPathAsync(".gitattributes", ct).ConfigureAwait(false);
        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct).ConfigureAwait(false);

        // Point HEAD at the branch we just created so CheckoutHeadAsync and
        // HEAD resolution work (InitAsync defaults HEAD to refs/heads/master;
        // our commits go to refName, so we must repoint HEAD).
        if (parent is null)
        {
            await repo.SetHeadAsync(refName, ct).ConfigureAwait(false);
        }

        return commitOid;
    }

    // ── clean path (workdir → ODB) ──────────────────────────────────────

    /// <summary>
    /// With <c>ident</c> enabled in <c>.gitattributes</c>, staging a
    /// workdir file containing <c>$Id: &lt;hex&gt; $</c> runs the clean
    /// filter (<see cref="IdentFilter.ApplyAsync"/> with
    /// <see cref="GitFilterMode.ToOdb"/>) which contracts it back to
    /// <c>$Id$</c>. The stored blob's content is the contracted form.
    /// Exercises the clean branch + <see cref="GitFilterList"/> clean
    /// pipeline + <see cref="BufferedFilterStream"/>.
    /// </summary>
    [Fact]
    public async Task Clean_ContractsIdOidHex_ToIdDollar()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Write workdir file with an expanded $Id: ... $ and stage it.
            // Use a plausible 40-hex placeholder; the clean filter only
            // checks for the $Id...$ envelope, not the hex validity.
            await CommitFilesAsync(
                repo, path,
                gitattributes: "*.txt ident\n",
                files: new Dictionary<string, string>
                {
                    ["a.txt"] = "prefix $Id: 0123456789abcdef0123456789abcdef01234567 $ suffix\n",
                },
                message: "init\n", refName: "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // The clean filter ran during AddByPathAsync → the stored blob
            // (tree entry) has the contracted $Id$ form. Look up the tree
            // and read the blob.
            GitReference headRef = (await repo.ReferenceResolveAsync("HEAD", ct).ConfigureAwait(false))!;
            GitOid headId = ((GitDirectReference)headRef).Target;
            Commit head = (await repo.ObjectLookupAsync<Commit>(headId, ct).ConfigureAwait(false))!;
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(head.Tree, ct).ConfigureAwait(false))!;
            GitTreeEntry? entry = tree["a.txt"];
            Assert.NotNull(entry);
            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct).ConfigureAwait(false))!;
            string stored = Encoding.UTF8.GetString(blob.Content.Span);

            Assert.Contains("$Id$", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef01234567", stored, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── smudge path (ODB → workdir) ─────────────────────────────────────

    /// <summary>
    /// With <c>ident</c> enabled, checking out a commit whose stored
    /// blob contains <c>$Id$</c> runs the smudge filter
    /// (<see cref="IdentFilter.ApplyAsync"/> with
    /// <see cref="GitFilterMode.ToWorktree"/>) which expands it to
    /// <c>$Id: &lt;40-hex&gt; $</c> using the blob's OID. The workdir file
    /// contains the expanded form with the actual blob OID.
    /// </summary>
    [Fact]
    public async Task Smudge_ExpandsIdDollar_ToOidHex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Commit a file whose stored form is "$Id$" (contracted).
            GitOid commitOid = await CommitFilesAsync(
                repo, path,
                gitattributes: "*.txt ident\n",
                files: new Dictionary<string, string>
                {
                    ["a.txt"] = "before $Id$ after\n",
                },
                message: "init\n", refName: "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Resolve the blob OID (the smudge filter expands using this).
            Commit head = (await repo.ObjectLookupAsync<Commit>(commitOid, ct).ConfigureAwait(false))!;
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(head.Tree, ct).ConfigureAwait(false))!;
            GitTreeEntry? entry = tree["a.txt"];
            Assert.NotNull(entry);
            GitOid blobOid = entry!.Value.Id;

            // Delete the workdir file, then force-checkout HEAD to re-run
            // the smudge filter.
            File.Delete(Path.Combine(path, "a.txt"));
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            string workdirContent = await File.ReadAllTextAsync(Path.Combine(path, "a.txt"), ct).ConfigureAwait(false);
            Assert.Contains($"$Id: {blobOid} $", workdirContent, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── passthrough branches ────────────────────────────────────────────

    /// <summary>
    /// A binary file with <c>ident</c> enabled is passthrough on both
    /// clean and smudge — the <see cref="IdentFilter"/> detects binary
    /// content via <see cref="GitTextStats"/> and returns
    /// <see cref="GitApplyResult.Passthrough"/>. Staging a binary file with
    /// a NUL byte and <c>$Id$</c> produces a stored blob whose content is
    /// unchanged (no contraction).
    /// </summary>
    [Fact]
    public async Task BinaryFile_PassthroughOnIdent_StoredUnchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Binary content: NUL byte + "$Id$" — the filter's IsBinary
            // check fires before any $Id$ processing.
            byte[] binary = [0x00, (byte)'$', (byte)'I', (byte)'d', (byte)'$', 0x01, 0x02];

            // Write .gitattributes first.
            await File.WriteAllTextAsync(Path.Combine(path, ".gitattributes"), "*.bin ident\n", ct).ConfigureAwait(false);

            string binPath = Path.Combine(path, "a.bin");
            await File.WriteAllBytesAsync(binPath, binary, ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            await index.AddByPathAsync("a.bin", ct).ConfigureAwait(false);
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
            GitTreeEntry? entry = tree["a.bin"];
            Assert.NotNull(entry);
            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, ct).ConfigureAwait(false))!;

            // Passthrough: the stored bytes are exactly what was written.
            Assert.Equal(binary, blob.Content.ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="IdentFilter.ApplyAsync"/> with a
    /// <see cref="GitFilterSource"/> whose <see cref="GitFilterSource.Id"/>
    /// is zero (no source OID) returns
    /// <see cref="GitApplyResult.Passthrough"/> on the smudge path — the
    /// <c>source.SourceId is null</c> guard. This branch cannot be reached
    /// via the normal checkout path (blobs always have OIDs in the ODB),
    /// so we exercise it directly via <see cref="InternalsVisibleTo"/>.
    /// </summary>
    [Fact]
    public async Task NoOid_SmudgePassthrough_ReturnsPassthrough()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Construct an IdentFilter directly (internal, reachable via
            // InternalsVisibleTo) and a filter source with a zero OID.
            var filter = new IdentFilter();
            var source = new GitFilterSource(
                Repo: repo,
                Path: GitPath.FromUtf8String("a.txt"),
                Id: GitOid.Empty, // zero OID → SourceId is null
                FileMode: GitFileMode.Regular,
                Mode: GitFilterMode.ToWorktree,
                Flags: GitFilterListFlags.None);

            byte[] input = Encoding.UTF8.GetBytes("before $Id$ after\n");
            GitApplyResult result = await filter.ApplyAsync(source, input, ct).ConfigureAwait(false);

            Assert.Equal(GitApplyResult.Passthrough, result);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

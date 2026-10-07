using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.TestKit;

/// <summary>
/// Reusable builder for locally-initialized non-bare repos with arbitrary
/// commit DAG topologies. Owns the <see cref="GitRepository"/> and
/// <see cref="GitContext"/> (both disposed on <see cref="DisposeAsync"/>)
/// and the temp directory (deleted on disposal).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The revwalk, revparse, graph-query, and
/// merge-base integration test classes all need the same primitives:
/// init a repo, write blobs, build trees, create commits with fixed
/// signatures, create branches/tags. Duplicating the helpers across four
/// files mirrored <see cref="Objects.TagIntegrationTests"/> and
/// <see cref="Checkout.CheckoutIntegrationTests"/>, but the DAG builders
/// (linear, divergent, criss-cross, octopus) are non-trivial and worth
/// sharing. This class consolidates them.
/// </para>
/// <para>
/// <b>Deterministic timestamps.</b> <see cref="Sig"/> uses a fixed epoch
/// (1700000000) and <see cref="CommitAsync"/> advances a per-builder
/// counter so successive commits get strictly increasing timestamps.
/// This makes the commit OIDs reproducible within a single build, which
/// the golden OID sequence tests rely on.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Mirrors the <see cref="Transports.LocalTransportTests"/>
/// pattern: no fixtures, no containers, runs on every build.
/// </para>
/// </remarks>
public sealed class RepoBuilder : IAsyncDisposable
{
    private const long BaseTime = 1700000000;

    private readonly string _path;
    private readonly GitContext _context;
    private GitRepository? _repo;
    private long _nextTime = BaseTime;
    private long _commitIndex;

    /// <summary>
    /// Fixed author/committer signature at the base epoch. All commits
    /// created by this builder use a signature derived from
    /// <see cref="Sig"/> with the commit-specific timestamp.
    /// </summary>
    public static GitSignature Sig => new("t", "t@t", new GitTime(BaseTime, 0));

    /// <summary>The initialized non-bare repository.</summary>
    public GitRepository Repo
        => _repo ?? throw new InvalidOperationException("RepoBuilder has been disposed.");

    /// <summary>The owning context.</summary>
    public GitContext Context => _context;

    /// <summary>The temp directory path holding the repo.</summary>
    public string Path => _path;

    /// <summary>
    /// Initializes a non-bare repo in a fresh temp dir and returns a
    /// builder bound to it. Caller must <see cref="DisposeAsync"/> to
    /// clean up the temp directory.
    /// </summary>
    public RepoBuilder()
    {
        _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "libgit2cs-repobld-" + Guid.NewGuid().ToString("N"));
        _context = new GitContext();
        _repo = GitRepository.InitAsync(_path, isBare: false, _context).GetAwaiter().GetResult();
    }

    // ── Blob / tree writers ─────────────────────────────────────────────

    /// <summary>Writes a blob to the ODB and returns its OID.</summary>
    public async ValueTask<GitOid> WriteBlobAsync(byte[] content, CancellationToken ct)
        => await Repo.ObjectWriteAsync(GitObjectType.Blob, content, ct).ConfigureAwait(false);

    /// <summary>
    /// Builds a tree from the given entries. Each entry is
    /// (relative path, blob OID, file mode). Paths with <c>/</c> separators
    /// create nested subtrees automatically via <see cref="GitTreeBuilder"/>.
    /// </summary>
    public async ValueTask<GitOid> BuildTreeAsync(
        IEnumerable<(string Path, GitOid Blob, GitFileMode Mode)> entries,
        CancellationToken ct)
    {
        using GitTreeBuilder bld = Repo.NewTreeBuilder();
        foreach ((string path, GitOid blob, GitFileMode mode) in entries)
        {
            await bld.InsertAsync(path, blob, mode, ct).ConfigureAwait(false);
        }

        return await bld.WriteAsync(ct).ConfigureAwait(false);
    }

    // ── Commit writers ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a commit with the given tree and parents, advancing the
    /// per-builder timestamp so commits are strictly time-ordered. If
    /// <paramref name="updateRef"/> is non-null, the ref is updated and
    /// HEAD is set to point at it.
    /// </summary>
    public async ValueTask<GitOid> CommitAsync(
        GitOid tree,
        IReadOnlyList<GitOid> parents,
        string message,
        string? updateRef = null,
        CancellationToken ct = default)
    {
        long time = _nextTime++;
        var sig = new GitSignature(Sig.Name, Sig.Email, new GitTime(time, 0));
        GitOid commitOid = await Repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = updateRef,
        }, ct).ConfigureAwait(false);

        if (updateRef is not null)
        {
            await Repo.SetHeadAsync(updateRef, ct).ConfigureAwait(false);
        }

        _commitIndex++;
        return commitOid;
    }

    /// <summary>
    /// Convenience: writes a single file to the workdir, stages it, writes
    /// the tree, and creates a commit. If <paramref name="parent"/> is null
    /// the commit is a root; otherwise it has that single parent. Returns
    /// the commit OID.
    /// </summary>
    public async ValueTask<GitOid> CommitFileAsync(
        string path,
        byte[] content,
        string message,
        GitOid? parent = null,
        string? updateRef = null,
        CancellationToken ct = default)
    {
        string workdir = Repo.Workdir!;
        string fullPath = System.IO.Path.Combine(workdir, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content, ct).ConfigureAwait(false);

        GitIndex index = await Repo.GetIndexAsync(ct).ConfigureAwait(false);
        await index.AddByPathAsync(path, ct).ConfigureAwait(false);
        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);

        IReadOnlyList<GitOid> parents = parent is null ? Array.Empty<GitOid>() : new[] { parent.Value };
        return await CommitAsync(treeOid, parents, message, updateRef, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a merge commit with the given parents and tree. The caller
    /// is responsible for building the merged tree (this helper does not
    /// run a real merge — it just writes a commit with >1 parents).
    /// The commit is created WITHOUT <c>update_ref</c> and the ref is then
    /// moved with a forced create: C's <c>git_commit_create</c> rejects a
    /// merge whose parent[0] is not the current ref tip with GIT_EMODIFIED
    /// when update_ref is set (commit.c:109-117), so the only C-faithful
    /// way to land a merge commit on a ref is to update the ref separately.
    /// </summary>
    public async ValueTask<GitOid> MergeCommitAsync(
        GitOid tree,
        IReadOnlyList<GitOid> parents,
        string message,
        string? updateRef = null,
        CancellationToken ct = default)
    {
        long time = _nextTime++;
        var sig = new GitSignature(Sig.Name, Sig.Email, new GitTime(time, 0));
        GitOid commitOid = await Repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = null,
        }, ct).ConfigureAwait(false);

        if (updateRef is not null)
        {
            await Repo.ReferenceCreateAsync(updateRef, commitOid, force: true, logMessage: "merge", cancellationToken: ct).ConfigureAwait(false);
            await Repo.SetHeadAsync(updateRef, ct).ConfigureAwait(false);
        }

        _commitIndex++;
        return commitOid;
    }

    // ── Ref writers ─────────────────────────────────────────────────────

    /// <summary>Creates a branch <c>refs/heads/&lt;name&gt;</c> at the target commit.</summary>
    public async ValueTask CreateBranchAsync(string name, GitOid target, CancellationToken ct)
        => await Repo.ReferenceCreateAsync($"refs/heads/{name}", target, force: false, cancellationToken: ct).ConfigureAwait(false);

    /// <summary>Creates a lightweight tag <c>refs/tags/&lt;name&gt;</c> at the target commit.</summary>
    public async ValueTask CreateTagAsync(string name, GitOid target, CancellationToken ct)
        => await Repo.ReferenceCreateAsync($"refs/tags/{name}", target, force: false, cancellationToken: ct).ConfigureAwait(false);

    // ── HEAD / checkout ─────────────────────────────────────────────────

    /// <summary>
    /// Points HEAD at the given <paramref name="refName"/> (symbolic ref).
    /// Matches <c>git checkout &lt;branch&gt;</c> for the ref-update part
    /// (no workdir file sync — rebase init does the checkout itself).
    /// </summary>
    public async ValueTask CheckoutRefAsync(string refName, CancellationToken ct)
        => await Repo.SetHeadAsync(refName, ct).ConfigureAwait(false);

    /// <summary>
    /// Detaches HEAD to point directly at <paramref name="commit"/> (direct
    /// ref). Matches <c>git checkout &lt;sha&gt;</c> for the ref-update part.
    /// </summary>
    public async ValueTask DetachHeadAsync(GitOid commit, CancellationToken ct)
        => await Repo.ReferenceCreateAsync("HEAD", commit, force: true, logMessage: "detach", cancellationToken: ct).ConfigureAwait(false);

    // ── Common DAG topologies ────────────────────────────────────────────

    /// <summary>
    /// Builds a linear history of <paramref name="contentVersions.Length"/>
    /// commits, all modifying the same file at <paramref name="path"/> with
    /// successive content versions. Returns the commit OIDs in order
    /// (index 0 = root, last = tip). HEAD ends at <paramref name="branch"/>.
    /// Used by blame tests which need a multi-commit history on one file.
    /// </summary>
    public async Task<GitOid[]> BuildSameFileHistoryAsync(
        string path,
        byte[][] contentVersions,
        string branch = "refs/heads/main",
        CancellationToken ct = default)
    {
        var commits = new GitOid[contentVersions.Length];
        GitOid? parent = null;
        for (int i = 0; i < contentVersions.Length; i++)
        {
            commits[i] = await CommitFileAsync(
                path,
                contentVersions[i],
                $"commit {i}\n",
                parent,
                updateRef: branch,
                ct: ct).ConfigureAwait(false);
            parent = commits[i];
        }

        return commits;
    }

    /// <summary>
    /// Builds a divergent DAG for rebase/conflict tests: a root commit
    /// creates <paramref name="path"/> with <paramref name="baseContent"/>,
    /// then two branches diverge — <c>refs/heads/{mainBranch}</c> gets a
    /// commit modifying the file to <paramref name="mainEdit"/>, and
    /// <c>refs/heads/{featureBranch}</c> gets a commit modifying the file to
    /// <paramref name="featureEdit"/>. Returns
    /// (<c>root</c>, <c>mainTip</c>, <c>featureTip</c>). HEAD ends on
    /// <c>refs/heads/{featureBranch}</c>.
    /// </summary>
    public async Task<(GitOid Root, GitOid MainTip, GitOid FeatureTip)> BuildDivergentFileHistoryAsync(
        string path,
        byte[] baseContent,
        byte[] mainEdit,
        byte[] featureEdit,
        string mainBranch = "main",
        string featureBranch = "feature",
        CancellationToken ct = default)
    {
        string mainRef = $"refs/heads/{mainBranch}";
        string featureRef = $"refs/heads/{featureBranch}";

        GitOid root = await CommitFileAsync(
            path, baseContent, "root\n", parent: null, updateRef: mainRef, ct: ct).ConfigureAwait(false);

        GitOid mainTip = await CommitFileAsync(
            path, mainEdit, "main edit\n", parent: root, updateRef: mainRef, ct: ct).ConfigureAwait(false);

        GitOid featureTip = await CommitFileAsync(
            path, featureEdit, "feature edit\n", parent: root, updateRef: featureRef, ct: ct).ConfigureAwait(false);

        await CheckoutRefAsync(featureRef, ct).ConfigureAwait(false);
        return (root, mainTip, featureTip);
    }

    /// <summary>
    /// Builds a linear history of <paramref name="count"/> commits, each
    /// adding a file <c>fileN.txt</c>. Returns the commit OIDs in order
    /// (index 0 = root, last = tip). HEAD ends at <c>refs/heads/main</c>.
    /// </summary>
    public async Task<GitOid[]> BuildLinearHistoryAsync(int count, string branch = "refs/heads/main", CancellationToken ct = default)
    {
        var commits = new GitOid[count];
        GitOid? parent = null;
        for (int i = 0; i < count; i++)
        {
            commits[i] = await CommitFileAsync(
                $"file{i}.txt",
                Encoding.UTF8.GetBytes($"content{i}\n"),
                $"commit {i}\n",
                parent,
                updateRef: branch,
                ct: ct).ConfigureAwait(false);
            parent = commits[i];
        }

        return commits;
    }

    /// <summary>
    /// Builds a divergent DAG: a root commit <c>R</c>, then two children
    /// <c>A</c> (on <c>main</c>) and <c>B</c> (on <c>feature</c>), then a
    /// merge commit <c>M</c> on <c>main</c> with parents [<c>A</c>, <c>B</c>].
    /// Returns (root, a, b, merge).
    /// </summary>
    public async Task<(GitOid Root, GitOid A, GitOid B, GitOid Merge)> BuildMergeDagAsync(CancellationToken ct = default)
    {
        GitOid root = await CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);

        GitOid a = await CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: root, updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);

        GitOid b = await CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: root, updateRef: "refs/heads/feature", ct: ct).ConfigureAwait(false);

        // Build a merge tree that contains base.txt, a.txt, and b.txt.
        GitOid baseBlob = await WriteBlobAsync("base\n"u8.ToArray(), ct).ConfigureAwait(false);
        GitOid aBlob = await WriteBlobAsync("a\n"u8.ToArray(), ct).ConfigureAwait(false);
        GitOid bBlob = await WriteBlobAsync("b\n"u8.ToArray(), ct).ConfigureAwait(false);
        GitOid mergeTree = await BuildTreeAsync(
            [
                ("base.txt", baseBlob, GitFileMode.Regular),
                ("a.txt", aBlob, GitFileMode.Regular),
                ("b.txt", bBlob, GitFileMode.Regular),
            ], ct).ConfigureAwait(false);

        GitOid merge = await MergeCommitAsync(mergeTree, [a, b], "merge\n", updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);
        return (root, a, b, merge);
    }

    /// <summary>
    /// Builds a criss-cross merge topology for testing multiple merge
    /// bases: root R, then two parallel branches that both merge from each
    /// other, producing two merge bases for the two tips. Returns every
    /// commit in the DAG, in topological order:
    /// (root, left, right, m1, m2, tip1, tip2). The merge bases of
    /// (tip1, tip2) are the two merge commits {m1, m2}.
    /// </summary>
    public async Task<(GitOid Root, GitOid Left, GitOid Right, GitOid M1, GitOid M2, GitOid Tip1, GitOid Tip2)> BuildCrissCrossDagAsync(CancellationToken ct = default)
    {
        // R: root
        GitOid root = await CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct).ConfigureAwait(false);

        // Two children of root on separate branches.
        GitOid left = await CommitFileAsync("left.txt", "left\n"u8.ToArray(), "left\n", parent: root, updateRef: "refs/heads/left", ct: ct).ConfigureAwait(false);
        GitOid right = await CommitFileAsync("right.txt", "right\n"u8.ToArray(), "right\n", parent: root, updateRef: "refs/heads/right", ct: ct).ConfigureAwait(false);

        // m1: merge of left + right (on left branch)
        GitOid m1Tree = await BuildTreeAsync(
            [
                ("base.txt", await WriteBlobAsync("base\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("left.txt", await WriteBlobAsync("left\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("right.txt", await WriteBlobAsync("right\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
            ], ct).ConfigureAwait(false);
        GitOid m1 = await MergeCommitAsync(m1Tree, [left, right], "m1\n", updateRef: "refs/heads/left", ct: ct).ConfigureAwait(false);

        // m2: merge of left + right (on right branch) — same parents, different tree.
        GitOid m2Tree = await BuildTreeAsync(
            [
                ("base.txt", await WriteBlobAsync("base\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("left.txt", await WriteBlobAsync("left\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
                ("right.txt", await WriteBlobAsync("right\n"u8.ToArray(), ct).ConfigureAwait(false), GitFileMode.Regular),
            ], ct).ConfigureAwait(false);
        GitOid m2 = await MergeCommitAsync(m2Tree, [left, right], "m2\n", updateRef: "refs/heads/right", ct: ct).ConfigureAwait(false);

        // tip1: child of m1; tip2: child of m2. Merge bases of (tip1, tip2) = {m1, m2}.
        GitOid tip1 = await CommitFileAsync("tip1.txt", "tip1\n"u8.ToArray(), "tip1\n", parent: m1, updateRef: "refs/heads/left", ct: ct).ConfigureAwait(false);
        GitOid tip2 = await CommitFileAsync("tip2.txt", "tip2\n"u8.ToArray(), "tip2\n", parent: m2, updateRef: "refs/heads/right", ct: ct).ConfigureAwait(false);
        return (root, left, right, m1, m2, tip1, tip2);
    }

    // ── Disposal ────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the repo and context and deletes the temp directory.
    /// Best-effort: temp-dir deletion failures are swallowed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_repo is not null)
        {
            await _repo.DisposeAsync().ConfigureAwait(false);
            _repo = null;
        }

        _context.Dispose();

        try
        {
            Directory.Delete(_path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;

using LibGit2CS.Checkout;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;
using LibGit2CS.Revwalk;

using CommitOps = LibGit2CS.Objects.Commit;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Rebase;

/// <summary>
/// Rebase state machine. Managed port of <c>src/libgit2/rebase.c</c> (1,469 LOC).
/// </summary>
/// <remarks>
/// <para>
/// Rebase replays commits from <c>branch</c> on top of <c>onto</c>, skipping
/// commits already reachable from <c>upstream</c>. Only <c>GIT_REBASE_MERGE</c>
/// (cherry-pick based) rebase is implemented; <c>GIT_REBASE_APPLY</c> and
/// <c>GIT_REBASE_INTERACTIVE</c> are not supported.
/// </para>
/// <para>
/// <b>Lifecycle:</b> <see cref="InitAsync"/> → <see cref="NextAsync"/>/<see cref="CommitOps"/>
/// loop → <see cref="FinishAsync"/> (or <see cref="AbortAsync"/> at any point). For
/// in-memory rebase, <see cref="GitRebaseOptions.InMemory"/> = <c>true</c>
/// skips all filesystem state and working directory interaction.
/// </para>
/// </remarks>
public sealed class GitRebase : IDisposable
{
    /// <summary>Indicates that a rebase operation is not (yet) in progress. Matches <c>GIT_REBASE_NO_OPERATION</c>.</summary>
    public const ulong NoOperation = ulong.MaxValue;

    private GitRepository? _repo;
    private readonly GitRebaseOptions _options;
    private RebaseType _type;
    private string? _statePath;

    private bool _headDetached;
    private bool _inmemory;
    private bool _quiet;
    private bool _started;

    private List<GitRebaseOperation> _operations = [];
    private int _current;

    // In-memory rebase state.
    private GitIndex? _index;
    private CommitOps? _lastCommit;

    // On-disk merge rebase state.
    private GitOid _origHeadId;
    private string? _origHeadName;

    private GitOid _ontoId;
    private string? _ontoName;

    private bool _disposed;

    private GitRebase(GitRepository repo, GitRebaseOptions? options)
    {
        _repo = repo;
        _options = options ?? GitRebaseOptions.Default;
        _type = RebaseType.Merge;
    }

    // ── Public properties ────────────────────────────────────────────

    /// <summary>The repository being rebased.</summary>
    internal GitRepository Repository => _repo ?? throw new ObjectDisposedException(nameof(GitRebase));

    /// <summary>
    /// The repository, throwing if it has been disposed (<c>_repo</c> is cleared
    /// in <see cref="Dispose"/>). Same as <see cref="Repository"/> — alias
    /// used by step methods that capture it into a local <c>repo</c>.
    /// </summary>
    private GitRepository Repo => _repo ?? throw new ObjectDisposedException(nameof(GitRebase));

    /// <summary>The path to the state directory (<c>.git/rebase-merge/</c>).</summary>
    internal string? StatePath => _statePath;

    /// <summary>
    /// The on-disk state directory, throwing if this rebase is in-memory or not
    /// yet initialized. Used by methods that read/write state files.
    /// </summary>
    internal string CurrentStatePath => _statePath ?? throw new InvalidOperationException("rebase has no on-disk state path");

    /// <summary>
    /// The original HEAD name (branch or detached), throwing if not yet set.
    /// Populated when a rebase is opened or started.
    /// </summary>
    private string CurrentOrigHeadName => _origHeadName ?? throw new InvalidOperationException("rebase original HEAD name not set");

    /// <summary>Whether HEAD was detached when rebase started.</summary>
    internal bool HeadDetached
    {
        get => _headDetached;
        set => _headDetached = value;
    }

    /// <summary>Whether this is an in-memory rebase.</summary>
    internal bool InMemory => _inmemory;

    /// <summary>Quiet flag (written to state file for interop).</summary>
    internal bool Quiet
    {
        get => _quiet;
        set => _quiet = value;
    }

    /// <summary>Whether <see cref="NextAsync"/> has been called at least once.</summary>
    internal bool Started
    {
        get => _started;
        set => _started = value;
    }

    /// <summary>Current operation index (0-based).</summary>
    internal int Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>The list of rebase operations.</summary>
    internal List<GitRebaseOperation> Operations
    {
        get => _operations;
        set => _operations = value;
    }

    /// <summary>The original HEAD OID before rebase.</summary>
    internal GitOid OrigHeadId
    {
        get => _origHeadId;
        set => _origHeadId = value;
    }

    /// <summary>The original HEAD ref name (or null if detached).</summary>
    internal string? OrigHeadName
    {
        get => _origHeadName;
        set => _origHeadName = value;
    }

    /// <summary>The onto commit OID.</summary>
    internal GitOid OntoId
    {
        get => _ontoId;
        set => _ontoId = value;
    }

    /// <summary>The onto name (branch name or OID string).</summary>
    internal string? OntoName
    {
        get => _ontoName;
        set => _ontoName = value;
    }

    /// <summary>The in-memory index (for in-memory rebase).</summary>
    internal GitIndex? InMemoryIndex
    {
        get => _index;
        set => _index = value;
    }

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>Gets the original HEAD ref name (or "detached HEAD").</summary>
    public string? OrigHeadNameProperty => _origHeadName;

    /// <summary>Gets the original HEAD OID.</summary>
    public GitOid OrigHeadIdProperty => _origHeadId;

    /// <summary>Gets the onto name.</summary>
    public string? OntoNameProperty => _ontoName;

    /// <summary>Gets the onto OID.</summary>
    public GitOid OntoIdProperty => _ontoId;

    /// <summary>Gets the total number of rebase operations.</summary>
    public int OperationCount => _operations.Count;

    /// <summary>
    /// Gets the index of the current operation, or <see cref="NoOperation"/>
    /// if <see cref="NextAsync"/> has not been called.
    /// </summary>
    public ulong CurrentOperation => _started ? (ulong)_current : NoOperation;

    /// <summary>Gets the operation at the given index.</summary>
    public GitRebaseOperation this[int index] => _operations[index];

    // ── Init ─────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a rebase. Matches <c>git_rebase_init</c> (rebase.c:689-757).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="branch">The terminal commit to rebase, or null to use HEAD.</param>
    /// <param name="upstream">The commit to begin rebasing from, or null to rebase all reachable commits.</param>
    /// <param name="onto">The branch to rebase onto, or null to rebase onto <paramref name="upstream"/>.</param>
    /// <param name="options">Rebase options, or null for defaults.</param>
    /// <returns>A new <see cref="GitRebase"/> state machine.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<GitRebase> InitAsync(
        GitRepository repo,
        GitAnnotatedCommit? branch,
        GitAnnotatedCommit? upstream,
        GitAnnotatedCommit? onto,
        GitRebaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (upstream is null && onto is null)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "upstream and onto cannot both be null",
                GitErrorCategory.Rebase);
        }

        // If onto is null, use upstream as onto. Matches rebase.c:708-709.
        onto ??= upstream;
        Debug.Assert(onto is not null);

        bool inmemory = options?.InMemory ?? false;

        // Safety checks for non-in-memory rebase.
        if (!inmemory)
        {
            if (repo.IsBare)
            {
                throw new GitException(
                    GitErrorCode.BareRepo,
                    "cannot rebase into a bare repository",
                    GitErrorCategory.Repository);
            }

            EnsureNotInProgress(repo);
            await EnsureNotDirtyAsync(repo, checkIndex: true, checkWorkdir: true, failWith: GitErrorCode.Error, cancellationToken).ConfigureAwait(false);
        }

        // If branch is null, use HEAD resolved with git_repository_head
        // semantics (repository.c:2963-2982): a symbolic HEAD resolves to the
        // underlying branch ref (so branch.Ref is "refs/heads/<branch>"), a
        // detached HEAD stays the direct "HEAD" ref, and an unborn target
        // yields GIT_EUNBORNBRANCH. Matches rebase.c:721-727.
        if (branch is null)
        {
            GitReference headRef = await repo.HeadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.UnbornBranch,
                    "cannot rebase - you are on an unborn branch",
                    GitErrorCategory.Rebase);
            branch = await GitAnnotatedCommit.FromRefAsync(repo, headRef, cancellationToken).ConfigureAwait(false);
        }

        var rebase = new GitRebase(repo, options)
        {
            _inmemory = inmemory,
        };

        try
        {
            // Compute the operation list.
            await InitOperationsAsync(rebase, repo, branch, upstream, onto, cancellationToken).ConfigureAwait(false);

            if (inmemory)
            {
                await InitInMemoryAsync(rebase, repo, onto, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InitMergeAsync(rebase, repo, branch, upstream, onto, cancellationToken).ConfigureAwait(false);
            }

            return rebase;
        }
        catch
        {
            rebase.Dispose();
            throw;
        }
    }

    // ── Open ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an existing in-progress rebase. Matches <c>git_rebase_open</c>
    /// (rebase.c:316-398).
    /// </summary>
    /// <param name="repo">The repository with an in-progress rebase.</param>
    /// <param name="options">Rebase options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="GitRebase"/> state machine resume from the saved state.</returns>
    internal static async Task<GitRebase> OpenAsync(GitRepository repo, GitRebaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var rebase = new GitRebase(repo, options);

        // Detect rebase type.
        rebase._type = RebaseStateFiles.DetectType(repo, out rebase._statePath);

        if (rebase._type == RebaseType.None)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                "there is no rebase in progress",
                GitErrorCategory.Rebase);
        }

        // Read head-name.
        string origHeadName = (await RebaseStateFiles.ReadFileAsync(rebase.CurrentStatePath, RebaseStateFiles.HeadNameFile, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"rebase state file '{RebaseStateFiles.HeadNameFile}' not found",
                GitErrorCategory.Rebase);

        if (origHeadName == RebaseStateFiles.OrigDetachedHead)
        {
            rebase._headDetached = true;
        }

        // Read orig-head (fallback to head for old git compat). Matches rebase.c:357-364.
        GitHashAlgorithmKind oidType = repo.ObjectFormat;
        GitOid? origHeadId = await RebaseStateFiles.ReadOidAsync(rebase.CurrentStatePath, RebaseStateFiles.OrigHeadFile, oidType, cancellationToken).ConfigureAwait(false);
        origHeadId ??= await RebaseStateFiles.ReadOidAsync(rebase.CurrentStatePath, RebaseStateFiles.HeadFile, oidType, cancellationToken).ConfigureAwait(false);

        if (origHeadId is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"rebase state file '{RebaseStateFiles.OrigHeadFile}' not found",
                GitErrorCategory.Rebase);
        }

        rebase._origHeadId = origHeadId.Value;

        // Read onto.
        GitOid ontoId = (await RebaseStateFiles.ReadOidAsync(rebase.CurrentStatePath, RebaseStateFiles.OntoFile, oidType, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"rebase state file '{RebaseStateFiles.OntoFile}' not found",
                GitErrorCategory.Rebase);
        rebase._ontoId = ontoId;

        // Set orig_head_name (null if detached).
        if (!rebase._headDetached)
        {
            rebase._origHeadName = origHeadName;
        }

        // Dispatch by type.
        switch (rebase._type)
        {
            case RebaseType.Interactive:
                // C (rebase.c:373-376): git_error_set(GIT_ERROR_REBASE,
                // "interactive rebase is not supported"); error = -1 — the
                // generic GIT_ERROR code, NOT GIT_ENOTSUPPORTED.
                throw new GitException(
                    GitErrorCode.Error,
                    "interactive rebase is not supported",
                    GitErrorCategory.Rebase);

            case RebaseType.Merge:
                await RebaseState.OpenMergeAsync(rebase, cancellationToken).ConfigureAwait(false);
                break;

            case RebaseType.Apply:
                // C (rebase.c:380-383): "patch application rebase is not
                // supported"; error = -1.
                throw new GitException(
                    GitErrorCode.Error,
                    "patch application rebase is not supported",
                    GitErrorCategory.Rebase);

            default:
                throw new InvalidOperationException("unreachable: invalid rebase type");
        }

        return rebase;
    }

    // ── Next ────────────────────────────────────────────────────────

    /// <summary>
    /// Performs the next rebase operation. Matches <c>git_rebase_next</c>
    /// (rebase.c:909-929).
    /// </summary>
    /// <returns>The operation that was applied.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.IterOver"/> if there are no more operations.
    /// </exception>
    public async Task<GitRebaseOperation> NextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Advance cursor.
        if (MoveNext())
        {
            throw new GitException(
                GitErrorCode.IterOver,
                "no more rebase operations",
                GitErrorCategory.Rebase);
        }

        if (_inmemory)
        {
            return await NextInMemoryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_type == RebaseType.Merge)
        {
            return await NextMergeAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("unreachable: invalid rebase type in Next");
    }

    // ── Commit ───────────────────────────────────────────────────────

    /// <summary>
    /// Commits the current patch. Matches <c>git_rebase_commit</c>
    /// (rebase.c:1157-1180).
    /// </summary>
    /// <param name="author">Author signature, or null to use the original commit's author.</param>
    /// <param name="committer">Committer signature (required).</param>
    /// <param name="messageEncoding">Message encoding, or null for default.</param>
    /// <param name="message">Commit message, or null to use the original commit's message.</param>
    /// <returns>The OID of the newly created commit.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Unmerged"/> if there are unresolved conflicts.
    /// <see cref="GitErrorCode.Applied"/> if the patch was already applied.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitOid> CommitAsync(
        GitSignature? author,
        GitSignature committer,
        string? messageEncoding = null,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(committer);

        if (_inmemory)
        {
            return await CommitInMemoryAsync(author, committer, messageEncoding, message, cancellationToken).ConfigureAwait(false);
        }

        if (_type == RebaseType.Merge)
        {
            return await CommitMergeAsync(author, committer, messageEncoding, message, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("unreachable: invalid rebase type in Commit");
    }

    /// <summary>
    /// Gets the in-memory index produced by the last <see cref="NextAsync"/> call.
    /// Only valid for in-memory rebases. Matches <c>git_rebase_inmemory_index</c>
    /// (rebase.c:931-943).
    /// </summary>
    public GitIndex GetInMemoryIndex()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _index
            ?? throw new GitException(
                GitErrorCode.Invalid,
                "no in-memory index available",
                GitErrorCategory.Rebase);
    }

    // ── Abort ────────────────────────────────────────────────────────

    /// <summary>
    /// Aborts the rebase, resetting HEAD and working directory to the
    /// pre-rebase state. Matches <c>git_rebase_abort</c> (rebase.c:1182-1216).
    /// </summary>
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inmemory)
        {
            return;
        }

        GitRepository repo = Repo;

        // Restore HEAD: symbolic if not detached, direct if detached.
        if (_headDetached)
        {
            await repo.Refs.CreateAsync("HEAD", _origHeadId, force: true, logMessage: "rebase: aborting", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await repo.Refs.CreateSymbolicAsync("HEAD", CurrentOrigHeadName, force: true, logMessage: "rebase: aborting", cancellationToken).ConfigureAwait(false);
        }

        // Hard reset to orig-head.
        CommitOps origHeadCommit = await repo.Objects.LookupAsync<CommitOps>(_origHeadId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"commit {_origHeadId} not found",
                GitErrorCategory.Object);
        await repo.ResetAsync(origHeadCommit, GitResetMode.Hard, _options.CheckoutOptions, cancellationToken).ConfigureAwait(false);

        // Remove the state directory.
        Cleanup();
    }

    // ── Finish ──────────────────────────────────────────────────────

    /// <summary>
    /// Finishes the rebase: moves the original branch ref to the new HEAD,
    /// copies notes (if configured), and cleans up state. Matches
    /// <c>git_rebase_finish</c> (rebase.c:1395-1413).
    /// </summary>
    /// <param name="signature">Identity for note-copying operations, or null for default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task FinishAsync(GitSignature? signature = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inmemory)
        {
            return;
        }

        if (!_headDetached)
        {
            await ReturnToOrigHeadAsync(cancellationToken).ConfigureAwait(false);
        }

        await CopyNotesAsync(signature, cancellationToken).ConfigureAwait(false);
        Cleanup();
    }

    // ── Init helpers ────────────────────────────────────────────────

    private static async Task InitOperationsAsync(
        GitRebase rebase,
        GitRepository repo,
        GitAnnotatedCommit branch,
        GitAnnotatedCommit? upstream,
        GitAnnotatedCommit onto,
        CancellationToken cancellationToken)
    {
        // If upstream is null, use onto. Matches rebase.c:587-588.
        upstream ??= onto;

        var operations = new List<GitRebaseOperation>();
        using var walker = new GitRevWalker(repo);
        await walker.PushAsync(branch.Id, cancellationToken).ConfigureAwait(false);
        await walker.HideAsync(upstream.Id, cancellationToken).ConfigureAwait(false);
        walker.Sort = GitSortMode.Reverse;

        await foreach (GitOid oid in walker.WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            CommitOps? commit = await repo.Objects.LookupAsync<CommitOps>(oid, cancellationToken).ConfigureAwait(false);
            if (commit is null)
            {
                continue;
            }

            // Skip merge commits (parent_count > 1). Matches rebase.c:601-605.
            if (commit.Parents.Count > 1)
            {
                continue;
            }

            operations.Add(GitRebaseOperation.CreatePick(oid));
        }

        rebase._operations = operations;
    }

    private static async Task InitMergeAsync(
        GitRebase rebase,
        GitRepository repo,
        GitAnnotatedCommit branch,
        GitAnnotatedCommit? _,
        GitAnnotatedCommit onto,
        CancellationToken cancellationToken)
    {
        // Set state path.
        rebase._statePath = Path.Join(repo.Path, RebaseStateFiles.RebaseMergeDir);

        // Determine if HEAD was detached.
        if (branch.Ref is not null and not "HEAD")
        {
            rebase._origHeadName = branch.Ref;
        }
        else
        {
            rebase._headDetached = true;
        }

        rebase._ontoName = ComputeOntoName(onto);
        rebase._quiet = rebase._options.Quiet;
        rebase._origHeadId = branch.Id;
        rebase._ontoId = onto.Id;

        // Write state files.
        await RebaseState.SetupFilesAsync(rebase, cancellationToken).ConfigureAwait(false);

        // Checkout the onto commit.
        CommitOps ontoCommit = await repo.Objects.LookupAsync<CommitOps>(onto.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"onto commit {onto.Id} not found",
                GitErrorCategory.Object);

        string reflog = $"rebase: checkout {ComputeOntoName(onto)}";
        GitTree ontoTree = await repo.Objects.LookupAsync<GitTree>(ontoCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"onto tree {ontoCommit.Tree} not found",
                GitErrorCategory.Object);
        await repo.CheckoutTreeAsync(ontoTree, rebase._options.CheckoutOptions, cancellationToken).ConfigureAwait(false);

        // Update HEAD to point at onto.
        await repo.Refs.CreateAsync("HEAD", onto.Id, force: true, logMessage: reflog, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InitInMemoryAsync(
        GitRebase rebase,
        GitRepository repo,
        GitAnnotatedCommit onto,
        CancellationToken cancellationToken = default)
    {
        // Just store the onto commit as last_commit.
        rebase._lastCommit = await repo.Objects.LookupAsync<CommitOps>(onto.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"onto commit {onto.Id} not found",
                GitErrorCategory.Object);
    }

    private static string ComputeOntoName(GitAnnotatedCommit onto)
    {
        if (onto.Ref is not null && onto.Ref.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return onto.Ref["refs/heads/".Length..];
        }

        if (onto.Ref is not null)
        {
            return onto.Ref;
        }

        return onto.Id.ToString();
    }

    // ── Next helpers ────────────────────────────────────────────────

    private bool MoveNext()
    {
        int next = _started ? _current + 1 : 0;

        if (next == _operations.Count)
        {
            return true; // GIT_ITEROVER
        }

        _started = true;
        _current = next;
        return false;
    }

    private async Task<GitRebaseOperation> NextMergeAsync(CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;
        GitRebaseOperation operation = _operations[_current];

        CommitOps currentCommit = await repo.Objects.LookupAsync<CommitOps>(operation.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"commit {operation.Id} not found",
                GitErrorCategory.Object);

        GitTree currentTree = await repo.Objects.LookupAsync<GitTree>(currentCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {currentCommit.Tree} not found",
                GitErrorCategory.Object);

        // Get HEAD tree.
        GitReference headRef = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD not found",
                GitErrorCategory.Reference);
        CommitOps headCommit = await repo.Objects.LookupAsync<CommitOps>(((GitDirectReference)headRef).Target, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD commit not found",
                GitErrorCategory.Object);
        GitTree headTree = await repo.Objects.LookupAsync<GitTree>(headCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {headCommit.Tree} not found",
                GitErrorCategory.Object);

        // Get parent tree (may be null for root commits).
        GitTree? parentTree = null;
        int parentCount = currentCommit.Parents.Count;
        if (parentCount > 1)
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot rebase a merge commit",
                GitErrorCategory.Rebase);
        }

        if (parentCount == 1)
        {
            CommitOps parentCommit = await repo.Objects.LookupAsync<CommitOps>(currentCommit.ParentId(0), cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"parent commit not found",
                    GitErrorCategory.Object);
            parentTree = await repo.Objects.LookupAsync<GitTree>(parentCommit.Tree, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"parent tree not found",
                    GitErrorCategory.Object);
        }

        // Write msgnum and current state files.
        await RebaseStateFiles.WriteFileAsync(repo, CurrentStatePath, RebaseStateFiles.MsgNumFile, $"{_current + 1}\n", cancellationToken).ConfigureAwait(false);
        await RebaseStateFiles.WriteFileAsync(repo, CurrentStatePath, RebaseStateFiles.CurrentFile, operation.Id + "\n", cancellationToken).ConfigureAwait(false);

        // Normalize checkout options.
        GitCheckoutOptions checkoutOpts = NormalizeCheckoutOptions(currentCommit);

        // 3-way merge: ancestor=parentTree, ours=headTree, theirs=currentTree.
        GitIndex index = await repo.MergeTreesAsync(parentTree, headTree, currentTree, _options.MergeOptions, cancellationToken).ConfigureAwait(false);

        // Post-merge safety check. Matches rebase.c:835.
        await repo.MergeCheckResultAsync(index, cancellationToken).ConfigureAwait(false);

        // Write index to disk before checkout (preserves conflict entries).
        repo.SetIndex(index);
        await index.WriteAsync(cancellationToken).ConfigureAwait(false);

        // Checkout the merge result.
        GitCheckoutOptions normalizedCheckout = checkoutOpts with
        {
            Strategy = checkoutOpts.Strategy | GitCheckoutStrategy.DontWriteIndex,
        };
        await repo.CheckoutIndexAsync(index, normalizedCheckout, cancellationToken).ConfigureAwait(false);

        // Reload index from disk to restore conflict entries.
        await ReloadIndexFromDiskAsync(repo, cancellationToken).ConfigureAwait(false);

        return operation;
    }

    private async Task<GitRebaseOperation> NextInMemoryAsync(CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;
        GitRebaseOperation operation = _operations[_current];

        CommitOps currentCommit = await repo.Objects.LookupAsync<CommitOps>(operation.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"commit {operation.Id} not found",
                GitErrorCategory.Object);

        GitTree currentTree = await repo.Objects.LookupAsync<GitTree>(currentCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {currentCommit.Tree} not found",
                GitErrorCategory.Object);

        // Get parent tree (may be null for root commits).
        GitTree? parentTree = null;
        int parentCount = currentCommit.Parents.Count;
        if (parentCount > 1)
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot rebase a merge commit",
                GitErrorCategory.Rebase);
        }

        if (parentCount == 1)
        {
            CommitOps parentCommit = await repo.Objects.LookupAsync<CommitOps>(currentCommit.ParentId(0), cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    "parent commit not found",
                    GitErrorCategory.Object);
            parentTree = await repo.Objects.LookupAsync<GitTree>(parentCommit.Tree, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    "parent tree not found",
                    GitErrorCategory.Object);
        }

        // Get HEAD tree (= last_commit's tree).
        CommitOps? lastCommit = _lastCommit;
        Debug.Assert(lastCommit is not null, "_lastCommit is set before CommitCreateAsync is invoked.");
        GitTree headTree = await repo.Objects.LookupAsync<GitTree>(lastCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {lastCommit.Tree} not found",
                GitErrorCategory.Object);

        // 3-way merge.
        GitIndex mergeIndex = await repo.MergeTreesAsync(parentTree, headTree, currentTree, _options.MergeOptions, cancellationToken).ConfigureAwait(false);

        // Accumulate: first next sets _index; subsequent calls ReadIndex.
        if (_index is null)
        {
            _index = mergeIndex;
        }
        else
        {
            _index.ReadIndex(mergeIndex);
            mergeIndex.Dispose();
        }

        return operation;
    }

    private GitCheckoutOptions NormalizeCheckoutOptions(CommitOps currentCommit)
    {
        GitCheckoutOptions checkoutOpts = _options.CheckoutOptions ?? new GitCheckoutOptions();

        checkoutOpts = checkoutOpts with
        {
            AncestorLabel = checkoutOpts.AncestorLabel ?? "ancestor",
            OurLabel = checkoutOpts.OurLabel ?? _ontoName,
            TheirLabel = checkoutOpts.TheirLabel ?? currentCommit.Summary,
        };

        return checkoutOpts;
    }

    // ── Commit helpers ──────────────────────────────────────────────

    private async Task<GitOid> CommitCreateAsync(
        GitIndex index,
        CommitOps parentCommit,
        GitSignature? author,
        GitSignature? committer,
        string? messageEncoding,
        string? message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(committer);
        GitRepository repo = Repo;
        GitRebaseOperation operation = _operations[_current];

        // Check for unresolved conflicts.
        if (index.HasConflicts)
        {
            throw new GitException(
                GitErrorCode.Unmerged,
                "conflicts have not been resolved",
                GitErrorCategory.Rebase);
        }

        // Look up the original commit being cherry-picked.
        CommitOps currentCommit = await repo.Objects.LookupAsync<CommitOps>(operation.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"commit {operation.Id} not found",
                GitErrorCategory.Object);

        // Get the parent tree.
        GitTree parentTree = await repo.Objects.LookupAsync<GitTree>(parentCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {parentCommit.Tree} not found",
                GitErrorCategory.Object);

        // Write the index as a tree.
        GitOid treeId = await index.WriteTreeToAsync(repo, cancellationToken).ConfigureAwait(false);

        // Check if already applied (tree == parent tree).
        if (treeId.Equals(parentTree.Id))
        {
            throw new GitException(
                GitErrorCode.Applied,
                "this patch has already been applied",
                GitErrorCategory.Rebase);
        }

        // Resolve author and message.
        author ??= currentCommit.Author;
        if (message is null)
        {
            // C (rebase.c:1032-1035): message = git_commit_message(...) —
            // leading newlines trimmed (commit.c:595-607).
            messageEncoding = currentCommit.Encoding;
            message = currentCommit.Message;
        }

        // Try the commit_create_cb if set.
        if (_options.CommitCreateCallback is { } cb)
        {
            GitTree tree = await repo.Objects.LookupAsync<GitTree>(treeId, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"tree {treeId} not found", GitErrorCategory.Object);
            CommitOps[] parents = [parentCommit];
            GitOid? result = cb(author, committer, messageEncoding, message, treeId, parents);
            if (result is { } cbOid)
            {
                return cbOid;
            }

            // PassThrough → fall through to default.
        }

        // Default: create commit via CommitOps.CreateAsync (no UpdateRef — rebase manages refs).
        var createOpts = new CommitCreateOptions
        {
            Tree = treeId,
            Parents = [parentCommit.Id],
            Author = author,
            Committer = committer,
            MessageEncoding = messageEncoding,
            Message = message,
            UpdateRef = null,
            AllowEmptyCommit = true,
        };

        return await CommitOps.CreateAsync(repo, createOpts, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOid> CommitMergeAsync(
        GitSignature? author,
        GitSignature committer,
        string? messageEncoding,
        string? message,
        CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;

        // Ensure workdir is not dirty (no unstaged changes).
        await EnsureNotDirtyAsync(repo, checkIndex: false, checkWorkdir: true, failWith: GitErrorCode.Unmerged, cancellationToken).ConfigureAwait(false);

        // Get HEAD commit.
        GitReference headRef = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD not found",
                GitErrorCategory.Reference);
        CommitOps headCommit = await repo.Objects.LookupAsync<CommitOps>(((GitDirectReference)headRef).Target, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD commit not found",
                GitErrorCategory.Object);

        // Get the repo index.
        GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);

        // Create the commit.
        GitOid commitId = await CommitCreateAsync(index, headCommit, author, committer, messageEncoding, message, cancellationToken).ConfigureAwait(false);

        // Update HEAD.
        await repo.Refs.CreateAsync("HEAD", commitId, force: true, logMessage: "rebase", cancellationToken).ConfigureAwait(false);

        // Append to rewritten file.
        GitRebaseOperation operation = _operations[_current];
        string oldIdStr = operation.Id.ToString();
        string newIdStr = commitId.ToString();
        await RebaseStateFiles.AppendFileAsync(CurrentStatePath, RebaseStateFiles.RewrittenFile, $"{oldIdStr} {newIdStr}\n", cancellationToken).ConfigureAwait(false);

        return commitId;
    }

    private async Task<GitOid> CommitInMemoryAsync(
        GitSignature? author,
        GitSignature committer,
        string? messageEncoding,
        string? message,
        CancellationToken cancellationToken)
    {
        if (_index is null)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "no in-memory index — call Next first",
                GitErrorCategory.Rebase);
        }

        if (_lastCommit is null)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "no last commit",
                GitErrorCategory.Rebase);
        }

        GitOid commitId = await CommitCreateAsync(_index, _lastCommit, author, committer, messageEncoding, message, cancellationToken).ConfigureAwait(false);

        // Update last_commit.
        CommitOps newCommit = await Repo.Objects.LookupAsync<CommitOps>(commitId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"commit {commitId} not found",
                GitErrorCategory.Object);
        _lastCommit.Dispose();
        _lastCommit = newCommit;

        return commitId;
    }

    // ── Finish helpers ──────────────────────────────────────────────

    private async Task ReturnToOrigHeadAsync(CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;
        string ontoStr = _ontoId.ToString();

        string branchMsg = $"rebase finished: {_origHeadName} onto {ontoStr}";
        string headMsg = $"rebase finished: returning to {_origHeadName}";

        // Get the current HEAD (terminal commit).
        GitReference terminalRef = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD not found",
                GitErrorCategory.Reference);
        CommitOps terminalCommit = await repo.Objects.LookupAsync<CommitOps>(((GitDirectReference)terminalRef).Target, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD commit not found",
                GitErrorCategory.Object);

        // CAS update: move the original branch ref to the terminal commit,
        // but only if it still points at orig_head_id.
        await repo.Refs.CreateMatchingAsync(CurrentOrigHeadName, terminalCommit.Id, _origHeadId, force: true, logMessage: branchMsg, cancellationToken).ConfigureAwait(false);

        // Point HEAD at the original branch (symbolic).
        await repo.Refs.CreateSymbolicAsync("HEAD", CurrentOrigHeadName, force: true, logMessage: headMsg, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyNotesAsync(GitSignature? signature, CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;

        // Resolve the notes ref.
        string? notesRef = await NotesRefLookupAsync(cancellationToken).ConfigureAwait(false);
        if (notesRef is null)
        {
            return;
        }

        // Read the rewritten file. C (rebase.c:1313-1315): a MISSING
        // rewritten file propagates the git_futils_readbuffer error
        // (GIT_ENOTFOUND, "failed to stat file '%s'") — the finish FAILS
        // instead of silently skipping.
        string rewrittenPath = Path.Join(CurrentStatePath, RebaseStateFiles.RewrittenFile);
        if (!File.Exists(rewrittenPath))
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"failed to stat file '{rewrittenPath}'",
                GitErrorCategory.Os);
        }

        int lineNum = 0;
        await foreach (string line in AsyncFileIO.ReadLinesAsync(rewrittenPath, cancellationToken).ConfigureAwait(false))
        {
            lineNum++;
            string[] parts = line.Split(' ', 2);
            if (parts.Length != 2)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"invalid rewritten file: '{line}'",
                    GitErrorCategory.Rebase);
            }

            // C's
            // rebase_copy_notes requires exact-hexsize OIDs and fails with
            // 'invalid rewritten file at line %d' (rebase.c:1334-1350) — the
            // port accepted abbreviated OIDs (silently padding) and let
            // FormatException escape for non-hex content.
            int hexSize = GitOid.HexSizeFor(repo.ObjectFormat);
            if (parts[0].Length != hexSize || parts[1].Length != hexSize)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"invalid rewritten file at line {lineNum}",
                    GitErrorCategory.Rebase);
            }

            GitOid fromOid;
            GitOid toOid;
            try
            {
                fromOid = GitOid.Parse(parts[0].AsSpan(), repo.ObjectFormat);
                toOid = GitOid.Parse(parts[1].AsSpan(), repo.ObjectFormat);
            }
            catch (FormatException)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"invalid rewritten file at line {lineNum}",
                    GitErrorCategory.Rebase);
            }

            await CopyNoteAsync(notesRef, fromOid, toOid, signature, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> NotesRefLookupAsync(CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;

        // Explicit option overrides config.
        if (_options.RewriteNotesRef is not null)
        {
            return _options.RewriteNotesRef;
        }

        // Check notes.rewrite.rebase config. C (rebase.c:1227-1235): a
        // MISSING key defaults to rewrite=1; a PRESENT but unparseable value
        // is a non-ENOTFOUND error that FAILS the finish ("failed to parse
        // '%s' as a boolean", config.c:1450).
        GitConfigEntry? rewriteEntry = await repo.Config.GetEntryAsync("notes.rewrite.rebase", cancellationToken).ConfigureAwait(false);
        bool doRewrite;
        if (rewriteEntry is null || rewriteEntry.Value.ValueBytes is null)
        {
            doRewrite = true; // missing key, or lone variable → true
        }
        else if (!ConfigurationValueParser.TryParseBool(rewriteEntry.Value.ValueBytes.GetValueOrDefault().Span, out doRewrite))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to parse '{rewriteEntry.Value.Value}' as a boolean",
                GitErrorCategory.Config);
        }

        if (!doRewrite)
        {
            return null;
        }

        // C (rebase.c:1218-1247, notes_ref_lookup): with no explicit rewrite_notes_ref option, the ref comes ONLY from the "notes.rewriteref" config key
        // — a missing key is GIT_ENOTFOUND and rebase_copy_notes copies NOTHING (rebase.c:1304-1311). There is no default "refs/notes/commits" fallback.
        // Non-gap: the value feeds the string ref-name tier (NotesReadAsync); GetBytesAsync+decode would be identical to GetStringAsync, so the display
        // tier is final here.
        string? rewriteRef = await repo.Config.GetStringAsync("notes.rewriteRef", cancellationToken).ConfigureAwait(false);
        return rewriteRef;
    }

    private async Task CopyNoteAsync(string notesRef, GitOid fromOid, GitOid toOid, GitSignature? signature, CancellationToken cancellationToken)
    {
        GitRepository repo = Repo;

        // Read the note from the old commit.
        GitNote? note = await repo.NotesReadAsync(notesRef, fromOid, cancellationToken).ConfigureAwait(false);
        if (note is null)
        {
            return; // No note on the old commit — skip silently.
        }

        try
        {
            GitSignature committer;
            if (signature is not null)
            {
                committer = signature;
            }
            else
            {
                try
                {
                    committer = await GitSignature.DefaultAsync(repo.Config, cancellationToken).ConfigureAwait(false);
                }
                catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                {
                    // C (rebase.c:1274-1278): no identity configured →
                    // git_signature_now("unknown", "unknown") and continue.
                    committer = GitSignature.Now("unknown", "unknown");
                }
            }

            GitSignature author = note.Author;

            // C (rebase.c:1282-1283): git_note_create with force=0 — an
            // existing note on the new commit errors (GIT_EEXISTS) instead of
            // being overwritten.
            await repo.NotesCreateAsync(notesRef, author, committer, toOid, note.Message, allowOverwrite: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            note.Dispose();
        }
    }

    // ── Safety checks ───────────────────────────────────────────────

    private static void EnsureNotInProgress(GitRepository repo)
    {
        RebaseType type = RebaseStateFiles.DetectType(repo, out _);
        if (type != RebaseType.None)
        {
            throw new GitException(
                GitErrorCode.Error,
                "there is an existing rebase in progress",
                GitErrorCategory.Rebase);
        }
    }

    private static async Task EnsureNotDirtyAsync(GitRepository repo, bool checkIndex, bool checkWorkdir, GitErrorCode failWith, CancellationToken cancellationToken)
    {
        if (checkIndex)
        {
            // Diff HEAD tree vs index.
            GitReference? headRef = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (headRef is GitDirectReference dr)
            {
                CommitOps? headCommit = await repo.Objects.LookupAsync<CommitOps>(dr.Target, cancellationToken).ConfigureAwait(false);
                if (headCommit is not null)
                {
                    GitTree? headTree = await repo.Objects.LookupAsync<GitTree>(headCommit.Tree, cancellationToken).ConfigureAwait(false);
                    DiffGenerator diff = await DiffGenerator.TreeToIndexAsync(repo, headTree, null, cancellationToken).ConfigureAwait(false);
                    if (diff.Deltas.Count > 0)
                    {
                        throw new GitException(
                            failWith,
                            "uncommitted changes exist in index",
                            GitErrorCategory.Rebase);
                    }
                }
            }
        }

        if (checkWorkdir)
        {
            // C (rebase.c:552-555): the index→workdir diff is generated with
            // diff_opts.ignore_submodules = GIT_SUBMODULE_IGNORE_UNTRACKED —
            // untracked files inside a submodule must not make the workdir
            // dirty.
            var workdirOpts = new GitDiffOptions { IgnoreSubmodules = GitDiffIgnoreSubmodules.Untracked };
            DiffGenerator diffWd = await DiffGenerator.IndexToWorkdirAsync(repo, workdirOpts, cancellationToken).ConfigureAwait(false);
            if (diffWd.Deltas.Count > 0)
            {
                throw new GitException(
                    failWith,
                    "unstaged changes exist in workdir",
                    GitErrorCategory.Rebase);
            }
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    private void Cleanup()
    {
        if (_inmemory)
        {
            return;
        }

        if (_statePath is not null && Directory.Exists(_statePath))
        {
            Directory.Delete(_statePath, recursive: true);
        }
    }

    private static async Task ReloadIndexFromDiskAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        string indexPath = Path.Join(repo.Path, "index");
        if (File.Exists(indexPath))
        {
            GitIndex savedIndex = await GitIndex.OpenAsync(indexPath, repo.ObjectFormat, cancellationToken).ConfigureAwait(false);
            savedIndex.SetOwner(repo);
            repo.SetIndex(savedIndex);
        }
    }

    // ── IDisposable ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _index?.Dispose();
        _lastCommit?.Dispose();
        _repo = null;
    }
}

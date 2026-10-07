// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Refs;
using LibGit2CS.Utils;

namespace LibGit2CS.Repository;

/// <summary>
/// A linked worktree. Managed port of libgit2's
/// <c>src/libgit2/worktree.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Linked worktrees share the object database and refs of a parent (main)
/// repository via the <c>.git/worktrees/&lt;name&gt;/</c> admin directory
/// structure. Each worktree has its own HEAD, index, and per-worktree refs.
/// </para>
/// <para>
/// Admin files under <c>.git/worktrees/&lt;name&gt;/</c>:
/// <c>HEAD</c> — the worktree's HEAD; <c>commondir</c> — path to the parent's
/// <c>.git</c> common dir; <c>gitdir</c> — path back to the worktree's
/// <c>.git</c> file; <c>locked</c> (optional) — lock reason.
/// </para>
/// </remarks>
public sealed class Worktree : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Creates a <see cref="Worktree"/> from parsed admin dir contents.
    /// </summary>
    internal Worktree(
        string name,
        string worktreePath,
        string gitlinkPath,
        string gitdirPath,
        string commondirPath,
        string? parentPath,
        bool isLocked)
    {
        Name = name;
        Path = worktreePath;
        GitlinkPath = gitlinkPath;
        GitdirPath = gitdirPath;
        CommondirPath = commondirPath;
        ParentPath = parentPath;
        IsLocked = isLocked;
    }

    /// <summary>The directory name under <c>.git/worktrees/</c>.</summary>
    public string Name { get; }

    /// <summary>The filesystem path to the working tree directory.</summary>
    public string Path { get; }

    /// <summary>The path to the <c>.git</c> file in the worktree.</summary>
    public string GitlinkPath { get; }

    /// <summary>The path to the <c>.git/worktrees/&lt;name&gt;/</c> admin dir.</summary>
    public string GitdirPath { get; }

    /// <summary>The path to the shared common directory.</summary>
    public string CommondirPath { get; }

    /// <summary>The path to the parent repo's working directory, if known.</summary>
    public string? ParentPath { get; }

    /// <summary>True if the worktree is locked (has a <c>locked</c> file).</summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// Returns the lock reason, or null if not locked or no reason was given.
    /// Matches <c>git_worktree_is_locked</c> with reason output.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask<string?> GetLockReasonAsync(CancellationToken cancellationToken = default)
    {
        string lockedPath = PathHelpers.Join(GitdirPath, "locked");
        if (!File.Exists(lockedPath))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return new ValueTask<string?>(GetLockReasonSlowAsync(lockedPath, cancellationToken));
    }

    /// <summary>Slow path of <see cref="GetLockReasonAsync"/>: reads the locked file from disk.</summary>
    private static async Task<string?> GetLockReasonSlowAsync(string lockedPath, CancellationToken cancellationToken)
    {
        // C (worktree.c:489-511, git_worktree__is_locked): the reason is the
        // RAW file contents — no trimming. `git worktree lock --reason`
        // writes "reason\n" and libgit2 returns "reason\n".
        string content = await AsyncFileIO.ReadAllTextWithNoBomAsync(lockedPath, cancellationToken).ConfigureAwait(false);
        return content;
    }

    // ==============================
    // List / Lookup / Open
    // ==============================

    /// <summary>
    /// Lists all linked worktrees. Matches <c>git_worktree_list</c>
    /// (worktree.c:34-73).
    /// </summary>
    /// <param name="repo">The parent repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of worktrees (empty if none).</returns>
    internal static ValueTask<IReadOnlyList<Worktree>> ListAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        string worktreesDir = PathHelpers.Join(repo.CommonDir, "worktrees");
        if (!Directory.Exists(worktreesDir))
        {
            return ValueTask.FromResult<IReadOnlyList<Worktree>>([]);
        }

        return new ValueTask<IReadOnlyList<Worktree>>(ListSlowAsync(repo, worktreesDir, cancellationToken));
    }

    /// <summary>Slow path of <see cref="ListAsync"/>: per-worktree admin-file reads (disk IO).</summary>
    private static async Task<IReadOnlyList<Worktree>> ListSlowAsync(GitRepository repo, string worktreesDir, CancellationToken cancellationToken)
    {
        var result = new List<Worktree>();
        foreach (string entry in Directory.EnumerateDirectories(worktreesDir))
        {
            string name = System.IO.Path.GetFileName(entry.TrimEnd('/'));
            Worktree? wt = await OpenWorktreeDirAsync(repo.Workdir, entry, name, cancellationToken).ConfigureAwait(false);
            if (wt is not null)
            {
                result.Add(wt);
            }
        }

        return result;
    }

    /// <summary>
    /// Looks up a worktree by name. Matches <c>git_worktree_lookup</c>
    /// (worktree.c:176-205).
    /// </summary>
    /// <returns>The worktree, or null if not found.</returns>
    internal static async Task<Worktree?> LookupAsync(GitRepository repo, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        string dir = PathHelpers.Join(PathHelpers.Join(repo.CommonDir, "worktrees"), name);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return await OpenWorktreeDirAsync(repo.Workdir, dir, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the worktree associated with the given repository (which must
    /// itself be a linked worktree). Matches <c>git_worktree_open_from_repository</c>
    /// (worktree.c:207-237).
    /// </summary>
    internal static async Task<Worktree?> FromRepositoryAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        if (!repo.IsWorktree)
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot open worktree of a non-worktree repo",
                GitErrorCategory.Worktree);
        }

        // The name is the last component of the gitdir path.
        string name = System.IO.Path.GetFileName(repo.Path.TrimEnd('/'));

        // The parent path is ".." relative to the commondir.
        string parent = PathHelpers.PrettifyDir(PathHelpers.Join(repo.CommonDir, ".."));

        return await OpenWorktreeDirAsync(parent, repo.Path, name, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Add
    // ==============================

    /// <summary>
    /// Adds a new linked worktree. Matches <c>git_worktree_add</c>
    /// (worktree.c:304-429).
    /// </summary>
    /// <param name="repo">The parent repository.</param>
    /// <param name="name">The name for the worktree (also the branch name unless <paramref name="options"/>.Ref is set).</param>
    /// <param name="worktreePath">The filesystem path for the new worktree directory.</param>
    /// <param name="options">Add options (lock, checkout_existing, ref, checkout options).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly created worktree.</returns>
    internal static async Task<Worktree> AddAsync(GitRepository repo, string name, string worktreePath, WorktreeAddOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(worktreePath);

        options ??= new WorktreeAddOptions();

        // Resolve the reference for the worktree's HEAD.
        GitReference? ref_ = null;
        if (options.Ref is not null)
        {
            if (!options.Ref.IsBranch)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "reference is not a branch",
                    GitErrorCategory.Worktree);
            }

            ref_ = options.Ref;
        }
        else if (options.CheckoutExisting)
        {
            ref_ = await repo.BranchLookupAsync(name, GitBranchType.Local, cancellationToken).ConfigureAwait(false);
        }

        if (ref_ is null)
        {
            // Create a new branch from HEAD.
            GitReference? headResolved = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (headResolved is not GitDirectReference headDirect)
            {
                throw new GitException(
                    GitErrorCode.UnbornBranch,
                    "HEAD is unborn — cannot create worktree branch",
                    GitErrorCategory.Reference);
            }

            ref_ = await repo.BranchCreateAsync(name, headDirect.Target, force: false, cancellationToken).ConfigureAwait(false);
        }

        // Check if the branch is already checked out.
        if (await ref_.IsCheckedOutAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"reference '{ref_.Name}' is already checked out",
                GitErrorCategory.Worktree);
        }

        // C (worktree.c:355-372): ".git/worktrees/<name>" and the worktree
        // dir are created with GIT_MKDIR_EXCL — GIT_EEXISTS if either exists
        // ("failed to make directory '%s': directory exists"). The
        // worktrees base dir itself is created without EXCL (C only mkdirs
        // it when absent).
        string worktreesBase = PathHelpers.Join(repo.CommonDir, "worktrees");
        if (!Directory.Exists(worktreesBase))
        {
            Directory.CreateDirectory(worktreesBase);
        }

        // C's git_worktree_add CREATES each directory first and only
        // THEN prettifies (realpath) it (worktree.c:363-377).
        string gitdir = PathHelpers.Join(worktreesBase, name);
        TryCreateDirExcl(gitdir);
        gitdir = PathHelpers.PrettifyDir(gitdir);

        // Create worktree working directory.
        string wddir = worktreePath;
        TryCreateDirExcl(wddir);
        wddir = PathHelpers.PrettifyDir(wddir);

        // Optional: lock the worktree.
        if (options.Lock)
        {
            await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(gitdir, "locked"), string.Empty, cancellationToken).ConfigureAwait(false);
        }

        // Create the .git file in the worktree.
        await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(wddir, ".git"), $"gitdir: {gitdir}\n", cancellationToken).ConfigureAwait(false);

        // Create admin files in gitdir.
        await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(gitdir, "commondir"), $"{PathHelpers.PrettifyDir(repo.CommonDir)}\n", cancellationToken).ConfigureAwait(false);
        await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(gitdir, "gitdir"), $"{PathHelpers.Join(wddir, ".git")}\n", cancellationToken).ConfigureAwait(false);

        // Set the worktree's HEAD (symbolic ref pointing at the branch).
        await File.WriteAllBytesAsync(PathHelpers.Join(gitdir, "HEAD"), ref_.NameKey.SymbolicContent(), cancellationToken).ConfigureAwait(false);

        // Open the worktree as a repo and checkout HEAD.
        GitRepository wtRepo = await GitRepository.OpenAsync(wddir, repo.Context, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable wtDisposable = wtRepo.ConfigureAwait(false);
        await wtRepo.CheckoutHeadAsync(options.CheckoutOptions, cancellationToken).ConfigureAwait(false);

        // Load and return the result.
        return (await LookupAsync(repo, name, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(
                GitErrorCode.Error,
                $"failed to look up newly created worktree '{name}'",
                GitErrorCategory.Worktree);
    }

    // ==============================
    // Lock / Unlock
    // ==============================

    /// <summary>
    /// Creates <paramref name="dir"/> with EXCL semantics — GIT_EEXISTS
    /// "failed to make directory '%s': directory exists" when it already
    /// exists. Matches <c>git_futils_mkdir(..., GIT_MKDIR_EXCL)</c>.
    /// C's mkdir(EXCL)
    /// is SINGLE-LEVEL — a missing parent fails with ENOENT. The old
    /// Directory.CreateDirectory implicitly created all missing parents, so
    /// worktree names with '/' or paths with missing parents succeeded where
    /// C fails.
    /// </summary>
    /// <param name="dir">The directory path.</param>
    private static void TryCreateDirExcl(string dir)
    {
        if (Directory.Exists(dir))
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"failed to make directory '{dir}': directory exists",
                GitErrorCategory.Worktree);
        }

        // C (futils.c, git_futils_mkdir without GIT_MKDIR_PATH): the mkdir
        // starts at the last '/', so a missing parent is ENOENT. Trim the
        // trailing separator first — Path.GetDirectoryName on "a/b/" returns
        // "a/b" (the dir itself), which would always look missing.
        string trimmed = dir.TrimEnd('/', '\\');
        string? parent = System.IO.Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to make directory '{dir}': parent directory does not exist",
                GitErrorCategory.Worktree);
        }

        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Locks the worktree. Matches <c>git_worktree_lock</c>
    /// (worktree.c:431-460).
    /// </summary>
    /// <param name="reason">Optional lock reason written to the <c>locked</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Locked"/> if the worktree is already locked.
    /// </exception>
    public async Task LockAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        // C (worktree.c:438-443): git_worktree_lock calls
        // git_worktree_is_locked — the `locked` FILE is consulted on every
        // call (the cached flag goes stale when the file is created or
        // removed out-of-band, e.g. by the git CLI).
        string lockedPath = PathHelpers.Join(GitdirPath, "locked");
        if (File.Exists(lockedPath))
        {
            throw new GitException(
                GitErrorCode.Locked,
                "worktree is already locked",
                GitErrorCategory.Worktree);
        }

        // C writes with O_CREAT|O_EXCL (worktree.c:451): fail if the file
        // appeared meanwhile — WriteAtomicIfMissingAsync is the managed
        // equivalent.
        bool written = await AsyncFileIO.WriteAtomicIfMissingAsync(
            lockedPath,
            System.Text.Encoding.UTF8.GetBytes(reason ?? string.Empty),
            cancellationToken).ConfigureAwait(false);
        if (!written)
        {
            throw new GitException(
                GitErrorCode.Locked,
                "worktree is already locked",
                GitErrorCategory.Worktree);
        }

        IsLocked = true;
    }

    /// <summary>
    /// Unlocks the worktree. Matches <c>git_worktree_unlock</c>
    /// (worktree.c:462-487).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the lock was removed; <c>false</c> if the worktree was
    /// not locked (C returns 1 — not an error).
    /// </returns>
    /// <remarks>
    /// <c>File.Delete</c> on the <c>locked</c> admin file is a metadata-only
    /// op (inode unlink, not buffered IO) and stays sync (the metadata
    /// exemption).
    /// </remarks>
    public bool Unlock()
    {
        // C (worktree.c:469-472): git_worktree_unlock consults the `locked`
        // FILE on every call and returns 1 (not an error) when not locked.
        string lockedPath = PathHelpers.Join(GitdirPath, "locked");
        if (!File.Exists(lockedPath))
        {
            return false;
        }

        File.Delete(lockedPath);
        IsLocked = false;
        return true;
    }

    // ==============================
    // Validate
    // ==============================

    /// <summary>
    /// Validates that the worktree's admin files and directories exist.
    /// Matches <c>git_worktree_validate</c> (worktree.c:253-286).
    /// </summary>
    /// <exception cref="GitException">if the worktree is invalid.</exception>
    public void Validate()
    {
        if (!IsWorktreeDir(GitdirPath))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"worktree gitdir ('{GitlinkPath}') is not valid",
                GitErrorCategory.Worktree);
        }

        if (ParentPath is not null && !PathHelpers.Exists(ParentPath))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"worktree parent directory ('{ParentPath}') does not exist",
                GitErrorCategory.Worktree);
        }

        if (!PathHelpers.Exists(CommondirPath))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"worktree common directory ('{CommondirPath}') does not exist",
                GitErrorCategory.Worktree);
        }

        if (!PathHelpers.Exists(Path))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"worktree directory '{Path}' does not exist",
                GitErrorCategory.Worktree);
        }
    }

    // ==============================
    // Prune
    // ==============================

    /// <summary>
    /// Returns true if the worktree is prunable. Matches
    /// <c>git_worktree_is_prunable</c> (worktree.c:561-611).
    /// </summary>
    public bool IsPrunable(WorktreePruneOptions? options = null)
    {
        options ??= new WorktreePruneOptions();

        // If not pruning locked worktrees, refuse locked worktrees.
        // C's
        // git_worktree_is_prunable calls git_worktree__is_locked, which
        // re-reads the `locked` FILE on every invocation (worktree.c:575-
        // 590) — the cached IsLocked flag goes stale when the file is
        // created out-of-band (e.g. `git worktree lock`), and a stale flag
        // let an out-of-band-locked worktree be pruned.
        if ((options.Flags & WorktreePruneFlags.Locked) == 0
            && File.Exists(PathHelpers.Join(GitdirPath, "locked")))
        {
            return false;
        }

        // If not pruning valid worktrees, refuse valid ones.
        if ((options.Flags & WorktreePruneFlags.Valid) == 0)
        {
            try
            {
                Validate();
                return false; // valid → not prunable
            }
            catch (GitException)
            {
                // Invalid → prunable (continue).
            }
        }

        // Check if the gitdir still exists.
        string gitdirInParent = PathHelpers.Join(PathHelpers.Join(CommondirPath, "worktrees"), Name);
        if (!PathHelpers.Exists(gitdirInParent))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Prunes the worktree. Matches <c>git_worktree_prune</c>
    /// (worktree.c:613-669).
    /// </summary>
    /// <param name="options">Prune options.</param>
    public void Prune(WorktreePruneOptions? options = null)
    {
        options ??= new WorktreePruneOptions();

        if (!IsPrunable(options))
        {
            throw new GitException(
                GitErrorCode.Error,
                "worktree is not prunable",
                GitErrorCategory.Worktree);
        }

        // Delete the gitdir in the parent repository.
        string gitdirInParent = PathHelpers.Join(PathHelpers.Join(CommondirPath, "worktrees"), Name);
        if (!PathHelpers.Exists(gitdirInParent))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"worktree gitdir '{gitdirInParent}' does not exist",
                GitErrorCategory.Worktree);
        }

        Directory.Delete(gitdirInParent, recursive: true);

        // Optionally delete the actual working tree.
        if ((options.Flags & WorktreePruneFlags.WorkingTree) != 0 &&
            PathHelpers.Exists(GitlinkPath))
        {
            string wtParent = PathHelpers.Dirname(GitlinkPath);
            if (PathHelpers.Exists(wtParent))
            {
                Directory.Delete(wtParent, recursive: true);
            }
        }
    }

    // ==============================
    // Internal helpers
    // ==============================

    /// <summary>
    /// Checks if a directory has the required worktree admin files
    /// (commondir, gitdir, HEAD). Matches <c>is_worktree_dir</c>
    /// (worktree.c:18-32).
    /// </summary>
    internal static bool IsWorktreeDir(string dir)
    {
        return PathHelpers.ContainsFile(dir, "commondir")
            && PathHelpers.ContainsFile(dir, "gitdir")
            && PathHelpers.ContainsFile(dir, "HEAD");
    }

    /// <summary>
    /// Reads a file (like <c>commondir</c> or <c>gitdir</c>), resolving relative
    /// paths against <paramref name="baseDir"/>. Matches
    /// <c>git_worktree__read_link</c> (worktree.c:75-106).
    /// </summary>
    private static async Task<string?> ReadLinkAsync(string baseDir, string file, CancellationToken cancellationToken)
    {
        string path = PathHelpers.Join(baseDir, file);
        if (!File.Exists(path))
        {
            return null;
        }

        // C (git_worktree__read_link, worktree.c:78-114): git_str_rtrim —
        // TRAILING whitespace only; leading whitespace is preserved (Trim()
        // would strip it too).
        ReadOnlySpan<char> content = AsciiText.Rtrim(await AsyncFileIO.ReadAllTextWithNoBomAsync(path, cancellationToken).ConfigureAwait(false));

        if (!PathHelpers.IsAbsolute(content))
        {
            // Resolve relative path against baseDir. C's
            // git_worktree__read_link uses git_fs_path_apply_relative — a
            // LEXICAL resolution with no realpath and no existence
            // requirement (worktree.c:104-110) — the pointer may target a
            // pruned worktree, so PrettifyLexical is used (Prettify would
            // throw "failed to resolve path").
            return PathHelpers.PrettifyLexical(PathHelpers.Join(baseDir, content));
        }

        return content.ToString();
    }

    /// <summary>
    /// Parses a worktree metadata directory. Matches
    /// <c>open_worktree_dir</c> (worktree.c:129-174).
    /// </summary>
    /// <param name="parent">The parent repo's workdir, or null.</param>
    /// <param name="dir">The <c>.git/worktrees/&lt;name&gt;/</c> path.</param>
    /// <param name="name">The worktree name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed worktree, or null if the dir is invalid.</returns>
    private static async Task<Worktree?> OpenWorktreeDirAsync(string? parent, string dir, string name, CancellationToken cancellationToken)
    {
        if (!IsWorktreeDir(dir))
        {
            return null;
        }

        string? commondirPath = await ReadLinkAsync(dir, "commondir", cancellationToken).ConfigureAwait(false);
        string? gitlinkPath = await ReadLinkAsync(dir, "gitdir", cancellationToken).ConfigureAwait(false);

        if (commondirPath is null || gitlinkPath is null)
        {
            return null;
        }

        string worktreePath = PathHelpers.Dirname(gitlinkPath);
        string gitdirPath = PathHelpers.PrettifyDir(dir);

        bool isLocked = File.Exists(PathHelpers.Join(dir, "locked"));

        return new Worktree(
            name: name,
            worktreePath: worktreePath,
            gitlinkPath: gitlinkPath,
            gitdirPath: gitdirPath,
            commondirPath: commondirPath,
            parentPath: parent,
            isLocked: isLocked);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}

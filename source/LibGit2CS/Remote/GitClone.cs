// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.Remote;

/// <summary>
/// Clone operations. Managed port of <c>src/libgit2/clone.c</c> (712 lines).
/// </summary>
/// <remarks>
/// <see cref="RunAsync"/> orchestrates: repository init → origin creation →
/// connect + fetch → checkout HEAD.
/// </remarks>
public static class GitClone
{
    /// <summary>
    /// Clones a remote repository into a local path.
    /// Matches <c>git_clone</c> (clone.c:682-689).
    /// </summary>
    /// <param name="url">The source URL (http(s)://, git://, file://, or
    /// local path).</param>
    /// <param name="localPath">The destination directory path.</param>
    /// <param name="options">Clone options (null for defaults).</param>
    /// <param name="context">The library context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cloned repository, opened and ready.</returns>
    public static async Task<GitRepository> RunAsync(
        string url,
        string localPath,
        GitCloneOptions? options,
        GitContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(localPath);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new GitCloneOptions();

        // Validate destination: must not exist or be an empty directory.
        bool pathExists = PathHelpers.Exists(localPath);
        if (pathExists && !IsDirEmpty(localPath))
        {
            throw new GitException(GitErrorCode.Exists,
                $"'{localPath}' exists and is not an empty directory",
                GitErrorCategory.Repository);
        }

        // Create the target repository.
        GitRepository? repo = null;
        bool createdDir = !pathExists;

        try
        {
            try
            {
                repo = (options.RepositoryCreate is { } repoCb)
                    ? await repoCb(localPath, options.Bare, context, cancellationToken).ConfigureAwait(false)
                    : await GitRepository.InitAsync(localPath, options.Bare, context, cancellationToken).ConfigureAwait(false);
            }
            catch when (options.RepositoryCreate is not null)
            {
                // C (clone.c:647-648): a failing repository_cb returns IMMEDIATELY with NO directory cleanup — the callback owns the directory.
                throw;
            }

            // Create the origin remote.
            GitRemote remote = await CreateAndConfigureOriginAsync(repo, url, options, cancellationToken).ConfigureAwait(false);

            // Decide local vs network clone.
            bool cloneLocal = ShouldCloneLocal(url, options.CloneLocal);

            string reflogMessage = $"clone: from {remote.Url}";

            if (cloneLocal)
            {
                await CloneLocalIntoAsync(repo, remote, options, reflogMessage, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CloneIntoAsync(repo, remote, options, reflogMessage, cancellationToken).ConfigureAwait(false);
            }

            return repo;
        }
        catch
        {
            // On failure, clean up: dispose repo, remove the directory tree
            // (matching C's git_futils_rmdir_r with GIT_RMDIR_REMOVE_FILES).
            // When the root directory pre-existed, C adds GIT_RMDIR_SKIP_ROOT
            // (clone.c:638-640, 666-676): contents (including the .git created
            // by init) are removed but the pre-existing root is kept.
            if (repo is not null)
            {
                await repo.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                if (createdDir)
                {
                    Directory.Delete(localPath, recursive: true);
                }
                else
                {
                    // GIT_RMDIR_SKIP_ROOT: remove the contents, keep the root.
                    foreach (string entry in Directory.GetFileSystemEntries(localPath))
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                }
            }
            catch
            {
                // Best-effort cleanup — matches C's (void)git_futils_rmdir_r.
            }
            throw;
        }
    }

    /// <summary>
    /// Submodule clone entry point: allows cloning into an existing
    /// non-empty directory (use_existing=true). Matches
    /// <c>git_clone__submodule</c> (clone.c:691-698).
    /// </summary>
    internal static async Task<GitRepository> RunForSubmoduleAsync(
        string url,
        string localPath,
        GitCloneOptions? options,
        GitContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(localPath);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new GitCloneOptions();

        GitRepository? repo = null;

        try
        {
            repo = (options.RepositoryCreate is { } repoCb)
                ? await repoCb(localPath, options.Bare, context, cancellationToken).ConfigureAwait(false)
                : await GitRepository.InitAsync(localPath, options.Bare, context, cancellationToken).ConfigureAwait(false);

            GitRemote remote = await CreateAndConfigureOriginAsync(repo, url, options, cancellationToken).ConfigureAwait(false);

            bool cloneLocal = ShouldCloneLocal(url, options.CloneLocal);
            string reflogMessage = $"clone: from {remote.Url}";

            if (cloneLocal)
            {
                await CloneLocalIntoAsync(repo, remote, options, reflogMessage, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CloneIntoAsync(repo, remote, options, reflogMessage, cancellationToken).ConfigureAwait(false);
            }

            return repo;
        }
        catch
        {
            // Submodule clones use use_existing=1 → the target dir always
            // pre-exists → SKIP_ROOT: remove contents, keep the root.
            if (repo is not null)
            {
                await repo.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(localPath))
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
            }
            catch
            {
                // Best-effort cleanup — matches C's (void)git_futils_rmdir_r.
            }
            throw;
        }
    }

    // ==============================
    // Local vs network decision
    // ==============================

    /// <summary>
    /// Decides whether to use the local-clone path based on the URL
    /// and <see cref="GitCloneLocal"/> option.
    /// Matches <c>git_clone__should_clone_local</c> (clone.c:571-599).
    /// </summary>
    internal static bool ShouldCloneLocal(string urlOrPath, GitCloneLocal local)
    {
        if (local == GitCloneLocal.NoLocal)
        {
            return false;
        }

        if (IsUrl(urlOrPath))
        {
            // C (clone.c:583-594, git_clone__should_clone_local): "If
            // GIT_CLONE_LOCAL_AUTO is specified, any url should be treated
            // as remote" — file:// URLs are local ONLY under
            // GIT_CLONE_LOCAL (or LOCAL_AND_LINKS).
            if (local == GitCloneLocal.Auto || !GitUrlUtils.IsLocalFileUrl(urlOrPath))
            {
                return false;
            }

            // CloneLocal.Local + file:// URL.
            string localPath = GitUrlUtils.LocalPathFromUrl(urlOrPath);
            return Directory.Exists(localPath);
        }

        // Not a URL — it's a path. CloneLocal.Local or Auto → local if dir exists.
        return Directory.Exists(urlOrPath);
    }

    /// <summary>
    /// Returns true if the string is a URL (contains <c>://</c>).
    /// Matches <c>git_net_str_is_url</c>.
    /// </summary>
    private static bool IsUrl(ReadOnlySpan<char> str)
    {
        // C (util/net.c:95-108, git_net_str_is_url): the scheme before "://" may contain only [A-Za-z0-9+-.] — "./foo://bar" is NOT a URL.
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (c == ':' && i + 2 < str.Length && str[i + 1] == '/' && str[i + 2] == '/')
            {
                return true;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
            {
                break;
            }
        }

        return false;
    }

    // ==============================
    // Network clone
    // ==============================

    /// <summary>
    /// Network clone: connect, detect OID type, fetch, checkout.
    /// Matches <c>clone_into</c> (clone.c:412-463).
    /// </summary>
    private static async Task CloneIntoAsync(
        GitRepository repo,
        GitRemote remote,
        GitCloneOptions options,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        // C (clone.c:426-429): clone_into rejects a non-empty target
        // repository — GIT_ERROR_INVALID "the repository is not empty".
        await EnsureRepositoryEmptyAsync(repo, cancellationToken).ConfigureAwait(false);

        // Connect to detect OID type.
        var connectOpts = new GitRemoteConnectOptions
        {
            Callbacks = options.FetchOptions?.RemoteCallbacks,
            Proxy = options.FetchOptions?.ProxyConfig,
            FollowRedirects = options.FetchOptions?.FollowRedirects ?? GitRemoteRedirect.Unspecified,
            CustomHeaders = options.FetchOptions?.CustomHeaders,
            Depth = options.FetchOptions?.Depth ?? 0,
            ScpPortOverride = options.FetchOptions?.ScpPortOverride,
        };

        await remote.ConnectAsync(GitDirection.Fetch, connectOpts, cancellationToken).ConfigureAwait(false);

        // C (clone.c:444-450): the target adopts the remote's object format before the fetch so the downloaded pack parses correctly.
        repo.SetObjectFormat(remote.OidType);

        // Fetch.
        GitFetchOptions fetchOpts = options.FetchOptions ?? new GitFetchOptions
        {
            DownloadTags = GitAutoTagOption.All,
            UpdateFetchhead = false,
        };

        // Enforce clone defaults: update_fetchhead=false, download_tags=All
        // if depth is 0.
        GitFetchOptions effectiveOpts = fetchOpts with
        {
            UpdateFetchhead = false,
            DownloadTags = fetchOpts.Depth == 0
                ? GitAutoTagOption.All
                : fetchOpts.DownloadTags,
        };

        await remote.FetchAsync(refspecs: null, effectiveOpts, reflogMessage, cancellationToken).ConfigureAwait(false);

        // Checkout.
        await CheckoutBranchAsync(repo, remote, options, reflogMessage, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Local clone (hardlink/copy objects + fetch)
    // ==============================

    /// <summary>
    /// Local clone: copy objects dir from source (hardlink if possible),
    /// then fetch (for refs) and checkout.
    /// Matches <c>clone_local_into</c> (clone.c:489-569).
    /// </summary>
    private static async Task CloneLocalIntoAsync(
        GitRepository repo,
        GitRemote remote,
        GitCloneOptions options,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        // C (clone.c:503-506): clone_local_into rejects a non-empty target
        // repository — GIT_ERROR_INVALID "the repository is not empty".
        await EnsureRepositoryEmptyAsync(repo, cancellationToken).ConfigureAwait(false);

        // Resolve the source path from the URL.
        string srcPath = GitUrlUtils.LocalPathFromUrl(remote.Url!); // clone requires a fetch URL
        GitRepository source = await GitRepository.OpenAsync(srcPath, repo.Context, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sourceDisposable = source.ConfigureAwait(false);

        // C (clone.c:522-526): the target adopts the SOURCE repository's object format so it can read the copied object database.
        repo.SetObjectFormat(source.ObjectFormat);

        // Copy objects directory from source to target.
        string srcObjects = PathHelpers.Join(source.Path, "objects");
        string dstObjects = PathHelpers.Join(repo.Path, "objects");

        bool canLink = options.CloneLocal != GitCloneLocal.NoLinks &&
            CanLink(source.Path, repo.Path);

        // C (clone.c:534-550): git_futils_cp_r with GIT_CPDIR_LINK_FILES hard-links object files; on ANY failure the ENTIRE copy is retried without links
        // (clone.c:544-549) — never a per-file fallback that mixes hardlinks and copies.
        try
        {
            CopyObjectsDir(srcObjects, dstObjects, hardlink: canLink, cancellationToken);
        }
        catch (Exception ex) when (canLink && ex is not OperationCanceledException)
        {
            CopyObjectsDir(srcObjects, dstObjects, hardlink: false, cancellationToken);
        }

        // Fetch (refs + any missing objects).
        GitFetchOptions fetchOpts = options.FetchOptions ?? new GitFetchOptions
        {
            DownloadTags = GitAutoTagOption.All,
            UpdateFetchhead = false,
        };

        GitFetchOptions effectiveOpts = fetchOpts with
        {
            UpdateFetchhead = false,
            DownloadTags = fetchOpts.Depth == 0
                ? GitAutoTagOption.All
                : fetchOpts.DownloadTags,
        };

        await remote.FetchAsync(refspecs: null, effectiveOpts, reflogMessage, cancellationToken).ConfigureAwait(false);

        // Checkout.
        await CheckoutBranchAsync(repo, remote, options, reflogMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the objects directory from source to destination, optionally
    /// using hardlinks. Matches <c>git_futils_cp_r</c> with
    /// <c>GIT_CPDIR_LINK_FILES</c>: each file is hard-linked (link(2)) when
    /// possible, falling back to a plain copy on any error.
    /// </summary>
    private static void CopyObjectsDir(string srcDir, string dstDir, bool hardlink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dstDir);

        // Copy all files, optionally hardlinking. C's _cp_r_callback
        // classifies entries with lstat (futils.c:1034): a symlink is
        // neither S_ISDIR nor — without GIT_CPDIR_COPY_SYMLINKS — a regular
        // file, so it is skipped entirely (futils.c:1069-1072). Directory.GetFiles
        // + File.Copy would follow symlinks (copying the link TARGET), so the
        // IsSymlink guard below is required to match C.
        foreach (string file in Directory.GetFiles(srcDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymlink(file))
            {
                continue;
            }

            string fileName = Path.GetFileName(file);
            string dstFile = PathHelpers.Join(dstDir, fileName);

            if (hardlink)
            {
                // File.CreateHardLink(pathToNewFile, pathToExistingFile) is link(2) — a HARD link sharing the inode, NOT a symlink (note the .NET argument
                // order: new link first, existing file second). A failure (cross-device, fs without hardlinks) propagates so the CALLER retries the whole copy
                // without links.
                File.CreateHardLink(dstFile, file);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, dstFile, overwrite: true);
        }

        // Recurse into subdirectories. Directory.GetDirectories lists a
        // symlink-to-directory as a directory; C's lstat-based walk
        // never sees a symlink as S_ISDIR, so skip them here — otherwise a
        // symlink loop (objects/loop -> objects) recurses without bound.
        foreach (string dir in Directory.GetDirectories(srcDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymlink(dir))
            {
                continue;
            }

            string dirName = Path.GetFileName(dir);
            CopyObjectsDir(dir, PathHelpers.Join(dstDir, dirName), hardlink, cancellationToken);
        }
    }

    /// <summary>
    /// Lstat-style classification: true when <paramref name="path"/> is a
    /// symbolic link (never following it). Matches C's
    /// <c>git_fs_path_lstat</c> + <c>S_ISLNK</c> in <c>_cp_r_callback</c>
    /// (futils.c:1034-1072). A stat failure (entry vanished mid-walk) is
    /// treated as not-a-symlink so the entry proceeds through the normal
    /// copy path, which surfaces the natural error.
    /// </summary>
    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if source and destination gitdirs are on the same filesystem
    /// (so object files can be hard-linked). Matches <c>can_link</c>
    /// (clone.c:465-487): <c>st_dev</c> comparison of the two paths.
    /// On Windows, always returns false (no hardlinks for objects).
    /// </summary>
    private static bool CanLink(string src, string dst)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            uint? srcDev = NativeStat.GetStat(new FileInfo(src)).Dev;
            uint? dstDev = NativeStat.GetStat(new FileInfo(dst)).Dev;
            return srcDev is { } sd && dstDev is { } dd && sd == dd;
        }
        catch
        {
            // Any stat failure → don't attempt hardlinks.
            return false;
        }
    }

    // ==============================
    // Checkout / HEAD setup
    // ==============================

    /// <summary>
    /// Sets up HEAD and optionally checks out the working tree.
    /// Matches <c>checkout_branch</c> (clone.c:385-410).
    /// </summary>
    private static async Task CheckoutBranchAsync(
        GitRepository repo,
        GitRemote remote,
        GitCloneOptions options,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        // The smart transport now retains its advertised heads across
        // CloseAsync (like C's git_smart__close, smart.c:370-411), so the
        // LsAsync calls below see the fetch's heads without reconnecting.
        if (options.BranchName is { } branchName)
        {
            await UpdateHeadToBranchAsync(repo, remote, branchName, reflogMessage, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await UpdateHeadToRemoteAsync(repo, remote, reflogMessage, cancellationToken).ConfigureAwait(false);
        }

        await remote.DisconnectAsync(cancellationToken).ConfigureAwait(false);

        // Determine if we should checkout. C (clone.c:403): should_checkout(..., git_repository_is_bare(repo), opts) — the ACTUAL repository bareness, not
        // options.Bare (a custom RepositoryCreate may differ).
        if (repo.IsBare)
        {
            return;
        }

        if (options.CheckoutOptions?.Strategy == Checkout.GitCheckoutStrategy.None)
        {
            return;
        }

        // Check if HEAD is unborn (no commit yet).
        GitReference? headResolved = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (headResolved is null)
        {
            return;
        }

        await repo.CheckoutHeadAsync(options.CheckoutOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets HEAD to the remote's default branch. Matches
    /// <c>update_head_to_remote</c> (clone.c:214-258).
    /// </summary>
    private static async Task UpdateHeadToRemoteAsync(
        GitRepository repo,
        GitRemote remote,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(cancellationToken).ConfigureAwait(false);

        // Empty remote or unborn HEAD → use default.
        if (heads.Count == 0 || heads[0].Name != "HEAD")
        {
            await UpdateHeadToDefaultAsync(repo, remote, cancellationToken).ConfigureAwait(false);
            return;
        }

        GitOid remoteHeadId = heads[0].Oid;

        // Try to determine the default branch.
        string? branchName = await remote.DefaultBranchAsync(cancellationToken).ConfigureAwait(false);

        if (branchName is null)
        {
            // No default branch found → detached HEAD.
            await repo.SetHeadDetachedAsync(remoteHeadId, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Strip refs/heads/ prefix for the local branch name.
        string shortName = branchName.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? branchName["refs/heads/".Length..]
            : branchName;

        // Create refs/remotes/<remote>/HEAD -> refs/remotes/<remote>/<branch>.
        await UpdateRemoteHeadAsync(repo, remote, branchName, reflogMessage, cancellationToken).ConfigureAwait(false);

        // Create local tracking branch and set HEAD.
        await UpdateHeadToNewBranchAsync(repo, remote, remoteHeadId, shortName, reflogMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets HEAD to a specific user-requested branch. Matches
    /// <c>update_head_to_branch</c> (clone.c:260-302).
    /// </summary>
    private static async Task UpdateHeadToBranchAsync(
        GitRepository repo,
        GitRemote remote,
        string branch,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        // Resolve the remote tracking ref: refs/remotes/<remote>/<branch>.
        string remoteRefName = $"refs/remotes/{remote.Name}/{branch}";
        GitReference remoteRef = await repo.Refs.LookupAsync(remoteRefName, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound,
                $"remote branch '{branch}' not found", GitErrorCategory.Reference);

        GitOid target;
        if (remoteRef is GitDirectReference dr)
        {
            target = dr.Target;
        }
        else
        {
            GitReference? resolved = await repo.Refs.ResolveAsync(remoteRefName, cancellationToken).ConfigureAwait(false);
            target = resolved is GitDirectReference dr2 ? dr2.Target : default;
        }

        // Create local tracking branch.
        await UpdateHeadToNewBranchAsync(repo, remote, target, branch, reflogMessage, cancellationToken).ConfigureAwait(false);

        // C (clone.c:285-295, update_head_to_branch): git_remote__default_branch
        // is resolved and, if it fits a fetch refspec, update_remote_head is
        // called with the DEFAULT branch — no comparison with the requested
        // `branch`. GIT_ENOTFOUND (no default) is treated as success.
        string? defaultBranch = await remote.DefaultBranchAsync(cancellationToken).ConfigureAwait(false);
        if (defaultBranch is null)
        {
            return;
        }

        // Check if the default branch matches a fetch refspec.
        foreach (GitRefSpec spec in remote.RefSpecs)
        {
            if (!spec.IsFetch)
            {
                continue;
            }

            if (spec.SrcMatches(defaultBranch))
            {
                await UpdateRemoteHeadAsync(repo, remote, defaultBranch, reflogMessage, cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    /// <summary> Creates a local tracking branch, sets up tracking config, and points HEAD at it. Matches <c>update_head_to_new_branch</c> (clone.c:112-138):
    /// when the branch already exists (EEXISTS — e.g. a custom refspec created it), the tracking config is NOT written and HEAD is NOT moved. </summary>
    private static async Task UpdateHeadToNewBranchAsync(
        GitRepository repo,
        GitRemote remote,
        GitOid target,
        string name,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        // Strip refs/heads/ if present.
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            name = name["refs/heads/".Length..];
        }

        // Create the branch ref.
        string refName = $"refs/heads/{name}";
        try
        {
            await repo.Refs.CreateAsync(refName, target, force: false, logMessage: reflogMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.Exists)
        {
            // C (clone.c:121-135): create_tracking_branch returns the EEXISTS
            // error (skipping setup_tracking_config) and update_head_to_new_
            // branch swallows it — no tracking config, no set_head.
            return;
        }

        // Set up tracking config: branch.<name>.remote + branch.<name>.merge. C hardcodes GIT_REMOTE_ORIGIN (clone.c:108-109); the C# RemoteName extension
        // threads the actual remote name through.
        await SetupTrackingConfigAsync(repo, name, remote.Name ?? "origin", refName, cancellationToken).ConfigureAwait(false);

        // Set HEAD to point at the new branch.
        await repo.SetHeadAsync(refName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the <c>refs/remotes/&lt;remote&gt;/HEAD</c> symbolic ref
    /// pointing at the tracking branch. Matches <c>update_remote_head</c>
    /// (clone.c:165-212).
    /// </summary>
    private static async Task UpdateRemoteHeadAsync(
        GitRepository repo,
        GitRemote remote,
        string targetBranch,
        string reflogMessage,
        CancellationToken cancellationToken)
    {
        // Find the fetch refspec that matches the target branch.
        GitRefSpec? matchSpec = null;
        foreach (GitRefSpec spec in remote.RefSpecs)
        {
            if (!spec.IsFetch)
            {
                continue;
            }

            if (spec.SrcMatches(targetBranch))
            {
                matchSpec = spec;
                break;
            }
        }

        if (matchSpec is null)
        {
            throw new GitException(GitErrorCode.InvalidSpec,
                "the remote's default branch does not fit the refspec configuration",
                GitErrorCategory.Net);
        }

        // Transform to get the tracking ref name.
        string trackingRef = matchSpec.Transform(targetBranch);

        // Create symbolic ref refs/remotes/<remote>/HEAD -> <trackingRef>.
        // C (clone.c:199-205): git_reference_symbolic_create errors PROPAGATE
        // (update_remote_head returns them).
        string symrefName = $"refs/remotes/{remote.Name}/HEAD";
        await repo.Refs.CreateSymbolicAsync(symrefName, trackingRef, force: true,
            logMessage: reflogMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets up tracking config for a branch. Matches
    /// <c>setup_tracking_config</c> (clone.c:60-91).
    /// </summary>
    private static async Task SetupTrackingConfigAsync(
        GitRepository repo,
        string branchName,
        string remoteName,
        string mergeTarget,
        CancellationToken cancellationToken)
    {
        await repo.Config.SetStringAsync($"branch.{branchName}.remote", remoteName, cancellationToken).ConfigureAwait(false);
        await repo.Config.SetStringAsync($"branch.{branchName}.merge", mergeTarget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Sets HEAD to the repo's default branch when the remote has no usable HEAD. Matches <c>update_head_to_default</c> (clone.c:140-163); the
    /// tracking config uses the actual remote name. </summary>
    private static async Task UpdateHeadToDefaultAsync(GitRepository repo, GitRemote remote, CancellationToken cancellationToken)
    {
        string initialBranch = await repo.InitialBranchAsync(cancellationToken).ConfigureAwait(false);
        if (!initialBranch.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return;
        }

        string shortName = initialBranch["refs/heads/".Length..];
        await SetupTrackingConfigAsync(repo, shortName, remote.Name ?? "origin", initialBranch, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Origin creation
    // ==============================

    /// <summary>
    /// Creates and configures the "origin" remote. Matches
    /// <c>create_and_configure_origin</c> (clone.c:327-362).
    /// </summary>
    private static async Task<GitRemote> CreateAndConfigureOriginAsync(
        GitRepository repo,
        string url,
        GitCloneOptions options,
        CancellationToken cancellationToken)
    {
        string remoteName = options.RemoteName ?? "origin";

        // Resolve local relative paths to absolute. C (clone.c:340-346): p_realpath — symlinks are RESOLVED (GetFullPath would keep the symlink path in
        // remote.origin.url).
        string effectiveUrl = url;
        if (!IsUrl(url) && !Path.IsPathRooted(url) && PathHelpers.Exists(url))
        {
            effectiveUrl = NativeStat.TryRealpath(url) ?? Path.GetFullPath(url);
        }

        if (options.RemoteCreate is { } remoteCb)
        {
            return await remoteCb(repo, remoteName, effectiveUrl, cancellationToken).ConfigureAwait(false);
        }

        return await GitRemote.CreateAsync(repo, remoteName, effectiveUrl, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Helpers
    // ==============================

    /// <summary>
    /// Throws GIT_ERROR_INVALID "the repository is not empty" (rc=-1) when
    /// the target repository already contains refs. Matches the guards at the
    /// top of <c>clone_into</c> (clone.c:426-429) and
    /// <c>clone_local_into</c> (clone.c:503-506).
    /// </summary>
    private static async Task EnsureRepositoryEmptyAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        if (!await IsRepositoryEmptyAsync(repo, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Error,
                "the repository is not empty",
                GitErrorCategory.Invalid);
        }
    }

    /// <summary>
    /// Determines whether the repository is empty. Matches
    /// <c>git_repository_is_empty</c> (repository.c:3141-3160): HEAD must be
    /// a symbolic ref pointing at the initial branch and no other refs may
    /// exist.
    /// </summary>
    private static async Task<bool> IsRepositoryEmptyAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        GitReference? head = await repo.Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is not GitSymbolicReference sym)
        {
            return false;
        }

        string initialBranch = await repo.InitialBranchAsync(cancellationToken).ConfigureAwait(false);
        if (sym.TargetNameKey != (RefNameKey)initialBranch)
        {
            return false;
        }

        bool hasRef = false;
        await foreach (RefNameKey _ in repo.Refs.ListNameKeysAsync(null, cancellationToken).ConfigureAwait(false))
        {
            hasRef = true;
            break;
        }

        return !hasRef;
    }

    /// <summary>
    /// Checks if a directory is empty (contains no files or subdirs).
    /// </summary>
    private static bool IsDirEmpty(string path)
    {
        try
        {
            return Directory.GetFiles(path).Length == 0 &&
                   Directory.GetDirectories(path).Length == 0;
        }
        catch
        {
            return false;
        }
    }
}

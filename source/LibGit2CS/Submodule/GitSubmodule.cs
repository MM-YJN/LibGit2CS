// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Submodule;

/// <summary>
/// A git submodule. Managed port of libgit2's
/// <c>src/libgit2/submodule.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// A submodule is a git repository embedded inside another repository. The
/// parent repo records the submodule as a gitlink entry (mode
/// <c>0160000</c>) in its index and tree, with the submodule's commit OID.
/// The <c>.gitmodules</c> file in the workdir maps submodule names to paths
/// and URLs.
/// </para>
/// <para>
/// Covers lookup, config (set*), status, init, sync, open, and reload.
/// </para>
/// </remarks>
public sealed class GitSubmodule : IDisposable
{
    private bool _disposed;

    internal GitSubmodule(GitRepository repo, string name)
    {
        Owner = repo;
        Name = name;
        NameBytes = Encoding.UTF8.GetBytes(name);
        Path = GitPath.FromUtf8String(name);
        // C (submodule.c:1895, submodule_alloc): the default ignore is
        // GIT_SUBMODULE_IGNORE_NONE — a submodule without an ignore key in
        // .gitmodules gets the full dirty computation.
        Ignore = SubmoduleIgnore.None;
        // C (submodule.c:1896, submodule_alloc): the default update strategy is GIT_SUBMODULE_UPDATE_CHECKOUT (1); the public getter normalizes anything below
        // Checkout to Checkout.
        UpdateStrategy = SubmoduleUpdateStrategy.Checkout;
        FetchRecurse = SubmoduleRecurse.No;
    }

    /// <summary> Byte-name constructor: the name bytes are stored verbatim (C's <c>submodule_alloc</c> keeps the raw <c>char *</c> name, submodule.c:1895) and
    /// <see cref="Name"/> is the UTF-8 display decode. </summary>
    internal GitSubmodule(GitRepository repo, ReadOnlyMemory<byte> name)
    {
        Owner = repo;
        Name = Encoding.UTF8.GetString(name.Span);
        NameBytes = name.ToArray();
        Path = GitPath.FromUtf8Bytes(name);
        // C (submodule.c:1895, submodule_alloc): the default ignore is
        // GIT_SUBMODULE_IGNORE_NONE — a submodule without an ignore key in
        // .gitmodules gets the full dirty computation.
        Ignore = SubmoduleIgnore.None;
        // C (submodule.c:1896, submodule_alloc): the default update strategy is GIT_SUBMODULE_UPDATE_CHECKOUT (1); the public getter normalizes anything below
        // Checkout to Checkout.
        UpdateStrategy = SubmoduleUpdateStrategy.Checkout;
        FetchRecurse = SubmoduleRecurse.No;
    }

    /// <summary>The parent (superproject) repository.</summary>
    public GitRepository Owner { get; }

    /// <summary>The submodule name (from .gitmodules).</summary>
    public string Name { get; internal set; }

    /// <summary> The submodule name as raw bytes. Byte-parity surface — C's <c>git_submodule_cache</c> keys on the raw <c>sm->name</c> bytes (submodule.c:268,
    /// 478); <see cref="Name"/> is the UTF-8 display decode. </summary>
    internal ReadOnlyMemory<byte> NameBytes { get; }

    /// <summary>The path where the submodule lives (may differ from name). Byte-faithful.</summary>
    public GitPath? Path { get; internal set; }

    /// <summary>Convenience: <see cref="Path"/> decoded as UTF-8 for display.</summary>
    public string? PathString => Path?.ToUtf8String();

    /// <summary>The submodule URL (from .gitmodules or .git/config).</summary>
    public string? Url { get; internal set; }

    /// <summary>The configured branch (from .gitmodules or .git/config).</summary>
    public string? Branch { get; internal set; }

    /// <summary>The effective ignore strategy.</summary>
    public SubmoduleIgnore Ignore { get; internal set; }

    /// <summary> The effective update strategy. Matches <c>git_submodule_update_strategy</c> (submodule.c:1255-1261): values below Checkout are normalized to
    /// Checkout. </summary>
    public SubmoduleUpdateStrategy UpdateStrategy
    {
        get => _updateStrategy < SubmoduleUpdateStrategy.Checkout ? SubmoduleUpdateStrategy.Checkout : _updateStrategy;
        internal set => _updateStrategy = value;
    }

    private SubmoduleUpdateStrategy _updateStrategy = SubmoduleUpdateStrategy.Checkout;

    /// <summary>The fetch recursion strategy.</summary>
    public SubmoduleRecurse FetchRecurse { get; internal set; }

    /// <summary>The OID from the superproject HEAD tree.</summary>
    public GitOid HeadId { get; internal set; }

    /// <summary>The OID from the superproject index.</summary>
    public GitOid IndexId { get; internal set; }

    /// <summary>The OID from the submodule workdir's HEAD.</summary>
    public GitOid WdId { get; internal set; }

    /// <summary>
    /// Internal: the submodule path was seen as a directory in the workdir.
    /// Port of C's <c>GIT_SUBMODULE_STATUS__WD_SCANNED</c> (submodule.c:2164)
    /// — masked out of the public status, but required to distinguish
    /// WD_UNINITIALIZED (scanned, not in wd) from WD_DELETED.
    /// </summary>
    internal bool _wdScanned;

    /// <summary>Status flags.</summary>
    public SubmoduleStatus StatusFlags { get; internal set; }

    /// <summary>
    /// Returns true if the submodule status is unmodified (only location flags set).
    /// </summary>
    public bool IsUnmodified => (StatusFlags & ~InFlagsMask) == 0;

    private const SubmoduleStatus InFlagsMask = (SubmoduleStatus)0x000Fu;

    // ==============================
    // Lookup / ForEach
    // ==============================

    /// <summary>
    /// Looks up a submodule by name. Matches <c>git_submodule_lookup</c>
    /// (submodule.c:308-433). UTF-8 convenience tier — the byte-parity
    /// surface is <see cref="LookupAsync(GitRepository, GitPath, CancellationToken)"/>.
    /// </summary>
    /// <returns>The submodule.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> "no submodule named '&lt;name&gt;'"
    /// when the submodule is not configured and not present in the workdir;
    /// <see cref="GitErrorCode.Exists"/> "submodule '&lt;name&gt;' has not
    /// been added yet" when a repo exists at the workdir path but the
    /// submodule was never added (submodule.c:403-425).
    /// </exception>
    internal static async ValueTask<GitSubmodule> LookupAsync(GitRepository repo, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        if (repo.IsBare)
        {
            throw new GitException(
                GitErrorCode.Error, // C (submodule.c:329-332): generic -1
                "cannot get submodules without a working tree",
                GitErrorCategory.Submodule);
        }

        // C trims
        // trailing '/' from the lookup name (submodule.c:364-367) — the
        // public header documents "trailing slash is allowed" for
        // git_submodule_lookup.
        name = name.TrimEnd('/');

        SubmoduleCache cache = repo.GetOrInitSubmoduleCache();
        await cache.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        GitSubmodule? sm = await cache.LookupAsync(name, cancellationToken).ConfigureAwait(false);
        if (sm is not null)
        {
            return sm;
        }

        // C (submodule.c:403-425): if we still haven't found it, do the WD
        // check — a repo at <workdir>/<name>/.git means the submodule "has
        // not been added yet" (GIT_EEXISTS); otherwise ENOTFOUND with
        // submodule_set_lookup_error's message (submodule.c:97-105).
        GitErrorCode code = GitErrorCode.NotFound;
        string message = $"no submodule named '{name}'";
        if (repo.Workdir is not null)
        {
            string gitPath = PathHelpers.Join(PathHelpers.Join(repo.Workdir, name), ".git");
            if (File.Exists(gitPath) || Directory.Exists(gitPath))
            {
                code = GitErrorCode.Exists;
                message = $"submodule '{name}' has not been added yet";
            }
        }

        throw new GitException(code, message, GitErrorCategory.Submodule);
    }

    /// <summary> Looks up a submodule by name/path bytes. Matches <c>git_submodule_lookup</c> (submodule.c:308-433) — C takes the raw <c>char *</c> name bytes,
    /// so non-UTF-8 names/paths round-trip byte-exact. The trailing-<c>/</c> trim (submodule.c:364- 367) is applied in the byte domain. </summary> <returns>The
    /// submodule.</returns> <exception cref="GitException"> <see cref="GitErrorCode.NotFound"/> "no submodule named '&lt;name&gt;'" when the submodule is not
    /// configured and not present in the workdir; <see cref="GitErrorCode.Exists"/> "submodule '&lt;name&gt;' has not been added yet" when a repo exists at the
    /// workdir path but the submodule was never added (submodule.c:403-425). </exception>
    internal static async ValueTask<GitSubmodule> LookupAsync(GitRepository repo, GitPath name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        if (repo.IsBare)
        {
            throw new GitException(
                GitErrorCode.Error, // C (submodule.c:329-332): generic -1
                "cannot get submodules without a working tree",
                GitErrorCategory.Submodule);
        }

        // C trims
        // trailing '/' from the lookup name (submodule.c:364-367) — the
        // public header documents "trailing slash is allowed" for
        // git_submodule_lookup. Byte-domain trim (0x2F).
        while (name.Length > 0 && name.Span[^1] == (byte)'/')
        {
            name = name.Slice(0, name.Length - 1);
        }

        SubmoduleCache cache = repo.GetOrInitSubmoduleCache();
        await cache.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        GitSubmodule? sm = await cache.LookupAsync(name, cancellationToken).ConfigureAwait(false);
        if (sm is not null)
        {
            return sm;
        }

        // C (submodule.c:403-425): if we still haven't found it, do the WD
        // check — a repo at <workdir>/<name>/.git means the submodule "has
        // not been added yet" (GIT_EEXISTS); otherwise ENOTFOUND with
        // submodule_set_lookup_error's message (submodule.c:97-105).
        string nameString = name.ToUtf8String();
        GitErrorCode code = GitErrorCode.NotFound;
        string message = $"no submodule named '{nameString}'";
        if (repo.Workdir is not null)
        {
            string gitPath = PathHelpers.Join(PathHelpers.Join(repo.Workdir, nameString), ".git");
            if (File.Exists(gitPath) || Directory.Exists(gitPath))
            {
                code = GitErrorCode.Exists;
                message = $"submodule '{nameString}' has not been added yet";
            }
        }

        throw new GitException(code, message, GitErrorCategory.Submodule);
    }

    /// <summary>
    /// Iterates over all submodules. Matches <c>git_submodule_foreach</c>
    /// (submodule.c:270).
    /// </summary>
    internal static async IAsyncEnumerable<GitSubmodule> ForEachAsync(GitRepository repo, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        if (repo.IsBare)
        {
            throw new GitException(
                GitErrorCode.Error, // C (submodule.c:329-332): generic -1
                "cannot get submodules without a working tree",
                GitErrorCategory.Submodule);
        }

        SubmoduleCache cache = repo.GetOrInitSubmoduleCache();
        await cache.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        foreach (GitSubmodule sm in await cache.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return sm;
        }
    }

    // ==============================
    // Set* (config writes)
    // ==============================

    /// <summary>
    /// Sets the submodule URL. Matches <c>git_submodule_set_url</c>
    /// (submodule.h:434).
    /// </summary>
    internal static async Task SetUrlAsync(GitRepository repo, string name, string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(url);

        await WriteGitmodulesVarAsync(repo, name, "url", url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the submodule branch. Matches <c>git_submodule_set_branch</c>
    /// (submodule.h:420). Pass null to clear the branch.
    /// </summary>
    internal static async Task SetBranchAsync(GitRepository repo, string name, string? branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        if (branch is null)
        {
            await DeleteGitmodulesVarAsync(repo, name, "branch", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteGitmodulesVarAsync(repo, name, "branch", branch, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets the submodule ignore strategy. Matches <c>git_submodule_set_ignore</c>
    /// (submodule.h:501).
    /// </summary>
    internal static async Task SetIgnoreAsync(GitRepository repo, string name, SubmoduleIgnore ignore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        string value = ignore switch
        {
            SubmoduleIgnore.None => "none",
            SubmoduleIgnore.Untracked => "untracked",
            SubmoduleIgnore.Dirty => "dirty",
            SubmoduleIgnore.All => "all",
            // C (submodule.c:1159-1173): write_mapped_var reports an invalid enum value as GIT_ERROR_SUBMODULE "invalid value for %s".
            _ => throw new GitException(GitErrorCode.Error, "invalid value for ignore", GitErrorCategory.Submodule),
        };
        await WriteGitmodulesVarAsync(repo, name, "ignore", value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the submodule update strategy. Matches <c>git_submodule_set_update</c>
    /// (submodule.h:529).
    /// </summary>
    internal static async Task SetUpdateAsync(GitRepository repo, string name, SubmoduleUpdateStrategy update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        string value = update switch
        {
            SubmoduleUpdateStrategy.Checkout => "checkout",
            SubmoduleUpdateStrategy.Rebase => "rebase",
            SubmoduleUpdateStrategy.Merge => "merge",
            SubmoduleUpdateStrategy.None => "none",
            _ => throw new GitException(GitErrorCode.Error, "invalid value for update", GitErrorCategory.Submodule),
        };
        await WriteGitmodulesVarAsync(repo, name, "update", value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the submodule fetch recursion. Matches
    /// <c>git_submodule_set_fetch_recurse_submodules</c> (submodule.h:559).
    /// </summary>
    internal static async Task SetFetchRecurseAsync(GitRepository repo, string name, SubmoduleRecurse recurse, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        // C (submodule.c:1159-1172 + config.c:1420-1438, write_mapped_var): RECURSE_NO maps to the GIT_CONFIGMAP_FALSE entry whose string is NULL → write_var
        // DELETES the key; only YES is written as "true".
        if (recurse == SubmoduleRecurse.No)
        {
            await DeleteGitmodulesVarAsync(repo, name, "fetchRecurseSubmodules", cancellationToken).ConfigureAwait(false);
            return;
        }

        string value = recurse switch
        {
            SubmoduleRecurse.Yes => "true",
            SubmoduleRecurse.OnDemand => "on-demand",
            _ => throw new GitException(GitErrorCode.Error, "invalid value for fetchRecurseSubmodules", GitErrorCategory.Submodule),
        };
        await WriteGitmodulesVarAsync(repo, name, "fetchRecurseSubmodules", value, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Init / Sync
    // ==============================

    /// <summary>
    /// Copies submodule config from .gitmodules to .git/config. Matches
    /// <c>git_submodule_init</c> (submodule.c:1498-1540). C writes exactly two
    /// keys: <c>submodule.NAME.url</c> (the URL <b>resolved</b> against the
    /// parent's default remote / workdir) and <c>submodule.NAME.update</c> —
    /// where <c>update = checkout</c> (or an absent update) deletes the
    /// update key. branch/ignore/fetchRecurseSubmodules are NOT copied.
    /// </summary>
    /// <param name="overwrite">If true, overwrite existing .git/config entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitAsync(bool overwrite = false, CancellationToken cancellationToken = default)
    {
        // C (submodule.c:1505-1509): a submodule without a URL errors.
        if (Url is null)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"no URL configured for submodule '{Name}'",
                GitErrorCategory.Submodule);
        }

        // C (submodule.c:1516): write "submodule.NAME.url" — the RESOLVED url
        // (git_submodule__resolve_url against the parent base).
        string effectiveUrl = await ResolveUrlAsync(Owner, Url, cancellationToken).ConfigureAwait(false);
        await UpdateConfigEntryAsync(
            Owner.Config, $"submodule.{Name}.url", effectiveUrl,
            overwrite, onlyIfExisting: false, cancellationToken).ConfigureAwait(false);

        // C (submodule.c:1522-1530): write "submodule.NAME.update" if not
        // checkout — checkout (or Default) yields NULL, which deletes the key
        // when overwriting.
        string? updateValue = UpdateStrategy == SubmoduleUpdateStrategy.Checkout
            ? null
            : SubmoduleUpdateToStr(UpdateStrategy);
        await UpdateConfigEntryAsync(
            Owner.Config, $"submodule.{Name}.update", updateValue,
            overwrite, onlyIfExisting: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>git_config__update_entry</c> (config.c): set/delete a config
    /// key honoring <paramref name="overwrite"/> (skip when the entry exists
    /// and overwrite is false) and <paramref name="onlyIfExisting"/> (skip
    /// when the entry is absent). A null <paramref name="value"/> deletes the
    /// key; deleting an absent entry is a no-op.
    /// </summary>
    private static async ValueTask UpdateConfigEntryAsync(
        GitConfiguration config, string key, string? value,
        bool overwrite, bool onlyIfExisting, CancellationToken cancellationToken)
    {
        // byte-domain no-change compare (C's git_config__update_entry strcmps raw bytes, config.c:750).
        GitConfigEntry? existingEntry = await config.GetEntryAsync(key, cancellationToken).ConfigureAwait(false);
        bool exists = existingEntry is not null;

        if (!exists && onlyIfExisting)
        {
            return;
        }

        if (exists && !overwrite)
        {
            return;
        }

        if (value is not null && exists
            && existingEntry is { } entry
            && entry.ValueBytes is { } existingBytes
            && ConfigKeyName.AsciiEquals(existingBytes.Span, Encoding.UTF8.GetBytes(value)))
        {
            return;
        }

        if (value is null && !exists)
        {
            return;
        }

        if (value is null)
        {
            await config.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await config.SetStringAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// C (submodule.c:1101-1108, submodule_update_to_str): the config string
    /// for an update strategy (the first _sm_update_map entry with a matching
    /// map_value); null for values without a string form (Default).
    /// </summary>
    private static string? SubmoduleUpdateToStr(SubmoduleUpdateStrategy update)
        => update switch
        {
            SubmoduleUpdateStrategy.Checkout => "checkout",
            SubmoduleUpdateStrategy.Rebase => "rebase",
            SubmoduleUpdateStrategy.Merge => "merge",
            SubmoduleUpdateStrategy.None => "none",
            _ => null,
        };

    /// <summary>
    /// Syncs the submodule's URL by writing the superproject's resolved URL
    /// into the submodule's .git/config. Matches <c>git_submodule_sync</c>
    /// (submodule.c:1542-1585). C (1) errors without a URL, (2) writes
    /// <c>submodule.NAME.url</c> to .git/config only if the key already
    /// exists (only_if_existing), (3) resolves the URL, and (4) updates the
    /// remote of the submodule's HEAD tracking branch (falling back to
    /// <c>remote.origin.url</c>).
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        // C (submodule.c:1549-1552): a submodule without a URL errors.
        if (Url is null)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"no URL configured for submodule '{Name}'",
                GitErrorCategory.Submodule);
        }

        // C (submodule.c:1554-1559): copy URL over to config ONLY IF it
        // already exists (git_config__update_entry with only_if_existing).
        string resolvedUrl = await ResolveUrlAsync(Owner, Url, cancellationToken).ConfigureAwait(false);
        await UpdateConfigEntryAsync(
            Owner.Config, $"submodule.{Name}.url", resolvedUrl,
            overwrite: true, onlyIfExisting: true, cancellationToken).ConfigureAwait(false);

        // C (submodule.c:1561-1562): if the submodule is not in the workdir, done.
        if ((StatusFlags & SubmoduleStatus.InWd) == 0)
        {
            return;
        }

        GitRepository? smRepo = await OpenInternalAsync(Owner.Context, cancellationToken).ConfigureAwait(false);
        if (smRepo is null)
        {
            return;
        }

        try
        {
            // C (submodule.c:1569-1574): the remote of the local tracking
            // branch HEAD points to (lookup_head_remote_key), else
            // "remote.origin.url".
            string? remoteName = await LookupHeadRemoteKeyAsync(smRepo, cancellationToken).ConfigureAwait(false);
            string key = remoteName is null ? "remote.origin.url" : $"remote.{remoteName}.url";

            // C (submodule.c:1576): overwrite = true, only_if_existing = false.
            await UpdateConfigEntryAsync(
                smRepo.Config, key, resolvedUrl,
                overwrite: true, onlyIfExisting: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await smRepo.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ==============================
    // Open / Reload
    // ==============================

    /// <summary>
    /// Opens the submodule as a standalone repository. Matches
    /// <c>git_submodule_open</c> (submodule.c:1604-1633).
    /// </summary>
    public async Task<GitRepository?> OpenAsync(CancellationToken cancellationToken = default)
    {
        return await OpenInternalAsync(Owner.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitRepository?> OpenInternalAsync(GitContext context, CancellationToken cancellationToken)
    {
        if (Path is null || Path.Value.IsEmpty)
        {
            return null;
        }

        string smPath = PathHelpers.Join(Owner.Workdir, Path.Value.ToUtf8String());

        // Check if the submodule workdir exists and has a .git file/dir.
        string gitPath = PathHelpers.Join(smPath, ".git");
        if (!File.Exists(gitPath) && !Directory.Exists(gitPath))
        {
            return null;
        }

        try
        {
            return await GitRepository.OpenAsync(smPath, context, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            // C
            // PROPAGATES git_submodule_open failures (submodule.c:1458-1459)
            // — a broken .git (gitdir pointing at a missing modules dir,
            // corrupt config, missing HEAD) must fail the caller, not be
            // swallowed into a silent success.
            throw;
        }
    }

    /// <summary>
    /// Reloads the submodule's config and status from disk. Matches
    /// <c>git_submodule_reload</c> (submodule.c:1635-1650).
    /// </summary>
    public async Task ReloadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        SubmoduleCache cache = Owner.GetOrInitSubmoduleCache();
        await cache.ReloadSubmoduleAsync(this, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Status / Location
    // ==============================

    /// <summary>
    /// Computes the submodule status. Matches <c>git_submodule_status</c>
    /// (submodule.c:1774-1841).
    /// </summary>
    internal static async ValueTask<SubmoduleStatus> StatusAsync(GitRepository repo, string name, SubmoduleIgnore ignore = SubmoduleIgnore.Unspecified, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        // LookupAsync raises the C lookup errors (ENOTFOUND / EEXISTS).
        GitSubmodule sm = await LookupAsync(repo, name, cancellationToken).ConfigureAwait(false);

        return await sm.ComputeStatusAsync(ignore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the location flags for this submodule. Matches
    /// <c>git_submodule_location</c> (submodule.c:1843-...).
    /// </summary>
    public SubmoduleStatus Location()
    {
        return StatusFlags & InFlagsMask;
    }

    /// <summary>
    /// Computes the submodule status from the current index/head/wd state.
    /// Matches <c>git_submodule__status</c> (submodule.c:1774-1841).
    /// </summary>
    internal async ValueTask<SubmoduleStatus> ComputeStatusAsync(SubmoduleIgnore ignoreOverride, CancellationToken cancellationToken)
    {
        SubmoduleIgnore effectiveIgnore = ignoreOverride != SubmoduleIgnore.Unspecified
            ? ignoreOverride
            : Ignore;

        // If ignore is ALL, only return location flags.
        if (effectiveIgnore == SubmoduleIgnore.All)
        {
            return StatusFlags & InFlagsMask;
        }

        // C (submodule.c:1801-1809): with no persistent cache the index and
        // HEAD OIDs are refreshed on every status call.
        SubmoduleCache cache = Owner.GetOrInitSubmoduleCache();
        await cache.RefreshSubmoduleOidsAsync(this, cancellationToken).ConfigureAwait(false);

        // Compute index status (head vs index).
        SubmoduleStatus indexStatus = ComputeIndexStatus();

        // Compute wd status (index vs wd).
        SubmoduleStatus wdStatus = await ComputeWdStatusAsync(effectiveIgnore, cancellationToken).ConfigureAwait(false);

        return (StatusFlags & InFlagsMask) | indexStatus | wdStatus;
    }

    private SubmoduleStatus ComputeIndexStatus()
    {
        bool hasHead = !HeadId.IsZero;
        bool hasIndex = !IndexId.IsZero;

        if (!hasHead && hasIndex)
        {
            return SubmoduleStatus.IndexAdded;
        }

        if (hasHead && !hasIndex)
        {
            return SubmoduleStatus.IndexDeleted;
        }

        if (hasHead && hasIndex && HeadId != IndexId)
        {
            return SubmoduleStatus.IndexModified;
        }

        return 0;
    }

    private async Task<SubmoduleStatus> ComputeWdStatusAsync(SubmoduleIgnore ignore, CancellationToken cancellationToken)
    {
        // Exact port of git_submodule__status (submodule.c:1811-1841) +
        // submodule_get_wd_status (submodule.c:2357-2434). The repo is opened
        // BEFORE the status computation, and the open refreshes the WD OID
        // (submodule.c:1622) — so the oid-based flags always use fresh data.
        var status = (SubmoduleStatus)0;
        GitRepository? smRepo = null;

        // ignore == DIRTY: don't scan the working directory (submodule.c:1811-1817);
        // C still opens the submodule BARE, which loads the WD OID data.
        bool openBare = ignore == SubmoduleIgnore.Dirty;
        GitRepository? opened = await OpenInternalAsync(Owner.Context, cancellationToken).ConfigureAwait(false);

        // C (submodule.c:1607-1634, git_submodule__open / git_submodule_open_bare):
        // the IN_WD / WD_SCANNED / WD_OID_VALID state is cleared first, then
        // re-derived from the open outcome — a submodule whose .git vanished,
        // or whose HEAD can no longer be resolved, must not report stale
        // location bits or a stale WD OID.
        StatusFlags &= ~SubmoduleStatus.InWd;
        _wdScanned = false;
        WdId = GitOid.Empty;

        if (opened is not null)
        {
            // git_submodule__open sets IN_WD + WD_SCANNED on success
            // (submodule.c:1619-1620).
            StatusFlags |= SubmoduleStatus.InWd;
            _wdScanned = true;
            try
            {
                await RefreshWdOidAsync(opened, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (openBare)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!openBare)
            {
                smRepo = opened;
            }
        }
        else
        {
            // C (submodule.c:1626-1634): open failed — a `.git` at the path
            // still means IN_WD | WD_SCANNED; a bare directory means
            // WD_SCANNED only.
            string smPath = PathHelpers.Join(Owner.Workdir, Path?.ToUtf8String() ?? string.Empty);
            string gitPath = PathHelpers.Join(smPath, ".git");
            if (File.Exists(gitPath) || Directory.Exists(gitPath))
            {
                StatusFlags |= SubmoduleStatus.InWd;
                _wdScanned = true;
            }
            else if (Directory.Exists(smPath))
            {
                _wdScanned = true;
            }
        }

        // OID-based wd flags (submodule.c:2363-2375) — computed with the
        // freshly-refreshed WD OID; no early returns (the dirty diffs OR in).
        bool hasIndex = !IndexId.IsZero;
        bool hasWd = !WdId.IsZero;
        if (!hasIndex && hasWd)
        {
            status |= SubmoduleStatus.WdAdded;
        }
        else if (hasIndex && !hasWd)
        {
            // C (submodule.c:2377-2382): WD_UNINITIALIZED requires
            // WD_SCANNED && !IN_WD — a checked-out submodule whose HEAD
            // cannot be resolved has IN_WD set and reports WD_DELETED; a
            // bare directory (no .git) reports WD_UNINITIALIZED.
            status |= (_wdScanned && (StatusFlags & SubmoduleStatus.InWd) == 0)
                ? SubmoduleStatus.WdUninitialized
                : SubmoduleStatus.WdDeleted;
        }
        else if (hasIndex && hasWd && IndexId != WdId)
        {
            status |= SubmoduleStatus.WdModified;
        }

        // "if we have no repo, then we're done" (submodule.c:2376-2378).
        if (smRepo is null)
        {
            return status;
        }

        try
        {
            // head→index diff → WD_INDEX_MODIFIED (submodule.c:2389-2401).
            // C does
            // `if (git_repository_head_tree(&sm_head, sm_repo) < 0)
            // git_error_clear();` (submodule.c:2402-2403) — ANY failure
            // (unborn OR a missing HEAD commit object) just skips the
            // head→index diff, and status is reported without
            // WD_INDEX_MODIFIED. The NotFound is swallowed here.
            GitTree? headTree;
            try
            {
                headTree = await ResolveHeadTreeAsync(smRepo, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
            {
                headTree = null;
            }

            if (headTree is not null)
            {
                DiffGenerator diff1 = await DiffGenerator.TreeToIndexAsync(smRepo, headTree, null, cancellationToken).ConfigureAwait(false);
                if (diff1.Deltas.Count > 0)
                {
                    status |= SubmoduleStatus.WdIndexModified;
                }
            }

            // index→workdir diff → WD_UNTRACKED + WD_WD_MODIFIED. C adds
            // GIT_DIFF_INCLUDE_UNTRACKED only when ignore == NONE
            // (submodule.c:2396-2397).
            var diffOpts = new GitDiffOptions();
            if (ignore == SubmoduleIgnore.None)
            {
                diffOpts = diffOpts with { Flags = GitDiffOptionsFlags.IncludeUntracked };
            }

            DiffGenerator diff2 = await DiffGenerator.IndexToWorkdirAsync(smRepo, diffOpts, cancellationToken).ConfigureAwait(false);
            int untracked = 0;
            foreach (GitDiffDelta delta in diff2.Deltas)
            {
                if (delta.Status == GitDeltaStatus.Untracked)
                {
                    untracked++;
                }
            }

            if (untracked > 0)
            {
                status |= SubmoduleStatus.WdUntracked;
            }

            if (diff2.Deltas.Count != untracked)
            {
                status |= SubmoduleStatus.WdWdModified;
            }
        }
        finally
        {
            await smRepo.DisposeAsync().ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>
    /// Reads the submodule repo's HEAD into <see cref="WdId"/>. Matches the
    /// WD OID refresh in <c>git_submodule__open</c> (submodule.c:1622).
    /// </summary>
    private async Task RefreshWdOidAsync(GitRepository smRepo, CancellationToken cancellationToken)
    {
        GitReference? smHead = await smRepo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (smHead is GitDirectReference smDr && !smDr.Target.IsZero)
        {
            WdId = smDr.Target;
        }
    }

    private static async Task<GitTree?> ResolveHeadTreeAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        GitReference? head = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is not GitDirectReference dr)
        {
            return null;
        }

        GitOid targetOid = dr.Target;

        if (targetOid.IsZero)
        {
            return null;
        }

        Commit? commit = await repo.Objects.LookupAsync<Commit>(targetOid, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            // HEAD resolved to a non-zero OID, but the commit object is absent.
            // Inconsistent-DB state — not unborn (the unborn case returns
            // earlier on the null/symref/zero-OID paths). C's
            // checkout_lookup_head_tree propagates this as GIT_ENOTFOUND.
            throw new GitException(
                GitErrorCode.NotFound,
                $"HEAD points to commit {targetOid} but the object is not in the database",
                GitErrorCategory.Reference);
        }

        return await repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // ResolveUrl
    // ==============================

    /// <summary>
    /// Resolves a possibly relative submodule URL against the parent repo's
    /// base URL. Matches <c>git_submodule__resolve_url</c>
    /// (submodule.c:776-809). A relative URL (<c>./</c>/<c>../</c> prefix) is
    /// resolved against the parent's default remote URL (head's tracking
    /// remote, else origin, else the workdir) via <c>git_fs_path_apply_relative</c>;
    /// URLs containing <c>:</c> or starting with <c>/</c> are returned as-is;
    /// anything else is an error.
    /// </summary>
    public static async ValueTask<string> ResolveUrlAsync(GitRepository repo, string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(url);

        // C (submodule.c:788-794): normalize backslashes (Windows-authored
        // .gitmodules). Forward slashes are the internal convention.
        string normalized = url.Replace('\\', '/');

        // C (submodule.c:797-799): a relative path (./ or ../ prefix) is
        // resolved against the URL base.
        if (normalized.StartsWith("./", StringComparison.Ordinal) ||
            normalized.StartsWith("../", StringComparison.Ordinal))
        {
            string baseUrl = await GetUrlBaseAsync(repo, cancellationToken).ConfigureAwait(false);
            return ApplyRelative(baseUrl, normalized);
        }

        // C (submodule.c:800-801): contains ':' or starts with '/' → as-is.
        if (normalized.Contains(':', StringComparison.Ordinal) || normalized.StartsWith('/', StringComparison.Ordinal))
        {
            return normalized;
        }

        // C (submodule.c:802-805): "invalid format for submodule URL".
        throw new GitException(
            GitErrorCode.Error,
            "invalid format for submodule URL",
            GitErrorCategory.Submodule);
    }

    /// <summary>
    /// C (submodule.c:2309-2337, get_url_base): the base for resolving a
    /// relative submodule URL — the parent's default remote URL
    /// (lookup_default_remote), or the workdir when no remote exists.
    /// </summary>
    private static async Task<string> GetUrlBaseAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        GitRemote? remote = await LookupDefaultRemoteAsync(repo, cancellationToken).ConfigureAwait(false);
        if (remote is not null)
        {
            try
            {
                string? url = remote.Url;
                if (!string.IsNullOrEmpty(url))
                {
                    return url;
                }
            }
            finally
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }

        // C's
        // get_url_base uses the main worktree's directory (wt->parent_path)
        // as the fallback base for a linked-worktree superproject
        // (submodule.c:2323-2330), so relative submodule URLs resolve against
        // the right directory.
        if (repo.IsWorktree)
        {
            return PathHelpers.PrettifyDir(PathHelpers.Join(repo.CommonDir, ".."));
        }

        return repo.Workdir ?? ".";
    }

    /// <summary>
    /// C (submodule.c:2292-2307, lookup_default_remote): the remote of the
    /// local tracking branch HEAD points to, falling back to <c>origin</c>.
    /// Returns null when no default remote exists (C returns GIT_ENOTFOUND
    /// with "cannot get default remote for submodule - no local tracking
    /// branch for HEAD and origin does not exist").
    /// </summary>
    private static async Task<GitRemote?> LookupDefaultRemoteAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        string? remoteName = await LookupHeadRemoteKeyAsync(repo, cancellationToken).ConfigureAwait(false);
        remoteName ??= "origin";

        try
        {
            return await GitRemote.LookupAsync(repo, remoteName, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// C (submodule.c:2235-2274, lookup_head_remote_key): the name of the
    /// remote of the local tracking branch HEAD points to, or null when HEAD
    /// is not a branch or has no upstream (C returns GIT_ENOTFOUND).
    /// </summary>
    private static async Task<string?> LookupHeadRemoteKeyAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        GitReference? head = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is not GitDirectReference dr ||
            !dr.IsBranch)
        {
            // C (submodule.c:2251-2256): HEAD does not refer to a branch.
            return null;
        }

        string upstreamName;
        try
        {
            upstreamName = await repo.BranchUpstreamNameAsync(dr.NameKey.ToUtf8StringStrict(), cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            return null;
        }

        // C's
        // git_branch__remote_name (branch.c:557-618) walks all remotes and
        // matches the upstream ref against each remote's fetch refspec DST,
        // erroring on ambiguity. ResolveRemoteName follows that: a prefix parse
        // of refs/remotes/<remote>/<branch> would return the wrong name for
        // custom dst refspecs and remote names containing '/'.
        const string refsRemotes = "refs/remotes/";
        if (!upstreamName.StartsWith(refsRemotes, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<string> remoteNames = await repo.RemoteListAsync(cancellationToken).ConfigureAwait(false);
        string? match = null;
        foreach (string remoteName in remoteNames)
        {
            GitRemote? remote;
            try
            {
                remote = await Remote.GitRemote.LookupAsync(repo, remoteName, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                continue; // C: a failed git_remote_lookup is skipped
            }

            try
            {
                bool matches = false;
                foreach (GitRefSpec spec in remote.RefSpecs)
                {
                    if (spec.DstMatches(upstreamName))
                    {
                        matches = true;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                if (match is not null)
                {
                    // C (branch.c:598-604): multiple remotes match — GIT_EAMBIGUOUS.
                    throw new GitException(
                        GitErrorCode.Ambiguous,
                        $"reference '{upstreamName}' is ambiguous",
                        GitErrorCategory.Reference);
                }

                match = remoteName;
            }
            finally
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }

        return match;
    }

    /// <summary>
    /// C (fs_path.c, git_fs_path_apply_relative): joinpath + resolve_relative.
    /// </summary>
    private static string ApplyRelative(string baseUrl, string relativeUrl)
        => ResolveRelative(PathHelpers.Join(baseUrl, relativeUrl));

    /// <summary>
    /// C (fs_path.c:172-215, git_fs_path_resolve_relative): resolves <c>.</c>
    /// and <c>..</c> segments without backing over the filesystem root or a
    /// <c>scheme://</c> URL prefix.
    /// </summary>
    private static string ResolveRelative(string path)
    {
        int ceiling = 0;
        if (path.Length > 0 && path[0] == '/')
        {
            ceiling = 1;
        }

        if (ceiling == 0)
        {
            int i = 0;
            while (i < path.Length && char.IsAsciiLetter(path[i]))
            {
                i++;
            }

            if (i + 2 < path.Length && path[i] == ':' && path[i + 1] == '/' && path[i + 2] == '/')
            {
                ceiling = i + 3;
            }
        }

        var segments = new List<string>();
        // count of
        // non-".." segments — C keeps a leading ".." by ADVANCING base
        // (fs_path.c:855-866), so subsequent ".." accumulate instead of
        // popping the previously-kept one. ".." only backs up over real
        // segments (realSegments > 0); leading ".."s are never popped.
        int realSegments = 0;
        int start = ceiling;
        for (int i = ceiling; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == '/')
            {
                if (i > start)
                {
                    string segment = path[start..i];
                    if (segment == ".")
                    {
                        // singleton dot — do nothing
                    }
                    else if (segment == "..")
                    {
                        if (realSegments == 0)
                        {
                            if (ceiling != 0)
                            {
                                throw new GitException(
                                    GitErrorCode.Error,
                                    "cannot strip root component off url",
                                    GitErrorCategory.Invalid);
                            }

                            // C (ceiling == 0): keep "../" as a new base path —
                            // the base advances, so further ".." accumulate
                            // (fs_path.c:855-866).
                            segments.Add("..");
                        }
                        else
                        {
                            // Back up a path segment (fs_path.c:861-864).
                            segments.RemoveAt(segments.Count - 1);
                            realSegments--;
                        }
                    }
                    else
                    {
                        segments.Add(segment);
                        realSegments++;
                    }
                }

                start = i + 1;
            }
        }

        return path[..ceiling] + string.Join('/', segments);
    }

    // ==============================
    // .gitmodules helpers
    // ==============================

    internal static async Task<GitConfiguration?> OpenGitmodulesConfigAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        string gitmodulesPath = PathHelpers.Join(repo.Workdir, ".gitmodules");
        if (!File.Exists(gitmodulesPath))
        {
            return null;
        }

        var config = new GitConfiguration(repo.Context);
        await config.AddFileOnDiskAsync(gitmodulesPath, GitConfigLevel.Local, repo.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return config;
    }

    private Task<GitConfiguration?> OpenGitmodulesConfigAsync(CancellationToken cancellationToken = default)
    {
        return OpenGitmodulesConfigAsync(Owner, cancellationToken);
    }

    private static async Task WriteGitmodulesVarAsync(GitRepository repo, string name, string varName, string value, CancellationToken cancellationToken)
    {
        string gitmodulesPath = PathHelpers.Join(repo.Workdir, ".gitmodules");
        var backend = new FileConfigBackend(gitmodulesPath, null, repo.Context.Dirs);
        await using ConfiguredAsyncDisposable backendDisposable = backend.ConfigureAwait(false);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken).ConfigureAwait(false);

        string key = $"submodule.{name}.{varName}";
        await backend.SetAsync(key, value, cancellationToken).ConfigureAwait(false);

        // C has no
        // persistent submodule cache, so every lookup re-reads .gitmodules —
        // a Set* followed by a lookup must see the new value, so the
        // per-repo cache is invalidated.
        await InvalidateSubmoduleCacheAsync(repo, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteGitmodulesVarAsync(GitRepository repo, string name, string varName, CancellationToken cancellationToken)
    {
        string gitmodulesPath = PathHelpers.Join(repo.Workdir, ".gitmodules");
        if (!File.Exists(gitmodulesPath))
        {
            return;
        }

        var backend = new FileConfigBackend(gitmodulesPath, null, repo.Context.Dirs);
        await using ConfiguredAsyncDisposable backendDisposable = backend.ConfigureAwait(false);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken).ConfigureAwait(false);

        string key = $"submodule.{name}.{varName}";
        await backend.DeleteKeyAsync(key, cancellationToken).ConfigureAwait(false);

        // see WriteGitmodulesVarAsync.
        await InvalidateSubmoduleCacheAsync(repo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// invalidates the per-repo submodule cache after a .gitmodules
    /// mutation so the next lookup/status re-reads the file (C has no
    /// persistent cache).
    /// </summary>
    private static async Task InvalidateSubmoduleCacheAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        SubmoduleCache cache = repo.GetOrInitSubmoduleCache();
        await cache.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Add / Clone / Update
    // ==============================

    /// <summary>
    /// Sets up a new submodule: writes .gitmodules, creates the submodule
    /// repo directory, and initializes it. Matches <c>git_submodule_add_setup</c>
    /// (submodule.c:819-923).
    /// </summary>
    /// <param name="repo">The parent repository.</param>
    /// <param name="url">The submodule URL.</param>
    /// <param name="path">The path within the parent workdir.</param>
    /// <param name="useGitlink">If true (default), use a gitlink (.git/modules).</param>
    /// <returns>The newly created submodule.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<GitSubmodule> AddSetupAsync(GitRepository repo, string url, string path, bool useGitlink = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(path);

        // C (submodule.c:837-845): reject re-adding an existing submodule.
        // Lookup now throws ENOTFOUND/EEXISTS for a miss — C clears those
        // errors and proceeds (a workdir-only repo at the path is reused).
        GitSubmodule? existing = null;
        try
        {
            existing = await LookupAsync(repo, path, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code is GitErrorCode.NotFound or GitErrorCode.Exists)
        {
            // C (submodule.c:839-840): git_error_clear() — proceed.
        }

        if (existing is not null)
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"attempt to add submodule '{path}' that already exists",
                GitErrorCategory.Submodule);
        }

        // C (submodule.c:849-856): normalize the path (strip a workdir
        // prefix) and reject absolute paths ("submodule path must be a
        // relative path"). C does NOT reject ".." components here.
        string normalizedPath = path;
        if (repo.Workdir is not null && normalizedPath.StartsWith(repo.Workdir, StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[repo.Workdir.Length..];
        }

        if (PathHelpers.IsAbsolute(normalizedPath))
        {
            throw new GitException(
                GitErrorCode.Error,
                "submodule path must be a relative path",
                GitErrorCategory.Submodule);
        }

        // C (submodule.c:858-864, is_path_occupied): the path must not
        // collide with an index file or directory.
        GitIndex parentIndex = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (parentIndex.Find(normalizedPath) != -1)
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"File '{normalizedPath}' already exists in the index",
                GitErrorCategory.Submodule);
        }

        string dirPrefix = normalizedPath.EndsWith('/', StringComparison.Ordinal) ? normalizedPath : normalizedPath + "/";
        if (parentIndex.FindPrefix(dirPrefix) != -1)
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"Directory '{normalizedPath}' already exists in the index",
                GitErrorCategory.Submodule);
        }

        // Write to .gitmodules: submodule.<path>.path and submodule.<path>.url
        // (the RAW url — the resolved url goes to the submodule repo's
        // origin remote below).
        await WriteGitmodulesVarAsync(repo, normalizedPath, "path", normalizedPath, cancellationToken).ConfigureAwait(false);
        await WriteGitmodulesVarAsync(repo, normalizedPath, "url", url, cancellationToken).ConfigureAwait(false);

        // Create the submodule repo in .git/modules/<path>/ (gitlink mode).
        // Matches libgit2's submodule_repo_init (submodule.c:724-770): for
        // gitlink, inits a NON-bare repo at <repo>/.git/modules/<path> with
        // NO_DOTGIT_DIR + RELATIVE_GITLINK + a workdir at <workdir>/<path>,
        // then writes the <workdir>/<path>/.git gitdir-link file. The gitlink
        // repo must be NON-bare: a bare modules dir would make a subsequent
        // CloneAsync open a bare repo and the clone's checkout step would write
        // no working tree (libgit2's non-bare gitlink repo lets checkout
        // populate the workdir).
        // C resolves the url and passes it as initopt.origin_url
        // (submodule.c:896-900) — the new submodule repo's .git/config
        // carries [remote "origin"].
        string resolvedUrl = await ResolveUrlAsync(repo, url, cancellationToken).ConfigureAwait(false);

        // C (submodule.c:893-902): only init a fresh repo when no repo
        // already exists at the path.
        string smWorkPath = PathHelpers.Join(repo.Workdir, normalizedPath);
        string smGitPath = PathHelpers.Join(smWorkPath, ".git");
        if (!(File.Exists(smGitPath) || Directory.Exists(smGitPath)))
        {
            if (useGitlink)
            {
                string modulesDir = PathHelpers.Join(PathHelpers.Join(repo.Path, "modules"), normalizedPath);
                // InitExtAsync writes both the gitdir at <repo>/.git/modules/<path>
                // and the <workdir>/<path>/.git gitlink file (pointing at it)
                // because of NO_DOTGIT_DIR + WorkdirPath — the non-bare gitlink
                // init handles both, matching libgit2's submodule_repo_init.
                GitRepository gitlinkRepo = await GitRepository.InitExtAsync(modulesDir, new GitRepositoryInitOptions
                {
                    Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoReinit
                        | GitRepositoryInitFlags.NoDotgitDir | GitRepositoryInitFlags.RelativeGitlink,
                    WorkdirPath = smWorkPath,
                }, repo.Context, cancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable gitlinkRepoDisposable = gitlinkRepo.ConfigureAwait(false);
                await Remote.GitRemote.CreateAsync(gitlinkRepo, "origin", resolvedUrl, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Non-gitlink: init the repo directly in the workdir path.
                Directory.CreateDirectory(smWorkPath);
                GitRepository subRepo = await GitRepository.InitAsync(smWorkPath, isBare: false, repo.Context, cancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable subRepoDisposable = subRepo.ConfigureAwait(false);
                await Remote.GitRemote.CreateAsync(subRepo, "origin", resolvedUrl, cancellationToken).ConfigureAwait(false);
            }
        }

        // The .gitmodules write invalidated the cache — reload so the lookup
        // below sees the new entry (C's lookup is always fresh).
        SubmoduleCache cache = repo.GetOrInitSubmoduleCache();
        await cache.ReloadAsync(cancellationToken).ConfigureAwait(false);

        // C (submodule.c:904-907): look up the submodule and init it.
        GitSubmodule sm = await LookupAsync(repo, normalizedPath, cancellationToken).ConfigureAwait(false);
        await sm.InitAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return sm;
    }

    /// <summary>
    /// Clones the submodule from its URL. Matches <c>git_submodule_clone</c>
    /// (submodule.c:970-1009). Routes through <see cref="GitClone.RunAsync"/>
    /// for network URLs; uses direct object copy for local filesystem paths.
    /// </summary>
    public async Task CloneAsync(SubmoduleUpdateOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SubmoduleUpdateOptions();

        string resolvedUrl = Url
            ?? throw new GitException(
                GitErrorCode.Invalid,
                "submodule has no URL",
                GitErrorCategory.Submodule);

        string smPath = PathHelpers.Join(Owner.Workdir, Path?.ToUtf8String() ?? string.Empty);

        // For network URLs, use Clone.Run which goes through Remote.Fetch.
        // For local paths, use direct object copy (fast path).
        // NOTE: no "already initialized?" guard here — libgit2's
        // git_submodule_clone has none. The Add flow calls AddSetup (which
        // creates the gitlink repo + .git file) then CloneAsync (which fetches
        // into the existing repo via the RepositoryCreate callback); a guard
        // on .git existence would skip the fetch and leave the submodule
        // workdir empty. The Update flow (UpdateAsync) builds its own clone
        // options with a repo-create callback and does not route here.
        if (IsNetworkUrl(resolvedUrl))
        {
            // The submodule was initialized by Init(), so the gitlink repo
            // already exists. Use a custom RepositoryCreate callback that
            // opens the existing repo instead of creating a new one.
            var cloneOpts = new GitCloneOptions
            {
                CheckoutOptions = options.CheckoutOptions,
                FetchOptions = options.FetchOptions,
                RemoteName = "origin",
                RepositoryCreate = async (_, _, ctx, ct) => await OpenInternalAsync(ctx, ct).ConfigureAwait(false)
                    ?? throw new GitException(GitErrorCode.NotFound,
                        "submodule repository not initialized",
                        GitErrorCategory.Submodule),
                RemoteCreate = async (repo, name, url, ct) =>
                {
                    // Look up the existing remote (created by Init), or
                    // create one if it doesn't exist.
                    try
                    {
                        return await GitRemote.LookupAsync(repo, name, ct).ConfigureAwait(false);
                    }
                    catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                    {
                        return await GitRemote.CreateAsync(repo, name, url, ct).ConfigureAwait(false);
                    }
                },
            };

            GitRepository repository = await GitClone.RunForSubmoduleAsync(resolvedUrl, smPath, cloneOpts, Owner.Context, cancellationToken).ConfigureAwait(false);
            await repository.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            // Local path: direct object copy (the fast path).
            await CloneLocalPathAsync(resolvedUrl, smPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns true if the URL is a network URL (goes through the clone
    /// machinery's transport layer). Mirrors C's <c>transport_find_fn</c>
    /// (transport.c:75-103): a known scheme prefix wins, otherwise ANY url
    /// containing ':' is SSH
    /// (scp-style), everything else is a local filesystem path.
    /// </summary>
    private static bool IsNetworkUrl(string url)
    {
        return url.Contains("://", StringComparison.Ordinal)
            || url.Contains(':', StringComparison.Ordinal);
    }

    /// <summary>
    /// Clones a local repository by directly copying the objects directory
    /// and refs. This is the fast path for local filesystem paths, avoiding
    /// the overhead of transport setup.
    /// </summary>
    private async Task CloneLocalPathAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        GitRepository source = await GitRepository.OpenAsync(sourcePath, Owner.Context, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sourceDisposable = source.ConfigureAwait(false);

        // Determine the target gitdir (gitlink mode).
        string? workdir = Owner.Workdir;
        Debug.Assert(workdir is not null, "Owner has a workdir when cloning into a submodule");
        string relativePath = PathHelpers.MakeRelative(targetPath, workdir);
        string targetGitdir = PathHelpers.Join(
            PathHelpers.Join(Owner.Path, "modules"), relativePath);

        Directory.CreateDirectory(targetGitdir);

        // Copy objects (loose + packed).
        CopyObjectsDir(source, targetGitdir, cancellationToken);

        // Copy refs.
        CopyRefsDir(source, targetGitdir, cancellationToken);

        // Write HEAD.
        string sourceHead = (await AsyncFileIO.ReadAllTextWithNoBomAsync(PathHelpers.Join(source.Path, "HEAD"), cancellationToken).ConfigureAwait(false)).Trim();
        await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(targetGitdir, "HEAD"), $"{sourceHead}\n", cancellationToken).ConfigureAwait(false);

        // Write config:
        // C's git_submodule_clone clones INTO the existing repo created by
        // submodule_repo_init, which already recorded the origin remote with
        // the resolved URL (submodule.c:740/900) — rewriting the config
        // destroyed that remote (and the relative gitlink). Only a fresh
        // gitdir (no existing config) gets a new one.
        string targetConfig = PathHelpers.Join(targetGitdir, "config");
        if (!File.Exists(targetConfig))
        {
            await AsyncFileIO.WriteAllTextWithNoBomAsync(targetConfig,
                "[core]\n" +
                "\trepositoryformatversion = 0\n" +
                "\tfilemode = true\n" +
                "\tbare = false\n" +
                "\tlogallrefupdates = true\n" +
                $"\tworktree = {targetPath}\n", cancellationToken).ConfigureAwait(false);
        }

        // Write the .git file in the workdir — preserve the existing gitlink
        // file (e.g. the RELATIVE_GITLINK written by InitExtAsync); only a
        // fresh workdir gets an absolute gitdir: pointer.
        Directory.CreateDirectory(targetPath);
        string gitlinkPath = PathHelpers.Join(targetPath, ".git");
        if (!File.Exists(gitlinkPath) && !Directory.Exists(gitlinkPath))
        {
            await AsyncFileIO.WriteAllTextWithNoBomAsync(gitlinkPath, $"gitdir: {targetGitdir}\n", cancellationToken).ConfigureAwait(false);
        }

        // Checkout HEAD in the target.
        GitRepository targetRepo = await GitRepository.OpenAsync(targetPath, Owner.Context, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable targetRepoDisposable = targetRepo.ConfigureAwait(false);
        await targetRepo.CheckoutHeadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void CopyObjectsDir(GitRepository source, string targetGitdir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceObjectsDir = PathHelpers.Join(source.Path, "objects");
        string targetObjectsDir = PathHelpers.Join(targetGitdir, "objects");

        if (!Directory.Exists(sourceObjectsDir))
        {
            return;
        }

        CopyDirectoryRecursive(sourceObjectsDir, targetObjectsDir, cancellationToken);
    }

    private static void CopyRefsDir(GitRepository source, string targetGitdir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceRefsDir = PathHelpers.Join(source.Path, "refs");
        string targetRefsDir = PathHelpers.Join(targetGitdir, "refs");

        if (Directory.Exists(sourceRefsDir))
        {
            CopyDirectoryRecursive(sourceRefsDir, targetRefsDir, cancellationToken);
        }

        // Copy packed-refs if present.
        string packedRefsPath = PathHelpers.Join(source.Path, "packed-refs");
        if (File.Exists(packedRefsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(packedRefsPath, PathHelpers.Join(targetGitdir, "packed-refs"), overwrite: true);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = System.IO.Path.GetFileName(file);
            File.Copy(file, PathHelpers.Join(targetDir, fileName), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dirName = System.IO.Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, PathHelpers.Join(targetDir, dirName), cancellationToken);
        }
    }

    /// <summary>
    /// Adds the submodule gitlink to the parent's index. Matches
    /// <c>git_submodule_add_to_index</c> (submodule.c:1025-1099).
    /// </summary>
    /// <param name="writeIndex">If true, write the index to disk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddToIndexAsync(bool writeIndex = true, CancellationToken cancellationToken = default)
    {
        // Get the submodule's HEAD OID.
        GitRepository smRepo = await OpenInternalAsync(Owner.Context, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "submodule repository not open",
                GitErrorCategory.Submodule);

        try
        {
            // C
            // (submodule.c:1051-1054) fails when the submodule working
            // directory is missing.
            string smWorkPath = PathHelpers.Join(Owner.Workdir, Path?.ToUtf8String() ?? string.Empty);
            if (!Directory.Exists(smWorkPath))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "cannot add submodule without working directory",
                    GitErrorCategory.Submodule);
            }

            GitReference? head = await smRepo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (head is GitDirectReference dr)
            {
                WdId = dr.Target;
            }

            // C (submodule.c:1059-1064) fails with "cannot add
            // submodule without HEAD to index" when git_submodule_open could
            // not resolve HEAD (unborn branch / missing HEAD), instead of
            // adding a zero-OID gitlink into the parent index.
            if (WdId.IsZero || head is not GitDirectReference)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "cannot add submodule without HEAD to index",
                    GitErrorCategory.Submodule);
            }

            // C (submodule.c:1065-1068): the gitlink OID must be a commit.
            Commit? headCommit = await smRepo.Objects.LookupAsync<Commit>(WdId, cancellationToken).ConfigureAwait(false);
            if (headCommit is null)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "cannot add submodule without HEAD to index",
                    GitErrorCategory.Submodule);
            }

            // Add the gitlink entry to the parent index.
            GitPath? path = Path;
            Debug.Assert(path is not null, "submodule path is set");
            var entry = new Index.GitIndexEntry(path.Value, WdId, GitFileMode.GitLink);
            GitIndex parentIndex = await Owner.GetIndexAsync(cancellationToken).ConfigureAwait(false);
            parentIndex.Add(entry);

            if (writeIndex)
            {
                await parentIndex.WriteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await smRepo.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finalizes the submodule add: stages .gitmodules and the gitlink.
    /// Matches <c>git_submodule_add_finalize</c> (submodule.c:1011-1023).
    /// </summary>
    public async Task AddFinalizeAsync(CancellationToken cancellationToken = default)
    {
        // Add .gitmodules to the parent index.
        GitIndex index = await Owner.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        await index.AddByPathAsync(".gitmodules", cancellationToken).ConfigureAwait(false);
        await index.WriteAsync(cancellationToken).ConfigureAwait(false);

        // Add the gitlink entry.
        await AddToIndexAsync(true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the submodule: clone if uninitialized, then checkout the
    /// index commit OID. Matches <c>git_submodule_update</c>
    /// (submodule.c:1365-1496). The clone uses the URL read back from
    /// <c>.git/config</c> (the resolved URL, submodule.c:1405-1432), and the
    /// initialized-workdir branch fetches from the default remote when the
    /// target commit is missing and <c>allow_fetch</c> is set
    /// (submodule.c:1467-1475).
    /// </summary>
    /// <param name="init">If true, init the submodule first (copy URL from .gitmodules to .git/config).</param>
    /// <param name="options">Update options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateAsync(bool init = false, SubmoduleUpdateOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SubmoduleUpdateOptions();

        SubmoduleStatus status = await ComputeStatusAsync(SubmoduleIgnore.Unspecified, cancellationToken).ConfigureAwait(false);

        // If only in config (never added), skip.
        if ((status & ~InFlagsMask) == 0 &&
            (status & SubmoduleStatus.InConfig) != 0 &&
            (status & (SubmoduleStatus.InHead | SubmoduleStatus.InIndex | SubmoduleStatus.InWd)) == 0)
        {
            return;
        }

        if ((status & SubmoduleStatus.WdUninitialized) != 0)
        {
            // C (submodule.c:1405-1432): the clone URL comes from
            // .git/config ("submodule.NAME.url") — after init that is the
            // RESOLVED url, so a relative .gitmodules url clones from the
            // correct location.
            string? configUrl = await Owner.Config.GetStringAsync($"submodule.{Name}.url", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(configUrl))
            {
                if (!init)
                {
                    // C (submodule.c:1418): exact message, no name suffix.
                    throw new GitException(
                        GitErrorCode.Error,
                        "submodule is not initialized",
                        GitErrorCategory.Submodule);
                }

                await InitAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                configUrl = await Owner.Config.GetStringAsync($"submodule.{Name}.url", cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(configUrl))
                {
                    // C (submodule.c:1430-1432): the post-init config read
                    // still missing the url — GIT_ENOTFOUND propagates.
                    throw new GitException(
                        GitErrorCode.NotFound,
                        "submodule is not initialized",
                        GitErrorCategory.Submodule);
                }
            }

            // libgit2's git_submodule_update uses git_submodule_update_repo_init_cb
            // (→ submodule_repo_create) as the repository-creation callback for
            // the clone, NOT git_submodule_open. submodule_repo_create inits a
            // fresh gitlink repo at <repo>/.git/modules/<path> with a worktree
            // at <workdir>/<path> — the layout `git submodule update --init`
            // produces on a freshly cloned superproject (where neither the
            // modules dir nor the .git file exist yet). CloneAsync's default
            // RepositoryCreate (OpenInternalAsync) assumes the gitlink repo
            // was already created by AddSetupAsync, which is the Add flow,
            // not the Update-from-a-clone flow. Mirror libgit2 by building a
            // GitCloneOptions whose RepositoryCreate calls SubmoduleRepoCreate.
            string smWorkPath = PathHelpers.Join(Owner.Workdir, Path?.ToUtf8String() ?? string.Empty);

            var cloneOpts = new GitCloneOptions
            {
                // libgit2 disables checkout inside the clone and checks out the
                // specific index commit manually afterwards (so the detached
                // HEAD lands on the gitlink oid, not the remote's HEAD).
                CheckoutOptions = new Checkout.GitCheckoutOptions { Strategy = Checkout.GitCheckoutStrategy.None },
                FetchOptions = options.FetchOptions,
                RemoteName = "origin",
                RepositoryCreate = async (_, _, ctx, ct) => await SubmoduleRepoCreateAsync(ctx, ct).ConfigureAwait(false),
                RemoteCreate = async (repo, name, url, ct) =>
                {
                    try
                    {
                        return await GitRemote.LookupAsync(repo, name, ct).ConfigureAwait(false);
                    }
                    catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                    {
                        return await GitRemote.CreateAsync(repo, name, url, ct).ConfigureAwait(false);
                    }
                },
            };

            GitRepository cloneRepo = await GitClone.RunForSubmoduleAsync(configUrl, smWorkPath, cloneOpts, Owner.Context, cancellationToken).ConfigureAwait(false);
            await cloneRepo.DisposeAsync().ConfigureAwait(false);

            // C (submodule.c:1446-1449): after the clone, detach HEAD at the
            // index commit OID and check it out.
            await CheckoutIndexOidAsync(options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // C (submodule.c:1450-1486): the workdir is initialized — look up
            // the target commit in the submodule (fetching when missing and
            // allow_fetch), then check it out and detach HEAD at the gitlink.
            if (IndexId.IsZero)
            {
                // C (submodule.c:1461-1465).
                throw new GitException(
                    GitErrorCode.Error,
                    "could not get ID of submodule in index",
                    GitErrorCategory.Submodule);
            }

            GitRepository? smRepo = await OpenInternalAsync(Owner.Context, cancellationToken).ConfigureAwait(false);
            if (smRepo is null)
            {
                // C's git_submodule_open fails with GIT_ENOTFOUND when
                // the workdir has no .git entry (submodule.c:1458-1459) —
                // the update must not silently succeed.
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"submodule '{Name}' cannot be opened",
                    GitErrorCategory.Submodule);
            }

            await using ConfiguredAsyncDisposable smRepoDisposable = smRepo.ConfigureAwait(false);

            // C (submodule.c:1467-1475): git_object_lookup — when the
            // target commit is missing and allow_fetch is set, fetch from
            // the default remote and retry.
            Commit? target = await smRepo.Objects.LookupAsync<Commit>(IndexId, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                if (!options.AllowFetch)
                {
                    throw new GitException(
                        GitErrorCode.NotFound,
                        $"object not found - no match for id ({IndexId})",
                        GitErrorCategory.Object);
                }

                GitRemote? remote = await LookupDefaultRemoteAsync(smRepo, cancellationToken).ConfigureAwait(false);
                if (remote is null)
                {
                    throw new GitException(
                        GitErrorCode.NotFound,
                        "cannot get default remote for submodule - no local tracking branch for HEAD and origin does not exist",
                        GitErrorCategory.Submodule);
                }

                await using ConfiguredAsyncDisposable remoteDisposable = remote.ConfigureAwait(false);
                await remote.FetchAsync(null, options.FetchOptions, null, cancellationToken).ConfigureAwait(false);

                target = await smRepo.Objects.LookupAsync<Commit>(IndexId, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitException(
                        GitErrorCode.NotFound,
                        $"object not found - no match for id ({IndexId})",
                        GitErrorCategory.Object);
            }

            // C (submodule.c:1477-1479): checkout the target commit and
            // detach HEAD at the gitlink oid.
            await smRepo.SetHeadDetachedAsync(IndexId, cancellationToken).ConfigureAwait(false);
            await smRepo.CheckoutHeadAsync(options.CheckoutOptions ?? new Checkout.GitCheckoutOptions(), cancellationToken).ConfigureAwait(false);
        }

        // The clone+checkout mutated the workdir (created the .git file,
        // checked out files, advanced the submodule HEAD). The cached WdId /
        // InWd flag are now stale — reload so a subsequent StatusAsync sees
        // the post-update state. Parity with libgit2's git_submodule_update,
        // which clears the IN_WD / WD_OID_VALID / WD_SCANNED flags before
        // returning so the next status query re-reads the workdir.
        await ReloadAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches the submodule HEAD at <see cref="IndexId"/> and checks it out.
    /// </summary>
    private async Task CheckoutIndexOidAsync(SubmoduleUpdateOptions options, CancellationToken cancellationToken)
    {
        if (IndexId.IsZero)
        {
            return;
        }

        GitRepository? smRepo = await OpenInternalAsync(Owner.Context, cancellationToken).ConfigureAwait(false);
        if (smRepo is null)
        {
            // C's git_submodule_open fails with GIT_ENOTFOUND when the
            // workdir has no .git entry (submodule.c:1458-1459) — the
            // checkout must not silently succeed.
            throw new GitException(
                GitErrorCode.NotFound,
                $"submodule '{Name}' cannot be opened",
                GitErrorCategory.Submodule);
        }

        await using ConfiguredAsyncDisposable smRepoDisposable = smRepo.ConfigureAwait(false);

        // Detach HEAD at the index commit OID.
        await smRepo.SetHeadDetachedAsync(IndexId, cancellationToken).ConfigureAwait(false);

        // Checkout HEAD.
        await smRepo.CheckoutHeadAsync(options.CheckoutOptions ?? new Checkout.GitCheckoutOptions(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the submodule's gitlink repository, mirroring libgit2's
    /// <c>submodule_repo_create</c> (submodule.c:1286-1335). Inits a non-bare
    /// repo at <c>&lt;repo&gt;/.git/modules/&lt;path&gt;</c> with its worktree
    /// at <c>&lt;workdir&gt;/&lt;path&gt;</c> (no <c>.git</c> subdir — the
    /// gitdir IS <c>.git/modules/&lt;path&gt;</c>, pointed at by a <c>.git</c>
    /// file in the worktree). Used by <see cref="UpdateAsync"/> as the
    /// <see cref="GitCloneOptions.RepositoryCreate"/> callback so a
    /// <c>submodule update --init</c> on a freshly cloned superproject builds
    /// the gitlink layout from scratch (the Add flow's
    /// <see cref="AddSetupAsync"/> creates it inline instead).
    /// </summary>
    private async Task<GitRepository> SubmoduleRepoCreateAsync(GitContext context, CancellationToken cancellationToken)
    {
        string smWorkPath = PathHelpers.Join(Owner.Workdir, Path?.ToUtf8String() ?? string.Empty);
        string modulesDir = PathHelpers.Join(PathHelpers.Join(Owner.Path, "modules"), Path?.ToUtf8String() ?? string.Empty);

        // Init the gitlink repo (gitdir at .git/modules/<path>, worktree at
        // <workdir>/<path>). Matches submodule_repo_create's flags:
        // MKPATH | NO_REINIT | NO_DOTGIT_DIR | RELATIVE_GITLINK.
        GitRepository smRepo = await GitRepository.InitExtAsync(modulesDir, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoReinit
                | GitRepositoryInitFlags.NoDotgitDir | GitRepositoryInitFlags.RelativeGitlink,
            WorkdirPath = smWorkPath,
        }, context, cancellationToken).ConfigureAwait(false);

        // InitExtAsync writes both the gitdir at .git/modules/<path> and the
        // <workdir>/<path>/.git gitlink file (NO_DOTGIT_DIR + WorkdirPath),
        // so OpenInternalAsync (which looks for <workdir>/<path>/.git) finds
        // the gitlink repo. No manual .git write needed here.

        return smRepo;
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

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Submodule;

/// <summary>
/// Per-repo submodule cache. Loads submodule definitions from .gitmodules,
/// the index (gitlink entries), and the HEAD tree (gitlink entries).
/// Managed port of <c>git_submodule__map</c> (submodule.c:591-660).
/// </summary>
internal sealed class SubmoduleCache
{
    private readonly GitRepository _repo;

    /// <summary> Submodule NAME bytes → submodule. C's <c>git_submodule_cache</c> is a <c>char *</c>-keyed hashmap keyed by <c>sm->name</c> (submodule.c:268,
    /// 478) — the cache is keyed on the raw name bytes via <see cref="ConfigNameKey"/> so non-UTF-8 names round-trip byte-exact. </summary>
    private readonly Dictionary<ConfigNameKey, GitSubmodule> _submodules = [];

    /// <summary>.gitmodules path → submodule NAME map (C's namemap, submodule.c:513-522). Index/HEAD gitlinks are keyed by path; the cache entries are keyed by
    /// name. byte-keyed — the path is a <see cref="GitPath"/> (byte-faithful) and the name is the raw subsection bytes. </summary>
    private readonly Dictionary<GitPath, ReadOnlyMemory<byte>> _pathToName = [];
    private bool _loaded;

    /// <summary>
    /// True once <see cref="EnsureLoadedAsync"/> / <see cref="LoadAsync"/> has
    /// populated the cache from .gitmodules + index + HEAD. Used by
    /// <see cref="GitRepository.ReloadSubmoduleCacheIfLoadedAsync"/> to skip the
    /// reload when no lookup has run yet (avoid creating a cache eagerly).
    /// </summary>
    internal bool IsLoaded => _loaded;

    internal SubmoduleCache(GitRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Ensures the cache has been loaded from .gitmodules + index + HEAD.
    /// </summary>
    internal ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            // Cache hit — the majority case.
            return ValueTask.CompletedTask;
        }

        return new ValueTask(LoadSlowAsync(cancellationToken));
    }

    /// <summary>Slow path of <see cref="EnsureLoadedAsync"/>: loads the cache from disk.</summary>
    private async Task LoadSlowAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads the cache from disk.
    /// </summary>
    internal async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _submodules.Clear();
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads a single submodule's state from disk. Matches
    /// <c>git_submodule_reload</c> (submodule.c:1724-1758): re-reads
    /// <c>.gitmodules</c> config, clears the workdir flags, and re-runs the
    /// shallow workdir scan so <see cref="GitSubmodule.WdId"/> /
    /// <see cref="SubmoduleStatus.InWd"/> reflect the post-mutation state
    /// (e.g. after <see cref="GitSubmodule.UpdateAsync"/> cloned + checked
    /// out the submodule), so <see cref="GitSubmodule.StatusAsync"/> cannot
    /// keep reporting <see cref="SubmoduleStatus.WdUninitialized"/> from
    /// stale wd state after a successful update.
    /// </summary>
    internal async Task ReloadSubmoduleAsync(GitSubmodule sm, CancellationToken cancellationToken = default)
    {
        await ReadConfigAsync(sm, cancellationToken).ConfigureAwait(false);

        // Clear the workdir-derived flags + oid before rescanning, matching
        // libgit2's `sm->flags &= ~(IN_WD | WD_OID_VALID | WD_FLAGS)`.
        sm.StatusFlags &= ~(SubmoduleStatus.InWd
            | SubmoduleStatus.WdUninitialized
            | SubmoduleStatus.WdAdded
            | SubmoduleStatus.WdDeleted
            | SubmoduleStatus.WdModified
            | SubmoduleStatus.WdIndexModified
            | SubmoduleStatus.WdWdModified
            | SubmoduleStatus.WdUntracked);
        sm.WdId = GitOid.Empty;
        sm._wdScanned = false;
        await LoadFromWorkdirLiteAsync(sm, cancellationToken).ConfigureAwait(false);

        // C (submodule.c:1753-1756): reload also re-runs
        // submodule_update_index and submodule_update_head — the IN_INDEX /
        // IN_HEAD location bits and the index/HEAD OIDs must reflect the
        // current index and HEAD tree, not whatever the initial (possibly
        // stale) cache load produced.
        await RefreshSubmoduleOidsAsync(sm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes a single submodule's index/HEAD OIDs from the current index
    /// and HEAD tree. Matches <c>submodule_update_index</c> /
    /// <c>submodule_update_head</c> (submodule.c:1670-1715), which C runs on
    /// every status call when no persistent cache was requested
    /// (submodule.c:1801-1809).
    /// </summary>
    internal async ValueTask RefreshSubmoduleOidsAsync(GitSubmodule sm, CancellationToken cancellationToken)
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        GitIndexEntry? ie = sm.Path is { } p && !p.IsEmpty
            ? index.EntryByPath(p)
            : null;
        if (ie is { Mode: GitFileMode.GitLink })
        {
            sm.IndexId = ie.Value.Id;
            sm.StatusFlags |= SubmoduleStatus.InIndex;
        }
        else
        {
            sm.IndexId = GitOid.Empty;
            sm.StatusFlags &= ~SubmoduleStatus.InIndex;
        }

        GitReference? head = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is GitDirectReference dr)
        {
            Commit? commit = await _repo.Objects.LookupAsync<Commit>(dr.Target, cancellationToken).ConfigureAwait(false);
            GitTree? tree = commit is null ? null : await _repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
            if (tree is not null && sm.Path is { } smPath && !smPath.IsEmpty)
            {
                GitTreeEntry? te = await tree.EntryByPathAsync(smPath, cancellationToken).ConfigureAwait(false);
                if (te is { IsGitLink: true })
                {
                    sm.HeadId = te.Value.Id;
                    sm.StatusFlags |= SubmoduleStatus.InHead;
                }
                else
                {
                    sm.HeadId = GitOid.Empty;
                    sm.StatusFlags &= ~SubmoduleStatus.InHead;
                }
            }
        }
    }

    /// <summary>
    /// Looks up a submodule by name (UTF-8 convenience tier — the byte-parity
    /// surface is <see cref="LookupAsync(GitPath, CancellationToken)"/>).
    /// </summary>
    internal ValueTask<GitSubmodule?> LookupAsync(string name, CancellationToken cancellationToken = default)
        => LookupAsync(GitPath.FromUtf8String(name), cancellationToken);

    /// <summary> Looks up a submodule by name/path bytes. Matches <c>git_submodule__lookup_with_cache</c> (submodule.c:316-433): the cache is probed by NAME
    /// bytes first, then the path→name map (C's find_by_path, submodule.c:357-396) resolves a path≠name entry. byte-domain end-to-end — non-UTF-8 names/paths
    /// round-trip byte-exact. </summary>
    internal ValueTask<GitSubmodule?> LookupAsync(GitPath name, CancellationToken cancellationToken = default)
    {
        // Loaded-cache dict lookup is the majority path; the load is deferred
        // to the slow path only on the first access.
        if (_loaded)
        {
            if (_submodules.TryGetValue(ConfigNameKey.From(name.ToUtf8Bytes()), out GitSubmodule? sm))
            {
                return ValueTask.FromResult<GitSubmodule?>(sm);
            }

            // C (submodule.c:357-396): when the name is not a configured
            // submodule, git_submodule_lookup searches .gitmodules for a
            // `submodule.<name>.path == <name>` entry (find_by_path) and reloads
            // under the found NAME — so looking up by PATH resolves the
            // .gitmodules entry whose path differs from its name.
            if (_pathToName.TryGetValue(name, out ReadOnlyMemory<byte> mapped) &&
                _submodules.TryGetValue(ConfigNameKey.From(mapped), out GitSubmodule? sm2))
            {
                return ValueTask.FromResult<GitSubmodule?>(sm2);
            }

            return ValueTask.FromResult<GitSubmodule?>(null);
        }

        return new ValueTask<GitSubmodule?>(LookupSlowAsync(name, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LookupAsync(GitPath, CancellationToken)"/>: ensures the cache is loaded first (disk IO).</summary>
    private async Task<GitSubmodule?> LookupSlowAsync(GitPath name, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_submodules.TryGetValue(ConfigNameKey.From(name.ToUtf8Bytes()), out GitSubmodule? sm))
        {
            return sm;
        }

        // C (submodule.c:357-396): when the name is not a configured
        // submodule, git_submodule_lookup searches .gitmodules for a
        // `submodule.<name>.path == <name>` entry (find_by_path) and reloads
        // under the found NAME — so looking up by PATH resolves the
        // .gitmodules entry whose path differs from its name.
        if (_pathToName.TryGetValue(name, out ReadOnlyMemory<byte> mapped) &&
            _submodules.TryGetValue(ConfigNameKey.From(mapped), out GitSubmodule? sm2))
        {
            return sm2;
        }

        return null;
    }

    internal ValueTask<IReadOnlyCollection<GitSubmodule>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            // Cache hit — the majority case.
            return ValueTask.FromResult<IReadOnlyCollection<GitSubmodule>>(
                [.. _submodules.Values.OrderBy(sm => sm.NameBytes, ByteOrdinalComparer.Instance)]);
        }

        return new ValueTask<IReadOnlyCollection<GitSubmodule>>(EnumerateSlowAsync(cancellationToken));
    }

    /// <summary>Slow path of <see cref="EnumerateAsync"/>: ensures the cache is loaded first (disk IO).</summary>
    private async Task<IReadOnlyCollection<GitSubmodule>> EnumerateSlowAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        // C (submodule.c:685-707): the foreach snapshot is sorted ascending by name (git_vector_uniq → sort with submodule_cmp = strcmp) — Dictionary order is
        // hash-bucket order. byte-ordinal sort over the raw name bytes (C's strcmp).
        return [.. _submodules.Values.OrderBy(sm => sm.NameBytes, ByteOrdinalComparer.Instance)];
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        // 1. Load from .gitmodules.
        await LoadFromGitmodulesAsync(cancellationToken).ConfigureAwait(false);

        // 2. Load from index (gitlink entries).
        await LoadFromIndexAsync(cancellationToken).ConfigureAwait(false);

        // 3. Load from HEAD tree (gitlink entries).
        await LoadFromHeadAsync(cancellationToken).ConfigureAwait(false);

        // 4. Shallow scan of workdir.
        await LoadFromWorkdirLiteAsync(cancellationToken).ConfigureAwait(false);

        // 5. Compute status flags.
        foreach (GitSubmodule sm in _submodules.Values)
        {
            UpdateStatusFlags(sm);
        }

        // The load completed; mark the cache loaded. On a config error (e.g.
        // an invalid submodule update value — C's submodule_read_config
        // failure) _loaded stays false so the next call re-attempts the load
        // and re-raises, matching C's per-call lookup failure.
        _loaded = true;
    }

    /// <summary>
    /// Loads submodule definitions from .gitmodules. Matches
    /// <c>submodule_load_each</c> (submodule.c:2098-2154).
    /// </summary>
    private async Task LoadFromGitmodulesAsync(CancellationToken cancellationToken)
    {
        GitConfiguration? gitmodules = await GitSubmodule.OpenGitmodulesConfigAsync(_repo, cancellationToken).ConfigureAwait(false);
        if (gitmodules is null)
        {
            return;
        }

        await using (gitmodules.ConfigureAwait(false))
        {
            // Enumerate all submodule.<name>.* keys and extract names.
            var names = new HashSet<ReadOnlyMemory<byte>>(ReadOnlyMemoryByteComparer.Ordinal);
            await foreach (GitConfigEntry entry in gitmodules.EnumerateAsync("submodule.*", cancellationToken).ConfigureAwait(false))
            {
                // C (submodule.c:2110-2118, submodule_load_each): the name is the text between the FIRST and LAST dots after the "submodule." prefix —
                // `submodule.my.name.path` names the submodule "my.name" (dotted subsection names must round-trip). byte-domain extraction on the raw name
                // bytes.
                ReadOnlySpan<byte> key = entry.NameBytes.Span;
                if (!key.StartsWith("submodule."u8))
                {
                    continue;
                }

                ReadOnlySpan<byte> namestart = key["submodule.".Length..];
                int lastDot = namestart.LastIndexOf((byte)'.');
                if (lastDot <= 0)
                {
                    continue;
                }

                names.Add(entry.NameBytes.Slice("submodule.".Length, lastDot));
            }

            foreach (ReadOnlyMemory<byte> name in names)
            {
                // C
                // skips names failing git_submodule_name_is_valid
                // (GIT_FS_PATH_REJECT_FILESYSTEM_DEFAULTS, submodule.c:2121-
                // 2125 + name_is_valid at 435-455) — a [submodule ".."]
                // entry must never materialize a submodule whose path
                // resolves outside the workdir.
                if (!await GitPathValidator.IsValidAsync(
                        GitPath.FromUtf8Bytes(name), PathRejectPresets.FilesystemDefaults,
                        _repo, 0, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                GitSubmodule sm = GetOrCreate(name);
                await ReadConfigAsync(sm, cancellationToken).ConfigureAwait(false);
                sm.StatusFlags |= SubmoduleStatus.InConfig;

                // Build the path→name map (namemap, submodule.c:513-522).
                // C's load_submodule_names fails the whole
                // map load with "duplicated submodule path '%s'" when a
                // second submodule declares the same path (submodule.c:227-
                // 232) — the map load fails instead of silently picking a
                // nondeterministic last winner.
                if (sm.Path is { } p && !p.IsEmpty)
                {
                    if (_pathToName.TryGetValue(p, out ReadOnlyMemory<byte> existing) && !existing.Span.SequenceEqual(name.Span))
                    {
                        throw new GitException(
                            GitErrorCode.Error,
                            $"duplicated submodule path '{p.ToUtf8String()}'",
                            GitErrorCategory.Submodule);
                    }

                    _pathToName[p] = name;
                }
            }
        }
    }

    /// <summary>
    /// Reads per-submodule config keys. Matches <c>submodule_read_config</c>
    /// (submodule.c:2011-2096).
    /// </summary>
    private async Task ReadConfigAsync(GitSubmodule sm, CancellationToken cancellationToken)
    {
        GitConfiguration? gitmodules = await GitSubmodule.OpenGitmodulesConfigAsync(_repo, cancellationToken).ConfigureAwait(false);
        if (gitmodules is null)
        {
            return;
        }

        await using (gitmodules.ConfigureAwait(false))
        {
            // the config keys are built from the raw name bytes (C's git_str_printf("submodule.%.*s.path", …, sm->name), submodule.c:2022-2096) — a non-UTF-8
            // submodule name builds the same key bytes C would.
            ReadOnlyMemory<byte> nameBytes = sm.NameBytes;
            byte[]? path = await gitmodules.GetBytesAsync(ConfigKeyName.BuildNameBytes("submodule"u8, nameBytes.Span, "path"u8), cancellationToken).ConfigureAwait(false);
            // C (submodule.c:2022-2037): a `-`-prefixed path value is a
            // command-line option (e.g. `-f`) and is ignored.
            if (path is not null && path.Length > 0 && !LooksLikeCommandLineOption(path))
            {
                sm.Path = GitPath.FromUtf8Bytes(path);
            }

            byte[]? url = await gitmodules.GetBytesAsync(ConfigKeyName.BuildNameBytes("submodule"u8, nameBytes.Span, "url"u8), cancellationToken).ConfigureAwait(false);
            // C (submodule.c:2042-2051): a `-`-prefixed url value is ignored.
            if (url is not null && url.Length > 0 && !LooksLikeCommandLineOption(url))
            {
                sm.Url = Encoding.UTF8.GetString(url);
            }

            byte[]? branch = await gitmodules.GetBytesAsync(ConfigKeyName.BuildNameBytes("submodule"u8, nameBytes.Span, "branch"u8), cancellationToken).ConfigureAwait(false);
            if (branch is not null && branch.Length > 0)
            {
                sm.Branch = Encoding.UTF8.GetString(branch);
            }

            byte[]? update = await gitmodules.GetBytesAsync(ConfigKeyName.BuildNameBytes("submodule"u8, nameBytes.Span, "update"u8), cancellationToken).ConfigureAwait(false);
            if (update is not null && update.Length > 0)
            {
                // C (submodule.c:2061-2068): git_submodule_parse_update —
                // invalid values make submodule_read_config FAIL, which
                // propagates out of lookup/foreach (C-verified: lookup fails
                // with "invalid value for submodule 'update' property: 'x'").
                if (!TryParseUpdate(update, out SubmoduleUpdateStrategy updateValue))
                {
                    throw SubmoduleConfigError("update", update);
                }

                sm.UpdateStrategy = updateValue;
            }

            byte[]? ignore = await gitmodules.GetBytesAsync(ConfigKeyName.BuildNameBytes("submodule"u8, nameBytes.Span, "ignore"u8), cancellationToken).ConfigureAwait(false);
            if (ignore is not null && ignore.Length > 0)
            {
                // C (submodule.c:2079-2086): git_submodule_parse_ignore.
                if (!TryParseIgnore(ignore, out SubmoduleIgnore ignoreValue))
                {
                    throw SubmoduleConfigError("ignore", ignore);
                }

                sm.Ignore = ignoreValue;
            }

            byte[]? recurse = await gitmodules.GetBytesAsync(ConfigKeyName.BuildNameBytes("submodule"u8, nameBytes.Span, "fetchRecurseSubmodules"u8), cancellationToken).ConfigureAwait(false);
            if (recurse is not null && recurse.Length > 0)
            {
                // C (submodule.c:2070-2077): submodule_parse_recurse (the
                // error message names the property "recurse").
                if (!TryParseRecurse(recurse, out SubmoduleRecurse recurseValue))
                {
                    throw SubmoduleConfigError("recurse", recurse);
                }

                sm.FetchRecurse = recurseValue;
            }
        }
    }

    /// <summary>
    /// Loads gitlink entries from the index. Matches
    /// <c>submodules_from_index</c> (submodule.c:489-534).
    /// </summary>
    private async ValueTask LoadFromIndexAsync(CancellationToken cancellationToken)
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        foreach (GitIndexEntry entry in index.Entries)
        {
            if (entry.Mode == GitFileMode.GitLink)
            {
                // byte-domain — C probes the name-keyed cache with the raw path bytes (submodule.c:508) and falls back to the path→name map
                // (submodule.c:516-517).
                GitPath path = entry.Path;
                ReadOnlyMemory<byte> name = _pathToName.TryGetValue(path, out ReadOnlyMemory<byte> mapped) ? mapped : path.ToUtf8Bytes();
                GitSubmodule sm = GetOrCreate(name);
                sm.IndexId = entry.Id;
                sm.StatusFlags |= SubmoduleStatus.InIndex;
            }
        }
    }

    /// <summary>
    /// Loads gitlink entries from the HEAD tree. Matches
    /// <c>submodules_from_head</c> (submodule.c:536-582).
    /// </summary>
    private async Task LoadFromHeadAsync(CancellationToken cancellationToken)
    {
        GitReference? head = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is not GitDirectReference dr)
        {
            return;
        }

        Commit? commit = await _repo.Objects.LookupAsync<Commit>(dr.Target, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return;
        }

        GitTree? tree = await _repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        await WalkTreeForGitlinksAsync(tree, default, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WalkTreeForGitlinksAsync(GitTree tree, GitPath _, CancellationToken cancellationToken)
    {
        await foreach ((GitPath path, GitTreeEntry entry) in tree.WalkAsync(GitTreeWalkMode.PreOrder, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsGitLink)
            {
                // byte-domain — C probes the name-keyed cache with the raw path bytes (submodule.c:555) and falls back to the path→name map
                // (submodule.c:563-564).
                ReadOnlyMemory<byte> name = _pathToName.TryGetValue(path, out ReadOnlyMemory<byte> mapped) ? mapped : path.ToUtf8Bytes();
                GitSubmodule sm = GetOrCreate(name);
                sm.HeadId = entry.Id;
                sm.StatusFlags |= SubmoduleStatus.InHead;
            }
        }
    }

    /// <summary>
    /// Shallow scan: checks if the submodule path exists as a directory with
    /// a .git inside. Matches <c>submodule_load_from_wd_lite</c>
    /// (submodule.c:2156-2171).
    /// </summary>
    private async Task LoadFromWorkdirLiteAsync(CancellationToken cancellationToken)
    {
        foreach (GitSubmodule sm in _submodules.Values)
        {
            await LoadFromWorkdirLiteAsync(sm, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Per-submodule workdir scan: sets <see cref="SubmoduleStatus.InWd"/>
    /// if the path exists as a directory, and reads the submodule's HEAD
    /// oid into <see cref="GitSubmodule.WdId"/> when a <c>.git</c> is present.
    /// Factored out of the all-submodules scan so
    /// <see cref="ReloadSubmoduleAsync"/> can rescan a single submodule
    /// after a mutation (e.g. <see cref="GitSubmodule.UpdateAsync"/>).
    /// </summary>
    private async Task LoadFromWorkdirLiteAsync(GitSubmodule sm, CancellationToken cancellationToken)
    {
        if (sm.Path is null || sm.Path.Value.IsEmpty)
        {
            return;
        }

        string smPath = IO.PathHelpers.Join(_repo.Workdir, sm.Path.Value.ToUtf8String());

        // C (submodule.c:2156-2171, submodule_load_from_wd_lite): a bare
        // directory gets WD_SCANNED only; IN_WD requires a `.git` file or
        // directory inside it. The scanned bit is tracked internally
        // (the public status masks it out, like C's __WD_SCANNED).
        sm._wdScanned = Directory.Exists(smPath);

        string gitPath = IO.PathHelpers.Join(smPath, ".git");
        if (File.Exists(gitPath) || Directory.Exists(gitPath))
        {
            sm.StatusFlags |= SubmoduleStatus.InWd;

            // Try to read the submodule's HEAD OID.
            try
            {
                GitRepository smRepo = await GitRepository.OpenAsync(smPath, _repo.Context, cancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable smRepoDisposable = smRepo.ConfigureAwait(false);
                GitReference? head = await smRepo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
                if (head is GitDirectReference dr)
                {
                    sm.WdId = dr.Target;
                }
            }
            catch (GitException)
            {
                // Submodule repo might not be fully initialized.
            }
        }
        else
        {
            sm.StatusFlags &= ~SubmoduleStatus.InWd;
        }
    }

    /// <summary>
    /// Updates the location flags on a submodule.
    /// </summary>
    private static void UpdateStatusFlags(GitSubmodule _)
    {
        // The InHead/InIndex/InConfig/InWd flags are set during loading.
        // The actual status computation (IndexAdded/Modified, WdModified, etc.)
        // is done on-demand by Submodule.ComputeStatus.
    }

    private GitSubmodule GetOrCreate(ReadOnlyMemory<byte> name)
    {
        var key = ConfigNameKey.From(name);
        if (!_submodules.TryGetValue(key, out GitSubmodule? sm))
        {
            sm = new GitSubmodule(_repo, name);
            _submodules[key] = sm;
        }

        return sm;
    }

    // ==============================
    // Config value maps
    // ==============================

    /// <summary>
    /// C (submodule.c:2003-2009, looks_like_command_line_option): a
    /// <c>path</c>/<c>url</c> value starting with <c>-</c> is treated as a
    /// command-line option and ignored.
    /// </summary>
    private static bool LooksLikeCommandLineOption(ReadOnlySpan<byte> value)
        => value.Length > 0 && value[0] == (byte)'-';

    /// <summary> Port of <c>git_submodule_parse_update</c> (submodule.c:1962-1974) against <c>_sm_update_map</c> (submodule.c:33-40): the four string forms
    /// (case-insensitive) plus the boolean aliases <c>true</c>→CHECKOUT and <c>false</c>→NONE (config bool parsing, so yes/on/1/0 also match). Returns false
    /// for any other value. byte-domain (C's configmap lookup strcasecmps raw bytes). </summary>
    private static bool TryParseUpdate(ReadOnlySpan<byte> value, out SubmoduleUpdateStrategy result)
    {
        result = default;

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "checkout"u8))
        {
            result = SubmoduleUpdateStrategy.Checkout;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "rebase"u8))
        {
            result = SubmoduleUpdateStrategy.Rebase;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "merge"u8))
        {
            result = SubmoduleUpdateStrategy.Merge;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "none"u8))
        {
            result = SubmoduleUpdateStrategy.None;
            return true;
        }

        if (ConfigurationValueParser.TryParseBool(value, out bool boolVal))
        {
            // GIT_CONFIGMAP_FALSE → NONE, GIT_CONFIGMAP_TRUE → CHECKOUT.
            result = boolVal ? SubmoduleUpdateStrategy.Checkout : SubmoduleUpdateStrategy.None;
            return true;
        }

        return false;
    }

    /// <summary> Port of <c>git_submodule_parse_ignore</c> (submodule.c:1948-1960) against <c>_sm_ignore_map</c> (submodule.c:42-49): the four string forms
    /// plus <c>true</c>→ALL and <c>false</c>→NONE. byte-domain (C's configmap lookup strcasecmps raw bytes). </summary> <summary>Internal for the diff
    /// generator's diff.ignoresubmodules read.</summary>
    internal static bool TryParseIgnore(ReadOnlySpan<byte> value, out SubmoduleIgnore result)
    {
        result = default;

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "none"u8))
        {
            result = SubmoduleIgnore.None;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "untracked"u8))
        {
            result = SubmoduleIgnore.Untracked;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "dirty"u8))
        {
            result = SubmoduleIgnore.Dirty;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "all"u8))
        {
            result = SubmoduleIgnore.All;
            return true;
        }

        if (ConfigurationValueParser.TryParseBool(value, out bool boolVal))
        {
            // GIT_CONFIGMAP_FALSE → NONE, GIT_CONFIGMAP_TRUE → ALL.
            result = boolVal ? SubmoduleIgnore.All : SubmoduleIgnore.None;
            return true;
        }

        return false;
    }

    /// <summary> Port of <c>submodule_parse_recurse</c> (submodule.c:1976-1988) against <c>_sm_recurse_map</c> (submodule.c:51-55): "on-demand" plus
    /// <c>true</c>→YES and <c>false</c>→NO. byte-domain. </summary>
    private static bool TryParseRecurse(ReadOnlySpan<byte> value, out SubmoduleRecurse result)
    {
        result = default;

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "on-demand"u8))
        {
            result = SubmoduleRecurse.OnDemand;
            return true;
        }

        if (ConfigurationValueParser.TryParseBool(value, out bool boolVal))
        {
            // GIT_CONFIGMAP_FALSE → NO, GIT_CONFIGMAP_TRUE → YES.
            result = boolVal ? SubmoduleRecurse.Yes : SubmoduleRecurse.No;
            return true;
        }

        return false;
    }

    /// <summary>
    /// C (submodule.c:1941-1946, submodule_config_error): the config error
    /// for an invalid update/ignore/recurse value — the read fails with
    /// GIT_ERROR (bare -1) and category GIT_ERROR_INVALID.
    /// </summary>
    private static GitException SubmoduleConfigError(string property, ReadOnlySpan<byte> value)
        => new(
            GitErrorCode.Error,
            $"invalid value for submodule '{property}' property: '{Encoding.UTF8.GetString(value)}'",
            GitErrorCategory.Invalid);
}

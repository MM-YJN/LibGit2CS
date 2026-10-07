// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary>
/// Object database. Aggregates one or more <see cref="IObjectBackend"/> instances
/// (loose, pack, alternates) and provides unified lookup with caching. Managed
/// port of libgit2's <c>src/libgit2/odb.c</c> (read side).
/// </summary>
/// <remarks>
/// <para>
/// Backends are consulted in priority order (loose first, then pack, then
/// alternates). A successful lookup caches the parsed object.
/// </para>
/// <para>
/// <b>Strict hash verification</b>: when
/// <see cref="GitSettings.StrictHashVerification"/> is true (default), the OID of
/// each read object is recomputed from its raw bytes and compared to the requested
/// OID. A mismatch throws <see cref="GitException"/> with
/// <see cref="GitErrorCode.Mismatch"/>. The flag is read from the
/// <see cref="GitSettings"/> captured at construction (per-context).
/// </para>
/// <para>
/// <b>Async model:</b> all public methods are <c>*Async</c>
/// returning <see cref="ValueTask"/>/<see cref="ValueTask{TResult}"/>/
/// <see cref="IAsyncEnumerable{T}"/>. <see cref="GitOid"/> is passed by value
/// (C# forbids <c>in</c> on async). The <see cref="CommitGraph"/> property
/// became <see cref="GetCommitGraphAsync"/> (lazy IO in a getter cannot stay
/// async) and <see cref="SetCommitGraph"/> for the test-injection path.
/// Cache hits on <see cref="LookupAsync"/> complete synchronously via
/// <see cref="ValueTask.FromResult"/>. Disposal is async
/// — awaits each backend's <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed class GitObjectDb : IAsyncDisposable
{
    private const int GitOidMinPrefixLen = 4; // minimum abbreviated OID hex length

    private readonly List<BackendEntry> _backends = [];
    private readonly ObjectCache _cache = new();
    private readonly GitSettings _settings;
    private GitRepository? _owner;
    private bool _disposed;

    private CommitGraph? _commitGraph;
    private bool _commitGraphChecked;

    /// <summary>Creates a standalone ODB using the supplied context's settings.</summary>
    /// <param name="context">The caller-owned context whose settings this ODB uses.</param>
    /// <remarks>
    /// Settings are shared with the context, so subsequent settings changes apply
    /// to this ODB. Disposing the ODB does not dispose the context.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public GitObjectDb(GitContext context) : this(owner: null, settings: (context ?? throw new ArgumentNullException(nameof(context))).Settings)
    {
    }

    /// <summary>Creates an ODB owned by <paramref name="owner"/>.</summary>
    /// <param name="owner">The owning repository (null for a standalone ODB).</param>
    /// <param name="settings">
    /// The context settings (strictness flags) this ODB consults at read time.
    /// Pass the owning <see cref="GitContext"/>'s <see cref="GitContext.Settings"/>.
    /// </param>
    internal GitObjectDb(GitRepository? owner, GitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _owner = owner;
        _settings = settings;
    }

    /// <summary>
    /// Sets the owning repository. Called once during <see cref="GitRepository"/>
    /// construction to resolve the chicken-and-egg between the ODB and its owner
    /// (mirrors <c>GitReferences.SetOwner</c>).
    /// </summary>
    internal void SetOwner(GitRepository owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner = owner;
    }

    private sealed class BackendEntry(IObjectBackend backend, int priority, bool isAlternate)
    {
        public IObjectBackend Backend { get; } = backend;
        public int Priority { get; } = priority;
        public bool IsAlternate { get; } = isAlternate;
    }

    /// <summary>
    /// Adds a backend with the given priority. Higher priority backends are
    /// consulted first. Matches <c>git_odb_add_backend</c>.
    /// </summary>
    internal void AddBackend(IObjectBackend backend, int priority, bool isAlternate = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(backend);

        // Insert at the right position to keep sorted by priority (descending).
        int i = 0;
        while (i < _backends.Count && _backends[i].Priority >= priority)
        {
            i++;
        }

        _backends.Insert(i, new BackendEntry(backend, priority, isAlternate));
    }

    /// <summary>
    /// Refreshes all pack backends — re-scans the pack directory for new
    /// <c>.pack</c> files written after the <see cref="GitObjectDb"/> was
    /// constructed. Called after a fetch/clone writes a new pack file to
    /// make the newly-arrived objects visible to <see cref="LookupAsync"/>.
    /// Matches <c>git_odb__refresh</c>.
    /// </summary>
    internal async Task RefreshPackBackendsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (BackendEntry entry in _backends)
        {
            if (entry.Backend is PackObjectBackend packBackend)
            {
                await packBackend.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary> Refreshes the pack backends, mapping a refresh failure to "no refresh happened". Matches C's <c>!git_odb_refresh(db)</c> gate
    /// (odb.c:1049-1053, 1416-1420, 1571-1574): a corrupt/unreadable pack makes the refresh fail, and the lookup short-circuits to not-found instead of
    /// propagating the refresh error. </summary>
    private async Task<bool> TryRefreshPackBackendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary> Adds loose + pack backends for <paramref name="objectsDir"/>, plus alternates from <c>objects/info/alternates</c>. Matches
    /// <c>git_odb__add_default_backends</c>, including the inode dedup (odb.c:697-719): an objects dir whose inode was already loaded is skipped — duplicate
    /// alternates, symlinked object dirs and cyclic alternates add only one backend set. </summary>
    internal async Task AddDefaultBackendsAsync(string objectsDir, GitHashAlgorithmKind algorithm, int alternateDepth, CancellationToken cancellationToken, HashSet<string>? seenDirs = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const int MaxAlternateDepth = 5;

        // C (odb.c:697-719): stat the objects dir and skip it if any loaded
        // backend already carries this inode (the inode is stored on both the
        // loose and the pack backend of each loaded dir). The managed port
        // keys on (dev, ino) via the native stat when available, falling back
        // to the canonical full path.
        seenDirs ??= [];
        string identity = ObjectsDirIdentity(objectsDir);
        if (!seenDirs.Add(identity))
        {
            return;
        }

        // Loose backend (priority 1).
        var loose = new LooseObjectBackend(objectsDir, algorithm);
        AddBackend(loose, priority: 1);

        // Pack backend (priority 2). C (odb.c:734-745): the pack backend is
        // ALWAYS added — with no pack folder its refresh returns 0
        // immediately (odb_pack.c:524-526).
        string packDir = Path.Join(objectsDir, "pack");
        var pack = new PackObjectBackend(packDir, algorithm);
        await pack.RefreshAsync(cancellationToken).ConfigureAwait(false);
        AddBackend(pack, priority: 2);

        // Alternates (recursive). C (odb.c:763-766): files at depths 0..5 are
        // read (alternate_depth > GIT_ALTERNATES_MAX_DEPTH stops).
        if (alternateDepth <= MaxAlternateDepth)
        {
            string alternatesPath = Path.Join(objectsDir, "info", "alternates");
            if (File.Exists(alternatesPath))
            {
                await foreach (string line in AsyncFileIO.ReadLinesAsync(alternatesPath, cancellationToken).ConfigureAwait(false))
                {
                    // C (odb.c:788-790): tokens split on BOTH \r and \n
                    // (git__strtok "\r\n"); no whitespace trimming.
                    foreach (string token in line.Split('\r'))
                    {
                        string altPath = token;
                        if (string.IsNullOrEmpty(altPath) || altPath[0] == '#')
                        {
                            continue;
                        }

                        // C (odb.c:791-798): ONLY dot-prefixed relative paths
                        // resolve against the current objects dir — other
                        // relative paths stay CWD-relative (a C quirk).
                        if (altPath[0] == '.')
                        {
                            altPath = Path.GetFullPath(Path.Join(objectsDir, altPath));
                        }

                        if (Directory.Exists(altPath))
                        {
                            await AddDefaultBackendsAsync(altPath, algorithm, alternateDepth + 1, cancellationToken, seenDirs).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Computes the dedup identity of an objects dir: <c>(dev,ino)</c> from
    /// the native stat when available (mirroring C's <c>st_ino</c> check — two
    /// paths resolving to the same directory, e.g. via symlinks, dedup), else
    /// the canonical full path (managed fallback). On Windows the inode check
    /// is skipped like C's <c>GIT_WIN32</c> branch (odb.c:697-704: "inodes are
    /// not really relevant on Win32" — <c>inode = 0</c>, no dedup loop), because
    /// the 32-bit-truncated NTFS file ID is not unique across directories.
    /// </summary>
    private static string ObjectsDirIdentity(string objectsDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            NativeStat.StatResult stat = NativeStat.GetStat(new DirectoryInfo(objectsDir));
            if (stat.Valid && stat.Dev is { } dev && stat.Ino is { } ino)
            {
                return $"ino:{dev}:{ino}";
            }
        }

        return "path:" + Path.GetFullPath(objectsDir);
    }

    /// <summary>
    /// Checks if an object with the given <paramref name="id"/> exists.
    /// Matches <c>git_odb_exists</c>.
    /// </summary>
    public ValueTask<bool> ExistsAsync(GitOid id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (id.IsZero)
        {
            return ValueTask.FromResult(false);
        }

        // Check cache first.
        if (_cache.Get(id) is not null)
        {
            return ValueTask.FromResult(true);
        }

        // Consult backends. Note: the empty blob/tree are NOT hardcoded here —
        // C's git_odb_exists_ext (odb.c:1031-1054) has no hardcoded check.
        if (ExistsCore(id))
        {
            return ValueTask.FromResult(true);
        }

        // C (odb.c:1046-1053, git_odb_exists_ext): a miss refreshes the pack backends (packs written after the odb was constructed become visible) and retries
        // once. Plain git_odb_exists has the same behavior (odb.c:1028 calls exists_ext with flags 0). A failed refresh short-circuits to not-found.
        return new ValueTask<bool>(ExistsSlowAsync(id, cancellationToken));
    }

    /// <summary>
    /// Slow path of <see cref="ExistsAsync"/>: refreshes the pack backends
    /// (real IO) and retries the (synchronous) backend scan once. Only
    /// reached on a stale miss, so the async machinery is confined here.
    /// </summary>
    private async Task<bool> ExistsSlowAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (await TryRefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            return ExistsCore(id);
        }

        return false;
    }

    /// <summary>
    /// Synchronous backend existence scan. Matches C's <c>odb_exists_1</c>
    /// (odb.c:1000-1010): every backend's <see cref="IObjectBackend.Exists"/>
    /// is a sync file-stat / in-memory dictionary lookup that never yields.
    /// </summary>
    private bool ExistsCore(GitOid id)
    {
        // Consult backends.
        foreach (BackendEntry entry in _backends)
        {
            if (entry.Backend.Exists(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the full OID for an abbreviated prefix. Matches
    /// <c>git_odb_exists_prefix</c>.
    /// </summary>
    /// <param name="prefix">The abbreviated OID (≥ 4 hex chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple of (found, oid). <c>found</c> is <c>true</c> if exactly one object
    /// matches; <c>false</c> if none match. Throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.Ambiguous"/> if multiple objects match.
    /// </returns>
    public async Task<(bool Found, GitOid Oid)> ExistsPrefixAsync(GitOid prefix, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hexLen = prefix.HexLength;
        if (hexLen < GitOidMinPrefixLen)
        {
            // C (odb.c:1117-1120): git_odb_exists_prefix rejects
            // len < GIT_OID_MINPREFIXLEN with GIT_EAMBIGUOUS
            // "prefix length too short" (git_odb__error_ambiguous).
            throw new GitException(
                GitErrorCode.Ambiguous,
                "prefix length too short",
                GitErrorCategory.Odb);
        }

        // Full OID → delegate to Exists.
        if (hexLen >= prefix.HexSize)
        {
            if (await ExistsAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                return (true, prefix);
            }

            return (false, default);
        }

        // Abbreviated: scan all backends, collecting matches.
        (bool found, GitOid oid) = await ExistsPrefixCoreAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (found)
        {
            return (true, oid);
        }

        // C (odb.c:1571-1574, git_odb_read_prefix): a miss refreshes the pack backends (packs written after the odb was constructed become visible) and retries
        // once. A failed refresh short-circuits to not-found, like C's
        // `!git_odb_refresh(db)`
        // gate.
        if (await TryRefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            (found, oid) = await ExistsPrefixCoreAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (found)
            {
                return (true, oid);
            }
        }

        return (false, default);
    }

    private async Task<(bool Found, GitOid Oid)> ExistsPrefixCoreAsync(GitOid prefix, CancellationToken cancellationToken)
    {
        GitOid? match = null;
        foreach (BackendEntry entry in _backends)
        {
            (bool found, GitOid backendFound) = await entry.Backend.ExistsPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                continue;
            }

            if (match is not null && !match.Value.Equals(backendFound))
            {
                throw new GitException(GitErrorCode.Ambiguous, "abbreviated OID matches multiple objects", GitErrorCategory.Odb);
            }

            match = backendFound;
        }

        if (match is { } oid)
        {
            return (true, oid);
        }

        return (false, default);
    }

    /// <summary>
    /// Reads only the header (type + size) for <paramref name="id"/>.
    /// Matches <c>git_odb_read_header</c>.
    /// </summary>
    public ValueTask<GitObjectHeader?> ReadHeaderAsync(GitOid id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (id.IsZero)
        {
            return ValueTask.FromResult<GitObjectHeader?>(null);
        }

        // Check hardcoded empty objects — C special-cases ONLY the empty
        // tree (odb_hardcoded_type, odb.c:60-84); the empty blob falls
        // through to the backends and misses with ENOTFOUND.
        if (IsEmptyTree(id))
        {
            return ValueTask.FromResult<GitObjectHeader?>(new GitObjectHeader(GitObjectType.Tree, 0));
        }

        // Check cache (majority warm path).
        if (_cache.Get(id) is { } cached)
        {
            return ValueTask.FromResult<GitObjectHeader?>(new GitObjectHeader(cached.Type, cached.Size));
        }

        return new ValueTask<GitObjectHeader?>(ReadHeaderSlowAsync(id, cancellationToken));
    }

    /// <summary>Slow path of <see cref="ReadHeaderAsync"/>: backend header reads (ODB IO) + refresh-retry.</summary>
    private async Task<GitObjectHeader?> ReadHeaderSlowAsync(GitOid id, CancellationToken cancellationToken)
    {
        // Consult backends.
        foreach (BackendEntry entry in _backends)
        {
            if (await entry.Backend.ReadHeaderAsync(id, cancellationToken).ConfigureAwait(false) is { } header)
            {
                return header;
            }
        }

        // C (odb.c:1300-1303, git_odb__read_header_or_object): a miss refreshes the pack backends and retries once; a failed refresh short-circuits to
        // not-found.
        if (!await TryRefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        foreach (BackendEntry entry in _backends)
        {
            if (await entry.Backend.ReadHeaderAsync(id, cancellationToken).ConfigureAwait(false) is { } header)
            {
                return header;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="id"/> resolves to a readable object, optionally
    /// of the expected type. Matches <c>git_object__is_valid</c> (object.c):
    /// a READ-based check — unlike <c>ExistsAsync</c>, the hardcoded empty
    /// tree counts as valid (odb_read_hardcoded), exactly like C.
    /// </summary>
    internal async ValueTask<bool> IsValidAsync(GitOid id, GitObjectType expectedType, CancellationToken cancellationToken)
    {
        GitObjectHeader? header = await ReadHeaderAsync(id, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return false;
        }

        if (expectedType != GitObjectType.Any && expectedType != header.Value.Type)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Looks up an object by OID. Matches <c>git_odb_read</c> +
    /// <c>git_object__from_odb_object</c>.
    /// </summary>
    /// <remarks>
    /// Dispatches on the parsed object's <see cref="GitObjectType"/> to construct
    /// the appropriate concrete subclass (<see cref="Commit"/>/<see cref="GitTree"/>/<see cref="GitBlob"/>/<see cref="GitTag"/>)
    /// via <see cref="GitObject.Parse"/>. Use <see cref="LookupAsync{T}"/> for a typed
    /// result that enforces the expected type.
    /// </remarks>
    /// <returns>The parsed object, or null if not found.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Mismatch"/> if strict hash verification fails.
    /// </exception>
    public ValueTask<GitObject?> LookupAsync(GitOid id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (id.IsZero)
        {
            return ValueTask.FromResult<GitObject?>(null);
        }

        // Check cache (majority warm path).
        if (_cache.Get(id) is { } cached)
        {
            return ValueTask.FromResult<GitObject?>(cached);
        }

        // Check hardcoded empty objects — C special-cases ONLY the empty
        // tree (odb_read_hardcoded, odb.c:68-84); the empty blob falls
        // through to the backends and misses with GIT_ENOTFOUND.
        if (IsEmptyTree(id))
        {
            var emptyObj = GitObject.Parse(_owner, id, GitObjectType.Tree, ReadOnlyMemory<byte>.Empty, id.Algorithm);
            _cache.Put(id, emptyObj);
            return ValueTask.FromResult<GitObject?>(emptyObj);
        }

        return new ValueTask<GitObject?>(LookupSlowAsync(id, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LookupAsync(GitOid, CancellationToken)"/>: backend reads (ODB IO) + refresh-retry.</summary>
    private async Task<GitObject?> LookupSlowAsync(GitOid id, CancellationToken cancellationToken)
    {
        // Consult backends.
        foreach (BackendEntry entry in _backends)
        {
            if (await entry.Backend.ReadAsync(id, cancellationToken).ConfigureAwait(false) is { } raw)
            {
                // Strict hash verification: re-hash the raw object and compare.
                if (_settings.StrictHashVerification)
                {
                    GitOid computed = HashObject(raw.Type, raw.Data, id.Algorithm);
                    if (!computed.Equals(id))
                    {
                        throw new GitException(
                            GitErrorCode.Mismatch,
                            $"object hash mismatch: expected {id}, got {computed}",
                            GitErrorCategory.Odb);
                    }
                }

                GitObject obj = ParseWithErrorDowngrade(id, raw.Type, raw.Data);
                _cache.Put(id, obj);
                return obj;
            }
        }

        // C (odb.c:1399-1417, git_odb_read): a miss refreshes the pack backends and retries once; a failed refresh short-circuits to not-found.
        if (!await TryRefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await LookupCoreAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a raw object, downgrading parser <c>GIT_EINVALID</c> failures to
    /// a plain <c>-1</c> (GIT_ERROR) — C's
    /// <c>git_object__from_odb_object</c> does exactly this
    /// ("parse returns EINVALID on invalid data; downgrade that to a normal
    /// -1 error code", object.c:165-172).
    /// </summary>
    private GitObject ParseWithErrorDowngrade(GitOid id, GitObjectType type, ReadOnlyMemory<byte> data)
    {
        try
        {
            return GitObject.Parse(_owner, id, type, data, id.Algorithm);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.Invalid)
        {
            throw new GitException(GitErrorCode.Error, ex.Message, ex.Category);
        }
    }

    private async Task<GitObject?> LookupCoreAsync(GitOid id, CancellationToken cancellationToken)
    {
        // Consult backends.
        foreach (BackendEntry entry in _backends)
        {
            if (await entry.Backend.ReadAsync(id, cancellationToken).ConfigureAwait(false) is { } raw)
            {
                // Strict hash verification: re-hash the raw object and compare.
                if (_settings.StrictHashVerification)
                {
                    GitOid computed = HashObject(raw.Type, raw.Data, id.Algorithm);
                    if (!computed.Equals(id))
                    {
                        throw new GitException(
                            GitErrorCode.Mismatch,
                            $"object hash mismatch: expected {id}, got {computed}",
                            GitErrorCategory.Odb);
                    }
                }

                GitObject obj = ParseWithErrorDowngrade(id, raw.Type, raw.Data);
                _cache.Put(id, obj);
                return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up an object by OID, enforcing the requested type <typeparamref name="T"/>.
    /// Matches <c>git_commit_lookup</c>/<c>git_tree_lookup</c>/etc. Throws if the
    /// parsed object's type doesn't match <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The expected concrete type (<see cref="Commit"/>, <see cref="GitTree"/>, <see cref="GitBlob"/>, <see cref="GitTag"/>).</typeparam>
    /// <returns>The typed object, or null if not found.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Mismatch"/> if the object exists but its type doesn't match <typeparamref name="T"/>.
    /// </exception>
    public async ValueTask<T?> LookupAsync<T>(GitOid id, CancellationToken cancellationToken = default) where T : GitObject
    {
        GitObject? obj = await LookupAsync(id, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            return null;
        }

        if (obj is not T typed)
        {
            // C (git_object__init_from_odb_object, object.c:121-128):
            // git_object_lookup with a mismatched type returns GIT_ENOTFOUND
            // "the requested type does not match the type in the ODB" with
            // GIT_ERROR_INVALID.
            throw new GitException(
                GitErrorCode.NotFound,
                "the requested type does not match the type in the ODB",
                GitErrorCategory.Invalid);
        }

        return typed;
    }

    /// <summary>
    /// Looks up an object by abbreviated OID prefix, enforcing the requested type
    /// <typeparamref name="T"/>. Matches <c>git_commit_lookup_prefix</c>/etc.
    /// </summary>
    /// <returns>The typed object, or <c>null</c> if no object is found for the prefix.</returns>
    public async Task<T?> LookupPrefixAsync<T>(GitOid prefix, CancellationToken cancellationToken = default) where T : GitObject
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CheckPrefixLength(prefix);

        (bool found, GitOid oid) = await ExistsPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return null;
        }

        return await LookupAsync<T>(oid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks up an object by abbreviated OID prefix. Matches
    /// <c>git_odb_read_prefix</c>.
    /// </summary>
    /// <returns>The object, or <c>null</c> if no object is found for the prefix.</returns>
    public async Task<GitObject?> LookupPrefixAsync(GitOid prefix, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CheckPrefixLength(prefix);

        (bool found, GitOid oid) = await ExistsPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return null;
        }

        return await LookupAsync(oid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// C (object.c:206-209): <c>git_object_lookup_prefix</c> rejects
    /// len &lt; GIT_OID_MINPREFIXLEN with GIT_EAMBIGUOUS "ambiguous lookup -
    /// OID prefix is too short" (GIT_ERROR_OBJECT).
    /// </summary>
    private static void CheckPrefixLength(GitOid prefix)
    {
        if (prefix.HexLength < GitOidMinPrefixLen)
        {
            throw new GitException(
                GitErrorCode.Ambiguous,
                "ambiguous lookup - OID prefix is too short",
                GitErrorCategory.Object);
        }
    }

    /// <summary>
    /// Enumerates all OIDs across all backends. Matches <c>git_odb_foreach</c>.
    /// </summary>
    public IAsyncEnumerable<GitOid> EnumerateAsync(CancellationToken cancellationToken = default)
        => EnumerateCoreAsync(cancellationToken);

    private async IAsyncEnumerable<GitOid> EnumerateCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // C (git_odb_foreach, odb.c:1582-1611): each backend's foreach is
        // invoked in order with NO dedup — the same OID found in two backends
        // (e.g. loose + pack) is yielded twice.
        foreach (BackendEntry entry in _backends)
        {
            await foreach (GitOid oid in entry.Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return oid;
            }
        }
    }

    /// <summary>
    /// Hashes a raw object (header + body) to produce its OID. Matches
    /// <c>git_odb__hashobj</c>. Stays sync — pure CPU.
    /// </summary>
    internal static GitOid HashObject(GitObjectType type, ReadOnlySpan<byte> body, GitHashAlgorithmKind algorithm)
    {
        // git_odb__hashobj (odb.c:119-122): non-loose types are rejected with
        // GIT_ERROR_INVALID "invalid object type" (git_object_typeisloose,
        // object.c:345-351 — only commit/tree/blob/tag are loose).
        if (!IsLooseType(type))
        {
            throw new GitException(
                GitErrorCode.Error,
                "invalid object type",
                GitErrorCategory.Invalid);
        }

        return GitOid.ComputeOid(type, body, algorithm);
    }

    /// <summary>
    /// The object format (hash algorithm) used by this ODB. Delegates to the
    /// owning repository's <see cref="LibGit2CS.Repository.GitRepository.ObjectFormat"/>, or SHA-1 for
    /// standalone ODBs.
    /// </summary>
    internal GitHashAlgorithmKind Algorithm => _owner?.ObjectFormat ?? GitHashAlgorithmKind.Sha1;

    /// <summary>
    /// Writes a complete object to the ODB. Matches <c>git_odb_write</c>.
    /// Hashes the object (header + body) to compute its OID, then persists it
    /// via the first write-capable backend. If the object already exists, it is
    /// freshened (mtime touched) and not rewritten.
    /// </summary>
    /// <param name="type">The object type.</param>
    /// <param name="body">The raw object body (without the type/size header).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the written object.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if no write-capable backend is available.
    /// </exception>
    public async Task<GitOid> WriteAsync(GitObjectType type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        GitOid oid = HashObject(type, body.Span, Algorithm);
        if (oid.IsZero)
        {
            // C (odb.c:1626-1627, 1980-1984): error_null_oid(GIT_EINVALID,
            // "cannot write object") — "odb: cannot write object: null OID
            // cannot exist", GIT_ERROR_ODB.
            throw new GitException(
                GitErrorCode.Invalid,
                "odb: cannot write object: null OID cannot exist",
                GitErrorCategory.Odb);
        }

        // C (odb.c:1629-1630): git_odb_write freshens when the object is found
        // in ANY backend (git_odb__freshen → odb_freshen_1, odb.c:981-1009).
        // The freshen check consults the BACKENDS ONLY — no in-memory cache,
        // no hardcoded empty objects. A cached-but-not-stored object is
        // written normally.
        if (await FreshenAsync(oid, cancellationToken).ConfigureAwait(false))
        {
            return oid;
        }

        // Find the first write-capable, non-alternate backend and write.
        foreach (BackendEntry entry in _backends)
        {
            if (entry.IsAlternate)
            {
                continue;
            }

            if (entry.Backend is IObjectWriteBackend wb)
            {
                await wb.WriteAsync(oid, type, body, cancellationToken).ConfigureAwait(false);
                return oid;
            }
        }

        // C (odb.c:1656-1657, 1715-1716): git_odb__error_unsupported_in_backend ("write object") → "cannot write object - unsupported in the loaded odb
        // backends".
        throw new GitException(
            GitErrorCode.Error,
            "cannot write object - unsupported in the loaded odb backends",
            GitErrorCategory.Odb);
    }

    /// <summary>
    /// Opens a streaming write. Matches <c>git_odb_open_wstream</c>. The
    /// returned stream accepts body bytes in chunks; call
    /// <see cref="ObjectWriteStream.FinalizeAsync"/> to atomically persist.
    /// </summary>
    /// <param name="type">The object type.</param>
    /// <param name="declaredSize">The expected body size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write stream.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if no write-capable backend is available.
    /// </exception>
    internal async Task<ObjectWriteStream> OpenWriteStreamAsync(GitObjectType type, long declaredSize, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (BackendEntry entry in _backends)
        {
            if (entry.IsAlternate)
            {
                continue;
            }

            if (entry.Backend is IObjectWriteBackend wb)
            {
                ObjectWriteStream backendStream = await wb.OpenWriteStreamAsync(type, declaredSize, cancellationToken).ConfigureAwait(false);
                return new OdbWriteStream(backendStream, type, declaredSize, Algorithm);
            }
        }

        throw new GitException(
            GitErrorCode.Error,
            "cannot write object - unsupported in the loaded odb backends",
            GitErrorCategory.Odb);
    }

    /// <summary> Freshens an existing object (touches its mtime to prevent GC). Matches <c>git_odb__freshen</c> (odb.c:1011-1024): every backend is consulted
    /// (alternates included); backends with a freshen operation use it, others fall back to an existence check. On a miss the pack backends are refreshed and
    /// the retry consults only the refreshed (pack) backends — an OID present only in a pack written after the ODB was constructed is still freshened. Returns
    /// true if the object was found by at least one backend. </summary>
    internal async ValueTask<bool> FreshenAsync(GitOid oid, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (oid.IsZero)
        {
            return false;
        }

        if (FreshenCore(oid, packsOnly: false))
        {
            return true;
        }

        // C (odb.c:1020-1024): on a miss, git_odb_refresh() the pack backends
        // and retry, consulting only the refreshed backends (odb_freshen_1
        // with only_refreshed=true skips backends without a refresh op). A
        // failed refresh short-circuits to not-found, like C's
        // `!git_odb_refresh(db)` gate.
        if (await TryRefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            return FreshenCore(oid, packsOnly: true);
        }

        return false;
    }

    /// <summary>
    /// Synchronous backend freshen scan. Matches C's <c>odb_freshen_1</c>
    /// (odb.c:977-1009): backends with a freshen operation use it, others
    /// fall back to <see cref="IObjectBackend.Exists"/> — both are sync
    /// metadata operations that never yield.
    /// </summary>
    private bool FreshenCore(GitOid oid, bool packsOnly)
    {
        foreach (BackendEntry entry in _backends)
        {
            if (packsOnly && entry.Backend is not PackObjectBackend)
            {
                continue;
            }

            bool found = entry.Backend is IObjectWriteBackend wb
                ? wb.Freshen(oid)
                : entry.Backend.Exists(oid);

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>.git/objects/info/commit-graph</c> for this ODB's owning repository,
    /// lazily opened on first access. Null when the file is absent (revwalk falls
    /// back to ODB reads). Matches <c>git_odb__get_commit_graph_file</c> +
    /// <c>git_odb_set_commit_graph</c> (odb.c:815-830).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single source of truth:</b> the commit-graph lives on the
    /// <see cref="GitObjectDb"/>; <c>GitRepository.GetCommitGraphAsync</c> delegates
    /// here. The lazy-load reads from <c>&lt;owner.Path&gt;/objects/info/commit-graph</c>
    /// on first access (catching <see cref="GitException"/> → null, matching C's
    /// silent-fallback on corrupt files).
    /// </para>
    /// <para>
    /// <b>Async model:</b> the <c>CommitGraph</c> property became
    /// <see cref="GetCommitGraphAsync"/> (lazy IO in a getter cannot stay sync).
    /// <see cref="SetCommitGraph"/> replaces the property setter for
    /// the test-injection path (<c>ObjectDbCommitGraphSetterTests</c>). C takes
    /// ownership (frees the previous instance); in C# the previous reference
    /// is simply dropped for GC to collect. Not
    /// thread-safe — consistent with the rest of <see cref="GitObjectDb"/>.
    /// </para>
    /// </remarks>
    internal ValueTask<CommitGraph?> GetCommitGraphAsync(CancellationToken cancellationToken)
    {
        // Later calls are a memoized cache hit (majority) — return synchronously.
        if (_commitGraphChecked || _owner is null)
        {
            return ValueTask.FromResult(_commitGraph);
        }

        return new ValueTask<CommitGraph?>(GetCommitGraphSlowAsync(cancellationToken));
    }

    /// <summary>Cold path of <see cref="GetCommitGraphAsync"/>: opens the commit-graph file (disk IO).</summary>
    private async Task<CommitGraph?> GetCommitGraphSlowAsync(CancellationToken cancellationToken)
    {
        if (!_commitGraphChecked && _owner is not null)
        {
            _commitGraphChecked = true;
            try
            {
                _commitGraph = await CommitGraph.OpenAsync(
                    Path.Join(_owner.Path, "objects"),
                    Algorithm,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                // Corrupt commit-graph: silently fall back to ODB.
                _commitGraph = null;
            }
        }

        return _commitGraph;
    }

    /// <summary>
    /// Replaces the cached commit-graph reference and marks it checked so the
    /// lazy-load does not overwrite an explicit set. Pass <c>null</c> to unset.
    /// Matches <c>git_odb_set_commit_graph</c> (<c>include/git2/odb.h:691</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ownership:</b> C transfers ownership of <c>cgraph</c> to the ODB and
    /// the caller must not free it; in C# the previous reference is simply
    /// dropped for GC to collect.
    /// </para>
    /// <para>
    /// Also the test-injection path (<c>ObjectDbCommitGraphSetterTests</c>).
    /// Not thread-safe — consistent with the rest of <see cref="GitObjectDb"/>.
    /// </para>
    /// </remarks>
    public void SetCommitGraph(CommitGraph? graph)
    {
        _commitGraph = graph;
        _commitGraphChecked = true;
    }

    /// <summary>
    /// Clears the cached commit-graph (and the checked flag) so the next
    /// <see cref="GetCommitGraphAsync"/> re-reads
    /// <c>&lt;owner.Path&gt;/objects/info/commit-graph</c> from disk. No C
    /// counterpart — C has no cached-graph invalidation; this exists so
    /// <see cref="GitRepository.WriteCommitGraphAsync"/> can make a write
    /// visible to subsequent reads on the same repository handle.
    /// </summary>
    internal void ResetCommitGraphCache()
    {
        _commitGraph = null;
        _commitGraphChecked = false;
    }

    /// <summary>
    /// Writes (rewrites) the <c>multi-pack-index</c> file for each pack backend.
    /// Managed port of <c>git_odb_write_multi_pack_index</c> (odb.c:1874-1901).
    /// </summary>
    /// <remarks>
    /// Iterates <see cref="_backends"/>, skips alternates (C:
    /// <c>if (internal-&gt;is_alternate) continue;</c>), and dispatches to each
    /// <see cref="PackObjectBackend"/>'s <see cref="PackObjectBackend.WriteMultiPackIndexAsync"/>
    /// (replaces C's <c>b-&gt;writemidx != NULL</c> vtable check via a type test).
    /// Throws <see cref="GitException"/> with <see cref="GitErrorCode.NotSupported"/>
    /// when no pack backend writes (matches C's
    /// <c>git_odb__error_unsupported_in_backend</c>).
    /// </remarks>
    public async Task WriteMultiPackIndexAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int writes = 0;
        foreach (BackendEntry entry in _backends)
        {
            if (entry.IsAlternate)
            {
                continue;
            }

            if (entry.Backend is PackObjectBackend pack)
            {
                if (await pack.WriteMultiPackIndexAsync(cancellationToken).ConfigureAwait(false))
                {
                    writes++;
                }
            }
        }

        if (writes == 0)
        {
            throw new GitException(
                GitErrorCode.NotSupported,
                "no pack backend supports writing a multi-pack-index",
                GitErrorCategory.Odb);
        }
    }

    /// <summary>
    /// Expands one or more abbreviated object IDs to full OIDs, mutating the
    /// <paramref name="ids"/> span in place. Managed port of
    /// <c>git_odb_expand_ids</c> (odb.c:1144-1211).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each <see cref="GitOdbExpandId"/>:
    /// </para>
    /// <list type="bullet">
    /// <item>If <see cref="GitOdbExpandId.Length"/> is &gt;=
    /// <see cref="GitOidMinPrefixLen"/> (4) and &lt; the OID hex size, the
    /// abbreviation is expanded via <see cref="ExistsPrefixAsync"/>. On success the
    /// <see cref="GitOdbExpandId.Id"/> is replaced with the full OID and
    /// <see cref="GitOdbExpandId.Length"/> is set to the hex size.</item>
    /// <item>If the (now-full) OID's <see cref="ReadHeaderAsync"/> succeeds, the type
    /// is verified against the requested <see cref="GitOdbExpandId.Type"/> (when
    /// not <see cref="GitObjectType.Any"/>). On match, <see cref="GitOdbExpandId.Type"/>
    /// is filled in with the actual type.</item>
    /// <item>On not-found or ambiguity (including type mismatch), the entry is
    /// cleared to <c>default</c> (zero OID, length 0, <see cref="GitObjectType.Ext1"/>).</item>
    /// </list>
    /// <para>
    /// <b>Ambiguity handling:</b> C's <c>odb_exists_prefix_1</c> returns
    /// <c>GIT_EAMBIGUOUS</c> as a status code; C#'s <see cref="ExistsPrefixAsync"/>
    /// throws <see cref="GitException"/> with <see cref="GitErrorCode.Ambiguous"/>.
    /// This method catches that exception and treats it as "not found" (clear the
    /// entry), matching C's <c>switch (error) { case ENOTFOUND: case EAMBIGUOUS: clear; }</c>.
    /// </para>
    /// <para>
    /// <b>No refresh-retry:</b> unlike <see cref="ExistsPrefixAsync"/> (the public
    /// API, which has a refresh fallback via <c>git_odb_refresh</c>), this batch
    /// API calls <see cref="ExistsPrefixAsync"/> directly — matching C, which calls
    /// <c>odb_exists_prefix_1</c> (the internal helper) not
    /// <c>git_odb_exists_prefix</c> (the public API with refresh). The batch
    /// comment in <c>odb.h:278-279</c> documents this intentional difference.
    /// </para>
    /// </remarks>
    /// <param name="ids">The short OIDs to expand; mutated in place.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExpandIdsAsync(GitOdbExpandId[] ids, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ids);

        int hexSize = GitOid.HexSizeFor(Algorithm);

        for (int i = 0; i < ids.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitOdbExpandId query = ids[i];

            // C: if (!query->type) query->type = GIT_OBJECT_ANY;
            // GitObjectType.Ext1 (0) is the C# zero-value and means "unspecified".
            if (query.Type == GitObjectType.Ext1)
            {
                query = query with { Type = GitObjectType.Any };
            }

            ExpandStatus status = ExpandStatus.NotFound; // sentinel; cleared to Ok on success

            // C: if (query->length >= GIT_OID_MINPREFIXLEN && query->length < hex_size)
            if (query.Length >= GitOidMinPrefixLen && query.Length < hexSize)
            {
                // Normalize the OID's HexLength to match query.Length — the C
                // git_odb_expand_ids passes query->length separately to
                // odb_exists_prefix_1, but the C# ExistsPrefix infers the
                // prefix length from GitOid.HexLength. If they diverge (e.g. a
                // full OID from FromRaw with Length=8), the lookup would use
                // the wrong nibble count. WithHexLength aligns them.
                GitOid prefix = query.Id.WithHexLength(query.Length);
                try
                {
                    (bool found, GitOid actual) = await ExistsPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
                    if (found)
                    {
                        query = query with { Id = actual, Length = hexSize };
                        status = ExpandStatus.Ok;
                    }
                }
                catch (GitException ex) when (ex.Code == GitErrorCode.Ambiguous)
                {
                    // Ambiguous → leave status as NotFound sentinel; cleared below.
                    status = ExpandStatus.Ambiguous;
                }
            }

            // C: if (query->length >= hex_size) — verify type via ReadHeader.
            if (query.Length >= hexSize)
            {
                GitObjectHeader? header = await ReadHeaderAsync(query.Id, cancellationToken).ConfigureAwait(false);
                if (header is { } h)
                {
                    if (query.Type != GitObjectType.Any && query.Type != h.Type)
                    {
                        status = ExpandStatus.NotFound; // type mismatch → not found
                    }
                    else
                    {
                        query = query with { Type = h.Type };
                        status = ExpandStatus.Ok;
                    }
                }
                // else: header not found → status stays non-Ok
            }

            // C: switch (error) { case ENOTFOUND/EAMBIGUOUS: clear; case 0: continue; }
            if (status is not ExpandStatus.Ok)
            {
                query = default; // zero OID, length 0, GitObjectType.Ext1
            }

            ids[i] = query;
        }
    }

    private enum ExpandStatus { Ok, NotFound, Ambiguous }

    private bool IsEmptyTree(GitOid id)
    {
        // C (odb.c:59-66, odb_hardcoded_type): only the SHA-1 empty tree is
        // hardcoded in 1.9.4. The SHA-256 empty tree is an intentional
        // forward-port for SHA-256 repositories (C 1.9.4 has no SHA-256
        // support); it is only fabricated when the OID's algorithm matches
        // this ODB's object format, so a SHA-1 repo never fabricates an
        // object for a SHA-256 OID (C returns GIT_ENOTFOUND there) in
        // libgit2 1.9.4.
        return id == (Algorithm == GitHashAlgorithmKind.Sha256 ? GitOid.EmptyTreeSha256 : GitOid.EmptyTreeSha1);
    }

    /// <summary>
    /// Whether the type is a loose object type (commit/tree/blob/tag).
    /// Matches <c>git_object_typeisloose</c> (object.c:345-351).
    /// </summary>
    private static bool IsLooseType(GitObjectType type) => type is >= GitObjectType.Commit and <= GitObjectType.Tag;

    internal static string TypeToString(GitObjectType type) => type switch
    {
        GitObjectType.Commit => "commit",
        GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob",
        GitObjectType.Tag => "tag",
        GitObjectType.OfsDelta => "OFS_DELTA",
        GitObjectType.RefDelta => "REF_DELTA",
        _ => string.Empty,
    };

    /// <summary>Clears the object cache. Matches <c>git_cache_clear</c>.</summary>
    internal void ClearCache() => _cache.Clear();

    /// <summary>
    /// Enables or disables caching of large trees (&#8805;4096 bytes) for the
    /// duration of a blame walk. See <see cref="ObjectCache.AllowLargeTrees"/>.
    /// </summary>
    internal void SetAllowLargeTrees(bool value) => _cache.AllowLargeTrees = value;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Dispose();
        foreach (BackendEntry entry in _backends)
        {
            await entry.Backend.DisposeAsync().ConfigureAwait(false);
        }

        _backends.Clear();
    }
}

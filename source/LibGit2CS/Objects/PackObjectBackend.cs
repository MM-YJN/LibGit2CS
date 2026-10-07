// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Pack;

namespace LibGit2CS.Objects;

/// <summary>
/// Pack object backend — bridges <see cref="GitObjectDb"/> to one or more
/// <see cref="PackFile"/> instances. Managed port of libgit2's
/// <c>src/libgit2/odb_pack.c</c> (read side).
/// </summary>
/// <remarks>
/// <para>
/// On construction, scans <c>objects/pack/</c> for all <c>.pack</c> files and
/// opens each one. Lookup dispatches across all packs, with a "last found"
/// optimization: the pack that last had a hit is tried first.
/// </para>
/// <para>
/// <b>Cross-pack REF_DELTA</b>: when a REF_DELTA's base OID isn't found in the
/// same pack, each <see cref="PackFile"/> delegates to the
/// <see cref="ResolveCrossPackBaseAsync"/> callback (wired via
/// <see cref="PackFile.OpenAsync"/>) to resolve the base from other packs. This
/// makes packs with cross-pack REF_DELTA references — common in corrupt packs
/// from failed fetches — readable. An <see cref="AsyncLocal{T}"/> depth counter
/// bounds recursion to <see cref="PackFile.MaxDeltaDepth"/>.
/// </para>
/// <para>
/// <b>Async IO</b>: pack content is read with file-positioned async reads
/// (<see cref="File.OpenHandle"/> + <see cref="RandomAccess.ReadAsync(Microsoft.Win32.SafeHandles.SafeFileHandle, Memory{byte}, long, CancellationToken)"/>)
/// on a single <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/> held by each <see cref="PackFile"/>.
/// <see cref="PackFile.OpenAsync"/> performs the file open; every per-object
/// read (<see cref="PackFile.ReadAsync"/>, <see cref="PackFile.ReadHeaderAsync"/>)
/// is genuinely async IO. Index-only operations (<see cref="IObjectBackend.Exists"/>,
/// <see cref="ExistsPrefixAsync"/>, <see cref="EnumerateAsync"/>) stay sync —
/// they never touch the pack window. Disposal is async.
/// </para>
/// <para>
/// <b>Accepted divergence from libgit2</b>: the C reference reads packs through
/// a sliding-window mmap layer (<c>src/libgit2/mwindow.c</c>) and materializes
/// every window read into a fresh buffer. This port instead reads the pack via
/// file-positioned async IO on the handle — there is no mwindow equivalent
/// class, and every read writes into a caller-managed buffer. Trade-off: the C
/// reader relies on OS page-cache zero-copy, while the port pays explicit
/// syscalls per read and reuses buffers (<see cref="System.Buffers.ArrayPool{T}.Shared"/>)
/// rather than mapping pages. This is intentional in favor of a purely-async,
/// AOT-clean IO model.
/// </para>
/// </remarks>
internal sealed class PackObjectBackend : IObjectBackend
{
    private readonly List<PackFile> _packs = [];
    private readonly HashSet<string> _loadedPackPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<int, PackFile> _midxPacks = [];
    private readonly string _packDir;
    private readonly GitHashAlgorithmKind _algorithm;
    private PackFile? _lastFound;
    private MultiPackIndex? _midx;
    private bool _disposed;

    /// <summary>
    /// Per-async-flow recursion depth for cross-pack REF_DELTA resolution.
    /// When <see cref="ResolveCrossPackBaseAsync"/> is called from
    /// <see cref="PackFile.ReadAsync"/> (which is itself called from
    /// <see cref="ResolveCrossPackBaseAsync"/>), the depth increments. At
    /// <see cref="PackFile.MaxDeltaDepth"/> the resolver returns null to
    /// prevent infinite recursion on corrupt packs with circular delta
    /// references.
    /// </summary>
    private readonly AsyncLocal<int> _crossPackDepth = new();

    public PackObjectBackend(string packDir, GitHashAlgorithmKind algorithm)
    {
        _packDir = packDir;
        _algorithm = algorithm;
    }

    /// <summary>
    /// Re-scans the pack directory for pack files. Matches
    /// <c>pack_backend__refresh</c>. Also opens the multi-pack-index when present.
    /// Now async — opens each <c>.pack</c> via <see cref="PackFile.OpenAsync"/>
    /// and the MIDX via <see cref="MultiPackIndex.OpenAsync"/>.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !Directory.Exists(_packDir))
        {
            return;
        }

        foreach (string idxFile in Directory.EnumerateFiles(_packDir, "*.idx"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packFile = Path.ChangeExtension(idxFile, ".pack");
            if (!File.Exists(packFile))
            {
                continue;
            }

            // Skip if already loaded. Ports packfile_load__cb's
            // git_vector_search2(... backend->packs ...) dedup-by-name guard
            // (odb_pack.c:250-253) — a refresh re-scans the whole directory,
            // but must not re-open packs that a prior refresh already loaded.
            // Without this, a fetch into a repo with existing packs would
            // duplicate every pack in _packs (double mmap + wasted iteration).
            if (!_loadedPackPaths.Add(packFile))
            {
                continue;
            }

            try
            {
                _packs.Add(await PackFile.OpenAsync(packFile, _algorithm, cancellationToken, ResolveCrossPackBaseAsync).ConfigureAwait(false));
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
            {
                // C (odb_pack.c:255-257): a GIT_ENOTFOUND open error is
                // cleared and the pack skipped (missing pack). Any OTHER
                // error propagates and FAILS the whole refresh
                // (odb_pack.c:258-260, odb.c:1416-1417) — corrupt packs are
                // not silently skipped. Undo the path registration so a
                // later refresh can retry.
                _loadedPackPaths.Remove(packFile);
            }
        }

        // C sorts the pack vector (packfile_sort__cb, odb_pack.c:205-225): local packs first (every pack here is local), then YOUNGER packs first (larger
        // mtime). Stable sort — equal mtimes keep load order. Pack search order is observable: the first pack whose.idx claims an OID is the one read
        // (pack_entry_find, odb_pack.c:270-301). C compares WHOLE-SECOND mtimes (pack.c:1227: `p->mtime = (git_time_t)st.st_mtime`); two packs written within
        // the same whole second are a tie and keep their load order. Comparing sub-second DateTime values would order them younger-first, changing which pack
        // wins a duplicated-OID lookup.
        TimSort.Sort(_packs, static (a, b) =>
        {
            long aMtime = File.GetLastWriteTimeUtc(a.PackPath).Ticks / TimeSpan.TicksPerSecond;
            long bMtime = File.GetLastWriteTimeUtc(b.PackPath).Ticks / TimeSpan.TicksPerSecond;
            return bMtime.CompareTo(aMtime);
        });

        // Open the multi-pack-index if present (accelerates multi-pack lookup).
        try
        {
            _midx = await MultiPackIndex.OpenAsync(_packDir, _algorithm, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            _midx = null;
        }
    }

    /// <summary>
    /// Writes (rewrites) the <c>multi-pack-index</c> file for this backend's pack
    /// directory. Managed port of <c>pack_backend__writemidx</c>
    /// (odb_pack.c:791-855). Matches <c>git_odb_backend::writemidx</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adds every loaded pack's <c>.idx</c> to a fresh <see cref="MultiPackIndexWriter"/>,
    /// invalidates the previous MIDX (delete before write, matching C's
    /// <c>remove_multi_pack_index</c>), commits the new file atomically, then
    /// refreshes the in-memory <see cref="_midx"/> handle.
    /// </para>
    /// <para>
    /// Called by <see cref="GitObjectDb.WriteMultiPackIndexAsync"/> via a
    /// <c>is PackObjectBackend</c> type test (replaces C's <c>b-&gt;writemidx != NULL</c>
    /// vtable check). Returns <c>false</c> when the pack directory has no packs;
    /// the caller (<see cref="GitObjectDb.WriteMultiPackIndexAsync"/>) raises
    /// <see cref="GitErrorCode.NotSupported"/> when no backend writes (matches C's
    /// <c>git_odb__error_unsupported_in_backend</c>).
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> if a MIDX was written; <c>false</c> if no packs present.</returns>
    internal async Task<bool> WriteMultiPackIndexAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _packs.Count == 0)
        {
            return false;
        }

        using var writer = new MultiPackIndexWriter(_packDir, _algorithm);
        foreach (PackFile pack in _packs)
        {
            await writer.AddAsync(pack.IdxPath, cancellationToken).ConfigureAwait(false);
        }

        // Invalidate the previous MIDX before writing the new one (matches C's
        // remove_multi_pack_index at odb_pack.c:844).
        string midxPath = Path.Join(_packDir, "multi-pack-index");
        if (File.Exists(midxPath))
        {
            File.Delete(midxPath);
        }

        await writer.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Re-open the fresh MIDX so subsequent reads use it (matches C's
        // refresh_multi_pack_index at odb_pack.c:850).
        try
        {
            _midx = await MultiPackIndex.OpenAsync(_packDir, _algorithm, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            _midx = null;
        }

        return true;
    }

    /// <summary>
    /// Resolves a midx pack index to a loaded <see cref="PackFile"/>,
    /// loading it lazily from the PNAM name on first use. Matches the
    /// <c>midx_packs</c> vector population in <c>refresh_multi_pack_index</c>.
    /// </summary>
    private ValueTask<PackFile?> GetMidxPackAsync(int packIndex, CancellationToken cancellationToken)
    {
        if (_midx is null)
        {
            return ValueTask.FromResult<PackFile?>(null);
        }

        if (_midxPacks.TryGetValue(packIndex, out PackFile? cached))
        {
            return ValueTask.FromResult<PackFile?>(cached);
        }

        return new ValueTask<PackFile?>(GetMidxPackSlowAsync(packIndex, cancellationToken));
    }

    /// <summary>Slow path of <see cref="GetMidxPackAsync"/>: loads the pack file from disk on first use.</summary>
    private async Task<PackFile?> GetMidxPackSlowAsync(int packIndex, CancellationToken cancellationToken)
    {
        string name = _midx!.GetPackName(packIndex); // e.g. "pack-<hash>.idx"
        string packPath = Path.Join(_packDir, Path.ChangeExtension(name, ".pack"));
        if (!File.Exists(packPath))
        {
            return null;
        }

        try
        {
            PackFile pack = await PackFile.OpenAsync(packPath, _algorithm, cancellationToken, ResolveCrossPackBaseAsync).ConfigureAwait(false);
            _midxPacks[packIndex] = pack;
            return pack;
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cross-pack REF_DELTA base resolver. Called by <see cref="PackFile.ReadAsync"/>
    /// / <see cref="PackFile.ReadHeaderAsync"/> when a REF_DELTA base OID is not in
    /// the same pack. Iterates all other packs to find and read the base object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implements the <c>crossPackBaseResolver</c> callback documented in
    /// the class-level remarks. The C libgit2 <c>get_delta_base</c>
    /// (<c>pack.c:1030</c>) throws "base entry delta is not in the same pack"
    /// when a REF_DELTA base is missing from the current pack — the C# port
    /// instead delegates here, making packs with cross-pack REF_DELTA references
    /// (common in corrupt packs from failed fetches, or in repos packed with
    /// <c>git repack</c>) readable.
    /// </para>
    /// <para>
    /// <b>Recursion safety</b>: resolving a cross-pack base may trigger another
    /// cross-pack resolution if the base itself is a delta in another pack. The
    /// <see cref="_crossPackDepth"/> AsyncLocal counter bounds total recursion
    /// to <see cref="PackFile.MaxDeltaDepth"/> (50) levels, preventing infinite
    /// loops on corrupt packs with circular delta references.
    /// </para>
    /// </remarks>
    /// <param name="id">The REF_DELTA base OID to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The base object data, or <c>null</c> if not found in any pack.</returns>
    private async Task<RawObjectData?> ResolveCrossPackBaseAsync(GitOid id, CancellationToken cancellationToken)
    {
        int depth = _crossPackDepth.Value;
        if (depth >= PackFile.MaxDeltaDepth)
        {
            return null;
        }

        _crossPackDepth.Value = depth + 1;
        try
        {
            foreach (PackFile pack in _packs)
            {
                try
                {
                    RawObjectData? data = await pack.ReadAsync(id, cancellationToken).ConfigureAwait(false);
                    if (data is not null)
                    {
                        return data;
                    }
                }
                catch (GitException)
                {
                    // The pack's idx claims the OID but the content is
                    // unreadable (e.g. its own base chain is broken) — try the
                    // remaining packs for the base.
                }
            }

            return null;
        }
        finally
        {
            _crossPackDepth.Value = depth;
        }
    }

    /// <inheritdoc/>
    public async Task<RawObjectData?> ReadAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return null;
        }

        // C (pack_backend__read, odb_pack.c:588-600): pack_entry_find picks the
        // FIRST pack whose .idx claims the OID (midx → last-found → packs in
        // mtime order), then git_packfile_unpack runs. If unpacking fails the
        // error propagates immediately — a "good copy" in a later pack is
        // never consulted.
        PackFile? pack = await FindPackForOidAsync(id, cancellationToken).ConfigureAwait(false);
        return pack is null ? null : await pack.ReadAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PackFile?> FindPackForOidAsync(GitOid id, CancellationToken cancellationToken)
    {
        // Multi-pack-index fast path: O(log N) lookup across all packs.
        if (_midx is not null && _midx.FindEntry(id) is { } midxEntry)
        {
            if (await GetMidxPackAsync(midxEntry.PackIndex, cancellationToken).ConfigureAwait(false) is { } midxPack)
            {
                _lastFound = midxPack;
                return midxPack;
            }
        }

        // Try last-found first (optimization — matches pack_entry_find).
        if (_lastFound is { } last && last.Exists(id))
        {
            return last;
        }

        foreach (PackFile pack in _packs)
        {
            if (pack.Exists(id))
            {
                _lastFound = pack;
                return pack;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<GitObjectHeader?> ReadHeaderAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return null;
        }

        // C (pack_backend__read_header, odb_pack.c:555-567): same
        // fail-on-first-claiming-pack semantics as ReadAsync.
        PackFile? pack = await FindPackForOidAsync(id, cancellationToken).ConfigureAwait(false);
        return pack is null ? null : await pack.ReadHeaderAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool Exists(GitOid id)
    {
        if (_disposed)
        {
            return false;
        }

        // Multi-pack-index fast path.
        if (_midx is not null && _midx.FindEntry(id) is not null)
        {
            return true;
        }

        if (_lastFound is { } last && last.Exists(id))
        {
            return true;
        }

        foreach (PackFile pack in _packs)
        {
            if (pack.Exists(id))
            {
                _lastFound = pack;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public ValueTask<(bool Found, GitOid Oid)> ExistsPrefixAsync(GitOid prefix, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return ValueTask.FromResult<(bool, GitOid)>((false, default));
        }

        GitOid? match = null;
        foreach (PackFile pack in _packs)
        {
            if (!pack.ExistsPrefix(prefix, out GitOid packFound))
            {
                continue;
            }

            if (match is not null && !match.Value.Equals(packFound))
            {
                throw new GitException(
                    GitErrorCode.Ambiguous,
                    "abbreviated OID matches multiple pack entries across packs",
                    GitErrorCategory.Odb);
            }

            match = packFound;
        }

        if (match is { } oid)
        {
            return ValueTask.FromResult<(bool, GitOid)>((true, oid));
        }

        return ValueTask.FromResult<(bool, GitOid)>((false, default));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<GitOid> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            yield break;
        }

        // C (odb_pack.c pack_backend__foreach): yields every entry of every
        // pack with NO dedup — an OID duplicated across packs is reported
        // multiple times.
        // The ODB-level enumerator contract is per-backend-entry, not
        // unique-OID; the previous HashSet collapsed duplicates.
        foreach (PackFile pack in _packs)
        {
            foreach (GitOid oid in pack.Enumerate())
            {
                yield return oid;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        foreach (PackFile pack in _packs)
        {
            pack.Dispose();
        }

        foreach (PackFile pack in _midxPacks.Values)
        {
            pack.Dispose();
        }

        _packs.Clear();
        _midxPacks.Clear();
        return ValueTask.CompletedTask;
    }
}

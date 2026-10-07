// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

using Microsoft.Win32.SafeHandles;

namespace LibGit2CS.Pack;

/// <summary> Pack file reader. Managed port of libgit2's <c>src/libgit2/pack.c</c> (read side). </summary> <remarks> <para> Handles: pack open/validate, object header unpacking, OFS_DELTA/REF_DELTA chain resolution, delta application. </para> <para> <b>Varint —
/// OFS_DELTA encoding</b>: see <see cref="GitDeltaApplier"/>. The offset is a continuation-bit varint with +1 per continuation byte (not
/// the same as <c>util/varint.c</c>). </para> <para> <b>Delta depth</b>: the C reader has NO depth limit (<c>pack.c:596</c> walks <c>while
/// (true)</c>; the 50-limit is the writer-side <c>GIT_PACK_DEPTH</c>). Chain walks are iterative; only the cross-pack REF_DELTA resolver keeps <see
/// cref="MaxDeltaDepth"/> as an anti-cycle bound. </para> <para> <b>Per-Repository instances</b> (by design): no global pack cache. Each
/// <see cref="LibGit2CS.Repository.GitRepository"/> owns its own <see cref="PackFile"/> instances. </para> <para> <b>Async IO</b>: pack content is read with
/// file-positioned async reads
/// (<see cref="File.OpenHandle"/> + <see cref="RandomAccess.ReadAsync(SafeFileHandle, Memory{byte}, long, CancellationToken)"/>) on a single <see
/// cref="SafeFileHandle"/> held for the lifetime of the pack. <see cref="OpenAsync"/> performs the file open; every per-object read (<see cref="ReadAsync"/>,
/// <see cref="ReadHeaderAsync"/>) is genuinely async IO. Index-only operations (<see cref="Exists"/>, <see cref="ExistsPrefix"/>, <see cref="Enumerate"/>) stay
/// sync — they never touch the pack window. Disposal is synchronous — closes the <see cref="SafeFileHandle"/> and disposes the in-memory index.
/// </para> <para> <b>Accepted divergence from libgit2</b>: the C reference reads packs through a sliding-window mmap layer (<c>src/libgit2/mwindow.c</c>) and
/// materializes every window read into a fresh buffer. This port instead reads the pack via file-positioned async IO on the handle — there is no mwindow
/// equivalent class, and every read writes into a caller-managed buffer (see <see cref="RandomAccess.ReadAsync(SafeFileHandle, Memory{byte}, long,
/// CancellationToken)"/>). Trade-off: the C reader relies on OS page-cache zero-copy, while the port pays explicit syscalls per read and reuses buffers (<see
/// cref="ArrayPool{T}.Shared"/>) rather than mapping pages. This is intentional in favor of a purely-async, AOT-clean IO model. </para> </remarks>
internal sealed class PackFile : IDisposable
{
    /// <summary> Maximum cross-pack REF_DELTA resolution depth (writer-side <c>GIT_PACK_DEPTH</c>-inspired bound; the C reader has no depth limit). Used only
    /// by <see cref="PackObjectBackend"/>'s AsyncLocal guard against infinite cross-pack recursion on cyclic packs. </summary>
    internal const int MaxDeltaDepth = 50;

    private const uint PackSignature = 0x5041434b; // "PACK"
    private const int PackHeaderSize = 12;

    private readonly string _packPath;
    private readonly SafeFileHandle _packHandle;
    private readonly GitPackIndex _index;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;
    private readonly long _fileSize;
    private readonly long _numObjects;
    private readonly Func<GitOid, CancellationToken, Task<RawObjectData?>>? _crossPackBaseResolver;
    private bool _disposed;

    private PackFile(
        string packPath,
        SafeFileHandle packHandle,
        long fileSize,
        GitPackIndex index,
        GitHashAlgorithmKind algorithm,
        Func<GitOid, CancellationToken, Task<RawObjectData?>>? crossPackBaseResolver = null)
    {
        _packPath = packPath;
        _packHandle = packHandle;
        _index = index;
        _algorithm = algorithm;
        _oidSize = GitOid.SizeFor(algorithm);
        _fileSize = fileSize;
        _numObjects = index.ObjectCount;
        _crossPackBaseResolver = crossPackBaseResolver;
    }

    /// <summary>The number of objects in this pack.</summary>
    public int ObjectCount => _index.ObjectCount;

    /// <summary>The hash algorithm used by this pack.</summary>
    public GitHashAlgorithmKind Algorithm => _algorithm;

    /// <summary>
    /// The path of this pack's <c>.idx</c> file (sibling of <c>_packPath</c>).
    /// Used by <see cref="PackObjectBackend.WriteMultiPackIndexAsync"/> to feed each
    /// pack's index to <see cref="MultiPackIndexWriter"/>.
    /// </summary>
    internal string IdxPath => Path.ChangeExtension(_packPath, ".idx");

    /// <summary>
    /// The path of this pack's <c>.pack</c> file. Used by
    /// <see cref="PackObjectBackend"/> to sort packs by mtime (C's
    /// packfile_sort__cb reads the .pack's mtime, pack.c:1227).
    /// </summary>
    internal string PackPath => _packPath;

    /// <summary>
    /// Opens a pack file at <paramref name="packPath"/> (the <c>.pack</c> file).
    /// The corresponding <c>.idx</c> file must exist alongside it.
    /// Matches <c>packfile_open_locked</c>.
    /// </summary>
    /// <param name="packPath">Path to the <c>.pack</c> file.</param>
    /// <param name="algorithm">Hash algorithm (SHA-1 or SHA-256).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="crossPackBaseResolver">Optional callback to resolve REF_DELTA
    /// bases that reside in other packs or as loose objects. When set,
    /// <see cref="ReadAsync"/> and <see cref="ReadHeaderAsync"/> delegate to this callback
    /// instead of throwing when a REF_DELTA base is not in this pack.</param>
    public static async Task<PackFile> OpenAsync(
        string packPath,
        GitHashAlgorithmKind algorithm,
        CancellationToken cancellationToken,
        Func<GitOid, CancellationToken, Task<RawObjectData?>>? crossPackBaseResolver = null)
    {
        string idxPath = Path.ChangeExtension(packPath, ".idx");
        if (!File.Exists(idxPath))
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"pack index not found: {idxPath}",
                GitErrorCategory.Odb);
        }

        // The .pack file is opened once and read with file-positioned async IO
        // (RandomAccess) for the lifetime of the PackFile — accepted divergence
        // from libgit2's mmap windowing, see the class remarks.
        SafeFileHandle packHandle = File.OpenHandle(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long fileSize = RandomAccess.GetLength(packHandle);
        GitPackIndex index = await GitPackIndex.OpenAsync(idxPath, algorithm, cancellationToken).ConfigureAwait(false);

        // Validate the pack header: "PACK" + version 2 + num_objects.
        await ValidatePackHeaderAsync(packHandle, fileSize, index, cancellationToken).ConfigureAwait(false);

        return new PackFile(packPath, packHandle, fileSize, index, algorithm, crossPackBaseResolver);
    }

    /// <summary>
    /// Validates the pack file header (PACK signature, version 2, object count
    /// matches index). Matches <c>packfile_open_locked</c> header validation.
    /// </summary>
    private static async Task ValidatePackHeaderAsync(
        SafeFileHandle packHandle,
        long fileSize,
        GitPackIndex index,
        CancellationToken cancellationToken)
    {
        if (fileSize < PackHeaderSize)
        {
            throw new GitException(GitErrorCode.Error, "pack file too small", GitErrorCategory.Odb);
        }

        byte[] header = new byte[PackHeaderSize];
        await ReadExactAsync(packHandle, header, 0, cancellationToken).ConfigureAwait(false);

        uint sig = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (sig != PackSignature)
        {
            throw new GitException(GitErrorCode.Error, "invalid pack signature", GitErrorCategory.Odb);
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        if (version != 2)
        {
            throw new GitException(GitErrorCode.Error, $"unsupported pack version {version}", GitErrorCategory.Odb);
        }

        uint numObjects = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (numObjects != (uint)index.ObjectCount)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"pack object count {numObjects} doesn't match index {index.ObjectCount}",
                GitErrorCategory.Odb);
        }

        // C (pack.c:1135-1144, packfile_open_locked): the pack's trailing
        // checksum (last oid_size bytes) must match the pack checksum embedded
        // in the .idx (index_map.len - 2*oid_size). A mismatch fails the open.
        byte[] idxChecksum = index.PackChecksum().RawBytes.ToArray();
        byte[] trailer = new byte[idxChecksum.Length];
        await ReadExactAsync(packHandle, trailer, fileSize - idxChecksum.Length, cancellationToken).ConfigureAwait(false);
        if (!trailer.AsSpan().SequenceEqual(idxChecksum))
        {
            throw new GitException(
                GitErrorCode.Error,
                "invalid packfile",
                GitErrorCategory.Odb);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes at
    /// <paramref name="offset"/> into <paramref name="buffer"/>, mapping a
    /// short read to C's GIT_EBUFS ("buffer too small", GIT_ERROR_ODB —
    /// pack.c:429-432).
    /// </summary>
    private static async Task ReadExactAsync(SafeFileHandle packHandle, Memory<byte> buffer, long offset, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await RandomAccess.ReadAsync(packHandle, buffer[total..], offset + total, cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new GitException(GitErrorCode.BufferTooShort, "buffer too small", GitErrorCategory.Odb);
            }

            total += n;
        }
    }

    /// <summary>
    /// Looks up an object by OID. Matches <c>git_pack_entry_find</c> +
    /// <c>git_packfile_unpack</c>.
    /// </summary>
    /// <returns>The raw object data, or null if not found.</returns>
    public async Task<RawObjectData?> ReadAsync(GitOid id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        GitPackIndexLookupResult result = _index.FindIndex(id);
        if (!result.Found)
        {
            return null;
        }

        long offset = _index.GetObjectOffset(result.Index);
        return await UnpackAtOffsetAsync(offset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads only the header (type + size) for <paramref name="id"/>.
    /// Matches <c>git_packfile_resolve_header</c>.
    /// </summary>
    public async Task<GitObjectHeader?> ReadHeaderAsync(GitOid id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        GitPackIndexLookupResult result = _index.FindIndex(id);
        if (!result.Found)
        {
            return null;
        }

        long offset = _index.GetObjectOffset(result.Index);
        (GitObjectType type, long size) = await ResolveHeaderAsync(offset, cancellationToken).ConfigureAwait(false);
        return new GitObjectHeader(type, size);
    }

    /// <summary>Checks if <paramref name="id"/> exists in this pack.</summary>
    public bool Exists(GitOid id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _index.FindIndex(id).Found;
    }

    /// <summary>
    /// Finds the full OID for an abbreviated prefix.
    /// </summary>
    public bool ExistsPrefix(GitOid prefix, out GitOid found)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GitPackIndexLookupResult result = _index.FindIndex(prefix, prefix.HexLength);
        if (result.Ambiguous)
        {
            throw new GitException(
                GitErrorCode.Ambiguous,
                "abbreviated OID matches multiple pack entries",
                GitErrorCategory.Odb);
        }

        if (!result.Found)
        {
            found = default;
            return false;
        }

        found = _index.GetOid(result.Index);
        return true;
    }

    /// <summary>Enumerates all OIDs in this pack.</summary>
    public IEnumerable<GitOid> Enumerate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _index.EnumerateOids();
    }

    /// <summary> Unpacks the object at <paramref name="offset"/>. If it's a delta, resolves the base and applies the delta. Matches <c>git_packfile_unpack</c>.
    /// Iterative: the delta chain is collected on an explicit stack and applied base-first, mirroring C's <c>pack_dependency_chain</c> — the C reader has NO
    /// depth limit (pack.c:596 walks <c>while (true)</c>; the 50-limit is writer-side <c>GIT_PACK_DEPTH</c>), so valid deep chains (e.g. <c>git repack
    /// --depth=100</c>) must not be rejected. Cyclic chains (self- or mutually-referential deltas, which make C hang) are detected via the visited-offset set
    /// and rejected. </summary>
    private async Task<RawObjectData> UnpackAtOffsetAsync(long offset, CancellationToken cancellationToken)
    {
        // Chain elements: metadata only — where each delta body lives in the
        // pack. Each level is re-inflated on demand during the unwind below,
        // so a 1000-deep chain of 1 MB deltas never pins ~1 GB of
        // decompressed bodies; C holds one delta + base (pack_dependency_chain
        // stores only {offset, size, type} metadata, pack.c:583-663, and
        // git_packfile_unpack inflates each delta during the unwind,
        // pack.c:766-817).
        List<(long DeltaBodyOffset, long DeclaredSize)> chain = [];
        var visited = new HashSet<long>();

        long curOffset = offset;
        while (true)
        {
            // A valid pack's chain never revisits an object; a repeat is a
            // corrupt cyclic chain (C would loop forever; cycle detection is
            // the safety net, without capping legitimate deep chains).
            if (!visited.Add(curOffset))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "delta chain contains a cycle",
                    GitErrorCategory.Odb);
            }

            (GitObjectType type, long size, int headerLen) = await UnpackHeaderAsync(curOffset, cancellationToken).ConfigureAwait(false);

            switch (type)
            {
                case GitObjectType.Commit:
                case GitObjectType.Tree:
                case GitObjectType.Blob:
                case GitObjectType.Tag:
                    {
                        // Non-delta: decompress the body, then unwind the
                        // chain applying deltas base-first (re-inflating each
                        // delta body on demand).
                        byte[] body = await DecompressBodyAsync(curOffset + headerLen, size, cancellationToken).ConfigureAwait(false);
                        for (int i = chain.Count - 1; i >= 0; i--)
                        {
                            (long deltaBodyOffset, long declaredSize) = chain[i];
                            byte[] delta = await DecompressBodyAsync(deltaBodyOffset, declaredSize, cancellationToken).ConfigureAwait(false);
                            body = GitDeltaApplier.Apply(body, delta);
                        }

                        return new RawObjectData(type, body.Length, body);
                    }

                case GitObjectType.OfsDelta:
                    {
                        // OFS_DELTA: base is at a negative offset within the same pack.
                        (long baseOffset, int ofsHeaderLen) = await ReadOfsDeltaBaseAsync(curOffset + headerLen, curOffset, cancellationToken).ConfigureAwait(false);
                        chain.Add((curOffset + headerLen + ofsHeaderLen, size));
                        curOffset = baseOffset;
                        continue;
                    }

                case GitObjectType.RefDelta:
                    {
                        // REF_DELTA: base is referenced by OID (may be in another pack or loose).
                        GitOid baseOid = await ReadRefDeltaBaseAsync(curOffset + headerLen, cancellationToken).ConfigureAwait(false);
                        chain.Add((curOffset + headerLen + _oidSize, size));

                        // Try same-pack first.
                        GitPackIndexLookupResult baseResult = _index.FindIndex(baseOid);
                        if (baseResult.Found)
                        {
                            long baseOffset = _index.GetObjectOffset(baseResult.Index);
                            curOffset = baseOffset;
                            continue;
                        }

                        // Cross-pack REF_DELTA: resolve the base from other
                        // packs / loose objects via the callback (which has
                        // its own AsyncLocal cycle guard in PackObjectBackend).
                        if (_crossPackBaseResolver is not null)
                        {
                            RawObjectData? externalBase = await _crossPackBaseResolver(baseOid, cancellationToken).ConfigureAwait(false);
                            if (externalBase.HasValue)
                            {
                                byte[] body = externalBase.Value.Data;
                                for (int i = chain.Count - 1; i >= 0; i--)
                                {
                                    (long deltaBodyOffset, long declaredSize) = chain[i];
                                    byte[] delta = await DecompressBodyAsync(deltaBodyOffset, declaredSize, cancellationToken).ConfigureAwait(false);
                                    body = GitDeltaApplier.Apply(body, delta);
                                }

                                return new RawObjectData(externalBase.Value.Type, body.Length, body);
                            }
                        }

                        throw new GitException(
                            GitErrorCode.NotFound,
                            $"REF_DELTA base {baseOid} not found in pack",
                            GitErrorCategory.Odb);
                    }

                default:
                    throw new GitException(
                        GitErrorCode.Error,
                        $"unknown pack object type {type} at offset {offset}",
                        GitErrorCategory.Odb);
            }
        }
    }

    /// <summary> Resolves the final (non-delta) type and the delta result size of the object at <paramref name="offset"/>. Matches
    /// <c>git_packfile_resolve_header</c>: for a delta object, the reported size is the delta's RESULT size (read from the decompressed delta header via
    /// <c>git_delta_read_header_fromstream</c>, pack.c:540-552), and the chain is walked only to recover the final type. Iterative with no depth cap (C walks
    /// <c>while (type == OFS/REF)</c>). </summary>
    private async Task<(GitObjectType Type, long Size)> ResolveHeaderAsync(long offset, CancellationToken cancellationToken)
    {
        (GitObjectType type, long size, int headerLen) = await UnpackHeaderAsync(offset, cancellationToken).ConfigureAwait(false);
        long resultSize = size;
        long baseOffset = 0;

        if (type is GitObjectType.OfsDelta or GitObjectType.RefDelta)
        {
            if (type == GitObjectType.OfsDelta)
            {
                (baseOffset, int ofsHeaderLen) = await ReadOfsDeltaBaseAsync(offset + headerLen, offset, cancellationToken).ConfigureAwait(false);
                byte[] delta = await DecompressBodyAsync(offset + headerLen + ofsHeaderLen, size, cancellationToken).ConfigureAwait(false);
                (_, resultSize) = GitDeltaApplier.ReadHeader(delta);
            }
            else
            {
                GitOid baseOid = await ReadRefDeltaBaseAsync(offset + headerLen, cancellationToken).ConfigureAwait(false);
                byte[] delta = await DecompressBodyAsync(offset + headerLen + _oidSize, size, cancellationToken).ConfigureAwait(false);
                (_, resultSize) = GitDeltaApplier.ReadHeader(delta);

                // The type-walk needs the base offset; resolve it the same
                // way C's while loop does (get_delta_base).
                GitPackIndexLookupResult baseResult = _index.FindIndex(baseOid);
                if (!baseResult.Found && _crossPackBaseResolver is not null)
                {
                    // Cross-pack base: the type comes from the resolved base
                    // object; C requires the base in the same pack for the
                    // header walk, the port extends via the resolver.
                    RawObjectData? externalBase = await _crossPackBaseResolver(baseOid, cancellationToken).ConfigureAwait(false);
                    if (externalBase.HasValue)
                    {
                        return (externalBase.Value.Type, resultSize);
                    }
                }

                if (!baseResult.Found)
                {
                    throw new GitException(
                        GitErrorCode.NotFound,
                        $"REF_DELTA base {baseOid} not found",
                        GitErrorCategory.Odb);
                }

                baseOffset = _index.GetObjectOffset(baseResult.Index);
            }
        }

        // Walk the chain to recover the final type (pack.c:556-563). A repeated offset is a corrupt cyclic chain (C loops forever); reject it (safety net).
        var visited = new HashSet<long> { offset };
        while (type is GitObjectType.OfsDelta or GitObjectType.RefDelta)
        {
            long curpos = baseOffset;
            if (!visited.Add(curpos))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "delta chain contains a cycle",
                    GitErrorCategory.Odb);
            }

            (type, size, headerLen) = await UnpackHeaderAsync(curpos, cancellationToken).ConfigureAwait(false);
            if (type is not (GitObjectType.OfsDelta or GitObjectType.RefDelta))
            {
                break;
            }

            if (type == GitObjectType.OfsDelta)
            {
                (baseOffset, int _) = await ReadOfsDeltaBaseAsync(curpos + headerLen, curpos, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                GitOid baseOid = await ReadRefDeltaBaseAsync(curpos + headerLen, cancellationToken).ConfigureAwait(false);
                GitPackIndexLookupResult baseResult = _index.FindIndex(baseOid);
                if (!baseResult.Found && _crossPackBaseResolver is not null)
                {
                    RawObjectData? externalBase = await _crossPackBaseResolver(baseOid, cancellationToken).ConfigureAwait(false);
                    if (externalBase.HasValue)
                    {
                        return (externalBase.Value.Type, resultSize);
                    }
                }

                if (!baseResult.Found)
                {
                    throw new GitException(
                        GitErrorCode.NotFound,
                        $"REF_DELTA base {baseOid} not found",
                        GitErrorCategory.Odb);
                }

                baseOffset = _index.GetObjectOffset(baseResult.Index);
            }
        }

        return (type, resultSize);
    }

    /// <summary>
    /// Unpacks the pack object header at <paramref name="offset"/>. Returns the
    /// type, size, and number of bytes consumed by the header. Matches
    /// <c>git_packfile_unpack_header</c>.
    /// </summary>
    /// <remarks>
    /// Pack object header encoding (varint): first byte is
    /// <c>(type &lt;&lt; 4) | (size &amp; 15)</c>, with MSB=1 meaning continuation.
    /// Subsequent bytes give 7 more size bits each, MSB=1 for continuation.
    /// </remarks>
    private async Task<(GitObjectType Type, long Size, int HeaderLength)> UnpackHeaderAsync(long offset, CancellationToken cancellationToken)
    {
        // Ten bytes can encode the size; the eleventh is needed to preserve
        // the bounds-before-overflow check for corrupt continuation chains.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(11);
        try
        {
            int count = await ReadPackPrefixAsync(offset, buffer.AsMemory(0, 11), cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> header = buffer.AsSpan(0, count);
            int pos = 0;
            byte b = ReadHeaderByte(header, ref pos);
            var type = (GitObjectType)((b >> 4) & 7);
            long size = b & 15;
            int shift = 4;

            while ((b & 0x80) != 0)
            {
                // Bounds must be checked before the shift overflow (pack.c:428-438).
                b = ReadHeaderByte(header, ref pos);
                if (shift >= 64)
                {
                    throw new GitException(GitErrorCode.Error, "header length is zero", GitErrorCategory.Odb);
                }

                size |= (long)(b & 0x7f) << shift;
                shift += 7;
            }

            return (type, size, pos);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a bounded header prefix, handling short reads. Parsing checks how
    /// many bytes it actually needs, so a valid short header near EOF still works.
    /// </summary>
    private async Task<int> ReadPackPrefixAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || offset >= _fileSize)
        {
            throw new GitException(GitErrorCode.BufferTooShort, "buffer too small", GitErrorCategory.Odb);
        }

        int length = (int)Math.Min(buffer.Length, _fileSize - offset);
        int total = 0;
        while (total < length)
        {
            int read = await RandomAccess.ReadAsync(_packHandle, buffer.Slice(total, length - total), offset + total, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte ReadHeaderByte(ReadOnlySpan<byte> header, ref int pos)
    {
        if (pos >= header.Length)
        {
            throw new GitException(GitErrorCode.BufferTooShort, "buffer too small", GitErrorCategory.Odb);
        }

        return header[pos++];
    }

    /// <summary>
    /// Reads the OFS_DELTA base offset. Matches <c>get_delta_base</c> (varint).
    /// The encoding is a continuation-bit varint with +1 per continuation byte.
    /// </summary>
    private async Task<(long BaseOffset, int BytesConsumed)> ReadOfsDeltaBaseAsync(long pos, long deltaObjOffset, CancellationToken cancellationToken)
    {
        // A 64-bit offset uses at most ten bytes; longer encodings overflow
        // before another byte is read. Each operation owns its rental.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(10);
        try
        {
            int count = await ReadPackPrefixAsync(pos, buffer.AsMemory(0, 10), cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> header = buffer.AsSpan(0, count);
            int used = 0;
            byte b = ReadHeaderByte(header, ref used);
            ulong unsignedBaseOffset = (ulong)(b & 127);

            while ((b & 128) != 0)
            {
                unsignedBaseOffset += 1;
                if (unsignedBaseOffset == 0 || (unsignedBaseOffset & (~0UL << 57)) != 0)
                {
                    throw new GitException(GitErrorCode.Error, "OFS_DELTA offset overflow", GitErrorCategory.Odb);
                }

                b = ReadHeaderByte(header, ref used);
                unsignedBaseOffset = (unsignedBaseOffset << 7) + (ulong)(b & 127);
            }

            // Keep unsigned arithmetic so wrapped offsets cannot point forward.
            if (unsignedBaseOffset == 0 || (ulong)deltaObjOffset <= unsignedBaseOffset)
            {
                throw new GitException(GitErrorCode.Error, "OFS_DELTA base offset out of bounds", GitErrorCategory.Odb);
            }

            return (deltaObjOffset - (long)unsignedBaseOffset, used);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads the REF_DELTA base OID (raw bytes at the current position).
    /// </summary>
    private async Task<GitOid> ReadRefDeltaBaseAsync(long pos, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[_oidSize];
        await RandomAccess.ReadAsync(_packHandle, bytes, pos, cancellationToken).ConfigureAwait(false);
        return GitOid.FromRaw(bytes, _algorithm);
    }

    /// <summary>
    /// Decompresses the zlib-framed pack object body starting at
    /// <paramref name="offset"/>, expecting <paramref name="expectedSize"/> bytes.
    /// </summary>
    /// <remarks>
    /// <b>Delta bases</b>: pack object bodies use zlib-framed compression
    /// (2-byte zlib header + raw DEFLATE + 4-byte Adler-32 trailer), the same
    /// format as loose objects. Since the exact compressed size is unknown
    /// upfront, we read incrementally growing windows and let
    /// <see cref="ZLibDecoder"/> stop at the end of the stream via
    /// <see cref="LibGit2CS.Core.Compression.Zlib.DecompressPackObject(System.ReadOnlySpan{byte})"/>.
    /// </remarks>
    private async Task<byte[]> DecompressBodyAsync(long offset, long expectedSize, CancellationToken cancellationToken)
    {
        // Reads start small and grow; a read scaled off the attacker-
        // controlled declared size would allocate up to the whole remaining
        // pack tail (and expectedSize*3+1024 can wrap for a crafted header
        // size ≥ ~3.07e18, where C's GIT_ERROR_CHECK_ALLOC_ADD errors
        // cleanly, pack.c:899). C feeds zlib whole mmap windows at zero copy
        // (lazy OS
        // paging, pack.c:886-957); the port materializes every window it
        // reads, so it starts at 4 KiB and doubles on NeedMoreData, keeping
        // each read bounded (≤ 1 MiB) regardless of the declared size.
        const int MaxWindowSize = 1024 * 1024;
        const int InitialWindowSize = 4096;

        // C (pack.c:899): GIT_ERROR_CHECK_ALLOC_ADD(&buffer_len, size, 1) —
        // sizes that cannot be represented fail with a clean error, and the
        // port cannot materialize > int.MaxValue bodies in a byte[] anyway.
        if (expectedSize is < 0 or >= int.MaxValue)
        {
            throw new GitException(GitErrorCode.Error, "error inflating zlib stream", GitErrorCategory.Zlib);
        }

        // Keep the result exact-sized. Once full, drain into a separate guard
        // byte so oversized streams are still detected without copying the body.
        byte[] decompressed = new byte[(int)expectedSize];
        using var decoder = new ZLibDecoder();

        // The compressed input window is rented once (up to MaxWindowSize) and
        // reused across every window read — no per-window allocation.
        byte[] compressed = ArrayPool<byte>.Shared.Rent(MaxWindowSize);
        long pos = offset;
        int total = 0;
        byte[] guard = new byte[1];
        bool streamEnded = false;
        int windowSize = InitialWindowSize;
        try
        {
            while (!streamEnded)
            {
                int available = (int)Math.Min(windowSize, _fileSize - pos);
                if (available <= 0)
                {
                    // No more input before the stream ended — truncated.
                    throw new GitException(
                        GitErrorCode.Error,
                        $"failed to decompress pack object body at offset {offset}: truncated zlib stream",
                        GitErrorCategory.Zlib);
                }

                // Read the full window (loop until filled — RandomAccess may
                // short-read). `available` never exceeds the remaining file,
                // so EOF here means truncation.
                int n = 0;
                while (n < available)
                {
                    int r = await RandomAccess.ReadAsync(_packHandle, compressed.AsMemory(n, available - n), pos + n, cancellationToken).ConfigureAwait(false);
                    if (r <= 0)
                    {
                        throw new GitException(
                            GitErrorCode.Error,
                            $"failed to decompress pack object body at offset {offset}: truncated zlib stream",
                            GitErrorCategory.Zlib);
                    }

                    n += r;
                }

                pos += n;

                int consumed = 0;
                while (consumed < n)
                {
                    Span<byte> dst = total < expectedSize ? decompressed.AsSpan(total) : guard;
                    OperationStatus status = decoder.Decompress(compressed.AsSpan(consumed, n - consumed), dst, out int c, out int written);
                    consumed += c;
                    total += written;
                    if (total > expectedSize)
                    {
                        throw new GitException(GitErrorCode.Error, "error inflating zlib stream", GitErrorCategory.Zlib);
                    }

                    if (status == OperationStatus.Done)
                    {
                        streamEnded = true;
                        break;
                    }

                    if (status == OperationStatus.InvalidData)
                    {
                        throw new GitException(
                            GitErrorCode.Error,
                            $"failed to decompress pack object body at offset {offset}: corrupt zlib stream",
                            GitErrorCategory.Zlib);
                    }

                    if (status == OperationStatus.NeedMoreData)
                    {
                        // Window exhausted — rewind any unconsumed tail (the
                        // decoder retains state but not input) and grow the
                        // window exponentially for the next read.
                        pos -= n - consumed;
                        windowSize = Math.Min(windowSize * 2, MaxWindowSize);
                        break;
                    }

                    // DestinationTooSmall: no output space (budget full) or the
                    // decoder needs a bigger span — keep draining this window.
                    if (written == 0 && consumed == 0)
                    {
                        // C (pack.c:905-915): no progress means the stream
                        // outlives the declared size ("error inflating zlib
                        // stream") or is corrupt.
                        throw new GitException(GitErrorCode.Error, "error inflating zlib stream", GitErrorCategory.Zlib);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }

        // C (pack.c:938-942, packfile_unpack_compressed): the stream must
        // produce EXACTLY the size declared in the object header
        // ("error inflating zlib stream", GIT_ERROR_ZLIB).
        if (total != expectedSize)
        {
            throw new GitException(
                GitErrorCode.Error,
                "error inflating zlib stream",
                GitErrorCategory.Zlib);
        }

        return decompressed;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packHandle.Dispose();
        _index.Dispose();
    }
}

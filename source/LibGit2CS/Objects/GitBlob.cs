// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary>
/// A git blob (file content) object. Managed port of libgit2's
/// <c>src/libgit2/blob.c</c> (read side).
/// </summary>
/// <remarks>
/// Blobs are the simplest git object: the raw body is the content (no
/// structured header). <see cref="Content"/> is the same slice as
/// <see cref="GitObject.Raw"/>.
/// </remarks>
public sealed class GitBlob : GitObject
{
    private int? _isBinary;

    private GitBlob(GitRepository? owner, GitOid id, long size, ReadOnlyMemory<byte> raw)
        : base(owner, id, GitObjectType.Blob, size, raw)
    {
    }

    /// <summary>
    /// The blob's raw content. Equivalent to <see cref="GitObject.Raw"/>.
    /// Matches <c>git_blob_rawcontent</c> + <c>git_blob_rawsize</c>.
    /// </summary>
    public ReadOnlyMemory<byte> Content => Raw;

    /// <summary>
    /// True if this blob appears to be binary. Uses git core's heuristic:
    /// scan the first 8000 bytes for NUL, detect UTF-16/32 BOMs, and apply
    /// a <c>nonprintable &gt; printable/128</c> ratio check. Matches
    /// <c>git_blob_is_binary</c> / <c>git_str_is_binary</c>.
    /// </summary>
    public bool IsBinary
    {
        get
        {
            if (!_isBinary.HasValue)
            {
                // C's git_blob_is_binary (blob.c:395-406) caps the scan at GIT_FILTER_BYTES_TO_CHECK_NUL (8000); git_blob_data_is_binary (blob.c:409-416) scans
                // the FULL buffer.
                int scanLen = Math.Min(Raw.Length, MaxBinaryCheckBytes);
                _isBinary = IsBinaryBytes(Raw.Span[..scanLen]) ? 1 : 0;
            }

            return _isBinary.Value != 0;
        }
    }

    /// <summary>
    /// Parses a raw blob body (no header) into a <see cref="GitBlob"/>. The body
    /// is stored as-is — blobs have no structured fields. Matches
    /// <c>git_blob__parse_raw</c>.
    /// </summary>
    internal static GitBlob Parse(GitRepository? owner, GitOid id, ReadOnlyMemory<byte> raw)
        => new(owner, id, raw.Length, raw);

    /// <summary> Determines whether the given byte span contains binary content. Matches <c>git_blob_data_is_binary</c> + <c>git_str_is_binary</c>
    /// (<c>src/util/str.c:1253-1279</c>): the FULL buffer is scanned (the 8000-byte cap belongs to the blob-level <see cref="IsBinary"/> only). </summary>
    /// <remarks> <para> Detection rules (in order): <list type="number"> <item><description>UTF-16/UTF-32 BOM at start → binary (UTF-8 BOM alone is NOT
    /// binary).</description></item> <item><description>Any NUL byte in the scan window → binary.</description></item> <item><description>Else: count printable
    /// vs nonprintable bytes (whitespace is neither); binary if <c>nonprintable &gt; printable / 128</c>.</description></item> </list> </para> </remarks>
    public static bool IsBinaryBytes(ReadOnlySpan<byte> data)
    {
        // 1. UTF-16/32 BOM detection.
        if (HasUtf16Or32Bom(data))
        {
            return true;
        }

        // 2. NUL scan + printable/nonprintable ratio over the FULL buffer.
        long printable = 0L;
        long nonprintable = 0L;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0)
            {
                return true;
            }

            if (IsPrintable(b))
            {
                printable++;
            }
            else if (IsSpaceByte(b))
            {
                // whitespace is neither printable nor nonprintable — skip
            }
            else
            {
                nonprintable++;
            }
        }

        // git_str_is_binary: (printable >> 7) < nonprintable
        return (printable >> 7) < nonprintable;
    }

    private const int MaxBinaryCheckBytes = 8000;

    private static bool HasUtf16Or32Bom(ReadOnlySpan<byte> data)
        => data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF || // UTF-16 BE
           data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE || // UTF-16 LE
           data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF || // UTF-32 BE
           data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00;   // UTF-32 LE

    // Matches `git__isspace` in `git2/str.h`: space, tab, CR, LF, VT, FF.
    private static bool IsSpaceByte(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;

    // Matches `git__isprint`-ish for binary detection: bytes > 0x1F and != 0x7F (DEL),
    // plus the explicit printable control chars BS (0x08), ESC (0x1B), FF (0x0C).
    // FF (0x0C) was
    // missing, so it fell through to IsSpaceByte and was counted in neither
    // bucket — C counts it printable (str.c:1264-1276), flipping the
    // (printable >> 7) < nonprintable ratio for form-feed-heavy content.
    private static bool IsPrintable(byte b)
        => b is > 0x1F and not 0x7F or 0x08 or 0x1B or 0x0C;

    // ==============================
    // Write side
    // ==============================

    /// <summary> Creates a blob from a file in the repository's working directory, applying clean filters. Matches <c>git_blob_create_from_workdir</c>
    /// (<c>blob.c:272-276</c>). Byte-faithful: the <see cref="GitPath"/> overload is primary; the <c>string</c> overload delegates via <see
    /// cref="GitPath.FromUtf8String"/>. </summary>
    internal static async Task<GitOid> CreateFromWorkdirAsync(GitRepository repo, GitPath relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // Delegate to the existing internal helper (used by Stash/Index) and
        // discard the stat-derived IndexEntry — the public C API returns only
        // the OID. Matches git_blob_create_from_workdir → git_blob__create_from_paths.
        (GitOid oid, Index.GitIndexEntry _) = await BlobHelper.CreateFromWorkdirAsync(repo, relativePath, cancellationToken).ConfigureAwait(false);
        return oid;
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    internal static Task<GitOid> CreateFromWorkdirAsync(GitRepository repo, string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return CreateFromWorkdirAsync(repo, GitPath.FromUtf8String(relativePath), cancellationToken);
    }

    /// <summary>
    /// Creates a blob from a file anywhere on disk. Matches
    /// <c>git_blob_create_from_disk</c> (<c>blob.c:278-300</c>).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="absolutePath">
    /// The absolute path of the file to read. If the path is relative, it's
    /// resolved against the current working directory (matches C's
    /// <c>git_fs_path_prettify</c> behavior).
    /// </param>
    /// <returns>The OID of the newly created blob.</returns>
    /// <remarks>
    /// If <paramref name="absolutePath"/> is inside the repository's working
    /// directory, the workdir prefix is stripped to derive a filter hint path
    /// and clean filters are applied (matching C's <c>hintpath</c> logic at
    /// <c>blob.c:290-296</c>). If the path is outside the workdir, no filters
    /// are applied (the raw file content is hashed directly).
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<GitOid> CreateFromDiskAsync(GitRepository repo, string absolutePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(absolutePath);

        string fullPath = Path.GetFullPath(absolutePath);
        string? workdir = repo.Workdir;

        GitPath? hintPath = null;
        if (workdir is not null && fullPath.StartsWith(workdir, OperatingSystemIsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            // Ingress hook: the hint path is derived from the OS path by stripping the workdir prefix. Route through FromFileSystemString
            // with core.precomposeunicode (ports the hintpath derivation at blob.c:290-296 plus the per-call iconv pattern from git_reference__normalize_name
            // refs.c:942 — a one-shot read, no iterator to cache on). On non-macOS the transcode inside PrecomposeCore is a no-op.
            bool precompose = await repo.Config.GetBoolAsync(
                "core.precomposeunicode", false, cancellationToken).ConfigureAwait(false);
            hintPath = GitPath.FromFileSystemString(fullPath[workdir.Length..], precompose);
        }

        // If we have a hint path inside the workdir, delegate to the existing
        // internal helper (which applies filters + computes stat entry).
        // Otherwise (path outside workdir, or bare repo) read the raw bytes
        // and hash them directly — no filters, no stat. Matches C's
        // git_blob__create_from_paths(..., hintpath, 0, !!hintpath).
        if (hintPath is not null)
        {
            (GitOid oid, Index.GitIndexEntry _) = await BlobHelper.CreateFromWorkdirAsync(repo, hintPath.Value, cancellationToken).ConfigureAwait(false);
            return oid;
        }

        // C (blob.c:210-228, git_blob__create_from_paths): the file is lstat'ed (no follow) and a symlink — even one pointing at a directory or a broken target
        // — produces a blob of the LINK-TARGET path bytes (write_symlink, blob.c:163-180).
        if (TryReadLinkTarget(fullPath, out byte[] linkTarget))
        {
            return await repo.Objects.WriteAsync(GitObjectType.Blob, linkTarget, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(fullPath))
        {
            if (Directory.Exists(fullPath))
            {
                throw new GitException(
                    GitErrorCode.Directory,
                    $"cannot create blob from '{absolutePath}' — it is a directory",
                    GitErrorCategory.Odb);
            }

            throw new GitException(
                GitErrorCode.NotFound,
                $"cannot create blob from '{absolutePath}' — file does not exist",
                GitErrorCategory.Odb);
        }

        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return await repo.Objects.WriteAsync(GitObjectType.Blob, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a symlink's target path as raw bytes (p_readlink, blob.c:173).
    /// Returns false when <paramref name="path"/> is not a symlink. Works for
    /// broken links (readlink does not require the target to exist). Reading
    /// raw bytes preserves non-UTF-8 link targets (a re-encoded
    /// <see cref="System.IO.FileSystemInfo.LinkTarget"/> string would corrupt
    /// them).
    /// </summary>
    private static bool TryReadLinkTarget(string path, out byte[] targetBytes)
        => NativeStat.TryReadLinkTarget(path, out targetBytes);

    /// <summary>
    /// Opens a streaming blob writer. Matches <c>git_blob_create_from_stream</c>
    /// (<c>blob.c:333-371</c>).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="hintPath">
    /// Optional relative path used to load clean filters at
    /// <see cref="GitBlobWriteStream.CommitAsync"/> time. Pass null to skip filter
    /// application (e.g. for content from outside the workdir).
    /// </param>
    /// <returns>
    /// A <see cref="GitBlobWriteStream"/>; call <see cref="GitBlobWriteStream.CommitAsync"/>
    /// to finalize the blob and obtain its OID.
    /// </returns>
    internal static GitBlobWriteStream CreateWriteStream(GitRepository repo, string? hintPath = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return new GitBlobWriteStream(repo, hintPath);
    }

    private static bool OperatingSystemIsWindows()
        => OperatingSystem.IsWindows();
}

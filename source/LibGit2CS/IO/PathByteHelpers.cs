// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

/*
 * Based on the Android implementation, BSD licensed.
 * http://android.git.kernel.org/
 *
 * Copyright (C) 2008 The Android Open Source Project
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * * Redistributions of source code must retain the above copyright
 *   notice, this list of conditions and the following disclaimer.
 * * Redistributions in binary form must reproduce the above copyright
 *   notice, this list of conditions and the following disclaimer in
 *   the documentation and/or other materials provided with the
 *   distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * AS IS AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
 * COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
 * INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS
 * OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED
 * AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
 * OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System.Text;

namespace LibGit2CS.IO;

/// <summary> Byte-wise path manipulation helpers. Managed byte-port of the <c>src/util/fs_path.c</c> subset mirrored by <see cref="PathHelpers"/>, but
/// operating on <see cref="ReadOnlySpan{T}"/> (<c>byte</c>) and returning <see cref="GitPath"/>. This is the byte-faithful companion layer used as subsystems
/// migrate off the <c>string</c>/<c>char</c>-based <see cref="PathHelpers"/>. </summary> <remarks> Git internal paths always use forward slashes (<c>/</c>);
/// backslashes are accepted at the filesystem boundary and normalized to forward slashes by <see cref="MakePosix"/>. Root detection (<see cref="Dirname"/> /
/// <see cref="CommonDirLength"/>) recognises POSIX <c>/</c>, Windows drive letters <c>C:\</c>, and UNC <c>\\server\share</c>, matching <see
/// cref="PathHelpers"/>. </remarks>
internal static class PathByteHelpers
{
    /// <summary>
    /// True if <paramref name="path"/> is <c>"."</c> or <c>".."</c>. Byte-port
    /// of <see cref="PathHelpers.IsDotOrDotDot(ReadOnlySpan{char})"/>
    /// (<c>git_fs_path_is_dot_or_dotdot</c>).
    /// </summary>
    public static bool IsDotOrDotDot(ReadOnlySpan<byte> path)
        => path.Length == 1 && path[0] == (byte)'.'
        || (path.Length == 2 && path[0] == (byte)'.' && path[1] == (byte)'.');

    /// <summary>
    /// True if <paramref name="c"/> is a forward or backward slash. Byte-port of
    /// <see cref="PathHelpers.IsDirSeparator(char)"/>
    /// (<c>git_fs_path_is_dirsep</c>).
    /// </summary>
    public static bool IsDirSeparator(byte c) => c is (byte)'/' or (byte)'\\';

    /// <summary>
    /// Normalizes backslashes to forward slashes. Byte-port of
    /// <see cref="PathHelpers.MakePosix"/> (<c>git_fs_path_mkposix</c>).
    /// </summary>
    public static GitPath MakePosix(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty)
        {
            return default;
        }

        byte[] result = new byte[path.Length];
        for (int i = 0; i < path.Length; i++)
        {
            byte c = path[i];
            result[i] = c == (byte)'\\' ? (byte)'/' : c;
        }

        return GitPath.FromUtf8Bytes(result);
    }

    /// <summary>
    /// Joins two path components with a single separator. Byte-port of
    /// <see cref="PathHelpers.Join"/> (<c>git_str_join</c> with separator
    /// <c>'/'</c>, str.c:760-807): when <paramref name="a"/> is non-empty, ALL
    /// leading <c>'/'</c> of <paramref name="b"/> are skipped, a separator is
    /// inserted only when <paramref name="a"/> does not end with one, and only
    /// <c>'/'</c> counts as a separator (a backslash is a literal byte).
    /// </summary>
    public static GitPath Join(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.IsEmpty)
        {
            return ToOwned(b);
        }

        while (!b.IsEmpty && b[0] == (byte)'/')
        {
            b = b[1..];
        }

        if (a[^1] == (byte)'/')
        {
            return Concat(a, b);
        }

        return Concat3(a, [(byte)'/'], b);
    }

    /// <summary>
    /// Returns the directory part of <paramref name="path"/>. Byte-port of
    /// <see cref="PathHelpers.Dirname"/> (<c>git_fs_path_dirname</c>).
    /// </summary>
    public static GitPath Dirname(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty)
        {
            return GitPath.FromUtf8String(".");
        }

        ReadOnlySpan<byte> trimmed = TrimTrailingSeparators(path);
        int lastSep = trimmed.LastIndexOfAny([(byte)'/', (byte)'\\']);
        if (lastSep < 0)
        {
            return GitPath.FromUtf8String(".");
        }

        if (lastSep == 0)
        {
            return GitPath.FromUtf8String("/");
        }

        int rootLen = GetRootLengthInternal(trimmed);
        if (lastSep + 1 == rootLen)
        {
            return ToOwned(trimmed[..rootLen]);
        }

        return ToOwned(trimmed[..lastSep]);
    }

    /// <summary>
    /// Squashes runs of consecutive separators into a single separator. Byte-port
    /// of <see cref="PathHelpers.SquashSlashes"/>
    /// (<c>git_fs_path_squash_slashes</c>).
    /// </summary>
    public static GitPath SquashSlashes(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty)
        {
            return default;
        }

        var result = new List<byte>(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            byte c = path[i];
            if (IsDirSeparator(c))
            {
                result.Add((byte)'/');
                while (i + 1 < path.Length && IsDirSeparator(path[i + 1]))
                {
                    i++;
                }
            }
            else
            {
                result.Add(c);
            }
        }

        return GitPath.FromUtf8Bytes(result.ToArray());
    }

    /// <summary>
    /// Computes the common directory prefix length of two paths. Byte-port of
    /// <see cref="PathHelpers.CommonDirLength"/> (<c>git_fs_path_common_dirlen</c>):
    /// the last position where BOTH bytes are <c>'/'</c>, plus one; 0 when
    /// there is no shared <c>'/'</c>.
    /// </summary>
    public static int CommonDirLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        int dirsep = -1;

        for (int i = 0; i < minLen; i++)
        {
            if (a[i] == (byte)'/' && b[i] == (byte)'/')
            {
                dirsep = i;
            }
            else if (a[i] != b[i])
            {
                break;
            }
        }

        return dirsep < 0 ? 0 : dirsep + 1;
    }

    /// <summary>
    /// Returns the trailing basename component of <paramref name="path"/> as a
    /// zero-copy <see cref="GitPath"/> slice into an owned copy of
    /// <paramref name="path"/>. Byte-port of the
    /// <c>info->basename = strrchr(info->path, '/')</c> step in
    /// <c>git_attr_path__init</c> (<c>src/libgit2/attr_file.c:593-597</c>): if
    /// the path contains no <c>/</c>, the basename is the whole path; an empty
    /// basename falls back to the whole path (the <c>!info->basename || !*info->basename</c>
    /// guard).
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="GitPath"/> that owns its bytes (a copy of the input
    /// span). Callers that already hold a <see cref="GitPath"/> backing buffer
    /// may slice it directly via <see cref="GitPath.Span"/>.<see cref="MemoryExtensions.LastIndexOf{T}(ReadOnlySpan{T}, T)"/>
    /// for zero-copy basename extraction; this helper is for the common case
    /// where the caller has a transient span.
    /// </remarks>
    public static GitPath Basename(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty)
        {
            // C (fs_path.c:104-114): NULL/empty paths are treated as ".".
            return GitPath.FromUtf8String(".");
        }

        // Strip trailing separators (consistent with Dirname/Join).
        int end = path.Length;
        while (end > 1 && IsDirSeparator(path[end - 1]))
        {
            end--;
        }

        ReadOnlySpan<byte> trimmed = path[..end];
        int lastSep = trimmed.LastIndexOfAny([(byte)'/', (byte)'\\']);
        if (lastSep < 0)
        {
            return ToOwned(trimmed);
        }

        ReadOnlySpan<byte> basename = trimmed[(lastSep + 1)..];
        if (basename.IsEmpty)
        {
            return ToOwned(trimmed);
        }

        return ToOwned(basename);
    }

    /// <summary>
    /// Joins <paramref name="basePath"/> and <paramref name="path"/> and reports the
    /// byte offset into the result where the <paramref name="path"/> portion
    /// begins (the <c>root</c> out-parameter of
    /// <c>git_fs_path_join_unrooted</c>). Byte-port of
    /// <c>git_fs_path_join_unrooted</c> (<c>src/util/fs_path.c:329-358</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Semantics faithful to the C original: if <paramref name="path"/> has a
    /// root component (POSIX <c>/</c>, Windows drive <c>C:\</c>, or UNC
    /// <c>\\server\share</c>), it is taken as-is and the <paramref name="basePath"/>
    /// is ignored; <paramref name="rootOut"/> is the root length (0 if path is
    /// relative). If <paramref name="path"/> is relative and
    /// <paramref name="basePath"/> is non-empty, the two are joined with a
    /// separator and <paramref name="rootOut"/> is the length of
    /// <paramref name="basePath"/> (the path portion begins right after the base).
    /// </para>
    /// <para>
    /// Used by <see cref="AttrPath.Init(GitPath, GitPath, AttrPath.DirFlag)"/>
    /// to build <c>full</c> and locate the relative <c>path</c> pointer in one
    /// pass, mirroring <c>attr_file.c:575-578</c>.
    /// </para>
    /// </remarks>
    public static GitPath JoinUnrooted(ReadOnlySpan<byte> path, ReadOnlySpan<byte> basePath, out int rootOut)
    {
        // GetRootLengthInternal returns -1 for an unrooted path and the root
        // length (0 for a POSIX absolute path) otherwise, mirroring C's
        // git_fs_path_root. We map "no root" to root == 0 here.
        int root = GetRootLengthInternal(path);

        if (!basePath.IsEmpty && root < 0)
        {
            GitPath joined = Join(basePath, path);
            rootOut = basePath.Length;
            return joined;
        }

        rootOut = root < 0 ? 0 : root;
        return ToOwned(path);
    }

    private static GitPath ToOwned(ReadOnlySpan<byte> span)
        => GitPath.FromUtf8Bytes(span.ToArray());

    private static GitPath Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return GitPath.FromUtf8Bytes(result);
    }

    private static GitPath Concat3(ReadOnlySpan<byte> a, ReadOnlySpan<byte> mid, ReadOnlySpan<byte> b)
    {
        byte[] result = new byte[a.Length + mid.Length + b.Length];
        int offset = 0;
        a.CopyTo(result.AsSpan(offset, a.Length));
        offset += a.Length;
        mid.CopyTo(result.AsSpan(offset, mid.Length));
        offset += mid.Length;
        b.CopyTo(result.AsSpan(offset, b.Length));
        return GitPath.FromUtf8Bytes(result);
    }

    private static ReadOnlySpan<byte> TrimTrailingSeparators(ReadOnlySpan<byte> path)
    {
        int end = path.Length;
        while (end > 1 && IsDirSeparator(path[end - 1]))
        {
            end--;
        }

        return path[..end];
    }

    /// <summary>
    /// Byte-port of <c>git_fs_path_root</c> (<c>src/util/fs_path.c</c>): the
    /// byte offset of the root of the path, or -1 when the path is not
    /// rooted (C's sentinel; callers map it to 0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// POSIX filenames with drive/UNC shapes must NOT be treated as rooted
    /// paths (the whole path length for a UNC-shaped name without a second
    /// separator, or rooting <c>x:\\y</c> on POSIX, is wrong). The drive
    /// branch mirrors C's <c>dos_drive_prefix_length</c> (not WIN32-gated, no
    /// isalpha requirement) and only counts when a real separator follows;
    /// the UNC and trailing-backslash branches are WIN32-gated exactly like
    /// C.
    /// </para>
    /// </remarks>
    internal static int GetRootLengthInternal(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty)
        {
            return -1;
        }

        int offset = 0;

        // dos_drive_prefix_length (fs_path.c): 2 for an ASCII byte followed
        // by ':', 1+i for a high-bit (UTF-8) lead byte whose continuation
        // run is followed by ':'.
        int prefixLen = DosDrivePrefixLength(path);
        if (prefixLen > 0)
        {
            offset += prefixLen;
        }
        else if (OperatingSystem.IsWindows())
        {
            // UNC root: "//server" or "\\server" — the C condition
            // (path[0] == path[1] == sep && path[2] != sep) reads the NUL
            // terminator as "different", so a 2-char "//" qualifies too.
            bool uncSlash = path[0] == (byte)'/' && path.Length >= 2
                && path[1] == (byte)'/' && (path.Length < 3 || path[2] != (byte)'/');
            bool uncBackslash = path[0] == (byte)'\\' && path.Length >= 2
                && path[1] == (byte)'\\' && (path.Length < 3 || path[2] != (byte)'\\');
            if (uncSlash || uncBackslash)
            {
                offset += 2;

                // Skip the computer-name segment (both separators, like C).
                while (offset < path.Length && path[offset] != (byte)'/' && path[offset] != (byte)'\\')
                {
                    offset++;
                }
            }
        }

        // C (fs_path.c): the trailing-backslash root check is NOT gated on
        // the UNC branch — it runs whenever the drive branch did not consume
        // the path, so "x:\\y" (drive prefix 2 + '\\') is rooted at 2 on
        // Windows exactly like "//server/share" is rooted at the share
        // separator. On POSIX '\\' is never a root separator.
        if (OperatingSystem.IsWindows() && offset < path.Length && path[offset] == (byte)'\\')
        {
            return offset;
        }

        if (offset < path.Length && path[offset] == (byte)'/')
        {
            return offset;
        }

        // Not rooted — C returns -1.
        return -1;
    }

    /// <summary>
    /// Byte-port of C's <c>dos_drive_prefix_length</c> (fs_path.c): 2 for an
    /// ASCII first byte followed by ':', 1+i for a high-bit (UTF-8) first
    /// character whose continuation run is followed by ':', else 0. Not
    /// WIN32-gated and not alpha-required, exactly like C.
    /// </summary>
    private static int DosDrivePrefixLength(ReadOnlySpan<byte> path)
    {
        if (path.Length >= 1 && (path[0] & 0x80) == 0)
        {
            return path.Length >= 2 && path[1] == (byte)':' ? 2 : 0;
        }

        int i = 1;
        while (i < 4 && i < path.Length && (path[i] & 0x80) != 0)
        {
            i++;
        }

        return i < path.Length && path[i] == (byte)':' ? i + 1 : 0;
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;
using LibGit2CS.Objects;

namespace LibGit2CS.IO;

/// <summary>
/// Filesystem stat utilities shared by <see cref="FilesystemIterator"/> and
/// <see cref="LibGit2CS.Index.GitIndex.AddByPathAsync(LibGit2CS.IO.GitPath, System.Threading.CancellationToken)"/>. Extracted from
/// <c>FilesystemIterator.GetStatInfo</c>/<c>GetFileMode</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GetStatInfo"/> reads real <c>dev</c>/<c>ino</c>/<c>uid</c>/
/// <c>gid</c> and nanosecond <c>mtime</c>/<c>ctime</c> via <see cref="NativeStat"/>
/// (platform <c>statx</c>/<c>stat</c>/<c>GetFileInformationByHandle</c>),
/// matching <c>git_index_entry__init_from_stat</c> (<c>index.c:900-916</c>).
/// This lets the workdir side of the stat-cache comparison in
/// <see cref="Diff.DiffGenerator"/> validate against the index side (which
/// preserves the values git wrote). If native stat is unavailable at runtime,
/// the identity fields fall back to 0 and mtime/ctime fall back to the BCL
/// 100 ns <see cref="FileSystemInfo"/> timestamps — the stat cache misses and
/// recomputes the OID, as it did before the native-stat fix.
/// </para>
/// </remarks>
internal static class StatUtil
{
    /// <summary>
    /// Maps a <see cref="FileSystemInfo"/> to a <see cref="GitFileMode"/>.
    /// Matches <c>git_futils_canonical_mode</c>.
    /// </summary>
    public static GitFileMode GetFileMode(FileSystemInfo fsInfo)
    {
        FileAttributes attrs = fsInfo.Attributes;

        // the reparse
        // point bit must win — a symlink to a directory reports both
        // 'Directory' and 'ReparsePoint', and C's lstat-based
        // git_futils_canonical_mode never follows it (S_IFLNK).
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            return GitFileMode.Symlink;
        }

        if ((attrs & FileAttributes.Directory) != 0)
        {
            return GitFileMode.Tree;
        }

        if (!OperatingSystem.IsWindows())
        {
            if (fsInfo is FileInfo info &&
                (info.UnixFileMode & UnixFileMode.UserExecute) != 0)
            {
                return GitFileMode.Executable;
            }
        }

        return GitFileMode.Regular;
    }

    /// <summary>
    /// Extracts stat data from a <see cref="FileSystemInfo"/>. Matches
    /// <c>git_index_entry__init_from_stat</c> (<c>index.c:900-916</c>): reads
    /// real <c>dev</c>/<c>ino</c>/<c>uid</c>/<c>gid</c> and nanosecond
    /// <c>mtime</c>/<c>ctime</c> via <see cref="NativeStat"/>. Falls back to
    /// BCL <see cref="FileSystemInfo"/> values (identity fields 0, mtime/ctime
    /// truncated to 100 ns) when native stat is unavailable.
    /// </summary>
    public static (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size)
        GetStatInfo(FileSystemInfo fsInfo)
        => GetStatInfoCore(fsInfo, useRdev: false);

    /// <summary>
    /// Extracts stat data for the INDEX-WRITE path. Identical to
    /// <see cref="GetStatInfo"/> except the <c>dev</c> slot carries
    /// <c>st_rdev</c> — C's <c>git_index_entry__init_from_stat</c>
    /// (<c>index.c:909</c>) stores <c>st_rdev</c> (0 for regular files on
    /// POSIX), while the workdir iterator (<c>iterator.c:1531</c>) reads
    /// <c>st_dev</c>. Used by <c>git_index_add_bypath</c> (GitIndex),
    /// <c>git_diff__oid_for_entry</c>'s UPDATE_INDEX refresh
    /// (DiffFileContent) and the stash/from-workdir blob entry (BlobHelper).
    /// </summary>
    public static (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size)
        GetStatInfoForIndex(FileSystemInfo fsInfo)
        => GetStatInfoCore(fsInfo, useRdev: true);

    private static (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size)
        GetStatInfoCore(FileSystemInfo fsInfo, bool useRdev)
    {
        NativeStat.StatResult native = NativeStat.GetStat(fsInfo);

        if (native.Valid)
        {
            // Size: clamp to uint.MaxValue to match git_index_entry.file_size
            // (uint32). Directories have size 0 (git_index_entry__init_from_stat
            // uses st_size which is 0 for dirs on most filesystems).
            uint size = (uint)Math.Min(native.Size, uint.MaxValue);

            return (
                native.Ctime,
                native.Mtime,
                useRdev ? (native.Rdev ?? 0u) : (native.Dev ?? 0u),
                native.Ino ?? 0u,
                native.Uid ?? 0u,
                native.Gid ?? 0u,
                size);
        }

        // BCL fallback: identity fields unavailable, mtime/ctime at 100 ns.
        return GetStatInfoBcl(fsInfo);
    }

    /// <summary>
    /// BCL-only fallback for <see cref="GetStatInfo"/>. Used when native stat
    /// is unavailable. Identity fields are 0; mtime/ctime are derived from
    /// <see cref="FileSystemInfo"/> timestamps at 100 ns granularity.
    /// </summary>
    private static (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size)
        GetStatInfoBcl(FileSystemInfo fsInfo)
    {
        IndexTime ctime = ToIndexTime(fsInfo.CreationTimeUtc);
        IndexTime mtime = ToIndexTime(fsInfo.LastWriteTimeUtc);

        uint size = 0;
        if (fsInfo is FileInfo fileInfo)
        {
            size = (uint)Math.Min(fileInfo.Length, uint.MaxValue);
        }

        return (ctime, mtime, 0u, 0u, 0u, 0u, size);
    }

    /// <summary>
    /// Converts a UTC <see cref="DateTime"/> to an <see cref="IndexTime"/>,
    /// preserving sub-second precision (nanoseconds). 1 tick = 100ns, so
    /// the sub-second tick remainder is multiplied by 100 to get nanoseconds.
    /// This is the BCL fallback path; the native path in <see cref="NativeStat"/>
    /// reads true nanoseconds via <c>statx</c>/<c>stat</c>.
    /// </summary>
    internal static IndexTime ToIndexTime(DateTime utc)
    {
        // Unix epoch seconds via DateTimeOffset (handles the year-1 → 1970 offset).
        long sec = new DateTimeOffset(utc).ToUnixTimeSeconds();

        // Sub-second remainder: ticks within the current second.
        // 1 tick = 100ns; 1 second = 10,000,000 ticks. The remainder is
        // ticks % TicksPerSecond, then × 100 to get nanoseconds.
        long subTicks = utc.Ticks % TimeSpan.TicksPerSecond;
        uint nanos = (uint)(subTicks * 100); // 100ns → ns
        return new IndexTime((int)sec, nanos);
    }
}

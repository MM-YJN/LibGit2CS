// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.InteropServices;

using LibGit2CS.Index;

using Microsoft.Win32.SafeHandles;

namespace LibGit2CS.IO;

/// <summary>
/// Native <c>stat</c>-equivalent reads for the workdir stat cache. Provides
/// real <c>dev</c>/<c>ino</c>/<c>uid</c>/<c>gid</c> and nanosecond
/// <c>mtime</c>/<c>ctime</c> — fields that .NET's <see cref="FileSystemInfo"/>
/// does not expose portably. Matches <c>git_index_entry__init_from_stat</c>
/// (<c>src/libgit2/index.c:900-916</c>) on the values it feeds into a
/// <c>git_index_entry</c>, so the workdir side of the stat-cache comparison
/// in <see cref="Diff.DiffGenerator"/> can actually validate against the
/// index side (which preserves the values git wrote).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why native.</b> <see cref="FileSystemInfo.LastWriteTimeUtc"/> is rounded
/// to the .NET tick (100 ns), but ext4/btrfs/XFS store true nanosecond mtime.
/// The index (parsed by <see cref="GitIndex"/>) keeps the real
/// nanoseconds git wrote, so a 100 ns-truncated workdir mtime mismatches the
/// index for ~98% of files on nanosecond-precision filesystems, forcing a
/// full content rehash per file on every status/diff call. Likewise
/// <c>dev</c>/<c>ino</c>/<c>uid</c>/<c>gid</c> are not exposed by the BCL at
/// all; hard-coding them to 0 (the prior behavior) made the stat-cache
/// identity compare always-fail. Reading the real values via the platform
/// <c>stat</c>/<c>statx</c>/<c>GetFileInformationByHandle</c> restores the
/// cache.
/// </para>
/// <para>
/// <b>Platform dispatch.</b> Linux uses <c>statx(2)</c> (kernel UAPI
/// <c>struct statx</c>); macOS/BSD use <c>stat(2)</c>; Windows uses
/// <c>GetFileInformationByHandle</c>. All P/Invoke is via
/// <see cref="LibraryImportAttribute"/> (source-generated marshalling, AOT-
/// clean). The native structs are blittable (no pointers/strings). On any
/// native failure (symbol missing, ENOSYS, handle open failure), the methods
/// fall back to <see cref="FileSystemInfo"/>-derived values — never throw;
/// the stat cache simply misses and recomputes the OID, as it did before.
/// </para>
/// <para>
/// <b>dev/rdev split.</b> libgit2 has a long-standing split: the index-write
/// path <c>git_index_entry__init_from_stat</c> (<c>index.c:909</c>) stores
/// <c>st_rdev</c> (0 for regular files on POSIX), while the workdir iterator
/// <c>filesystem_iterator</c> (<c>iterator.c:1531</c>) reads <c>st_dev</c>.
/// This port reproduces the split: <see cref="StatUtil.GetStatInfo"/> (workdir
/// iterator / cache side) feeds <c>st_dev</c>, and
/// <see cref="StatUtil.GetStatInfoForIndex"/> (index-write side) feeds
/// <c>st_rdev</c>, so the on-disk index <c>dev</c> field matches C byte for
/// byte. (C's stat-cache comparison in <c>maybe_modified</c>
/// (diff_generate.c:895-899) never compares <c>dev</c>, so the split does not
/// weaken cache validation.)
/// </para>
/// <para>
/// <b>AOT.</b> No <c>unsafe</c>, no <c>delegate*</c>, no reflection. No static
/// mutable state (only <c>static readonly</c> constants and P/Invoke method
/// declarations) — passes <c>StaticStateConventionTests</c>.
/// </para>
/// </remarks>
internal static partial class NativeStat
{
    // ━━ Linux: statx(2) ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    // STATX_BASIC_STATS = TYPE|MODE|NLINK|UID|GID|ATIME|MTIME|CTIME|INO|SIZE|BLOCKS
    private const uint StatxBasicStats = 0x000007ffU;

    // AT_FDCWD + AT_SYMLINK_NOFOLLOW (don't dereference symlinks — git stat's
    // the link itself). Values are stable across glibc/musl (kernel UAPI).
    private const int AtFdCwd = -100;
    private const int AtSymlinkNofollow = 0x100;

    /// <summary>
    /// Linux <c>struct statx_timestamp</c> (<c>linux/stat.h:56-60</c>).
    /// Blittable: s64 + u32 + s32 = 16 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long TvSec;
        public uint TvNsec;
        public int Reserved;
    }

    /// <summary>
    /// Linux <c>struct statx</c> (<c>linux/stat.h:99-132</c>). Blittable,
    /// 256 bytes. Only the fields the stat cache needs are named; the
    /// trailing spares are kept as a fixed-size array to preserve layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Statx
    {
        public uint StxMask;
        public uint StxBlksize;
        public ulong StxAttributes;
        public uint StxNlink;
        public uint StxUid;
        public uint StxGid;
        public ushort StxMode;
        public ushort Spare0;
        public ulong StxIno;
        public ulong StxSize;
        public ulong StxBlocks;
        public ulong StxAttributesMask;
        public StatxTimestamp StxAtime;
        public StatxTimestamp StxBtime;
        public StatxTimestamp StxCtime;
        public StatxTimestamp StxMtime;
        public uint StxRdevMajor;
        public uint StxRdevMinor;
        public uint StxDevMajor;
        public uint StxDevMinor;
        public ulong StxMntId;
        public uint StxDioMemAlign;
        public uint StxDioOffsetAlign;
        public ulong Spare3a;
        public ulong Spare3b;
        public ulong Spare3c;
        public ulong Spare3d;
        public ulong Spare3e;
        public ulong Spare3f;
        public ulong Spare3g;
        public ulong Spare3h;
        public ulong Spare3i;
        public ulong Spare3j;
        public ulong Spare3k;
        public ulong Spare3l;
    }

    // P/Invoke lives in the nested NativeMethods class (CA1060).

    // ━━ macOS / *BSD: stat(2) ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// macOS <c>struct stat</c> (<c>sys/stat.h</c>, 64-bit). Uses
    /// <c>struct timespec</c> for <c>st_mtimespec</c>/<c>st_ctimespec</c>
    /// (the <c>st_mtime_nsec</c> macro maps to <c>st_mtimespec.tv_nsec</c>
    /// on Apple — <c>src/util/unix/posix.h:28</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MacStat
    {
        public uint StDev;          // dev_t
        public uint StMode;         // mode_t
        public ushort StNlink;      // nlink_t
        public uint StIno;          // ino_t (uint64 on macOS 10.6+, but the
                                    //  user-side struct packs it here)
        public uint StUid;          // uid_t
        public uint StGid;          // gid_t
        public long StRdev;         // dev_t
        public MacTimespec StAtimespec;
        public MacTimespec StMtimespec;
        public MacTimespec StCtimespec;
        public long StSize;         // off_t
        public long StBlocks;       // off_t
        public uint StBlksize;      // uint32
        public uint StFlags;        // uint32
        public uint StGen;          // uint32
        public int StLspare;
        public long StQpad;         // int64 padding
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacTimespec
    {
        public long TvSec;          // time_t
        public long TvNsec;         // long
    }

    // P/Invoke lives in the nested NativeMethods class (CA1060).

    // ━━ Windows: GetFileInformationByHandle ━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint DwFileAttributes;
        public uint FtCreationTimeLow;
        public uint FtCreationTimeHigh;
        public uint FtLastAccessTimeLow;
        public uint FtLastAccessTimeHigh;
        public uint FtLastWriteTimeLow;
        public uint FtLastWriteTimeHigh;
        public uint DwVolumeSerialNumber;
        public uint NFileSizeHigh;
        public uint NFileSizeLow;
        public uint NNumberOfLinks;
        public uint NFileIndexLow;
        public uint NFileIndexHigh;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // needed for directories

    /// <summary>
    /// Result of a native stat read. The identity fields (<c>dev</c>/
    /// <c>rdev</c>/<c>ino</c>/<c>uid</c>/<c>gid</c>) are <c>null</c> when the
    /// platform does not provide them (e.g. Windows uid/gid) — the caller
    /// leaves the index entry's field at its default 0 in that case.
    /// <c>Dev</c> is <c>st_dev</c> (the containing device); <c>Rdev</c> is
    /// <c>st_rdev</c> (0 for regular files) — C writes <c>st_rdev</c> into the
    /// index (<c>index.c:909</c>) and reads <c>st_dev</c> in the workdir
    /// iterator (<c>iterator.c:1531</c>).
    /// </summary>
    internal readonly record struct StatResult(
        IndexTime Ctime,
        IndexTime Mtime,
        uint? Dev,
        uint? Rdev,
        uint? Ino,
        uint? Uid,
        uint? Gid,
        long Size,
        bool Valid,
        ushort TypeBits = 0);

    /// <summary>
    /// Reads native stat data for <paramref name="fsInfo"/>. On any native
    /// failure, returns <see cref="StatResult.Valid"/> = false so the caller
    /// falls back to <see cref="FileSystemInfo"/>-derived values. Never
    /// throws.
    /// </summary>
    internal static StatResult GetStat(FileSystemInfo fsInfo)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return GetStatx(fsInfo);
            }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                return GetMacStat(fsInfo);
            }

            if (OperatingSystem.IsWindows())
            {
                return GetWindowsStat(fsInfo);
            }
        }
        catch
        {
            // Any P/Invoke / marshalling failure → BCL fallback.
            return default;
        }

        return default;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to a canonical absolute path,
    /// following every symlink component and requiring the path to exist.
    /// Matches <c>realpath(3)</c> / <c>p_realpath</c> (the ceiling-directory
    /// check calls it exactly like <c>find_ceiling_dir_offset</c>,
    /// repository.c:497-500). Returns null when the path does not exist or
    /// cannot be resolved (glibc <c>realpath(path, NULL)</c> with a
    /// malloc'd buffer, freed with <c>free(3)</c>).
    /// </summary>
    /// <remarks>
    /// <c>realpath(path, NULL)</c> is glibc-specific; other platforms fall
    /// back to lexical canonicalization (<see cref="System.IO.Path.GetFullPath(string)"/>),
    /// which neither resolves symlinks nor requires existence — the
    /// Linux test environment exercises the exact path.
    /// </remarks>
    internal static string? TryRealpath(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                string full = Path.GetFullPath(path);

                // C's p_realpath resolves symlinks on every platform (on
                // Windows via GetFinalPathNameByHandleW) and FAILS with
                // ENOENT when the path does not exist (fs_path.c:387-401).
                // Path.GetFullPath is lexical, so existence is checked
                // explicitly and the final link component is resolved.
                if (File.Exists(full))
                {
                    FileSystemInfo? target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        return target.FullName;
                    }

                    return full;
                }

                if (Directory.Exists(full))
                {
                    FileSystemInfo? target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        return target.FullName;
                    }

                    return full;
                }

                // Nonexistent — realpath(3) fails with ENOENT; the caller
                // maps null to GIT_ENOTFOUND "failed to resolve path".
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        IntPtr resolved = NativeMethods.RealpathCall(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            NativeMethods.FreeCall(resolved);
        }
    }

    /// <summary>
    /// Reads a symlink's target as raw bytes (p_readlink, blob.c:173).
    /// Returns false when <paramref name="path"/> is not a symlink or the
    /// read fails. Works for broken links (readlink does not require the
    /// target to exist). On Windows, falls back to
    /// <see cref="System.IO.FileSystemInfo.LinkTarget"/>: the OS stores the target as
    /// UTF-16 and C's w32 readlink UTF-8-encodes it, which re-encoding
    /// reproduces. On Unix, the raw link bytes are read directly — .NET's
    /// <c>LinkTarget</c> decodes them as UTF-8, corrupting non-UTF-8
    /// targets.
    /// </summary>
    internal static bool TryReadLinkTarget(string path, out byte[] targetBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            string? target = new FileInfo(path).LinkTarget;
            if (target is null)
            {
                targetBytes = [];
                return false;
            }

            targetBytes = System.Text.Encoding.UTF8.GetBytes(target);
            return true;
        }

        targetBytes = [];

        // Size the buffer from lstat (C: git_odb__hashlink, odb.c:302-348).
        StatResult stat = GetStat(new FileInfo(path));
        if (!stat.Valid || (stat.TypeBits & 0xF000) != 0xA000)
        {
            return false; // not a symlink
        }

        long size = stat.Size;
        if (size is < 0 or >= int.MaxValue)
        {
            return false;
        }

        // readlink does not NUL-terminate; size+1 leaves room for the
        // terminator (C allocates st_size + 1, odb.c:322-326).
        byte[] buffer = new byte[size + 1];
        long n = NativeMethods.ReadlinkCall(path, buffer, (nuint)buffer.Length);
        if (n < 0)
        {
            return false;
        }

        targetBytes = buffer.AsSpan(0, (int)n).ToArray();
        return true;
    }

    /// <summary>
    /// Creates a symlink whose target is the given raw bytes (symlink(2)),
    /// bypassing .NET's UTF-8 string encoding. Returns false on failure.
    /// C's
    /// blob_content_to_link passes the raw blob bytes to p_symlink
    /// (checkout.c:1596-1630); File.CreateSymbolicLink decodes/re-encodes
    /// the target as UTF-8, corrupting non-UTF-8 targets.
    /// </summary>
    internal static bool TryCreateSymbolicLinkRaw(string linkPath, ReadOnlySpan<byte> targetBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            return false; // callers fall back to File.CreateSymbolicLink
        }

        byte[] target = new byte[targetBytes.Length + 1];
        targetBytes.CopyTo(target);
        byte[] link = new byte[System.Text.Encoding.UTF8.GetByteCount(linkPath) + 1];
        System.Text.Encoding.UTF8.GetBytes(linkPath, link);
        return NativeMethods.SymlinkCall(target, link) == 0;
    }

    /// <summary>
    /// The effective user id of the current process. Matches <c>geteuid(2)</c>
    /// — the comparison base for <c>git_fs_path_owner_is</c>
    /// (fs_path.c:1939). POSIX only; the callers gate on
    /// <see cref="OperatingSystem.IsWindows"/> first.
    /// </summary>
    internal static uint GetEuid() => NativeMethods.GetEuidCall();

    private static StatResult GetStatx(FileSystemInfo fsInfo)
    {
        IntPtr pathPtr = Marshal.StringToCoTaskMemUTF8(fsInfo.FullName);
        try
        {
            int rc = NativeMethods.StatxCall(AtFdCwd, pathPtr, AtSymlinkNofollow, StatxBasicStats, out Statx stx);
            if (rc != 0)
            {
                return default;
            }

            // makedev: combine major/minor into a single dev_t value, matching
            // how glibc's st_dev is encoded (the index stores a 32-bit dev).
            uint dev = stx.StxDevMajor << 8 | stx.StxDevMinor;

            // uint64 ino → uint32 (git index field is 32-bit; truncation matches
            // libgit2's entry->ino = (uint32_t)st->st_ino on 32-bit-inode systems
            // and is safe for the cache since both sides truncate identically).
            uint ino = (uint)stx.StxIno;

            return new StatResult(
                Ctime: new IndexTime((int)stx.StxCtime.TvSec, stx.StxCtime.TvNsec),
                Mtime: new IndexTime((int)stx.StxMtime.TvSec, stx.StxMtime.TvNsec),
                Dev: dev,
                Rdev: stx.StxRdevMajor << 8 | stx.StxRdevMinor,
                Ino: ino,
                Uid: stx.StxUid,
                Gid: stx.StxGid,
                Size: (long)stx.StxSize,
                Valid: true,
                TypeBits: (ushort)(stx.StxMode & 0xF000));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    private static StatResult GetMacStat(FileSystemInfo fsInfo)
    {
        IntPtr pathPtr = Marshal.StringToCoTaskMemUTF8(fsInfo.FullName);
        try
        {
            int rc = NativeMethods.MacStatCall(pathPtr, out MacStat st);
            if (rc != 0)
            {
                return default;
            }

            return new StatResult(
                Ctime: new IndexTime((int)st.StCtimespec.TvSec, (uint)st.StCtimespec.TvNsec),
                Mtime: new IndexTime((int)st.StMtimespec.TvSec, (uint)st.StMtimespec.TvNsec),
                Dev: st.StDev,
                Rdev: (uint)st.StRdev,
                Ino: st.StIno,
                Uid: st.StUid,
                Gid: st.StGid,
                Size: st.StSize,
                Valid: true,
                TypeBits: (ushort)(st.StMode & 0xF000));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    private static StatResult GetWindowsStat(FileSystemInfo fsInfo)
    {
        // GetFileInformationByHandle requires a handle; open with backup
        // semantics so directories work. This is VFS metadata (no IO wait),
        // matching the AGENTS.md "stat/metadata ops stay sync" rule.
        uint share = FileShareRead | FileShareWrite | FileShareDelete;

        // CreateFileW returns the handle as an IntPtr (INVALID_HANDLE_VALUE
        // on failure) and the caller wraps it in a SafeFileHandle. Marshalling
        // the handle through an `out SafeFileHandle` parameter loses the
        // value on the .NET 11 preview runtime (the handle comes back as 0
        // while the file stays locked forever), so the raw-pointer form is
        // used instead.
        IntPtr rawHandle = NativeMethods.CreateFileW(
            fsInfo.FullName,
            GenericRead,
            share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
        {
            return default;
        }

        using SafeFileHandle handle = new(rawHandle, ownsHandle: true);
        if (!NativeMethods.GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            return default;
        }

        // Windows has no POSIX uid/gid; leave them null → 0 in the entry.
        // Inode = (indexHigh << 32) | indexLow, truncated to 32 bits to
        // fit the index field (NTFS file IDs are 128-bit; the low 64 are
        // the per-volume reference, of which we keep the low 32).
        uint ino = info.NFileIndexLow;

        // dev = volume serial number (stable per volume).
        uint dev = info.DwVolumeSerialNumber;

        long size = ((long)info.NFileSizeHigh << 32) | info.NFileSizeLow;

        IndexTime ctime = FileTimeToIndexTime(info.FtCreationTimeHigh, info.FtCreationTimeLow);
        IndexTime mtime = FileTimeToIndexTime(info.FtLastWriteTimeHigh, info.FtLastWriteTimeLow);

        return new StatResult(
            Ctime: ctime,
            Mtime: mtime,
            Dev: dev,
            Rdev: null, // C's Windows stat shim sets st_rdev = 0.
            Ino: ino,
            Uid: null,
            Gid: null,
            Size: size,
            Valid: true,
            TypeBits: 0);
    }

    /// <summary>
    /// Converts a Windows FILETIME (two uint32 halves) to an
    /// <see cref="IndexTime"/>. FILETIME is 100ns ticks since 1601-01-01;
    /// convert to Unix seconds + sub-second nanoseconds.
    /// </summary>
    private static IndexTime FileTimeToIndexTime(uint high, uint low)
    {
        long fileTimeTicks = ((long)high << 32) | low;

        // 1601-01-01 → 1970-01-01 in 100ns ticks: 116444736000000000.
        const long FileTimeEpochOffset = 116444736000000000L;
        long unixTicks = fileTimeTicks - FileTimeEpochOffset;

        if (unixTicks < 0)
        {
            return IndexTime.Zero;
        }

        long seconds = unixTicks / TimeSpan.TicksPerSecond;
        long subTicks = unixTicks % TimeSpan.TicksPerSecond;
        uint nanos = (uint)(subTicks * 100); // 100ns → ns

        return new IndexTime((int)seconds, nanos);
    }

    // ━━ P/Invoke declarations (CA1060: nested NativeMethods class) ━━━━━

    /// <summary>
    /// Native method declarations. The class name <c>NativeMethods</c>
    /// suppresses CA1060. <see cref="DefaultDllImportSearchPathsAttribute"/>
    /// restricts the library search to safe paths (CA5392): on Unix, libc is
    /// loaded from the system linker; on Windows, kernel32 is in System32.
    /// </summary>
    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial int StatxCall(
            int dirfd,
            IntPtr pathname,
            int flags,
            uint mask,
            out Statx stx);

        [LibraryImport("libc", EntryPoint = "stat", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial int MacStatCall(
            IntPtr path,
            out MacStat st);

        [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial IntPtr RealpathCall(string path, IntPtr resolved);

        [LibraryImport("libc", EntryPoint = "readlink", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial long ReadlinkCall(string path, [Out] byte[] buf, nuint bufsiz);

        [LibraryImport("libc", EntryPoint = "symlink", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial int SymlinkCall(byte[] target, byte[] linkpath);

        [LibraryImport("libc", EntryPoint = "free", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial void FreeCall(IntPtr ptr);

        [LibraryImport("libc", EntryPoint = "geteuid", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
        public static partial uint GetEuidCall();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);
    }
}

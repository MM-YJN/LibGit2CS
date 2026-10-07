// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Index;

/// <summary>
/// In-memory representation of a file entry in the git index. Managed port of
/// <c>git_index_entry</c> in <c>include/git2/index.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>flags</c> field packs the path name length (lower 12 bits), the
/// conflict stage (bits 12-13), the extended-flag indicator (bit 14), and the
/// assume-valid bit (bit 15). When the extended bit is set, <c>flagsExtended</c>
/// carries intent-to-add and skip-worktree flags.
/// </para>
/// <para>
/// Parsed from <c>.git/index</c> v2/v3 binary format (read side) and
/// serialized back to disk (write side). <see cref="WithStage"/> returns a
/// copy with the stage bits set.
/// </para>
/// </remarks>
public readonly record struct GitIndexEntry
{
    /// <summary>Mask for the path name length field in <see cref="Flags"/>.</summary>
    public const ushort NameMask = 0x0FFF;

    /// <summary>Mask for the stage field in <see cref="Flags"/>.</summary>
    public const ushort StageMask = 0x3000;

    /// <summary>Shift for the stage field in <see cref="Flags"/>.</summary>
    public const int StageShift = 12;

    /// <summary>Bit 14: extended flags are present in <see cref="FlagsExtended"/>.</summary>
    public const ushort Extended = 0x4000;

    /// <summary>Bit 15: assume-valid flag.</summary>
    public const ushort Valid = 0x8000;

    /// <summary>Extended flag: intent-to-add (bit 13 of <see cref="FlagsExtended"/>).</summary>
    public const ushort IntentToAdd = 1 << 13;

    /// <summary>Extended flag: skip-worktree (bit 14 of <see cref="FlagsExtended"/>).</summary>
    public const ushort SkipWorktree = 1 << 14;

    /// <summary>Mask of on-disk extended flags.</summary>
    public const ushort ExtendedFlags = IntentToAdd | SkipWorktree;

    /// <summary>
    /// Change time (seconds + nanoseconds). Matches <c>git_index_time</c>.
    /// </summary>
    public IndexTime Ctime { get; init; }

    /// <summary>Modification time (seconds + nanoseconds).</summary>
    public IndexTime Mtime { get; init; }

    /// <summary>Device ID.</summary>
    public uint Dev { get; init; }

    /// <summary>Inode number.</summary>
    public uint Ino { get; init; }

    /// <summary>Canonical git file mode (raw 32-bit on disk; normalized here).</summary>
    public GitFileMode Mode { get; init; }

    /// <summary>Owner UID.</summary>
    public uint Uid { get; init; }

    /// <summary>Owner GID.</summary>
    public uint Gid { get; init; }

    /// <summary>File size in bytes (truncated to 32 bits).</summary>
    public uint FileSize { get; init; }

    /// <summary>Object ID of the staged blob.</summary>
    public GitOid Id { get; init; }

    /// <summary>
    /// Packed flags: name length (12 bits), stage (2 bits), extended (1 bit),
    /// assume-valid (1 bit).
    /// </summary>
    public ushort Flags { get; init; }

    /// <summary>
    /// Extended flags (only meaningful when <see cref="Extended"/> bit is set
    /// in <see cref="Flags"/>).
    /// </summary>
    public ushort FlagsExtended { get; init; }

    /// <summary>
    /// Slash-separated path (relative to repo root). Byte-faithful
    /// <see cref="GitPath"/>: compared byte-wise end-to-end (matching
    /// <c>entry->path</c> raw-byte comparison in libgit2), so non-UTF-8 paths
    /// round-trip exactly. UTF-8 decode happens only at display/FS egress
    /// (<see cref="GitPath.ToUtf8String"/> / <see cref="GitPath.ToFileSystemString"/>).
    /// </summary>
    public GitPath Path { get; init; }

    /// <summary>
    /// The conflict stage (0 = normal, 1 = ancestor, 2 = ours, 3 = theirs).
    /// Matches <c>GIT_INDEX_ENTRY_STAGE</c>.
    /// </summary>
    public int Stage => (Flags & StageMask) >> StageShift;

    /// <summary>
    /// True if this entry is part of a conflict (stage > 0). Matches
    /// <c>git_index_entry_is_conflict</c>.
    /// </summary>
    public bool IsConflict => Stage > 0;

    /// <summary>True if the assume-valid flag is set.</summary>
    public bool AssumeValid => (Flags & Valid) != 0;

    /// <summary>True if extended flags are present.</summary>
    public bool HasExtended => (Flags & Extended) != 0;

    /// <summary>True if the intent-to-add extended flag is set.</summary>
    public bool IntentToAddFlag => (FlagsExtended & IntentToAdd) != 0;

    /// <summary>True if the skip-worktree extended flag is set.</summary>
    public bool SkipWorktreeFlag => (FlagsExtended & SkipWorktree) != 0;

    /// <summary>
    /// Returns a copy of this entry with the stage field set in the flags.
    /// Matches <c>GIT_INDEX_ENTRY_STAGE_SET</c>. Stage 0 clears the stage
    /// bits; stages 1-3 set them.
    /// </summary>
    /// <param name="stage">The conflict stage (0-3).</param>
    /// <returns>A new entry with the stage set.</returns>
    public GitIndexEntry WithStage(int stage)
    {
        ushort flags = (ushort)(Flags & ~StageMask);
        flags |= (ushort)((stage & 0x3) << StageShift);
        return this with { Flags = flags };
    }

    /// <summary>
    /// Creates an index entry with the given path and OID, all stat fields
    /// zeroed. Used for in-memory construction (e.g. by iterators synthesizing
    /// pseudo-tree entries).
    /// </summary>
    public GitIndexEntry(string path, GitOid id, GitFileMode mode)
        : this(GitPath.FromUtf8String(path), id, mode)
    {
    }

    /// <summary> Byte-faithful <see cref="GitIndexEntry(string, GitOid, GitFileMode)"/>. Use this overload when constructing entries from byte-faithful sources
    /// (diff files, tree entries) to avoid the UTF-8 round-trip that corrupts non-UTF-8 paths. </summary>
    public GitIndexEntry(GitPath path, GitOid id, GitFileMode mode)
    {
        Path = path;
        Id = id;
        Mode = mode;
        Ctime = default;
        Mtime = default;
        Dev = 0;
        Ino = 0;
        Uid = 0;
        Gid = 0;
        FileSize = 0;
        Flags = 0;
        FlagsExtended = 0;
    }
}

/// <summary>
/// Time structure used in index entries. Matches <c>git_index_time</c>.
/// </summary>
/// <summary>
/// A timestamp with second + nanosecond parts. The seconds field is
/// <see cref="long"/> because C's <c>git_time_t</c> is int64 and the index
/// reader widens the on-disk uint32 value-preserving (index.c:2558-2561) —
/// a stored value with the high bit set is a large positive time.
/// </summary>
public readonly record struct IndexTime(long Seconds, uint Nanoseconds)
{
    /// <summary>Zero (unset) time.</summary>
    public static IndexTime Zero { get; } = new(0, 0);
}

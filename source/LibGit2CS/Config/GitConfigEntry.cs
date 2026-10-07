// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.Config;

/// <summary> A single configuration entry. Managed equivalent of libgit2's <c>git_config_entry</c>. </summary> <remarks> <para> Immutable value type — the C
/// struct's lifetime-management (<c>git_config_entry_free</c> dispatching through the backend's <c>free</c> function pointer) is unnecessary in managed code:
/// every field is an immutable value and the GC reclaims them. No <c>IDisposable</c> is needed. </para> <para> <see cref="ValueBytes"/> is <c>null</c> for
/// "lone variables" (<c>key</c> with no <c>= value</c>). <see cref="GitConfiguration.GetBoolAsync"/> treats lone variables as <c>true</c>, matching git semantics.
/// </para> <para> Values are carried as raw bytes (<see cref="ValueBytes"/>) — the byte-parity surface matching libgit2's bag-of-bytes <c>char*</c>. <see
/// cref="Value"/> is the UTF-8 display convenience (decode with replacement, same contract as <c>GitPath.ToUtf8String</c>): non-UTF-8 values surface as U+FFFD
/// there while <see cref="ValueBytes"/> round-trips byte-exact. </para> <para> The name is byte-primary too (<see cref="NameBytes"/>) — libgit2 matches entry
/// names with <c>git_regexp</c>/<c>strcmp</c> over the raw name bytes (<c>config.c</c> <c>all_iter_glob_next</c>/<c>multivar_iter_next</c>), so <see
/// cref="GitConfiguration.EnumerateAsync"/> and the multivar name compares operate on the bytes. <see cref="Name"/> is the lazy UTF-8 display decode (same
/// replacement contract as <see cref="Value"/>). </para> </remarks> <param name="NameBytes">Normalized fully-qualified key bytes, e.g. <c>core.autocrlf</c>. Section and variable name are lowercased; subsection preserves case. Byte-parity surface.</param>
/// <param name="ValueBytes">The raw value bytes, or <c>null</c> for a lone variable (treated as <c>true</c> by <see cref="GitConfiguration.GetBoolAsync"/>). Byte-parity surface.</param>
/// <param name="BackendType">Backend that produced the entry: <c>"file"</c>, <c>"in-memory"</c>, or <c>"snapshot"</c>.</param>
/// <param name="Path">Origin file path for file-backed entries; <c>null</c> for in-memory entries.</param>
/// <param name="IncludeDepth">0 for top-level entries; 1+ for entries pulled in via <c>[include]</c>/<c>[includeIf]</c>.</param>
/// <param name="Level">The <see cref="GitConfigLevel"/> at which the entry was registered.</param>
public readonly record struct GitConfigEntry(
    ReadOnlyMemory<byte> NameBytes,
    ReadOnlyMemory<byte>? ValueBytes,
    string BackendType,
    string? Path,
    int IncludeDepth,
    GitConfigLevel Level)
{
    /// <summary> The name as a UTF-8 display string (decode with replacement). Byte-parity surface is <see cref="NameBytes"/> — this decode is lossy for
    /// non-UTF-8 subsection bytes (U+FFFD replacement), matching the <c>GitPath.ToUtf8String</c> display contract. </summary>
    public string Name => Encoding.UTF8.GetString(NameBytes.Span);

    /// <summary> The value as a UTF-8 display string (decode with replacement). Null for a lone variable. Byte-parity surface is <see cref="ValueBytes"/> —
    /// this decode is lossy for non-UTF-8 values (U+FFFD replacement), matching the <c>GitPath.ToUtf8String</c> display contract. </summary>
    public string? Value => ValueBytes is { } b ? Encoding.UTF8.GetString(b.Span) : null;

    /// <summary>
    /// Backend type constant for the on-disk file backend. Matches libgit2's
    /// <c>CONFIG_FILE_TYPE</c>.
    /// </summary>
    public const string FileBackendType = "file";

    /// <summary>
    /// Backend type constant for the in-memory backend. Matches libgit2's default
    /// for <c>git_config_backend_memory</c>.
    /// </summary>
    public const string MemoryBackendType = "in-memory";

    /// <summary>
    /// Backend type constant for the snapshot backend.
    /// </summary>
    public const string SnapshotBackendType = "snapshot";
}

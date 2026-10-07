// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// A short object ID to expand, plus its (eventual) full OID and type. Managed
/// equivalent of libgit2's <c>git_odb_expand_id</c> (<c>include/git2/odb.h:250</c>).
/// </summary>
/// <remarks>
/// <para>
/// Passed by the caller to <see cref="GitObjectDb.ExpandIdsAsync"/> with <see cref="Id"/>
/// set to a short (abbreviated) OID and <see cref="Length"/> set to the hex prefix
/// length in nibbles. The array is mutated in place: entries that are found are
/// updated with the full OID, full <see cref="Length"/> (hex size), and concrete
/// <see cref="Type"/>; entries that are not found (or are ambiguous) are cleared
/// to <c>default</c> (zero OID, length 0, <see cref="GitObjectType.Ext1"/>).
/// </para>
/// <para>
/// <b>Type sentinel:</b> <see cref="GitObjectType.Ext1"/> (value 0) is the C# zero
/// value and corresponds to C's <c>GIT_OBJECT__EXT1 = 0</c>. It means "type
/// unspecified"; <see cref="GitObjectDb.ExpandIdsAsync"/> normalizes it to
/// <see cref="GitObjectType.Any"/> before lookup, matching C's
/// <c>if (!query-&gt;type) query-&gt;type = GIT_OBJECT_ANY</c>.
/// </para>
/// <para>
/// Declared <c>readonly record struct</c> for value semantics and
/// <see cref="object.GetHashCode"/>/equality. The <see cref="GitObjectDb.ExpandIdsAsync"/>
/// overload taking <c>Span&lt;OdbExpandId&gt;</c> mutates elements in place via
/// <c>ref</c> access + <c>with</c> expressions, matching C's in-place mutation.
/// </para>
/// </remarks>
/// <param name="Id">The object ID (full or abbreviated) to expand.</param>
/// <param name="Length">
/// The number of hex characters (nibbles) of <paramref name="Id"/> that are
/// significant. Values &gt;= 4 and &lt; <c>GitOid.HexSizeFor(algorithm)</c> trigger
/// prefix expansion; values &gt;= <c>HexSize</c> are treated as full OIDs.
/// Cleared to 0 when the entry is not found (or ambiguous).
/// </param>
/// <param name="Type">
/// The (optional) object type to constrain the lookup. Pass
/// <see cref="GitObjectType.Ext1"/> (or leave default) for "any type"; the
/// expander fills in the actual <see cref="GitObjectType"/> on success.
/// </param>
public readonly record struct GitOdbExpandId(GitOid Id, int Length, GitObjectType Type);

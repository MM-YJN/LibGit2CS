// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Repository;

namespace LibGit2CS.Refs;

/// <summary>
/// A git reference: a named pointer to an object (direct) or another reference
/// (symbolic). Managed port of libgit2's <c>git_reference</c>
/// (<c>src/libgit2/refs.h:63-74</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GitDirectReference"/> points at an object OID;
/// <see cref="GitSymbolicReference"/> points at another reference by name.
/// </para>
/// <para>
/// <b>Records</b> are used (not plain classes) so that <see cref="Owner"/> can be
/// injected via <c>with</c> expressions after backend construction — the backend
/// creates instances with <c>Owner = null</c>, and <see cref="LibGit2CS.Refs.GitReferences.LookupAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/>
/// injects the owning <see cref="GitRepository"/> before returning. This mirrors
/// the <c>GitObject.Owner</c> pattern.
/// </para>
/// <para>
/// References are immutable snapshots — no <c>IDisposable</c>. The C
/// <c>git_reference_free</c>/<c>_dup</c> are unnecessary (GC + record copy semantics).
/// </para>
/// </remarks>
public abstract partial record GitReference
{
    /// <summary>The fully-qualified reference name (e.g. <c>refs/heads/master</c>, <c>HEAD</c>).</summary>
    private RefNameKey _name;

    /// <summary>Raw reference name. Input bytes are copied; display decoding is never used for identity.</summary>
    public ReadOnlyMemory<byte> NameBytes { get => _name.Bytes; init => _name = RefNameKey.From(value.ToArray()); }

    /// <summary>UTF-8 display name with replacement. String initialization encodes to the canonical bytes.</summary>
    public string Name { get => _name.ToString(); init => _name = value; }

    // Record equality compares this content-equal key, never a ReadOnlyMemory backing-store identity.
    internal RefNameKey NameKey { get => _name; init => _name = RefNameKey.From(value.Bytes.ToArray()); }

    /// <summary>
    /// The owning repository. Injected by <see cref="LibGit2CS.Refs.GitReferences.LookupAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/>/
    /// <see cref="LibGit2CS.Refs.GitReferences.ResolveAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/> after backend construction. Null when a
    /// reference is created outside a repository context (unit tests).
    /// </summary>
    internal GitRepository? Owner { get; init; }

    /// <summary>True if this is a <see cref="GitSymbolicReference"/>; false for <see cref="GitDirectReference"/>.</summary>
    public abstract bool IsSymbolic { get; }

    /// <summary>True if the name starts with <c>refs/heads/</c>. Matches <c>git_reference_is_branch</c>.</summary>
    public bool IsBranch => NameKey.StartsWith("refs/heads/"u8);

    /// <summary>True if the name starts with <c>refs/remotes/</c>. Matches <c>git_reference_is_remote</c>.</summary>
    public bool IsRemote => NameKey.StartsWith("refs/remotes/"u8);

    /// <summary>True if the name starts with <c>refs/tags/</c>. Matches <c>git_reference_is_tag</c>.</summary>
    public bool IsTag => NameKey.StartsWith("refs/tags/"u8);

    /// <summary>True if the name starts with <c>refs/notes/</c>. Matches <c>git_reference_is_note</c>.</summary>
    public bool IsNote => NameKey.StartsWith("refs/notes/"u8);
}

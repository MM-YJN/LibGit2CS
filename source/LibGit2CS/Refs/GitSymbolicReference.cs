// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Refs;

/// <summary>
/// A reference that points at another reference by name. Managed equivalent of
/// libgit2's <c>GIT_REFERENCE_SYMBOLIC</c>.
/// </summary>
public sealed record GitSymbolicReference : GitReference
{
    /// <summary>The name of the referenced reference (e.g. <c>refs/heads/master</c>).</summary>
    private RefNameKey _targetName;

    /// <summary>Raw symbolic target name; input bytes are copied.</summary>
    public ReadOnlyMemory<byte> TargetNameBytes { get => _targetName.Bytes; init => _targetName = RefNameKey.From(value.ToArray()); }

    /// <summary>UTF-8 display target with replacement. String initialization encodes once.</summary>
    public string TargetName { get => _targetName.ToString(); init => _targetName = value; }

    internal RefNameKey TargetNameKey { get => _targetName; init => _targetName = RefNameKey.From(value.Bytes.ToArray()); }

    /// <inheritdoc/>
    public override bool IsSymbolic => true;

    /// <summary>
    /// The referenced reference, looked up via <see cref="LibGit2CS.Repository.GitRepository.Refs"/>.
    /// Returns null if <see cref="GitReference.Owner"/> is null or the target name
    /// does not resolve. This is a cascading convenience method; the underlying
    /// <c>git_reference_symbolic_target</c> returns only the target name.
    /// </summary>
    public async Task<GitReference?> TargetAsync(CancellationToken cancellationToken = default)
    {
        if (Owner is null)
        {
            return null;
        }

        return await Owner.Refs.LookupAsync(TargetNameKey, cancellationToken).ConfigureAwait(false);
    }
}

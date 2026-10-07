// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Refs;

/// <summary>
/// A reference that points directly at an object OID. Managed equivalent of
/// libgit2's <c>GIT_REFERENCE_DIRECT</c>.
/// </summary>
public sealed record GitDirectReference : GitReference
{
    /// <summary>The OID of the referenced object.</summary>
    public required GitOid Target { get; init; }

    /// <summary>
    /// The pre-peeled OID for annotated tags, read from the <c>^&lt;oid&gt;</c> line
    /// in <c>packed-refs</c>. Null when not available (loose refs, or packed refs
    /// without <c>peeled</c>/<c>fully-peeled</c> header). To obtain the peel at
    /// runtime, load the tag via <c>repo.ObjectLookupAsync&lt;GitTag&gt;(Target)</c>
    /// and call <c>PeelAsync&lt;Commit&gt;()</c>.
    /// </summary>
    public GitOid? Peel { get; init; }

    /// <inheritdoc/>
    public override bool IsSymbolic => false;
}

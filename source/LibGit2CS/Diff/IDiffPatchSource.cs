// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Strategy for obtaining <see cref="GitPatch"/> objects from a <see cref="GitDiff"/>.
/// Matches the <c>patch_fn</c> vtable slot in libgit2's <c>git_diff</c>
/// (<c>diff.h:42-48</c>). Two implementations:
/// <list type="bullet">
/// <item><see cref="DiffGenerator"/> — lazily generates patches via XDiff.</item>
/// <item><see cref="DiffParsed"/> — returns pre-parsed patches by index.</item>
/// </list>
/// </summary>
internal interface IDiffPatchSource
{
    /// <summary>Number of patches (equals delta count).</summary>
    int PatchCount { get; }

    /// <summary>Retrieves the patch at <paramref name="index"/>.</summary>
    ValueTask<GitPatch> GetPatchAsync(int index, CancellationToken cancellationToken = default);
}

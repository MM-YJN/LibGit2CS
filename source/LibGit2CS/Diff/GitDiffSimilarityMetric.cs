// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Pluggable similarity metric for rename/copy detection. Maps to
/// <c>git_diff_similarity_metric</c> in <c>include/git2/diff.h:748-767</c>.
/// The default (null) uses the <see cref="LibGit2CS.Core.SimilarityHash"/> engine.
/// </summary>
public sealed class GitDiffSimilarityMetric
{
    /// <summary>Compute a signature for a file path.</summary>
    public required Func<string, GitDiffFile, object?> FileSignature { get; init; }

    /// <summary>Compute a signature for an in-memory buffer.</summary>
    public required Func<ReadOnlyMemory<byte>, GitDiffFile, object?> BufferSignature { get; init; }

    /// <summary>Release a previously computed signature.</summary>
    public required Action<object?> FreeSignature { get; init; }

    /// <summary>Compare two signatures and return a 0–100 similarity score.</summary>
    public required Func<object?, object?, int> Similarity { get; init; }
}

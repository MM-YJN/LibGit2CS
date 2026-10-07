// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Blame;

/// <summary> One blob in a commit that is being suspected. Managed port of libgit2's <c>git_blame__origin</c> (<c>blame.h:15-21</c>). </summary> <remarks> In
/// C, origins are refcounted (<c>refcnt</c>) and <c>path</c> is an inline <c>char path[GIT_FLEX_ARRAY]</c> buffer with <c>strcpy</c> (blame_git.c:56). In C#,
/// we rely on GC for refcounting and store the path as a byte-faithful <see cref="GitPath"/> — compared byte-wise via <see cref="LibGit2CS.IO.GitPath.Equals(LibGit2CS.IO.GitPath)"/> (matching
/// C's <c>strcmp(a->path, b->path)</c> at blame_git.c:88). The <see cref="Previous"/> chain is still maintained for the blame walk. </remarks>
internal sealed class BlameOrigin
{
    /// <summary>
    /// The commit this origin belongs to.
    /// </summary>
    public Commit Commit { get; }

    /// <summary>
    /// The blob content at <see cref="Path"/> in <see cref="Commit"/>. May be null
    /// if not yet loaded.
    /// </summary>
    public GitBlob? Blob { get; set; }

    /// <summary>
    /// The path within the commit's tree that this origin refers to (byte-faithful).
    /// </summary>
    public GitPath Path { get; }

    /// <summary>
    /// The previous origin in the blame chain (set during <c>pass_blame</c>).
    /// </summary>
    public BlameOrigin? Previous { get; set; }

    private GitTree? _tree;

    public BlameOrigin(Commit commit, GitPath path, GitBlob? blob = null)
    {
        Commit = commit;
        Path = path;
        Blob = blob;
    }

    /// <summary>
    /// Lazily resolves and caches the tree of <see cref="Commit"/>. Subsequent
    /// calls return the cached tree without re-parsing. Used by
    /// <c>FindOriginAsync</c> to avoid re-resolving the same origin tree once
    /// per parent of a multi-parent commit. Returns null if the commit has no
    /// owning repository or the tree lookup fails.
    /// </summary>
    internal ValueTask<GitTree?> GetTreeAsync(CancellationToken cancellationToken)
    {
        if (_tree is not null)
        {
            // Cached — the majority case after the first call.
            return ValueTask.FromResult<GitTree?>(_tree);
        }

        if (Commit.Owner is null)
        {
            return ValueTask.FromResult<GitTree?>(null);
        }

        return new ValueTask<GitTree?>(GetTreeSlowAsync(cancellationToken));
    }

    /// <summary>Slow path of <see cref="GetTreeAsync"/>: resolves the origin tree via the ODB.</summary>
    private async Task<GitTree?> GetTreeSlowAsync(CancellationToken cancellationToken)
    {
        _tree = await Commit.Owner!.Objects.LookupAsync<GitTree>(Commit.Tree, cancellationToken).ConfigureAwait(false);
        return _tree;
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Blame;

/// <summary> Structure that represents a blame hunk. Managed equivalent of libgit2's <c>git_blame_hunk</c>. </summary>
/// <param name="LinesInHunk">The number of lines in this hunk.</param>
/// <param name="FinalStartLineNumber">The 1-based line number where this hunk begins in the final version of the file.</param>
/// <param name="FinalSignature">The author of <see cref="FinalCommitId"/> (mailmap-applied if <see cref="GitBlameFlags.UseMailmap"/> was set).</param>
/// <param name="FinalCommitter">The committer of <see cref="FinalCommitId"/> (mailmap-applied if <see cref="GitBlameFlags.UseMailmap"/> was set).</param>
/// <param name="FinalCommitId">The OID of the commit where this line was last changed.</param>
/// <param name="OrigStartLineNumber">The 1-based line number where this hunk begins in the file named by <see cref="OrigPath"/> in the commit specified by <see cref="OrigCommitId"/>.</param>
/// <param name="OrigCommitId">The OID of the commit where this hunk was found (usually the same as <see cref="FinalCommitId"/>).</param>
/// <param name="OrigSignature">The author of <see cref="OrigCommitId"/> (mailmap-applied).</param>
/// <param name="OrigCommitter">The committer of <see cref="OrigCommitId"/> (mailmap-applied).</param>
/// <param name="OrigPath">The path to the file where this hunk originated, as of the commit specified by <see cref="OrigCommitId"/>. Byte-faithful <see cref="GitPath"/> — mirrors libgit2's raw <c>const char *orig_path</c>. Use <see cref="GitPath.ToUtf8String"/> for display/API egress.</param>
/// <param name="Summary">The summary (first line) of the commit.</param>
/// <param name="Boundary">1 if the hunk has been tracked to a boundary commit (the root, or the commit specified in <c>oldest_commit</c>); 0 otherwise.</param>
public sealed record BlameHunk(
    int LinesInHunk,
    int FinalStartLineNumber,
    GitSignature FinalSignature,
    GitSignature FinalCommitter,
    GitOid FinalCommitId,
    int OrigStartLineNumber,
    GitOid OrigCommitId,
    GitSignature OrigSignature,
    GitSignature OrigCommitter,
    GitPath OrigPath,
    string? Summary,
    int Boundary);

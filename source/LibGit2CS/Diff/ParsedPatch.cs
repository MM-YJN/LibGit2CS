// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary> A parsed patch: the delta + hunks + lines + optional binary data, plus the six path byte buffers (header/rename/old/new + prefixes) that
/// <c>patch_parse.c</c> tracks. Managed equivalent of <c>git_patch_parsed</c> (<c>patch_parse.c:15-34</c>). </summary> <remarks> <para> In C,
/// <c>git_patch_parsed</c> embeds a <c>git_patch base</c> and is freed via the <c>free_fn</c> vtable. In C#, this is a plain class owned by the <c>Patch</c>
/// facade (via <c>ParsedPatchSource</c>). GC handles reclamation. </para> <para> The <c>Delta</c> is a mutable <see cref="GitDiffDelta"/> (status /
/// modes / OIDs set during parse). The hunks and lines are <see cref="GitDiffHunk"/> / <see cref="GitDiffLine"/> immutable records. The binary data (if
/// present) is a <see cref="GitBinaryPatch"/>. </para> <para> Paths are <see cref="ReadOnlyMemory{T}"/> byte buffers (owned copies), matching C's
/// <c>git_str</c> path storage — the parse domain is raw patch bytes; no decode happens until the delta's <see cref="IO.GitPath"/> is built in
/// <c>check_filenames</c>. </para> </remarks>
internal sealed class ParsedPatch
{
    public GitDiffDelta Delta { get; } = new(GitDeltaStatus.Modified, 2, new GitDiffFile(), new GitDiffFile());

    public List<GitDiffHunk> Hunks { get; } = [];

    public List<GitDiffLine> Lines { get; } = [];

    public GitBinaryPatch? Binary { get; set; }

    public bool IsBinary => (Delta.Flags & GitDiffFileFlags.Binary) != 0;

    public ReadOnlyMemory<byte>? HeaderOldPath { get; set; }
    public ReadOnlyMemory<byte>? HeaderNewPath { get; set; }
    public ReadOnlyMemory<byte>? RenameOldPath { get; set; }
    public ReadOnlyMemory<byte>? RenameNewPath { get; set; }
    public ReadOnlyMemory<byte>? OldPath { get; set; }
    public ReadOnlyMemory<byte>? NewPath { get; set; }
    public ReadOnlyMemory<byte>? OldPrefix { get; set; }
    public ReadOnlyMemory<byte>? NewPrefix { get; set; }

    public GitPatchParseOptions Options { get; }

    public ParsedPatch(GitPatchParseOptions options)
    {
        Options = options;
        Delta.OldFile = new GitDiffFile();
        Delta.NewFile = new GitDiffFile();
    }
}

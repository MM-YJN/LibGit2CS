// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.IO;

namespace LibGit2CS.Merge;

/// <summary> A single side's input to a file-level 3-way merge. Matches <c>git_merge_file_input</c> in <c>include/git2/merge.h</c>. </summary>
/// <param name="Path">The file path (used for conflict-marker labels and best-path selection). If <c>null</c>, <see cref="GitMergeFile.Merge"/> normalizes to <c>"file.txt"</c>. Byte-faithful.</param>
/// <param name="Mode">The file mode (e.g. <c>0100644</c>). If <c>0</c>, <see cref="GitMergeFile.Merge"/> normalizes to <c>0100644</c>.</param>
/// <param name="Contents">The raw file contents.</param>
public readonly record struct GitMergeFileInput(
    GitPath? Path,
    uint Mode,
    ReadOnlyMemory<byte> Contents)
{
    /// <summary>
    /// Convenience factory for in-memory text content. Encodes
    /// <paramref name="text"/> as UTF-8.
    /// </summary>
    public static GitMergeFileInput Create(string? path, uint mode, string text)
        => new(path is null ? null : GitPath.FromUtf8String(path), mode, Encoding.UTF8.GetBytes(text));
}

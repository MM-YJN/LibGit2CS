// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.IO;

namespace LibGit2CS.Merge;

/// <summary>
/// The output of a successful merge driver apply: the merged path, mode,
/// and content. Replaces the <c>path_out</c> / <c>mode_out</c> /
/// <c>merged_out</c> output parameters of <c>git_merge_driver_apply_fn</c>.
/// </summary>
public sealed record GitMergeDriverOutput(
    GitPath? Path,
    uint Mode,
    ReadOnlyMemory<byte> Content);

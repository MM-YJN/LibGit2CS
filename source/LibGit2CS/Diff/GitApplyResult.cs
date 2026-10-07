// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Diff;

/// <summary>
/// Result of applying a patch to source content.
/// </summary>
public sealed record GitApplyResult(
    ReadOnlyMemory<byte> Content,
    GitPath? Filename,
    GitFileMode Mode);

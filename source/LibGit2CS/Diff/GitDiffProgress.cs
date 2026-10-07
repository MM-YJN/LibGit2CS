// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.IO;

namespace LibGit2CS.Diff;

/// <summary> Progress payload reported per path during the iterator walk. Byte-faithful. </summary>
public readonly record struct GitDiffProgress(GitPath? OldPath, GitPath? NewPath);

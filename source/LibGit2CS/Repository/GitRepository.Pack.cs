// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Pack;

namespace LibGit2CS.Repository;

public sealed partial class GitRepository
{
    /// <summary>
    /// Creates a pack writer for this repository. Matches <c>git_packbuilder_new</c>.
    /// The caller owns the returned writer and must dispose it.
    /// </summary>
    /// <param name="threads">Requested thread count; 0 means auto-detect. The managed writer runs single-threaded.</param>
    public GitPackWriter NewPackWriter(int threads = 1)
        => new(this, threads);
}

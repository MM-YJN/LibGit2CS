// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;

namespace LibGit2CS.Checkout;

/// <summary>
/// Per-entry action result from the checkout walk.
/// </summary>
internal sealed class CheckoutEntryAction
{
    public GitPath Path;
    public CheckoutAction Action;
    public GitDiffDelta? Delta;
    public GitIndexEntry? WorkdirEntry;
}

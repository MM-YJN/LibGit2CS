// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Diff;
using LibGit2CS.IO;

namespace LibGit2CS.Checkout;

/// <summary>
/// A checkout notification event. Passed to the
/// <see cref="GitCheckoutOptions.Notify"/> callback. Maps to the parameters of
/// <c>git_checkout_notify_cb</c> in <c>include/git2/checkout.h:94-101</c>.
/// </summary>
/// <param name="Why">The notification reason (which <see cref="GitCheckoutNotifyFlags"/> bit triggered it).</param>
/// <param name="Path">The path of the affected file, relative to the repo root
/// (byte-faithful <see cref="GitPath"/> — mirrors libgit2's raw
/// <c>const char *path</c>). Use <see cref="GitPath.ToUtf8String"/> for display.</param>
/// <param name="Baseline">The baseline diff file (from HEAD or user-provided), or null.</param>
/// <param name="Target">The target diff file (from the tree/index being checked out), or null.</param>
/// <param name="Workdir">The workdir diff file (current state on disk), or null.</param>
public readonly record struct GitCheckoutNotification(
    GitCheckoutNotifyFlags Why,
    GitPath Path,
    GitDiffFile? Baseline,
    GitDiffFile? Target,
    GitDiffFile? Workdir);

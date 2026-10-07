// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Checkout;

/// <summary>
/// Checkout progress event. Passed to the
/// <see cref="GitCheckoutOptions.Progress"/> callback. Maps to the parameters of
/// <c>git_checkout_progress_cb</c> in <c>include/git2/checkout.h:110-116</c>.
/// </summary>
/// <param name="Path">The path just processed, or empty for the initial
/// baseline report (byte-faithful <see cref="GitPath"/> — the C
/// <c>const char *path</c> is NULL for the baseline report; the non-nullable
/// <see cref="GitPath"/> defaults to empty). Use <see cref="GitPath.IsEmpty"/>
/// to detect the baseline report; <see cref="GitPath.ToUtf8String"/> for display.</param>
/// <param name="CompletedSteps">Number of steps completed so far.</param>
/// <param name="TotalSteps">Total number of steps in the checkout.</param>
public readonly record struct GitCheckoutProgress(
    GitPath Path,
    long CompletedSteps,
    long TotalSteps);

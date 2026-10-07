// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Checkout;

/// <summary>
/// Checkout performance counters. Maps to <c>git_checkout_perfdata</c> in
/// <c>include/git2/checkout.h:122-127</c>.
/// </summary>
/// <param name="MkdirCalls">Number of <c>mkdir</c> calls made during checkout.</param>
/// <param name="StatCalls">Number of <c>stat</c>/<c>lstat</c> calls made.</param>
/// <param name="ChmodCalls">Number of <c>chmod</c> calls made.</param>
public readonly record struct GitCheckoutPerformance(
    long MkdirCalls,
    long StatCalls,
    long ChmodCalls);

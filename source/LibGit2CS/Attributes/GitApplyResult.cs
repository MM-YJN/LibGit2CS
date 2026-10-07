// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Attributes;

/// <summary>
/// Result of <see cref="IFilter.ApplyAsync"/>. When <see cref="Applied"/> is
/// false, the input is passed through unchanged (passthrough). When true,
/// <see cref="Output"/> contains the filtered bytes.
/// </summary>
public readonly record struct GitApplyResult(bool Applied, byte[]? Output)
{
    /// <summary>Creates a passthrough result (input unchanged).</summary>
    public static readonly GitApplyResult Passthrough = new(false, null);

    /// <summary>Creates an applied result with the given output.</summary>
    public static GitApplyResult WithOutput(byte[] output) => new(true, output);
}

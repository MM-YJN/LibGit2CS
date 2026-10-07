// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Text;

namespace LibGit2CS.Utils;

internal static class ProgressExtensions
{
    public static void Report(this IProgress<string> progress, ReadOnlySpan<byte> utf8Bytes)
    {
        progress?.Report(Encoding.UTF8.GetString(utf8Bytes));
    }
}

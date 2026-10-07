// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.IO;

/// <summary>
/// The result of an <see cref="IObjectReader.ReadAsync"/> call — bundles the
/// <see cref="ReadStatus"/> with the optional <see cref="ReaderResult"/>.
/// </summary>
internal readonly record struct ReaderReadResult(ReadStatus Status, ReaderResult? Result)
{
    /// <summary>True when the read succeeded (file found).</summary>
    public bool IsFound => Status == ReadStatus.Found && Result is not null;
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Core;
using LibGit2CS.Objects;

namespace LibGit2CS.IO;

/// <summary>
/// The content/OID/mode triple for a successfully read file.
/// </summary>
internal sealed record ReaderResult(
    ReadOnlyMemory<byte> Content,
    GitOid Oid,
    GitFileMode Mode);

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Objects;

/// <summary>
/// Header of a git object: type and size. Returned by
/// <see cref="GitObjectDb.ReadHeaderAsync"/> without reading the full object body.
/// </summary>
public readonly record struct GitObjectHeader(GitObjectType Type, long Size);

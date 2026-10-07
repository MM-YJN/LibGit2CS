// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary>
/// Internal concrete <see cref="GitObject"/> holding raw bytes before type-specific
/// parsing. Used as a fallback when the dispatch table can't resolve a type
/// (e.g. <see cref="GitObjectType.Ext1"/>/<see cref="GitObjectType.Ext2"/>); also used
/// for the hardcoded empty blob/tree objects which have no body to parse.
/// </summary>
internal sealed class RawGitObject(GitRepository? owner, GitOid id, GitObjectType type, long size, ReadOnlyMemory<byte> raw) : GitObject(owner, id, type, size, raw);

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// A single key/value pair from a commit message trailer block.
/// Managed port of libgit2's <c>git_message_trailer</c>
/// (include/git2/message.h:43-46).
/// </summary>
public readonly record struct GitMessageTrailer(string Key, string Value);

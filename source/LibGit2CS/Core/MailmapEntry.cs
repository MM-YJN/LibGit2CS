// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

namespace LibGit2CS.Core;

/// <summary> A single mailmap entry mapping a replace_email (+ optional replace_name) to a real_name + real_email. Matches libgit2's <c>git_mailmap_entry</c>.
/// </summary> <remarks> all fields are raw bytes — libgit2 stores the parsed <c>char *</c> segments verbatim (mailmap.c:98-149) and compares them with
/// <c>git__strcmp</c> (mailmap.c:42-59), so non-UTF-8 name/email bytes round-trip byte-exact through parse and lookup. The string members are UTF-8 display
/// decodes. </remarks>
internal sealed class MailmapEntry
{
    public ReadOnlyMemory<byte>? RealNameBytes { get; set; }
    public ReadOnlyMemory<byte>? RealEmailBytes { get; set; }
    public ReadOnlyMemory<byte>? ReplaceNameBytes { get; set; }
    public ReadOnlyMemory<byte> ReplaceEmailBytes { get; set; }

    /// <summary>UTF-8 display decode of <see cref="RealNameBytes"/>.</summary>
    public string? RealName => RealNameBytes is { } b ? Encoding.UTF8.GetString(b.Span) : null;

    /// <summary>UTF-8 display decode of <see cref="RealEmailBytes"/>.</summary>
    public string? RealEmail => RealEmailBytes is { } b ? Encoding.UTF8.GetString(b.Span) : null;

    /// <summary>UTF-8 display decode of <see cref="ReplaceNameBytes"/>.</summary>
    public string? ReplaceName => ReplaceNameBytes is { } b ? Encoding.UTF8.GetString(b.Span) : null;

    /// <summary>UTF-8 display decode of <see cref="ReplaceEmailBytes"/>.</summary>
    public string ReplaceEmail => Encoding.UTF8.GetString(ReplaceEmailBytes.Span);
}

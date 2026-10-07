// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;

namespace LibGit2CS.Core;

/// <summary>
/// A point in time expressed as Unix epoch seconds plus a UTC offset in minutes.
/// Managed equivalent of libgit2's <c>git_time</c>.
/// </summary>
/// <param name="Seconds">Seconds since 1970-01-01T00:00:00Z (Unix epoch).</param>
/// <param name="OffsetMinutes">East-of-UTC offset in minutes (e.g. <c>+0200</c> → <c>120</c>, <c>-0530</c> → <c>-330</c>).</param>
/// <param name="Sign">
/// The sign character from the original timezone text (<c>'-'</c> or <c>'+'</c>),
/// or <c>'\0'</c> when the sign is derived from <paramref name="OffsetMinutes"/>.
/// Matches C's <c>git_time.sign</c>, which preserves <c>'-'</c> even for a zero
/// offset (signature.c:387-392) — so <c>-0000</c> re-serializes as <c>-0000</c>.
/// </param>
public readonly record struct GitTime(long Seconds, int OffsetMinutes, char Sign = '\0') : ISpanFormattable
{
    /// <summary>
    /// Formats the offset as <c>+HHMM</c> / <c>-HHMM</c>. Matches
    /// <c>git_signature__writebuf</c>'s sign derivation (signature.c:430):
    /// <c>sign = (offset &lt; 0 || sign == '-') ? '-' : '+'</c>.
    /// </summary>
    public string FormatOffset()
    {
        int abs = Math.Abs(OffsetMinutes);
        int hours = abs / 60;
        int mins = abs % 60;
        char sign = OffsetMinutes < 0 || Sign == '-' ? '-' : '+';
        return $"{sign}{hours:D2}{mins:D2}";
    }

    /// <summary>
    /// Renders as <c>Seconds Offset</c> (the git commit/tag object header form, e.g. <c>1461698037 +0200</c>).
    /// </summary>
    public override string ToString() => ToString(null, null);

    /// <summary>Formats this value into the destination; returns false if the buffer is too small.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        int abs = Math.Abs(OffsetMinutes);
        int hours = abs / 60;
        int mins = abs % 60;
        char sign = OffsetMinutes < 0 || Sign == '-' ? '-' : '+';
        return destination.TryWrite(CultureInfo.InvariantCulture, $"{Seconds} {sign}{hours:D2}{mins:D2}", out charsWritten);
    }

    /// <summary>Returns the Git textual representation of this value.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        int abs = Math.Abs(OffsetMinutes);
        int hours = abs / 60;
        int mins = abs % 60;
        char sign = OffsetMinutes < 0 || Sign == '-' ? '-' : '+';
        return $"{Seconds} {sign}{hours:D2}{mins:D2}";
    }
}

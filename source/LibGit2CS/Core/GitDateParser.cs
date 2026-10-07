// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LibGit2CS.Core;

/// <summary>
/// Parses git date/time formats. Managed port of <c>src/util/date.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Supports the full range of git date formats:
/// </para>
/// <list type="bullet">
/// <item>Object-header timestamps: <c>1461698037 +0200</c></item>
/// <item><c>@</c>-prefixed: <c>@1461698037 +0200</c></item>
/// <item>ISO 8601: <c>2025-01-15</c>, <c>2025-01-15 10:30:00</c></item>
/// <item>RFC 2822: <c>Mon, 15 Jan 2025 10:30:00 +0200</c></item>
/// <item>Approxidate: <c>"2 weeks ago"</c>, <c>"yesterday"</c>, <c>"last tuesday"</c>,
/// <c>"Jan 15 2025"</c>, <c>"10:30 AM"</c>, <c>"noon"</c>, <c>"5.minutes.ago"</c></item>
/// </list>
/// <para>
/// The only consumer of approxidate is revparse's <c>@{date}</c> syntax.
/// </para>
/// </remarks>
public static class GitDateParser
{
    /// <summary>
    /// Formats a timestamp + offset in RFC 2822 form. Matches
    /// <c>git_date_rfc2822_fmt</c> (date.c:887-906). Used by
    /// <c>EmailFormatter</c> for the <c>Date:</c> header.
    /// </summary>
    /// <example><c>"Wed, 9 Apr 2014 20:57:01 +0200"</c></example>
    public static string FormatRfc2822(long seconds, int offsetMinutes)
    {
        // Compute the local time by adding the offset, then convert to UTC
        // components (matches C: t = time + offset*60; gmtime_r(&t, &gmt)).
        long localSeconds = seconds + (long)offsetMinutes * 60;
        DateTime dt = DateTimeOffset.FromUnixTimeSeconds(localSeconds).UtcDateTime;

        // C format: "%.3s, %u %.3s %.4u %02u:%02u:%02u %+03d%02d"
        // weekday (3-char abbr), day-of-month (no padding), month (3-char abbr),
        // year (4-digit), time (2-digit each), offset (sign + 2-digit hours + 2-digit mins).
        ReadOnlySpan<char> weekday = s_weekdayNames[(int)dt.DayOfWeek].AsSpan(0, 3);
        ReadOnlySpan<char> month = s_monthNames[dt.Month - 1].AsSpan(0, 3);
        int offsetHours = offsetMinutes / 60;
        int offsetMins = offsetMinutes % 60;

        // C prints the offset as "%+03d%02d" of (offset/60, offset%60): the
        // minutes keep their own sign when negative — "-05-45" for -345,
        // "+00-1" for -1 (verified against git_date_rfc2822_fmt). Positive
        // minutes are zero-padded ("+0545").
        if (offsetMins < 0)
        {
            return $"{weekday}, {dt.Day} {month} {dt.Year:D4} " +
                   $"{dt.Hour:D2}:{dt.Minute:D2}:{dt.Second:D2} " +
                   $"{(offsetHours < 0 ? '-' : '+')}{Math.Abs(offsetHours):D2}{offsetMins}";
        }

        return $"{weekday}, {dt.Day} {month} {dt.Year:D4} " +
               $"{dt.Hour:D2}:{dt.Minute:D2}:{dt.Second:D2} " +
               $"{(offsetHours < 0 ? '-' : '+')}{Math.Abs(offsetHours):D2}{offsetMins:D2}";
    }

    /// <summary>
    /// Parses a git object-header timestamp: <c>&lt;unix-seconds&gt; &lt;+/-HHMM&gt;</c>.
    /// Matches libgit2's <c>match_object_header_date</c>.
    /// </summary>
    /// <example><c>"1461698037 +0200"</c></example>
    /// <returns>True if the input matches the object-header date format.</returns>
    public static bool TryParseObjectHeader(ReadOnlySpan<char> input, out GitTime value)
    {
        value = default;

        if (input.IsEmpty || input[0] is < '0' or > '9')
        {
            return false;
        }

        // Parse the leading integer (epoch seconds).
        int i = 0;
        while (i < input.Length && input[i] is >= '0' and <= '9')
        {
            i++;
        }

        if (i == 0 || i >= input.Length || input[i] != ' ')
        {
            return false;
        }

        if (!long.TryParse(input[..i], CultureInfo.InvariantCulture, out long seconds))
        {
            return false;
        }

        // Need at least: space + sign + 4 digits = 6 more chars after the number.
        if (i + 5 >= input.Length)
        {
            return false;
        }

        char sign = input[i + 1];
        if (sign is not ('+' or '-'))
        {
            return false;
        }

        // The 4 digits of HHMM must immediately follow the sign.
        if (!int.TryParse(input.Slice(i + 2, 4), CultureInfo.InvariantCulture, out int hhmm))
        {
            return false;
        }

        // Reject anything trailing other than newline/NUL.
        if (i + 6 < input.Length && input[i + 6] is not '\n' and not '\0')
        {
            return false;
        }

        int hours = hhmm / 100;
        int mins = hhmm % 100;
        if (hours > 14 || mins > 59)
        {
            return false;
        }

        int offset = hours * 60 + mins;
        if (sign == '-')
        {
            offset = -offset;
        }

        value = new GitTime(seconds, offset);
        return true;
    }

    /// <summary>
    /// Parses a <c>+/-HHMM</c> or <c>+/-HH:MM</c> timezone offset into minutes.
    /// Matches libgit2's <c>match_tz</c>.
    /// </summary>
    /// <param name="input">A span starting at the <c>+</c>/<c>-</c> sign.</param>
    /// <param name="offsetMinutes">The parsed offset in minutes (signed).</param>
    /// <returns>The number of characters consumed.</returns>
    [SuppressMessage("Design", "CA1021:Avoid out parameters", Justification = "'out' is the idiomatic .NET TryParse signature")]
    public static int TryParseOffset(ReadOnlySpan<char> input, out int offsetMinutes)
    {
        offsetMinutes = 0;
        if (input.IsEmpty || input[0] is not ('+' or '-'))
        {
            return 0;
        }

        char sign = input[0];
        int i = 1;

        // Read contiguous digits.
        int digits = 0;
        while (i < input.Length && input[i] is >= '0' and <= '9')
        {
            digits++;
            i++;
        }

        int hour;
        int min;

        if (digits == 4)
        {
            // hhmm
            if (!int.TryParse(input.Slice(1, 4), CultureInfo.InvariantCulture, out int hhmm))
            {
                return 0;
            }

            hour = hhmm / 100;
            min = hhmm % 100;
        }
        else if (digits == 2 && i < input.Length && input[i] == ':')
        {
            // hh:mm
            if (!int.TryParse(input.Slice(1, 2), CultureInfo.InvariantCulture, out hour))
            {
                return 0;
            }

            int colon = i + 1;
            if (colon + 2 > input.Length ||
                !int.TryParse(input.Slice(colon, 2), CultureInfo.InvariantCulture, out min))
            {
                return 0;
            }

            i = colon + 2;
        }
        else
        {
            // Not a recognized tz format. Matches libgit2's "random stuff" fallback
            // (it leaves the offset unchanged); we simply fail to parse.
            return 0;
        }

        if (hour >= 24 || min >= 60)
        {
            return 0;
        }

        offsetMinutes = hour * 60 + min;
        if (sign == '-')
        {
            offsetMinutes = -offsetMinutes;
        }

        return i;
    }

    /// <summary>
    /// Parses a full date string into a <see cref="GitTime"/>. Accepts the
    /// object-header format (<c>1461698037 +0200</c>), the <c>@</c>-prefixed
    /// form, strict date formats (ISO 8601, RFC 2822), and approxidate
    /// (<c>"2 weeks ago"</c>, <c>"yesterday"</c>, etc.).
    /// </summary>
    /// <exception cref="FormatException">Input is not a recognized date format.</exception>
    public static GitTime Parse(ReadOnlySpan<char> input)
    {
        if (TryParse(input, out GitTime value))
        {
            return value;
        }

        throw new FormatException($"Date format not recognized: '{input}'");
    }

    /// <summary>
    /// Parses a date string into a <see cref="GitTime"/>. Accepts the
    /// object-header format (only via the <c>@</c> prefix, matching C's
    /// parse_date_basic), strict date formats, and approxidate. Returns
    /// false if the input cannot be parsed.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> input, out GitTime value)
    {
        // C's parse_date_basic handles the object-header format only behind
        // the '@' prefix (date.c:506-508); a bare "1461698037 +0200" goes
        // through the epoch path of match_digit, which requires >= 9 digits
        // and an encodable year. The public TryParseObjectHeader must not be
        // tried on bare input ("0 +0000" must fall through to approxidate).
        if (!input.IsEmpty && input[0] == '@' &&
            TryParseObjectHeader(input[1..], out value))
        {
            return true;
        }

        if (ParseDateBasic(input, out long timestamp, out int offset))
        {
            value = new GitTime(timestamp, offset);
            return true;
        }

        if (Approxidate(input, out timestamp))
        {
            value = new GitTime(timestamp, 0);
            return true;
        }

        value = default;
        return false;
    }

    // ===== Strict date parser (parse_date_basic) =====

    /// <summary>
    /// Strict date parser. Handles ISO 8601, RFC 2822, raw timestamps, and
    /// named timezones. Matches libgit2's <c>parse_date_basic</c>.
    /// </summary>
    /// <returns>True on success.</returns>
    internal static bool ParseDateBasic(ReadOnlySpan<char> date, out long timestamp, out int offset)
    {
        // C (date.c:495-503): memset(&tm, 0) then tm_year = tm_mon = tm_mday
        // = tm_hour = tm_min = tm_sec = -1. The -1 sentinels matter: nodate(),
        // the day/month guards and tm_to_time_t's hour check all rely on them
        // (a 0-filled struct parses "2005-04-07" as midnight, drops the day in
        // RFC 2822 "day month" ordering, and defeats the epoch >= 9-digit path).
        var tm = new ApproxidateTm
        {
            Year = -1,
            Mon = -1,
            Mday = -1,
            Hour = -1,
            Min = -1,
            Sec = -1,
        };

        offset = -1;
        bool tmGmt = false;

        // @-prefixed raw timestamp. C's match_object_header_date writes the
        // outputs only on success, so a failed match must not clobber
        // offset's -1 sentinel (which would suppress the local-time fallback).
        if (!date.IsEmpty && date[0] == '@')
        {
            if (MatchObjectHeaderDate(date.Slice(1), out long atTimestamp, out int atOffset))
            {
                timestamp = atTimestamp;
                offset = atOffset;
                return true;
            }
        }

        int pos = 0;
        while (pos < date.Length)
        {
            char c = date[pos];
            if (c is '\0' or '\n')
            {
                break;
            }

            int match = 0;
            if (char.IsAsciiLetter(c))
            {
                match = MatchAlpha(date, pos, ref tm, ref offset);
            }
            else if (char.IsAsciiDigit(c))
            {
                match = MatchDigit(date, pos, ref tm, ref offset, ref tmGmt);
            }
            else if ((c == '-' || c == '+') && pos + 1 < date.Length && char.IsAsciiDigit(date[pos + 1]))
            {
                match = MatchTz(date, pos, ref offset);
            }

            if (match == 0)
            {
                match = 1;
            }

            pos += match;
        }

        timestamp = TmToTimeT(ref tm);
        if (offset == -1)
        {
            // Compute the offset from the local timezone, matching C
            // (date.c:534-535): offset = (naive UTC timestamp - mktime(&tm)) / 60
            // = UTC - local. EST → -300, not +300.
            if (tmGmt)
            {
                // The epoch path ran gmtime_r over the tm, which clobbers
                // tm_isdst back to 0 (date.c:338), so C's mktime uses the
                // zone's STANDARD offset here: offset = (naive - mktime)/60
                // = the standard offset itself (EST → -300). Only the
                // reported offset is affected (tm_gmt skips the adjustment).
                offset = (int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes;
            }
            else
            {
                try
                {
                    // mktime-style normalization: out-of-range fields roll over
                    // (2005-02-29 → 2005-03-01) instead of throwing.
                    DateTime dt = NormalizeLocal(tm.Year, tm.Mon, tm.Mday, tm.Hour, tm.Min, tm.Sec);
                    TimeZoneInfo local = TimeZoneInfo.Local;
                    DateTime utc = TimeZoneInfo.ConvertTimeToUtc(dt, local);
                    offset = (int)(dt - utc).TotalMinutes;
                }
                catch (ArgumentOutOfRangeException)
                {
                    offset = 0;
                }
            }
        }

        if (timestamp == -1)
        {
            return false;
        }

        if (!tmGmt)
        {
            timestamp -= offset * 60L;
        }

        return true;
    }

    /// <summary>
    /// Parses <c>&lt;unix-seconds&gt; &lt;+/-HHMM&gt;</c> (the internal
    /// match_object_header_date, separate from the public TryParseObjectHeader
    /// which returns GitTime).
    /// </summary>
    private static bool MatchObjectHeaderDate(ReadOnlySpan<char> date, out long timestamp, out int offset)
    {
        timestamp = 0;
        offset = 0;

        if (date.IsEmpty || date[0] is < '0' or > '9')
        {
            return false;
        }

        int i = 0;
        while (i < date.Length && date[i] is >= '0' and <= '9')
        {
            i++;
        }

        if (i >= date.Length || date[i] != ' ')
        {
            return false;
        }

        if (!long.TryParse(date[..i], CultureInfo.InvariantCulture, out timestamp))
        {
            return false;
        }

        if (i + 5 >= date.Length || (date[i + 1] != '+' && date[i + 1] != '-'))
        {
            return false;
        }

        if (!int.TryParse(date.Slice(i + 2, 4), CultureInfo.InvariantCulture, out int ofs))
        {
            return false;
        }

        // C (date.c:470-473): the offset must be exactly 4 digits with nothing
        // trailing — the char after them must be NUL or '\n'.
        if (i + 6 < date.Length && date[i + 6] is not '\n' and not '\0')
        {
            return false;
        }

        offset = (ofs / 100) * 60 + (ofs % 100);
        if (date[i + 1] == '-')
        {
            offset = -offset;
        }

        return true;
    }

    // ===== Approxidate engine (approxidate_str) =====

    /// <summary>
    /// Flexible natural-language date parser. Matches libgit2's
    /// <c>approxidate_str</c>. Handles <c>"2 weeks ago"</c>,
    /// <c>"yesterday"</c>, <c>"last tuesday"</c>, <c>"Jan 15"</c>, etc.
    /// </summary>
    internal static bool Approxidate(ReadOnlySpan<char> date, out long timestamp)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var tm = new ApproxidateTm
        {
            Year = now.Year - 1900,
            Mon = now.Month - 1,
            Mday = now.Day,
            Hour = now.Hour,
            Min = now.Minute,
            Sec = now.Second,
            Wday = (int)now.DayOfWeek,
            // C: struct tm tm = {0}, now; then p_localtime_r(&time_sec, &tm)
            // — tm_isdst keeps the DST flag of the CURRENT local time and is
            // never reset, so every later mktime() assumes DST is in effect
            // ("in my world, it's always summer", date.c:66-70). Only
            // date_never() overwrites it (localtime_r of epoch 0).
            IsDst = TimeZoneInfo.Local.IsDaylightSavingTime(now) ? 1 : 0,
        };

        ApproxidateTm nowTm = tm;
        tm.Year = -1;
        tm.Mon = -1;
        tm.Mday = -1;

        int number = 0;
        bool touched = false;
        int pos = 0;

        while (pos < date.Length)
        {
            char c = date[pos];
            if (c == '\0')
            {
                break;
            }

            pos++;
            if (char.IsAsciiDigit(c))
            {
                PendingNumber(ref tm, ref number);
                pos = ApproxidateDigit(date, pos - 1, ref tm, ref number);
                touched = true;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                pos = ApproxidateAlpha(date, pos - 1, ref tm, ref nowTm, ref number, ref touched);
            }
        }

        PendingNumber(ref tm, ref number);
        timestamp = UpdateTm(ref tm, ref nowTm, 0);
        return touched;
    }

    /// <summary>
    /// Parse alphabetic tokens in approxidate: month names, special words
    /// (yesterday/noon/midnight/tea/now/never/AM/PM), number words
    /// (one..ten), time units (seconds/minutes/hours/days/weeks), weekday
    /// names, "months", "years", "last". Matches <c>approxidate_alpha</c>.
    /// </summary>
    private static int ApproxidateAlpha(ReadOnlySpan<char> date, int start, ref ApproxidateTm tm, ref ApproxidateTm now, ref int number, ref bool touched)
    {
        // Scan to end of alpha run.
        int end = start + 1;
        while (end < date.Length && char.IsAsciiLetter(date[end]))
        {
            end++;
        }

        // Try month names.
        for (int i = 0; i < 12; i++)
        {
            int match = MatchString(date, start, s_monthNames[i]);
            if (match >= 3)
            {
                tm.Mon = i;
                touched = true;
                return end;
            }
        }

        // Try special words.
        foreach ((string? name, SpecialKind kind) in s_specialWords)
        {
            if (MatchString(date, start, name) == name.Length)
            {
                ApplySpecial(ref tm, ref now, ref number, kind);
                touched = true;
                return end;
            }
        }

        // Try number words (one..ten) when no pending number.
        if (number == 0)
        {
            for (int i = 1; i < s_numberNames.Length; i++)
            {
                if (MatchString(date, start, s_numberNames[i]) == s_numberNames[i].Length)
                {
                    number = i;
                    touched = true;
                    return end;
                }
            }

            if (MatchString(date, start, "last") == 4)
            {
                number = 1;
                touched = true;
            }

            return end;
        }

        // Try time units (seconds/minutes/hours/days/weeks).
        foreach ((string? type, int length) in s_typeLengths)
        {
            if (MatchString(date, start, type) >= type.Length - 1)
            {
                _ = UpdateTm(ref tm, ref now, length * (uint)number);
                number = 0;
                touched = true;
                return end;
            }
        }

        // Try weekday names.
        for (int i = 0; i < 7; i++)
        {
            int match = MatchString(date, start, s_weekdayNames[i]);
            if (match >= 3)
            {
                int n = number - 1;
                number = 0;
                int diff = tm.Wday - i;
                if (diff <= 0)
                {
                    n++;
                }

                diff += 7 * n;
                _ = UpdateTm(ref tm, ref now, diff * 24 * 60 * 60);
                touched = true;
                return end;
            }
        }

        // "months" — subtract N months.
        if (MatchString(date, start, "months") >= 5)
        {
            _ = UpdateTm(ref tm, ref now, 0);
            int n = tm.Mon - number;
            number = 0;
            while (n < 0)
            {
                n += 12;
                tm.Year--;
            }

            tm.Mon = n;
            touched = true;
            return end;
        }

        // "years" — subtract N years.
        if (MatchString(date, start, "years") >= 4)
        {
            _ = UpdateTm(ref tm, ref now, 0);
            tm.Year -= number;
            number = 0;
            touched = true;
            return end;
        }

        return end;
    }

    /// <summary>
    /// Parse numeric tokens in approxidate context. Matches
    /// <c>approxidate_digit</c>.
    /// </summary>
    private static int ApproxidateDigit(ReadOnlySpan<char> date, int start, ref ApproxidateTm tm, ref int number)
    {
        int pos = start;
        while (pos < date.Length && char.IsAsciiDigit(date[pos]))
        {
            pos++;
        }

        ReadOnlySpan<char> numStr = date.Slice(start, pos - start);
        if (!ulong.TryParse(numStr, CultureInfo.InvariantCulture, out ulong numberVal))
        {
            return pos;
        }

        // Check for multi-number formats: num[-.:/]num[same]num
        if (pos < date.Length)
        {
            char c = date[pos];
            if ((c == ':' || c == '.' || c == '/' || c == '-') &&
                pos + 1 < date.Length && char.IsAsciiDigit(date[pos + 1]))
            {
                int match = MatchMultiNumber(numberVal, c, date, start, pos, ref tm);
                if (match > 0)
                {
                    return start + match;
                }
            }
        }

        // Accept zero-padding only for small numbers ("Dec 02", never "Dec 0002").
        if (date[start] != '0' || pos - start <= 2)
        {
            number = (int)numberVal;
        }

        return pos;
    }

    /// <summary>
    /// Resolve a pending number as mday/month/year. Matches
    /// <c>pending_number</c>.
    /// </summary>
    private static void PendingNumber(ref ApproxidateTm tm, ref int number)
    {
        if (number == 0)
        {
            return;
        }

        int n = number;
        number = 0;
        if (tm.Mday < 0 && n < 32)
        {
            tm.Mday = n;
        }
        else if (tm.Mon < 0 && n < 13)
        {
            tm.Mon = n - 1;
        }
        else if (tm.Year < 0)
        {
            if (n is > 1969 and < 2100)
            {
                tm.Year = n - 1900;
            }
            else if (n is > 69 and < 100)
            {
                tm.Year = n;
            }
            else if (n < 38)
            {
                tm.Year = 100 + n;
            }
        }
    }

    /// <summary>
    /// Relative time update: fill in unset fields from <paramref name="now"/>,
    /// then shift by <paramref name="sec"/> seconds. Matches
    /// <c>update_tm</c>.
    /// </summary>
    private static long UpdateTm(ref ApproxidateTm tm, ref ApproxidateTm now, long sec)
    {
        if (tm.Mday < 0)
        {
            tm.Mday = now.Mday;
        }

        if (tm.Mon < 0)
        {
            tm.Mon = now.Mon;
        }

        if (tm.Year < 0)
        {
            tm.Year = now.Year;
            if (tm.Mon > now.Mon)
            {
                tm.Year--;
            }
        }

        try
        {
            // mktime-style normalization: out-of-range fields roll over
            // (2005-02-29 → 2005-03-01, hour 24 → next day) instead of
            // throwing, matching C's update_tm (date.c:550-567).
            DateTime dt = NormalizeLocal(tm.Year, tm.Mon, tm.Mday, tm.Hour, tm.Min, tm.Sec);
            TimeZoneInfo local = TimeZoneInfo.Local;

            // C: n = mktime(tm) - sec with tm_isdst left over from the initial
            // localtime_r — the zone offset is the DST offset of NOW whenever
            // DST is currently in effect, the standard offset otherwise
            // (date_never sets isdst=0 → always standard).
            TimeSpan standard = local.BaseUtcOffset;
            TimeSpan dstOrStandard = local.GetUtcOffset(DateTimeOffset.Now);
            TimeSpan forced = tm.IsDst != 0 && dstOrStandard != standard ? dstOrStandard : standard;
            DateTime utc = dt - forced;

            // SpecifyKind(Utc): the shifted instant is a UTC wall clock; a
            // Kind=Unspecified DateTime would be re-interpreted as local by
            // the DateTimeOffset conversion below (double offset).
            var shifted = System.DateTime.SpecifyKind(utc.AddSeconds(-sec), DateTimeKind.Utc);
            // p_localtime_r is date-aware: the shifted instant's local fields
            // use the DATE-correct offset (and refresh the isdst flag).
            DateTime localShifted = TimeZoneInfo.ConvertTimeFromUtc(shifted, local);
            tm.Year = localShifted.Year - 1900;
            tm.Mon = localShifted.Month - 1;
            tm.Mday = localShifted.Day;
            tm.Hour = localShifted.Hour;
            tm.Min = localShifted.Minute;
            tm.Sec = localShifted.Second;
            tm.Wday = (int)localShifted.DayOfWeek;
            tm.IsDst = local.IsDaylightSavingTime(localShifted) ? 1 : 0;
            return ((DateTimeOffset)shifted).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Builds a local naive <see cref="DateTime"/> from broken-down fields
    /// (years-since-1900, 0-based month, like C's <c>struct tm</c>) with
    /// mktime-style normalization: out-of-range days/months/hours roll over
    /// instead of throwing. Matches C's mktime for the 1970-2099 range used
    /// by the date parser.
    /// </summary>
    private static DateTime NormalizeLocal(int year, int month, int day, int hour, int min, int sec)
        => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .AddYears(year - 70)
            .AddMonths(month)
            .AddDays(day - 1)
            .AddHours(hour)
            .AddMinutes(min)
            .AddSeconds(sec);

    // ===== Strict parser helpers (match_alpha, match_digit, etc.) =====

    /// <summary>Matches month, weekday, or timezone name. Matches <c>match_alpha</c>.</summary>
    private static int MatchAlpha(ReadOnlySpan<char> date, int start, ref ApproxidateTm tm, ref int offset)
    {
        // Month names.
        for (int i = 0; i < 12; i++)
        {
            int match = MatchString(date, start, s_monthNames[i]);
            if (match >= 3)
            {
                tm.Mon = i;
                return match;
            }
        }

        // Weekday names.
        for (int i = 0; i < 7; i++)
        {
            int match = MatchString(date, start, s_weekdayNames[i]);
            if (match >= 3)
            {
                tm.Wday = i;
                return match;
            }
        }

        // Timezone names.
        foreach ((string? name, int off, int dst) in s_timezoneNames)
        {
            int match = MatchString(date, start, name);
            if (match >= 3 || match == name.Length)
            {
                int totalOff = off + dst;
                if (offset == -1)
                {
                    offset = 60 * totalOff;
                }

                return match;
            }
        }

        // AM / PM.
        if (MatchString(date, start, "PM") == 2)
        {
            tm.Hour = (tm.Hour % 12) + 12;
            return 2;
        }

        if (MatchString(date, start, "AM") == 2)
        {
            tm.Hour %= 12;
            return 2;
        }

        // Unknown alpha — skip.
        return SkipAlpha(date, start);
    }

    /// <summary>Matches numeric date/time components. Matches <c>match_digit</c>.</summary>
    private static int MatchDigit(ReadOnlySpan<char> date, int start, ref ApproxidateTm tm, ref int offset, ref bool tmGmt)
    {
        int pos = start;
        while (pos < date.Length && char.IsAsciiDigit(date[pos]))
        {
            pos++;
        }

        int n = pos - start;
        if (!ulong.TryParse(date.Slice(start, n), CultureInfo.InvariantCulture, out ulong num))
        {
            return n;
        }

        // Raw timestamp (≥ 8 digits, no prior date fields).
        if (num >= 100000000 && NoDate(ref tm))
        {
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds((long)num);
                tm.Year = dt.Year - 1900;
                tm.Mon = dt.Month - 1;
                tm.Mday = dt.Day;
                tm.Hour = dt.Hour;
                tm.Min = dt.Minute;
                tm.Sec = dt.Second;
                tm.Wday = (int)dt.DayOfWeek;
                tmGmt = true;
                return n;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Fall through
            }
        }

        // Multi-number formats: num[-.:/]num[same]num
        if (pos < date.Length)
        {
            char c = date[pos];
            if ((c == ':' || c == '.' || c == '/' || c == '-') &&
                pos + 1 < date.Length && char.IsAsciiDigit(date[pos + 1]))
            {
                int match = MatchMultiNumber(num, c, date, start, pos, ref tm);
                if (match > 0)
                {
                    return match;
                }
            }
        }

        // Four-digit year or timezone.
        if (n == 4)
        {
            if (num <= 1400 && offset == -1)
            {
                ulong minutes = num % 100;
                ulong hours = num / 100;
                offset = (int)(hours * 60 + minutes);
            }
            else if (num is > 1900 and < 2100)
            {
                tm.Year = (int)num - 1900;
            }

            return n;
        }

        // Ignore > 2 digit numerals (days/months are 1-2 digits).
        if (n > 2)
        {
            return n;
        }

        // Day of month takes precedence.
        if (num > 0 && num < 32 && tm.Mday < 0)
        {
            tm.Mday = (int)num;
            return n;
        }

        // Two-digit year.
        if (n == 2 && tm.Year < 0)
        {
            if (num < 10 && tm.Mday >= 0)
            {
                tm.Year = (int)num + 100;
                return n;
            }

            if (num >= 70)
            {
                tm.Year = (int)num;
                return n;
            }
        }

        if (num > 0 && num < 13 && tm.Mon < 0)
        {
            tm.Mon = (int)num - 1;
        }

        return n;
    }

    /// <summary>
    /// Parses multi-number date/time formats: <c>YYYY-MM-DD</c>,
    /// <c>DD/MM/YYYY</c>, <c>HH:MM:SS</c>, <c>DD.MM.YYYY</c>. Matches
    /// <c>match_multi_number</c>.
    /// </summary>
    private static int MatchMultiNumber(ulong num, char sep, ReadOnlySpan<char> date, int start, int firstSep, ref ApproxidateTm tm)
    {
        // Parse second number.
        int pos = firstSep + 1;
        while (pos < date.Length && char.IsAsciiDigit(date[pos]))
        {
            pos++;
        }

        if (!long.TryParse(date.Slice(firstSep + 1, pos - firstSep - 1), CultureInfo.InvariantCulture, out long num2))
        {
            return 0;
        }

        // Parse optional third number.
        long num3 = -1;
        if (pos < date.Length && date[pos] == sep && pos + 1 < date.Length && char.IsAsciiDigit(date[pos + 1]))
        {
            int p3 = pos + 1;
            while (p3 < date.Length && char.IsAsciiDigit(date[p3]))
            {
                p3++;
            }

            if (long.TryParse(date.Slice(pos + 1, p3 - pos - 1), CultureInfo.InvariantCulture, out long n3))
            {
                num3 = n3;
                pos = p3;
            }
        }

        switch (sep)
        {
            case ':':
                if (num3 < 0)
                {
                    num3 = 0;
                }

                if (num < 25 && num2 >= 0 && num2 < 60 && num3 >= 0 && num3 <= 60)
                {
                    tm.Hour = (int)num;
                    tm.Min = (int)num2;
                    tm.Sec = (int)num3;
                    return pos - start;
                }

                return 0;

            case '-':
            case '/':
            case '.':
                DateTimeOffset now = DateTimeOffset.Now;
                var nowTm = new ApproxidateTm
                {
                    Year = now.Year - 1900,
                    Mon = now.Month - 1,
                    Mday = now.Day,
                };

                if (num > 70)
                {
                    // yyyy-mm-dd?
                    if (IsDate((int)num, (int)num2, (int)num3, ref nowTm, now.ToUnixTimeSeconds(), ref tm))
                    {
                        break;
                    }

                    // yyyy-dd-mm?
                    if (IsDate((int)num, (int)num3, (int)num2, ref nowTm, now.ToUnixTimeSeconds(), ref tm))
                    {
                        break;
                    }
                }

                // mm/dd/yy[yy] (non-dot separator gets precedence).
                if (sep != '.' &&
                    IsDate((int)num3, (int)num, (int)num2, ref nowTm, now.ToUnixTimeSeconds(), ref tm))
                {
                    break;
                }

                // dd.mm.yy[yy] or dd/mm/yy[yy]
                if (IsDate((int)num3, (int)num2, (int)num, ref nowTm, now.ToUnixTimeSeconds(), ref tm))
                {
                    break;
                }

                // mm.dd.yy (dot separator).
                if (sep == '.' &&
                    IsDate((int)num3, (int)num, (int)num2, ref nowTm, now.ToUnixTimeSeconds(), ref tm))
                {
                    break;
                }

                return 0;
        }

        return pos - start;
    }

    /// <summary>Validates and sets date fields. Matches <c>is_date</c>.</summary>
    private static bool IsDate(int year, int month, int day, ref ApproxidateTm nowTm, long now, ref ApproxidateTm tm)
    {
        if (month <= 0 || month > 12 || day <= 0 || day >= 32)
        {
            return false;
        }

        ApproxidateTm r = tm;
        r.Mon = month - 1;
        r.Mday = day;

        if (year == -1)
        {
            r.Year = nowTm.Year;
        }
        else if (year is >= 1970 and < 2100)
        {
            r.Year = year - 1900;
        }
        else if (year is > 70 and < 100)
        {
            r.Year = year;
        }
        else if (year < 38)
        {
            r.Year = year + 100;
        }
        else
        {
            return false;
        }

        // Sanity check: not more than 10 days in the future.
        long specified = TmToTimeT(ref r);
        if (specified != -1 && now + 10 * 24 * 3600 < specified)
        {
            return false;
        }

        tm.Mon = r.Mon;
        tm.Mday = r.Mday;
        if (year != -1)
        {
            tm.Year = r.Year;
        }

        return true;
    }

    /// <summary>
    /// Converts <c>struct tm</c>-style fields to Unix timestamp. Only works
    /// for 1970-2099. Matches <c>tm_to_time_t</c>.
    /// </summary>
    private static long TmToTimeT(ref ApproxidateTm tm)
    {
        ReadOnlySpan<int> mdays = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];

        int year = tm.Year - 70;
        int month = tm.Mon;
        int day = tm.Mday;

        if (year is < 0 or > 129)
        {
            return -1;
        }

        if (month is < 0 or > 11)
        {
            return -1;
        }

        // C (date.c:47): if (month < 2 || (year + 2) % 4) day--; — decrement
        // for January/February, or for NON-leap years. mdays[] counts Feb as
        // 28 days; leap years need the extra day back for March onward.
        if (month < 2 || (year + 2) % 4 != 0)
        {
            day--;
        }

        if (tm.Hour < 0 || tm.Min < 0 || tm.Sec < 0)
        {
            return -1;
        }

        return (year * 365L + (year + 1) / 4 + mdays[month] + day) * 24 * 60 * 60 +
               tm.Hour * 60L * 60 + tm.Min * 60L + tm.Sec;
    }

    /// <summary>
    /// Checks if no date fields have been set yet. Matches <c>nodate</c>.
    /// </summary>
    private static bool NoDate(ref ApproxidateTm tm)
        => (tm.Year & tm.Mon & tm.Mday & tm.Hour & tm.Min & tm.Sec) < 0;

    /// <summary>
    /// Case-insensitive prefix match. Matches <c>match_string</c>.
    /// Returns the number of matching characters, or 0 on mismatch.
    /// </summary>
    private static int MatchString(ReadOnlySpan<char> date, int start, string str)
    {
        int i = 0;
        while (start + i < date.Length && i < str.Length)
        {
            char dc = date[start + i];
            char sc = str[i];
            if (dc == sc)
            {
                i++;
                continue;
            }

            if (char.ToUpperInvariant(dc) == char.ToUpperInvariant(sc))
            {
                i++;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(dc))
            {
                break;
            }

            return 0;
        }

        return i;
    }

    /// <summary>Skip past an alphabetic token. Matches <c>skip_alpha</c>.</summary>
    private static int SkipAlpha(ReadOnlySpan<char> date, int start)
    {
        int i = 1;
        while (start + i < date.Length && char.IsAsciiLetter(date[start + i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Parses a timezone offset from <c>+/-HHMM</c> or <c>+/-HH:MM</c>.
    /// Matches <c>match_tz</c> (used by parse_date_basic, not the public
    /// TryParseOffset which has a different return convention).
    /// </summary>
    private static int MatchTz(ReadOnlySpan<char> date, int start, ref int offset)
    {
        int pos = start + 1;
        while (pos < date.Length && char.IsAsciiDigit(date[pos]))
        {
            pos++;
        }

        int n = pos - (start + 1);

        // C (date.c:417-452): int hour = strtoul(date + 1, &end, 10) — the
        // hour is parsed from the leading digits for ALL forms; only the
        // hhmm branch re-derives it from the 4-digit number.
        int hour = 0;
        if (n > 0)
        {
            _ = int.TryParse(date.Slice(start + 1, n), CultureInfo.InvariantCulture, out hour);
        }

        int min = 0;
        if (n == 4)
        {
            // hhmm
            min = hour % 100;
            hour /= 100;
        }
        else if (n != 2)
        {
            min = 99; /* random stuff */
        }
        else if (pos < date.Length && date[pos] == ':')
        {
            // hh:mm? — C consumes all digits after the colon (strtoul) and
            // requires exactly two ("+01:059" → min = 59 but 6 chars → 99).
            int p = pos + 1;
            while (p < date.Length && char.IsAsciiDigit(date[p]))
            {
                p++;
            }

            if (int.TryParse(date.Slice(pos + 1, p - pos - 1), CultureInfo.InvariantCulture, out int m))
            {
                min = m;
                pos = p;
            }
            else
            {
                min = 99;
            }

            if (pos - (start + 1) != 5)
            {
                min = 99; /* random stuff */
            }
        }
        /* otherwise we parsed "hh" */

        if (min < 60 && hour < 24)
        {
            int off = hour * 60 + min;
            if (date[start] == '-')
            {
                off = -off;
            }

            offset = off;
        }

        return pos - start;
    }

    /// <summary>
    /// Apply a special date word (yesterday, noon, midnight, tea, now, never,
    /// AM, PM). Matches the <c>date_*</c> functions.
    /// </summary>
    private static void ApplySpecial(ref ApproxidateTm tm, ref ApproxidateTm now, ref int number, SpecialKind kind)
    {
        switch (kind)
        {
            case SpecialKind.Now:
                _ = UpdateTm(ref tm, ref now, 0);
                break;
            case SpecialKind.Yesterday:
                _ = UpdateTm(ref tm, ref now, 24 * 60 * 60);
                break;
            case SpecialKind.Midnight:
                DateTime(ref tm, ref now, 0);
                break;
            case SpecialKind.Noon:
                DateTime(ref tm, ref now, 12);
                break;
            case SpecialKind.Tea:
                DateTime(ref tm, ref now, 17);
                break;
            case SpecialKind.PM:
                {
                    int hour = tm.Hour;
                    int n = number;
                    number = 0;
                    if (n != 0)
                    {
                        hour = n;
                        tm.Min = 0;
                        tm.Sec = 0;
                    }

                    tm.Hour = (hour % 12) + 12;
                    break;
                }
            case SpecialKind.AM:
                {
                    int hour = tm.Hour;
                    int n = number;
                    number = 0;
                    if (n != 0)
                    {
                        hour = n;
                        tm.Min = 0;
                        tm.Sec = 0;
                    }

                    tm.Hour = hour % 12;
                    break;
                }
            case SpecialKind.Never:
                try
                {
                    // C: p_localtime_r(&n, tm) with n = 0 — the LOCAL wall-clock
                    // fields of the epoch, which also reset tm_isdst (Dec 1969
                    // in the northern hemisphere → standard time from now on).
                    DateTimeOffset epoch = DateTimeOffset.FromUnixTimeSeconds(0).LocalDateTime;
                    tm.Year = epoch.Year - 1900;
                    tm.Mon = epoch.Month - 1;
                    tm.Mday = epoch.Day;
                    tm.Hour = epoch.Hour;
                    tm.Min = epoch.Minute;
                    tm.Sec = epoch.Second;
                    tm.Wday = (int)epoch.DayOfWeek;
                    tm.IsDst = TimeZoneInfo.Local.IsDaylightSavingTime(epoch) ? 1 : 0;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Leave tm as-is
                }

                break;
        }
    }

    /// <summary>Set time of day. Matches <c>date_time</c>.</summary>
    private static void DateTime(ref ApproxidateTm tm, ref ApproxidateTm now, int hour)
    {
        if (tm.Hour < hour)
        {
            _ = UpdateTm(ref tm, ref now, 24 * 60 * 60);
        }

        tm.Hour = hour;
        tm.Min = 0;
        tm.Sec = 0;
    }

    // ===== Tables (from date.c) =====

    private static readonly ImmutableArray<string> s_monthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static readonly ImmutableArray<string> s_weekdayNames =
    [
        "Sundays", "Mondays", "Tuesdays", "Wednesdays", "Thursdays", "Fridays", "Saturdays",
    ];

    private static readonly ImmutableArray<string> s_numberNames =
    [
        "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    ];

    private static readonly ImmutableArray<(string type, int length)> s_typeLengths =
    [
        ("seconds", 1),
        ("minutes", 60),
        ("hours", 60 * 60),
        ("days", 24 * 60 * 60),
        ("weeks", 7 * 24 * 60 * 60),
    ];

    private enum SpecialKind { Yesterday, Noon, Midnight, Tea, PM, AM, Never, Now }

    private static readonly ImmutableArray<(string name, SpecialKind kind)> s_specialWords =
    [
        ("yesterday", SpecialKind.Yesterday),
        ("noon", SpecialKind.Noon),
        ("midnight", SpecialKind.Midnight),
        ("tea", SpecialKind.Tea),
        ("PM", SpecialKind.PM),
        ("AM", SpecialKind.AM),
        ("never", SpecialKind.Never),
        ("now", SpecialKind.Now),
    ];

    private static readonly ImmutableArray<(string name, int offset, int dst)> s_timezoneNames =
    [
        ("IDLW", -12, 0), ("NT", -11, 0), ("CAT", -10, 0), ("HST", -10, 0),
        ("HDT", -10, 1), ("YST", -9, 0), ("YDT", -9, 1), ("PST", -8, 0),
        ("PDT", -8, 1), ("MST", -7, 0), ("MDT", -7, 1), ("CST", -6, 0),
        ("CDT", -6, 1), ("EST", -5, 0), ("EDT", -5, 1), ("AST", -3, 0),
        ("ADT", -3, 1), ("WAT", -1, 0),
        ("GMT", 0, 0), ("UTC", 0, 0), ("Z", 0, 0),
        ("WET", 0, 0), ("BST", 0, 1), ("CET", 1, 0), ("MET", 1, 0),
        ("MEWT", 1, 0), ("MEST", 1, 1), ("CEST", 1, 1), ("MESZ", 1, 1),
        ("FWT", 1, 0), ("FST", 1, 1), ("EET", 2, 0), ("EEST", 2, 1),
        ("WAST", 7, 0), ("WADT", 7, 1), ("CCT", 8, 0), ("JST", 9, 0),
        ("EAST", 10, 0), ("EADT", 10, 1), ("GST", 10, 0),
        ("NZT", 12, 0), ("NZST", 12, 0), ("NZDT", 12, 1), ("IDLE", 12, 0),
    ];

    /// <summary>
    /// Mutable date-time state mirroring C's <c>struct tm</c>. Unset fields
    /// are -1 (matching the C convention). <c>Year</c> is years-since-1900;
    /// <c>Mon</c> is 0-based.
    /// </summary>
    private struct ApproxidateTm
    {
        public int Year;   // years since 1900 (2024 = 124), -1 = unset
        public int Mon;    // 0-11, -1 = unset
        public int Mday;   // 1-31, -1 = unset
        public int Hour;   // 0-23, -1 = unset
        public int Min;    // 0-59, -1 = unset
        public int Sec;    // 0-59, -1 = unset
        public int Wday;   // 0-6 (Sunday = 0)
        public int IsDst;  // mirrors tm_isdst for update_tm's mktime call
    }
}

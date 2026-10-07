// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Utils;

namespace LibGit2CS.Core;

/// <summary> A name/email/time triple identifying who created a git object. Managed equivalent of libgit2's <c>git_signature</c>. </summary> <remarks> <para>
/// Immutable. libgit2's mutable <c>git_signature_dup</c>/<c>_new</c> become record copy semantics. <see cref="DefaultAsync"/>/<see cref="DefaultFromEnvAsync"/> are
/// read <c>user.name</c>/<c>user.email</c> from Configuration. </para> <para> The name/email are stored as raw bytes (<see
/// cref="NameBytes"/>/<see cref="EmailBytes"/>) — libgit2 keeps the <c>char *</c> bytes verbatim (signature.c:70-100, 322-398) with no charset concept, so
/// non-UTF-8 name/email bytes round-trip byte-exact through parse and write. <see cref="Name"/>/<see cref="Email"/> are lazy UTF-8 display decodes. Equality is
/// hand-written byte-content equality over the canonical members (<see cref="NameBytes"/>/<see cref="EmailBytes"/> + <see cref="When"/>) — the synthesized
/// record equality would compare the <c>ReadOnlyMemory&lt;byte&gt;</c> backing stores by reference+length, so two content-identical signatures parsed from
/// separate buffers would never be equal. </para> </remarks>
public sealed record GitSignature : ISpanFormattable
{
    private readonly byte[] _nameBytes;
    private readonly byte[] _emailBytes;
    private string? _name;
    private string? _email;

    /// <summary>
    /// Constructs a signature from UTF-8 name/email strings. The strings are
    /// UTF-8-encoded once and stored as the canonical bytes
    /// (<see cref="NameBytes"/>/<see cref="EmailBytes"/>); <see cref="Name"/>/
    /// <see cref="Email"/> are the cached display strings. No validation or
    /// trimming is performed — use <see cref="Create(string, string, GitTime)"/>
    /// for the validated + trimmed factory (matches libgit2's
    /// <c>git_signature_new</c>).
    /// </summary>
    public GitSignature(string name, string email, GitTime when)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);

        _nameBytes = Encoding.UTF8.GetBytes(name);
        _emailBytes = Encoding.UTF8.GetBytes(email);
        _name = name;
        _email = email;
        When = when;
    }

    /// <summary> Constructs a signature from raw name/email bytes. Byte-parity surface — C's <c>git_signature_new</c> (signature.c:70-100) and
    /// <c>git_signature__parse</c> (signature.c:322-398) keep the <c>char *</c> bytes verbatim with no charset concept, so non-UTF-8 name/email bytes
    /// round-trip byte-exact through parse and write. The caller transfers ownership of <paramref name="nameBytes"/>/<paramref name="emailBytes"/> (they must
    /// be fresh private arrays, not slices into pooled/shared buffers). </summary>
    internal GitSignature(byte[] nameBytes, byte[] emailBytes, GitTime when)
    {
        ArgumentNullException.ThrowIfNull(nameBytes);
        ArgumentNullException.ThrowIfNull(emailBytes);

        _nameBytes = nameBytes;
        _emailBytes = emailBytes;
        When = when;
    }

    /// <summary> Byte-content equality: <c>SequenceEqual</c> over the raw name/email bytes plus value equality on <see cref="When"/> — the managed equivalent
    /// of comparing C's raw <c>char *</c> name/email and <c>git_time</c> by value. Byte equality implies <see cref="Name"/>/<see cref="Email"/> string equality
    /// for consistently-constructed signatures, and correctly distinguishes invalid-UTF-8 byte sets that decode to the same U+FFFD display string. Declaring
    /// the typed <c>Equals</c> suppresses the synthesized record equality (which would compare the <see cref="NameBytes"/>/ <see cref="EmailBytes"/> backing
    /// stores by reference+length); the compiler-generated <see cref="object.Equals(object?)"/> and <c>==</c>/<c>!=</c> delegate to this member. </summary>
    public bool Equals(GitSignature? other)
        => other is not null
            && When == other.When
            && NameBytes.Span.SequenceEqual(other.NameBytes.Span)
            && EmailBytes.Span.SequenceEqual(other.EmailBytes.Span);

    /// <summary>
    /// FNV-1a over the byte members combined with <see cref="When"/>'s hash —
    /// consistent with the byte-content equality (same algorithm as
    /// <see cref="LibGit2CS.IO.GitPath.GetHashCode()"/>).
    /// </summary>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = When.GetHashCode();
            hash = (hash * 397) ^ ByteHash.Fnv1a(NameBytes.Span);
            hash = (hash * 397) ^ ByteHash.Fnv1a(EmailBytes.Span);
            return hash;
        }
    }

    /// <summary> The name as raw bytes. Byte-parity surface — the canonical storage; <see cref="Name"/> is the UTF-8 display decode. </summary>
    public ReadOnlyMemory<byte> NameBytes => _nameBytes;

    /// <summary> The email as raw bytes. Byte-parity surface — the canonical storage; <see cref="Email"/> is the UTF-8 display decode. </summary>
    public ReadOnlyMemory<byte> EmailBytes => _emailBytes;

    /// <summary> The name as a UTF-8 display decode — lazy over the canonical <see cref="NameBytes"/>. </summary>
    public string Name => _name ??= Encoding.UTF8.GetString(_nameBytes);

    /// <summary> The email as a UTF-8 display decode — lazy over the canonical <see cref="EmailBytes"/>. </summary>
    public string Email => _email ??= Encoding.UTF8.GetString(_emailBytes);

    /// <summary>The signature time.</summary>
    public GitTime When { get; }

    /// <summary> Creates a signature from raw name/email bytes. Byte-parity surface — C's <c>git_signature_new</c> takes <c>char *</c> bytes and stores them
    /// verbatim (signature.c:70-100); the string <see cref="Create(string, string, GitTime)"/> is the UTF-8 convenience tier on top. </summary> <exception
    /// cref="FormatException">Name or email contains angle brackets, or is empty after trimming.</exception>
    public static GitSignature Create(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> email, GitTime when)
    {
        if (ContainsAngleBrackets(name.Span) || ContainsAngleBrackets(email.Span))
        {
            throw new FormatException(
                "Neither `name` nor `email` should contain angle brackets chars.");
        }

        ReadOnlySpan<byte> trimmedName = TrimCrud(name.Span);
        ReadOnlySpan<byte> trimmedEmail = TrimCrud(email.Span);

        if (trimmedName.Length == 0 || trimmedEmail.Length == 0)
        {
            throw new FormatException("Signature cannot have an empty name or email");
        }

        return new GitSignature(
            trimmedName.ToArray(),
            trimmedEmail.ToArray(),
            when);
    }

    /// <summary>
    /// Creates a signature with validation matching libgit2's <c>git_signature_new</c>:
    /// rejects angle brackets in name/email, trims "crud" characters, requires non-empty.
    /// UTF-8 convenience tier — the byte-parity surface is
    /// <see cref="Create(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, GitTime)"/>.
    /// </summary>
    /// <exception cref="FormatException">Name or email contains angle brackets, or is empty after trimming.</exception>
    public static GitSignature Create(string name, string email, GitTime when)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(email));
        try
        {
            int nameBytesWritten = Encoding.UTF8.GetBytes(name, buffer);
            int emailBytesWritten = Encoding.UTF8.GetBytes(email, buffer.AsSpan(nameBytesWritten));
            return Create(buffer.AsMemory(0, nameBytesWritten), buffer.AsMemory(nameBytesWritten, emailBytesWritten), when);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Creates a signature with the current UTC time and the local timezone offset.
    /// Matches libgit2's <c>git_signature_now</c>.
    /// </summary>
    public static GitSignature Now(string name, string email)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var when = new GitTime(now.ToUnixTimeSeconds(), (int)now.Offset.TotalMinutes);
        return Create(name, email, when);
    }

    // Hoisted config key bytes for GetBytesAsync(ReadOnlyMemory<byte>, …) — avoids per-call UTF-8 encoding of the literal. ReadOnlyMemory<byte> is not a
    // mutable-collection type, so the static-state convention does not flag it.
    private static readonly ReadOnlyMemory<byte> s_userNameLiteral = "user.name"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> s_userEmailLiteral = "user.email"u8.ToArray();

    /// <summary>
    /// Creates a signature from <c>user.name</c>/<c>user.email</c> in the given
    /// configuration, with the current time. Matches libgit2's
    /// <c>git_signature_default</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if <c>user.name</c> or <c>user.email</c> is not set.
    /// </exception>
    public static async ValueTask<GitSignature> DefaultAsync(GitConfiguration config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        // read the raw value bytes — C's git_signature_default (signature.c:281-309) takes the config char* bytes verbatim, so a non-UTF-8 user.name/user.email
        // must not degrade to U+FFFD here.
        byte[]? name = (await config.GetBytesAsync(s_userNameLiteral, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(GitErrorCode.NotFound, "user.name is not set", GitErrorCategory.Config);
        byte[]? email = (await config.GetBytesAsync(s_userEmailLiteral, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(GitErrorCode.NotFound, "user.email is not set", GitErrorCategory.Config);

        (long now, int nowOffset) = NowTime();
        return Create(name, email, new GitTime(now, nowOffset));
    }

    /// <summary>
    /// Creates a signature from environment variables
    /// (<c>GIT_AUTHOR_NAME</c>/<c>GIT_AUTHOR_EMAIL</c>), falling back to
    /// <c>user.name</c>/<c>user.email</c> in <paramref name="config"/> if the env
    /// vars are unset. Exact port of <c>git_signature_default_from_env</c>
    /// (signature.c:281-309): produces BOTH the author and the committer,
    /// honoring <c>GIT_AUTHOR_NAME/EMAIL/DATE</c> and
    /// <c>GIT_COMMITTER_NAME/EMAIL/DATE</c> (with the <c>EMAIL</c> fallback
    /// for either side), and parsing the date env vars through
    /// <c>git_date_offset_parse</c>.
    /// </summary>
    /// <param name="config">Optional configuration for env-var fallback. Pass <c>null</c> to use env vars only.</param>
    /// <param name="context">The library context supplying the env-var snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if neither the env var nor the config value provides a name/email.
    /// </exception>
    public static async ValueTask<(GitSignature Author, GitSignature Committer)> DefaultFromEnvAsync(GitConfiguration? config, GitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        (long now, int nowOffset) = NowTime();
        GitSignature author = await UserFromEnvAsync(config, context, "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_AUTHOR_DATE", now, nowOffset, cancellationToken).ConfigureAwait(false);
        GitSignature committer = await UserFromEnvAsync(config, context, "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_COMMITTER_DATE", now, nowOffset, cancellationToken).ConfigureAwait(false);
        return (author, committer);
    }

    private static (long seconds, int offsetMinutes) NowTime()
    {
        // Matches C's current_time() (signature.c:156-176): the LOCAL UTC
        // offset, not 0. DateTimeOffset.Now carries the current local offset
        // (DST-aware), exactly like C's difftime(now, mktime(gmtime(now))).
        DateTimeOffset now = DateTimeOffset.Now;
        return (now.ToUnixTimeSeconds(), (int)now.Offset.TotalMinutes);
    }

    /// <summary>Exact port of <c>user_from_env</c> (signature.c:208-279).</summary>
    private static async ValueTask<GitSignature> UserFromEnvAsync(GitConfiguration? config, GitContext context, string nameVar, string emailVar, string dateVar, long defaultTime, int defaultOffset, CancellationToken cancellationToken)
    {
        // the name/email sources are byte-primary — C's user_from_env (signature.c:208-279) feeds the raw getenv/config char* bytes to git_signature_new
        // verbatim (signature.c:70-100). The env snapshot is the string tier by design (.NET process env), so env values are UTF-8-encoded here; config values
        // are read via GetBytesAsync so non-UTF-8 user.name/user.email survive verbatim instead of degrading to a U+FFFD needle (same fix as DefaultAsync).
        string? nameEnv = context.Env[nameVar];
        byte[]? name;
        if (nameEnv is not null)
        {
            name = Encoding.UTF8.GetBytes(nameEnv);
        }
        else if (config is not null)
        {
            name = await config.GetBytesAsync(s_userNameLiteral, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            name = null;
        }

        string? emailEnv = context.Env[emailVar];
        byte[]? email;
        if (emailEnv is not null)
        {
            email = Encoding.UTF8.GetBytes(emailEnv);
        }
        else if (config is not null)
        {
            email = await config.GetBytesAsync(s_userEmailLiteral, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            email = null;
        }

        // C (signature.c:244-252): when user.email is unset, fall back to the
        // EMAIL environment variable.
        if (email is null && context.Env["EMAIL"] is { } emailFallback)
        {
            email = Encoding.UTF8.GetBytes(emailFallback);
        }

        if (name is null)
        {
            throw new GitException(GitErrorCode.NotFound, $"{nameVar} / user.name is not set", GitErrorCategory.Config);
        }

        if (email is null)
        {
            throw new GitException(GitErrorCode.NotFound, $"{emailVar} / user.email is not set", GitErrorCategory.Config);
        }

        // C (signature.c:254-266): the date env var is parsed via
        // git_date_offset_parse (parse_date_basic, then approxidate).
        long timestamp = defaultTime;
        int offset = defaultOffset;
        string? date = context.Env[dateVar];
        if (date is not null)
        {
            if (GitDateParser.ParseDateBasic(date, out long ts, out int off))
            {
                timestamp = ts;
                offset = off;
            }
            else
            {
                // C's
                // approxidate sets error_ret = -1 when nothing was touched
                // (date.c:856-857, 877), so git_date_offset_parse returns an
                // error and user_from_env propagates it (signature.c:264) —
                // an unparseable date env var FAILS signature creation
                // instead of silently falling back to the current time.
                if (!GitDateParser.Approxidate(date, out long approx))
                {
                    throw new GitException(GitErrorCode.Error, $"failed to parse date '{date}'", GitErrorCategory.Config);
                }

                timestamp = approx;
                offset = 0;
            }
        }

        return Create(name, email, new GitTime(timestamp, offset));
    }

    /// <summary>
    /// Parses a signature from the standard git buffer format:
    /// <c>Name &lt;email&gt; &lt;unix-time&gt; &lt;+/-HHMM&gt;</c>.
    /// Matches libgit2's <c>git_signature_from_buffer</c> / <c>git_signature__parse</c>.
    /// UTF-8 convenience tier — the byte-parity surface is
    /// <see cref="FromBuffer(ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <example><c>A U Thor &lt;author@example.com&gt; 1461698037 +0200</c></example>
    /// <exception cref="FormatException">Malformed signature buffer.</exception>
    public static GitSignature FromBuffer(ReadOnlySpan<char> buffer)
    {
        if (!TryParse(buffer, out GitSignature? sig, out _))
        {
            throw new FormatException("Failed to parse signature: malformed e-mail or missing timestamp");
        }

        return sig;
    }

    /// <summary> Parses a signature from a raw byte buffer. Byte-parity surface — C's <c>git_signature__parse</c> operates on <c>char *</c> bytes
    /// (signature.c:322-398): the <c>&lt;</c>/<c>&gt;</c> searches, crud trimming and timestamp parsing are all byte-domain, so non-UTF-8 name/email bytes are
    /// preserved verbatim. </summary> <example><c>A U Thor &lt;author@example.com&gt; 1461698037 +0200</c></example> <exception
    /// cref="FormatException">Malformed signature buffer.</exception>
    public static GitSignature FromBuffer(ReadOnlySpan<byte> buffer)
    {
        if (!TryParse(buffer, out GitSignature? sig, out _))
        {
            throw new FormatException("Failed to parse signature: malformed e-mail or missing timestamp");
        }

        return sig;
    }

    /// <summary>
    /// Attempts to parse a signature from a character buffer. On success, advances
    /// <paramref name="consumed"/> past the parsed content (including a trailing
    /// newline if <paramref name="ender"/> was provided and matched).
    /// Used by the commit/tag object parsers. UTF-8 convenience tier — the
    /// byte-parity surface is
    /// <see cref="TryParse(ReadOnlySpan{byte}, out GitSignature?, out int, string?, byte)"/>.
    /// </summary>
    /// <param name="buffer">The buffer to parse.</param>
    /// <param name="sig">The parsed signature.</param>
    /// <param name="consumed">The number of characters consumed from <paramref name="buffer"/>.</param>
    /// <param name="header">Optional prefix to require (e.g. <c>"author "</c>). Default null.</param>
    /// <param name="ender">Optional terminator character (e.g. <c>'\n'</c>). <c>'\0'</c> means no terminator.</param>
    /// <returns>True if parsing succeeded.</returns>
    [SuppressMessage("Design", "CA1021:Avoid out parameters", Justification = "'out' is the idiomatic .NET TryParse signature")]
    public static bool TryParse(
        ReadOnlySpan<char> buffer,
        [NotNullWhen(true)] out GitSignature? sig,
        out int consumed,
        string? header = null,
        char ender = '\0')
    {
        byte[] bufferBytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(buffer));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(buffer, bufferBytes);
            if (!TryParse(bufferBytes.AsSpan(0, bytesWritten), out sig, out int byteConsumed, header, (byte)ender))
            {
                consumed = 0;
                return false;
            }

            consumed = byteConsumed;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferBytes);
        }
    }

    /// <summary> Attempts to parse a signature from a raw byte buffer. On success, advances <paramref name="consumed"/> past the parsed content (including a
    /// trailing newline if <paramref name="ender"/> was provided and matched). Byte-parity surface — C's <c>git_signature__parse</c> (signature.c:322-398)
    /// operates on raw bytes: <c>git__memrchr</c> for <c>&lt;</c>/<c>&gt;</c>, <c>extract_trimmed</c> over byte ranges, <c>git__strntol64</c>/<c>_32</c> over
    /// the digit bytes. Non-UTF-8 name/email bytes are preserved verbatim. </summary> <param name="buffer">The buffer to parse.</param> <param name="sig">The
    /// parsed signature.</param> <param name="consumed">The number of bytes consumed from <paramref name="buffer"/>.</param> <param name="header">Optional
    /// prefix to require (e.g. <c>"author "</c>). Default null.</param> <param name="ender">Optional terminator byte (e.g. <c>'\n'</c>). <c>0</c> means no
    /// terminator.</param> <returns>True if parsing succeeded.</returns>
    [SuppressMessage("Design", "CA1021:Avoid out parameters", Justification = "'out' is the idiomatic .NET TryParse signature")]
    public static bool TryParse(
        ReadOnlySpan<byte> buffer,
        [NotNullWhen(true)] out GitSignature? sig,
        out int consumed,
        string? header = null,
        byte ender = 0)
    {
        sig = null;
        consumed = 0;

        int end = buffer.Length;
        int pos = 0;

        // If an ender is specified, truncate at the first occurrence of it.
        if (ender != 0)
        {
            int idx = buffer.IndexOf(ender);
            if (idx < 0)
            {
                return false;
            }

            end = idx;
        }

        // Match optional header prefix.
        if (header is { Length: > 0 })
        {
            byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(header));
            try
            {
                int headerBytesWritten = Encoding.UTF8.GetBytes(header, headerBuffer);

                if (pos + headerBytesWritten > end || !buffer.Slice(pos, headerBytesWritten).SequenceEqual(headerBuffer.AsSpan(0, headerBytesWritten)))
                {
                    return false;
                }

                pos += headerBytesWritten;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }

        // Find the LAST '<' and '>' in the line — handles names containing '>'.
        ReadOnlySpan<byte> slice = buffer[pos..end];
        int emailStartRel = slice.LastIndexOf((byte)'<');
        int emailEndRel = slice.LastIndexOf((byte)'>');

        if (emailStartRel < 0 || emailEndRel < 0 || emailEndRel <= emailStartRel)
        {
            return false;
        }

        int emailStart = pos + emailStartRel;
        int emailEnd = pos + emailEndRel;

        ReadOnlySpan<byte> name = TrimCrud(buffer[pos..emailStart]);
        ReadOnlySpan<byte> email = TrimCrud(buffer[(emailStart + 1)..emailEnd]);

        long time = 0;
        int offset = 0;
        char sign = '\0';

        // Do we have a time after the closing '>' + space?
        if (emailEnd + 2 < end)
        {
            int timeStart = emailEnd + 2;
            if (!ParseStrntol64(buffer, timeStart, end, out long parsedTime, out int timeEnd))
            {
                return false;
            }

            time = parsedTime;
            int afterTime = timeEnd;

            // Optional timezone: +HHMM / -HHMM (separated from timestamp by a space)
            if (afterTime + 1 < end)
            {
                // afterTime points to the space after digits; sign is at afterTime + 1
                byte tzSign = buffer[afterTime + 1];
                if (tzSign is (byte)'+' or (byte)'-')
                {
                    // Matches C (signature.c:373-389): git__strntol32 over the
                    // digits after the sign; a malformed/overflowing run is
                    // assumed to be zero. hours/mins come from the /100, %100
                    // split, and the tz is stored only when hours <= 14 and
                    // mins <= 59 (the sign is kept even for a zero offset).
                    if (!ParseStrntol32(buffer, afterTime + 2, end, out int tzValue, out _))
                    {
                        tzValue = 0;
                    }

                    int hours = tzValue / 100;
                    int mins = tzValue % 100;
                    if (hours <= 14 && mins <= 59)
                    {
                        offset = hours * 60 + mins;
                        sign = (char)tzSign;
                        if (tzSign == (byte)'-')
                        {
                            offset = -offset;
                        }
                    }
                }
            }
        }

        // Advance past the terminator (if any) — callers expect the next line.
        consumed = end + (ender != 0 && end < buffer.Length ? 1 : 0);

        sig = new GitSignature(
            name.ToArray(),
            email.ToArray(),
            new GitTime(time, offset, sign));
        return true;
    }

    /// <summary>
    /// Renders as the git buffer format: <c>Name &lt;email&gt; &lt;time&gt; &lt;+/-HHMM&gt;</c>.
    /// Matches libgit2's <c>git_signature__writebuf</c>.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        return destination.TryWrite(CultureInfo.InvariantCulture, $"{Name} <{Email}> {When}", out charsWritten);
    }

    /// <summary>
    /// Renders as the git buffer format: <c>Name &lt;email&gt; &lt;time&gt; &lt;+/-HHMM&gt;</c>.
    /// Matches libgit2's <c>git_signature__writebuf</c>.
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => $"{Name} <{Email}> {When}";

    /// <summary>Returns the Git textual representation of this value.</summary>
    public override string ToString() => ToString(null, null);

    /// <summary> Renders the signature line bytes (without a trailing newline) into <paramref name="destination"/>: <c>Name &lt;email&gt; &lt;time&gt;
    /// &lt;+/-HHMM&gt;</c>. Byte-parity surface — C's <c>git_signature__writebuf</c> (signature.c:425-441) splices the raw name/email bytes. Returns false if
    /// the destination is too small. </summary>
    internal bool TryFormatBytes(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        int nameLen = NameBytes.Length;
        int emailLen = EmailBytes.Length;
        int total = nameLen + 3 + emailLen + 2 + 1 + 1 + 4; // name + " <" + email + "> " + time + ' ' + sign + HHMM
        if (destination.Length < total)
        {
            return false;
        }

        NameBytes.Span.CopyTo(destination);
        int pos = nameLen;
        destination[pos++] = (byte)' ';
        destination[pos++] = (byte)'<';
        EmailBytes.Span.CopyTo(destination[pos..]);
        pos += emailLen;
        destination[pos++] = (byte)'>';
        destination[pos++] = (byte)' ';

        if (!Utf8Formatter.TryFormat(When.Seconds, destination[pos..], out int timeWritten))
        {
            return false;
        }

        pos += timeWritten;
        destination[pos++] = (byte)' ';

        int abs = Math.Abs(When.OffsetMinutes);
        int hours = abs / 60;
        int mins = abs % 60;
        char sign = When.OffsetMinutes < 0 || When.Sign == '-' ? '-' : '+';
        destination[pos++] = (byte)sign;
        destination[pos++] = (byte)('0' + (hours / 10));
        destination[pos++] = (byte)('0' + (hours % 10));
        destination[pos++] = (byte)('0' + (mins / 10));
        destination[pos++] = (byte)('0' + (mins % 10));

        bytesWritten = pos;
        return true;
    }

    /// <summary> Writes the signature line bytes (without a trailing newline) to the writer: <c>Name &lt;email&gt; &lt;time&gt; &lt;+/-HHMM&gt;</c>.
    /// Byte-parity surface — C's <c>git_signature__writebuf</c> (signature.c:425-441) splices the raw name/email bytes; the char <see cref="TryFormat"/> is the
    /// display tier. </summary>
    internal void WriteTo(IBufferWriter<byte> writer)
    {
        writer.Write(NameBytes.Span);
        writer.Write(" <"u8);
        writer.Write(EmailBytes.Span);
        writer.Write("> "u8);

        // Time + offset: "1461698037 +0200" (C's git_str_printf "%u %c%02d%02d").
        int abs = Math.Abs(When.OffsetMinutes);
        int hours = abs / 60;
        int mins = abs % 60;
        char sign = When.OffsetMinutes < 0 || When.Sign == '-' ? '-' : '+';

        Span<byte> scratch = stackalloc byte[32];
        if (Utf8Formatter.TryFormat(When.Seconds, scratch, out int written))
        {
            writer.Write(scratch[..written]);
        }

        writer.WriteByte((byte)' ');
        writer.WriteByte((byte)sign);
        writer.WriteByte((byte)('0' + (hours / 10)));
        writer.WriteByte((byte)('0' + (hours % 10)));
        writer.WriteByte((byte)('0' + (mins / 10)));
        writer.WriteByte((byte)('0' + (mins % 10)));
    }

    /// <summary>
    /// Characters libgit2 considers "crud" to trim from name/email (control chars,
    /// spaces, and several punctuation marks). Matches <c>is_crud</c> in <c>signature.c</c>.
    /// </summary>
    private static bool IsCrud(byte c) => c is <= (byte)' ' or (byte)',' or (byte)':' or (byte)';' or (byte)'<' or (byte)'>' or (byte)'"' or (byte)'\\' or (byte)'\'';

    /// <summary>
    /// Trims leading/trailing crud characters. Matches <c>extract_trimmed</c>.
    /// </summary>
    private static ReadOnlySpan<byte> TrimCrud(ReadOnlySpan<byte> value)
    {
        int start = 0;
        int end = value.Length;

        while (start < end && IsCrud(value[start]))
        {
            start++;
        }

        while (end > start && IsCrud(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool ContainsAngleBrackets(ReadOnlySpan<byte> value)
        => value.IndexOf((byte)'<') >= 0 || value.IndexOf((byte)'>') >= 0;

    /// <summary>
    /// Port of <c>git__strntol64</c> (util.c:36-124) with base 10 over
    /// <c>buffer[start..end)</c>: skips leading ASCII whitespace, accepts one
    /// leading <c>+</c>/<c>-</c>, then requires a non-empty digit run. Digits
    /// are accumulated in the negative (mirroring C) so <c>-9223372036854775808</c>
    /// parses. On success returns the value and the absolute end position of
    /// the digit run (past any skipped whitespace).
    /// </summary>
    private static bool ParseStrntol64(ReadOnlySpan<byte> buffer, int start, int end, out long value, out int endPos)
    {
        value = 0;
        endPos = start;

        int i = start;
        while (i < end && AsciiText.IsAsciiSpace((char)buffer[i]))
        {
            i++;
        }

        if (i >= end)
        {
            endPos = i;
            return false;
        }

        bool neg = false;
        if (buffer[i] is (byte)'-' or (byte)'+')
        {
            neg = buffer[i] == (byte)'-';
            i++;
        }

        if (i >= end)
        {
            endPos = i;
            return false;
        }

        int digits = 0;
        long n = 0; // accumulated negative (C accumulates v = neg ? -v : v)
        bool overflow = false;
        while (i < end && buffer[i] is >= (byte)'0' and <= (byte)'9')
        {
            int v = buffer[i] - '0';
            if (neg)
            {
                v = -v;
            }

            if (!overflow)
            {
                try
                {
                    n = checked(n * 10 + v);
                }
                catch (OverflowException)
                {
                    overflow = true;
                }
            }

            digits++;
            i++;
        }

        endPos = i;
        if (digits == 0)
        {
            return false;
        }

        if (overflow)
        {
            return false;
        }

        value = n;
        return true;
    }

    /// <summary>
    /// Port of <c>git__strntol32</c> (util.c:128-145) over
    /// <c>buffer[start..end)</c>: like <see cref="ParseStrntol64"/> but the
    /// value must fit in 32 bits (C errors on truncation).
    /// </summary>
    private static bool ParseStrntol32(ReadOnlySpan<byte> buffer, int start, int end, out int value, out int endPos)
    {
        if (!ParseStrntol64(buffer, start, end, out long tmp, out endPos))
        {
            value = 0;
            return false;
        }

        if (tmp is < int.MinValue or > int.MaxValue)
        {
            value = 0;
            return false;
        }

        value = (int)tmp;
        return true;
    }
}

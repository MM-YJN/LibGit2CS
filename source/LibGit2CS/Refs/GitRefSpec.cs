// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Refs;

/// <summary> A git refspec: a mapping rule between source and destination reference names. Managed port of libgit2's <c>git_refspec</c>
/// (<c>src/libgit2/refspec.c</c>). </summary> <remarks> <para> Format: <c>[+]&lt;src&gt;[:&lt;dst&gt;]</c>. The optional <c>+</c> prefix forces overwriting. A
/// <c>*</c> wildcard in both src and dst enables pattern matching. Negative refspecs (<c>^</c> prefix, Git 2.29+) exclude matching refs. </para> <para> Pure
/// data type — no I/O. Used by remote transport. </para> <para> The spec parts are byte-primary (<see cref="StringBytes"/>, <see
/// cref="SourceBytes"/>, <see cref="DestinationBytes"/>) — libgit2 parses and matches refspecs over raw <c>char *</c> bytes (refspec.c:16-177, <c>wildmatch</c>
/// over the raw source). The string members (<see cref="String"/>, <see cref="Source"/>, <see cref="Destination"/>) are UTF-8 display decodes; the byte
/// overloads of <see cref="SrcMatches(ReadOnlySpan{byte})"/>/ <see cref="Transform(ReadOnlySpan{byte})"/> are the parity surface. </para> </remarks>
public sealed class GitRefSpec
{
    /// <summary>Creates a parsed refspec. Use <see cref="Parse(ReadOnlySpan{byte}, bool)"/> for validation.</summary>
    private GitRefSpec(ReadOnlyMemory<byte> stringBytes, ReadOnlyMemory<byte> srcBytes, ReadOnlyMemory<byte> dstBytes, bool force, bool isFetch, bool pattern, bool matching)
    {
        StringBytes = stringBytes;
        SourceBytes = srcBytes;
        DestinationBytes = dstBytes;
        Force = force;
        IsFetch = isFetch;
        IsWildcard = pattern;
        IsMatching = matching;
    }

    /// <summary>The original input bytes. Byte-parity surface.</summary>
    public ReadOnlyMemory<byte> StringBytes { get; }

    /// <summary>The source (LHS) bytes. Empty for matching refspec or fetch-with-no-src. Byte-parity surface.</summary>
    public ReadOnlyMemory<byte> SourceBytes { get; }

    /// <summary>The destination (RHS) bytes. Empty for negative/matching refspec. Byte-parity surface.</summary>
    public ReadOnlyMemory<byte> DestinationBytes { get; }

    /// <summary>The original input string (UTF-8 display decode of <see cref="StringBytes"/>).</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'String' matches libgit2's git_refspec_string (original input). Not renamed to avoid divergence from the C API.")]
    public string String => Encoding.UTF8.GetString(StringBytes.Span);

    /// <summary>The source (LHS) as a UTF-8 display string. Empty for matching refspec or fetch-with-no-src. Byte-parity surface is <see cref="SourceBytes"/>.</summary>
    public string Source => Encoding.UTF8.GetString(SourceBytes.Span);

    /// <summary>The destination (RHS) as a UTF-8 display string. Empty for negative/matching refspec. Byte-parity surface is <see cref="DestinationBytes"/>.</summary>
    public string Destination => Encoding.UTF8.GetString(DestinationBytes.Span);

    /// <summary>True if the <c>+</c> force prefix was present.</summary>
    public bool Force { get; }

    /// <summary>True for fetch refspecs, false for push.</summary>
    public bool IsFetch { get; }

    /// <summary>True if the refspec contains a <c>*</c> wildcard.</summary>
    public bool IsWildcard { get; }

    /// <summary>
    /// True if the refspec's source ENDS with <c>*</c> — C's
    /// <c>git_refspec_is_wildcard</c> (refspec.c:350-355). The parse-time
    /// <see cref="IsWildcard"/> (any '*' in the source) is the isGlob used
    /// by Transform/DwimOne; the FETCH_HEAD merge decision needs C's
    /// ends-with-star test.
    /// </summary>
    public bool IsWildcardByC => SourceBytes.Span.EndsWith((byte)'*');

    /// <summary>True for push <c>:</c> matching refspec (empty src+dst).</summary>
    public bool IsMatching { get; }

    /// <summary>
    /// True if the refspec is negative. Matches C's
    /// <c>git_refspec_is_negative</c> (refspec.c:361-366):
    /// <c>src[0] == '^' &amp;&amp; dst == NULL</c> — so a push spec whose
    /// <c>^</c>-prefixed source was copied to the destination is NOT
    /// negative. (The parse-time <c>^</c> prefix, used only by the glob
    /// validity rules, is a separate local in C.)
    /// </summary>
    public bool IsNegative => SourceBytes.Length > 0 && SourceBytes.Span[0] == (byte)'^' && DestinationBytes.Length == 0;

    /// <summary> Checks if <paramref name="refName"/> matches the source pattern. Uses <see cref="WildMatch"/> with no flags (<c>*</c> crosses slashes).
    /// Matches <c>git_refspec_src_matches</c>. Byte-parity surface — C wildmatches the raw source bytes. </summary> <remarks> The raw <see cref="SourceBytes"/>
    /// is wildmatched — for a negative refspec the <c>^</c> prefix is kept, so this never matches a refname (C refspec.c:239-247). Use <see
    /// cref="SrcMatchesNegative(ReadOnlySpan{byte})"/> for negative patterns. </remarks>
    public bool SrcMatches(ReadOnlySpan<byte> refName)
    {
        if (SourceBytes.Length == 0)
        {
            return false;
        }

        return WildMatch.IsMatch(SourceBytes.Span, refName, WildMatchFlags.None);
    }

    /// <summary>
    /// Checks if <paramref name="refName"/> matches the source pattern.
    /// UTF-8 convenience — the byte-parity surface is
    /// <see cref="SrcMatches(ReadOnlySpan{byte})"/>.
    /// </summary>
    public bool SrcMatches(string refName)
        => SrcMatches(Encoding.UTF8.GetBytes(refName));

    /// <summary> Checks if <paramref name="refName"/> matches a negative refspec's source pattern. Matches <c>git_refspec_src_matches_negative</c>
    /// (refspec.c:230-236) — only this function strips the <c>^</c>. Byte-parity surface. </summary>
    public bool SrcMatchesNegative(ReadOnlySpan<byte> refName)
    {
        if (SourceBytes.Length == 0 || !IsNegative)
        {
            return false;
        }

        return WildMatch.IsMatch(SourceBytes.Span[1..], refName, WildMatchFlags.None);
    }

    /// <summary>
    /// Checks if <paramref name="refName"/> matches a negative refspec's
    /// source pattern. UTF-8 convenience — the byte-parity surface is
    /// <see cref="SrcMatchesNegative(ReadOnlySpan{byte})"/>.
    /// </summary>
    public bool SrcMatchesNegative(string refName)
        => SrcMatchesNegative(Encoding.UTF8.GetBytes(refName));

    /// <summary> Checks if <paramref name="refName"/> matches the destination pattern. Matches <c>git_refspec_dst_matches</c>. Byte-parity surface — C
    /// wildmatches the raw destination bytes. </summary>
    public bool DstMatches(ReadOnlySpan<byte> refName)
    {
        if (DestinationBytes.Length == 0)
        {
            return false;
        }

        return WildMatch.IsMatch(DestinationBytes.Span, refName, WildMatchFlags.None);
    }

    /// <summary>
    /// Checks if <paramref name="refName"/> matches the destination pattern.
    /// UTF-8 convenience — the byte-parity surface is
    /// <see cref="DstMatches(ReadOnlySpan{byte})"/>.
    /// </summary>
    public bool DstMatches(string refName)
        => DstMatches(Encoding.UTF8.GetBytes(refName));

    /// <summary> Transforms a source ref name to its destination using this refspec's pattern. Matches <c>git_refspec__transform</c>
    /// (<c>refspec.c:303-318</c>). Byte-parity surface — C substitutes raw bytes. </summary> <exception cref="GitException"> <see cref="GitErrorCode.Error"/>
    /// if <paramref name="refName"/> doesn't match the source (C returns a bare -1 with class GIT_ERROR_INVALID). </exception>
    public byte[] Transform(ReadOnlySpan<byte> refName)
    {
        if (!SrcMatches(refName))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"ref '{Encoding.UTF8.GetString(refName)}' doesn't match the source",
                GitErrorCategory.Invalid);
        }

        if (!IsWildcard)
        {
            return DestinationBytes.ToArray();
        }

        return WildcardSubstitute(SourceBytes.Span, DestinationBytes.Span, refName);
    }

    /// <summary>
    /// Transforms a source ref name to its destination using this refspec's pattern.
    /// UTF-8 convenience — the byte-parity surface is
    /// <see cref="Transform(ReadOnlySpan{byte})"/>.
    /// </summary>
    public string Transform(string refName)
        => Encoding.UTF8.GetString(Transform(Encoding.UTF8.GetBytes(refName)));

    /// <summary> Transforms a destination ref name back to its source. Matches <c>git_refspec__rtransform</c> (<c>refspec.c:325-340</c>). Byte-parity surface.
    /// </summary>
    public byte[] ReverseTransform(ReadOnlySpan<byte> refName)
    {
        if (!DstMatches(refName))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"ref '{Encoding.UTF8.GetString(refName)}' doesn't match the destination",
                GitErrorCategory.Invalid);
        }

        if (!IsWildcard)
        {
            return SourceBytes.ToArray();
        }

        return WildcardSubstitute(DestinationBytes.Span, SourceBytes.Span, refName);
    }

    /// <summary>
    /// Transforms a destination ref name back to its source. UTF-8
    /// convenience — the byte-parity surface is
    /// <see cref="ReverseTransform(ReadOnlySpan{byte})"/>.
    /// </summary>
    public string ReverseTransform(string refName)
        => Encoding.UTF8.GetString(ReverseTransform(Encoding.UTF8.GetBytes(refName)));

    /// <summary>
    /// Parses a refspec string. Matches <c>git_refspec__parse</c>
    /// (<c>refspec.c:16-177</c>). UTF-8 convenience — the byte-parity
    /// surface is <see cref="Parse(ReadOnlySpan{byte}, bool)"/>.
    /// </summary>
    /// <param name="spec">The refspec string (e.g. <c>"+refs/heads/*:refs/remotes/origin/*"</c>).</param>
    /// <param name="isFetch">True for fetch refspecs, false for push.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> if the refspec is malformed.
    /// </exception>
    public static GitRefSpec Parse(string spec, bool isFetch)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Parse(Encoding.UTF8.GetBytes(spec), isFetch);
    }

    /// <summary> Parses a refspec from raw bytes. Matches <c>git_refspec__parse</c> (<c>refspec.c:16-177</c>) — C scans the raw <c>char *</c> (strrchr for
    /// <c>:</c>, byte <c>+</c>/<c>^</c>/<c>*</c> tests). Byte-parity surface: non-UTF-8 spec bytes round-trip byte-exact. </summary> <param name="spec">The
    /// refspec bytes (e.g. <c>"+refs/heads/*:refs/remotes/origin/*"</c>).</param> <param name="isFetch">True for fetch refspecs, false for push.</param>
    /// <exception cref="GitException"> <see cref="GitErrorCode.InvalidSpec"/> if the refspec is malformed. </exception>
    public static GitRefSpec Parse(ReadOnlySpan<byte> spec, bool isFetch)
    {
        bool force = false;
        bool isNegative = false;
        bool isGlob = false;
        bool matching = false;
        int pos = 0;

        // '+' prefix = force.
        if (pos < spec.Length && spec[pos] == (byte)'+')
        {
            force = true;
            pos++;
        }

        // '^' prefix = negative refspec.
        if (pos < spec.Length && spec[pos] == (byte)'^')
        {
            isNegative = true;
        }

        // Find the rightmost ':' (rhs separator).
        int colonIdx = spec.LastIndexOf((byte)':');

        // Special case: push ":" or "+:" = matching refspec.
        if (!isFetch && colonIdx == pos && spec.Length == pos + 1)
        {
            return new GitRefSpec(spec.ToArray(), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, force, isFetch: false, pattern: false, matching: true);
        }

        // Split into src and dst.
        ReadOnlySpan<byte> src;
        ReadOnlySpan<byte> dst = default;
        bool hasDst = false;

        if (colonIdx >= 0)
        {
            ReadOnlySpan<byte> rhs = spec[(colonIdx + 1)..];
            if (rhs.Length > 0 || !isFetch)
            {
                if (rhs.Length > 0 && rhs.IndexOf((byte)'*') >= 0)
                {
                    isGlob = true;
                }

                dst = rhs;
                hasDst = true;
            }
        }

        ReadOnlySpan<byte> lhs = colonIdx >= 0
            ? spec[pos..colonIdx]
            : spec[pos..];

        // Check for '*' in LHS.
        if (lhs.Length >= 1 && lhs.IndexOf((byte)'*') >= 0)
        {
            // LHS has glob. One of these must be true:
            // 1) rhs exists and also has glob
            // 2) it's a negative refspec (no rhs)
            // 3) no rhs and we're fetching
            if ((hasDst && !isGlob) ||
                (hasDst && isNegative) ||
                (!hasDst && isFetch && !isNegative))
            {
                throw InvalidSpec(spec);
            }

            isGlob = true;
        }
        else if (hasDst && isGlob)
        {
            // RHS has glob but LHS doesn't.
            throw InvalidSpec(spec);
        }

        src = lhs;

        // Validate ref names. C validates the raw bytes; the char-domain validator is outcome-identical for every byte input (invalid bytes decode to
        // U+FFFD, which is > ' ' and not in the reject set — it passes/fails exactly like the raw byte), so the candidates are UTF-8-decoded for validation
        // only.
        GitReferenceFormatFlags flags = GitReferenceFormatFlags.AllowOneLevel | GitReferenceFormatFlags.RefspecShorthand;
        if (isGlob)
        {
            flags |= GitReferenceFormatFlags.RefspecPattern;
        }

        string srcStr = Encoding.UTF8.GetString(src);
        string? dstStr = hasDst ? Encoding.UTF8.GetString(dst) : null;

        if (isFetch)
        {
            // LHS: empty is ok (HEAD), otherwise must be valid.
            if (srcStr.Length > 0 && !GitReferences.IsNameValid(srcStr, flags))
            {
                throw InvalidSpec(spec);
            }

            // RHS: missing/empty is ok, otherwise must be valid.
            if (dstStr is not null && dstStr.Length > 0 && !GitReferences.IsNameValid(dstStr, flags))
            {
                throw InvalidSpec(spec);
            }
        }
        else
        {
            // Push: LHS empty = delete; wildcarded = must be valid; otherwise anything goes.
            if (srcStr.Length > 0 && isGlob && !GitReferences.IsNameValid(srcStr, flags))
            {
                throw InvalidSpec(spec);
            }

            // RHS: missing → LHS must be valid; empty → invalid; otherwise must be valid.
            if (dstStr is null)
            {
                if (!GitReferences.IsNameValid(srcStr, flags))
                {
                    throw InvalidSpec(spec);
                }

                dst = src; // copy LHS
                hasDst = true;
            }
            else if (dstStr.Length == 0)
            {
                throw InvalidSpec(spec);
            }
            else
            {
                if (!GitReferences.IsNameValid(dstStr, flags))
                {
                    throw InvalidSpec(spec);
                }
            }
        }

        return new GitRefSpec(spec.ToArray(), src.ToArray(), hasDst ? dst.ToArray() : [], force, isFetch, isGlob, matching);
    }

    /// <summary>
    /// Expands shorthand source/destination names against the advertised
    /// remote heads. Matches <c>git_refspec__dwim_one</c>
    /// (refspec.c:377-435): a source not starting with <c>refs/</c> is
    /// tried as <c>refs/&lt;src&gt;</c>, <c>refs/tags/&lt;src&gt;</c>,
    /// <c>refs/heads/&lt;src&gt;</c> — the LAST matching form wins (the C
    /// loop keeps overwriting); a destination not starting with
    /// <c>refs/</c> becomes <c>refs/&lt;dst&gt;</c> when it starts with
    /// <c>remotes/</c>, else <c>refs/heads/&lt;dst&gt;</c>.
    /// </summary>
    /// <param name="advertisedNames">The advertised remote head names.</param>
    /// <returns>The dwimmed refspec (this spec unchanged when nothing to expand).</returns>
    public GitRefSpec DwimOne(IReadOnlyCollection<string> advertisedNames)
    {
        string src = Source;
        string dst = Destination;

        // Shorthand on the lhs. C (refspec.c:395-409): the formatter loop
        // does NOT stop at the first match — every matching formatter
        // overwrites cur->src, so the LAST matching form (refs/heads/ if
        // advertised) wins. Each candidate is formatted from the ORIGINAL
        // spec->src, never the running result.
        if (!src.StartsWith("refs/", StringComparison.Ordinal))
        {
            string[] formatters = ["refs/{0}", "refs/tags/{0}", "refs/heads/{0}"];
            foreach (string formatter in formatters)
            {
                string candidate = string.Format(System.Globalization.CultureInfo.InvariantCulture, formatter, Source);
                if (advertisedNames.Contains(candidate))
                {
                    src = candidate;
                }
            }
        }

        // Shorthand on the rhs. C (refspec.c:425-437): git_refspec__dwim_one expands only a NON-NULL destination that does not start with "refs/". The C parse
        // leaves dst NULL for a fetch spec with no RHS or an EMPTY RHS (refspec.c:54-58: `if (rlen || !is_fetch)`), so "refs/heads/x" and "refs/heads/x:" both
        // keep their empty destination after dwim (the port maps NULL dst to "", hence the length guard — re-verified as C-equivalent).
        if (dst.Length > 0 && !dst.StartsWith("refs/", StringComparison.Ordinal))
        {
            dst = dst.StartsWith("remotes/", StringComparison.Ordinal)
                ? "refs/" + dst
                : "refs/heads/" + dst;
        }

        if (src == Source && dst == Destination)
        {
            return this;
        }

        return new GitRefSpec(StringBytes, Encoding.UTF8.GetBytes(src), Encoding.UTF8.GetBytes(dst), Force, IsFetch, IsWildcard, IsMatching);
    }

    /// <summary>
    /// Wildcard substitution. Matches <c>refspec_transform</c>
    /// (<c>refspec.c:263-296</c>) — byte-domain (C substitutes raw bytes).
    /// </summary>
    private static byte[] WildcardSubstitute(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, ReadOnlySpan<byte> name)
    {
        int fromStar = from.IndexOf((byte)'*');
        int toStar = to.IndexOf((byte)'*');

        // Star offset (position of * in both from and name).
        int starOffset = fromStar;

        // First half: copy from 'to' up to its '*'.
        ReadOnlySpan<byte> firstHalf = to[..toStar];

        // Replacement: portion of 'name' that matched the '*'.
        ReadOnlySpan<byte> nameSuffix = name[starOffset..];
        ReadOnlySpan<byte> fromSuffix = from[(fromStar + 1)..];
        int replacementLen = nameSuffix.Length - fromSuffix.Length;

        byte[] result = new byte[firstHalf.Length + replacementLen + fromSuffix.Length];
        firstHalf.CopyTo(result);
        nameSuffix[..replacementLen].CopyTo(result.AsSpan(firstHalf.Length));
        to[(toStar + 1)..].CopyTo(result.AsSpan(firstHalf.Length + replacementLen));
        return result;
    }

    private static GitException InvalidSpec(ReadOnlySpan<byte> spec)
        => new(GitErrorCode.InvalidSpec, $"'{Encoding.UTF8.GetString(spec)}' is not a valid refspec.", GitErrorCategory.Invalid);
}

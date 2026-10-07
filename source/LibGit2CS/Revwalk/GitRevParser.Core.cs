// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Revwalk;

/// <summary>
/// The revparse grammar. See <see cref="GitRevParser"/> for the range entry point.
/// This partial implements <see cref="ParseSingleAsync"/> and all operator productions.
/// </summary>
public static partial class GitRevParser
{
    /// <summary>
    /// Resolves a single revision specifier to one object. Matches
    /// <c>git_revparse_single</c> (revparse.c:889-910). Handles the full
    /// grammar: <c>HEAD~3^2</c>, <c>master@&#123;1&#125;</c>, <c>:path</c>,
    /// <c>HEAD^&#123;commit&#125;</c>, <c>HEAD^&#123;/regex&#125;</c>,
    /// <c>@&#123;yesterday&#125;</c>, abbreviated OIDs, and DWIM refs.
    /// </summary>
    /// <returns>The resolved object, or null if the spec does not resolve.</returns>
    internal static async Task<GitObject?> ParseSingleAsync(GitRepository repo, string revspec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(revspec);

        // C (revparse.c:836-837, 647-648): an empty spec fails with
        // GIT_EINVALIDSPEC ("failed to parse revision specifier - Invalid
        // pattern ''") — ensure_base_rev_loaded rejects identifier_len == 0.
        // Range parsing substitutes "HEAD" for empty sides itself.
        (GitObject? obj, GitReference? _, bool _) = await ParseCoreAsync(repo, revspec, cancellationToken).ConfigureAwait(false);
        return obj;
    }

    /// <summary>
    /// Resolves a single revision specifier, returning the object and the
    /// intermediate reference. Matches <c>git_revparse_ext</c>
    /// (revparse.c:863-887). Specs routed through a ref
    /// (<c>@&#123;u&#125;</c>, <c>@&#123;N&#125;</c>, <c>@&#123;-N&#125;</c>,
    /// a plain refname) yield the reference; OID/abbreviation/operator-only
    /// specs yield <c>null</c> for the reference.
    /// </summary>
    /// <returns>The resolved object and the reference it came through (if any).</returns>
    internal static async Task<(GitObject? Object, GitReference? Reference)> ParseExtAsync(
        GitRepository repo, string revspec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(revspec);

        (GitObject? obj, GitReference? reference, bool _) =
            await ParseCoreAsync(repo, revspec, cancellationToken).ConfigureAwait(false);
        return (obj, reference);
    }

    // ── Core state machine (revparse.c:689-861) ─────────────────────────

    /// <summary>
    /// The character-by-character parser. Matches the <c>revparse</c> static
    /// function (revparse.c:689-861). Returns the resolved object and the
    /// reference it came through (if any).
    /// </summary>
    private static async Task<(GitObject? obj, GitReference? reference, bool parsed)> ParseCoreAsync(GitRepository repo, string spec, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ParseCoreInnerAsync(repo, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.InvalidSpec)
        {
            // C (revparse.c:849-853): every GIT_EINVALIDSPEC escaping the
            // state machine is overwritten with a single message.
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"failed to parse revision specifier - Invalid pattern '{spec}'",
                GitErrorCategory.Invalid,
                ex);
        }
    }

    /// <summary>
    /// The character-by-character parser. Matches the <c>revparse</c> static
    /// function (revparse.c:689-861). Returns the resolved object and the
    /// reference it came through (if any).
    /// </summary>
    private static async Task<(GitObject? obj, GitReference? reference, bool parsed)> ParseCoreInnerAsync(GitRepository repo, string spec, CancellationToken cancellationToken = default)
    {
        int pos = 0;
        int identifierLen = 0;
        GitObject? baseRev = null;
        GitReference? reference = null;
        bool shouldReturnReference = true;
        bool parsed = false;

        while (!parsed && pos < spec.Length)
        {
            char ch = spec[pos];
            switch (ch)
            {
                case '^':
                    {
                        shouldReturnReference = false;
                        (baseRev, reference) = await EnsureBaseRevLoadedAsync(repo, spec, identifierLen, baseRev, reference, allowEmptyIdentifier: false, cancellationToken).ConfigureAwait(false);
                        Debug.Assert(baseRev is not null, "EnsureBaseRevLoadedAsync yields non-null baseRev for non-empty identifier");

                        if (pos + 1 < spec.Length && spec[pos + 1] == '{')
                        {
                            string content = ExtractCurlyBracesContent(spec, ref pos);
                            GitObject temp = await HandleCaretCurlySyntaxAsync(repo, baseRev, content, cancellationToken).ConfigureAwait(false);
                            baseRev = temp;
                        }
                        else
                        {
                            int n = ExtractHowMany(spec, ref pos);
                            GitObject temp = await HandleCaretParentSyntaxAsync(repo, baseRev, n, cancellationToken).ConfigureAwait(false);
                            baseRev = temp;
                        }

                        break;
                    }

                case '~':
                    {
                        shouldReturnReference = false;
                        int n = ExtractHowMany(spec, ref pos);
                        (baseRev, reference) = await EnsureBaseRevLoadedAsync(repo, spec, identifierLen, baseRev, reference, allowEmptyIdentifier: false, cancellationToken).ConfigureAwait(false);
                        Debug.Assert(baseRev is not null, "EnsureBaseRevLoadedAsync yields non-null baseRev for non-empty identifier");
                        GitObject temp = await HandleLinearSyntaxAsync(repo, baseRev, n, cancellationToken).ConfigureAwait(false);
                        baseRev = temp;
                        break;
                    }

                case ':':
                    {
                        shouldReturnReference = false;
                        string path = ExtractPath(spec, ref pos);

                        if (AnyLeftHandIdentifier(baseRev, reference, identifierLen))
                        {
                            (baseRev, reference) = await EnsureBaseRevLoadedAsync(repo, spec, identifierLen, baseRev, reference, allowEmptyIdentifier: true, cancellationToken).ConfigureAwait(false);
                            Debug.Assert(baseRev is not null, "EnsureBaseRevLoadedAsync yields non-null baseRev when left-hand identifier is present");
                            GitObject temp = await HandleColonSyntaxAsync(repo, baseRev, path, cancellationToken).ConfigureAwait(false);
                            baseRev = temp;
                        }
                        else
                        {
                            if (path.Length > 0 && path[0] == '/')
                            {
                                baseRev = await HandleGrepSyntaxAsync(repo, null, path[1..], cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                // C (revparse.c:786-795): bare ":path" /
                                // ":stage:path" index lookup is unimplemented
                                // in libgit2 1.9.4 — GIT_ERROR (-1) "unimplemented".
                                throw new GitException(
                                    GitErrorCode.Error,
                                    "unimplemented",
                                    GitErrorCategory.Invalid);
                            }
                        }

                        break;
                    }

                case '@':
                    {
                        if (pos + 1 < spec.Length && spec[pos + 1] == '{')
                        {
                            string content = ExtractCurlyBracesContent(spec, ref pos);
                            EnsureBaseRevIsNotKnownYet(baseRev);
                            (GitObject? temp, GitReference? atRef) = await HandleAtSyntaxAsync(repo, spec, identifierLen, content, reference, cancellationToken).ConfigureAwait(false);
                            reference = atRef;
                            if (temp is not null)
                            {
                                baseRev = temp;
                            }

                            break;
                        }
                        else if (pos + 1 >= spec.Length && pos == 0)
                        {
                            // Bare "@" → HEAD.
                            spec = "HEAD";
                            identifierLen = 4;
                            parsed = true;
                            break;
                        }

                        goto default;
                    }

                default:
                    EnsureLeftHandIdentifierIsNotKnownYet(baseRev, reference);
                    pos++;
                    identifierLen++;
                    break;
            }
        }

        (baseRev, reference) = await EnsureBaseRevLoadedAsync(repo, spec, identifierLen, baseRev, reference, allowEmptyIdentifier: false, cancellationToken).ConfigureAwait(false);

        if (!shouldReturnReference)
        {
            reference = null;
        }

        return (baseRev, reference, parsed);
    }

    // ── Production handlers ─────────────────────────────────────────────

    /// <summary>Matches <c>handle_caret_parent_syntax</c> (revparse.c:408-426).</summary>
    private static async Task<GitObject> HandleCaretParentSyntaxAsync(GitRepository repo, GitObject obj, int n, CancellationToken cancellationToken)
    {
        Commit commit;
        try
        {
            commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code is GitErrorCode.Ambiguous or GitErrorCode.NotFound)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "cannot peel to commit for parent selection", GitErrorCategory.Invalid, ex);
        }

        if (n == 0)
        {
            return commit;
        }

        GitOid parentId;
        try
        {
            parentId = commit.ParentId(n - 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            // C (commit.c:704-708): "parent %u does not exist", GIT_ENOTFOUND with GIT_ERROR_INVALID (n is the 0-based index).
            throw new GitException(GitErrorCode.NotFound, $"parent {n - 1} does not exist", GitErrorCategory.Invalid);
        }

        return await repo.Objects.LookupAsync(parentId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"parent {n} of {commit.Id} not found", GitErrorCategory.Object);
    }

    /// <summary>Matches <c>handle_linear_syntax</c> (revparse.c:428-441).</summary>
    private static async Task<GitObject> HandleLinearSyntaxAsync(GitRepository repo, GitObject obj, int n, CancellationToken cancellationToken)
    {
        Commit commit;
        try
        {
            commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code is GitErrorCode.Ambiguous or GitErrorCode.NotFound)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "cannot peel to commit for ~N walk", GitErrorCategory.Invalid, ex);
        }

        GitOid ancestorId = await commit.NthGenAncestorAsync(n, cancellationToken).ConfigureAwait(false);
        return await repo.Objects.LookupAsync(ancestorId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"{n}-th ancestor of {commit.Id} not found", GitErrorCategory.Object);
    }

    /// <summary>Matches <c>handle_caret_curly_syntax</c> (revparse.c:531-547).</summary>
    private static async Task<GitObject> HandleCaretCurlySyntaxAsync(GitRepository repo, GitObject obj, string content, CancellationToken cancellationToken)
    {
        if (content.Length == 0)
        {
            return await DereferenceToNonTagAsync(obj, cancellationToken).ConfigureAwait(false);
        }

        if (content[0] == '/')
        {
            return await HandleGrepSyntaxAsync(repo, obj.Id, content[1..], cancellationToken).ConfigureAwait(false);
        }

        GitObjectType expectedType = ParseObjType(content);
        if (expectedType == GitObjectType.Ext1)
        {
            throw new GitException(GitErrorCode.InvalidSpec, $"unknown object type '{content}'", GitErrorCategory.Invalid);
        }

        return await PeelToTypeAsync(obj, expectedType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Matches <c>handle_colon_syntax</c> (revparse.c:443-474).</summary>
    private static async Task<GitObject> HandleColonSyntaxAsync(GitRepository repo, GitObject obj, string path, CancellationToken cancellationToken)
    {
        GitTree tree;
        try
        {
            tree = await obj.PeelAsync<GitTree>(cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "cannot peel to tree for path lookup", GitErrorCategory.Invalid, ex);
        }

        if (path.Length == 0)
        {
            return tree;
        }

        GitTreeEntry entry = await tree.EntryByPathAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"the path '{path}' does not exist in the given tree",
                GitErrorCategory.Tree);

        return await repo.Objects.LookupAsync(entry.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"object {entry.Id} at '{path}' not found", GitErrorCategory.Object);
    }

    /// <summary>Matches <c>handle_grep_syntax</c> (revparse.c:502-529).</summary>
    private static async Task<Commit> HandleGrepSyntaxAsync(GitRepository repo, GitOid? specOid, string pattern, CancellationToken cancellationToken)
    {
        if (pattern.Length == 0)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "empty regex pattern", GitErrorCategory.Invalid);
        }

        RegexAdapter regex;
        try
        {
            regex = RegexAdapter.Compile(pattern);
        }
        catch (ArgumentException ex)
        {
            // C (regexp.c:87-94): a rejected pattern is GIT_EINVALIDSPEC
            // (wrapped by the revparse state machine).
            throw new GitException(GitErrorCode.InvalidSpec, $"invalid regex pattern '{pattern}': {ex.Message}", GitErrorCategory.Invalid, ex);
        }

        using var walk = new GitRevWalker(repo);
        walk.Sort = GitSortMode.Time;

        if (specOid is { } oid)
        {
            await walk.PushAsync(oid, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await walk.PushGlobAsync("refs/*", cancellationToken).ConfigureAwait(false);
        }

        await foreach (GitOid commitOid in walk.WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            Commit? commit = await repo.Objects.LookupAsync<Commit>(commitOid, cancellationToken).ConfigureAwait(false);
            if (commit is null)
            {
                continue;
            }

            bool matched;
            try
            {
                // C (revparse.c:488) runs git_regexp_match over the raw message bytes; the byte overload matches in that domain. Commit.Message is a UTF-8
                // decode of the raw message, so re-encoding is byte-faithful for valid UTF-8 messages (the overwhelmingly common case).
                matched = regex.IsMatch(Encoding.UTF8.GetBytes(commit.Message));
            }
            catch (RegexMatchTimeoutException ex)
            {
                // each
                // match is bounded (RegexAdapter's 1 s budget — the managed
                // equivalent of PCRE's match-step limit). Surface the expiry
                // as an InvalidSpec error like an uncompileable pattern.
                throw new GitException(GitErrorCode.InvalidSpec, $"regex pattern '{pattern}' timed out", GitErrorCategory.Invalid, ex);
            }

            if (matched)
            {
                return commit;
            }
        }

        throw new GitException(GitErrorCode.NotFound, $"no commit matching /{pattern}/ found", GitErrorCategory.Object);
    }

    /// <summary> Port of <c>try_parse_numeric</c> → <c>git__strntol32</c> (revparse.c:344-350, util.c:128-145): skips leading ASCII whitespace, accepts one
    /// sign, then requires a digit run that consumes the whole string (C: <c>end_ptr == '\0'</c>) and fits int32. </summary>
    private static bool TryParseStrntol32(string content, out int value)
    {
        value = 0;
        int i = 0;
        while (i < content.Length && AsciiText.IsAsciiSpace(content[i]))
        {
            i++;
        }

        bool negative = false;
        if (i < content.Length && content[i] is '-' or '+')
        {
            negative = content[i] == '-';
            i++;
        }

        int start = i;
        while (i < content.Length && content[i] is >= '0' and <= '9')
        {
            i++;
        }

        if (i == start || i != content.Length)
        {
            return false; // no digits, or trailing junk (end_ptr != '\0')
        }

        if (!int.TryParse(content.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return false; // overflow (strntol32 truncation error)
        }

        value = negative ? -parsed : parsed;
        return true;
    }

    /// <summary>Matches <c>handle_at_syntax</c> (revparse.c:337-381).</summary>
    private static async Task<(GitObject? obj, GitReference? reference)> HandleAtSyntaxAsync(GitRepository repo, string spec, int identifierLen, string content, GitReference? reference, CancellationToken cancellationToken = default)
    {
        string identifier = identifierLen > 0 ? spec[..identifierLen] : string.Empty;

        // C (revparse.c:344-350, util.c:36-124): try_parse_numeric → git__strntol32 SKIPS leading ASCII whitespace and accepts a sign; the digits must consume
        // the ENTIRE content (end_ptr == '\0') — "@{ 5}" IS numeric, "@{ 5 }" is not.
        bool isNumeric = TryParseStrntol32(content, out int parsed);

        if (content.Length > 0 && content[0] == '-' && (!isNumeric || parsed == 0))
        {
            throw new GitException(GitErrorCode.InvalidSpec, $"invalid @{{{content}}} spec", GitErrorCategory.Invalid);
        }

        if (isNumeric)
        {
            if (parsed < 0)
            {
                (GitObject obj, GitReference? newRef) = await RetrievePreviouslyCheckedOutBranchAsync(repo, identifier, -parsed, reference, cancellationToken).ConfigureAwait(false);
                return (obj, newRef);
            }

            {
                (GitObject obj, GitReference? newRef) = await RetrieveRevObjectFromReflogAsync(repo, identifier, parsed, reference, cancellationToken).ConfigureAwait(false);
                return (obj, newRef);
            }
        }

        if (content is "u" or "upstream")
        {
            GitReference? upstream = await RetrieveRemoteTrackingReferenceAsync(repo, identifier, reference, cancellationToken).ConfigureAwait(false);
            return (null, upstream);
        }

        // Date-based reflog lookup. C keeps the timestamp 64-bit and passes
        // (size_t)timestamp (revparse.c:371-376) — values beyond int32 must
        // not wrap into the position-based search.
        if (!GitDateParser.TryParse(content, out GitTime gitTime))
        {
            throw new GitException(GitErrorCode.InvalidSpec, $"could not parse @{{{content}}} as date", GitErrorCategory.Invalid);
        }

        {
            (GitObject obj, GitReference? newRef) = await RetrieveRevObjectFromReflogAsync(repo, identifier, gitTime.Seconds, reference, cancellationToken).ConfigureAwait(false);
            return (obj, newRef);
        }
    }

    // ── Reflog / upstream retrieval ─────────────────────────────────────

    /// <summary>Matches <c>retrieve_revobject_from_reflog</c> (revparse.c:271-307).</summary>
    private static async Task<(GitObject obj, GitReference? reference)> RetrieveRevObjectFromReflogAsync(GitRepository repo, string identifier, long position, GitReference? reference, CancellationToken cancellationToken)
    {
        GitReference? ref_;
        if (reference is null)
        {
            // When HEAD@{n} is specified, do not use dwim (which would resolve to the branch).
            if (position > 0 && identifier == GitReferences.HeadFile)
            {
                ref_ = await repo.Refs.LookupAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ref_ = await repo.Refs.DwimAsync(identifier, cancellationToken).ConfigureAwait(false);
            }

            if (ref_ is null)
            {
                throw new GitException(GitErrorCode.NotFound, $"reference '{identifier}' not found", GitErrorCategory.Reference);
            }
        }
        else
        {
            ref_ = reference;
            reference = null;
        }

        GitDirectReference direct = await ResolveToDirectAsync(repo, ref_, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"reference '{ref_.Name}' does not resolve", GitErrorCategory.Reference);

        if (position == 0)
        {
            return ((await repo.Objects.LookupAsync(direct.Target, cancellationToken).ConfigureAwait(false))
                ?? throw new GitException(GitErrorCode.NotFound, $"object {direct.Target} not found", GitErrorCategory.Object), reference);
        }

        GitOid oid = await RetrieveOidFromReflogAsync(repo, ref_.NameKey, position, cancellationToken).ConfigureAwait(false);
        return ((await repo.Objects.LookupAsync(oid, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(GitErrorCode.NotFound, $"object {oid} not found", GitErrorCategory.Object), reference);
    }

    /// <summary>Matches <c>retrieve_oid_from_reflog</c> (revparse.c:214-269).</summary>
    private static async Task<GitOid> RetrieveOidFromReflogAsync(GitRepository repo, RefNameKey refName, long identifier, CancellationToken cancellationToken)
    {
        GitRefLog reflog = await repo.Refs.ReadLogAsync(refName, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"no reflog for '{refName}'", GitErrorCategory.Reference);

        // C takes the
        // position branch only for a size_t identifier <= 100000000 — the
        // date path casts (size_t)seconds, so a pre-1970 (negative)
        // timestamp becomes a huge size_t and takes the TIME-based branch.
        // Treating a negative long as a position would make
        // reflog[(int)negative] return the wrong (oldest) entry or throw an
        // unhandled ArgumentOutOfRangeException on an empty reflog, where C
        // returns GIT_ENOTFOUND.
        bool searchByPos = identifier is >= 0 and <= 100000000;

        if (searchByPos)
        {
            if (reflog.EntryCount < identifier + 1)
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"reflog for '{refName}' has only {reflog.EntryCount} entries, asked for {identifier}",
                    GitErrorCategory.Reference);
            }

            return reflog[(int)identifier].NewId;
        }

        // Time-based: find the newest entry with committer time <= identifier.
        // C (revparse.c:236-255): when every entry is newer than the requested
        // timestamp, the search falls back to the OLDEST entry (the last one
        // examined in most-recent-first order) instead of failing.
        GitRefLogEntry? found = null;
        GitRefLogEntry? lastExamined = null;
        foreach (GitRefLogEntry entry in reflog)
        {
            lastExamined = entry;
            if (entry.Committer.When.Seconds > identifier)
            {
                continue;
            }

            found = entry;
            break;
        }

        if (found is { } e)
        {
            return e.NewId;
        }

        if (lastExamined is { } o)
        {
            return o.NewId;
        }

        throw new GitException(GitErrorCode.NotFound, $"reflog for '{refName}' has only 0 entries, asked for {identifier}", GitErrorCategory.Reference);
    }

    [GeneratedRegex("checkout: moving from (.*) to .*", RegexOptions.None)]
    private static partial Regex CheckoutMovingFromRegex { get; }

    /// <summary>Matches <c>retrieve_previously_checked_out_branch_or_revision</c> (revparse.c:147-212).</summary>
    private static async Task<(GitObject obj, GitReference? reference)> RetrievePreviouslyCheckedOutBranchAsync(GitRepository repo, string identifier, int position, GitReference? reference, CancellationToken cancellationToken)
    {
        if (identifier.Length != 0 || reference is not null)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "@{-N} cannot have a left-hand identifier", GitErrorCategory.Invalid);
        }

        GitRefLog headReflog = await repo.Refs.ReadLogAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "no HEAD reflog", GitErrorCategory.Reference);

        var regex = new RegexAdapter(CheckoutMovingFromRegex);
        int cur = position;
        var matches = new RegexMatch[2];

        foreach (GitRefLogEntry entry in headReflog)
        {
            string msg = entry.Message;
            if (string.IsNullOrEmpty(msg))
            {
                continue;
            }

            // The pattern is pure ASCII ("checkout: moving from (.*) to.*"), so it is domain-invariant; the byte overload matches the raw reflog message bytes
            // and returns byte offsets.
            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            if (!regex.Search(msgBytes, matches))
            {
                continue;
            }

            cur--;
            if (cur > 0)
            {
                continue;
            }

            RegexMatch g1 = matches[1];
            if (g1.IsUnset)
            {
                break;
            }

            string branchName = Encoding.UTF8.GetString(msgBytes.AsSpan(g1.Start, g1.End - g1.Start));
            if (string.IsNullOrEmpty(branchName))
            {
                break;
            }

            // Try DWIM first.
            GitReference? dwimRef = await repo.Refs.DwimAsync(branchName, cancellationToken).ConfigureAwait(false);
            if (dwimRef is GitDirectReference directRef)
            {
                GitObject obj = await repo.Objects.LookupAsync(directRef.Target, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitException(GitErrorCode.NotFound, $"object {directRef.Target} not found", GitErrorCategory.Object);
                return (obj, dwimRef);
            }

            // Fall back to abbrev OID.
            if (GitOid.TryParse(branchName, repo.ObjectFormat, out GitOid prefix))
            {
                return ((await repo.Objects.LookupPrefixAsync(prefix, cancellationToken).ConfigureAwait(false))
                    ?? throw new GitException(GitErrorCode.NotFound, $"@{{-{position}}} resolved to '{branchName}' which was not found", GitErrorCategory.Object), reference);
            }

            break;
        }

        // C (revparse.c:147-212): when the previous target is gone the parse ends with "unable to parse OID - contains invalid characters", GIT_ERROR_INVALID.
        throw new GitException(GitErrorCode.NotFound, $"no previous checkout matching @{{-{position}}}", GitErrorCategory.Invalid);
    }

    /// <summary>Matches <c>retrieve_remote_tracking_reference</c> (revparse.c:309-335).</summary>
    private static async Task<GitReference?> RetrieveRemoteTrackingReferenceAsync(GitRepository repo, string identifier, GitReference? reference, CancellationToken cancellationToken = default)
    {
        GitReference? ref_;
        if (reference is null)
        {
            ref_ = await repo.Refs.DwimAsync(identifier, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"reference '{identifier}' not found", GitErrorCategory.Reference);
        }
        else
        {
            ref_ = reference;
        }

        if (!ref_.IsBranch)
        {
            throw new GitException(GitErrorCode.InvalidSpec, $"'{ref_.Name}' is not a branch; cannot resolve upstream", GitErrorCategory.Invalid);
        }

        // C (branch.c:639-642, git_branch_upstream): a missing tracking ref
        // propagates the reference-lookup error — "reference '%s' not found"
        string upstreamName = await repo.BranchUpstreamNameAsync(ref_.NameKey.ToUtf8StringStrict(), cancellationToken).ConfigureAwait(false);
        GitReference? upstream = await repo.Refs.ResolveAsync(upstreamName, cancellationToken).ConfigureAwait(false);
        if (upstream is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{upstreamName}' not found",
                GitErrorCategory.Reference);
        }

        return upstream;
    }

    // ── DWIM / base-rev resolution ──────────────────────────────────────

    /// <summary>Matches <c>ensure_base_rev_loaded</c> (revparse.c:636-657).</summary>
    private static async Task<(GitObject? baseRev, GitReference? reference)> EnsureBaseRevLoadedAsync(
        GitRepository repo, string spec, int identifierLen, GitObject? baseRev, GitReference? reference, bool allowEmptyIdentifier, CancellationToken cancellationToken)
    {
        if (baseRev is not null)
        {
            return (baseRev, reference);
        }

        if (reference is not null)
        {
            // C: object_from_reference (revparse.c:635-643) — a reference that
            // cannot be resolved or whose object is missing fails the parse.
            GitDirectReference? direct = await ResolveToDirectAsync(repo, reference, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"reference '{reference.Name}' does not resolve", GitErrorCategory.Reference);

            baseRev = await repo.Objects.LookupAsync(direct.Target, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"object {direct.Target} not found", GitErrorCategory.Object);
            return (baseRev, reference);
        }

        if (!allowEmptyIdentifier && identifierLen == 0)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "no base revision", GitErrorCategory.Invalid);
        }

        string identifier = spec[..identifierLen];
        (GitObject? obj, GitReference? ref_) = await RevparseLookupObjectAsync(repo, identifier, cancellationToken).ConfigureAwait(false);
        if (ref_ is not null)
        {
            reference = ref_;
        }

        return (obj, reference);
    }

    /// <summary>Matches <c>revparse_lookup_object</c> (revparse.c:93-129) — the DWIM dispatcher.</summary>
    private static async Task<(GitObject? obj, GitReference? reference)> RevparseLookupObjectAsync(GitRepository repo, string spec, CancellationToken cancellationToken)
    {
        // 1. Full SHA.
        if (spec.Length == GitOid.HexSizeFor(repo.ObjectFormat))
        {
            GitObject? obj = await TryResolveShaAsync(repo, spec, cancellationToken).ConfigureAwait(false);
            if (obj is not null)
            {
                return (obj, null);
            }
        }

        // 2. DWIM ref.
        GitReference? dwimRef = await repo.Refs.DwimAsync(spec, cancellationToken).ConfigureAwait(false);
        if (dwimRef is GitDirectReference direct)
        {
            // C (revparse.c:107-114): the object lookup failure propagates
            // (dangling refs must not degrade to a null object).
            GitObject? obj = await repo.Objects.LookupAsync(direct.Target, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"object {direct.Target} not found", GitErrorCategory.Object);
            return (obj, dwimRef);
        }

        // 3. Abbreviated OID. C (revparse.c:120-122) tries any spec shorter
        // than the full hex size; git_object_lookup_prefix then rejects
        // prefixes < 4 with GIT_EAMBIGUOUS ("OID prefix is too short").
        if (spec.Length < GitOid.HexSizeFor(repo.ObjectFormat))
        {
            GitObject? obj = await TryResolveAbbrevAsync(repo, spec, cancellationToken).ConfigureAwait(false);
            if (obj is not null)
            {
                return (obj, null);
            }
        }

        // 4. Describe-style spec ("v1.0-5-g1234567") — maybe_describe
        // (revparse.c:70-91): the tail after "-g" is an abbreviated OID.
        int dashG = spec.IndexOf("-g", StringComparison.Ordinal);
        if (dashG >= 0 && s_describeRegex.IsMatch(Encoding.UTF8.GetBytes(spec)))
        {
            GitObject? obj = await TryResolveAbbrevAsync(repo, spec[(dashG + 2)..], cancellationToken).ConfigureAwait(false);
            if (obj is not null)
            {
                return (obj, null);
            }
        }

        // 5. C (revparse.c:127-128): "revspec '%s' not found", GIT_ENOTFOUND.
        throw new GitException(GitErrorCode.NotFound, $"revspec '{spec}' not found", GitErrorCategory.Reference);
    }

    /// <summary> Matches <c>maybe_describe</c>'s pattern (revparse.c:79): ".+-[0-9]+-g[0-9a-fA-F]+". The byte-domain adapter — C runs <c>git_regexp_match</c>
    /// over the raw spec bytes (revparse.c:84), so the match counts bytes. The pattern is pure ASCII, so ASCII specs behave identically to a
    /// string match. </summary>
    private static readonly RegexAdapter s_describeRegex = RegexAdapter.Compile(".+-[0-9]+-g[0-9a-fA-F]+");

    private static async Task<GitObject?> TryResolveShaAsync(GitRepository repo, string spec, CancellationToken cancellationToken)
    {
        if (spec.Length != GitOid.HexSizeFor(repo.ObjectFormat))
        {
            return null;
        }

        if (!GitOid.TryParse(spec, repo.ObjectFormat, out GitOid oid))
        {
            return null;
        }

        return await repo.Objects.LookupAsync(oid, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitObject?> TryResolveAbbrevAsync(GitRepository repo, string spec, CancellationToken cancellationToken)
    {
        if (!GitOid.TryParse(spec, repo.ObjectFormat, out GitOid prefix))
        {
            return null;
        }

        return await repo.Objects.LookupPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
    }

    // ── Small helpers ───────────────────────────────────────────────────

    /// <summary>Matches <c>extract_curly_braces_content</c> (revparse.c:549-573).</summary>
    private static string ExtractCurlyBracesContent(string spec, ref int pos)
    {
        // spec[pos] is '^' or '@'.
        pos++;

        if (pos >= spec.Length || spec[pos] != '{')
        {
            throw new GitException(GitErrorCode.InvalidSpec, "expected '{'", GitErrorCategory.Invalid);
        }

        pos++;
        int start = pos;
        while (pos < spec.Length && spec[pos] != '}')
        {
            pos++;
        }

        if (pos >= spec.Length)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "unterminated '{...}'", GitErrorCategory.Invalid);
        }

        string content = spec[start..pos];
        pos++; // skip '}'
        return content;
    }

    /// <summary>Matches <c>extract_path</c> (revparse.c:575-589).</summary>
    private static string ExtractPath(string spec, ref int pos)
    {
        // spec[pos] is ':'.
        pos++;
        string path = spec[pos..];
        pos = spec.Length;
        return path;
    }

    /// <summary>Matches <c>extract_how_many</c> (revparse.c:591-620).</summary>
    private static int ExtractHowMany(string spec, ref int pos)
    {
        char kind = spec[pos];
        int accumulated = 0;

        do
        {
            do
            {
                pos++;
                accumulated++;
            }
            while (pos < spec.Length && spec[pos] == kind && kind == '~');

            if (pos < spec.Length && char.IsDigit(spec[pos]))
            {
                int start = pos;
                while (pos < spec.Length && char.IsDigit(spec[pos]))
                {
                    pos++;
                }

                if (!int.TryParse(spec.AsSpan(start, pos - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    throw new GitException(GitErrorCode.InvalidSpec, "invalid numeric count after ^/~", GitErrorCategory.Invalid);
                }

                accumulated += parsed - 1;
            }
        }
        while (pos < spec.Length && spec[pos] == kind && kind == '~');

        // An unchecked
        // accumulation wraps negative for specs like ~2147483647~2147483647;
        // C passes the wrapped value to git_commit_nth_gen_ancestor as a
        // huge unsigned count (walking to the root and failing with
        // GIT_ENOTFOUND). Reject negative counts so the wrapped value cannot
        // silently return the commit itself.
        if (accumulated < 0)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "invalid numeric count after ^/~", GitErrorCategory.Invalid);
        }

        return accumulated;
    }

    /// <summary>Matches <c>parse_obj_type</c> (revparse.c:383-398).</summary>
    private static GitObjectType ParseObjType(string str) => str switch
    {
        "commit" => GitObjectType.Commit,
        "tree" => GitObjectType.Tree,
        "blob" => GitObjectType.Blob,
        "tag" => GitObjectType.Tag,
        _ => GitObjectType.Ext1,
    };

    /// <summary>Matches <c>dereference_to_non_tag</c> (revparse.c:400-406).</summary>
    private static async Task<GitObject> DereferenceToNonTagAsync(GitObject obj, CancellationToken cancellationToken)
    {
        // Walk the tag→target chain until a non-tag is reached (git_tag_peel).
        // the artificial
        // 50-depth cap returned a still-GitTag object for ^{} on deeper
        // chains; C peels unboundedly (revparse.c:400-406). The loop is
        // safe: tag→target OIDs are content-addressed and cannot cycle.
        GitObject current = obj;
        while (current is GitTag tag)
        {
            if (tag.Owner is null)
            {
                throw new GitException(GitErrorCode.Peel, "tag has no owning repository; cannot dereference", GitErrorCategory.Object);
            }

            current = await tag.Owner.Objects.LookupAsync(tag.Target, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"tag target {tag.Target} not found", GitErrorCategory.Object);
        }

        return current;
    }

    /// <summary>Peels <paramref name="obj"/> to <paramref name="type"/> via <see cref="GitObject.PeelAsync{T}"/>.</summary>
    private static async Task<GitObject> PeelToTypeAsync(GitObject obj, GitObjectType type, CancellationToken cancellationToken)
    {
        // The peel mirrors git_object_peel: cross-type combinations raise
        // InvalidSpec, chain-end raises Peel, missing links raise NotFound —
        // all propagate as-is (C wraps only EINVALIDSPEC).
        return type switch
        {
            GitObjectType.Commit => await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false),
            GitObjectType.Tree => await obj.PeelAsync<GitTree>(cancellationToken).ConfigureAwait(false),
            GitObjectType.Blob => await obj.PeelAsync<GitBlob>(cancellationToken).ConfigureAwait(false),
            GitObjectType.Tag => await obj.PeelAsync<GitTag>(cancellationToken).ConfigureAwait(false),
            _ => throw new GitException(GitErrorCode.InvalidSpec, $"unknown type {type}", GitErrorCategory.Invalid),
        };
    }

    /// <summary>Matches <c>ensure_base_rev_is_not_known_yet</c> (revparse.c:659-665).</summary>
    private static void EnsureBaseRevIsNotKnownYet(GitObject? obj)
    {
        if (obj is not null)
        {
            throw new GitException(GitErrorCode.InvalidSpec, "cannot chain @{...} productions", GitErrorCategory.Invalid);
        }
    }

    /// <summary>Matches <c>any_left_hand_identifier</c> (revparse.c:667-679).</summary>
    private static bool AnyLeftHandIdentifier(GitObject? obj, GitReference? reference, int identifierLen)
        => obj is not null || reference is not null || identifierLen > 0;

    /// <summary>Matches <c>ensure_left_hand_identifier_is_not_known_yet</c> (revparse.c:681-687).</summary>
    private static void EnsureLeftHandIdentifierIsNotKnownYet(GitObject? obj, GitReference? reference)
    {
        if (obj is null && reference is null)
        {
            return;
        }

        throw new GitException(GitErrorCode.InvalidSpec, "left-hand identifier already resolved", GitErrorCategory.Invalid);
    }

    /// <summary>
    /// Resolves a reference (symbolic or direct) to its direct form. Direct
    /// refs and null (the majority) return synchronously; only symbolic refs
    /// take the disk-IO path.
    /// </summary>
    private static ValueTask<GitDirectReference?> ResolveToDirectAsync(GitRepository repo, GitReference? reference, CancellationToken cancellationToken)
    {
        if (reference is GitDirectReference d)
        {
            return ValueTask.FromResult<GitDirectReference?>(d);
        }

        if (reference is null)
        {
            return ValueTask.FromResult<GitDirectReference?>(null);
        }

        return new ValueTask<GitDirectReference?>(ResolveToDirectSlowAsync(repo, reference.NameKey, cancellationToken));
    }

    /// <summary>Slow path of <see cref="ResolveToDirectAsync"/>: resolves a symbolic ref chain (disk IO).</summary>
    private static async Task<GitDirectReference?> ResolveToDirectSlowAsync(GitRepository repo, RefNameKey referenceName, CancellationToken cancellationToken)
        => await repo.Refs.ResolveAsync(referenceName, cancellationToken).ConfigureAwait(false) as GitDirectReference;
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

using EncodingAlias = System.Text.Encoding;

namespace LibGit2CS.Objects;

/// <summary>
/// A git commit object. Managed port of libgit2's <c>src/libgit2/commit.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Parsed from the raw commit body via <see cref="Parse"/>. The body format is
/// line-oriented: <c>tree &lt;oid&gt;\n</c>, zero or more <c>parent &lt;oid&gt;\n</c>,
/// <c>author &lt;sig&gt;\n</c>, <c>committer &lt;sig&gt;\n</c>, optional
/// <c>encoding &lt;name&gt;\n</c> and other headers, blank line, message.
/// </para>
/// <para>
/// <see cref="Tree"/>/<see cref="Parents"/>/
/// <see cref="ParentId"/> return <see cref="GitOid"/>s — callers cascade explicitly
/// via <c>repo.ObjectLookupAsync&lt;GitTree&gt;(commit.Tree)</c>. This keeps the commit
/// cheap to construct and avoids hidden ODB hits.
/// </para>
/// </remarks>
public sealed class Commit : GitObject
{
    private readonly GitOid _treeId;
    private readonly GitOid[] _parentIds;
    private readonly GitSignature _author;
    private readonly GitSignature _committer;
    private readonly string? _messageEncoding;
    private readonly string _rawHeader;
    private readonly string _rawMessage;
    private readonly int _separatorIndex;
    private string? _summary;
    private byte[]? _summaryBytes;
    private string? _body;

    private Commit(
        GitRepository? owner,
        GitOid id,
        long size,
        ReadOnlyMemory<byte> raw,
        GitOid treeId,
        GitOid[] parentIds,
        GitSignature author,
        GitSignature committer,
        string? messageEncoding,
        string rawHeader,
        string rawMessage,
        int separatorIndex)
        : base(owner, id, GitObjectType.Commit, size, raw)
    {
        _treeId = treeId;
        _parentIds = parentIds;
        _author = author;
        _committer = committer;
        _messageEncoding = messageEncoding;
        _rawHeader = rawHeader;
        _rawMessage = rawMessage;
        _separatorIndex = separatorIndex;
    }

    /// <summary>
    /// The tree OID this commit points at. Matches <c>git_commit_tree_id</c>.
    /// Use <c>repo.ObjectLookupAsync&lt;GitTree&gt;(commit.Tree)</c> to load the tree.
    /// </summary>
    public GitOid Tree => _treeId;

    /// <summary>
    /// The parent commit OIDs, in order. Empty for a root commit. Matches
    /// iterating <c>git_commit_parent</c> via <c>git_commit_parentcount</c>.
    /// </summary>
    public IReadOnlyList<GitOid> Parents => _parentIds;

    /// <summary>
    /// The author signature (who wrote the change). Matches <c>git_commit_author</c>.
    /// </summary>
    public GitSignature Author => _author;

    /// <summary>
    /// The committer signature (who applied the change). Matches <c>git_commit_committer</c>.
    /// </summary>
    public GitSignature Committer => _committer;

    /// <summary>
    /// The commit time, taken from <see cref="Committer"/>. Matches <c>git_commit_time</c>.
    /// </summary>
    public GitTime Time => _committer.When;

    /// <summary>
    /// The text encoding declared in the commit header (via <c>encoding &lt;name&gt;</c>),
    /// or null if not present. Matches <c>git_commit_message_encoding</c>.
    /// </summary>
    public string? Encoding => _messageEncoding;

    /// <summary>
    /// The commit message with leading newlines trimmed. Matches <c>git_commit_message</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="RawMessage"/> is the untrimmed version. The difference matters
    /// for commits with leading blank lines in the message body.
    /// </remarks>
    public string Message
    {
        get
        {
            int i = 0;
            while (i < _rawMessage.Length && _rawMessage[i] == '\n')
            {
                i++;
            }

            return _rawMessage[i..];
        }
    }

    /// <summary>
    /// The raw commit message, including any leading newlines. Matches
    /// <c>git_commit_message_raw</c>.
    /// </summary>
    public string RawMessage => _rawMessage;

    /// <summary>
    /// The full commit header text, ending with the last header line's
    /// newline (the blank separator line is NOT included). Matches
    /// <c>git_commit_raw_header</c> (commit.c:488-499). Used by
    /// <see cref="HeaderField"/> and <see cref="ExtractSignature"/>.
    /// </summary>
    public string RawHeader => _rawHeader;

    /// <summary> The raw commit message bytes, including any leading newlines. byte-parity surface — a zero-copy slice of the retained object buffer (C's
    /// <c>git_commit_message_raw</c> returns the raw bytes, commit.c:578-579). Non-UTF-8 messages round-trip byte-exact. </summary>
    public ReadOnlyMemory<byte> RawMessageBytes
        => _separatorIndex < 0 ? ReadOnlyMemory<byte>.Empty : Raw[(_separatorIndex + 2)..];

    /// <summary> The commit message bytes with leading newlines trimmed. byte-parity surface — C's <c>git_commit_message</c> trims leading <c>'\n'</c> bytes
    /// (commit.c:586-599). </summary>
    public ReadOnlyMemory<byte> MessageBytes
    {
        get
        {
            ReadOnlyMemory<byte> raw = RawMessageBytes;
            int i = 0;
            while (i < raw.Length && raw.Span[i] == (byte)'\n')
            {
                i++;
            }

            return raw[i..];
        }
    }

    /// <summary> The full commit header bytes, ending with the last header line's newline. byte-parity surface — a zero-copy slice of the retained object
    /// buffer (C's <c>git_commit_raw_header</c>). </summary>
    public ReadOnlyMemory<byte> RawHeaderBytes
        => _separatorIndex < 0 ? Raw : Raw[..(_separatorIndex + 1)];

    /// <summary>
    /// The first paragraph of the commit message, with whitespace collapsed.
    /// Lazy-computed. Matches <c>git_commit_summary</c> (commit.c:601-655).
    /// </summary>
    public string Summary => _summary ??= ExtractSummary(Message);

    /// <summary> The first paragraph of the commit message bytes, with whitespace collapsed. byte-parity surface — C's <c>git_commit_summary</c>
    /// (commit.c:601-655) folds the raw message bytes with the ASCII <c>git__isspace</c> class; non-UTF-8 bytes pass through verbatim. <see cref="Summary"/> is
    /// the string display tier (computed independently over the decoded <see cref="Message"/>). </summary>
    internal ReadOnlyMemory<byte> SummaryBytes => _summaryBytes ??= ExtractSummaryBytes(MessageBytes.Span);

    /// <summary>
    /// The commit message body: everything after the first blank line, with
    /// leading/trailing whitespace trimmed. Lazy-computed. Matches
    /// <c>git_commit_body</c> (commit.c:657-682).
    /// </summary>
    public string Body => _body ??= ExtractBody(Message);

    /// <summary>
    /// Returns the OID of the <paramref name="n"/>-th parent. Throws if out of range.
    /// Matches <c>git_commit_parent_id</c>.
    /// </summary>
    public GitOid ParentId(int n)
    {
        if ((uint)n >= (uint)_parentIds.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, $"Commit has {_parentIds.Length} parents; index {n} is out of range.");
        }

        return _parentIds[n];
    }

    /// <summary>
    /// Peels this commit to <see cref="Commit"/> (itself) or its <see cref="Tree"/>.
    /// Matches <c>git_object_peel</c>'s commit→tree coercion. Other target types
    /// fall through to the base (which throws <see cref="GitErrorCode.Peel"/>).
    /// </summary>
    public override async Task<T> PeelAsync<T>(CancellationToken cancellationToken = default)
    {
        if (this is T self)
        {
            return self;
        }

        if (typeof(T) == typeof(GitTree))
        {
            if (Owner is null)
            {
                throw new GitException(
                    GitErrorCode.Peel,
                    "commit has no owning repository; cannot cascade to tree",
                    GitErrorCategory.Object);
            }

            return (T)(object)(await Owner.Objects.LookupAsync<GitTree>(_treeId, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, $"tree {_treeId} not found", GitErrorCategory.Object));
        }

        return await base.PeelAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the Nth-generation first-parent ancestor of this commit. Matches
    /// <c>git_commit_nth_gen_ancestor</c> (commit.c:713-745). Follows the
    /// first-parent chain <paramref name="n"/> times. <paramref name="n"/> == 0
    /// returns this commit's OID.
    /// </summary>
    /// <param name="n">Number of generations to walk back along the first parent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ancestor OID.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if a parent is missing before reaching
    /// the Nth generation (e.g. root commit hit early).
    /// <see cref="GitErrorCode.Invalid"/> if this commit has no owning repository.
    /// </exception>
    public async Task<GitOid> NthGenAncestorAsync(int n, CancellationToken cancellationToken = default)
    {
        if (Owner is null)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "commit has no owning repository; cannot walk ancestors",
                GitErrorCategory.Object);
        }

        GitOid currentId = Id;
        for (int i = 0; i < n; i++)
        {
            Commit current = await Owner.Objects.LookupAsync<Commit>(currentId, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"ancestor commit {currentId} not found while walking ~{n}",
                    GitErrorCategory.Object);

            if (current._parentIds.Length == 0)
            {
                // C (commit.c:704-708): git_commit_parent sets GIT_ERROR_INVALID "parent %u does not exist" (GIT_ENOTFOUND) when the walk runs past the root.
                throw new GitException(
                    GitErrorCode.NotFound,
                    "parent 0 does not exist",
                    GitErrorCategory.Invalid);
            }

            currentId = current._parentIds[0];
            current.Dispose();
        }

        return currentId;
    }

    /// <summary>
    /// Returns the value of a header field by name. Multi-line continuations
    /// (lines starting with a single space, as in <c>gpgsig</c>) are joined
    /// with <c>\n</c>, with the leading space stripped. Matches
    /// <c>git_commit_header_field</c> (commit.c:747-816).
    /// </summary>
    /// <param name="name">Header field name (e.g. <c>"gpgsig"</c>, <c>"parent"</c>).</param>
    /// <returns>The field value, or null if not present.</returns>
    public string? HeaderField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return null;
        }

        string prefix = name + " ";
        ReadOnlySpan<char> headerSpan = _rawHeader.AsSpan();
        int lineStart = 0;

        while (lineStart < headerSpan.Length)
        {
            int lineEnd = headerSpan[lineStart..].IndexOf('\n');
            if (lineEnd < 0)
            {
                lineEnd = headerSpan.Length - lineStart;
            }

            ReadOnlySpan<char> line = headerSpan.Slice(lineStart, lineEnd);

            if (line.StartsWith(prefix))
            {
                // Capture value (after prefix).
                ReadOnlySpan<char> value = line[prefix.Length..];

                // Walk forward for continuation lines (start with SP).
                int contStart = lineStart + lineEnd + 1;
                using var sb = new ValueStringBuilder(value.Length);
                sb.Append(value);

                while (contStart < headerSpan.Length && headerSpan[contStart] == ' ')
                {
                    int contEnd = headerSpan[contStart..].IndexOf('\n');
                    if (contEnd < 0)
                    {
                        contEnd = headerSpan.Length - contStart;
                    }

                    // Skip the leading SP, prepend a newline (matches git's convention).
                    sb.Append('\n');
                    sb.Append(headerSpan.Slice(contStart + 1, contEnd - 1));

                    contStart = contStart + contEnd + 1;
                }

                return sb.ToString();
            }

            lineStart = lineStart + lineEnd + 1;
        }

        return null;
    }

    /// <summary>
    /// Extracts the GPG-style signature block from this commit. Exact port of
    /// <c>git_commit_extract_signature</c> (commit.c:818-937).
    /// </summary>
    /// <returns>
    /// A tuple of (<c>signedData</c>, <c>signature</c>). <c>signedData</c> is
    /// the raw commit bytes with the signature block removed — everything
    /// before the <c>gpgsig</c> line (each header line with its newline) plus
    /// everything after the last signature continuation line's newline, so
    /// the blank separator line is preserved.
    /// </returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> "this commit is not signed" when
    /// the commit has no <c>gpgsig</c> header (C clears the outputs and
    /// returns GIT_ENOTFOUND, commit.c:919-921).
    /// </exception>
    public (ReadOnlyMemory<byte> signedData, string? signature) ExtractSignature()
    {
        // Exact port of git_commit__extract_signature (commit.c:866-921),
        // operating on the raw bytes.
        ReadOnlySpan<byte> buf = Raw.Span;
        var signedData = new List<byte>(buf.Length);

        while (true)
        {
            int nl = buf.IndexOf((byte)'\n');
            if (nl < 0 || nl + 1 >= buf.Length)
            {
                // strchr found no '\n', or the newline is the last byte
                // (h[1] == '\0') — the field was never found.
                throw new GitException(GitErrorCode.NotFound, "this commit is not signed", GitErrorCategory.Object);
            }

            int lineLen = nl + 1; // include the '\n'
            ReadOnlySpan<byte> line = buf[..lineLen];

            if (!line.StartsWith("gpgsig"u8))
            {
                signedData.AddRange(line);
                buf = buf[lineLen..];
                continue;
            }

            // Field-prefixed line: the byte after "gpgsig" must be a space.
            ReadOnlySpan<byte> after = buf["gpgsig".Length..];
            if (after.Length == 0 || after[0] != (byte)' ')
            {
                // h[0] != ' ' — C continues the scan from right after the
                // prefix (the rest of the line is re-scanned as a line).
                buf = after;
                continue;
            }

            int eol = after.IndexOf((byte)'\n');
            if (eol < 0)
            {
                throw new GitException(GitErrorCode.Error, "malformed header", GitErrorCategory.Object);
            }

            // Signature body: skip the SP after "gpgsig", up to the newline.
            const int InitialCapacity = 512;
            using var signature = new ValueStringBuilder(InitialCapacity);
            signature.AppendUtf8(after[1..eol]);

            // Multi-line signature: continuation lines start with a space.
            ReadOnlySpan<byte> rest = after[(eol + 1)..];
            while (rest.Length > 0 && rest[0] == (byte)' ')
            {
                signature.Append('\n');
                int contEol = rest.IndexOf((byte)'\n');
                if (contEol < 0)
                {
                    throw new GitException(GitErrorCode.Error, "malformed header", GitErrorCategory.Object);
                }

                signature.AppendUtf8(rest[1..contEol]);
                rest = rest[(contEol + 1)..];
            }

            // signed_data = everything after the last continuation newline.
            signedData.AddRange(rest);
            return (signedData.ToArray(), signature.ToString());
        }
    }

    // ==============================
    // Write side
    // ==============================

    /// <summary>
    /// Creates a commit object and writes it to the ODB. Matches
    /// <c>git_commit_create_from_ids</c> (commit.c:262-280) +
    /// <c>git_commit__create_internal</c> (commit.c:126-187).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="options">Commit creation options (tree, parents, signatures, message, updateRef).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the created commit.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the tree or parents don't exist in the ODB.
    /// <see cref="GitErrorCode.Modified"/> if <see cref="CommitCreateOptions.UpdateRef"/> is set but the current tip doesn't match the first parent.
    /// </exception>
    internal static async Task<GitOid> CreateAsync(GitRepository repo, CommitCreateOptions options, CancellationToken cancellationToken = default)
        => await CreateAsync(repo, options, validateTip: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a commit. Matches <c>git_commit_create</c>. With
    /// <paramref name="validateTip"/> the ref tip must equal the first parent
    /// (the C's <c>validate_tree_and_parents</c> current_id check); the amend
    /// path passes false.
    /// </summary>
    internal static async Task<GitOid> CreateAsync(GitRepository repo, CommitCreateOptions options, bool validateTip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(options);

        // Validate tree exists in the ODB — C: git_object__is_valid (a READ,
        // so the hardcoded empty tree passes even before it is written).
        if (repo.Context.Settings.StrictObjectCreation
            && !await repo.Objects.IsValidAsync(options.Tree, GitObjectType.Tree, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"failed to create commit: tree {options.Tree} does not exist",
                GitErrorCategory.Object);
        }

        // Validate parents exist in the ODB.
        foreach (GitOid parentId in options.Parents)
        {
            if (repo.Context.Settings.StrictObjectCreation
                && !await repo.Objects.IsValidAsync(parentId, GitObjectType.Commit, cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"failed to create commit: parent {parentId} does not exist",
                    GitErrorCategory.Object);
            }
        }

        // C (commit.c:109-117, git_commit__create_internal): when update_ref
        // is given, the ref's current tip must equal the first parent —
        // otherwise "failed to create commit: current tip is not the first
        // parent" (GIT_EMODIFIED) and the commit is NOT written. (The amend
        // path passes validateTip=false — C uses current_id=NULL there.)
        if (validateTip && options.UpdateRef is not null)
        {
            GitReference? current = await repo.Refs.ResolveAsync(options.UpdateRef, cancellationToken).ConfigureAwait(false);
            if (current is GitDirectReference direct)
            {
                GitOid currentId = direct.Target;
                if (options.Parents.Count == 0 || !currentId.Equals(options.Parents[0]))
                {
                    throw new GitException(
                        GitErrorCode.Modified,
                        "failed to create commit: current tip is not the first parent",
                        GitErrorCategory.Object);
                }
            }
        }

        // Build the commit buffer. MessageBytes wins over Message (byte-parity surface — C writes the message bytes verbatim, commit.c:73).
        byte[] buf = options.MessageBytes is { } messageBytes
            ? CreateBufferInternal(options.Author, options.Committer, options.MessageEncoding, messageBytes, options.Tree, options.Parents)
            : CreateBufferInternal(options.Author, options.Committer, options.MessageEncoding, options.Message, options.Tree, options.Parents);

        // Freshen the tree to prevent GC.
        _ = await repo.Objects.FreshenAsync(options.Tree, cancellationToken).ConfigureAwait(false);

        // Write to the ODB.
        GitOid oid = await repo.Objects.WriteAsync(GitObjectType.Commit, buf, cancellationToken).ConfigureAwait(false);

        // Update ref (writes via repo.Refs.CreateAsync).
        if (options.UpdateRef is not null)
        {
            // Build a reflog message. Matches C's git_reference__update_for_commit (refs.c:1162-1191): "%s%s: %s" with operation "commit" and commit_type = "
            // (initial)" for 0 parents, " (merge)" for >= 2, "" otherwise. The subject is extracted from the message BYTES (C summarizes git_commit_summary
            // over the raw bytes) — the raw bytes when MessageBytes is set, else the UTF-8 encoding of the convenience Message. A non-UTF-8 message keeps its
            // raw bytes instead of degrading to U+FFFD.
            byte[] subjectBytes = options.MessageBytes is { } commitMessageBytes
                ? ExtractSummaryBytes(commitMessageBytes.Span)
                : ExtractSummaryBytes(EncodingAlias.UTF8.GetBytes(options.Message));

            string operation = options.Parents is null || options.Parents.Count == 0
                ? "commit (initial)"
                : options.Parents.Count >= 2 ? "commit (merge)" : "commit";

            byte[] logMessageBytes = new byte[operation.Length + 2 + subjectBytes.Length];
            EncodingAlias.ASCII.GetBytes(operation, logMessageBytes);
            logMessageBytes[operation.Length] = (byte)':';
            logMessageBytes[operation.Length + 1] = (byte)' ';
            subjectBytes.AsSpan().CopyTo(logMessageBytes.AsSpan(operation.Length + 2));

            // Update the ref (force=true, matching C's git_reference_create
            // with force=1 after the tip==first-parent check above).
            await repo.Refs.CreateAsync(options.UpdateRef, oid, force: true, logMessageBytes, cancellationToken).ConfigureAwait(false);
        }

        return oid;
    }

    /// <summary>
    /// Creates a commit from the staging area (index). Matches
    /// <c>git_commit_create_from_stage</c> (commit.c:1089-1164). Writes the
    /// index as a tree, resolves HEAD parents, then calls
    /// <see cref="LibGit2CS.Objects.Commit.CreateAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.Objects.CommitCreateOptions, System.Threading.CancellationToken)"/>. If the index has no changes vs HEAD tree and
    /// <see cref="CommitCreateOptions.AllowEmptyCommit"/> is false, throws
    /// <see cref="GitErrorCode.Unchanged"/>.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="options">Commit options (author, committer, message, etc.).</param>
    /// <returns>The OID of the created commit.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<GitOid> CreateFromStageAsync(GitRepository repo, CommitCreateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(options);

        // Get the repo index.
        GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);

        // Check for empty commit (index matches HEAD tree).
        if (!options.AllowEmptyCommit)
        {
            // Resolve HEAD tree (if it exists — unborn branch is OK).
            GitTree? headTree = null;
            GitReference? headRef = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (headRef is GitDirectReference dr)
            {
                Commit? headCommit = await repo.Objects.LookupAsync<Commit>(dr.Target, cancellationToken).ConfigureAwait(false);
                if (headCommit is not null)
                {
                    headTree = await repo.Objects.LookupAsync<GitTree>(headCommit.Tree, cancellationToken).ConfigureAwait(false);
                }
            }

            // Diff HEAD tree vs index. If no deltas, reject.
            if (headTree is not null)
            {
                Diff.GitDiff diff = await Diff.GitDiff.TreeToIndexAsync(repo, headTree, null, cancellationToken).ConfigureAwait(false);
                if (diff.DeltaCount == 0)
                {
                    throw new GitException(
                        GitErrorCode.Unchanged,
                        "no changes are staged for commit",
                        GitErrorCategory.Repository);
                }
            }
        }

        // Write the index as a tree.
        GitOid treeId = await index.WriteTreeAsync(cancellationToken).ConfigureAwait(false);

        // Resolve parents from HEAD.
        var parents = new List<GitOid>();
        GitReference? head = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is GitDirectReference dr2)
        {
            parents.Add(dr2.Target);
        }

        // Create the commit with UpdateRef = "HEAD".
        CommitCreateOptions createOpts = options with
        {
            Tree = treeId,
            Parents = parents,
            UpdateRef = options.UpdateRef ?? "HEAD",
        };

        return await CreateAsync(repo, createOpts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Amends a commit: creates a new commit with modified fields, inheriting
    /// unset fields from <paramref name="commitToAmend"/>. Matches
    /// <c>git_commit_amend</c> (commit.c:331-392).
    /// </summary>
    /// <param name="commitToAmend">The original commit to amend.</param>
    /// <param name="author">New author, or null to keep the original.</param>
    /// <param name="committer">New committer, or null to keep the original.</param>
    /// <param name="messageEncoding">New encoding, or null to keep the original.</param>
    /// <param name="message">New message, or null to keep the original.</param>
    /// <param name="tree">New tree OID, or null to keep the original's tree.</param>
    /// <param name="updateRef">Ref to update (non-null throws).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the amended commit.</returns>
    public static async Task<GitOid> AmendAsync(
        Commit commitToAmend,
        GitSignature? author = null,
        GitSignature? committer = null,
        string? messageEncoding = null,
        string? message = null,
        GitOid? tree = null,
        string? updateRef = null,
        CancellationToken cancellationToken = default)
        => await AmendAsync(commitToAmend, author, committer, messageEncoding, message, messageBytes: null, tree, updateRef, cancellationToken).ConfigureAwait(false);

    /// <summary> Amends a commit with a byte-faithful message. byte-parity surface — C's <c>git_commit_amend</c> writes the message bytes verbatim; the string
    /// overload re-encodes a lossy decode, which corrupts non-UTF-8 messages on amend. </summary>
    public static async Task<GitOid> AmendAsync(
        Commit commitToAmend,
        GitSignature? author,
        GitSignature? committer,
        string? messageEncoding,
        string? message,
        ReadOnlyMemory<byte>? messageBytes,
        GitOid? tree,
        string? updateRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitToAmend);

        GitRepository repo = commitToAmend.Owner
            ?? throw new GitException(
                GitErrorCode.Invalid,
                "commit to amend has no owning repository",
                GitErrorCategory.Object);

        // Fall back to original values. When the message is preserved, the byte tier keeps the original bytes (the string fallback would re-encode a lossy
        // decode).
        GitSignature finalAuthor = author ?? commitToAmend.Author;
        GitSignature finalCommitter = committer ?? commitToAmend.Committer;
        string? finalEncoding = messageEncoding ?? commitToAmend.Encoding;
        string finalMessage = message ?? commitToAmend.RawMessage;
        // Explicit (ReadOnlyMemory<byte>?)null cast — the ternary would
        // otherwise coerce null to default(ReadOnlyMemory<byte>) (empty,
        // non-null) via the byte[]→ReadOnlyMemory<byte> implicit conversion
        // (avoids coercing null to an empty non-null memory).
        ReadOnlyMemory<byte>? finalMessageBytes = messageBytes ?? (message is null ? commitToAmend.RawMessageBytes : (ReadOnlyMemory<byte>?)null);
        GitOid treeId = tree ?? commitToAmend.Tree;

        // C (commit.c:370-379, git_commit_amend): with update_ref, the commit
        // being amended must BE the tip ("commit to amend is not the tip of
        // the given branch", GIT_EMODIFIED, GIT_ERROR_REFERENCE). The new
        // commit is then created with the ORIGINAL parents WITHOUT the
        // first-parent tip check (the C passes current_id=NULL to
        // validate_tree_and_parents at commit.c:976).
        if (updateRef is not null)
        {
            GitReference? current = await repo.Refs.ResolveAsync(updateRef, cancellationToken).ConfigureAwait(false);
            if (current is GitDirectReference direct && !direct.Target.Equals(commitToAmend.Id))
            {
                throw new GitException(
                    GitErrorCode.Modified,
                    "commit to amend is not the tip of the given branch",
                    GitErrorCategory.Reference);
            }
        }

        // Create the amended commit (same parents as original).
        return await CreateAsync(repo, new CommitCreateOptions
        {
            Tree = treeId,
            Parents = commitToAmend.Parents,
            Author = finalAuthor,
            Committer = finalCommitter,
            MessageEncoding = finalEncoding,
            Message = finalMessage,
            MessageBytes = finalMessageBytes,
            UpdateRef = updateRef,
        }, validateTip: false, cancellationToken).ConfigureAwait(false);
    }

    private static GitSignature commitToAmender(Commit c) => c.Committer;

    /// <summary>
    /// Builds a commit buffer (header + message) without writing to the ODB.
    /// Matches <c>git_commit_create_buffer</c> (commit.c:939-986) +
    /// <c>git_commit__create_buffer_internal</c> (commit.c:45-85).
    /// </summary>
    /// <returns>The raw commit body bytes (what would be written to the ODB).</returns>
    public static byte[] CreateBuffer(
        GitSignature author,
        GitSignature committer,
        string? messageEncoding,
        string message,
        GitOid tree,
        IReadOnlyList<GitOid> parents)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(committer);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(parents);

        return CreateBufferInternal(author, committer, messageEncoding, message, tree, parents);
    }

    /// <summary>
    /// Creates a commit with a detached signature (e.g. GPG). Matches
    /// <c>git_commit_create_with_signature</c> (commit.c:1022-1087).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="commitContent">The raw commit content (header + blank line + message).</param>
    /// <param name="signature">The signature to embed (e.g. GPG signature block).</param>
    /// <param name="signatureField">The header field name for the signature (default <c>"gpgsig"</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the created commit.</returns>
    internal static async Task<GitOid> CreateWithSignatureAsync(
        GitRepository repo,
        string commitContent,
        string? signature,
        string? signatureField,
        CancellationToken cancellationToken)
        => await CreateWithSignatureAsync(repo, EncodingAlias.UTF8.GetBytes(commitContent), signature, signatureField, cancellationToken).ConfigureAwait(false);

    /// <summary> Creates a commit with a detached signature from a raw byte buffer. byte-parity surface — C's <c>git_commit_create_with_signature</c> parses
    /// and re-emits the raw content bytes (commit.c:1022-1087); the string overload re-encodes a lossy decode, which corrupts non-UTF-8 content. </summary>
    internal static async Task<GitOid> CreateWithSignatureAsync(
        GitRepository repo,
        ReadOnlyMemory<byte> commitContent,
        string? signature,
        string? signatureField,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // C (commit.c:1022-1052, git_commit_create_with_signature): the content is parsed first (commit_parse — rejects malformed commits) and the tree +
        // parents are validated to exist before writing (validate_tree_and_parents with validate=true). The strictness gate mirrors CreateAsync.
        Commit parsed = Parse(
            owner: null,
            default,
            commitContent,
            repo.ObjectFormat);

        if (repo.Context.Settings.StrictObjectCreation
            && !await repo.Objects.IsValidAsync(parsed.Tree, GitObjectType.Tree, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"failed to create commit: tree {parsed.Tree} does not exist",
                GitErrorCategory.Object);
        }

        foreach (GitOid parentId in parsed.Parents)
        {
            if (repo.Context.Settings.StrictObjectCreation
                && !await repo.Objects.IsValidAsync(parentId, GitObjectType.Commit, cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"failed to create commit: parent {parentId} does not exist",
                    GitErrorCategory.Object);
            }
        }

        // Find the header/body separator ("\n\n").
        ReadOnlySpan<byte> content = commitContent.Span;
        int sepIdx = content.IndexOf("\n\n"u8);
        if (sepIdx < 0)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "malformed commit contents: no header/body separator",
                GitErrorCategory.Object);
        }

        // Build the new commit: header + optional signature + body.
        using var buffer = new PooledByteBufferWriter(commitContent.Length + (signature?.Length ?? 0));

        // Header up to and including the first '\n' of the separator.
        buffer.Write(content[..(sepIdx + 1)]);

        if (signature is not null)
        {
            string field = signatureField ?? "gpgsig";
            FormatHeaderFieldBytes(buffer, field, signature);
        }

        // Rest (starting from the second '\n' of the separator).
        buffer.Write(content[(sepIdx + 1)..]);

        return await repo.Objects.WriteAsync(GitObjectType.Commit, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-domain variant of <see cref="FormatHeaderField"/>: appends a header field with continuation-line formatting (RFC 822 folding) into a byte
    /// writer. </summary>
    private static void FormatHeaderFieldBytes(PooledByteBufferWriter buffer, string field, string content)
    {
        buffer.Write(EncodingAlias.UTF8.GetBytes(field));
        buffer.WriteByte((byte)' ');

        ReadOnlySpan<byte> remaining = EncodingAlias.UTF8.GetBytes(content);
        while (true)
        {
            int lf = remaining.IndexOf((byte)'\n');
            if (lf < 0)
            {
                break;
            }

            buffer.Write(remaining[..lf]);
            buffer.Write("\n "u8);
            remaining = remaining[(lf + 1)..];
        }

        buffer.Write(remaining);
        buffer.WriteByte((byte)'\n');
    }

    /// <summary>
    /// Appends a header field with continuation-line formatting: newlines in
    /// the value are prefixed with a space (RFC 822 folding). Matches
    /// <c>format_header_field</c> (commit.c:991-1012).
    /// </summary>
    private static void FormatHeaderField(ref ValueStringBuilder sb, string field, string content)
    {
        sb.Append(field);
        sb.Append(' ');

        ReadOnlySpan<char> remaining = content.AsSpan();
        while (true)
        {
            int lf = remaining.IndexOf('\n');
            if (lf < 0)
            {
                break;
            }

            sb.Append(remaining[..lf]);
            sb.Append("\n ");
            remaining = remaining[(lf + 1)..];
        }

        sb.Append(remaining);
        sb.Append('\n');
    }

    /// <summary>
    /// Builds the commit body buffer. Matches
    /// <c>git_commit__create_buffer_internal</c> (commit.c:45-85).
    /// Format:
    /// <code>
    /// tree &lt;hex-oid&gt;\n
    /// parent &lt;hex-oid&gt;\n   (one per parent)
    /// author &lt;sig&gt;\n
    /// committer &lt;sig&gt;\n
    /// [encoding &lt;enc&gt;\n]
    /// \n
    /// &lt;message&gt;
    /// </code>
    /// </summary>
    private static byte[] CreateBufferInternal(
        GitSignature author,
        GitSignature committer,
        string? messageEncoding,
        string message,
        GitOid tree,
        IReadOnlyList<GitOid> parents)
    {
        using var sb = new ValueStringBuilder(256 + message.Length);

        // tree <oid>\n
        sb.Append("tree ");
        sb.AppendSpanFormattable(tree, provider: CultureInfo.InvariantCulture);
        sb.Append('\n');

        // parent <oid>\n (one per parent)
        foreach (GitOid parent in parents)
        {
            sb.Append("parent ");
            sb.AppendSpanFormattable(parent, provider: CultureInfo.InvariantCulture);
            sb.Append('\n');
        }

        // author <sig>\n
        sb.Append("author ");
        sb.AppendSpanFormattable(author, provider: CultureInfo.InvariantCulture);
        sb.Append('\n');

        // committer <sig>\n
        sb.Append("committer ");
        sb.AppendSpanFormattable(committer, provider: CultureInfo.InvariantCulture);
        sb.Append('\n');

        // optional encoding <enc>\n
        if (messageEncoding is not null)
        {
            sb.Append("encoding ");
            sb.Append(messageEncoding);
            sb.Append('\n');
        }

        // blank separator
        sb.Append('\n');

        // message (raw, no trailing newline added)
        sb.Append(message);

        return Utf8Helper.EncodeToByteArray(sb.AsSpan());
    }

    /// <summary> Byte-message overload of <see cref="CreateBufferInternal(GitSignature, GitSignature, string?, string, GitOid, IReadOnlyList{GitOid})"/>.
    /// byte-parity surface — C writes the message bytes verbatim (commit.c:73); the string overload re-encodes a lossy decode, which corrupts non-UTF-8
    /// messages. </summary>
    private static byte[] CreateBufferInternal(
        GitSignature author,
        GitSignature committer,
        string? messageEncoding,
        ReadOnlyMemory<byte> message,
        GitOid tree,
        IReadOnlyList<GitOid> parents)
    {
        using var buffer = new PooledByteBufferWriter(256 + message.Length);

        // tree <oid>\n
        buffer.Write("tree "u8);
        WriteOidBytes(buffer, tree);
        buffer.WriteByte((byte)'\n');

        // parent <oid>\n (one per parent)
        foreach (GitOid parent in parents)
        {
            buffer.Write("parent "u8);
            WriteOidBytes(buffer, parent);
            buffer.WriteByte((byte)'\n');
        }

        // author <sig>\n
        buffer.Write("author "u8);
        WriteSignatureBytes(buffer, author);
        buffer.WriteByte((byte)'\n');

        // committer <sig>\n
        buffer.Write("committer "u8);
        WriteSignatureBytes(buffer, committer);
        buffer.WriteByte((byte)'\n');

        // optional encoding <enc>\n
        if (messageEncoding is not null)
        {
            buffer.Write("encoding "u8);
            buffer.Write(EncodingAlias.UTF8.GetBytes(messageEncoding));
            buffer.WriteByte((byte)'\n');
        }

        // blank separator
        buffer.WriteByte((byte)'\n');

        // message (raw, no trailing newline added)
        buffer.Write(message.Span);

        return buffer.WrittenMemory.ToArray();
    }

    /// <summary> Writes an OID's hex bytes. </summary>
    private static void WriteOidBytes(PooledByteBufferWriter buffer, GitOid oid)
    {
        Span<byte> hex = buffer.GetSpan(oid.HexSize);
        int written = oid.FormatHex(hex);
        buffer.Advance(written);
    }

    /// <summary> Writes a signature line's bytes. The raw name/email bytes are spliced verbatim (C's
    /// <c>git_signature__writebuf</c>, signature.c:425-441) — a char-format + UTF-8 re-encode would corrupt non-UTF-8 name/email bytes.
    /// </summary>
    private static void WriteSignatureBytes(PooledByteBufferWriter buffer, GitSignature signature)
    {
        signature.WriteTo(buffer);
    }

    /// <summary>
    /// Parses a raw commit body (no header) into a <see cref="Commit"/>.
    /// Matches <c>commit_parse</c> (commit.c:394-507).
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the commit body is malformed.
    /// </exception>
    internal static Commit Parse(GitRepository? owner, GitOid id, ReadOnlyMemory<byte> raw, GitHashAlgorithmKind algorithm)
    {
        ReadOnlySpan<byte> span = raw.Span;
        var parser = new GitObjectParser(span);

        // 1. tree <oid>\n
        GitOid treeId = AdvanceOidHeader(ref parser, "tree ", algorithm);

        // 2. parent <oid>\n (zero or more)
        var parents = new List<GitOid>(capacity: 1);
        while (parser.LineStartsWith("parent "u8))
        {
            GitOid parentId = AdvanceOidHeader(ref parser, "parent ", algorithm);
            parents.Add(parentId);
        }

        // 3. author <sig>\n
        if (!TryAdvanceSignature(ref parser, "author ", out GitSignature? author))
        {
            throw new GitException(GitErrorCode.Invalid, "commit has no author", GitErrorCategory.Object);
        }

        // 4. Skip duplicate author lines (buggy tools produce them).
        while (parser.LineStartsWith("author "u8))
        {
            if (!TryAdvanceSignature(ref parser, "author ", out _))
            {
                throw new GitException(GitErrorCode.Invalid, "commit has malformed duplicate author line", GitErrorCategory.Object);
            }
        }

        // 5. committer <sig>\n
        if (!TryAdvanceSignature(ref parser, "committer ", out GitSignature? committer))
        {
            throw new GitException(GitErrorCode.Invalid, "commit has no committer", GitErrorCategory.Object);
        }

        // 6. Walk remaining header lines looking for encoding + the blank-line separator.
        string? messageEncoding = null;
        while (!parser.IsAtEnd)
        {
            // Blank line terminates the header.
            if (parser.Line.Length == 0)
            {
                parser.AdvanceLine(); // consume the blank line
                break;
            }

            if (parser.LineStartsWith("encoding "u8))
            {
                parser.AdvanceExpected("encoding ");
                messageEncoding = EncodingAlias.UTF8.GetString(parser.Line);
                parser.AdvanceLine();
            }
            else
            {
                // Unknown header line — skip.
                parser.AdvanceLine();
            }
        }

        // Compute raw_header and raw_message by re-scanning the original span. Header ends at the first '\n\n' (or end of buffer if degenerate). The separator
        // index is retained so the byte getters (RawMessageBytes/RawHeaderBytes) slice the original buffer zero-copy.
        int sepIdx = FindSeparator(span);
        string rawHeader;
        string rawMessage;
        if (sepIdx < 0)
        {
            rawHeader = EncodingAlias.UTF8.GetString(span);
            rawMessage = string.Empty;
        }
        else
        {
            // sepIdx points at the first '\n' of the '\n\n' separator.
            // C's header walk (commit.c:488-493) stops with the cursor ON the
            // blank line, so raw_header runs up to AND INCLUDING the last
            // header line's '\n' but EXCLUDES the blank line:
            // span[..sepIdx+1]. raw_message starts after the second '\n'.
            rawHeader = EncodingAlias.UTF8.GetString(span[..(sepIdx + 1)]);
            rawMessage = sepIdx + 2 < span.Length
                ? EncodingAlias.UTF8.GetString(span[(sepIdx + 2)..])
                : string.Empty;
        }

        return new Commit(
            owner,
            id,
            raw.Length,
            raw,
            treeId,
            ApplyGrafts(owner, id, [.. parents]),
            author,
            committer,
            messageEncoding,
            rawHeader,
            rawMessage,
            sepIdx);
    }

    /// <summary>
    /// Replaces a commit's parent list from the repo's grafts/shallow files.
    /// Matches the graft step of <c>git_commit__parse_ext</c> (commit.c:554-567):
    /// the graft found in <c>repo-&gt;grafts</c> or <c>repo-&gt;shallow_grafts</c>
    /// replaces the parsed parents (a graft with no parents makes the commit a
    /// root). The commit-graph fast path skips grafts in both C and C#.
    /// </summary>
    private static GitOid[] ApplyGrafts(GitRepository? owner, GitOid id, GitOid[] parents)
    {
        if (owner is null)
        {
            return parents;
        }

        if (owner.Grafts?.Get(id) is { } graft)
        {
            return [.. graft.Parents];
        }

        if (owner.ShallowGrafts?.Get(id) is { } shallowGraft)
        {
            return [.. shallowGraft.Parents];
        }

        return parents;
    }

    /// <summary>
    /// Quick-parses a commit, extracting only the committer timestamp and
    /// parent OIDs. Matches libgit2's <c>commit_quick_parse</c> (called with
    /// <c>GIT_COMMIT_PARSE_QUICK</c>).
    /// </summary>
    /// <remarks>
    /// Skips the tree line, author signature, message, and encoding. Only reads
    /// committer time and parent OIDs — critical for revwalk performance on
    /// large repos where full <see cref="Parse"/> would be too slow.
    /// </remarks>
    /// <param name="commit">A parsed commit object (uses its Raw bytes).</param>
    /// <param name="algorithm">The OID algorithm.</param>
    /// <returns>The committer timestamp (Unix seconds) and parent OIDs.</returns>
    internal static (long Time, GitOid[] ParentOids) ParseQuick(Commit commit, GitHashAlgorithmKind algorithm)
    {
        ReadOnlySpan<byte> span = commit.Raw.Span;
        var parser = new GitObjectParser(span);

        // 1. Skip "tree <oid>\n"
        _ = AdvanceOidHeader(ref parser, "tree ", algorithm);

        // 2. Collect "parent <oid>\n" lines
        var parentOids = new List<GitOid>(capacity: 1);
        while (parser.LineStartsWith("parent "u8))
        {
            GitOid parentId = AdvanceOidHeader(ref parser, "parent ", algorithm);
            parentOids.Add(parentId);
        }

        // 3. Skip "author <sig>\n" (and any duplicate author lines)
        while (parser.LineStartsWith("author "u8))
        {
            parser.AdvanceLine();
        }

        // 4. Parse "committer <sig>\n" — extract just the timestamp. C's
        // quick parse ALWAYS parses the committer signature (commit.c:457-465):
        // a missing or malformed committer FAILS the quick parse rather than
        // yielding time=0 silently. Faithful port of git_signature__parse
        // (signature.c:322-380) as used by the QUICK path: the LAST '<' and
        // '>' win (memrchr), and the timestamp is only required when there is
        // content beyond "> " (email_end + 2 < end).
        long time;
        if (parser.LineStartsWith("committer "u8))
        {
            ReadOnlySpan<byte> line = parser.Line;
            // Format: committer Name <email> timestamp +offset
            int gtIdx = line.LastIndexOf((byte)'>');
            int ltIdx = line.LastIndexOf((byte)'<');
            if (gtIdx < 0 || ltIdx < 0 || gtIdx <= ltIdx)
            {
                // C (signature.c:346-349): GIT_EINVALID, GIT_ERROR_INVALID.
                throw new GitException(GitErrorCode.Invalid, "failed to parse signature - malformed e-mail", GitErrorCategory.Invalid);
            }

            // C (signature.c:352-353): only when there is content beyond
            // "> " — "Name <email>" parses with time 0.
            if (gtIdx + 2 < line.Length)
            {
                // C's git__strntol64 skips leading whitespace (util.c:43-47),
                // then parses the digits.
                int tsStart = gtIdx + 2;
                while (tsStart < line.Length && line[tsStart] is (byte)' ' or (byte)'\t')
                {
                    tsStart++;
                }

                // C's
                // git__strntol64 accepts a leading sign (util.c:58-63), so a
                // negative committer timestamp (e.g. `--date='@-1'`) parses in
                // the quick path, where requiring an initial digit would throw
                // 'invalid Unix timestamp' while the full parse (ParseStrntol64)
                // accepts the same line.
                int afterGt = tsStart;
                if (afterGt < line.Length && line[afterGt] is (byte)'-' or (byte)'+')
                {
                    afterGt++;
                }

                while (afterGt < line.Length && line[afterGt] is >= (byte)'0' and <= (byte)'9')
                {
                    afterGt++;
                }

                if (afterGt <= tsStart || !long.TryParse(line[tsStart..afterGt], CultureInfo.InvariantCulture, out time))
                {
                    // C (signature.c:356-361): GIT_EINVALID, GIT_ERROR_INVALID.
                    throw new GitException(GitErrorCode.Invalid, "failed to parse signature - invalid Unix timestamp", GitErrorCategory.Invalid);
                }
            }
            else
            {
                time = 0;
            }

            parser.AdvanceLine();
        }
        else
        {
            // C (signature.c:335-338): the committer prefix is required.
            throw new GitException(GitErrorCode.Invalid, "failed to parse signature - expected prefix doesn't match actual", GitErrorCategory.Invalid);
        }

        if (parentOids.Count > ushort.MaxValue)
        {
            // C (commit_list.c:149-152): rc=-1, GIT_ERROR_INVALID class,
            // "commit has more than 2^16 parents".
            throw new GitException(GitErrorCode.Error, "commit has more than 2^16 parents", GitErrorCategory.Invalid);
        }

        return (time, ApplyGrafts(commit.Owner, commit.Id, [.. parentOids]));
    }

    // Returns the index in `span` of the first '\n' of the header-body '\n\n' separator,
    // or -1 if no separator is found.
    private static int FindSeparator(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == '\n' && span[i + 1] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static GitOid AdvanceOidHeader(ref GitObjectParser parser, string header, GitHashAlgorithmKind algorithm)
    {
        if (!parser.AdvanceExpected(header))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"expected '{header}' header in commit",
                GitErrorCategory.Object);
        }

        if (parser.AdvanceOid(algorithm) is not { } oid)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"invalid OID in '{header}' header",
                GitErrorCategory.Object);
        }

        if (!parser.AdvanceNewline())
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"missing newline after '{header}' header",
                GitErrorCategory.Object);
        }

        return oid;
    }

    private static bool TryAdvanceSignature(ref GitObjectParser parser, string header, [NotNullWhen(true)] out GitSignature? sig)
    {
        // Read the full line bytes after the header, then parse as a signature.
        if (!parser.AdvanceExpected(header))
        {
            sig = null;
            return false;
        }

        // C's
        // git_signature__parse requires the '\n' ender — a signature line at
        // end-of-buffer without a trailing newline is rejected ("no newline
        // given", signature.c:330-332) rather than parsing the
        // newline-truncated line.
        if (!parser.IsLineTerminated)
        {
            sig = null;
            return false;
        }

        // Take the rest of the line and parse as a signature over the raw bytes — C's git_signature__parse operates on char* bytes (signature.c:322-398), so
        // non-UTF-8 name/email bytes are preserved verbatim. The line is already truncated at the newline boundary, so no ender is needed.
        ReadOnlySpan<byte> lineBytes = parser.Line;
        if (!GitSignature.TryParse(lineBytes, out sig, out _))
        {
            return false;
        }

        parser.AdvanceLine();
        return true;
    }

    // Exact port of git_commit_summary (commit.c:601-655): copies whitespace
    // runs that contain no newline verbatim (only runs containing a '\n'
    // collapse to a single space), and stops at a newline followed by a
    // whitespace-only line.
    private static string ExtractSummary(string message)
    {
        using var sb = new ValueStringBuilder(message.Length);
        int spaceStart = -1;
        bool spaceContainsNewline = false;

        for (int i = 0; i < message.Length; i++)
        {
            char c = message[i];

            // Stop processing at the end of the first paragraph.
            if (c == '\n')
            {
                if (i + 1 >= message.Length)
                {
                    break;
                }

                if (message[i + 1] == '\n')
                {
                    break;
                }

                // Stop if the next line contains only whitespace.
                int next = i + 1;
                while (next < message.Length && IsSpaceNonLf(message[next]))
                {
                    next++;
                }

                if (next >= message.Length || message[next] == '\n')
                {
                    break;
                }
            }

            // Record the beginning of contiguous whitespace runs.
            if (IsGitSpace(c))
            {
                if (spaceStart < 0)
                {
                    spaceStart = i;
                    spaceContainsNewline = false;
                }

                spaceContainsNewline |= c == '\n';
            }
            else
            {
                // Process any recorded whitespace.
                if (spaceStart >= 0)
                {
                    if (spaceContainsNewline)
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        sb.Append(message.AsSpan(spaceStart, i - spaceStart));
                    }

                    spaceStart = -1;
                }

                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>ASCII <c>git__isspace</c> (ctype_compat.h:43-47).</summary>
    private static bool IsGitSpace(char c)
        => c is ' ' or '\t' or '\n' or '\f' or '\r' or '\v';

    /// <summary>ASCII <c>git__isspace_nonlf</c> — whitespace except newline.</summary>
    private static bool IsSpaceNonLf(char c)
        => c is ' ' or '\t' or '\f' or '\r' or '\v';

    /// <summary>ASCII <c>git__isspace</c> over a byte (ctype_compat.h:43-47).</summary>
    internal static bool IsGitSpace(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)'\v';

    /// <summary>ASCII <c>git__isspace_nonlf</c> over a byte — whitespace except newline.</summary>
    internal static bool IsSpaceNonLf(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\f' or (byte)'\r' or (byte)'\v';

    // Exact byte-domain port of git_commit_summary (commit.c:601-655) — C folds the raw message bytes with the ASCII isspace class (one
    // byte = one character); every byte that is not ASCII whitespace, including invalid-UTF-8 bytes, is copied verbatim.
    internal static byte[] ExtractSummaryBytes(ReadOnlySpan<byte> message)
    {
        using var buf = new PooledByteBufferWriter();
        int spaceStart = -1;
        bool spaceContainsNewline = false;

        for (int i = 0; i < message.Length; i++)
        {
            byte b = message[i];

            // Stop processing at the end of the first paragraph.
            if (b == (byte)'\n')
            {
                if (i + 1 >= message.Length)
                {
                    break;
                }

                if (message[i + 1] == (byte)'\n')
                {
                    break;
                }

                // Stop if the next line contains only whitespace.
                int next = i + 1;
                while (next < message.Length && IsSpaceNonLf(message[next]))
                {
                    next++;
                }

                if (next >= message.Length || message[next] == (byte)'\n')
                {
                    break;
                }
            }

            // Record the beginning of contiguous whitespace runs.
            if (IsGitSpace(b))
            {
                if (spaceStart < 0)
                {
                    spaceStart = i;
                    spaceContainsNewline = false;
                }

                spaceContainsNewline |= b == (byte)'\n';
            }
            else
            {
                // Process any recorded whitespace.
                if (spaceStart >= 0)
                {
                    if (spaceContainsNewline)
                    {
                        buf.Write((byte)' ');
                    }
                    else
                    {
                        buf.Write(message[spaceStart..i]);
                    }

                    spaceStart = -1;
                }

                buf.Write(b);
            }
        }

        return buf.WrittenSpan.ToArray();
    }

    // Port of git_commit_body (commit.c:657-682): text after the first blank
    // line, trimmed with git__isspace (ASCII-only — string.Trim() would also
    // strip Unicode whitespace like NBSP, which C keeps).
    private static string ExtractBody(string message)
    {
        // Find the first blank line (i.e. the position right after "\n\n" or at a lone '\n').
        int i = 0;
        while (i < message.Length)
        {
            int nl = message.IndexOf('\n', i, StringComparison.Ordinal);
            if (nl < 0)
            {
                break;
            }

            if (nl + 1 < message.Length && message[nl + 1] == '\n')
            {
                // Found \n\n — body starts after the second \n.
                string body = message[(nl + 2)..];
                return TrimAsciiWhitespace(body);
            }

            // Lone empty line at start of message.
            if (nl == i)
            {
                string body = message[(nl + 1)..];
                return TrimAsciiWhitespace(body);
            }

            i = nl + 1;
        }

        return string.Empty;
    }

    /// <summary>
    /// Trims leading and trailing <c>git__isspace</c> characters
    /// (commit.c:671-676) — ASCII-only, unlike <see cref="string.Trim()"/>.
    /// </summary>
    private static string TrimAsciiWhitespace(string s)
    {
        int start = 0;
        while (start < s.Length && IsGitSpace(s[start]))
        {
            start++;
        }

        int end = s.Length - 1;
        while (end >= start && IsGitSpace(s[end]))
        {
            end--;
        }

        return s[start..(end + 1)];
    }
}

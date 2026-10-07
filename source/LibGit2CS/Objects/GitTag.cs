// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

using EncodingAlias = System.Text.Encoding;

namespace LibGit2CS.Objects;

/// <summary>
/// An annotated tag object. Managed port of libgit2's <c>src/libgit2/tag.c</c>.
/// </summary>
/// <remarks>
/// Parsed from the raw tag body via <see cref="Parse"/>. The body format is
/// line-oriented: <c>object &lt;oid&gt;\n</c>, <c>type &lt;type&gt;\n</c>,
/// <c>tag &lt;name&gt;\n</c>, optional <c>tagger &lt;sig&gt;\n</c>, blank line,
/// message. Matches <c>tag_parse</c> (tag.c:68-166).
/// </remarks>
public sealed class GitTag : GitObject
{

    private readonly GitOid _target;
    private readonly GitObjectType _targetType;
    private readonly string _name;
    private readonly ReadOnlyMemory<byte> _nameBytes;
    private readonly GitSignature? _tagger;
    private readonly string? _message;
    private readonly ReadOnlyMemory<byte>? _messageBytes;

    private GitTag(
        GitRepository? owner,
        GitOid id,
        long size,
        ReadOnlyMemory<byte> raw,
        GitOid target,
        GitObjectType targetType,
        string name,
        ReadOnlyMemory<byte> nameBytes,
        GitSignature? tagger,
        string? message,
        ReadOnlyMemory<byte>? messageBytes)
        : base(owner, id, GitObjectType.Tag, size, raw)
    {
        _target = target;
        _targetType = targetType;
        _name = name;
        _nameBytes = nameBytes;
        _tagger = tagger;
        _message = message;
        _messageBytes = messageBytes;
    }

    /// <summary>
    /// The OID of the tag's target object. Matches <c>git_tag_target_id</c>.
    /// Use <c>repo.ObjectLookupAsync(Target)</c> to load the target.
    /// </summary>
    public GitOid Target => _target;

    /// <summary>
    /// The type of the target object (Commit/Tree/Blob/Tag). Matches
    /// <c>git_tag_target_type</c>.
    /// </summary>
    public GitObjectType TargetType => _targetType;

    /// <summary>
    /// The tag name (e.g. <c>"v1.0"</c>). Matches <c>git_tag_name</c>.
    /// </summary>
    public string Name => _name;

    /// <summary> The tag name bytes. byte-parity surface — C's <c>git_tag_name</c> returns the raw name bytes (tag.c:45); the string <see cref="Name"/> is the
    /// UTF-8 display decode. </summary>
    public ReadOnlyMemory<byte> NameBytes => _nameBytes;

    /// <summary>
    /// The tagger signature (who created the tag), or null for taggerless tags.
    /// Matches <c>git_tag_tagger</c>.
    /// </summary>
    public GitSignature? Tagger => _tagger;

    /// <summary>
    /// The tag message, or null for messageless tags. Matches <c>git_tag_message</c>.
    /// </summary>
    public string? Message => _message;

    /// <summary> The tag message bytes, or null for messageless tags. byte-parity surface — C's <c>git_tag_message</c> returns the raw message bytes
    /// (tag.c:56-59); the string <see cref="Message"/> is the UTF-8 display decode. </summary>
    public ReadOnlyMemory<byte>? MessageBytes => _messageBytes;

    /// <summary>
    /// Peels this tag to the given target type by walking the tag→target
    /// chain (and a commit's tree beyond it). Matches
    /// <c>git_object_peel</c>/<c>git_tag_peel</c> (tag.c:566-569,
    /// object.c:422). The shared implementation lives in
    /// <see cref="GitObject.PeelCoreAsync{T}"/> so that <c>^{tree}</c> /
    /// <c>^{blob}</c> / <c>tag:path</c> peel tag→commit→tree like C
    /// (object.c:447-455), instead of stopping at the first non-tag.
    /// </summary>
    /// <typeparam name="T">The target type (typically <see cref="Commit"/>).</typeparam>
    /// <returns>The peeled object.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Peel"/> if the chain ends at a different type or exceeds the depth cap (50).
    /// </exception>
    public override async Task<T> PeelAsync<T>(CancellationToken cancellationToken = default)
        => await GitObject.PeelCoreAsync<T>(this, cancellationToken).ConfigureAwait(false);

    // ==============================
    // Write side
    // ==============================

    /// <summary>
    /// Creates an annotated tag object and writes it to the ODB. Does NOT
    /// create the <c>refs/tags/&lt;name&gt;</c> reference. Matches
    /// <c>write_tag_annotation</c> (tag.c:224-259) +
    /// <c>git_tag_annotation_create</c> (tag.c:342-358).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="tagName">The tag name (e.g. <c>"v1.0"</c>).</param>
    /// <param name="target">The target object (commit, tree, blob, or tag).</param>
    /// <param name="tagger">The tagger signature.</param>
    /// <param name="message">The tag message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the created tag annotation object.</returns>
    internal static async Task<GitOid> CreateAnnotationAsync(GitRepository repo, string tagName, GitObject target, GitSignature tagger, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(tagName);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tagger);
        ArgumentNullException.ThrowIfNull(message);

        if (!TagNameIsValid(tagName))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"'{tagName}' is not a valid tag name",
                GitErrorCategory.Tag);
        }

        byte[] buf = BuildTagBuffer(tagName, target, tagger, message);
        return await repo.Objects.WriteAsync(GitObjectType.Tag, buf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-message variant of <see cref="CreateAnnotationAsync(GitRepository, string, GitObject, GitSignature, string, CancellationToken)"/>.
    /// byte-parity surface — C's <c>write_tag_annotation</c> writes the message bytes verbatim (tag.c:243). </summary>
    internal static async Task<GitOid> CreateAnnotationAsync(GitRepository repo, string tagName, GitObject target, GitSignature tagger, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(tagName);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tagger);

        if (!TagNameIsValid(tagName))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"'{tagName}' is not a valid tag name",
                GitErrorCategory.Tag);
        }

        byte[] buf = BuildTagBuffer(tagName, target, tagger, message);
        return await repo.Objects.WriteAsync(GitObjectType.Tag, buf, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Repo-facing factories (internal — public entry points live on GitRepository)
    // ==============================

    /// <summary>
    /// Creates a tag (annotated or lightweight). Writes the tag object (if
    /// annotated) to the ODB and creates the <c>refs/tags/&lt;name&gt;</c>
    /// reference. Matches <c>git_tag_create</c> (tag.c:330-340) +
    /// <c>git_tag_create_lightweight</c> (tag.c:360-368).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="tagName">The tag name.</param>
    /// <param name="target">The target object.</param>
    /// <param name="tagger">The tagger signature (null for lightweight).</param>
    /// <param name="message">The tag message (null for lightweight).</param>
    /// <param name="allowOverwrite">Allow overwriting an existing tag ref.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID the tag ref points at (tag object OID for annotated, target OID for lightweight).</returns>
    internal static async Task<GitOid> CreateAsync(GitRepository repo, string tagName, GitObject target, GitSignature? tagger, string? message, bool allowOverwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(tagName);
        ArgumentNullException.ThrowIfNull(target);

        if (!TagNameIsValid(tagName))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"'{tagName}' is not a valid tag name",
                GitErrorCategory.Tag);
        }

        // C (tag.c:302-320): the ref existence is checked BEFORE the
        // annotation is written - an existing tag with allowOverwrite=0
        // fails with GIT_EEXISTS "tag already exists" and writes NOTHING.
        string refName = $"refs/tags/{tagName}";
        if (!allowOverwrite &&
            await repo.Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GitException(GitErrorCode.Exists, "tag already exists", GitErrorCategory.Tag);
        }

        GitOid oid;
        if (tagger is not null && message is not null)
        {
            // Annotated tag: write the tag object.
            oid = await CreateAnnotationAsync(repo, tagName, target, tagger, message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Lightweight tag: ref points directly at the target.
            oid = target.Id;
        }

        // Create the refs/tags/<name> reference.
        string logMessage = $"tag: {tagName}";
        await repo.Refs.CreateAsync(refName, oid, force: allowOverwrite, logMessage, cancellationToken).ConfigureAwait(false);

        return oid;
    }

    /// <summary>
    /// Creates a tag object from a raw buffer (pre-formatted tag content).
    /// Writes the object to the ODB and creates the <c>refs/tags/&lt;name&gt;</c>
    /// reference derived from the tag name INSIDE the buffer. Exact port of
    /// <c>git_tag_create_from_buffer</c> (tag.c:370-451): the buffer is parsed
    /// first (malformed tags fail), the target object must exist and its type
    /// must match the tag's <c>type</c> line, and an existing tag ref is
    /// GIT_EEXISTS before anything is written.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="buffer">The raw tag object content (header + message).</param>
    /// <param name="allowOverwrite">Allow overwriting an existing tag ref.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the written tag object.</returns>
    internal static async Task<GitOid> CreateFromBufferAsync(GitRepository repo, string buffer, bool allowOverwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(buffer);

        return await CreateFromBufferAsync(repo, EncodingAlias.UTF8.GetBytes(buffer), allowOverwrite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Creates a tag object from a raw byte buffer. byte-parity surface — C's <c>git_tag_create_from_buffer</c> writes the raw buffer bytes verbatim
    /// (tag.c:417-437); the string overload re-encodes a lossy decode, which corrupts non-UTF-8 tag content. </summary>
    internal static async Task<GitOid> CreateFromBufferAsync(GitRepository repo, ReadOnlyMemory<byte> bytes, bool allowOverwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // C (tag.c:384-388): validate the buffer with tag_parse — a malformed
        // tag fails with -1 (GIT_ERROR).
        GitTag parsed;
        try
        {
            parsed = Parse(repo, default, bytes, repo.ObjectFormat);
        }
        catch (GitException ex)
        {
            throw new GitException(GitErrorCode.Error, ex.Message, GitErrorCategory.Tag);
        }

        // C (tag.c:390-400): the target must exist and its type must match the
        // tag's type line — "the type for the given target is invalid".
        GitObject? targetObj = await repo.Objects.LookupAsync(parsed.Target, cancellationToken).ConfigureAwait(false);
        if (targetObj is null || targetObj.Type != parsed.TargetType)
        {
            targetObj?.Dispose();
            throw new GitException(
                GitErrorCode.Error,
                "the type for the given target is invalid",
                GitErrorCategory.Tag);
        }

        targetObj.Dispose();

        // C (tag.c:402-415): the ref name is DERIVED from the tag name inside
        // the buffer; an existing tag ref fails with GIT_EEXISTS before the
        // object is written.
        string refName = $"refs/tags/{parsed.Name}";
        if (!allowOverwrite && await repo.Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GitException(GitErrorCode.Exists, "tag already exists", GitErrorCategory.Tag);
        }

        // C (tag.c:417-437): write the raw buffer bytes.
        GitOid oid = await repo.Objects.WriteAsync(GitObjectType.Tag, bytes, cancellationToken).ConfigureAwait(false);
        await repo.Refs.CreateAsync(refName, oid, force: allowOverwrite, $"tag: {parsed.Name}", cancellationToken).ConfigureAwait(false);

        return oid;
    }

    /// <summary>
    /// Deletes a tag (removes the <c>refs/tags/&lt;name&gt;</c> reference).
    /// Matches <c>git_tag_delete</c> (tag.c:453-471).
    /// </summary>
    internal static async Task DeleteAsync(GitRepository repo, string tagName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(tagName);

        string refName = $"refs/tags/{tagName}";
        if (await repo.Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"tag '{tagName}' not found",
                GitErrorCategory.Tag);
        }

        await repo.Refs.DeleteAsync(refName, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // List / Enumerate / NameIsValid
    // ==============================

    /// <summary>
    /// Lists tag names, optionally filtered by a glob pattern. Matches
    /// <c>git_tag_list</c> / <c>git_tag_list_match</c> (tag.c:534-564).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="pattern">
    /// Optional glob pattern matched against the tag name WITHOUT the
    /// <c>refs/tags/</c> prefix (e.g. <c>"v1.*"</c> matches <c>v1.0</c>,
    /// <c>v1.1</c>). Pass null or empty for all tags.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of tag names (without the <c>refs/tags/</c> prefix), sorted by
    /// name (matching the refdb enumeration order).
    /// </returns>
    /// <remarks>
    /// Matches C's <c>tag_list_cb</c> (tag.c:517-532): strips the
    /// <c>refs/tags/</c> prefix, applies <c>wildmatch(pattern, name, 0)</c>.
    /// An empty pattern matches all tags (C's <c>!*filter-&gt;pattern</c> guard).
    /// </remarks>
    internal static async Task<IReadOnlyList<string>> ListAsync(GitRepository repo, string? pattern = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var result = new List<string>();
        await foreach ((string? name, GitOid _) in EnumerateAsync(repo, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(pattern) ||
                WildMatch.IsMatch(pattern, name, WildMatchFlags.None))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An async enumerable of <c>(Name, Oid)</c> tuples where <c>Name</c> is the tag
    /// name WITHOUT the <c>refs/tags/</c> prefix and <c>Oid</c> is the
    /// resolved target OID (following symbolic refs to the final direct ref,
    /// matching <c>git_reference_name_to_id</c> in C's <c>tags_cb</c>).
    /// </returns>
    /// <param name="repo">The repository used by this operation.</param>
    internal static async IAsyncEnumerable<(string Name, GitOid Oid)> EnumerateAsync(GitRepository repo, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        const string TagsPrefix = "refs/tags/";
        await foreach (RefNameKey name in repo.Refs.ListNameKeysAsync(glob: null, cancellationToken).ConfigureAwait(false))
        {
            if (!name.StartsWith("refs/tags/"u8))
            {
                continue;
            }

            // Resolve through symbolic refs to the final direct ref, matching
            // git_reference_name_to_id in tags_cb (tag.c:488).
            GitReference? resolved = await repo.Refs.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
            if (resolved is GitDirectReference direct)
            {
                yield return (Encoding.UTF8.GetString(name.Span[TagsPrefix.Length..]), direct.Target);
            }
            // Unresolved/unborn refs are skipped (C's git_reference_name_to_id
            // returns an error, and tags_cb propagates it — but for an
            // enumeration the idiomatic C# behavior is to skip the bad ref).
        }
    }

    /// <summary>
    /// Validates a tag name. Matches <c>git_tag_name_is_valid</c>
    /// (tag.c:571-592).
    /// </summary>
    /// <param name="name">The tag name (without <c>refs/tags/</c> prefix).</param>
    /// <returns>
    /// True if <paramref name="name"/> is a valid tag name: it must pass the
    /// basic check (non-empty, not starting with <c>-</c>, not <c>HEAD</c>)
    /// AND the resulting <c>refs/tags/&lt;name&gt;</c> must be a valid ref
    /// name per <see cref="LibGit2CS.Refs.GitReferences.IsNameValid(System.ReadOnlySpan{char}, LibGit2CS.Refs.GitReferenceFormatFlags)"/>.
    /// </returns>
    /// <remarks>
    /// <b>Two-stage check:</b> matches C — first the private
    /// <c>tag_name_is_valid</c> (tag.c:261-270: rejects <c>-</c> prefix +
    /// exact <c>HEAD</c>), then <c>git_reference_name_is_valid</c> on the
    /// <c>refs/tags/</c>-prepended form. The latter always uses
    /// <see cref="GitReferenceFormatFlags.AllowOneLevel"/> internally (matching
    /// C's <c>git_reference_name_is_valid</c> at refs.c:1367-1370).
    /// </remarks>
    public static bool NameIsValid(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Stage 1: tag_name_is_valid (tag.c:261-270).
        if (!TagNameIsValid(name))
        {
            return false;
        }

        // Stage 2: prepend refs/tags/ and validate as a full ref name.
        // Uses AllowOneLevel to match git_reference_name_is_valid's internal
        // flag (refs.c:1369 GIT_REFERENCE_FORMAT_ALLOW_ONELEVEL).
        return GitReferences.IsNameValid($"refs/tags/{name}", GitReferenceFormatFlags.AllowOneLevel);
    }

    /// <summary>
    /// Validates a tag name. Matches <c>tag_name_is_valid</c> (tag.c:261-270):
    /// must not start with <c>-</c> and must not be <c>"HEAD"</c>.
    /// </summary>
    private static bool TagNameIsValid(string tagName)
        => tagName.Length > 0 && tagName[0] != '-' && tagName != "HEAD";

    /// <summary>
    /// Builds the tag object buffer. Matches <c>write_tag_annotation</c>
    /// (tag.c:224-259). Format:
    /// <code>
    /// object &lt;hex-oid&gt;\n
    /// type &lt;type-string&gt;\n
    /// tag &lt;name&gt;\n
    /// tagger &lt;sig&gt;\n
    /// \n
    /// &lt;message&gt;
    /// </code>
    /// </summary>
    private static byte[] BuildTagBuffer(string tagName, GitObject target, GitSignature tagger, string message)
    {
        using var sb = new ValueStringBuilder(128 + message.Length);

        // object <oid>\n
        sb.Append("object ");
        sb.AppendSpanFormattable(target.Id, provider: CultureInfo.InvariantCulture);
        sb.Append('\n');

        // type <type-string>\n
        sb.Append("type ");
        sb.Append(GitObjectDb.TypeToString(target.Type));
        sb.Append('\n');

        // tag <name>\n
        sb.Append("tag ");
        sb.Append(tagName);
        sb.Append('\n');

        // tagger <sig>\n
        sb.Append("tagger ");
        sb.AppendSpanFormattable(tagger, provider: CultureInfo.InvariantCulture);
        sb.Append('\n');

        // blank separator
        sb.Append('\n');

        // message
        sb.Append(message);

        return Utf8Helper.EncodeToByteArray(sb.AsSpan());
    }

    /// <summary> Byte-message overload of <see cref="BuildTagBuffer(string, GitObject, GitSignature, string)"/>. byte-parity surface — C's
    /// <c>write_tag_annotation</c> writes the message bytes verbatim (tag.c:243); the string overload re-encodes a lossy decode, which corrupts non-UTF-8
    /// messages. </summary>
    private static byte[] BuildTagBuffer(string tagName, GitObject target, GitSignature tagger, ReadOnlyMemory<byte> message)
    {
        using var buffer = new PooledByteBufferWriter(128 + message.Length);

        // object <oid>\n
        buffer.Write("object "u8);
        WriteOidBytes(buffer, target.Id);
        buffer.WriteByte((byte)'\n');

        // type <type-string>\n
        buffer.Write("type "u8);
        buffer.Write(EncodingAlias.UTF8.GetBytes(GitObjectDb.TypeToString(target.Type)));
        buffer.WriteByte((byte)'\n');

        // tag <name>\n
        buffer.Write("tag "u8);
        buffer.Write(EncodingAlias.UTF8.GetBytes(tagName));
        buffer.WriteByte((byte)'\n');

        // tagger <sig>\n
        buffer.Write("tagger "u8);
        WriteSignatureBytes(buffer, tagger);
        buffer.WriteByte((byte)'\n');

        // blank separator
        buffer.WriteByte((byte)'\n');

        // message
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
    /// Parses a raw tag body (no header) into a <see cref="GitTag"/>. Matches
    /// <c>tag_parse</c> (tag.c:68-166).
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the tag body is malformed.
    /// </exception>
    internal static GitTag Parse(GitRepository? owner, GitOid id, ReadOnlyMemory<byte> raw, GitHashAlgorithmKind algorithm)
    {
        ReadOnlySpan<byte> span = raw.Span;
        var parser = new GitObjectParser(span);

        // 1. object <oid>\n
        if (!parser.AdvanceExpected("object "))
        {
            throw new GitException(GitErrorCode.Invalid, "tag has no 'object' header", GitErrorCategory.Object);
        }

        if (parser.AdvanceOid(algorithm) is not { } target)
        {
            throw new GitException(GitErrorCode.Invalid, "tag has invalid 'object' OID", GitErrorCategory.Object);
        }

        if (!parser.AdvanceNewline())
        {
            throw new GitException(GitErrorCode.Invalid, "tag 'object' header missing newline", GitErrorCategory.Object);
        }

        // 2. type <type>\n
        if (!parser.AdvanceExpected("type "))
        {
            throw new GitException(GitErrorCode.Invalid, "tag has no 'type' header", GitErrorCategory.Object);
        }

        string typeLine = EncodingAlias.UTF8.GetString(parser.Line);
        if (!TryParseObjectType(typeLine, out GitObjectType targetType))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"tag has invalid type '{typeLine}'",
                GitErrorCategory.Object);
        }

        parser.AdvanceLine();

        // 3. tag <name>\n
        if (!parser.AdvanceExpected("tag "))
        {
            throw new GitException(GitErrorCode.Invalid, "tag has no 'tag' header", GitErrorCategory.Object);
        }

        // C (tag.c:119-131): the tag name is everything up to the next '\n'
        // with NO length check — an empty tag name ("tag \n") is accepted.
        // the '\n' is
        // REQUIRED — a tag name at end-of-buffer without a newline is
        // rejected ("failed to parse tag: object too short", tag.c:119-121).
        if (!parser.IsLineTerminated)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "failed to parse tag: object too short",
                GitErrorCategory.Tag);
        }

        // the name bytes are a zero-copy slice of the retained object buffer (C's tag.c:119-131 memcpys the raw name bytes).
        int nameStart = raw.Length - parser.Remain.Length;
        ReadOnlyMemory<byte> nameBytes = raw.Slice(nameStart, parser.Line.Length);
        string name = EncodingAlias.UTF8.GetString(parser.Line);

        parser.AdvanceLine();

        // 4. Tagger + message — exact port of tag.c:134-163.
        GitSignature? tagger = null;
        if (!parser.IsAtEnd && parser.Remain[0] != (byte)'\n')
        {
            // C: when a non-blank line follows the tag name, the tagger
            // signature parse RUNS with the "tagger " prefix — any other
            // line fails ("failed to parse signature - expected prefix
            // doesn't match actual", tag.c:135-141).
            if (!TryAdvanceSignature(ref parser, "tagger ", out GitSignature? parsed))
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    "failed to parse signature - expected prefix doesn't match actual",
                    GitErrorCategory.Object);
            }

            tagger = parsed;
        }

        // 5. Message.
        string? message = null;
        ReadOnlyMemory<byte>? messageBytes = null;
        if (!parser.IsAtEnd)
        {
            ReadOnlySpan<byte> rest = parser.Remain;
            int restStart = raw.Length - rest.Length;
            if (rest[0] != (byte)'\n')
            {
                // C (tag.c:144-153): no blank line after the tagger — the
                // message starts after the FIRST "\n\n" in the remainder;
                // without one the tag is malformed.
                int sep = rest.IndexOf("\n\n"u8);
                if (sep < 0)
                {
                    throw new GitException(GitErrorCode.Invalid, "tag contains no message", GitErrorCategory.Object);
                }

                message = EncodingAlias.UTF8.GetString(rest[(sep + 2)..]);
                messageBytes = raw.Slice(restStart + sep + 2);
            }
            else
            {
                // Blank line: the message starts after it (tag.c:154-158).
                message = EncodingAlias.UTF8.GetString(rest[1..]);
                messageBytes = raw.Slice(restStart + 1);
            }
        }

        return new GitTag(owner, id, raw.Length, raw, target, targetType, name, nameBytes, tagger, message, messageBytes);
    }

    private static bool TryParseObjectType(string s, out GitObjectType type)
    {
        type = s switch
        {
            "commit" => GitObjectType.Commit,
            "tree" => GitObjectType.Tree,
            "blob" => GitObjectType.Blob,
            "tag" => GitObjectType.Tag,
            _ => GitObjectType.Ext1,
        };

        return type != GitObjectType.Ext1;
    }

    private static bool TryAdvanceSignature(ref GitObjectParser parser, string header, [NotNullWhen(true)] out GitSignature? sig)
    {
        if (!parser.AdvanceExpected(header))
        {
            sig = null;
            return false;
        }

        // C's
        // git_signature__parse requires the '\n' ender — a tagger line at
        // end-of-buffer without a trailing newline is rejected ("no newline
        // given", signature.c:330-332).
        if (!parser.IsLineTerminated)
        {
            sig = null;
            return false;
        }

        // The remaining line (excluding the newline) is the signature buffer. Parse over the raw bytes — C's git_signature__parse operates on char* bytes
        // (signature.c:322-398), so non-UTF-8 name/email bytes are preserved verbatim.
        ReadOnlySpan<byte> lineBytes = parser.Line;
        if (!GitSignature.TryParse(lineBytes, out sig, out _))
        {
            return false;
        }

        parser.AdvanceLine();
        return true;
    }
}

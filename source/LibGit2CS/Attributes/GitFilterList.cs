// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Attributes;

/// <summary>
/// An ordered list of filters to apply to a file's content. Managed port of
/// <c>git_filter_list</c> (<c>src/libgit2/filter.c</c>).
/// </summary>
/// <remarks>
/// Built by <see cref="LibGit2CS.Attributes.GitFilterList.LoadAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.IO.GitPath, LibGit2CS.Core.GitOid?, LibGit2CS.Attributes.GitFilterMode, LibGit2CS.Attributes.GitFilterListFlags, LibGit2CS.Core.GitOid?, System.Threading.CancellationToken)"/>, which iterates all registered filters in
/// priority order, resolves their declared attributes, calls
/// <see cref="IFilter.CheckAsync"/>, and collects those that apply. The list is
/// applied in forward order (clean: CRLF before ident) or reverse order
/// (smudge: ident before CRLF) depending on the mode — matches
/// <c>stream_list_init</c> (filter.c:1059-1100).
/// </remarks>
public sealed class GitFilterList : IDisposable
{
    private readonly List<FilterEntry> _entries = [];
    private readonly GitFilterSource _source;
    private bool _disposed;

    private GitFilterList(GitFilterSource source)
    {
        _source = source;
    }

    /// <summary>The source description for this filter list.</summary>
    public GitFilterSource Source => _source;

    /// <summary>The number of filters in the list.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Loads the clean (<c>GIT_FILTER_TO_ODB</c>) filter list for
    /// <paramref name="path"/> and applies it to <paramref name="input"/>.
    /// Returns the filtered bytes, or <paramref name="input"/> unchanged when
    /// no filters apply. Matches the load+convert idiom used by both
    /// <c>git_diff__oid_for_entry</c> (diff_generate.c:687-704) and
    /// <c>diff_file_content_load_workdir_file</c> (diff_file.c:362-389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the managed equivalent of C's <c>git_filter_list_load</c>
    /// followed by <c>git_filter_list__convert_buf</c> for the clean
    /// direction. Use this whenever workdir bytes must be normalized to
    /// their ODB form — blob hashing, workdir-vs-ODB diffing, etc. — so that
    /// the OID-computation and content-rendering paths agree on line endings.
    /// </para>
    /// <para>
    /// <see cref="GitFilterListFlags.AllowUnsafe"/> is set to match both
    /// upstream call sites. The fast path (no filters apply) returns the
    /// input memory without a copy — equivalent to upstream's
    /// <c>fl == NULL</c> mmap branch.
    /// </para>
    /// </remarks>
    /// <param name="repo">The repository (for attribute loading + config).</param>
    /// <param name="path">The workdir-relative path of the file being filtered.</param>
    /// <param name="input">The raw workdir bytes to be normalized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The filtered bytes, or <paramref name="input"/> unchanged if no filters
    /// apply for <paramref name="path"/>.
    /// </returns>
    public static async ValueTask<ReadOnlyMemory<byte>> ApplyCleanAsync(
        GitRepository repo,
        GitPath path,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default)
    {
        GitFilterList? filters = await LoadAsync(
            repo, path, blobId: null, GitFilterMode.ToOdb,
            GitFilterListFlags.AllowUnsafe, attrCommitId: null, cancellationToken).ConfigureAwait(false);
        if (filters is null)
        {
            return input;
        }

        try
        {
            return await filters.ApplyToBufferAsync(input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            filters.Dispose();
        }
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. The <see cref="GitPath"/> overload is primary; this overload keeps
    /// existing <c>string</c> callers compiling. </summary>
    public static ValueTask<ReadOnlyMemory<byte>> ApplyCleanAsync(
        GitRepository repo,
        string path,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default)
        => ApplyCleanAsync(repo, GitPath.FromUtf8String(path), input, cancellationToken);

    /// <summary> Loads the filter list for a path. Matches <c>git_filter_list__load</c> (filter.c:510-598). Iterates all registered filters in priority order,
    /// resolves their attributes, calls <see cref="IFilter.CheckAsync"/>, and collects those that apply. </summary> <param name="repo">The repository (for attr
    /// file loading + config).</param> <param name="path">The relative path of the file being filtered.</param> <param name="blobId">The OID of the source
    /// blob, or null if unknown.</param> <param name="mode">The filter direction (smudge or clean).</param> <param name="flags">Filter flags.</param> <param
    /// name="attrCommitId"> The commit whose <c>.gitattributes</c> is used when <see cref="GitFilterListFlags.AttributesFromCommit"/> is set — C's
    /// <c>git_filter_options.attr_commit_id</c>, DISTINCT from the blob OID. Null/zero when unset. </param> <param name="cancellationToken">Cancellation
    /// token.</param> <returns>The filter list, or <c>null</c> if no filters apply.</returns>
    internal static async ValueTask<GitFilterList?> LoadAsync(
        GitRepository repo,
        GitPath path,
        GitOid? blobId,
        GitFilterMode mode,
        GitFilterListFlags flags,
        GitOid? attrCommitId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // GitFilterSource.Path is byte-faithful. The path is an attribute path (not an FS path); attribute lookups use the raw bytes via
        // AttrPath.Init(GitPath,...).
        var source = new GitFilterSource(
            repo,
            path,
            blobId ?? GitOid.Empty,
            FileMode: 0,
            mode,
            flags);

        var list = new GitFilterList(source);
        AttributeCache attrCache = await repo.GetAttributeCacheAsync(cancellationToken).ConfigureAwait(false);

        // C (filter.c:449-458): attr_commit_id is copied from the caller's options ONLY under ATTRIBUTES_FROM_COMMIT (otherwise it stays zero) — it is never
        // derived from the source blob's OID.
        attrCache.SourceCommitId = (flags & GitFilterListFlags.AttributesFromCommit) != 0
            ? (attrCommitId ?? GitOid.Empty)
            : GitOid.Empty;

        // Build the AttrPath for attribute lookups (byte-faithful overload).
        // C (attr.c:69-70): GIT_DIR_FLAG_FALSE for bare repos,
        // otherwise UNKNOWN and the workdir path is stat'ed to decide is_dir
        // (attr_file.c:599-613) — a dir/ DIRECTORY pattern must match a
        // directory path.
        string? workdir = repo.Workdir;
        AttrPath.DirFlag dirFlag = repo.IsBare ? AttrPath.DirFlag.False : AttrPath.DirFlag.Unknown;
        var attrPath = new AttrPath();
        attrPath.Init(path, GitPath.FromUtf8String(workdir ?? string.Empty), dirFlag);

        foreach (FilterDef fdef in repo.Context.Filters.GetAll())
        {
            if (fdef.Nattrs > 0)
            {
                // Resolve attributes for this filter.
                var attrNames = new List<string>(fdef.Nattrs);
                foreach (AttrSpec spec in fdef.Specs)
                {
                    attrNames.Add(spec.Name);
                }

                GitAttrValue[] values = await attrCache.LookupManyAsync(attrPath, attrNames, AttrFlagsFromFilterFlags(flags), cancellationToken).ConfigureAwait(false);

                // Check if required matches fail.
                if (fdef.Nmatches > 0)
                {
                    bool allMatched = CheckMatches(fdef, values);
                    if (!allMatched)
                    {
                        continue; // GIT_ENOTFOUND — skip this filter.
                    }
                }

                // Call the filter's Check method.
                GitFilterResult checkResult = await fdef.Filter.CheckAsync(source, values, cancellationToken).ConfigureAwait(false);
                if (checkResult == GitFilterResult.Passthrough)
                {
                    continue;
                }

                list._entries.Add(new FilterEntry(fdef.Filter));
            }
            else
            {
                // No attributes — always check.
                GitAttrValue[] emptyValues = Array.Empty<GitAttrValue>();
                GitFilterResult checkResult = await fdef.Filter.CheckAsync(source, emptyValues, cancellationToken).ConfigureAwait(false);
                if (checkResult == GitFilterResult.Passthrough)
                {
                    continue;
                }

                list._entries.Add(new FilterEntry(fdef.Filter));
            }
        }

        return list._entries.Count > 0 ? list : null;
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. The <see cref="GitPath"/> overload is primary; this overload keeps
    /// existing <c>string</c> callers compiling. </summary>
    internal static ValueTask<GitFilterList?> LoadAsync(
        GitRepository repo,
        string path,
        GitOid? blobId,
        GitFilterMode mode,
        GitFilterListFlags flags,
        GitOid? attrCommitId = null,
        CancellationToken cancellationToken = default)
        => LoadAsync(repo, GitPath.FromUtf8String(path), blobId, mode, flags, attrCommitId, cancellationToken);

    /// <summary> Checks whether the resolved attribute values match the filter's required specs. Matches <c>filter_list_check_attributes</c>
    /// (filter.c:470-486). </summary> <summary> Maps filter-list flags to attribute-check flags. Matches <c>git_filter_list__load</c>'s attribute options
    /// mapping (filter.c:443-458). Internal so filter Apply phases (which re-resolve attributes, e.g. CRLF) use the same flags as Check. </summary>
    internal static GitAttrCheckFlags AttrFlagsFromFilterFlags(GitFilterListFlags filterFlags)
    {
        GitAttrCheckFlags attrFlags = GitAttrCheckFlags.FileThenIndex;
        if ((filterFlags & GitFilterListFlags.NoSystemAttributes) != 0)
        {
            attrFlags |= GitAttrCheckFlags.NoSystem;
        }

        if ((filterFlags & GitFilterListFlags.AttributesFromHead) != 0)
        {
            attrFlags |= GitAttrCheckFlags.IncludeHead;
        }

        if ((filterFlags & GitFilterListFlags.AttributesFromCommit) != 0)
        {
            attrFlags |= GitAttrCheckFlags.IncludeCommit;
        }

        return attrFlags;
    }

    private static bool CheckMatches(FilterDef fdef, GitAttrValue[] values)
    {
        for (int i = 0; i < fdef.Specs.Count; i++)
        {
            AttrSpec spec = fdef.Specs[i];
            if (!spec.RequiresMatch)
            {
                continue;
            }

            GitAttrValue found = values[i];

            // Compare based on expected value.
            string? expected = spec.ExpectedValue;
            if (expected == "true")
            {
                if (!found.IsTrue)
                {
                    return false;
                }
            }
            else if (expected == "false")
            {
                if (!found.IsFalse)
                {
                    return false;
                }
            }
            else if (expected == "unset")
            {
                if (!found.IsUnspecified)
                {
                    return false;
                }
            }
            else if (expected is not null)
            {
                // String value match — "=" spec.
                // Wildcard "*" matches any string value.
                if (expected == "*")
                {
                    if (found.Kind != GitAttrValueKind.Value)
                    {
                        return false;
                    }
                }
                else if (found.Kind != GitAttrValueKind.Value
                    || !string.Equals(found.Text, expected, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the filter list to a buffer. Matches
    /// <c>git_filter_list_apply_to_buffer</c> (filter.c:757-783). If the
    /// list is null or empty, returns the input unchanged.
    /// </summary>
    public async ValueTask<byte[]> ApplyToBufferAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        => await ApplyToBufferAsync(input, _source, cancellationToken).ConfigureAwait(false);

    private async ValueTask<byte[]> ApplyToBufferAsync(ReadOnlyMemory<byte> input, GitFilterSource source, CancellationToken cancellationToken)
    {
        if (_entries.Count == 0)
        {
            return input.ToArray();
        }

        ReadOnlyMemory<byte> current = input;
        byte[]? currentArray = null;

        // Apply filters in the same chaining direction as stream_list_init
        // (filter.c:1078-1079): the entries are sorted by priority ascending
        // ([crlf(0), ident(100)]). On clean (TO_ODB) the stream chain is
        // built forward, so the data flows ident → crlf (ident FIRST); on
        // smudge (TO_WORKTREE) it is built in reverse, so the data flows
        // crlf → ident (crlf FIRST).
        if (source.Mode == GitFilterMode.ToWorktree)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                GitApplyResult result = await _entries[i].Filter.ApplyAsync(source, current, cancellationToken).ConfigureAwait(false);
                if (result.Applied && result.Output is not null)
                {
                    currentArray = result.Output;
                    current = currentArray;
                }
            }
        }
        else
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                GitApplyResult result = await _entries[i].Filter.ApplyAsync(source, current, cancellationToken).ConfigureAwait(false);
                if (result.Applied && result.Output is not null)
                {
                    currentArray = result.Output;
                    current = currentArray;
                }
            }
        }

        return currentArray is not null ? currentArray : current.ToArray();
    }

    /// <summary> Applies the filter list to a file on disk. Matches <c>git_filter_list_apply_to_file</c> (filter.c:807-833). Byte-faithful: the FS-boundary
    /// <c>Path.Join</c> routes through <see cref="GitPath.ToFileSystemString"/>. </summary>
    public async Task<byte[]> ApplyToFileAsync(GitRepository repo, GitPath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        string fullPath = repo.Workdir is not null
            ? Path.Join(repo.Workdir, path.ToFileSystemString())
            : path.ToFileSystemString();
        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return await ApplyToBufferAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    public Task<byte[]> ApplyToFileAsync(GitRepository repo, string path, CancellationToken cancellationToken = default)
        => ApplyToFileAsync(repo, GitPath.FromUtf8String(path), cancellationToken);

    /// <summary>
    /// Applies the filter list to a blob's content. Matches
    /// <c>git_filter_list_apply_to_blob</c> (filter.c:848-872).
    /// </summary>
    public async ValueTask<byte[]> ApplyToBlobAsync(GitBlob blob, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blob);

        // C (filter.c:1183-1196, git_filter_list_stream_blob): the blob's OID is copied into the filter source before applying — the ident filter's $Id$
        // expansion is gated on it.
        GitFilterSource blobSource = _source with { Id = blob.Id };
        return await ApplyToBufferAsync(blob.Content, blobSource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a buffer through the filter pipeline into the target stream.
    /// Matches <c>git_filter_list_stream_buffer</c> (filter.c:1158-1190).
    /// </summary>
    public async ValueTask StreamBufferAsync(ReadOnlyMemory<byte> input, IFilterWriteStream target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        IFilterWriteStream streamStart = BuildStreamChain(target);
        streamStart.Write(input.Span);
        await streamStart.CloseAsync(cancellationToken).ConfigureAwait(false);
        streamStart.Dispose();
    }

    /// <summary> Streams a file through the filter pipeline into the target stream. Matches <c>git_filter_list_stream_file</c> (filter.c:1112-1156).
    /// Byte-faithful: the FS-boundary <c>Path.Join</c> routes through <see cref="GitPath.ToFileSystemString"/>. </summary>
    public async Task StreamFileAsync(GitRepository repo, GitPath path, IFilterWriteStream target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(target);

        string fullPath = repo.Workdir is not null
            ? Path.Join(repo.Workdir, path.ToFileSystemString())
            : path.ToFileSystemString();

        IFilterWriteStream streamStart = BuildStreamChain(target);
        using var fd = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
        byte[] buf = new byte[16 * 1024];
        int read;
        while ((read = await fd.ReadAsync(buf.AsMemory(0, buf.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            streamStart.Write(buf.AsSpan(0, read));
        }

        await streamStart.CloseAsync(cancellationToken).ConfigureAwait(false);
        streamStart.Dispose();
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    public Task StreamFileAsync(GitRepository repo, string path, IFilterWriteStream target, CancellationToken cancellationToken = default)
        => StreamFileAsync(repo, GitPath.FromUtf8String(path), target, cancellationToken);

    /// <summary>
    /// Builds the stream chain for the filter pipeline. Matches
    /// <c>stream_list_init</c> (filter.c:1059-1100). For smudge, filters are
    /// chained in reverse order; for clean, in forward order.
    /// </summary>
    private IFilterWriteStream BuildStreamChain(IFilterWriteStream target)
    {
        if (_entries.Count == 0)
        {
            return target;
        }

        IFilterWriteStream last = target;

        if (_source.Mode == GitFilterMode.ToWorktree)
        {
            // Smudge: reverse order (last filter in list runs first on data).
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                last = new BufferedFilterStream(_entries[i].Filter, _source, last);
            }
        }
        else
        {
            // Clean: forward order.
            for (int i = 0; i < _entries.Count; i++)
            {
                last = new BufferedFilterStream(_entries[i].Filter, _source, last);
            }
        }

        return last;
    }

    /// <summary>
    /// Checks whether the list contains a filter with the given name.
    /// Matches <c>git_filter_list_contains</c>.
    /// </summary>
    public bool Contains(string name)
    {
        foreach (FilterEntry entry in _entries)
        {
            if (entry.Filter.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Releases the filter list and its filter payloads.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries.Clear();
    }

    private sealed class FilterEntry(IFilter filter)
    {
        public IFilter Filter { get; } = filter;
    }
}

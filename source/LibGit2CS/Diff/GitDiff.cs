// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary>
/// Facade for a completed diff: holds the generated deltas and provides
/// iteration, merge, and output APIs. Managed port of
/// <c>src/libgit2/diff.c</c> (402 LOC) — the public surface that wraps
/// <see cref="DiffGenerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two-phase split is preserved: iterating
/// <see cref="Deltas"/> is cheap (no content loading); <see cref="PatchesAsync"/>,
/// <see cref="GetStatsAsync"/>, <see cref="PrintAsync"/> and <see cref="LibGit2CS.Diff.GitDiff.ToBufferAsync(System.Buffers.IBufferWriter{byte}, LibGit2CS.Diff.GitDiffPrintFormat, System.Threading.CancellationToken)"/>
/// materialize patches (load content + run XDiff lazily).
/// </para>
/// <para>
/// <see cref="FromBuffer(string, GitDiffParseOptions?)"/> parses a unified-diff
/// text into a <see cref="GitDiff"/> via <c>PatchParser</c> + <c>DiffParsed</c>.
/// Iterating <see cref="PatchesAsync"/> on a parsed diff returns
/// pre-materialized patches.
/// </para>
/// </remarks>
public sealed class GitDiff : IDisposable
{
    private readonly List<GitDiffDelta> _deltas;
    private readonly DiffGenerator? _generator;
    private readonly IDiffPatchSource? _patchSource;
    private readonly GitDiffOptions _opts;
    private bool _disposed;

    /// <summary>
    /// Creates a <c>Diff</c> wrapping a <see cref="DiffGenerator"/>.
    /// </summary>
    internal GitDiff(DiffGenerator generator)
    {
        _generator = generator;
        _patchSource = generator;
        _deltas = [.. generator.Deltas];
        _opts = generator.Options;
    }

    /// <summary>
    /// Creates a <c>Diff</c> from an explicit delta list (for blob/buffer diffs
    /// and parsed diffs). Used by <see cref="Blobs"/>/<see cref="Buffers"/>/
    /// <see cref="BlobToBuffer"/>.
    /// </summary>
    internal GitDiff(List<GitDiffDelta> deltas, GitDiffOptions opts)
    {
        _deltas = deltas;
        _opts = opts;
    }

    /// <summary>
    /// Creates a <c>Diff</c> from a parsed-patch source
    /// (<see cref="DiffParsed"/>). The delta list is sourced from the parsed
    /// patches.
    /// </summary>
    internal GitDiff(IDiffPatchSource patchSource, List<GitDiffDelta> deltas, GitDiffOptions opts)
    {
        _patchSource = patchSource;
        _deltas = deltas;
        _opts = opts;
    }

    // ━━ Static factory methods ━━

    /// <summary>Tree-to-tree diff. Matches <c>git_diff_tree_to_tree</c>.
    /// Internal — the public entry point is
    /// <see cref="GitRepository.DiffTreeToTreeAsync"/>.</summary>
    internal static async Task<GitDiff> TreeToTreeAsync(GitRepository repo, GitTree? oldTree, GitTree? newTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => new(await DiffGenerator.TreeToTreeAsync(repo, oldTree, newTree, options, cancellationToken).ConfigureAwait(false));

    /// <summary>Tree-to-index diff. Matches <c>git_diff_tree_to_index</c>.
    /// Internal — the public entry point is
    /// <see cref="GitRepository.DiffTreeToIndexAsync"/>.</summary>
    internal static async Task<GitDiff> TreeToIndexAsync(GitRepository repo, GitTree? oldTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return new(await DiffGenerator.TreeToIndexAsync(repo, oldTree, options, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Index-to-workdir diff. Matches <c>git_diff_index_to_workdir</c>.
    /// Internal — the public entry point is
    /// <see cref="GitRepository.DiffIndexToWorkdirAsync"/>.</summary>
    internal static async Task<GitDiff> IndexToWorkdirAsync(GitRepository repo, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return new(await DiffGenerator.IndexToWorkdirAsync(repo, options, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Tree-to-workdir diff. Matches <c>git_diff_tree_to_workdir</c>.
    /// Internal — the public entry point is
    /// <see cref="GitRepository.DiffTreeToWorkdirAsync"/>.</summary>
    internal static async Task<GitDiff> TreeToWorkdirAsync(GitRepository repo, GitTree? oldTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => new(await DiffGenerator.TreeToWorkdirAsync(repo, oldTree, options, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Tree-to-workdir-with-index diff (combines tree-to-index + index-to-workdir).
    /// Matches <c>git_diff_tree_to_workdir_with_index</c>. Internal — the public
    /// entry point is <see cref="GitRepository.DiffTreeToWorkdirWithIndexAsync"/>.
    /// </summary>
    internal static async Task<GitDiff> TreeToWorkdirWithIndexAsync(GitRepository repo, GitTree? oldTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        GitDiff t2i = await TreeToIndexAsync(repo, oldTree, options, cancellationToken).ConfigureAwait(false);
        GitDiff i2w = await IndexToWorkdirAsync(repo, options, cancellationToken).ConfigureAwait(false);
        t2i.Merge(i2w);
        return t2i;
    }

    /// <summary>Index-to-index diff. Matches <c>git_diff_index_to_index</c>.
    /// Internal — the public entry point is
    /// <see cref="GitRepository.DiffIndexToIndexAsync"/>.</summary>
    internal static async Task<GitDiff> IndexToIndexAsync(GitRepository repo, Index.GitIndex oldIndex, Index.GitIndex newIndex, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        GitDiffOptions opts = options ?? new GitDiffOptions();
        IteratorOptions iterOpts = DiffGenerator.PrepareIteratorOptions(opts, IteratorFlags.None);

        IIterator oldIter = IndexIterator.ForIndex(oldIndex, repo, iterOpts);
        IIterator newIter = IndexIterator.ForIndex(newIndex, repo, iterOpts);

        return new(await DiffGenerator.GenerateAsync(repo, oldIter, newIter, opts, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Commit diff (vs first parent). Matches <c>git_diff__commit</c>
    /// (diff_generate.c:1709-1750). Root commits diff against an empty tree;
    /// merge commits use first parent. Internal — the public entry point is
    /// <see cref="GitRepository.DiffCommitAsync"/>.
    /// </summary>
    internal static async Task<GitDiff> CommitAsync(GitRepository repo, Commit commit, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(commit);
        return new(await DiffGenerator.CommitAsync(repo, commit, options, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Blob-to-buffer diff. Matches <c>git_diff_blob_to_buffer</c>.
    /// Internal — the public entry point is
    /// <see cref="GitRepository.DiffBlobToBuffer"/>.</summary>
    internal static GitDiff BlobToBuffer(GitRepository repo, GitBlob? oldBlob, ReadOnlySpan<char> newContent, GitDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        GitDiffOptions opts = options ?? new GitDiffOptions();

        byte[]? buffer = newContent.IsEmpty ? null : ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(newContent));
        try
        {
            int bytesWritten = newContent.IsEmpty ? 0 : Encoding.UTF8.GetBytes(newContent, buffer);

            // C's default
            // path is "file" (patch_generate.c:490-497), not "blob"/"buffer".
            return BlobsAndBuffers(
                repo, oldBlob, "file", buffer.AsSpan(0, bytesWritten), "file", opts);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Blob-to-blob diff. Matches <c>git_diff_blobs</c>. Internal —
    /// the public entry point is <see cref="GitRepository.DiffBlobs"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Kept for facade symmetry with the repo wrapper; blob IDs are already resolved so no repo lookup is needed.")]
    internal static GitDiff Blobs(GitRepository repo, GitBlob oldBlob, GitBlob newBlob, GitDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldBlob);
        ArgumentNullException.ThrowIfNull(newBlob);
        GitDiffOptions opts = options ?? new GitDiffOptions();
        var deltas = new List<GitDiffDelta>();

        // C's default path is "file" (patch_generate.c:490-497), not
        // "blob".
        var oldFile = new GitDiffFile
        {
            Path = GitPath.FromUtf8String("file"),
            Id = oldBlob.Id,
            Size = oldBlob.Size,
            Mode = GitFileMode.Regular,
            Flags = GitDiffFileFlags.ValidId | GitDiffFileFlags.Exists,
            IdAbbrev = oldBlob.Id.HexLength
        };

        var newFile = new GitDiffFile
        {
            Path = GitPath.FromUtf8String("file"),
            Id = newBlob.Id,
            Size = newBlob.Size,
            Mode = GitFileMode.Regular,
            Flags = GitDiffFileFlags.ValidId | GitDiffFileFlags.Exists,
            IdAbbrev = newBlob.Id.HexLength
        };

        GitDeltaStatus status = oldBlob.Id == newBlob.Id
            ? GitDeltaStatus.Unmodified
            : GitDeltaStatus.Modified;

        deltas.Add(new GitDiffDelta(status, 2, oldFile, newFile));
        return new GitDiff(deltas, opts);
    }

    /// <summary>Buffer-to-buffer diff. Matches <c>git_diff_buffers</c>.</summary>
    public static GitDiff Buffers(ReadOnlyMemory<byte> oldBuffer, ReadOnlyMemory<byte> newBuffer, GitDiffOptions? options = null)
    {
        GitDiffOptions opts = options ?? new GitDiffOptions();
        GitHashAlgorithmKind oidType = opts.OidType;
        GitOid oldId = GitObjectDb.HashObject(GitObjectType.Blob, oldBuffer.Span, oidType);
        GitOid newId = GitObjectDb.HashObject(GitObjectType.Blob, newBuffer.Span, oidType);

        // C's default path is "file" (patch_generate.c:490-497), not
        // "buffer".
        var oldFile = new GitDiffFile
        {
            Path = GitPath.FromUtf8String("file"),
            Id = oldId,
            Size = oldBuffer.Length,
            Mode = GitFileMode.Regular,
            Flags = GitDiffFileFlags.ValidId | GitDiffFileFlags.Exists,
            IdAbbrev = oldId.HexLength
        };

        var newFile = new GitDiffFile
        {
            Path = GitPath.FromUtf8String("file"),
            Id = newId,
            Size = newBuffer.Length,
            Mode = GitFileMode.Regular,
            Flags = GitDiffFileFlags.ValidId | GitDiffFileFlags.Exists,
            IdAbbrev = newId.HexLength
        };

        GitDeltaStatus status = oldId == newId ? GitDeltaStatus.Unmodified : GitDeltaStatus.Modified;
        var deltas = new List<GitDiffDelta> { new(status, 2, oldFile, newFile) };
        return new GitDiff(deltas, opts);
    }

    /// <summary>
    /// Parses a multi-file patch from raw bytes. Matches
    /// <c>git_diff_from_buffer</c> (diff_parse.c:68-122). Each
    /// <c>diff --git</c> header in the buffer becomes a separate
    /// <see cref="GitPatch"/> accessible via <see cref="PatchesAsync"/>.
    /// </summary>
    /// <remarks>
    /// This is the byte-parity surface: arbitrary (binary / invalid-UTF-8)
    /// patch bytes round-trip exactly. Use
    /// <see cref="FromBuffer(string, GitDiffParseOptions?)"/> for the UTF-8
    /// string convenience tier.
    /// </remarks>
    public static GitDiff FromBuffer(ReadOnlyMemory<byte> patchBytes, GitDiffParseOptions? options = null)
    {
        DiffParsed parsed = DiffParsed.FromBuffer(patchBytes, options)
            ?? throw new ArgumentException("Failed to parse patch: no valid patches found.", nameof(patchBytes));
        var deltas = new List<GitDiffDelta>(parsed.Deltas);
        var opts = new GitDiffOptions();
        return new GitDiff(parsed, deltas, opts);
    }

    /// <summary>
    /// Parses a multi-file patch from a UTF-8 text string. Convenience
    /// overload: the string is UTF-8-encoded into the byte parse domain.
    /// Binary or invalid-UTF-8 patches must use
    /// <see cref="FromBuffer(ReadOnlyMemory{byte}, GitDiffParseOptions?)"/>.
    /// </summary>
    public static GitDiff FromBuffer(string patchText, GitDiffParseOptions? options = null)
    {
        DiffParsed parsed = DiffParsed.FromBuffer(
            patchText is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(patchText), options)
            ?? throw new ArgumentException("Failed to parse patch text: no valid patches found.", nameof(patchText));
        var deltas = new List<GitDiffDelta>(parsed.Deltas);
        var opts = new GitDiffOptions();
        return new GitDiff(parsed, deltas, opts);
    }

    /// <summary>
    /// Internal helper: creates a diff from a blob + buffer.
    /// </summary>
    private static GitDiff BlobsAndBuffers(
        GitRepository repo, GitBlob? oldBlob, string oldPath,
        ReadOnlySpan<byte> newBytes, string newPath, GitDiffOptions opts)
    {
        // Seed OidType from the repo if the caller left it at the default.
        // Matches how DiffGenerator.ApplyOptions seeds generator diffs.
        if (opts.OidType == GitHashAlgorithmKind.Sha1
            && repo.ObjectFormat != GitHashAlgorithmKind.Sha1)
        {
            opts = opts with { OidType = repo.ObjectFormat };
        }

        var deltas = new List<GitDiffDelta>();

        var oldFile = new GitDiffFile
        {
            Path = GitPath.FromUtf8String(oldPath),
            Mode = GitFileMode.Regular,
            Flags = GitDiffFileFlags.Exists,
        };

        if (oldBlob is not null)
        {
            oldFile.Id = oldBlob.Id;
            oldFile.Size = oldBlob.Size;
            oldFile.Flags |= GitDiffFileFlags.ValidId;
            oldFile.IdAbbrev = oldBlob.Id.HexLength;
        }

        GitOid newId = GitObjectDb.HashObject(GitObjectType.Blob, newBytes, opts.OidType);
        var newFile = new GitDiffFile
        {
            Path = GitPath.FromUtf8String(newPath),
            Id = newId,
            Size = newBytes.Length,
            Mode = GitFileMode.Regular,
            Flags = GitDiffFileFlags.ValidId | GitDiffFileFlags.Exists,
            IdAbbrev = newId.HexLength
        };

        GitDeltaStatus status = oldBlob is not null && oldBlob.Id == newId
            ? GitDeltaStatus.Unmodified
            : GitDeltaStatus.Modified;

        deltas.Add(new GitDiffDelta(status, 2, oldFile, newFile));
        return new GitDiff(deltas, opts);
    }

    // ━━ Accessors ━━

    /// <summary>Number of deltas. Matches <c>git_diff_num_deltas</c>.</summary>
    public int DeltaCount => _deltas.Count;

    /// <summary>Gets the delta at <paramref name="index"/>. Matches <c>git_diff_get_delta</c>.</summary>
    public GitDiffDelta GetDelta(int index) => _deltas[index];

    /// <summary>Enumerates all deltas. Matches <c>git_diff_foreach</c> (delta-only path).</summary>
    public IEnumerable<GitDiffDelta> Deltas => _deltas;

    /// <summary>The diff options.</summary>
    internal GitDiffOptions Options => _opts;

    /// <summary>
    /// The diff's owning repository, or null. Mirrors <c>git_diff::repo</c>:
    /// set for repository-generated diffs, null for buffer/blob/parsed diffs.
    /// Used by <see cref="DiffPrinter"/> to resolve <c>id_abbrev == 0</c>
    /// against <c>core.abbrev</c> (diff_print.c:52-63).
    /// </summary>
    internal GitRepository? Repo => _generator?.Repo;

    /// <summary>
    /// The resolved "a/" prefix for patch output (after config + reverse swap).
    /// Falls back to opts for blob/buffer diffs (no generator). Used by
    /// <see cref="DiffPrinter"/>.
    /// </summary>
    internal string OldPrefix => _generator?.OldPrefix ?? _opts.OldPrefix ?? "a/";

    /// <summary>The resolved "b/" prefix for patch output.</summary>
    internal string NewPrefix => _generator?.NewPrefix ?? _opts.NewPrefix ?? "b/";

    /// <summary>The underlying generator (null for blob/buffer/parsed diffs).</summary>
    internal DiffGenerator? Generator => _generator;

    /// <summary>
    /// The patch source strategy (non-null for generated AND parsed diffs).
    /// Used by <see cref="GitPatch.FromDiffAsync"/> to dispatch to the correct
    /// patch-creation path. Matches the <c>patch_fn</c> vtable slot.
    /// </summary>
    internal IDiffPatchSource? PatchSource => _patchSource;

    // ━━ Merge ━━

    /// <summary>
    /// Merges another diff's deltas into this one. Matches
    /// <c>git_diff_merge</c> (diff_tform.c:199-202) → <c>git_diff__merge</c>
    /// (diff_tform.c:114-197) with <c>git_diff__merge_like_cgit</c> status
    /// combination.
    /// </summary>
    public GitDiff Merge(GitDiff other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ThrowIfDisposed();
        if (_generator is not null && other._generator is not null)
        {
            DiffTransform.Merge(_generator, other._generator);
            // DiffTransform.Merge mutates _generator.DeltaList in place
            // (sorted merge changes the count + ordering). Refresh this
            // Diff's snapshot so DeltaCount/Deltas/GetDelta/GitPatch.FromDiffAsync
            // all stay in sync — same refresh pattern as FindSimilarAsync.
            _deltas.Clear();
            _deltas.AddRange(_generator.Deltas);
        }
        else
        {
            // Fallback: simple append for blob/buffer diffs.
            _deltas.AddRange(other._deltas);
        }

        return this;
    }

    // ━━ FindSimilar ━━

    /// <summary>
    /// Post-processes the diff to detect renames/copies. Matches
    /// <c>git_diff_find_similar</c>. Mutates the delta list in place.
    /// </summary>
    public async Task<GitDiff> FindSimilarAsync(GitDiffFindOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_generator is not null)
        {
            await DiffTransform.FindSimilarAsync(_generator, options, cancellationToken).ConfigureAwait(false);
            // DiffTransform mutates the generator's DeltaList in place (rename
            // merge changes the count + ordering). Refresh this Diff's snapshot
            // so DeltaCount/Deltas/GetDelta/GitPatch.FromDiffAsync all stay in sync.
            _deltas.Clear();
            _deltas.AddRange(_generator.Deltas);
        }

        return this;
    }

    // ━━ Stats / Print / Patches (steps 6-8) ━━

    /// <summary>Computes diff statistics. Matches <c>git_diff_get_stats</c>
    /// (diff_stats.c:177-259). Materializes every patch (two-pass: gather
    /// maxes, then format).</summary>
    public async Task<GitDiffStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        int n = DeltaCount;
        var perFile = new GitDiffFileStat[n];
        int totalInsertions = 0, totalDeletions = 0;
        int maxName = 0, maxFilestat = 0;

        for (int i = 0; i < n; i++)
        {
            using GitPatch patch = await GitPatch.FromDiffAsync(this, i, cancellationToken).ConfigureAwait(false);
            GitDiffDelta delta = GetDelta(i);
            GitPath path = delta.NewFile.Path ?? delta.OldFile.Path ?? default;
            GitPath? oldPath = delta.OldFile.Path;

            int add, del;
            // C's
            // git_diff_get_stats always calls git_patch_line_stats
            // (diff_stats.c:231), which counts 0/0 for binary patches —
            // nothing in diff_stats.c sets -1, which must not flow into the
            // public per-file Insertions/Deletions.
            (_, add, del) = await patch.LineStatsAsync(cancellationToken).ConfigureAwait(false);

            perFile[i] = new GitDiffFileStat(
                path, add, del,
                oldPath,
                delta.OldFile.Size,
                delta.NewFile.Size,
                (uint)delta.OldFile.Mode,
                (uint)delta.NewFile.Mode,
                // C (diff_stats.c:146-147): the "- -" numstat / "Bin"
                // rendering keys off the DELTA's binary flag, not the counts.
                ((delta.OldFile.Flags | delta.NewFile.Flags) & GitDiffFileFlags.Binary) != 0);

            if (add > 0)
            {
                totalInsertions += add;
            }

            if (del > 0)
            {
                totalDeletions += del;
            }

            int total = (add > 0 ? add : 0) + (del > 0 ? del : 0);
            if (total > maxFilestat)
            {
                maxFilestat = total;
            }

            // Rename-aware name length — matches git_diff_get_stats (diff_stats.c:220-240).
            int nameLen = path.Length;
            if (oldPath is not null && oldPath != path)
            {
                int commonDirLen = CommonDirLength(oldPath.Value, path);
                if (commonDirLen > 0)
                {
                    nameLen += oldPath.Value.Length + 2 + 4 - commonDirLen;
                }
                else
                {
                    nameLen += oldPath.Value.Length + 4;
                }
            }

            if (nameLen > maxName)
            {
                maxName = nameLen;
            }
        }

        int maxDigits = DigitsForValue(maxFilestat + 1);

        return new GitDiffStats(perFile, n, totalInsertions, totalDeletions,
            maxName, maxFilestat, maxDigits);
    }

    private static int DigitsForValue(int value)
    {
        int digits = 1;
        while (value >= 10)
        {
            digits++;
            value /= 10;
        }

        return digits;
    }

    /// <summary> Common directory prefix length (up to and including last shared <c>/</c>). Matches <c>git_fs_path_common_dirlen</c> (fs_path.c:932-944) which
    /// returns <c>(dirsep - one) + 1</c>. Byte-faithful. </summary>
    private static int CommonDirLength(GitPath one, GitPath two)
    {
        ReadOnlySpan<byte> sa = one.Span;
        ReadOnlySpan<byte> sb = two.Span;
        int min = Math.Min(sa.Length, sb.Length);
        int lastSep = -1;
        for (int i = 0; i < min; i++)
        {
            if (sa[i] != sb[i])
            {
                break;
            }

            if (sa[i] == (byte)'/')
            {
                lastSep = i;
            }
        }

        return lastSep >= 0 ? lastSep + 1 : 0;
    }

    /// <summary>Prints the diff in the given format. Matches <c>git_diff_print</c>
    /// (diff_print.c:734-790). Routes <see cref="GitDiffPrintFormat.Stat"/>/
    /// <see cref="GitDiffPrintFormat.Summary"/> to <see cref="GetStatsAsync"/> +
    /// <see cref="GitDiffStats.Format"/>.</summary>
    public async Task PrintAsync(GitDiffPrintFormat format, GitDiffPrintCallback callback, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(callback);

        // Stat/Summary route through DiffStats (same as ToBuffer).
        if (format is GitDiffPrintFormat.Stat or GitDiffPrintFormat.Summary)
        {
            GitDiffStatsFormat statsFormat = format == GitDiffPrintFormat.Stat
                ? GitDiffStatsFormat.Full | GitDiffStatsFormat.Short
                : GitDiffStatsFormat.IncludeSummary;

            using var bufferWriter = new PooledByteBufferWriter();

            GitDiffStats difStats = await GetStatsAsync(cancellationToken).ConfigureAwait(false);
            difStats.Format(bufferWriter, statsFormat);

            callback(null, null, new GitDiffLine(
                GitDiffLineOrigin.FileHeader, 0, 0, 0,
                bufferWriter.WrittenSpan.ToArray()));
            return;
        }

        await DiffPrinter.PrintAsync(this, format, callback, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Renders the diff into a caller-owned byte buffer, byte-end-to-end. Zero-copy tier mirroring <see cref="GitDiffStats.Format"/>; composes with
    /// <c>PipeWriter</c>/<c>StreamPipeWriter</c>. </summary> <param name="writer">The buffer writer receiving the rendered bytes.</param> <param
    /// name="format">The output format.</param> <param name="cancellationToken">Cancellation token.</param>
    public Task ToBufferAsync(IBufferWriter<byte> writer, GitDiffPrintFormat format, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(writer);

        // Stat/Summary route through DiffStats (same as git_diff_to_buf).
        if (format is GitDiffPrintFormat.Stat or GitDiffPrintFormat.Summary)
        {
            return WriteStatsAsync(writer, format, cancellationToken);
        }

        return DiffPrinter.PrintAsync(
            this,
            format,
            (delta, hunk, line) => XdiffBridge.RenderLine(writer, line),
            cancellationToken);
    }

    /// <summary> Renders the diff to raw bytes. Matches <c>git_diff_to_buf</c> (diff_print.c:846-863). The byte return is the parity surface (non-ASCII
    /// paths / funcnames are byte-exact vs libgit2); a Latin1-mapped <see cref="string"/> would lose bytes. </summary>
    public async Task<byte[]> ToBufferAsync(GitDiffPrintFormat format, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var bufferWriter = new PooledByteBufferWriter();
        await ToBufferAsync(bufferWriter, format, cancellationToken).ConfigureAwait(false);
        return bufferWriter.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Renders the diff to a UTF-8 decoded string (with replacement fallback,
    /// U+FFFD for invalid bytes) for display convenience. The
    /// <see cref="ToBufferAsync(GitDiffPrintFormat, CancellationToken)"/> byte
    /// tier is the parity surface.
    /// </summary>
    public async Task<string> ToBufferTextAsync(GitDiffPrintFormat format, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ToBufferAsync(format, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Renders the Stat/Summary formats via <see cref="GetStatsAsync"/> +
    /// <see cref="GitDiffStats.Format"/> directly into the caller's writer —
    /// no decode/re-encode round-trip (replaces the former Latin1 string
    /// bridge).
    /// </summary>
    private async Task WriteStatsAsync(IBufferWriter<byte> writer, GitDiffPrintFormat format, CancellationToken cancellationToken)
    {
        GitDiffStatsFormat statsFormat = format == GitDiffPrintFormat.Stat
            ? GitDiffStatsFormat.Full | GitDiffStatsFormat.Short
            : GitDiffStatsFormat.IncludeSummary;

        GitDiffStats difStats = await GetStatsAsync(cancellationToken).ConfigureAwait(false);
        difStats.Format(writer, statsFormat);
    }

    /// <summary>Enumerates patches (one per delta). Matches <c>git_diff_foreach</c>
    /// → <c>git_patch_from_diff</c> per delta. Each patch lazily loads content
    /// + runs XDiff on first hunk/line access. For parsed diffs
    /// (<see cref="LibGit2CS.Diff.GitDiff.FromBuffer(System.ReadOnlyMemory{byte}, LibGit2CS.Diff.GitDiffParseOptions?)"/>), patches are returned pre-materialized.</summary>
    public async IAsyncEnumerable<GitPatch> PatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_patchSource is not null)
        {
            for (int i = 0; i < _patchSource.PatchCount; i++)
            {
                yield return await _patchSource.GetPatchAsync(i, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            for (int i = 0; i < DeltaCount; i++)
            {
                yield return await GitPatch.FromDiffAsync(this, i, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ━━ Internal: paired foreach (for status.c) ━━

    /// <summary>
    /// Pairs head→index + index→workdir deltas. Matches
    /// <c>git_diff__paired_foreach</c> (diff_generate.c:1621-1707). Used by
    /// <c>status.c</c>. Handles either diff being null (e.g. when
    /// <c>show</c> is <c>INDEX_ONLY</c> or <c>WORKDIR_ONLY</c>) by iterating
    /// only the non-null side.
    /// </summary>
    internal static void PairedForeach(
        GitDiff? head2Idx, GitDiff? idx2Wd,
        Func<GitDiffDelta?, GitDiffDelta?, bool> callback)
    {
        // Both null → nothing to iterate.
        if (head2Idx is null && idx2Wd is null)
        {
            return;
        }

        // head2Idx null → iterate idx2Wd only.
        if (head2Idx is null)
        {
            if (idx2Wd?._generator is { } i2w)
            {
                DiffGenerator.PairedForeach(null, i2w, callback);
            }

            return;
        }

        // idx2Wd null → iterate head2Idx only.
        if (idx2Wd is null)
        {
            if (head2Idx._generator is { } h2i)
            {
                DiffGenerator.PairedForeach(h2i, null, callback);
            }

            return;
        }

        // Both non-null → normal paired walk.
        if (head2Idx._generator is { } h2iGen && idx2Wd._generator is { } i2wGen)
        {
            DiffGenerator.PairedForeach(h2iGen, i2wGen, callback);
        }
    }

    // ━━ Dispose ━━

    /// <summary>Releases resources. Matches <c>git_diff_free</c>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

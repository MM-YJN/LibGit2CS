// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Text;

using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary>
/// Patch facade for a single delta's diff output. Managed port of
/// <c>src/libgit2/patch.c</c> (230 LOC) — the public-facing type that wraps
/// either a <see cref="PatchGenerator"/> (from diff/blob/buffer) or a
/// parsed patch (<c>PatchParser</c>).
/// </summary>
/// <remarks>
/// <para>
/// Matches <c>git_patch</c> (<c>patch.h:22-39</c>). In C, <c>git_patch</c>
/// dispatches to <c>git_patch_generated</c> or <c>git_patch_parsed</c> via a
/// <c>patch_fn</c> vtable. In C#, the strategy is held directly.
/// </para>
/// <para>
/// <b>Lazy materialization</b>: accessing <see cref="Delta"/> is
/// cheap (no content load); accessing <see cref="GetHunkCountAsync"/>/<see cref="GetHunkAsync"/>
/// triggers content load + XDiff via <see cref="PatchGenerator.EnsureCreatedAsync"/>.
/// </para>
/// </remarks>
public sealed class GitPatch : IDisposable
{
    private readonly IPatchSource _source;
    private bool _disposed;

    internal GitPatch(IPatchSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Creates a patch for delta <paramref name="index"/> in the given diff.
    /// Matches <c>git_patch_from_diff</c> (patch.c:212-218). Dispatches via
    /// the diff's <c>IDiffPatchSource</c> — works for both generated diffs
    /// (lazily runs XDiff) and parsed diffs (returns pre-parsed patches).
    /// Internal — the public entry point is
    /// <see cref="GitRepository.PatchFromDiffAsync"/>.
    /// </summary>
    internal static async ValueTask<GitPatch> FromDiffAsync(GitDiff diff, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.PatchSource is not { } source)
        {
            throw new InvalidOperationException("Diff was not created from a generator (blob/buffer diffs have no indexable deltas).");
        }

        if (index < 0 || index >= diff.DeltaCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return await source.GetPatchAsync(index, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a patch comparing two blobs. Matches <c>git_patch_from_blobs</c>
    /// (patch_generate.c:617-656). Either blob may be null (produces an
    /// Added/Deleted delta). With no paths, both sides default to "file"
    /// (patch_generate.c:490-497). Internal — the public entry point is
    /// <see cref="GitRepository.PatchFromBlobs"/>.
    /// </summary>
    internal static GitPatch FromBlobs(
        GitRepository repo, GitBlob? oldBlob, GitBlob? newBlob,
        string? oldPath = null, string? newPath = null, GitDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        GitDiffOptions opts = options ?? new GitDiffOptions();
        return new GitPatch(PatchGenerator.FromBlobs(repo, oldBlob, newBlob, opts, oldPath, newPath, repo.Context.DiffDrivers));
    }

    /// <summary>
    /// Creates a patch comparing a blob to an in-memory buffer. Matches
    /// <c>git_patch_from_blob_and_buffer</c> (patch_generate.c:658-672).
    /// Internal — the public entry point is
    /// <see cref="GitRepository.PatchFromBlobAndBuffer"/>.
    /// </summary>
    internal static GitPatch FromBlobAndBuffer(
        GitRepository repo, GitBlob? oldBlob, ReadOnlyMemory<byte> newBuffer,
        GitDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        GitDiffOptions opts = options ?? new GitDiffOptions();
        return new GitPatch(PatchGenerator.FromBlobAndBuffer(
            repo, oldBlob, newBuffer, opts, oldPath: null, newPath: null, repo.Context.DiffDrivers));
    }

    /// <summary>
    /// Creates a patch comparing two in-memory buffers. Matches
    /// <c>git_patch_from_buffers</c> (patch_generate.c:696-711). Internal — the
    /// public entry point is <see cref="GitRepository.PatchFromBuffers"/>.
    /// </summary>
    internal static GitPatch FromBuffers(
        GitRepository repo, ReadOnlyMemory<byte> oldBuffer, ReadOnlyMemory<byte> newBuffer,
        GitDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        GitDiffOptions opts = options ?? new GitDiffOptions();
        return new GitPatch(PatchGenerator.FromBuffers(
            repo, oldBuffer, newBuffer, opts, oldPath: null, newPath: null, repo.Context.DiffDrivers));
    }

    /// <summary>
    /// Parses a patch from raw bytes. Matches <c>git_patch_from_buffer</c>
    /// (patch_parse.c:1222-1238). The buffer must contain a single patch
    /// (one <c>diff --git</c> header). For multi-file patches, use
    /// <see cref="GitDiff.FromBuffer(ReadOnlyMemory{byte}, GitDiffParseOptions?)"/>.
    /// </summary>
    /// <remarks>
    /// This is the byte-parity surface: arbitrary (binary / invalid-UTF-8)
    /// patch bytes parse exactly. Use
    /// <see cref="FromBuffer(string, GitPatchParseOptions?)"/> for the UTF-8
    /// string convenience tier.
    /// </remarks>
    public static GitPatch FromBuffer(ReadOnlyMemory<byte> patchBytes, GitPatchParseOptions? options = null)
    {
        ParsedPatch parsed = PatchParser.FromBuffer(patchBytes, options)
            ?? throw new ArgumentException("Failed to parse patch: no valid patch found in input.", nameof(patchBytes));
        return new GitPatch(new ParsedPatchSource(parsed));
    }

    /// <summary>
    /// Parses a patch from a UTF-8 text string. Convenience overload: the
    /// string is UTF-8-encoded into the byte parse domain. Binary or
    /// invalid-UTF-8 patches must use
    /// <see cref="FromBuffer(ReadOnlyMemory{byte}, GitPatchParseOptions?)"/>.
    /// Matches <c>git_patch_from_buffer</c> (patch_parse.c:1222-1238); the
    /// text must contain a single patch (one <c>diff --git</c> header). For
    /// multi-file patches, use <see cref="GitDiff.FromBuffer(string, GitDiffParseOptions?)"/>.
    /// </summary>
    public static GitPatch FromBuffer(string patchText, GitPatchParseOptions? options = null)
    {
        ParsedPatch parsed = PatchParser.FromBuffer(patchText, options)
            ?? throw new ArgumentException("Failed to parse patch: no valid patch found in input.", nameof(patchText));
        return new GitPatch(new ParsedPatchSource(parsed));
    }

    /// <summary>
    /// Tries to parse a patch from raw bytes. Returns false if no valid patch
    /// is found.
    /// </summary>
    public static bool TryFromBuffer(ReadOnlyMemory<byte> patchBytes, out GitPatch? patch, GitPatchParseOptions? options = null)
    {
        ParsedPatch? parsed = PatchParser.FromBuffer(patchBytes, options);
        if (parsed is null)
        {
            patch = null;
            return false;
        }

        patch = new GitPatch(new ParsedPatchSource(parsed));
        return true;
    }

    /// <summary>
    /// Tries to parse a patch from a UTF-8 text string (UTF-8-encoded into
    /// the byte parse domain). Returns false if no valid patch is found.
    /// Binary or invalid-UTF-8 patches must use
    /// <see cref="TryFromBuffer(ReadOnlyMemory{byte}, out GitPatch?, GitPatchParseOptions?)"/>.
    /// </summary>
    public static bool TryFromBuffer(string patchText, out GitPatch? patch, GitPatchParseOptions? options = null)
    {
        ParsedPatch? parsed = PatchParser.FromBuffer(patchText, options);
        if (parsed is null)
        {
            patch = null;
            return false;
        }

        patch = new GitPatch(new ParsedPatchSource(parsed));
        return true;
    }

    /// <summary>The internal patch source (for apply to access parsed data).</summary>
    internal IPatchSource Source => _source;

    /// <summary>
    /// The patch's owning repository, or null. Mirrors <c>git_patch::repo</c>;
    /// used by the printer to resolve <c>id_abbrev == 0</c> via
    /// <c>core.abbrev</c>.
    /// </summary>
    internal GitRepository? Repo => _source.Repo;

    /// <summary>The delta this patch represents.</summary>
    public GitDiffDelta Delta => _source.Delta;

    /// <summary>
    /// The configured OID abbreviation length for patch output
    /// (<c>diff_opts.id_abbrev</c>, default 7). Used by <see cref="DiffPrinter"/>.
    /// </summary>
    internal int IdAbbrevLength => _source.IdAbbrevLength;

    /// <summary>The resolved "a/" prefix (after config + reverse swap). Used by <see cref="DiffPrinter"/>.</summary>
    internal string OldPrefix => _source.OldPrefix;

    /// <summary>The resolved "b/" prefix (after config + reverse swap).</summary>
    internal string NewPrefix => _source.NewPrefix;

    /// <summary>The diff options flags (patch->diff_opts.flags, diff_print.c:106).</summary>
    internal GitDiffOptionsFlags DiffFlags => _source.DiffFlags;

    /// <summary>Number of hunks in this patch.</summary>
    public async Task<int> GetHunkCountAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitDiffHunk> hunks = await _source.GetHunksAsync(cancellationToken).ConfigureAwait(false);
        return hunks.Count;
    }

    /// <summary>
    /// Gets the hunk at <paramref name="index"/>, or null if out of range.
    /// Matches <c>git_patch_get_hunk</c> (patch.c:148-168).
    /// </summary>
    public async Task<GitDiffHunk?> GetHunkAsync(int index, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitDiffHunk> hunks = await _source.GetHunksAsync(cancellationToken).ConfigureAwait(false);
        if (index < 0 || index >= hunks.Count)
        {
            return null;
        }

        return hunks[index];
    }

    /// <summary>
    /// Number of lines in the hunk at <paramref name="hunkIdx"/>. Matches
    /// <c>git_patch_num_lines_in_hunk</c> (patch.c:170-178).
    /// </summary>
    public async Task<int> NumLinesInHunkAsync(int hunkIdx, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitDiffHunk> hunks = await _source.GetHunksAsync(cancellationToken).ConfigureAwait(false);
        if (hunkIdx < 0 || hunkIdx >= hunks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(hunkIdx));
        }

        return hunks[hunkIdx].Lines.Count;
    }

    /// <summary>
    /// Gets a specific line within a hunk. Matches
    /// <c>git_patch_get_line_in_hunk</c> (patch.c:180-205).
    /// </summary>
    public async Task<GitDiffLine> GetLineInHunkAsync(int hunkIdx, int lineIdx, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitDiffHunk> hunks = await _source.GetHunksAsync(cancellationToken).ConfigureAwait(false);
        if (hunkIdx < 0 || hunkIdx >= hunks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(hunkIdx));
        }

        IReadOnlyList<GitDiffLine> lines = hunks[hunkIdx].Lines;
        if (lineIdx < 0 || lineIdx >= lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(lineIdx));
        }

        return lines[lineIdx];
    }

    /// <summary>
    /// Computes line statistics. Matches <c>git_patch_line_stats</c>
    /// (patch.c:93-128).
    /// </summary>
    /// <returns>A tuple of (context, additions, deletions) line counts.</returns>
    public Task<(int context, int additions, int deletions)> LineStatsAsync(CancellationToken cancellationToken = default)
    {
        return _source.LineStatsAsync(cancellationToken);
    }

    /// <summary>
    /// The binary patch data, or null if this is not a binary delta.
    /// </summary>
    public Task<GitBinaryPatch?> GetBinaryAsync(CancellationToken cancellationToken = default)
    {
        return _source.GetBinaryAsync(cancellationToken);
    }

    /// <summary>Whether this patch represents a binary file change.</summary>
    public Task<bool> GetIsBinaryAsync(CancellationToken cancellationToken = default)
    {
        return _source.GetIsBinaryAsync(cancellationToken);
    }

    /// <summary> Renders this patch into a caller-owned byte buffer, byte-end-to-end. Zero-copy tier mirroring <see cref="GitDiffStats.Format"/>; composes with
    /// <c>PipeWriter</c>/<c>StreamPipeWriter</c>. </summary> <param name="writer">The buffer writer receiving the rendered bytes.</param> <param
    /// name="cancellationToken">Cancellation token.</param>
    public Task ToBufferAsync(IBufferWriter<byte> writer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(writer);

        return DiffPrinter.PrintPatchAsync(
            this,
            (delta, hunk, line) => XdiffBridge.RenderLine(writer, line),
            cancellationToken);
    }

    /// <summary>
    /// Renders this patch to raw bytes (unified-diff patch format). Matches
    /// <c>git_patch_to_buf</c> (patch.c:220-230 → diff_print.c:866-891).
    /// </summary>
    public async Task<byte[]> ToBufferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var bufferWriter = new PooledByteBufferWriter();
        await ToBufferAsync(bufferWriter, cancellationToken).ConfigureAwait(false);
        return bufferWriter.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Renders this patch to a UTF-8 decoded string (with replacement
    /// fallback, U+FFFD for invalid bytes) for display convenience. The
    /// <see cref="ToBufferAsync(CancellationToken)"/> byte tier is the parity
    /// surface.
    /// </summary>
    public async Task<string> ToBufferTextAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ToBufferAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>Releases resources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}

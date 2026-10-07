// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary>
/// Generated patch implementation. Managed port of
/// <c>src/libgit2/patch_generate.c</c> (934 LOC) — the lazy-load → XDiff
/// pipeline for <c>git_patch_generated</c>.
/// </summary>
/// <remarks>
/// <para>
/// Models <c>git_patch_generated</c> (<c>patch_generate.h:16-46</c>): holds the
/// <see cref="GitDiffDelta"/>, two <see cref="DiffFileContent"/> sides (ofile/nfile),
/// accumulated hunks/lines, and optional binary patch data.
/// </para>
/// <para>
/// <b>Lazy two-phase split</b>: delta generation
/// (<see cref="DiffGenerator"/>) does NOT load blob content or run XDiff.
/// Content loads only when a <see cref="GitPatch"/> is materialized — i.e. when
/// <see cref="EnsureCreatedAsync"/> runs (triggered by hunk/line/stats/print access).
/// </para>
/// <para>
/// <b>State machine</b>: <c>Initialized → Loaded → Created</c>. Matches the C
/// flag sequence <c>INITIALIZED → LOADED → DIFFED</c> with
/// <c>patch_generated_diffable</c> gating.
/// </para>
/// </remarks>
internal sealed class PatchGenerator : IPatchSource
{
    private readonly GitRepository _repo;
    private readonly GitRepository? _patchRepo;
    private readonly GitDiffDelta _delta;
    private readonly DiffFileContent _ofile;
    private readonly DiffFileContent _nfile;
    private readonly GitDiffOptions _opts;
    private readonly DiffDriver _driver;
    private readonly bool _ownContent;
    private readonly string _oldPrefix;
    private readonly string _newPrefix;

    private bool _loaded;
    private bool _created;
    private bool _diffable;
    private bool _binary;

    private readonly List<GitDiffHunk> _hunks = [];
    private readonly List<GitDiffLine> _lines = [];
    private GitBinaryPatch? _binaryPatch;

    private PatchGenerator(
        GitRepository repo, GitDiffDelta delta,
        DiffFileContent ofile, DiffFileContent nfile,
        GitDiffOptions opts, DiffDriver driver, bool ownContent,
        string oldPrefix, string newPrefix, GitRepository? patchRepo)
    {
        _repo = repo;
        _patchRepo = patchRepo;
        _delta = delta;
        _ofile = ofile;
        _nfile = nfile;
        _opts = opts;
        _driver = driver;
        _ownContent = ownContent;
        _oldPrefix = oldPrefix;
        _newPrefix = newPrefix;
    }

    public GitDiffDelta Delta => _delta;
    internal GitDiffOptions Options => _opts;
    public string OldPrefix => _oldPrefix;
    public string NewPrefix => _newPrefix;
    internal GitBinaryPatch? Binary => _binaryPatch;
    internal bool IsBinary => _binary;
    public int IdAbbrevLength => _opts.IdAbbrevLength;

    /// <summary>
    /// The diff options flags (patch->diff_opts.flags, diff_print.c:106);
    /// used by the printer's diff_print_patch_file skips.
    /// </summary>
    public GitDiffOptionsFlags DiffFlags => _opts.Flags;

    /// <summary>
    /// The patch's owning repository for <c>id_abbrev == 0</c> resolution.
    /// Mirrors <c>git_patch::repo</c>: the diff's repo for generated patches,
    /// the blob owner for blob patches, and NULL for buffer patches
    /// (<c>git_patch_from_buffers</c> leaves patch->repo NULL).
    /// </summary>
    public GitRepository? Repo => _patchRepo;

    /// <summary>
    /// Creates a patch generator for delta <paramref name="idx"/> in the given
    /// diff. Matches <c>git_patch_generated_from_diff</c>
    /// (patch_generate.c:713-764).
    /// </summary>
    internal static async ValueTask<PatchGenerator> FromDiffAsync(DiffGenerator gen, int idx, CancellationToken cancellationToken = default)
    {
        GitDiffDelta delta = gen.Deltas[idx];
        GitDiffOptions opts = gen.Options;

        // C's status switch (git_diff_file_content__init_from_diff,
        // diff_file.c:109-126). The untracked side gets data only under
        // SHOW_UNTRACKED_CONTENT (the old side under REVERSE), and NO data on
        // either side for IGNORED, TYPECHANGE, UNMODIFIED, and CONFLICTED.
        bool reverse = (opts.Flags & GitDiffOptionsFlags.Reverse) != 0;
        bool showUntrackedContent = (opts.Flags & GitDiffOptionsFlags.ShowUntrackedContent) != 0;

        bool oldHasData = delta.Status switch
        {
            GitDeltaStatus.Added => false,
            GitDeltaStatus.Deleted => true,
            GitDeltaStatus.Untracked => reverse && showUntrackedContent,
            GitDeltaStatus.Unreadable or GitDeltaStatus.Modified or GitDeltaStatus.Copied or GitDeltaStatus.Renamed => true,
            _ => false,
        };

        bool newHasData = delta.Status switch
        {
            GitDeltaStatus.Added => true,
            GitDeltaStatus.Deleted => false,
            GitDeltaStatus.Untracked => !reverse && showUntrackedContent,
            GitDeltaStatus.Unreadable or GitDeltaStatus.Modified or GitDeltaStatus.Copied or GitDeltaStatus.Renamed => true,
            _ => false,
        };

        DiffDriver driver = await gen.DriverRegistry.LookupAsync(delta.NewFile.Path ?? delta.OldFile.Path ?? default, cancellationToken).ConfigureAwait(false);

        var ofile = DiffFileContent.FromDiff(gen.Repo, delta.OldFile, gen.OldSrc, opts, driver, oldHasData);
        var nfile = DiffFileContent.FromDiff(gen.Repo, delta.NewFile, gen.NewSrc, opts, driver, newHasData);

        // Update binary flags from delta + driver.
        UpdateBinaryFlags(delta, ofile, nfile);

        return new PatchGenerator(gen.Repo, delta, ofile, nfile, opts, driver, ownContent: false,
            gen.OldPrefix, gen.NewPrefix, patchRepo: gen.Repo);
    }

    /// <summary>
    /// Resolves output prefixes from <paramref name="opts"/>: defaults to
    /// "a/"/"b/", swapped under <see cref="GitDiffOptionsFlags.Reverse"/> (matching
    /// diff_generated_apply_options:593-596). Used by blob/buffer patch
    /// factories which have no generator/config processing.
    /// </summary>
    private static (string oldPrefix, string newPrefix) ResolvePrefixes(GitDiffOptions opts)
    {
        string oldPrefix = opts.OldPrefix ?? "a/";
        string newPrefix = opts.NewPrefix ?? "b/";
        if ((opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            (oldPrefix, newPrefix) = (newPrefix, oldPrefix);
        }

        return (oldPrefix, newPrefix);
    }

    /// <summary>
    /// Creates a patch generator for two blobs. Matches
    /// <c>git_patch_from_blobs</c> (patch_generate.c:622-635) →
    /// <c>patch_from_sources</c>. With no paths
    /// supplied, both sides default to "file" (patch_generate.c:490-497).
    /// </summary>
    internal static PatchGenerator FromBlobs(
        GitRepository repo, GitBlob? oldBlob, GitBlob? newBlob, GitDiffOptions opts,
        string? oldPath, string? newPath, GitDiffDrivers drivers)
    {
        DiffDriver driver = drivers.Auto;
        string path = newPath ?? oldPath ?? "file";
        var ofile = DiffFileContent.FromBlob(repo, oldBlob, opts, driver, oldPath ?? path);
        var nfile = DiffFileContent.FromBlob(repo, newBlob, opts, driver, newPath ?? path);
        // GIT_DIFF_REVERSE swaps the two sides (content + status + prefixes),
        // matching git_patch_from_blobs → diff_from_sources iterator swap.
        if ((opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            (ofile, nfile) = (nfile, ofile);
        }

        GitDiffDelta delta = BuildStandaloneDelta(ofile.File, nfile.File);
        UpdateBinaryFlags(delta, ofile, nfile);
        (string? oldPrefix, string? newPrefix) = ResolvePrefixes(opts);
        return new PatchGenerator(repo, delta, ofile, nfile, opts, driver, ownContent: true, oldPrefix, newPrefix, patchRepo: repo);
    }

    /// <summary>
    /// Creates a patch generator for a blob vs a buffer. Matches
    /// <c>git_patch_from_blob_and_buffer</c> (patch_generate.c:658-672).
    /// </summary>
    internal static PatchGenerator FromBlobAndBuffer(
        GitRepository repo, GitBlob? oldBlob, ReadOnlyMemory<byte> newBuffer,
        GitDiffOptions opts, string? oldPath, string? newPath, GitDiffDrivers drivers)
    {
        DiffDriver driver = drivers.Auto;
        // C's default path is "file" (patch_generate.c:490-497), not
        // "blob".
        string path = newPath ?? oldPath ?? "file";
        var ofile = DiffFileContent.FromBlob(repo, oldBlob, opts, driver, oldPath ?? path);
        var nfile = DiffFileContent.FromBuffer(repo, newBuffer, opts, driver, newPath ?? path);
        if ((opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            (ofile, nfile) = (nfile, ofile);
        }

        GitDiffDelta delta = BuildStandaloneDelta(ofile.File, nfile.File);
        UpdateBinaryFlags(delta, ofile, nfile);
        (string? oldPrefix, string? newPrefix) = ResolvePrefixes(opts);
        return new PatchGenerator(repo, delta, ofile, nfile, opts, driver, ownContent: true, oldPrefix, newPrefix, patchRepo: repo);
    }

    /// <summary>
    /// Creates a patch generator for two raw buffers. Matches
    /// <c>git_patch_from_buffers</c> (patch_generate.c:696-711).
    /// </summary>
    internal static PatchGenerator FromBuffers(
        GitRepository repo, ReadOnlyMemory<byte> oldBuffer, ReadOnlyMemory<byte> newBuffer,
        GitDiffOptions opts, string? oldPath, string? newPath, GitDiffDrivers drivers)
    {
        DiffDriver driver = drivers.Auto;
        // C's default path is "file" (patch_generate.c:490-497), not
        // "buffer".
        string path = newPath ?? oldPath ?? "file";
        var ofile = DiffFileContent.FromBuffer(repo, oldBuffer, opts, driver, oldPath ?? path);
        var nfile = DiffFileContent.FromBuffer(repo, newBuffer, opts, driver, newPath ?? path);
        if ((opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            (ofile, nfile) = (nfile, ofile);
        }

        GitDiffDelta delta = BuildStandaloneDelta(ofile.File, nfile.File);
        UpdateBinaryFlags(delta, ofile, nfile);
        (string? oldPrefix, string? newPrefix) = ResolvePrefixes(opts);
        // C (patch_generate.c:696-711): git_patch_from_buffers never sets
        // patch->repo — id_abbrev == 0 falls back to GIT_ABBREV_DEFAULT (7)
        // instead of consulting core.abbrev.
        return new PatchGenerator(repo, delta, ofile, nfile, opts, driver, ownContent: true, oldPrefix, newPrefix, patchRepo: null);
    }

    /// <summary>
    /// Ensures the patch content has been loaded and the diff generated.
    /// Matches the <c>patch_generated_load</c> → <c>patch_generated_create</c>
    /// sequence (patch_generate.c:207-256, 379-410).
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (_created)
        {
            return;
        }

        await LoadContentAsync(cancellationToken).ConfigureAwait(false);
        Generate();
    }

    /// <summary>
    /// Loads both sides' content. Matches <c>patch_generated_load</c>
    /// (patch_generate.c:207-256).
    /// </summary>
    private async Task LoadContentAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        // Always load both sides. For buffers, Load() is a no-op (already
        // loaded); for blobs/workdir, it reads content + runs binary detection.
        await _ofile.LoadAsync(_opts, cancellationToken).ConfigureAwait(false);
        await _nfile.LoadAsync(_opts, cancellationToken).ConfigureAwait(false);

        // After load, re-check diffable/binary.
        UpdateBinaryFlags(_delta, _ofile, _nfile);
        _binary = (_delta.Flags & GitDiffFileFlags.Binary) != 0;
        _diffable = ComputeDiffable();
        _loaded = true;
    }

    /// <summary>
    /// Determines whether this delta should produce diff output. Matches
    /// <c>patch_generated_diffable</c> (patch_generate.c:178-205).
    /// </summary>
    private bool ComputeDiffable()
    {
        // C (patch_generate.c:181-183): an UNMODIFIED delta is never diffable — UNCONDITIONAL, even when INCLUDE_UNMODIFIED is set, so the
        // standalone patch is simply empty.
        if (_delta.Status == GitDeltaStatus.Unmodified)
        {
            return false;
        }

        // C (patch_generate.c:185-194): when the delta is binary and
        // SHOW_BINARY is not set, the content was never loaded — compare the
        // FILE sizes; otherwise use the loaded content lengths.
        bool binarySkip = (_delta.Flags & GitDiffFileFlags.Binary) != 0 &&
            (_opts.Flags & GitDiffOptionsFlags.ShowBinary) == 0;
        long olen = binarySkip ? _ofile.File.Size : _ofile.Data.Length;
        long nlen = binarySkip ? _nfile.File.Size : _nfile.Data.Length;

        // C (patch_generate.c:196-198): both sides empty → identical.
        if (olen == 0 && nlen == 0)
        {
            return false;
        }

        // C (patch_generate.c:200-204): identical size AND identical OID → identical content (e.g. a mode-only MODIFIED delta) — not diffable, so
        // no XDiff runs.
        return olen != nlen || !_ofile.File.Id.Equals(_nfile.File.Id);
    }

    /// <summary>
    /// Generates the diff (text or binary). Matches <c>patch_generated_create</c>
    /// (patch_generate.c:379-410) + <c>diff_binary</c> (339-377).
    /// </summary>
    private void Generate()
    {
        _created = true;

        if (!_diffable)
        {
            return;
        }

        // Binary path: produce BinaryPatch data (if SHOW_BINARY) or noshow.
        if (_binary)
        {
            if ((_opts.Flags & GitDiffOptionsFlags.ShowBinary) != 0)
            {
                _binaryPatch = CreateBinary();
            }

            return;
        }

        // Text path: run XDiff and accumulate hunks + lines. The bridge
        // populates each hunk's Lines in emission order; mirror them into the
        // flat patch-level list (C: patch->lines) in the same order.
        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = _driver.GetFunctionNameExtractor();

        XdiffBridge.Compute(_hunks, _ofile.Data, _nfile.Data, _opts, extractor);

        foreach (GitDiffHunk hunk in _hunks)
        {
            _lines.AddRange(hunk.Lines);
        }
    }

    /// <summary>
    /// Creates binary patch data. Matches <c>diff_binary</c> (339-377) +
    /// <c>create_binary</c> (272-337).
    /// </summary>
    private GitBinaryPatch CreateBinary()
    {
        ReadOnlyMemory<byte> oldData = _ofile.Data;
        ReadOnlyMemory<byte> newData = _nfile.Data;

        // old→new direction (stored as "new_file" in git_diff_binary)
        (GitBinaryPatchType newType, byte[]? newDataDeflated, long newInflated) = CreateBinarySide(newData, oldData);
        // new→old direction (stored as "old_file")
        (GitBinaryPatchType oldType, byte[]? oldDataDeflated, long oldInflated) = CreateBinarySide(oldData, newData);

        return new GitBinaryPatch
        {
            ContainsData = true,
            NewFile = new GitBinaryFile
            {
                Type = newType,
                Data = newDataDeflated,
                InflatedLength = newInflated,
            },
            OldFile = new GitBinaryFile
            {
                Type = oldType,
                Data = oldDataDeflated,
                InflatedLength = oldInflated,
            },
        };
    }

    /// <summary>
    /// Creates one side of the binary diff: deflate the target, optionally
    /// delta-encode against the source, pick the smaller. Matches
    /// <c>create_binary</c> (patch_generate.c:272-337).
    /// </summary>
    private static (GitBinaryPatchType type, byte[] data, long inflated) CreateBinarySide(
        ReadOnlyMemory<byte> target, ReadOnlyMemory<byte> source)
    {
        // Literal: deflate the target directly.
        using var literalDeflated = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(literalDeflated, target.Span);
        long inflatedLen = target.Length;

        // Delta: only if both sides have data.
        if (!source.IsEmpty && !target.IsEmpty)
        {
            byte[]? deltaRaw = DeltaEncoder.Create(source, target, maxDeltaSize: 0);
            if (deltaRaw is { Length: > 0 })
            {
                using var deltaDeflated = new PooledByteBufferWriter();
                Zlib.CompressLooseObject(deltaDeflated, deltaRaw);

                // Pick the smaller of delta vs literal.
                if (deltaDeflated.WrittenCount < literalDeflated.WrittenCount)
                {
                    // ToArray: the deflated bytes are the owning GitBinaryFile.Data
                    // (public byte[]); the writer only pooled the intermediate.
                    return (GitBinaryPatchType.Delta, deltaDeflated.WrittenSpan.ToArray(), deltaRaw.Length);
                }
            }
        }

        // ToArray: the deflated bytes are the owning GitBinaryFile.Data
        // (public byte[]); the writer only pooled the intermediate.
        return (GitBinaryPatchType.Literal, literalDeflated.WrittenSpan.ToArray(), inflatedLen);
    }

    /// <summary>
    /// Updates binary flags on the delta + content wrappers. Matches
    /// <c>patch_generated_update_binary</c> (patch_generate.c:52-68).
    /// </summary>
    private static void UpdateBinaryFlags(GitDiffDelta delta, DiffFileContent ofile, DiffFileContent nfile)
    {
        // C (patch_generate.c:54-56): DIFF_FLAGS_KNOWN_BINARY (BINARY or
        // NOT_BINARY) short-circuits.
        if ((delta.Flags & (GitDiffFileFlags.Binary | GitDiffFileFlags.NotBinary)) != 0)
        {
            return;
        }

        if ((ofile.File.Flags & GitDiffFileFlags.Binary) != 0 ||
            (nfile.File.Flags & GitDiffFileFlags.Binary) != 0)
        {
            delta.Flags |= GitDiffFileFlags.Binary;
            return;
        }

        // C marks the
        // delta binary when either FILE size exceeds GIT_XDIFF_MAX_SIZE
        // (1024*1024*1023, diff_xdiff.h:19), even with opts.MaxSize raised
        // above ~1 GiB — otherwise a >1 GiB file would be treated as text and
        // fed to XDiff (huge allocation/OOM) where C treats it as binary (and
        // old/new_data would error 'files too large for diff',
        // patch_generate.c:772-785).
        if (ofile.File.Size > GitXdiffMaxSize || nfile.File.Size > GitXdiffMaxSize)
        {
            delta.Flags |= GitDiffFileFlags.Binary;
            return;
        }

        if ((ofile.File.Flags & GitDiffFileFlags.NotBinary) != 0 &&
            (nfile.File.Flags & GitDiffFileFlags.NotBinary) != 0)
        {
            delta.Flags |= GitDiffFileFlags.NotBinary;
        }
    }

    /// <summary>Matches <c>GIT_XDIFF_MAX_SIZE</c> (diff_xdiff.h:19).</summary>
    private const long GitXdiffMaxSize = 1024L * 1024 * 1023;

    /// <summary>
    /// Builds a delta for standalone (non-diff) patches (blob/buffer paths).
    /// Matches the delta allocation in <c>patch_generated_with_delta_alloc</c>
    /// (patch_generate.c:511-543).
    /// </summary>
    private static GitDiffDelta BuildStandaloneDelta(GitDiffFile oldFile, GitDiffFile newFile)
    {
        GitDeltaStatus status;
        if (oldFile.Id.IsZero && !newFile.Id.IsZero)
        {
            status = GitDeltaStatus.Added;
        }
        else if (!oldFile.Id.IsZero && newFile.Id.IsZero)
        {
            status = GitDeltaStatus.Deleted;
        }
        else if (oldFile.Id == newFile.Id)
        {
            status = GitDeltaStatus.Unmodified;
        }
        else
        {
            status = GitDeltaStatus.Modified;
        }

        return new GitDiffDelta(status, 2, oldFile, newFile);
    }

    // ━━ Accessors ━━

    public async Task<IReadOnlyList<GitDiffHunk>> GetHunksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return _hunks;
    }

    internal async Task<IReadOnlyList<GitDiffLine>> GetLinesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return _lines;
    }

    public async Task<GitBinaryPatch?> GetBinaryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return _binaryPatch;
    }

    public async Task<bool> GetIsBinaryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return _binary;
    }

    /// <summary>
    /// Computes context/addition/deletion counts. Matches
    /// <c>git_patch_line_stats</c> (patch.c:93-128).
    /// </summary>
    public async Task<(int context, int additions, int deletions)> LineStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        int context = 0, additions = 0, deletions = 0;
        foreach (GitDiffLine line in _lines)
        {
            switch (line.Origin)
            {
                case GitDiffLineOrigin.Context:
                    context++;
                    break;
                case GitDiffLineOrigin.Addition:
                    additions++;
                    break;
                case GitDiffLineOrigin.Deletion:
                    deletions++;
                    break;
                // EOFNL markers ("\ No newline at end of file") are not counted; matches
                // git_patch_line_stats (patch.c:93-128), which skips the *_EOFNL origins.
                case GitDiffLineOrigin.ContextEofnl:
                case GitDiffLineOrigin.AddEofnl:
                case GitDiffLineOrigin.DelEofnl:
                    break;
            }
        }

        return (context, additions, deletions);
    }
}

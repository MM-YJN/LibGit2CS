// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Globalization;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary>
/// Diff output printer. Managed port of <c>src/libgit2/diff_print.c</c> (905 LOC).
/// </summary>
/// <remarks>
/// <para>
/// Implements <c>git_diff_print</c> (diff_print.c:734-790): format dispatch →
/// per-delta callbacks. Five formats: <see cref="GitDiffPrintFormat.Patch"/>,
/// <see cref="GitDiffPrintFormat.Raw"/>, <see cref="GitDiffPrintFormat.NameOnly"/>,
/// <see cref="GitDiffPrintFormat.NameStatus"/>,
/// <see cref="GitDiffPrintFormat.PatchHeader"/>.
/// </para>
/// <para>
/// <b>Deferred header mechanism</b>: the file header is
/// generated but NOT immediately emitted. It is flushed only when the first
/// hunk or binary body arrives (or when forced for rename/mode-change). This
/// prevents dangling headers on deltas whose hunks are all filtered out.
/// </para>
/// </remarks>
internal static class DiffPrinter
{
    /// <summary>
    /// C (diff_print.c:626-632, diff_print_patch_file): the patch printers
    /// skip deltas whose new side is a directory, or whose status is
    /// UNMODIFIED / IGNORED / UNREADABLE, or UNTRACKED without
    /// SHOW_UNTRACKED_CONTENT.
    /// </summary>
    private static bool ShouldSkipPatchPrint(GitDiffDelta delta, GitDiffOptionsFlags flags)
        => delta.NewFile.Mode == LibGit2CS.Objects.GitFileMode.Tree
           || delta.Status is GitDeltaStatus.Unmodified or GitDeltaStatus.Ignored or GitDeltaStatus.Unreadable
           || (delta.Status == GitDeltaStatus.Untracked && (flags & GitDiffOptionsFlags.ShowUntrackedContent) == 0);

    /// <summary>
    /// Prints a diff in the given format. Matches <c>git_diff_print</c>
    /// (diff_print.c:734-790).
    /// </summary>
    public static async Task PrintAsync(GitDiff diff, GitDiffPrintFormat format, GitDiffPrintCallback callback, CancellationToken cancellationToken)
    {
        // C (diff_print.c:52-63, diff_print_info_init__common): id_abbrev == 0
        // resolves against core.abbrev via git_repository__abbrev_length when
        // the diff has a repo, else GIT_ABBREV_DEFAULT (7).
        int idAbbrev = await ResolveIdAbbrevAsync(diff.Options.IdAbbrevLength, diff.Repo, cancellationToken).ConfigureAwait(false);

        switch (format)
        {
            case GitDiffPrintFormat.Patch:
                await PrintPatchAsync(diff, idAbbrev, callback, cancellationToken).ConfigureAwait(false);
                break;
            case GitDiffPrintFormat.PatchHeader:
                PrintPatchHeader(diff, idAbbrev, callback);
                break;
            case GitDiffPrintFormat.Raw:
                foreach (GitDiffDelta delta in diff.Deltas)
                {
                    PrintOneRaw(delta, idAbbrev, diff.Options.Flags, callback);
                }

                break;
            case GitDiffPrintFormat.NameOnly:
                foreach (GitDiffDelta delta in diff.Deltas)
                {
                    PrintOneNameOnly(delta, diff.Options.Flags, callback);
                }

                break;
            case GitDiffPrintFormat.NameStatus:
                foreach (GitDiffDelta delta in diff.Deltas)
                {
                    PrintOneNameStatus(delta, diff.Options.Flags, callback);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Resolves an <c>opts.id_abbrev</c> value like
    /// <c>diff_print_info_init__common</c> (diff_print.c:52-63): 0 consults
    /// <c>core.abbrev</c> when a repository is present, else 7; any other
    /// value is used as-is.
    /// </summary>
    private static async Task<int> ResolveIdAbbrevAsync(int idAbbrev, GitRepository? repo, CancellationToken cancellationToken)
    {
        return idAbbrev != 0
            ? idAbbrev
            : await ResolveRepoAbbrevAsync(repo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>git_repository__abbrev_length</c> (repository.c:3999-4019):
    /// reads <c>core.abbrev</c> (default 7), errors below
    /// <c>GIT_ABBREV_MINIMUM</c> (4), and clamps <c>false</c>/over-long values
    /// to the repository's full OID hex size.
    /// </summary>
    private static async Task<int> ResolveRepoAbbrevAsync(GitRepository? repo, CancellationToken cancellationToken)
    {
        if (repo is null)
        {
            return GitConfigMaps.AbbrevDefault;
        }

        int len = await repo.Config.GetMappedAsync(
            "core.abbrev", GitConfigMaps.AbbrevMap, GitConfigMaps.AbbrevDefault, cancellationToken).ConfigureAwait(false);

        if (len < GitConfigMaps.AbbrevMinimum)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"invalid oid abbreviation setting: '{len}'",
                GitErrorCategory.Config);
        }

        int hexSize = GitOid.HexSizeFor(repo.ObjectFormat);
        if (len == GitConfigMaps.AbbrevFalse || len > hexSize)
        {
            len = hexSize;
        }

        return len;
    }

    /// <summary>
    /// Prints a single patch (already materialized). Matches
    /// <c>git_patch_print</c> (diff_print.c:866-891) — replays accumulated
    /// hunks/lines through the callback.
    /// </summary>
    public static async Task PrintPatchAsync(GitPatch patch, GitDiffPrintCallback callback, CancellationToken cancellationToken)
    {
        GitDiffDelta delta = patch.Delta;

        // C diff_print_patch_file (diff_print.c:626-632), as invoked by
        // git_patch_print: skip dir/unmodified/ignored/unreadable/
        // untracked-without-content deltas entirely.
        if (ShouldSkipPatchPrint(delta, patch.DiffFlags))
        {
            return;
        }

        using var headerBuf = new PooledByteBufferWriter();
        bool sentFileHeader = false;

        using PooledByteBufferWriter oldPrefix = EncodeDiffPrefix(patch.OldPrefix, nameof(patch.OldPrefix));
        using PooledByteBufferWriter newPrefix = EncodeDiffPrefix(patch.NewPrefix, nameof(patch.NewPrefix));

        // C (diff_print.c:95-113, diff_print_info_init_frompatch +
        // __common): id_abbrev == 0 resolves via patch->repo's core.abbrev.
        int idAbbrev = await ResolveIdAbbrevAsync(patch.IdAbbrevLength, patch.Repo, cancellationToken).ConfigureAwait(false);

        // Generate and emit the file header.
        FormatFileHeader(headerBuf, delta, idAbbrev, oldPrefix.WrittenSpan, newPrefix.WrittenSpan, printIndex: true);
        FlushHeader(delta, headerBuf, callback, ref sentFileHeader);

        // git_patch_print
        // routes through diff_print_patch_binary (diff_print.c:882-885) — a
        // binary patch emits the "GIT binary patch" body (SHOW_BINARY) or the
        // "Binary files differ" line, not just the header.
        if (await patch.GetIsBinaryAsync(cancellationToken).ConfigureAwait(false))
        {
            bool showBinary = (patch.DiffFlags & GitDiffOptionsFlags.ShowBinary) != 0;
            await EmitBinaryBodyAsync(patch, delta, showBinary, oldPrefix.WrittenMemory, newPrefix.WrittenMemory, callback, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Emit hunks and lines.
        int hunkCount = await patch.GetHunkCountAsync(cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < hunkCount; i++)
        {
            GitDiffHunk hunk = (await patch.GetHunkAsync(i, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException($"hunk {i} not found");
            callback(delta, hunk, new GitDiffLine(
                GitDiffLineOrigin.HunkHeader, 0, 0, 0,
                hunk.Header));

            foreach (GitDiffLine line in hunk.Lines)
            {
                callback(delta, hunk, line);
            }
        }
    }

    // ━━ Patch format (full unified diff) ━━

    private static async Task PrintPatchAsync(GitDiff diff, int idAbbrev, GitDiffPrintCallback callback, CancellationToken cancellationToken)
    {
        using var headerBuf = new PooledByteBufferWriter();

        using PooledByteBufferWriter oldPrefix = EncodeDiffPrefix(diff.OldPrefix, nameof(diff.OldPrefix));
        using PooledByteBufferWriter newPrefix = EncodeDiffPrefix(diff.NewPrefix, nameof(diff.NewPrefix));

        bool showBinary = (diff.Options.Flags & GitDiffOptionsFlags.ShowBinary) != 0;

        for (int idx = 0; idx < diff.DeltaCount; idx++)
        {
            GitDiffDelta delta = diff.GetDelta(idx);

            // C diff_print_patch_file (diff_print.c:626-632): skip
            // dir/unmodified/ignored/unreadable/untracked-without-content
            // deltas entirely.
            if (ShouldSkipPatchPrint(delta, diff.Options.Flags))
            {
                continue;
            }

            bool sentFileHeader = false;
            headerBuf.Clear();

            // Materialize the patch FIRST. Accessing IsBinary triggers
            // PatchGenerator.EnsureCreated which loads content and sets
            // DiffFileFlags.Binary on the delta — git_diff_foreach does the
            // same (git_patch_from_diff runs before the file/binary/hunk
            // callbacks fire).
            using GitPatch patch = await GitPatch.FromDiffAsync(diff, idx, cancellationToken).ConfigureAwait(false);
            bool isBinary = await patch.GetIsBinaryAsync(cancellationToken).ConfigureAwait(false);

            // diff_print.c:619-620 — when binary && show_binary, use the
            // delta's per-file id_abbrev (full OID) instead of opts.id_abbrev.
            int headerIdAbbrev = idAbbrev;
            if (isBinary && showBinary)
            {
                headerIdAbbrev = delta.OldFile.IdAbbrev > 0
                    ? delta.OldFile.IdAbbrev
                    : delta.NewFile.IdAbbrev;
            }

            // Generate the file header into the buffer (deferred). Generated
            // AFTER materialization so the delta's binary flag is current; the
            // header suppresses ---/+++ for binary deltas.
            FormatFileHeader(headerBuf, delta, headerIdAbbrev, oldPrefix.WrittenSpan, newPrefix.WrittenSpan, printIndex: true);

            // Force flush for renames/mode-changes.
            if (ShouldForceHeader(delta))
            {
                FlushHeader(delta, headerBuf, callback, ref sentFileHeader);
            }

            // Binary path: emit either the full binary patch (SHOW_BINARY) or
            // the noshow "Binary files differ" line. Matches
            // diff_print_patch_file_binary (diff_print.c:544-572).
            if (isBinary)
            {
                FlushHeader(delta, headerBuf, callback, ref sentFileHeader);
                await EmitBinaryBodyAsync(patch, delta, showBinary, oldPrefix.WrittenMemory, newPrefix.WrittenMemory, callback, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Emit hunks.
            int hunkCount = await patch.GetHunkCountAsync(cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < hunkCount; i++)
            {
                GitDiffHunk hunk = (await patch.GetHunkAsync(i, cancellationToken).ConfigureAwait(false))!;

                // Flush deferred header before first hunk.
                FlushHeader(delta, headerBuf, callback, ref sentFileHeader);

                callback(delta, hunk, new GitDiffLine(
                    GitDiffLineOrigin.HunkHeader, 0, 0, 0,
                    hunk.Header));

                foreach (GitDiffLine line in hunk.Lines)
                {
                    callback(delta, hunk, line);
                }
            }

            // If no hunks and header wasn't flushed, skip (no output for
            // unmodified/empty deltas). This is the deferred-header behavior.
        }
    }

    private static void PrintPatchHeader(GitDiff diff, int idAbbrev, GitDiffPrintCallback callback)
    {
        using PooledByteBufferWriter oldPrefix = EncodeDiffPrefix(diff.OldPrefix, nameof(diff.OldPrefix));
        using PooledByteBufferWriter newPrefix = EncodeDiffPrefix(diff.NewPrefix, nameof(diff.NewPrefix));

        foreach (GitDiffDelta delta in diff.Deltas)
        {
            // C's PATCH_HEADER format uses diff_print_patch_file (diff_print.c:626-632)
            // — same skip as the full patch printer.
            if (ShouldSkipPatchPrint(delta, diff.Options.Flags))
            {
                continue;
            }

            using var buf = new PooledByteBufferWriter();
            FormatFileHeader(buf, delta, idAbbrev, oldPrefix.WrittenSpan, newPrefix.WrittenSpan, printIndex: true);
            if (buf.WrittenCount > 0)
            {
                callback(delta, null, new GitDiffLine(
                    GitDiffLineOrigin.FileHeader, 0, 0, 0,
                    buf.WrittenSpan.ToArray()));
            }
        }
    }

    /// <summary>
    /// Patch-id print path. Matches <c>git_diff_print</c> with
    /// <c>GIT_DIFF_FORMAT_PATCH_ID</c> (diff_print.c:755-759): same as patch
    /// format but the <c>index &lt;oid&gt;..&lt;oid&gt;</c> line is suppressed
    /// (<c>print_index = false</c> at diff_print.c:618). Used by
    /// <see cref="GitPatchId.ComputeAsync"/>.
    /// </summary>
    internal static async Task PrintPatchIdAsync(GitDiff diff, GitDiffPrintCallback callback, CancellationToken cancellationToken)
    {
        using var headerBuf = new PooledByteBufferWriter();
        int idAbbrev = await ResolveIdAbbrevAsync(diff.Options.IdAbbrevLength, diff.Repo, cancellationToken).ConfigureAwait(false);

        using PooledByteBufferWriter oldPrefix = EncodeDiffPrefix(diff.OldPrefix, nameof(diff.OldPrefix));
        using PooledByteBufferWriter newPrefix = EncodeDiffPrefix(diff.NewPrefix, nameof(diff.NewPrefix));

        for (int idx = 0; idx < diff.DeltaCount; idx++)
        {
            GitDiffDelta delta = diff.GetDelta(idx);

            // C's PATCH_ID format uses diff_print_patch_file (diff_print.c:626-632)
            // — same skip as the full patch printer.
            if (ShouldSkipPatchPrint(delta, diff.Options.Flags))
            {
                continue;
            }

            bool sentFileHeader = false;
            headerBuf.Clear();

            using GitPatch patch = await GitPatch.FromDiffAsync(diff, idx, cancellationToken).ConfigureAwait(false);
            bool isBinary = await patch.GetIsBinaryAsync(cancellationToken).ConfigureAwait(false);

            FormatFileHeader(headerBuf, delta, idAbbrev, oldPrefix.WrittenSpan, newPrefix.WrittenSpan, printIndex: false);

            if (ShouldForceHeader(delta))
            {
                FlushHeader(delta, headerBuf, callback, ref sentFileHeader);
            }

            // Binary deltas in patch-id mode: emit the "Binary files differ"
            // line (no binary body — git patch-id doesn't hash binary content).
            // Matches diff_print_patch_file_binary (diff_print.c:544-572) as
            // called by the PATCH_ID path (no print_hunk callback registered).
            if (isBinary)
            {
                FlushHeader(delta, headerBuf, callback, ref sentFileHeader);
                using var binaryBuf = new PooledByteBufferWriter();
                binaryBuf.Write("Binary files "u8);
                if (delta.OldFile.Id.IsZero)
                {
                    binaryBuf.Write("/dev/null"u8);
                }
                else
                {
                    AppendQuotedPath(binaryBuf, oldPrefix.WrittenSpan, (delta.OldFile.Path ?? default).Span);
                }

                binaryBuf.Write(" and "u8);
                if (delta.NewFile.Id.IsZero)
                {
                    binaryBuf.Write("/dev/null"u8);
                }
                else
                {
                    AppendQuotedPath(binaryBuf, newPrefix.WrittenSpan, (delta.NewFile.Path ?? default).Span);
                }

                binaryBuf.Write(" differ\n"u8);
                callback(delta, null, new GitDiffLine(
                    GitDiffLineOrigin.Binary, 0, 0, 0,
                    binaryBuf.WrittenSpan.ToArray()));
                continue;
            }

            int hunkCount = await patch.GetHunkCountAsync(cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < hunkCount; i++)
            {
                GitDiffHunk hunk = (await patch.GetHunkAsync(i, cancellationToken).ConfigureAwait(false))!;
                FlushHeader(delta, headerBuf, callback, ref sentFileHeader);

                // PATCH_ID format: hunk_cb is NULL (diff_print.c:755-759 sets
                // print_hunk = NULL). The @@ hunk header line is NOT emitted;
                // only the content lines (context/addition/deletion) are.
                foreach (GitDiffLine line in hunk.Lines)
                {
                    callback(delta, hunk, line);
                }
            }
        }
    }

    // ━━ Per-delta printers ━━

    private static void PrintOneNameOnly(GitDiffDelta delta, GitDiffOptionsFlags flags, GitDiffPrintCallback callback)
    {
        // C (diff_print.c:154-156): unmodified deltas are skipped unless
        // SHOW_UNMODIFIED.
        if ((flags & GitDiffOptionsFlags.ShowUnmodified) == 0 && delta.Status == GitDeltaStatus.Unmodified)
        {
            return;
        }

        // C (diff_print.c:159-160): the path is printed as raw bytes — no decode/re-encode round-trip.
        GitPath path = delta.NewFile.Path ?? delta.OldFile.Path ?? default;
        using var buf = new PooledByteBufferWriter();
        buf.Write(path.Span);
        buf.Write((byte)'\n');
        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.FileHeader, 0, 0, 0,
            buf.WrittenSpan.ToArray()));
    }

    private static void PrintOneNameStatus(GitDiffDelta delta, GitDiffOptionsFlags flags, GitDiffPrintCallback callback)
    {
        // C (diff_print.c:182-186): deltas whose status char is ' ' — that is Unmodified AND Conflicted (git_diff_status_char has no CONFLICTED arm) — are
        // skipped unless SHOW_UNMODIFIED.
        if ((flags & GitDiffOptionsFlags.ShowUnmodified) == 0 && DeltaStatusChar(delta.Status) == ' ')
        {
            return;
        }

        // C (diff_print.c:115-124, diff_pick_suffix): '/' for directories,
        // '*' for executable modes (GIT_PERMS_IS_EXEC), ' ' otherwise.
        static char PickSuffix(Objects.GitFileMode mode)
        {
            if ((int)mode == 0x4000)
            {
                return '/';
            }

            if (((int)mode & 0b1001001) != 0)
            {
                return '*';
            }

            return ' ';
        }

        char oldSuffix = PickSuffix(delta.OldFile.Mode);
        char newSuffix = PickSuffix(delta.NewFile.Mode);
        char statusChar = DeltaStatusChar(delta.Status);

        GitPath oldPath = delta.OldFile.Path ?? default;
        GitPath newPath = delta.NewFile.Path ?? default;

        // C (diff_print.c:194): the dual-path decision compares the path POINTERS — generated deltas share one pooled path string (one path printed), while a
        // PARSED diff allocates a fresh buffer per side, so both paths are printed even when the text is equal. The port mirrors this with buffer identity,
        // then falls back to a byte-value compare.
        bool samePath = oldPath.SharesBufferWith(newPath) || GitPath.Compare(oldPath, newPath) == 0;

        using var buf = new PooledByteBufferWriter();
        buf.Write((byte)statusChar);
        buf.Write((byte)'\t');

        if (!samePath)
        {
            // C (diff_print.c:194-199): a mode-change delta with both modes
            // nonzero prints both paths with their suffixes.
            buf.Write(oldPath.Span);
            buf.Write((byte)oldSuffix);
            buf.Write((byte)' ');
            buf.Write(newPath.Span);
            buf.Write((byte)newSuffix);
        }
        else if (delta.OldFile.Mode != delta.NewFile.Mode &&
                 (int)delta.OldFile.Mode != 0 && (int)delta.NewFile.Mode != 0)
        {
            buf.Write(oldPath.Span);
            buf.Write((byte)oldSuffix);
            buf.Write((byte)' ');
            buf.Write(newPath.Span);
            buf.Write((byte)newSuffix);
        }
        else if (oldSuffix != ' ')
        {
            buf.Write(oldPath.Span);
            buf.Write((byte)oldSuffix);
        }
        else
        {
            buf.Write(oldPath.Span);
        }

        buf.Write((byte)'\n');

        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.FileHeader, 0, 0, 0,
            buf.WrittenSpan.ToArray()));
    }

    private static void PrintOneRaw(GitDiffDelta delta, int idStrlen, GitDiffOptionsFlags flags, GitDiffPrintCallback callback)
    {
        // C (diff_print.c:226-230): deltas whose status char is ' ' — Unmodified AND Conflicted — are skipped unless SHOW_UNMODIFIED.
        if ((flags & GitDiffOptionsFlags.ShowUnmodified) == 0 && DeltaStatusChar(delta.Status) == ' ')
        {
            return;
        }

        // C (diff_print.c:231-239): the per-file id_abbrev is mode-gated
        // (old_file.mode ? old : new), and requesting more characters than
        // the input carries is a GIT_ERROR_PATCH error.
        int perFileAbbrev = (int)delta.OldFile.Mode != 0
            ? delta.OldFile.IdAbbrev
            : delta.NewFile.IdAbbrev;
        if (idStrlen > perFileAbbrev)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"the patch input contains {perFileAbbrev} id characters (cannot print {idStrlen})",
                GitErrorCategory.Patch);
        }

        // C (diff_print.c:248-257): each OID prints id_strlen characters
        // followed by "..." when 1..hexsize; git_oid_tostr clamps at the
        // full hex size for longer requests.
        int oidHexSize = delta.OldFile.Id.HexSize;
        bool idIsAbbrev = idStrlen > 0 && idStrlen <= oidHexSize;

        using var buf = new PooledByteBufferWriter();
        buf.Write((byte)':');
        buf.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.OldFile.Mode, 6), provider: CultureInfo.InvariantCulture);
        buf.Write((byte)' ');
        buf.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.NewFile.Mode, 6), provider: CultureInfo.InvariantCulture);
        buf.Write((byte)' ');
        WriteOidHexForPrint(buf, delta.OldFile.Id, idStrlen);
        buf.Write(idIsAbbrev ? "... "u8 : " "u8);
        WriteOidHexForPrint(buf, delta.NewFile.Id, idStrlen);
        buf.Write(idIsAbbrev ? "... "u8 : " "u8);

        char statusChar = DeltaStatusChar(delta.Status);
        buf.Write((byte)statusChar);

        // C (diff_print.c:258-259): the similarity is printed as a 3-digit
        // zero-padded field, and ONLY when similarity > 0 (a parsed RENAMED
        // delta without a similarity line prints nothing).
        if (delta.Similarity > 0)
        {
            buf.WriteSpanFormattable(delta.Similarity, "D3", CultureInfo.InvariantCulture);
        }

        buf.Write((byte)'\t');

        // diff_print_one_raw (diff_print.c:261-267): C compares the path POINTERS — generated deltas share one pooled path string (one path printed), while a
        // PARSED diff allocates a fresh buffer per side, so both paths are printed even when the text is equal. The port mirrors this with buffer-identity.
        GitPath? oldFile = delta.OldFile.Path;
        GitPath? newFile = delta.NewFile.Path;
        bool samePath = oldFile is { } oldPath && newFile is { } newPath && oldPath.SharesBufferWith(newPath);
        if (!samePath)
        {
            // C (diff_print.c:262-265): "\t%s %s\n" — a SPACE separates the
            // two paths.
            buf.Write((oldFile ?? default).Span);
            buf.Write((byte)' ');
        }

        buf.Write((newFile ?? default).Span);

        buf.Write((byte)'\n');

        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.FileHeader, 0, 0, 0,
            buf.WrittenSpan.ToArray()));
    }

    // ━━ File header generation ━━

    /// <summary>
    /// Formats the unified diff file header into the buffer. Matches
    /// <c>git_diff_delta__format_file_header</c> (diff_print.c:423-480).
    /// </summary>
    /// <param name="idAbbrev">The configured OID abbreviation length (<c>opts.id_abbrev</c>, default 7). NOT the per-file <c>id_abbrev</c>.</param>
    /// <param name="oldPrefix">The "a/" prefix (after config + reverse swap), ASCII-encoded.</param>
    /// <param name="newPrefix">The "b/" prefix (after config + reverse swap), ASCII-encoded.</param>
    /// <param name="writer">The destination buffer writer.</param>
    /// <param name="delta">The file delta to process.</param>
    /// <param name="printIndex">Whether to include the index header.</param>
    private static void FormatFileHeader(
        PooledByteBufferWriter writer, GitDiffDelta delta, int idAbbrev, ReadOnlySpan<byte> oldPrefix, ReadOnlySpan<byte> newPrefix,
        bool printIndex = true)
    {
        GitPath oldPathValue = delta.OldFile.Path ?? default;
        GitPath newPathValue = delta.NewFile.Path ?? default;

        // C (diff_print.c:438-439): a zero id_strlen falls back to
        // GIT_ABBREV_DEFAULT here (git_patch_size calls this directly with 0).
        if (idAbbrev == 0)
        {
            idAbbrev = GitConfigMaps.AbbrevDefault;
        }

        // "diff --git <oldPrefix>path <newPrefix>path\n" — the prefixed
        // paths are quoted via git_str_quote when they need it
        // (diff_delta_format_path, diff_print.c:333-345).
        writer.Write("diff --git "u8);
        AppendQuotedPath(writer, oldPrefix, oldPathValue.Span);
        writer.Write((byte)' ');
        AppendQuotedPath(writer, newPrefix, newPathValue.Span);
        writer.Write((byte)'\n');

        // Rename/copy headers. The from/to lines quote the raw path (no
        // prefix) — diff_delta_format_similarity_header (diff_print.c:375-401).
        // C (diff_print.c:456-460): a COPIED delta prints the similarity
        // header only when "unchanged" (both OIDs zero, or equal non-gitlink
        // OIDs — delta_is_unchanged, diff_print.c:407-419); a RENAMED delta
        // always prints it.
        bool unchanged = (delta.OldFile.Id.IsZero && delta.NewFile.Id.IsZero) ||
            (delta.OldFile.Mode != LibGit2CS.Objects.GitFileMode.GitLink && delta.NewFile.Mode != LibGit2CS.Objects.GitFileMode.GitLink &&
             delta.OldFile.Id == delta.NewFile.Id);

        // C (diff_print.c:448-449): an unchanged delta with a mode change
        // (mode-only change) prints the old/new mode lines and nothing else
        // (diff_print_modes, diff_print.c:280-286).
        if (unchanged && delta.OldFile.Mode != delta.NewFile.Mode)
        {
            writer.Write("old mode "u8);
            writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.OldFile.Mode, 0), provider: CultureInfo.InvariantCulture);
            writer.Write((byte)'\n');
            writer.Write("new mode "u8);
            writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.NewFile.Mode, 0), provider: CultureInfo.InvariantCulture);
            writer.Write((byte)'\n');
        }

        if (delta.Status == GitDeltaStatus.Renamed ||
            (delta.Status == GitDeltaStatus.Copied && unchanged))
        {
            writer.Write("similarity index "u8);
            writer.WriteSpanFormattable(delta.Similarity, provider: CultureInfo.InvariantCulture);
            writer.Write("%\n"u8);

            if (delta.Status == GitDeltaStatus.Renamed)
            {
                writer.Write("rename from "u8);
                AppendQuotedPath(writer, [], oldPathValue.Span);
                writer.Write((byte)'\n');
                writer.Write("rename to "u8);
                AppendQuotedPath(writer, [], newPathValue.Span);
                writer.Write((byte)'\n');
            }
            else
            {
                writer.Write("copy from "u8);
                AppendQuotedPath(writer, [], oldPathValue.Span);
                writer.Write((byte)'\n');
                writer.Write("copy to "u8);
                AppendQuotedPath(writer, [], newPathValue.Span);
                writer.Write((byte)'\n');
            }
        }

        // Mode/new file/deleted file headers + index line. Matches
        // diff_print_oid_range (diff_print.c:288-332): the block runs only
        // when the delta is NOT unchanged; the index OID length is the
        // configured id_abbrev (resolved), NOT the per-file id_abbrev field;
        // the mode suffix appears only when old==new mode. Requesting more id
        // characters than the input carries is a GIT_ERROR_PATCH error
        // (diff_print.c:294-307).
        if (!unchanged)
        {
            if ((int)delta.OldFile.Mode != 0 && idAbbrev > delta.OldFile.IdAbbrev)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"the patch input contains {delta.OldFile.IdAbbrev} id characters (cannot print {idAbbrev})",
                    GitErrorCategory.Patch);
            }

            if ((int)delta.NewFile.Mode != 0 && idAbbrev > delta.NewFile.IdAbbrev)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"the patch input contains {delta.NewFile.IdAbbrev} id characters (cannot print {idAbbrev})",
                    GitErrorCategory.Patch);
            }

            if (delta.OldFile.Mode == delta.NewFile.Mode)
            {
                if (printIndex)
                {
                    writer.Write("index "u8);
                    WriteOidHexForPrint(writer, delta.OldFile.Id, idAbbrev);
                    writer.Write(".."u8);
                    WriteOidHexForPrint(writer, delta.NewFile.Id, idAbbrev);
                    writer.Write((byte)' ');
                    writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.NewFile.Mode, 0), provider: CultureInfo.InvariantCulture);
                    writer.Write((byte)'\n');
                }
            }
            else
            {
                if (delta.OldFile.Mode == 0)
                {
                    writer.Write("new file mode "u8);
                    writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.NewFile.Mode, 0), provider: CultureInfo.InvariantCulture);
                    writer.Write((byte)'\n');
                }
                else if (delta.NewFile.Mode == 0)
                {
                    writer.Write("deleted file mode "u8);
                    writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.OldFile.Mode, 0), provider: CultureInfo.InvariantCulture);
                    writer.Write((byte)'\n');
                }
                else
                {
                    writer.Write("old mode "u8);
                    writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.OldFile.Mode, 0), provider: CultureInfo.InvariantCulture);
                    writer.Write((byte)'\n');
                    writer.Write("new mode "u8);
                    writer.WriteSpanFormattable(new OctalPadLeftFormatter((int)delta.NewFile.Mode, 0), provider: CultureInfo.InvariantCulture);
                    writer.Write((byte)'\n');
                }

                if (printIndex)
                {
                    writer.Write("index "u8);
                    WriteOidHexForPrint(writer, delta.OldFile.Id, idAbbrev);
                    writer.Write(".."u8);
                    WriteOidHexForPrint(writer, delta.NewFile.Id, idAbbrev);
                    writer.Write((byte)'\n');
                }
            }
        }

        // "---"/"+++" lines (when content differs AND non-binary). Matches
        // git_diff_delta__format_file_header (diff_print.c:464-468): the pair
        // is emitted when the delta is NOT unchanged, suppressed for binary
        // deltas (the "Binary files ... differ" line is emitted separately by
        // the patch printer). /dev/null is used when the side's OID is zero
        // (added -> --- /dev/null, deleted -> +++ /dev/null). The prefixed
        // paths are quoted.
        if (!unchanged &&
            delta.Status is not (GitDeltaStatus.Unmodified or GitDeltaStatus.Ignored or GitDeltaStatus.Untracked) &&
            (delta.Flags & GitDiffFileFlags.Binary) == 0)
        {
            writer.Write(delta.OldFile.Id.IsZero ? "--- /dev/null\n"u8 : "--- "u8);
            if (!delta.OldFile.Id.IsZero)
            {
                AppendQuotedPath(writer, oldPrefix, oldPathValue.Span);
                writer.Write((byte)'\n');
            }

            writer.Write(delta.NewFile.Id.IsZero ? "+++ /dev/null\n"u8 : "+++ "u8);
            if (!delta.NewFile.Id.IsZero)
            {
                AppendQuotedPath(writer, newPrefix, newPathValue.Span);
                writer.Write((byte)'\n');
            }
        }
    }

    /// <summary>
    /// Forces header emission for renames/copies/mode-changes. Matches
    /// <c>should_force_header</c> (diff_print.c:580-589).
    /// </summary>
    private static bool ShouldForceHeader(GitDiffDelta delta)
    {
        return delta.Status is GitDeltaStatus.Renamed or GitDeltaStatus.Copied ||
               delta.OldFile.Mode != delta.NewFile.Mode;
    }

    /// <summary>
    /// Flushes the deferred file header. Matches <c>flush_file_header</c>
    /// (diff_print.c:591-602). Idempotent — only emits once per delta.
    /// </summary>
    private static void FlushHeader(
        GitDiffDelta delta, PooledByteBufferWriter headerBuf,
        GitDiffPrintCallback callback, ref bool sentFileHeader)
    {
        if (sentFileHeader || headerBuf.WrittenCount == 0)
        {
            return;
        }

        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.FileHeader, 0, 0, 0,
            headerBuf.WrittenSpan.ToArray()));
        sentFileHeader = true;
    }

    /// <summary>
    /// Maps a <see cref="GitDeltaStatus"/> to the single status character used in
    /// raw/name-status output. Matches <c>git_diff_status_char</c>
    /// (diff_print.c:126-144).
    /// </summary>
    internal static char DeltaStatusChar(GitDeltaStatus status)
    {
        return status switch
        {
            GitDeltaStatus.Unmodified => ' ',
            GitDeltaStatus.Added => 'A',
            GitDeltaStatus.Deleted => 'D',
            GitDeltaStatus.Modified => 'M',
            GitDeltaStatus.Renamed => 'R',
            GitDeltaStatus.Copied => 'C',
            GitDeltaStatus.Ignored => 'I',
            GitDeltaStatus.Untracked => '?',
            GitDeltaStatus.Typechange => 'T',
            GitDeltaStatus.Unreadable => 'X',
            // C (diff_print.c:126-144): git_diff_status_char has NO GIT_DELTA_CONFLICTED arm — it falls to the default ' ', so conflicted deltas are skipped in
            // name-status and raw output unless SHOW_UNMODIFIED.
            _ => ' ',
        };
    }

    /// <summary>
    /// Writes an OID for index/raw output with the given length, matching
    /// <c>git_oid_tostr</c> clamping: a non-positive length prints nothing,
    /// a length at/above the full hex size prints the full OID.
    /// </summary>
    private static void WriteOidHexForPrint(PooledByteBufferWriter writer, GitOid oid, int len)
    {
        Debug.Assert(oid.HexSize <= 64); // The largest supported OID is SHA-256 (64 hex chars).
        Span<byte> buffer = writer.GetSpan(oid.HexSize);
        int bytesWritten = oid.FormatHex(buffer);

        ReadOnlySpan<byte> hex = buffer[..bytesWritten];
        ReadOnlySpan<byte> result = len <= 0 ? ReadOnlySpan<byte>.Empty : hex.Length <= len ? hex : hex[..len];
        writer.Advance(result.Length);
    }

    /// <summary>
    /// The <c>\a \b \t \n \v \f \r</c> escapes used by <c>git_str_quote</c>,
    /// indexed by <c>(byte - '\a')</c> (str.c:926). A string so it stays
    /// immutable (no static mutable state).
    /// </summary>
    private const string WhitespaceEscapes = "abtnvfr";

    /// <summary>
    /// Appends <paramref name="prefix"/> + <paramref name="path"/> to
    /// <paramref name="writer"/>, quoting the joined result like C's
    /// <c>git_str_quote</c> (str.c:924-968) when it needs quoting. Matches
    /// <c>diff_delta_format_path</c> (diff_print.c:333-345): a byte
    /// <c>!</c> first byte, <c>"</c>, <c>\</c>, a byte below 0x20, or a byte
    /// above 0x7e forces the whole string to be wrapped in double quotes
    /// with <c>\a..\r</c>, <c>\"</c>, <c>\\</c>, and octal <c>\NNN</c>
    /// escapes. Space and printable bytes pass through literally.
    /// </summary>
    private static void AppendQuotedPath(PooledByteBufferWriter writer, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> path)
    {
        // git_str_joinpath(prefix, path): the diff prefixes always end in
        // '/' and filenames never start with one, so the join is the plain
        // concatenation.
        bool quote = (prefix.Length > 0 && prefix[0] == (byte)'!') ||
                     (prefix.Length == 0 && path.Length > 0 && path[0] == (byte)'!') ||
                     NeedsQuote(prefix) || NeedsQuote(path);

        if (!quote)
        {
            // All bytes are ASCII printable — identity mapping to chars.
            writer.Write(prefix);
            writer.Write(path);
            return;
        }

        writer.Write((byte)'"');
        AppendQuotedBytes(writer, prefix);
        AppendQuotedBytes(writer, path);
        writer.Write((byte)'"');
    }

    /// <summary>
    /// True when any byte of <paramref name="bytes"/> needs quoting:
    /// <c>"</c>, <c>\</c>, below 0x20, or above 0x7e (str.c:933-939).
    /// </summary>
    private static bool NeedsQuote(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is (byte)'"' or (byte)'\\' or < (byte)' ' or > (byte)'~')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Appends <paramref name="bytes"/> with quote escapes (str.c:940-963).</summary>
    private static void AppendQuotedBytes(PooledByteBufferWriter writer, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is >= 0x07 and <= 0x0d)
            {
                writer.Write((byte)'\\');
                writer.Write((byte)WhitespaceEscapes[b - 0x07]);
            }
            else if (b is (byte)'"' or (byte)'\\')
            {
                writer.Write((byte)'\\');
                writer.Write(b);
            }
            else if (b is not (byte)' ' and (< ((byte)'!') or > ((byte)'~')))
            {
                // Octal escape, zero-padded to three digits ("\%03o").
                writer.Write((byte)'\\');
                writer.Write((byte)('0' + (b >> 6)));
                writer.Write((byte)('0' + ((b >> 3) & 7)));
                writer.Write((byte)('0' + (b & 7)));
            }
            else
            {
                writer.Write(b);
            }
        }
    }

    /// <summary>
    /// Encodes a diff prefix (<c>oldPrefix</c>/<c>newPrefix</c>, default
    /// <c>"a/"</c>/<c>"b/"</c>) to ASCII bytes in a pooled writer the caller
    /// disposes. libgit2 (and the git CLI) treat prefixes as ASCII literals;
    /// a non-ASCII prefix would corrupt the patch header, so it is rejected
    /// instead of silently best-fit substituted (the old
    /// <c>Encoding.ASCII.GetBytes</c> behavior).
    /// </summary>
    private static PooledByteBufferWriter EncodeDiffPrefix(string prefix, string paramName)
    {
        if (!Ascii.IsValid(prefix))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"diff prefix '{paramName}' must be ASCII (got '{prefix}')",
                GitErrorCategory.Config);
        }

        var writer = new PooledByteBufferWriter(prefix.Length);
        Encoding.ASCII.GetBytes(prefix, writer);
        return writer;
    }

    // ━━ Binary patch body emission ━━

    /// <summary>
    /// Emits the binary body for a binary delta: the full "GIT binary patch"
    /// (SHOW_BINARY + contains data) or the noshow "Binary files differ"
    /// line. Matches <c>diff_print_patch_file_binary</c>
    /// (diff_print.c:544-572) + <c>diff_print_patch_file_binary_noshow</c>
    /// (diff_print.c:522-538). Shared by the diff-level and patch-level
    /// printers.
    /// </summary>
    private static async ValueTask EmitBinaryBodyAsync(
        GitPatch patch, GitDiffDelta delta, bool showBinary,
        ReadOnlyMemory<byte> oldPrefix, ReadOnlyMemory<byte> newPrefix, GitDiffPrintCallback callback,
        CancellationToken cancellationToken)
    {
        GitBinaryPatch? binary = await patch.GetBinaryAsync(cancellationToken).ConfigureAwait(false);
        if (showBinary && binary is { ContainsData: true } bin)
        {
            EmitBinaryPatch(callback, delta, bin);
        }
        else
        {
            // diff_print_patch_file_binary_noshow (diff_print.c:522-538):
            // the prefixed paths are quoted.
            using var binaryBuf = new PooledByteBufferWriter();
            binaryBuf.Write("Binary files "u8);
            if (delta.OldFile.Id.IsZero)
            {
                binaryBuf.Write("/dev/null"u8);
            }
            else
            {
                AppendQuotedPath(binaryBuf, oldPrefix.Span, (delta.OldFile.Path ?? default).Span);
            }

            binaryBuf.Write(" and "u8);
            if (delta.NewFile.Id.IsZero)
            {
                binaryBuf.Write("/dev/null"u8);
            }
            else
            {
                AppendQuotedPath(binaryBuf, newPrefix.Span, (delta.NewFile.Path ?? default).Span);
            }

            binaryBuf.Write(" differ\n"u8);
            callback(delta, null, new GitDiffLine(
                GitDiffLineOrigin.Binary, 0, 0, 0,
                binaryBuf.WrittenSpan.ToArray()));
        }
    }

    /// <summary>
    /// Emits the "GIT binary patch" header + both directions (new_file then
    /// old_file). Matches <c>diff_print_patch_file_binary</c>
    /// (diff_print.c:544-572).
    /// </summary>
    private static void EmitBinaryPatch(
        GitDiffPrintCallback callback, GitDiffDelta delta, GitBinaryPatch bin)
    {
        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.Binary, 0, 0, 0,
            "GIT binary patch\n"u8.ToArray()));

        // New file direction (old→new), then old file direction (new→old).
        EmitBinarySide(callback, delta, bin.NewFile);
        EmitBinarySide(callback, delta, bin.OldFile);
    }

    /// <summary>
    /// Emits one direction of a binary patch. Matches <c>format_binary</c>
    /// (diff_print.c:494-519): "{typename} {inflatedlen}\n" then base85-encoded
    /// data in ≤52-byte chunks with length-encoded prefixes, terminated by "\n".
    /// </summary>
    private static void EmitBinarySide(
        GitDiffPrintCallback callback, GitDiffDelta delta, GitBinaryFile file)
    {
        using var hdrBuf = new PooledByteBufferWriter();
        hdrBuf.Write(file.Type == GitBinaryPatchType.Delta ? "delta "u8 : "literal "u8);
        hdrBuf.WriteSpanFormattable(file.InflatedLength, provider: CultureInfo.InvariantCulture);
        hdrBuf.Write((byte)'\n');
        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.Binary, 0, 0, 0,
            hdrBuf.WrittenSpan.ToArray()));

        byte[] data = file.Data;
        int offset = 0;
        while (offset < data.Length)
        {
            int chunkLen = Math.Min(data.Length - offset, 52);

            // Length prefix: A-Z for 1-26, a-z for 27-52.
            char lenChar = chunkLen <= 26
                ? (char)(chunkLen + 'A' - 1)
                : (char)(chunkLen - 26 + 'a' - 1);

            // {lenChar}{base85}\n
            byte[] lineBytes = new byte[1 + Base85.GetEncodedLength(chunkLen) + 1];
            lineBytes[0] = (byte)lenChar;
            Base85.Encode(data.AsSpan(offset, chunkLen), lineBytes.AsSpan(1));
            lineBytes[^1] = (byte)'\n';

            callback(delta, null, new GitDiffLine(
                GitDiffLineOrigin.Binary, 0, 0, 0, lineBytes));

            offset += chunkLen;
        }

        // Trailing empty line after each direction.
        callback(delta, null, new GitDiffLine(
            GitDiffLineOrigin.Binary, 0, 0, 0, "\n"u8.ToArray()));
    }
}

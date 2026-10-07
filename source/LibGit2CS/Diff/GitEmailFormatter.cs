// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary> <c>git format-patch</c> email output. Managed port of <c>src/libgit2/email.c</c> (317 LOC). </summary> <remarks> <para> Produces the RFC
/// 2822-style email format used by <c>git format-patch</c>: </para> <code> From &lt;oid&gt; Mon Sep 17 00:00:00 2001 From: Author Name
/// &lt;author@example.com&gt; Date: Wed, 9 Apr 2014 20:57:01 +0200 Subject: [PATCH] Summary line Body text (commit message) --- file.txt | 8 +++++--- 1 file
/// changed, 5 insertions(+), 3 deletions(-) diff --git a/file.txt b/file.txt... -- libgit2 1.9.4 </code> <para> The "magic" timestamp <c>Mon Sep 17 00:00:00
/// 2001</c> is the git <c>format-patch</c> signature recognized by <c>git am</c>. </para> <para> <b>Byte pipeline</b>: the email is rendered byte-end-to-end
/// into an <see cref="IBufferWriter{T}"/> — header text is UTF-8-encoded once, diffstat and patch bodies flow through as raw bytes. The three egress tiers
/// mirror <see cref="GitDiff.ToBufferAsync(System.Buffers.IBufferWriter{byte}, GitDiffPrintFormat, System.Threading.CancellationToken)"/>: writer-first,
/// <c>byte[]</c> (<c>git_buf</c> parity), and <see cref="string"/> (UTF-8 decode with replacement, display). </para> </remarks>
public static class GitEmailFormatter
{
    /// <summary>
    /// Renders a <c>format-patch</c> email from a commit into a caller-owned
    /// byte buffer. Matches <c>git_email_create_from_commit</c>
    /// (email.c:272-317). Computes the commit's diff (vs first parent),
    /// optionally detects renames, then formats. Zero-copy tier.
    /// </summary>
    public static Task ToBufferAsync(
        IBufferWriter<byte> writer,
        Commit commit,
        GitEmailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return WriteFromCommitAsync(writer, commit, options, cancellationToken);
    }

    /// <summary>
    /// Renders a <c>format-patch</c> email from a commit to raw bytes.
    /// Matches <c>git_email_create_from_commit</c> (email.c:272-317).
    /// Computes the commit's diff (vs first parent), optionally detects
    /// renames, then formats.
    /// </summary>
    public static async Task<byte[]> ToBufferAsync(
        Commit commit,
        GitEmailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var bufferWriter = new PooledByteBufferWriter();
        await WriteFromCommitAsync(bufferWriter, commit, options, cancellationToken).ConfigureAwait(false);
        return bufferWriter.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Renders a <c>format-patch</c> email from a commit to a UTF-8 decoded
    /// string (with replacement fallback) for display convenience.
    /// </summary>
    public static async Task<string> ToBufferTextAsync(
        Commit commit,
        GitEmailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ToBufferAsync(commit, options, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Renders a <c>format-patch</c> email from a pre-computed diff into a
    /// caller-owned byte buffer. Matches <c>git_email_create_from_diff</c>
    /// (email.c:245-270). All commit metadata (oid, summary, body, author)
    /// must be supplied. Zero-copy tier.
    /// </summary>
    public static Task ToBufferAsync(
        IBufferWriter<byte> writer,
        GitDiff diff,
        int patchIdx,
        int patchCount,
        GitOid commitId,
        string? summary,
        string? body,
        GitSignature author,
        GitEmailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return WriteFromDiffAsync(writer, diff, patchIdx, patchCount, commitId, summary, body, author, options, cancellationToken);
    }

    /// <summary>
    /// Renders a <c>format-patch</c> email from a pre-computed diff to raw
    /// bytes. Matches <c>git_email_create_from_diff</c> (email.c:245-270).
    /// All commit metadata (oid, summary, body, author) must be supplied.
    /// </summary>
    public static async Task<byte[]> ToBufferAsync(
        GitDiff diff,
        int patchIdx,
        int patchCount,
        GitOid commitId,
        string? summary,
        string? body,
        GitSignature author,
        GitEmailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var bufferWriter = new PooledByteBufferWriter();
        await WriteFromDiffAsync(bufferWriter, diff, patchIdx, patchCount, commitId, summary, body, author, options, cancellationToken).ConfigureAwait(false);
        return bufferWriter.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Renders a <c>format-patch</c> email from a pre-computed diff to a
    /// UTF-8 decoded string (with replacement fallback) for display
    /// convenience.
    /// </summary>
    public static async Task<string> ToBufferTextAsync(
        GitDiff diff,
        int patchIdx,
        int patchCount,
        GitOid commitId,
        string? summary,
        string? body,
        GitSignature author,
        GitEmailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ToBufferAsync(diff, patchIdx, patchCount, commitId, summary, body, author, options, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    // ===== Core writers =====

    /// <summary>Commit variant of <c>git_email_create_from_commit</c>'s body.</summary>
    private static async Task WriteFromCommitAsync(
        IBufferWriter<byte> writer,
        Commit commit,
        GitEmailOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        GitEmailOptions opts = options ?? new GitEmailOptions();
        GitDiffOptions diffOpts = opts.DiffOptions ?? new GitDiffOptions { Flags = GitDiffOptionsFlags.ShowBinary, ContextLines = 3 };
        GitDiffFindOptions findOpts = opts.DiffFindOptions ?? new GitDiffFindOptions();

        GitRepository repo = commit.Owner
            ?? throw new InvalidOperationException("Commit has no owner repository.");

        // Compute the commit's diff vs first parent (matches git_diff__commit).
        GitDiff diff = await GitDiff.CommitAsync(repo, commit, diffOpts, cancellationToken).ConfigureAwait(false);

        // Optional rename detection.
        if ((opts.Flags & GitEmailCreateFlags.NoRenames) == 0)
        {
            await diff.FindSimilarAsync(findOpts, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await WriteFromDiffAsync(
                writer,
                diff,
                patchIdx: 1,
                patchCount: 1,
                commitId: commit.Id,
                summary: commit.Summary,
                body: commit.Body,
                author: commit.Author,
                opts,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            diff.Dispose();
        }
    }

    /// <summary>Matches <c>git_email_create_from_diff</c> (email.c:245-270).</summary>
    private static async Task WriteFromDiffAsync(
        IBufferWriter<byte> writer,
        GitDiff diff,
        int patchIdx,
        int patchCount,
        GitOid commitId,
        string? summary,
        string? body,
        GitSignature author,
        GitEmailOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(author);

        GitEmailOptions opts = options ?? new GitEmailOptions();

        AppendHeader(writer, patchIdx, patchCount, commitId, summary, author, opts);
        AppendBody(writer, body);
        writer.Write("---\n"u8);
        await AppendDiffstatAsync(writer, diff, cancellationToken).ConfigureAwait(false);
        await AppendPatchesAsync(writer, diff, cancellationToken).ConfigureAwait(false);
        writer.Write("--\nlibgit2 "u8);
        WriteUtf8(writer, LibGit2Version.String);
        writer.Write("\n\n"u8);
    }

    // ===== Helpers (1:1 ports of email.c static functions) =====

    /// <summary>Matches <c>include_prefix</c> (email.c:28-36).</summary>
    private static bool IncludePrefix(int patchCount, GitEmailOptions opts)
    {
        // (!subject_prefix || *subject_prefix) — null OR non-empty string
        bool prefixNonEmpty = opts.SubjectPrefix is null || opts.SubjectPrefix.Length > 0;
        return prefixNonEmpty
            || (opts.Flags & GitEmailCreateFlags.AlwaysNumber) != 0
            || opts.RerollNumber != 0
            || (patchCount > 1 && (opts.Flags & GitEmailCreateFlags.OmitNumbers) == 0);
    }

    /// <summary>Matches <c>append_prefix</c> (email.c:38-75).</summary>
    private static void AppendPrefix(IBufferWriter<byte> writer, int patchIdx, int patchCount, GitEmailOptions opts)
    {
        string subjectPrefix = opts.SubjectPrefix ?? "PATCH";

        writer.WriteByte((byte)'[');

        if (subjectPrefix.Length > 0)
        {
            WriteUtf8(writer, subjectPrefix);
        }

        if (opts.RerollNumber != 0)
        {
            if (subjectPrefix.Length > 0)
            {
                writer.WriteByte((byte)' ');
            }

            writer.WriteByte((byte)'v');
            WriteInt(writer, opts.RerollNumber);
        }

        if ((opts.Flags & GitEmailCreateFlags.AlwaysNumber) != 0 ||
            (patchCount > 1 && (opts.Flags & GitEmailCreateFlags.OmitNumbers) == 0))
        {
            int startNumber = opts.StartNumber != 0 ? opts.StartNumber : 1;

            if (subjectPrefix.Length > 0 || opts.RerollNumber != 0)
            {
                writer.WriteByte((byte)' ');
            }

            WriteInt(writer, patchIdx + (startNumber - 1));
            writer.WriteByte((byte)'/');
            WriteInt(writer, patchCount + (startNumber - 1));
        }

        writer.WriteByte((byte)']');
    }

    /// <summary>Matches <c>append_date</c> (email.c:77-88).</summary>
    private static void AppendDate(IBufferWriter<byte> writer, GitTime date)
    {
        writer.Write("Date: "u8);
        WriteUtf8(writer, GitDateParser.FormatRfc2822(date.Seconds, date.OffsetMinutes));
        writer.WriteByte((byte)'\n');
    }

    /// <summary>Matches <c>append_subject</c> (email.c:90-123).</summary>
    private static void AppendSubject(
        IBufferWriter<byte> writer, int patchIdx, int patchCount, string? summary, GitEmailOptions opts)
    {
        bool prefix = IncludePrefix(patchCount, opts);
        int summaryLen = summary?.Length ?? 0;

        if (summaryLen > 0)
        {
            Debug.Assert(summary is not null, "summaryLen > 0 implies summary is non-null");
            int nl = summary.IndexOf('\n', StringComparison.Ordinal);
            if (nl >= 0)
            {
                summaryLen = nl;
            }
        }

        writer.Write("Subject: "u8);

        if (prefix)
        {
            AppendPrefix(writer, patchIdx, patchCount, opts);
        }

        if (prefix && summaryLen > 0)
        {
            writer.WriteByte((byte)' ');
        }

        if (summaryLen > 0)
        {
            WriteUtf8(writer, summary.AsSpan(0, summaryLen));
        }

        writer.WriteByte((byte)'\n');
    }

    /// <summary>Matches <c>append_header</c> (email.c:125-149).</summary>
    private static void AppendHeader(
        IBufferWriter<byte> writer, int patchIdx, int patchCount,
        GitOid commitId, string? summary, GitSignature author, GitEmailOptions opts)
    {
        writer.Write("From "u8);
        Span<byte> hex = writer.GetSpan(commitId.HexSize);
        int hexLen = commitId.FormatHex(hex);
        writer.Advance(hexLen);
        writer.Write(" Mon Sep 17 00:00:00 2001\n"u8);

        writer.Write("From: "u8);
        WriteUtf8(writer, author.Name);
        writer.Write(" <"u8);
        WriteUtf8(writer, author.Email);
        writer.Write(">\n"u8);

        AppendDate(writer, author.When);
        AppendSubject(writer, patchIdx, patchCount, summary, opts);
        writer.WriteByte((byte)'\n');
    }

    /// <summary>Matches <c>append_body</c> (email.c:151-168).</summary>
    private static void AppendBody(IBufferWriter<byte> writer, string? body)
    {
        if (body is null)
        {
            return;
        }

        WriteUtf8(writer, body);

        if (body.Length > 0 && body[^1] != '\n')
        {
            writer.WriteByte((byte)'\n');
        }
    }

    /// <summary>Matches <c>append_diffstat</c> (email.c:170-184).</summary>
    private static async Task AppendDiffstatAsync(IBufferWriter<byte> writer, GitDiff diff, CancellationToken cancellationToken)
    {
        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken).ConfigureAwait(false);

        // C passes width
        // 0 to git_diff__stats_to_buf (email.c:176-180), which takes the
        // `if (!width)` branch (diff_stats.c:114-117) and emits one '+'/'-'
        // per line (raw bars), not the scaled bars a default width of 80
        // would produce.
        stats.Format(writer, GitDiffStatsFormat.Full | GitDiffStatsFormat.IncludeSummary, width: 0);
        writer.WriteByte((byte)'\n');
    }

    /// <summary>Matches <c>append_patches</c> (email.c:186-206).</summary>
    private static async Task AppendPatchesAsync(IBufferWriter<byte> writer, GitDiff diff, CancellationToken cancellationToken)
    {
        await foreach (GitPatch patch in diff.PatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            await DiffPrinter.PrintPatchAsync(patch, (delta, hunk, line) =>
            {
                XdiffBridge.RenderLine(writer, line);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>UTF-8-encodes <paramref name="text"/> into the writer (display-tier edge for header text).</summary>
    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> text)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> span = writer.GetSpan(maxByteCount);
        int bytesWritten = Encoding.UTF8.GetBytes(text, span);
        writer.Advance(bytesWritten);
    }

    private static void WriteInt(IBufferWriter<byte> writer, int value)
        => writer.WriteSpanFormattable(value, provider: CultureInfo.InvariantCulture);
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary>
/// Patch-id computation. Managed port of <c>git_diff_patchid</c>
/// (<c>src/libgit2/diff.c:366-402</c>) + the <c>flush_hunk</c>/
/// <c>strip_spaces</c> helpers (<c>diff.c:285-320</c>).
/// </summary>
/// <remarks>
/// <para>
/// A patch-id is a stable hash of a diff that's invariant under whitespace
/// changes and OID re-computation: <c>git patch-id</c> uses it to match
/// commits across rebases/cherry-picks regardless of context or line numbers.
/// </para>
/// <para>
/// <b>Carry-add, NOT plain XOR:</b> the per-hunk fold is
/// <c>result[i] = carry + result[i] + hash[i]</c> with byte-carry propagation
/// across the OID width (<c>diff.c:285-304</c>). This is NOT plain XOR. Must
/// match byte-exact for <c>git patch-id</c> compatibility.
/// </para>
/// <para>
/// <b>Whitespace strip:</b> <c>strip_spaces</c>
/// (<c>diff.c:306-320</c>) removes ALL whitespace (space, tab, LF, CR, FF, VT
/// — the <c>git__isspace</c> set) from each assembled line BEFORE hashing.
/// This includes the leading context-line sigil ' '. Use <see cref="GitMessage.IsSpace"/>
/// (the existing <c>git__isspace</c> port) — NOT <c>char.IsWhiteSpace</c>,
/// which would over-strip Unicode whitespace (e.g. U+00A0 NBSP).
/// </para>
/// <para>
/// EOF-newline marker lines (<c>ContextEofnl</c>/<c>AddEofnl</c>/<c>DelEofnl</c>)
/// are skipped entirely (<c>diff.c:332-335</c>).
/// </para>
/// </remarks>
public static class GitPatchId
{
    /// <summary>
    /// Computes the patch-id of <paramref name="diff"/>. Matches
    /// <c>git_diff_patchid</c> (<c>diff.c:366-402</c>).
    /// </summary>
    /// <param name="diff">The diff to hash.</param>
    /// <param name="options">
    /// Options (empty in 1.9.4; present for API parity). Pass null for defaults.
    /// </param>
    /// <returns>The patch-id OID (SHA-1 or SHA-256 per the diff's OidType).</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<GitOid> ComputeAsync(GitDiff diff, GitPatchIdOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        GitHashAlgorithmKind oidType = diff.Options.OidType;
        int oidSize = GitOid.SizeFor(oidType);
        byte[] result = new byte[oidSize];
        using var ctx = GitIncrementalHash.Create(oidType);
        // The callback hashes synchronously, so one scratch buffer can serve
        // every line. Dispose returns it even when printing fails or is cancelled.
        using var scratch = new PooledByteBufferWriter();
        bool firstFile = true;

        await DiffPrinter.PrintPatchIdAsync(diff, (delta, hunk, line) =>
        {
            // diff.c:332-335 — skip EOF-newline marker lines entirely.
            if (line.Origin is GitDiffLineOrigin.ContextEofnl
                or GitDiffLineOrigin.AddEofnl
                or GitDiffLineOrigin.DelEofnl)
            {
                return;
            }

            // Assemble the line as git_diff_print_callback__to_buf does
            // (diff_print.c:792-812): prepend the origin sigil for
            // context/addition/deletion lines, then append content.
            ReadOnlySpan<byte> content = line.Content.Span;
            int sigilLen = line.Origin is GitDiffLineOrigin.Context
                or GitDiffLineOrigin.Addition
                or GitDiffLineOrigin.Deletion ? 1 : 0;

            Span<byte> buf = scratch.GetSpan(sigilLen + content.Length)[..(sigilLen + content.Length)];
            if (sigilLen == 1)
            {
                buf[0] = (byte)line.Origin;
                content.CopyTo(buf[1..]);
            }
            else
            {
                content.CopyTo(buf);
            }

            // diff.c:306-320 — strip ALL whitespace (git__isspace set).
            int strippedLength = StripSpaces(buf);

            // diff.c:343-346 — on FileHeader for the 2nd+ file, flush the
            // previous file's accumulated hash into `result` with carry-add.
            if (line.Origin == GitDiffLineOrigin.FileHeader && !firstFile)
            {
                FlushHunk(result, ctx, oidSize);
            }

            // diff.c:348 — hash this line's stripped bytes.
            if (strippedLength > 0)
            {
                ctx.AppendData(buf[..strippedLength]);
            }

            // diff.c:351-352 — first file's header clears the first_file flag.
            if (line.Origin == GitDiffLineOrigin.FileHeader && firstFile)
            {
                firstFile = false;
            }
        }, cancellationToken).ConfigureAwait(false);

        // diff.c:390 — final flush folds the last file's hash into result.
        FlushHunk(result, ctx, oidSize);

        return GitOid.FromRaw(result, oidType);
    }

    /// <summary>
    /// Folds the incremental hash's current value into <paramref name="result"/>
    /// with byte-carry propagation, then resets the hash context for the next
    /// hunk. Matches <c>flush_hunk</c> (<c>diff.c:285-304</c>).
    /// </summary>
    /// <remarks>
    /// <b>Carry width:</b> the carry is an <c>unsigned short</c> (16-bit) in C,
    /// holding at most <c>0xFF + 0xFF = 0x1FE</c>. The low 8 bits go into
    /// <c>result[i]</c>; the high bits carry to the next byte. This is
    /// addition-with-carry, NOT XOR — must match byte-exact.
    /// </remarks>
    private static void FlushHunk(byte[] result, GitIncrementalHash ctx, int oidSize)
    {
        // Finalize the current hunk's hash and reset ctx for the next hunk
        // (matches git_hash_final + git_hash_init at diff.c:293-295).
        GitOid hunkHash = ctx.Finalize();
        ReadOnlySpan<byte> hunkBytes = hunkHash.RawBytes;

        // Carry-add the hunk hash into result, byte-by-byte. Matches
        // diff.c:297-301. `carry` is unsigned short (16-bit) — max value
        // 0xFF + 0xFF = 0x1FE fits without overflow.
        ushort carry = 0;
        for (int i = 0; i < oidSize; i++)
        {
            carry += (ushort)(result[i] + hunkBytes[i]);
            result[i] = (byte)carry;
            carry >>= 8;
        }
    }

    /// <summary>
    /// Compacts in place and returns the length after removing ALL whitespace
    /// (the <c>git__isspace</c> set: space, tab, LF,
    /// CR, FF, VT) from <paramref name="buf"/>. Matches <c>strip_spaces</c>
    /// (<c>diff.c:306-320</c>). Use <see cref="GitMessage.IsSpace"/>
    /// (the existing exact port), NOT <c>char.IsWhiteSpace</c>.
    /// </summary>
    private static int StripSpaces(Span<byte> buf)
    {
        int dst = 0;
        for (int src = 0; src < buf.Length; src++)
        {
            byte b = buf[src];
            // Message.IsSpace operates on char; for the byte domain we inline
            // the same 6-char set to avoid the char-upcast overhead. This is
            // byte-exact: space(0x20), tab(0x09), LF(0x0A), FF(0x0C), CR(0x0D), VT(0x0B).
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)'\v'))
            {
                if (src != dst)
                {
                    buf[dst] = b;
                }

                dst++;
            }
        }

        return dst;
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// Abstraction over reading files from different sources (tree, workdir,
/// index). Managed port of libgit2's <c>src/libgit2/reader.c</c> +
/// <c>reader.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// The C <c>git_reader</c> is a vtable with a single <c>read</c> function
/// pointer. Three implementations exist: <c>tree_reader</c> (reads from a
/// <c>git_tree</c>), <c>workdir_reader</c> (reads from the working directory),
/// and <c>index_reader</c> (reads from the index). The managed port uses an
/// interface with concrete implementations.
/// </para>
/// <para>
/// <see cref="TreeReader"/> and <see cref="WorkdirReader"/> do basic
/// filesystem reads + OID computation; <see cref="IndexReader"/> reads staged
/// blobs by looking up the path in the index and loading the blob from the
/// ODB. The sole consumer is <c>apply.c</c> (patch parse/apply).
/// </para>
/// <para>
/// <see cref="WorkdirReader"/> applies filters + computes the OID on filtered
/// content + does index validation (3-way mode/OID compare) +
/// <see cref="ReadStatus.Mismatch"/> signaling. <see cref="ReadAsync"/> returns a
/// <see cref="ReaderReadResult"/> bundling the status to distinguish
/// not-found from mismatch (C sentinel <c>GIT_READER_MISMATCH=1</c>
/// → explicit enum).
/// </para>
/// </remarks>
internal interface IObjectReader
{
    /// <summary> Reads a file by path. Returns the result (including status), or a not-found result if the file does not exist. Matches <c>git_reader_read</c>.
    /// The <see cref="ReaderReadResult.Status"/> distinguishes <see cref="ReadStatus.NotFound"/> (file absent) from <see cref="ReadStatus.Mismatch"/> (workdir
    /// file differs from index). Byte-faithful: the path is a <see cref="GitPath"/> over raw UTF-8 bytes, matching libgit2's <c>git_reader_read(const char
    /// *)</c>. </summary>
    Task<ReaderReadResult> ReadAsync(GitPath path, CancellationToken cancellationToken = default);
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A streaming write target for the filter pipeline. Managed port of
/// <c>git_writestream</c> (<c>include/git2/types.h:375-379</c>).
/// </summary>
/// <remarks>
/// The filter pipeline chains <see cref="IFilterWriteStream"/> instances:
/// data written to the first stream is filtered and forwarded to the next,
/// and so on until it reaches the final target stream. All operations are
/// async because <see cref="IFilter.ApplyAsync"/> is async.
/// </remarks>
public interface IFilterWriteStream : IDisposable
{
    /// <summary>Writes a chunk of data. Matches <c>git_writestream.write</c>.</summary>
    void Write(ReadOnlySpan<byte> buffer);

    /// <summary>Closes the stream, flushing any buffered data. Matches <c>git_writestream.close</c>.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

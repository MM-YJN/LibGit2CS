// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// Write-pack interface — streams pack data into the object database.
/// Maps to <c>git_odb_writepack</c> in <c>include/git2/odb.h</c>.
/// </summary>
/// <remarks>
/// Implementation: <see cref="Pack.GitPackIndexer"/> (keep-as-pack —
/// writes <c>.pack</c> + <c>.idx</c> v2 files).
/// <para>
/// <b>Async model:</b> <see cref="AppendAsync"/> and <see cref="Commit"/>
/// become <c>*Async</c> returning <see cref="ValueTask{TResult}"/>; the
/// <c>ReadOnlySpan{byte}</c> data parameter becomes <see cref="ReadOnlyMemory{T}"/>
/// (spans are not allowed in async signatures). The interface is
/// <see cref="IAsyncDisposable"/>; the pack indexer's disposal (closing the
/// temp pack file + cleanup on rollback) is async.
/// </para>
/// </remarks>
public interface IGitWritePack : IAsyncDisposable
{
    /// <summary>
    /// Append raw pack data to the indexer. Called repeatedly as pack
    /// data arrives from the transport.
    /// </summary>
    /// <param name="data">Raw pack bytes (may be partial).</param>
    /// <param name="progress">Progress accumulator (updated in place).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> on success; <c>false</c> to cancel.</returns>
    Task<bool> AppendAsync(ReadOnlyMemory<byte> data, GitIndexerProgress progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalize the pack: resolve any pending deltas, write objects to
    /// the ODB, and commit.
    /// </summary>
    /// <param name="progress">Final progress state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> on success; <c>false</c> on failure.</returns>
    Task<bool> CommitAsync(GitIndexerProgress progress, CancellationToken cancellationToken = default);
}

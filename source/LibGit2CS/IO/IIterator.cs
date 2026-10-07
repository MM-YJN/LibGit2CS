// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;

namespace LibGit2CS.IO;

/// <summary>
/// Iterator over a sequence of git entries (tree, index, workdir, or
/// filesystem). Managed port of <c>git_iterator</c> +
/// <c>git_iterator_callbacks</c> in <c>src/libgit2/iterator.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// The C <c>git_iterator</c> uses a vtable of function pointers. The managed
/// port uses an interface with concrete implementations — no function
/// pointers (AOT-clean). All iterators yield <see cref="GitIndexEntry"/>-shaped
/// entries (the common denominator across tree, index, and workdir sources).
/// </para>
/// <para>
/// 4 backends (empty, tree, index, filesystem/workdir) + lock-step
/// multi-iterator walk.
/// </para>
/// </remarks>
internal interface IIterator : IDisposable
{
    /// <summary>The iterator type (discriminator).</summary>
    IteratorType Type { get; }

    /// <summary>The iterator flags.</summary>
    IteratorFlags Flags { get; }

    /// <summary>True if case-insensitive iteration is enabled.</summary>
    bool IgnoreCase { get; }

    /// <summary>
    /// The index associated with this iterator, or <c>null</c> if the
    /// iterator has no index (tree, empty, or filesystem iterator without an
    /// index). Used by the racy-git check in <see cref="Diff.DiffGenerator"/>
    /// (<c>EntryNewerThanIndex</c>) to reach <see cref="Index.GitIndex.Stamp"/>.
    /// Matches <c>git_iterator_index</c> (iterator.h) which returns the index
    /// associated with the iterator (workdir/index iterators return the repo
    /// index; tree/empty iterators return NULL).
    /// </summary>
    GitIndex? Index { get; }

    /// <summary>
    /// Returns the current entry without advancing, or <c>null</c> if
    /// exhausted. If the iterator has not been accessed yet, this triggers
    /// an initial <see cref="AdvanceAsync"/>. Matches <c>git_iterator_current</c>.
    /// </summary>
    ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Advances to the next entry and returns it. Returns <c>null</c> when
    /// the iterator is exhausted. Matches <c>git_iterator_advance</c>
    /// (GIT_ITEROVER → null).
    /// </summary>
    Task<GitIndexEntry?> AdvanceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// If the current entry is a directory/tree, descends into it and
    /// returns the first child. Otherwise, behaves like <see cref="AdvanceAsync"/>.
    /// Matches <c>git_iterator_advance_into</c>.
    /// </summary>
    Task<GitIndexEntry?> AdvanceIntoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// If the current entry is a directory/tree, skips over all its
    /// children and returns the next sibling along with the
    /// <see cref="IteratorStatus"/> of the skipped directory. Matches
    /// <c>git_iterator_advance_over</c>. Returns a tuple instead of using
    /// <c>out</c> because <c>out</c> is unidiomatic in async methods.
    /// </summary>
    Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resets the iterator to its initial position. Matches
    /// <c>git_iterator_reset</c>. Async because <see cref="FilesystemIterator"/>
    /// re-enumerates the root directory on reset (real IO); other backends
    /// complete synchronously.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken);
}

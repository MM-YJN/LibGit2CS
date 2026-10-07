// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Refs;

/// <summary>
/// Opaque handle to a locked reference (a <c>.lock</c> temp file on disk).
/// Returned by <see cref="IRefBackend.Lock"/>; passed to
/// <see cref="IRefBackend.WriteAsync"/>/<see cref="IRefBackend.DeleteAsync"/>/
/// <see cref="IRefBackend.RenameAsync"/>/<see cref="IRefBackend.UnlockAsync"/>.
/// </summary>
/// <remarks>
/// Stays <see cref="IDisposable"/> (not <see cref="IAsyncDisposable"/>):
/// <see cref="IDisposable.Dispose"/> only does <c>File.Delete</c> on the <c>.lock</c>
/// path — a metadata op that is stat-exempt.
/// </remarks>
internal interface IRefLock : IDisposable
{
    /// <summary>The ref name that was locked.</summary>
    RefNameKey RefName { get; }

    /// <summary>The on-disk <c>.lock</c> temp file path.</summary>
    string LockPath { get; }
}

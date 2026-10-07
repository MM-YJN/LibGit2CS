// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Base class for authentication credentials. Managed equivalent of
/// libgit2's <c>git_credential</c> in <c>include/git2/credential.h</c>.
/// </summary>
/// <remarks>
/// Concrete subclasses carry the actual credential material (password, key
/// paths, etc.). Callers dispose credentials when done; GC handles the rest.
/// </remarks>
public abstract class GitCredential : IDisposable
{
    /// <summary>
    /// The username associated with this credential, or <c>null</c> if none.
    /// </summary>
    public abstract string? Username { get; }

    /// <summary> Whether this credential carries a username. Matches <c>git_credential_has_username</c> (credential.c:22-28): every type except
    /// <c>GIT_CREDENTIAL_DEFAULT</c> reports a username — even an EMPTY one. </summary>
    public bool HasUsername => Type != GitCredentialType.Default;

    /// <summary>The <see cref="GitCredentialType"/> bit for this credential.</summary>
    public abstract GitCredentialType Type { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Overridden by subclasses to release sensitive material (e.g. zero
    /// password buffers). The base implementation does nothing.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }
}

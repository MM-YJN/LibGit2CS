// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Core;

/// <summary>
/// Exception carrying libgit2-style error information. Replaces the C library's
/// per-thread <c>git_error_last()</c> / <c>git_error_set()</c> mechanism — the
/// error state rides on the exception stack instead of TLS.
/// </summary>
/// <remarks>
/// <see cref="Code"/> maps to <c>git_error_code</c>; <see cref="Category"/> maps to
/// <c>git_error_t</c>. Internal <c>git_error_set(GIT_ERROR_*, ...)</c> sites in
/// libgit2 become <c>throw new GitException(...)</c> in the managed port.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "The four-arg ctor is the canonical form; parameterless/default-message ctors would lose Code/Category.")]
public class GitException : Exception
{
    /// <summary>
    /// The libgit2 error code (e.g. <see cref="GitErrorCode.NotFound"/>).
    /// </summary>
    public GitErrorCode Code { get; }

    /// <summary>
    /// The error category indicating which subsystem raised the error.
    /// </summary>
    public GitErrorCategory Category { get; }

    /// <summary>
    /// Creates a generic error (<see cref="GitErrorCode.Error"/>) with the given message.
    /// </summary>
    public GitException(string message)
        : this(GitErrorCode.Error, message, GitErrorCategory.None)
    {
    }

    /// <summary>
    /// Creates a new exception with the given code, message, and category.
    /// </summary>
    public GitException(GitErrorCode code, string message, GitErrorCategory category = GitErrorCategory.None)
        : base(message)
    {
        Code = code;
        Category = category;
    }

    /// <summary>
    /// Creates a new exception wrapping an inner cause. Defaults to <see cref="GitErrorCode.Error"/>.
    /// </summary>
    public GitException(string message, Exception innerException)
        : this(GitErrorCode.Error, message, GitErrorCategory.None, innerException)
    {
    }

    /// <summary>
    /// Creates a new exception with the given code, message, category, and inner cause.
    /// </summary>
    public GitException(GitErrorCode code, string message, GitErrorCategory category, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        Category = category;
    }

    /// <summary>
    /// Throws <see cref="GitException"/> if <paramref name="code"/> is negative.
    /// Convenience for translating libgit2-style return codes at interop boundaries.
    /// </summary>
    public static void ThrowIfError(GitErrorCode code, string message = "", GitErrorCategory category = GitErrorCategory.None)
    {
        if (code != GitErrorCode.Ok)
        {
            throw new GitException(code, string.IsNullOrEmpty(message) ? code.ToString() : message, category);
        }
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.IO;
using LibGit2CS.Submodule;

namespace LibGit2CS.Repository;

/// <content>
/// Submodule operations. Managed entry points over libgit2's
/// <c>git_submodule_*</c> factories. The underlying factories on
/// <see cref="GitSubmodule"/> are internal; these instance methods are the
/// public surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Looks up a submodule by name. Matches <c>git_submodule_lookup</c>
    /// (submodule.c:308-433). Throws <see cref="LibGit2CS.Core.GitErrorCode.NotFound"/> /
    /// <see cref="LibGit2CS.Core.GitErrorCode.Exists"/> on a miss.
    /// Convenience wrapper for <see cref="LibGit2CS.Submodule.GitSubmodule.LookupAsync(LibGit2CS.Repository.GitRepository, string, System.Threading.CancellationToken)"/>.</summary>
    public ValueTask<GitSubmodule> SubmoduleLookupAsync(string name, CancellationToken cancellationToken = default)
        => GitSubmodule.LookupAsync(this, name, cancellationToken);

    /// <summary>Sets a submodule's <c>url</c>. Matches <c>git_submodule_set_url</c>.
    /// Convenience wrapper for <see cref="GitSubmodule.SetUrlAsync"/>.</summary>
    public Task SubmoduleSetUrlAsync(string name, string url, CancellationToken cancellationToken = default)
        => GitSubmodule.SetUrlAsync(this, name, url, cancellationToken);

    /// <summary>Sets a submodule's <c>branch</c>. Matches
    /// <c>git_submodule_set_branch</c>. Convenience wrapper for
    /// <see cref="GitSubmodule.SetBranchAsync"/>.</summary>
    public Task SubmoduleSetBranchAsync(string name, string? branch, CancellationToken cancellationToken = default)
        => GitSubmodule.SetBranchAsync(this, name, branch, cancellationToken);

    /// <summary>Sets a submodule's ignore rule. Matches
    /// <c>git_submodule_set_ignore</c>. Convenience wrapper for
    /// <see cref="GitSubmodule.SetIgnoreAsync"/>.</summary>
    public Task SubmoduleSetIgnoreAsync(string name, SubmoduleIgnore ignore, CancellationToken cancellationToken = default)
        => GitSubmodule.SetIgnoreAsync(this, name, ignore, cancellationToken);

    /// <summary>Sets a submodule's update strategy. Matches
    /// <c>git_submodule_set_update</c>. Convenience wrapper for
    /// <see cref="GitSubmodule.SetUpdateAsync"/>.</summary>
    public Task SubmoduleSetUpdateAsync(string name, SubmoduleUpdateStrategy update, CancellationToken cancellationToken = default)
        => GitSubmodule.SetUpdateAsync(this, name, update, cancellationToken);

    /// <summary>Sets a submodule's fetch-recurse mode. Matches
    /// <c>git_submodule_set_fetchrecurse</c>. Convenience wrapper for
    /// <see cref="GitSubmodule.SetFetchRecurseAsync"/>.</summary>
    public Task SubmoduleSetFetchRecurseAsync(string name, SubmoduleRecurse recurse, CancellationToken cancellationToken = default)
        => GitSubmodule.SetFetchRecurseAsync(this, name, recurse, cancellationToken);

    /// <summary>Gets a submodule's status. Matches <c>git_submodule_status</c>.
    /// Convenience wrapper for <see cref="GitSubmodule.StatusAsync"/>.</summary>
    public ValueTask<SubmoduleStatus> SubmoduleStatusAsync(string name, SubmoduleIgnore ignore = SubmoduleIgnore.Unspecified, CancellationToken cancellationToken = default)
        => GitSubmodule.StatusAsync(this, name, ignore, cancellationToken);

    /// <summary>Sets up a new submodule: writes <c>.gitmodules</c> and creates
    /// the submodule repository. Matches <c>git_submodule_add_setup</c>.
    /// Convenience wrapper for
    /// <see cref="GitSubmodule.AddSetupAsync"/>.</summary>
    public Task<GitSubmodule> SubmoduleAddSetupAsync(string url, string path, bool useGitlink = true, CancellationToken cancellationToken = default)
        => GitSubmodule.AddSetupAsync(this, url, path, useGitlink, cancellationToken);

    /// <summary> Byte-faithful overload of <see cref="SubmoduleAddSetupAsync(string, string, bool, CancellationToken)"/>. </summary>
    public Task<GitSubmodule> SubmoduleAddSetupAsync(string url, GitPath path, bool useGitlink = true, CancellationToken cancellationToken = default)
        => GitSubmodule.AddSetupAsync(this, url, path.ToUtf8String(), useGitlink, cancellationToken);

    /// <summary>Enumerates all submodules. Matches <c>git_submodule_foreach</c>.
    /// Convenience wrapper for <see cref="GitSubmodule.ForEachAsync"/>.</summary>
    public IAsyncEnumerable<GitSubmodule> SubmoduleForEachAsync(CancellationToken cancellationToken = default)
        => GitSubmodule.ForEachAsync(this, cancellationToken);
}

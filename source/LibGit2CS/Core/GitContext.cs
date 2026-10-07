// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Attributes;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Remote;

namespace LibGit2CS.Core;

/// <summary> Library-wide context owning all per-instance mutable state (settings, trace, directories, registries). Managed port of the implicit global state
/// in <c>src/libgit2/sysdir.c</c>, <c>src/libgit2/settings.c</c>, <c>src/libgit2/transport.c</c>, <c>src/libgit2/filter_registry.c</c>, and
/// <c>src/libgit2/merge_driver.c</c>. </summary> <remarks> <para> All per-instance mutable state lives on this class. <see cref="Trace"/>, <see cref="Transports"/>, <see cref="Filters"/>, <see cref="MergeDrivers"/>, <see cref="Settings"/> (all members — <c>KnownHostsPathOverride</c>, <c>AgentSocketPathOverride</c>, and the strictness flags — are per-context), <see cref="Dirs"/>, <see cref="Env"/>, and <see cref="DiffDrivers"/> are per-context. </para> <para> <b>Ownership:</b> the caller owns the context. <see
/// cref="LibGit2CS.Repository.GitRepository"/> does <b>not</b> dispose the context — a single context is typically shared across multiple repositories in a session. Callers use
/// <c>using var ctx = new GitContext();</c> at the top of their call chain. </para> </remarks>
public sealed class GitContext : IDisposable
{
    /// <summary>Creates a new context with default (empty) state.</summary>
    public GitContext()
    {
        Env = new GitEnvironment();
        Settings = new GitSettings();
        Trace = new GitTrace();
        Dirs = new GitSystemDirs(Env);
        Transports = new GitTransportRegistry();
        Filters = new FilterRegistry();
        MergeDrivers = new GitMergeDriverRegistry();
        DiffDrivers = new GitDiffDrivers();
    }

    /// <summary> Per-context environment-variable snapshot for this context. Each context owns its own mutable copy of the process
    /// environment block. Reads via <c>ctx.Env[name]</c> consult the snapshot only (no live fall-through); mutations never leak to the process or to other
    /// contexts. Shared with <see cref="Dirs"/> so directory resolution honors the same overrides. </summary>
    public GitEnvironment Env { get; }

    /// <summary>
    /// Library-wide settings for this context. Each context owns its own <see cref="GitSettings.KnownHostsPathOverride"/>,
    /// <see cref="GitSettings.AgentSocketPathOverride"/>, and strictness
    /// flags (<see cref="GitSettings.StrictObjectCreation"/>,
    /// <see cref="GitSettings.StrictHashVerification"/>,
    /// <see cref="GitSettings.OwnerValidation"/>).
    /// </summary>
    public GitSettings Settings { get; }

    /// <summary>
    /// Diagnostic trace sink for this context. Each context owns
    /// its own level and subscribers.
    /// </summary>
    public GitTrace Trace { get; }

    /// <summary>
    /// Test hook mirroring <c>git_fs_path__set_owner</c> (fs_path.c:1829-1832):
    /// when non-zero, repository-ownership checks report the mocked owner
    /// instead of stat-ing the path (fs_path.c:1935-1938), exactly like
    /// libgit2's own test suite (<c>tests/libgit2/repo/open.c</c>). Internal —
    /// used by the parity tests to exercise the GIT_EOWNER path without
    /// chown privileges. Cleared (<see cref="GitFsPathOwner.None"/>) by
    /// default so real ownership checks run.
    /// </summary>
    internal GitFsPathOwner MockOwner { get; set; }

    /// <summary>
    /// System/global/XDG/template directory resolver for this context. Each
    /// context owns its own directory cache and overrides.
    /// </summary>
    public GitSystemDirs Dirs { get; }

    /// <summary>
    /// Transport registry for this context. Each context owns
    /// its own custom transport registrations.
    /// </summary>
    public GitTransportRegistry Transports { get; }

    /// <summary>
    /// Filter registry for this context. Each context owns its
    /// own filter registrations.
    /// </summary>
    public FilterRegistry Filters { get; }

    /// <summary>
    /// Merge driver registry for this context. Each context
    /// owns its own driver registrations.
    /// </summary>
    public GitMergeDriverRegistry MergeDrivers { get; }

    /// <summary> Frozen built-in diff-driver archetypes (<c>Auto</c>/<c>Binary</c>/ <c>Text</c>) for this context. Each context owns
    /// its own instances. Internal because <c>DiffDriver</c> is an internal implementation type — the archetypes are consumed only by the diff engine
    /// (<c>DiffDriverRegistry</c> + standalone <c>PatchGenerator</c> factories). Emptying the <c>NoStaticMutableStateOutsideGitContext</c> allowlist required
    /// moving these off process-global singletons on <c>DiffDriver</c>. </summary>
    internal GitDiffDrivers DiffDrivers { get; }

    /// <summary>
    /// Disposes the context and all owned sub-objects (registries, trace
    /// subscriptions). Clears <see cref="Trace"/> subscriptions so event
    /// handlers do not outlive the context.
    /// </summary>
    public void Dispose()
    {
        Trace.ClearSubscriptions();
        MergeDrivers.Dispose();
    }
}

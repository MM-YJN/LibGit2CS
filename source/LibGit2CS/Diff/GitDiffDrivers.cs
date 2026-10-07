// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Diff;

/// <summary> Per-context holder for the frozen built-in diff-driver archetypes (<see cref="Auto"/>, <see cref="Binary"/>, <see cref="Text"/>). Managed analogue
/// of the former process-global singletons on <c>DiffDriver</c>, lifted onto <see cref="Core.GitContext.DiffDrivers"/> so each
/// context owns its own instances and there is no process-global mutable state outside <c>GitContext</c>. </summary> <remarks> <para> <b>Why per-context.</b>
/// The archetypes are <c>init</c>-only and never mutated after construction,
/// so they are conceptually constant; the per-context ownership matches the
/// other sub-objects on <see cref="Core.GitContext"/> (<c>Env</c>,
/// <c>Settings</c>, <c>Trace</c>, <c>Dirs</c>, <c>Transports</c>,
/// <c>Filters</c>, <c>MergeDrivers</c>). </para>
/// <para> <b>Accessibility.</b> This class and its properties are <see langword="internal"/> because <see cref="DiffDriver"/> is an internal implementation
/// type of the diff engine; promoting it to <see langword="public"/> would expand the public surface for no consumer benefit. This is the one deviation from
/// the other <c>GitContext</c> sub-objects, which are all <see langword="public"/> — here the exposed element type is internal. </para> <para>
/// All <c>DiffDriver.Auto</c>/<c>Binary</c>/<c>Text</c> read sites in
/// <c>source/LibGit2CS/</c> (<c>DiffDriverRegistry</c> + standalone
/// <c>PatchGenerator</c> factories) route through this holder. </para> </remarks>
internal sealed class GitDiffDrivers
{
    /// <summary> Constructs the three archetype instances. Mirrors the field initializers of the former <c>DiffDriver.Auto</c>/<c>Binary</c>/ <c>Text</c>
    /// singletons verbatim. </summary>
    public GitDiffDrivers()
    {
        Auto = new DiffDriver("auto");

        Binary = new DiffDriver("binary")
        {
            Type = DiffDriverType.Binary,
            BinaryFlags = GitDiffOptionsFlags.ForceBinary,
        };

        Text = new DiffDriver("text")
        {
            Type = DiffDriverType.Text,
            BinaryFlags = GitDiffOptionsFlags.ForceText,
        };
    }

    /// <summary>
    /// The auto-detect archetype: auto-detect binary, no funcname patterns.
    /// Used by the standalone patch factories (blob/buffer) and as the default
    /// when a path has no <c>diff</c> attribute.
    /// </summary>
    internal DiffDriver Auto { get; }

    /// <summary>
    /// The force-binary archetype: forces a binary diff. Used when a path's
    /// <c>diff</c> attribute is <c>false</c>, or <c>diff.&lt;name&gt;.binary</c>
    /// is set.
    /// </summary>
    internal DiffDriver Binary { get; }

    /// <summary>
    /// The force-text archetype: forces a text diff. Used when a path's
    /// <c>diff</c> attribute is <c>true</c>.
    /// </summary>
    internal DiffDriver Text { get; }
}

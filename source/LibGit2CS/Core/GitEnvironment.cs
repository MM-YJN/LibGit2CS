// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Core;

/// <summary> A per-context snapshot of the process environment block. Managed analogue of libgit2's <c>getenv</c> calls, lifted onto <see
/// cref="GitContext.Env"/> so each context owns its own mutable copy and mutations never leak to the process or to concurrently-running contexts. </summary>
/// <remarks> <para> <b>Snapshot model.</b> The constructor eagerly copies <c>Environment.GetEnvironmentVariables</c> into an internal dictionary. Subsequent
/// reads via the indexer consult only the snapshot — there is no live fall-through to <c>Environment.GetEnvironmentVariable</c>. This means a context
/// constructed before any env-var mutation is unaffected by later process-wide <c>Environment.SetEnvironmentVariable</c> calls from other code (including
/// concurrently-running tests). </para> <para> <b>Indexer semantics.</b> <c>Env[name]</c> returns the snapshotted value or <c>null</c> if the variable is
/// absent. <c>Env[name] = value</c> records an override; <c>Env[name] = null</c> <b>removes</b> the entry, causing subsequent reads to return <c>null</c> (i.e.
/// the variable is unset for this context). This mirrors how a test would historically call <c>Environment.SetEnvironmentVariable(name, null)</c> to clear a
/// variable. </para> <para> <b>Scope.</b> Only covers variables read via <c>Environment.GetEnvironmentVariable</c>. OS-level resolution such as
/// <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c> is <b>not</b> routed through this class — it is an OS API, not an env-var. </para> <para>
/// Per-context instance held by <see cref="GitContext.Env"/>. All
/// <c>Environment.GetEnvironmentVariable</c> reads in <c>source/LibGit2CS/</c>
/// route through this snapshot. </para> </remarks>
public sealed class GitEnvironment
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);

    /// <summary>
    /// Snapshots the current process environment. Keys and values are copied
    /// eagerly; later process-wide mutations do not affect this instance.
    /// </summary>
    public GitEnvironment()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                _vars[key] = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the snapshotted value of the named variable. Getter returns
    /// <c>null</c> if the variable is absent from the snapshot. Setter with a
    /// <c>null</c> value removes the entry (variable becomes unset for this
    /// context); setter with a non-null value records/overwrites the entry.
    /// </summary>
    /// <param name="name">The variable name (case-sensitive on POSIX, matching <c>getenv</c>).</param>
    /// <returns>The snapshotted value, or <c>null</c> if unset.</returns>
    public string? this[string name]
    {
        get => _vars.TryGetValue(name, out string? value) ? value : null;
        set
        {
            ArgumentNullException.ThrowIfNull(name);
            if (value is null)
            {
                _vars.Remove(name);
            }
            else
            {
                _vars[name] = value;
            }
        }
    }

    /// <summary>
    /// Removes all entries from the snapshot. Subsequent reads return
    /// <c>null</c> for every variable. Intended for tests that need a pristine
    /// (empty) environment.
    /// </summary>
    public void Clear() => _vars.Clear();
}

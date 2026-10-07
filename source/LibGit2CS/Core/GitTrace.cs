// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Core;

/// <summary>
/// Diagnostic tracing. Managed equivalent of libgit2's <c>trace.c</c>.
/// </summary>
/// <remarks>
/// Replaces the global <c>git_trace__data</c> + <c>git_trace_set</c> mechanism
/// with an instance event. Subscribers attach via <see cref="OnTrace"/> and
/// filter by <see cref="Level"/>. The memory barrier in libgit2's
/// <c>git_trace_set</c> is handled by <see cref="Volatile"/>'s read/write in the
/// property accessors. An instance is held by each <see cref="GitContext"/> so
/// two contexts never share trace subscriptions or levels.
/// </remarks>
public sealed class GitTrace
{
    private int _level;

    /// <summary>
    /// The current trace level. Messages at or below this level are emitted.
    /// Default is <see cref="GitTraceLevel.None"/> (tracing off).
    /// </summary>
    public GitTraceLevel Level
    {
        get => (GitTraceLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    /// <summary>
    /// Trace event. Subscribe to receive diagnostic messages at or below
    /// <see cref="Level"/>. Replaces libgit2's single-callback
    /// <c>git_trace_set</c>.
    /// </summary>
    [SuppressMessage("Design", "CA1003:Use generic event handler instances", Justification = "Action<TraceLevel,string> is intentional — no custom EventArgs needed.")]
    public event Action<GitTraceLevel, string>? OnTrace;

    /// <summary>
    /// Emits a trace message at the given level if <see cref="Level"/> permits.
    /// </summary>
    public void Emit(GitTraceLevel level, string message)
    {
        if (level <= Level)
        {
            OnTrace?.Invoke(level, message);
        }
    }

    /// <summary>
    /// Detaches all <see cref="OnTrace"/> subscribers. Called by
    /// <see cref="GitContext.Dispose"/> so that event handlers do not
    /// outlive the context that owns the trace.
    /// </summary>
    internal void ClearSubscriptions() => OnTrace = null;
}

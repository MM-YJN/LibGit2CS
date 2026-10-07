// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Repository;

namespace LibGit2CS.Status;

/// <summary>
/// Per-repository ignore state — the internal ignore file (default rules +
/// user-added rules via <see cref="GitRepository.IgnoreAddRuleAsync"/>). Attached to
/// <see cref="GitRepository"/> as <see cref="GitRepository.IgnoreState"/>.
/// </summary>
internal sealed class IgnoreState
{
    private readonly GitRepository _repo;
    private IgnoreFile? _internal;
    private readonly object _lock = new();

    internal IgnoreState(GitRepository repo) => _repo = repo;

    /// <summary>The internal ignore file (default rules + user-added rules).</summary>
    public async ValueTask<IgnoreFile> GetInternalAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _internal ??= new IgnoreFile("[internal]exclude");
        }

        bool ignoreCase = await _repo.Config.GetBoolAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_internal.Rules.Count == 0)
            {
                _internal.ParseBuffer(".\n..\n.git\n", ignoreCase, context: null);
            }
        }

        return _internal;
    }

    /// <summary>Adds in-memory rules. Matches <c>git_ignore_add_rule</c>.</summary>
    public async ValueTask AddRuleAsync(string rules, CancellationToken cancellationToken = default)
    {
        bool ignoreCase = await _repo.Config.GetBoolAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);
        IgnoreFile internalFile = await GetInternalAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            internalFile.ParseBuffer(rules, ignoreCase, context: null);
        }
    }

    /// <summary>Clears internal rules and re-seeds defaults. Matches <c>git_ignore_clear_internal_rules</c>.</summary>
    public async ValueTask ClearInternalRulesAsync(CancellationToken cancellationToken = default)
    {
        bool ignoreCase = await _repo.Config.GetBoolAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _internal = new IgnoreFile("[internal]exclude");
            _internal.ParseBuffer(".\n..\n.git\n", ignoreCase, context: null);
        }
    }
}

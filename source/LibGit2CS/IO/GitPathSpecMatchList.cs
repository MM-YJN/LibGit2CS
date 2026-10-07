// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary> Result of a pathspec match operation. Matches <c>git_pathspec_match_list</c>. </summary> <remarks> <b>Byte-faithful.</b> Holds <see
/// cref="GitPath"/> (raw UTF-8 bytes) so non-UTF-8 paths round-trip byte-exact. The <see cref="string"/>-returning egress methods (<see cref="GetEntry"/>, <see
/// cref="GetFailedEntry"/>) decode via <see cref="GitPath.ToUtf8String"/>; new callers should use <see cref="GetEntryPath"/>/<see cref="GetFailedEntryPath"/>.
/// </remarks>
public sealed class GitPathSpecMatchList
{
    private readonly List<GitPath> _matches;
    private readonly List<GitPath> _failures;

    internal GitPathSpecMatchList(List<GitPath> matches, List<GitPath> failures)
    {
        _matches = matches;
        _failures = failures;
    }

    /// <summary>Number of matched paths.</summary>
    public int EntryCount => _matches.Count;

    /// <summary>
    /// Gets the matched path at the given index as a byte-faithful
    /// <see cref="GitPath"/>.
    /// </summary>
    public GitPath GetEntryPath(int index) => _matches[index];

    /// <summary>
    /// Gets the matched path at the given index as a UTF-8 decoded string.
    /// Egress bridge — decodes via <see cref="GitPath.ToUtf8String"/>.
    /// </summary>
    public string GetEntry(int index) => _matches[index].ToUtf8String();

    /// <summary>
    /// Enumerates matched paths as byte-faithful <see cref="GitPath"/> values.
    /// </summary>
    public IEnumerable<GitPath> EntryPaths => _matches;

    /// <summary>
    /// Enumerates matched paths as UTF-8 decoded strings. Egress bridge.
    /// </summary>
    public IEnumerable<string> Entries => _matches.Select(p => p.ToUtf8String());

    /// <summary>Number of patterns that did not match any path.</summary>
    public int FailedEntryCount => _failures.Count;

    /// <summary>
    /// Gets the failed pattern at the given index as a byte-faithful
    /// <see cref="GitPath"/>.
    /// </summary>
    public GitPath GetFailedEntryPath(int index) => _failures[index];

    /// <summary>
    /// Gets the failed pattern at the given index as a UTF-8 decoded string.
    /// Egress bridge.
    /// </summary>
    public string GetFailedEntry(int index) => _failures[index].ToUtf8String();

    /// <summary>
    /// Enumerates failed patterns as byte-faithful <see cref="GitPath"/> values.
    /// </summary>
    public IEnumerable<GitPath> FailedEntryPaths => _failures;

    /// <summary>
    /// Enumerates failed patterns as UTF-8 decoded strings. Egress bridge.
    /// </summary>
    public IEnumerable<string> FailedEntries => _failures.Select(p => p.ToUtf8String());
}

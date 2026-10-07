// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Repository;

namespace LibGit2CS.Diff;

/// <summary>
/// Strategy interface for a <see cref="GitPatch"/> facade's backing data. Matches
/// the <c>patch_fn</c> vtable in libgit2's <c>git_patch</c>
/// (<c>patch.h:22-39</c>). Two implementations:
/// <list type="bullet">
/// <item><see cref="PatchGenerator"/> — generated diffs (lazy XDiff).</item>
/// <item><see cref="ParsedPatchSource"/> — parsed patches (from text).</item>
/// </list>
/// </summary>
/// <remarks>
/// This interface enables <see cref="LibGit2CS.Diff.GitPatch.FromBuffer(System.ReadOnlyMemory{byte}, LibGit2CS.Diff.GitPatchParseOptions?)"/> to produce a
/// <see cref="GitPatch"/> backed by parsed data rather than a generator, while
/// keeping the public <see cref="GitPatch"/> surface uniform. The generator
/// (generator-backed) is unaffected — <see cref="PatchGenerator"/> simply
/// implements this interface.
/// </remarks>
internal interface IPatchSource
{
    GitDiffDelta Delta { get; }
    int IdAbbrevLength { get; }
    string OldPrefix { get; }
    string NewPrefix { get; }

    /// <summary>
    /// The patch's owning repository, or null. Mirrors <c>git_patch::repo</c>:
    /// set for patches from diffs/blobs, null for buffer and parsed patches.
    /// Used by the printer to resolve <c>opts.id_abbrev == 0</c> against
    /// <c>core.abbrev</c> (diff_print_info_init__common, diff_print.c:44-63).
    /// </summary>
    GitRepository? Repo { get; }

    /// <summary>
    /// The diff options flags the patch was created with. Mirrors
    /// <c>patch->diff_opts.flags</c> (diff_print.c:106) — used by the printer
    /// for the <c>diff_print_patch_file</c> skips in
    /// libgit2 1.9.4, e.g. UNTRACKED deltas
    /// are only printed with <see cref="GitDiffOptionsFlags.ShowUntrackedContent"/>.
    /// </summary>
    GitDiffOptionsFlags DiffFlags { get; }

    /// <summary>
    /// Materializes the patch (lazy XDiff for generators; no-op for parsed)
    /// and returns the hunks.
    /// </summary>
    Task<IReadOnlyList<GitDiffHunk>> GetHunksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes the patch and returns the binary patch data, or null if
    /// this is not a binary delta.
    /// </summary>
    Task<GitBinaryPatch?> GetBinaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes the patch and returns whether this is a binary file change.
    /// </summary>
    Task<bool> GetIsBinaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes line statistics (context, additions, deletions). Matches
    /// <c>git_patch_line_stats</c>.
    /// </summary>
    Task<(int context, int additions, int deletions)> LineStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the patch content has been materialized. For generators this
    /// triggers lazy XDiff; for parsed patches this is a no-op (content is
    /// already available from parse).
    /// </summary>
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IPatchSource"/> backed by a parsed <see cref="ParsedPatch"/>.
/// The patch data is already fully materialized from text — <see cref="EnsureCreatedAsync"/>
/// is a no-op.
/// </summary>
internal sealed class ParsedPatchSource : IPatchSource
{
    private readonly ParsedPatch _parsed;

    public ParsedPatchSource(ParsedPatch parsed)
    {
        _parsed = parsed;
    }

    public GitDiffDelta Delta => _parsed.Delta;

    public int IdAbbrevLength => 7; // default for parsed patches

    public GitRepository? Repo => null; // git_patch_from_buffer: patch->repo = NULL

    // C zeroes patch->diff_opts for parsed patches (patch_parse.c), and the
    // skipped statuses (UNTRACKED/IGNORED/UNREADABLE) cannot be expressed in
    // patch text anyway, so Normal (0) is faithful.
    public GitDiffOptionsFlags DiffFlags => GitDiffOptionsFlags.Normal;

    public string OldPrefix => _parsed.OldPrefix is { } prefix
        ? Encoding.UTF8.GetString(prefix.Span)
        : "a/";

    public string NewPrefix => _parsed.NewPrefix is { } prefix
        ? Encoding.UTF8.GetString(prefix.Span)
        : "b/";

    public Task<IReadOnlyList<GitDiffHunk>> GetHunksAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitDiffHunk>>(_parsed.Hunks);

    public Task<GitBinaryPatch?> GetBinaryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_parsed.Binary);

    public Task<bool> GetIsBinaryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_parsed.IsBinary);

    public Task<(int context, int additions, int deletions)> LineStatsAsync(CancellationToken cancellationToken = default)
    {
        int ctx = 0;
        int adds = 0;
        int dels = 0;
        foreach (GitDiffHunk hunk in _parsed.Hunks)
        {
            foreach (GitDiffLine line in hunk.Lines)
            {
                switch (line.Origin)
                {
                    // C's
                    // git_patch_line_stats only counts CONTEXT/ADDITION/
                    // DELETION — the *_EOFNL origins hit `default: break`
                    // ("diff --stat and --numstat don't count EOFNL marks",
                    // patch.c:93-128). Counting them would inflate
                    // Insertions/Deletions by one per EOFNL marker.
                    case GitDiffLineOrigin.Context:
                        ctx++;
                        break;
                    case GitDiffLineOrigin.Addition:
                        adds++;
                        break;
                    case GitDiffLineOrigin.Deletion:
                        dels++;
                        break;
                }
            }
        }

        return Task.FromResult((ctx, adds, dels));
    }

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        // No-op: parsed patches are already materialized.
        return Task.CompletedTask;
    }

    /// <summary>The underlying parsed patch data (for apply).</summary>
    public ParsedPatch Parsed => _parsed;
}

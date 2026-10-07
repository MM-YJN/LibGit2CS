// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Diff;

/// <summary>
/// A <see cref="GitDiff"/> backed by pre-parsed <see cref="GitPatch"/> objects.
/// Managed equivalent of <c>git_diff_parsed</c> (<c>diff_parse.h:14-18</c>).
/// </summary>
/// <remarks>
/// <para>
/// In C, <c>git_diff_parsed</c> embeds a <c>git_diff base</c> with a
/// <c>patches</c> vector and a <c>patch_fn = git_patch_parsed_from_diff</c>
/// vtable that returns patches by index. In C#, this class implements
/// <see cref="IDiffPatchSource"/> and the <see cref="GitDiff"/> facade holds it
/// via the <c>_patchSource</c> field.
/// </para>
/// <para>
/// The parse loop (matches <c>git_diff_from_buffer</c>, diff_parse.c:68-122)
/// calls <see cref="PatchParser.Parse(ParseContext, GitPatchParseOptions)"/>
/// repeatedly with a shared <see cref="ParseContext"/>, advancing the cursor
/// until the buffer is exhausted or no more patches are found (graceful EOF
/// after ≥1 patch).
/// </para>
/// </remarks>
internal sealed class DiffParsed : IDiffPatchSource
{
    private readonly List<GitPatch> _patches = [];
    private readonly List<GitDiffDelta> _deltas = [];

    private DiffParsed()
    {
    }

    /// <summary>Number of parsed patches.</summary>
    public int PatchCount => _patches.Count;

    /// <summary>The parsed deltas (one per patch).</summary>
    public IReadOnlyList<GitDiffDelta> Deltas => _deltas;

    /// <summary>Retrieves the patch at <paramref name="index"/>.</summary>
    public ValueTask<GitPatch> GetPatchAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= _patches.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ValueTask.FromResult(_patches[index]);
    }

    /// <summary>
    /// Parses a multi-file patch from raw bytes. Matches
    /// <c>git_diff_from_buffer</c> (diff_parse.c:68-122). Returns null if no
    /// valid patch is found.
    /// </summary>
    public static DiffParsed? FromBuffer(ReadOnlyMemory<byte> content, GitDiffParseOptions? options = null)
    {
        GitDiffParseOptions dpOpts = options ?? new GitDiffParseOptions();
        var patchOpts = new GitPatchParseOptions
        {
            PrefixLength = dpOpts.PrefixLength,
            OidType = dpOpts.OidType,
        };

        var ctx = new ParseContext();
        ctx.Init(content);

        var result = new DiffParsed();
        bool anyParsed = false;

        while (ctx.HasRemaining)
        {
            ParsedPatch? patch = PatchParser.Parse(ctx, patchOpts);
            if (patch is null)
            {
                if (anyParsed)
                {
                    break; // graceful EOF after ≥1 patch
                }

                return null; // no patches found at all
            }

            anyParsed = true;
            result._patches.Add(new GitPatch(new ParsedPatchSource(patch)));
            result._deltas.Add(patch.Delta);
        }

        return result._patches.Count > 0 ? result : null;
    }
}

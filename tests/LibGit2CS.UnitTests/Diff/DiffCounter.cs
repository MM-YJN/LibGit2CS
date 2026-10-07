using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Aggregates file/hunk/line counts from a <see cref="Diff"/>, mirroring
/// libgit2's <c>diff_expects</c> + <c>diff_file_cb</c>/<c>diff_hunk_cb</c>/
/// <c>diff_line_cb</c> (<c>tests/libgit2/diff/diff_helpers.c</c>). Used by the
/// golden tests for the statistical (count-based) assertions that complement
/// the byte-exact patch comparison.
/// </summary>
internal sealed class DiffCounter
{
    public int Files;
    public int FilesBinary;
    public int Hunks;
    public int Lines;
    public int LineContext;
    public int LineAdds;
    public int LineDels;
    // Indexed by DeltaStatus int value. Sized to cover all DeltaStatus members.
    private readonly int[] _fileStatus = new int[16];

    public int this[GitDeltaStatus s] => _fileStatus[(int)s];

    /// <summary>
    /// Walks <paramref name="diff"/>: counts deltas by status (cheap path) and
    /// materializes each patch to count hunks + lines by origin (the same
    /// work <c>git_diff_foreach</c> performs).
    /// </summary>
    public static async Task<DiffCounter> CountAsync(GitDiff diff)
    {
        var c = new DiffCounter();
        for (int i = 0; i < diff.DeltaCount; i++)
        {
            GitDiffDelta delta = diff.GetDelta(i);
            c._fileStatus[(int)delta.Status]++;
            c.Files++;

            using GitPatch patch = await GitPatch.FromDiffAsync(diff, i);
            c.Hunks += await patch.GetHunkCountAsync();
            await AccumulateAsync(patch, c);

            // clar's diff_file_cb increments files_binary when
            // GIT_DIFF_FLAG_BINARY is set on the delta. The flag is populated
            // lazily during patch materialization (DiffFileContent binary
            // detection), so check it after FromDiff — same ordering as clar's
            // diff_foreach_via_iterator (patch_from_diff precedes file_cb).
            if ((delta.Flags & GitDiffFileFlags.Binary) != 0)
            {
                c.FilesBinary++;
            }
        }

        return c;
    }

    /// <summary>
    /// Counts a single standalone <see cref="GitPatch"/> (blob/buffer diff created
    /// via <see cref="GitPatch.FromBlobs"/>/<see cref="GitPatch.FromBuffers"/>).
    /// Equivalent to running <c>diff_foreach_via_iterator</c> over a one-delta
    /// diff. The delta status comes from <see cref="GitPatch.Delta"/>.
    /// </summary>
    public static async Task<DiffCounter> CountAsync(GitPatch patch)
    {
        var c = new DiffCounter();
        GitDiffDelta delta = patch.Delta;
        c._fileStatus[(int)delta.Status]++;
        c.Files++;
        c.Hunks += await patch.GetHunkCountAsync();
        await AccumulateAsync(patch, c);
        if (await patch.GetIsBinaryAsync() || (delta.Flags & GitDiffFileFlags.Binary) != 0)
        {
            c.FilesBinary++;
        }

        return c;
    }

    private static async Task AccumulateAsync(GitPatch patch, DiffCounter c)
    {
        int hunkCount = await patch.GetHunkCountAsync();
        for (int h = 0; h < hunkCount; h++)
        {
            GitDiffHunk hunk = (await patch.GetHunkAsync(h))!;
            foreach (GitDiffLine line in hunk.Lines)
            {
                // Match clar's diff_line_cb (diff_helpers.c:126-155) exactly:
                // EOFNL-marker lines count toward their respective bucket
                // (CONTEXT_EOFNL→ctxt, ADD_EOFNL→adds, DEL_EOFNL→dels) AND
                // toward `lines`. File-header/hunk-header/binary lines never
                // appear via Patch.GetHunk (only content+eofnl lines do), so
                // `lines == ctxt + adds + dels` holds, same as clar.
                switch (line.Origin)
                {
                    case GitDiffLineOrigin.Context:
                    case GitDiffLineOrigin.ContextEofnl:
                        c.LineContext++;
                        c.Lines++;
                        break;
                    case GitDiffLineOrigin.Addition:
                    case GitDiffLineOrigin.AddEofnl:
                        c.LineAdds++;
                        c.Lines++;
                        break;
                    case GitDiffLineOrigin.Deletion:
                    case GitDiffLineOrigin.DelEofnl:
                        c.LineDels++;
                        c.Lines++;
                        break;
                }
            }
        }
    }
}

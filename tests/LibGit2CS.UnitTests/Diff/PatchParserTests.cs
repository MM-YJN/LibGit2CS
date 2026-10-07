using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Diff;

public class PatchParserTests
{
    [Fact]
    public void FromBuffer_SimpleModification_ParsesDeltaAndHunks()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            " line1\n" +
            "-line2\n" +
            "+line2-modified\n" +
            " line3\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(GitDeltaStatus.Modified, patch!.Delta.Status);
        Assert.Equal("file.txt", patch.Delta.OldFile.Path?.ToUtf8String());
        Assert.Equal("file.txt", patch.Delta.NewFile.Path?.ToUtf8String());
        Assert.Equal(2, patch.Delta.FileCount);
        Assert.Single(patch.Hunks);

        GitDiffHunk hunk = patch.Hunks[0];
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(3, hunk.OldCount);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(3, hunk.NewCount);
        Assert.Equal(4, hunk.Lines.Count);
        Assert.Equal(GitDiffLineOrigin.Context, hunk.Lines[0].Origin);
        Assert.Equal(GitDiffLineOrigin.Deletion, hunk.Lines[1].Origin);
        Assert.Equal(GitDiffLineOrigin.Addition, hunk.Lines[2].Origin);
        Assert.Equal(GitDiffLineOrigin.Context, hunk.Lines[3].Origin);
    }

    [Fact]
    public void FromBuffer_NewFile_SetsAddedStatus()
    {
        const string patchText =
            "diff --git a/newfile.txt b/newfile.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..af8f41d\n" +
            "--- /dev/null\n" +
            "+++ b/newfile.txt\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+line1\n" +
            "+line2\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(GitDeltaStatus.Added, patch!.Delta.Status);
        Assert.Equal(1, patch.Delta.FileCount);
        Assert.Equal(GitFileMode.Regular, patch.Delta.NewFile.Mode);
        Assert.Equal("newfile.txt", patch.Delta.NewFile.Path?.ToUtf8String());

        GitDiffHunk hunk = patch.Hunks[0];
        Assert.Equal(0, hunk.OldStart);
        Assert.Equal(0, hunk.OldCount);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(2, hunk.NewCount);
        Assert.All(hunk.Lines, l => Assert.Equal(GitDiffLineOrigin.Addition, l.Origin));
    }

    [Fact]
    public void FromBuffer_DeletedFile_SetsDeletedStatus()
    {
        const string patchText =
            "diff --git a/oldfile.txt b/oldfile.txt\n" +
            "deleted file mode 100644\n" +
            "index af8f41d..0000000\n" +
            "--- a/oldfile.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n" +
            "-line1\n" +
            "-line2\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(GitDeltaStatus.Deleted, patch!.Delta.Status);
        Assert.Equal(1, patch.Delta.FileCount);
        Assert.Equal(GitFileMode.Regular, patch.Delta.OldFile.Mode);
        Assert.Equal("oldfile.txt", patch.Delta.OldFile.Path?.ToUtf8String());

        GitDiffHunk hunk = patch.Hunks[0];
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(2, hunk.OldCount);
        Assert.Equal(0, hunk.NewStart);
        Assert.Equal(0, hunk.NewCount);
        Assert.All(hunk.Lines, l => Assert.Equal(GitDiffLineOrigin.Deletion, l.Origin));
    }

    [Fact]
    public void FromBuffer_RenameWithSimilarity_ParsesRenamePaths()
    {
        const string patchText =
            "diff --git a/old.txt b/new.txt\n" +
            "similarity index 86%\n" +
            "rename from old.txt\n" +
            "rename to new.txt\n" +
            "index af8f41d..a97157a 100644\n" +
            "--- a/old.txt\n" +
            "+++ b/new.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            " context\n" +
            "-old line\n" +
            "+new line\n" +
            " context\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(GitDeltaStatus.Renamed, patch!.Delta.Status);
        Assert.Equal(86, patch.Delta.Similarity);
        Assert.Equal("old.txt", patch.Delta.OldFile.Path?.ToUtf8String());
        Assert.Equal("new.txt", patch.Delta.NewFile.Path?.ToUtf8String());
    }

    [Fact]
    public void FromBuffer_ModeChange_ParsesOldAndNewMode()
    {
        const string patchText =
            "diff --git a/script.sh b/script.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        // Mode change with no hunks and no binary → check_patch rejects as "patch with no hunks"
        // unless the mode differs. C allows it when old_mode != new_mode.
        Assert.Equal(GitFileMode.Regular, patch!.Delta.OldFile.Mode);
        Assert.Equal(GitFileMode.Executable, patch.Delta.NewFile.Mode);
    }

    [Fact]
    public void FromBuffer_NoNewlineAtEof_AddsEofnlLine()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,2 +1,2 @@\n" +
            " line1\n" +
            "-line2\n" +
            "\\ No newline at end of file\n" +
            "+line2-new\n" +
            "\\ No newline at end of file\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        GitDiffHunk hunk = patch!.Hunks[0];
        // context, deletion, AddEofnl (deletion's EOFNL = new file has newline), addition, DelEofnl (addition's EOFNL = old file had newline)
        Assert.Equal(5, hunk.Lines.Count);
        Assert.Equal(GitDiffLineOrigin.Context, hunk.Lines[0].Origin);
        Assert.Equal(GitDiffLineOrigin.Deletion, hunk.Lines[1].Origin);
        Assert.Equal(GitDiffLineOrigin.AddEofnl, hunk.Lines[2].Origin);
        Assert.Equal(GitDiffLineOrigin.Addition, hunk.Lines[3].Origin);
        Assert.Equal(GitDiffLineOrigin.DelEofnl, hunk.Lines[4].Origin);
    }

    [Fact]
    public void FromBuffer_BinaryFilesDiffer_SetsBinaryFlag()
    {
        const string patchText =
            "diff --git a/binary.bin b/binary.bin\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "Binary files a/binary.bin and b/binary.bin differ\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.True((patch!.Delta.Flags & GitDiffFileFlags.Binary) != 0);
        Assert.NotNull(patch.Binary);
        Assert.False(patch.Binary.ContainsData);
    }

    [Fact]
    public void FromBuffer_GitBinaryPatch_ParsesLiteralAndDelta()
    {
        // Construct a minimal binary patch with literal sides.
        // "literal 0\n" + empty body means zero-length literal.
        const string patchText =
            "diff --git a/file.bin b/file.bin\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "GIT binary patch\n" +
            "literal 0\n" +
            "\n" +
            "literal 0\n" +
            "\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.True((patch!.Delta.Flags & GitDiffFileFlags.Binary) != 0);
        Assert.NotNull(patch.Binary);
        Assert.True(patch.Binary.ContainsData);
        Assert.Equal(GitBinaryPatchType.Literal, patch.Binary.NewFile.Type);
        Assert.Equal(GitBinaryPatchType.Literal, patch.Binary.OldFile.Type);
        Assert.Equal(0, patch.Binary.NewFile.InflatedLength);
        Assert.Equal(0, patch.Binary.OldFile.InflatedLength);
    }

    [Fact]
    public void FromBuffer_IndexHeader_ParsesOidsAndMode()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(7, patch!.Delta.OldFile.IdAbbrev);
        Assert.Equal(7, patch.Delta.NewFile.IdAbbrev);
        // "94aaae8" → verify the OID bytes
        Assert.True(GitOid.TryParse("94aaae8", GitHashAlgorithmKind.Sha1, out GitOid expectedOld));
        Assert.Equal(expectedOld, patch.Delta.OldFile.Id);
        Assert.True(GitOid.TryParse("af8f41d", GitHashAlgorithmKind.Sha1, out GitOid expectedNew));
        Assert.Equal(expectedNew, patch.Delta.NewFile.Id);
        Assert.Equal(GitFileMode.Regular, patch.Delta.OldFile.Mode);
        Assert.Equal(GitFileMode.Regular, patch.Delta.NewFile.Mode);
    }

    [Fact]
    public void FromBuffer_MultipleHunks_ParsesAllHunks()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,2 +1,2 @@\n" +
            " line1\n" +
            "-line2\n" +
            "+line2-new\n" +
            "@@ -10,2 +10,2 @@\n" +
            " line10\n" +
            "-line11\n" +
            "+line11-new\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(2, patch!.Hunks.Count);

        GitDiffHunk h1 = patch.Hunks[0];
        Assert.Equal(1, h1.OldStart);
        Assert.Equal(3, h1.Lines.Count);

        GitDiffHunk h2 = patch.Hunks[1];
        Assert.Equal(10, h2.OldStart);
        Assert.Equal(3, h2.Lines.Count);
    }

    [Fact]
    public void FromBuffer_NoPatchFound_ReturnsNull()
    {
        const string text = "this is not a patch\njust some text\n";

        ParsedPatch? patch = PatchParser.FromBuffer(text);

        Assert.Null(patch);
    }

    [Fact]
    public void FromBuffer_QuotedPath_ParsesCorrectly()
    {
        const string patchText =
            "diff --git \"a/file with spaces.txt\" \"b/file with spaces.txt\"\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- \"a/file with spaces.txt\"\n" +
            "+++ \"b/file with spaces.txt\"\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal("file with spaces.txt", patch!.Delta.OldFile.Path?.ToUtf8String());
        Assert.Equal("file with spaces.txt", patch.Delta.NewFile.Path?.ToUtf8String());
    }

    [Fact]
    public void FromBuffer_CopyWithSimilarity_ParsesCopyStatus()
    {
        const string patchText =
            "diff --git a/source.txt b/copy.txt\n" +
            "similarity index 100%\n" +
            "copy from source.txt\n" +
            "copy to copy.txt\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(GitDeltaStatus.Copied, patch!.Delta.Status);
        Assert.Equal(100, patch.Delta.Similarity);
        Assert.Equal("source.txt", patch.Delta.OldFile.Path?.ToUtf8String());
        Assert.Equal("copy.txt", patch.Delta.NewFile.Path?.ToUtf8String());
    }

    [Fact]
    public void FromBuffer_DissimilarityIndex_InvertsToSimilarity()
    {
        const string patchText =
            "diff --git a/old.txt b/new.txt\n" +
            "dissimilarity index 14%\n" +
            "rename from old.txt\n" +
            "rename to new.txt\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        Assert.Equal(86, patch!.Delta.Similarity); // 100 - 14
    }

    [Fact]
    public void FromBuffer_CustomPrefixLength_StripsCorrectPrefix()
    {
        const string patchText =
            "diff --git src/dir/file.txt src/dir/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- src/dir/file.txt\n" +
            "+++ src/dir/file.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText, new GitPatchParseOptions { PrefixLength = 2 });

        Assert.NotNull(patch);
        Assert.Equal("file.txt", patch!.Delta.OldFile.Path?.ToUtf8String());
        Assert.Equal("file.txt", patch.Delta.NewFile.Path?.ToUtf8String());
        Assert.True(patch.OldPrefix is { } oldPrefix && oldPrefix.Span.SequenceEqual("src/dir/"u8));
        Assert.True(patch.NewPrefix is { } newPrefix && newPrefix.Span.SequenceEqual("src/dir/"u8));
    }

    [Fact]
    public void FromBuffer_EmptyPatch_ReturnsNull()
    {
        ParsedPatch? patch = PatchParser.FromBuffer(string.Empty);

        Assert.Null(patch);
    }

    [Fact]
    public void FromBuffer_PrefixlessPaths_WithPrefixLengthZero()
    {
        const string patchText =
            "diff --git file.txt file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- file.txt\n" +
            "+++ file.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText, new GitPatchParseOptions { PrefixLength = 0 });

        Assert.NotNull(patch);
        Assert.Equal("file.txt", patch!.Delta.OldFile.Path?.ToUtf8String());
        Assert.Equal("file.txt", patch.Delta.NewFile.Path?.ToUtf8String());
    }

    [Fact]
    public void FromBuffer_HunkHeaderWithFunctionName_PreservesInHeader()
    {
        const string patchText =
            "diff --git a/code.c b/code.c\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/code.c\n" +
            "+++ b/code.c\n" +
            "@@ -10,3 +10,3 @@ static void my_function(int x)\n" +
            " {\n" +
            "-\tx = 1;\n" +
            "+\tx = 2;\n" +
            " }\n";

        ParsedPatch? patch = PatchParser.FromBuffer(patchText);

        Assert.NotNull(patch);
        GitDiffHunk hunk = patch!.Hunks[0];
        Assert.Equal(10, hunk.OldStart);
        Assert.Equal(3, hunk.OldCount);
        Assert.Contains("static void my_function(int x)", hunk.HeaderText);
    }

    // ── hunk line-number overflow → parse error ──────────────

    [Fact]
    public void FromBuffer_HunkLineNumberOverflow_Throws()
    {
        // C (patch_parse.c:592-599): old_start + old_lines overflows int →
        // "unrepresentable line count" parse error. C# int arithmetic is
        // unchecked by default, so the guard must use checked arithmetic to
        // fire; otherwise the wrapped line numbers parse silently.
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -2147483647,2147483647 +1,1 @@\n" +
            " line1\n";

        GitException ex = Assert.Throws<GitException>(() => GitDiff.FromBuffer(patchText));
        Assert.Contains("unrepresentable line count", ex.Message);
    }
}

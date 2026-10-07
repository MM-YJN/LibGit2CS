using LibGit2CS.Core;
using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitPatchApplier.ApplyPatchAsync"/> (parse core). Ported from
/// <c>tests/libgit2/apply/fromfile.c</c> + <c>fromdiff.c</c>. Uses inline patch
/// text + source content (no repository needed).
/// </summary>
public class PatchApplyTests
{
    private static ReadOnlyMemory<byte> Bytes(string s)
    {
        byte[] bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            bytes[i] = (byte)s[i];
        }
        return bytes;
    }

    private static string String(ReadOnlyMemory<byte> bytes)
    {
        char[] chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes.Span[i];
        }
        return new string(chars);
    }

    [Fact]
    public async Task ApplyPatch_SimpleModification_ProducesPostimage()
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

        ReadOnlyMemory<byte> source = Bytes("line1\nline2\nline3\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("file.txt", result.Filename?.ToUtf8String());
        Assert.Equal("line1\nline2-modified\nline3\n", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_Addition_ProducesPostimage()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,2 +1,3 @@\n" +
            " line1\n" +
            " line2\n" +
            "+line3\n";

        ReadOnlyMemory<byte> source = Bytes("line1\nline2\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("line1\nline2\nline3\n", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_Deletion_ProducesPostimage()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,2 @@\n" +
            " line1\n" +
            "-line2\n" +
            " line3\n";

        ReadOnlyMemory<byte> source = Bytes("line1\nline2\nline3\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("line1\nline3\n", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_NewFile_ProducesContentFromPatch()
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

        ReadOnlyMemory<byte> source = ReadOnlyMemory<byte>.Empty;
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("newfile.txt", result.Filename?.ToUtf8String());
        Assert.Equal("line1\nline2\n", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_DeletedFile_ProducesEmptyContent()
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

        ReadOnlyMemory<byte> source = Bytes("line1\nline2\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Filename);
        Assert.Equal(0, result.Content.Length);
    }

    [Fact]
    public async Task ApplyPatch_MultipleHunks_AppliesAll()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            " line1\n" +
            "-line2\n" +
            "+line2-new\n" +
            " line3\n" +
            "@@ -5,3 +5,3 @@\n" +
            " line5\n" +
            "-line6\n" +
            "+line6-new\n" +
            " line7\n";

        // Source: 7 lines, hunk 2 touches line 5-7.
        ReadOnlyMemory<byte> source = Bytes("line1\nline2\nline3\nline4\nline5\nline6\nline7\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("line1\nline2-new\nline3\nline4\nline5\nline6-new\nline7\n",
            String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_NoHunks_ReturnsSourceUnchanged()
    {
        // Mode-change patch with no hunks.
        const string patchText =
            "diff --git a/script.sh b/script.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n";

        ReadOnlyMemory<byte> source = Bytes("echo hello\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("echo hello\n", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_HunkDoesNotMatch_Throws()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            " line1\n" +
            "-line2\n" +
            "+line2-new\n" +
            " line3\n";

        // Source doesn't match the preimage (line2 is different).
        ReadOnlyMemory<byte> source = Bytes("line1\nDIFFERENT\nline3\n");
        var patch = GitPatch.FromBuffer(patchText);

        await Assert.ThrowsAsync<GitException>(async () => await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyPatch_NoNewlineAtEof_AppliesCorrectly()
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

        // Source has no trailing newline on line2.
        ReadOnlyMemory<byte> source = Bytes("line1\nline2");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        // Result: line1\nline2-new (no trailing newline)
        Assert.Equal("line1\nline2-new", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_ModeChange_PreservesContent()
    {
        const string patchText =
            "diff --git a/script.sh b/script.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n";

        ReadOnlyMemory<byte> source = Bytes("echo hello\n");
        var patch = GitPatch.FromBuffer(patchText);

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("script.sh", result.Filename?.ToUtf8String());
        Assert.Equal(LibGit2CS.Objects.GitFileMode.Executable, result.Mode);
    }

    [Fact]
    public async Task ApplyPatch_HunkCallback_SkipsHunk()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            " line1\n" +
            "-line2\n" +
            "+line2-new\n" +
            " line3\n";

        ReadOnlyMemory<byte> source = Bytes("line1\nline2\nline3\n");
        var patch = GitPatch.FromBuffer(patchText);

        // Hunk callback returns >0 to skip.
        var opts = new GitApplyOptions
        {
            HunkCallback = _ => 1,
        };

        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(source, patch, opts, cancellationToken: TestContext.Current.CancellationToken);

        // Source unchanged (hunk skipped).
        Assert.Equal("line1\nline2\nline3\n", String(result.Content));
    }

    [Fact]
    public async Task ApplyPatch_ParsedFromDiff_AppliesCorrectly()
    {
        const string patchText =
            "diff --git a/file1.txt b/file1.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file1.txt\n" +
            "+++ b/file1.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old1\n" +
            "+new1\n" +
            "diff --git a/file2.txt b/file2.txt\n" +
            "index 1234567..abcdef0 100644\n" +
            "--- a/file2.txt\n" +
            "+++ b/file2.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old2\n" +
            "+new2\n";

        var diff = GitDiff.FromBuffer(patchText);
        var patches = new List<GitPatch>();
        await foreach (GitPatch p in diff.PatchesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            patches.Add(p);
        }

        Assert.Equal(2, patches.Count);

        ReadOnlyMemory<byte> source1 = Bytes("old1\n");
        GitApplyResult result1 = await GitPatchApplier.ApplyPatchAsync(source1, patches[0], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new1\n", String(result1.Content));

        ReadOnlyMemory<byte> source2 = Bytes("old2\n");
        GitApplyResult result2 = await GitPatchApplier.ApplyPatchAsync(source2, patches[1], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new2\n", String(result2.Content));
    }
}

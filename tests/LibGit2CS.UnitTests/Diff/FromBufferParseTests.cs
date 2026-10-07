using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

public class FromBufferParseTests
{
    private static async Task<List<GitPatch>> ToListAsync(IAsyncEnumerable<GitPatch> source)
    {
        var list = new List<GitPatch>();
        await foreach (GitPatch p in source)
        {
            list.Add(p);
        }
        return list;
    }

    [Fact]
    public async Task FromBuffer_SinglePatch_EnumeratesOnePatch()
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

        var diff = GitDiff.FromBuffer(patchText);

        Assert.Equal(1, diff.DeltaCount);
        Assert.Equal(GitDeltaStatus.Modified, diff.GetDelta(0).Status);
        Assert.Equal("file.txt", diff.GetDelta(0).Path.ToUtf8String());

        List<GitPatch> patches = await ToListAsync(diff.PatchesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(patches);
        Assert.Equal(1, await patches[0].GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromBuffer_MultiFilePatch_EnumeratesAllPatches()
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

        Assert.Equal(2, diff.DeltaCount);
        Assert.Equal("file1.txt", diff.GetDelta(0).Path.ToUtf8String());
        Assert.Equal("file2.txt", diff.GetDelta(1).Path.ToUtf8String());

        List<GitPatch> patches = await ToListAsync(diff.PatchesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, patches.Count);
        Assert.Equal(1, await patches[0].GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await patches[1].GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromBuffer_NewAndDeletedFiles_ParsesStatuses()
    {
        const string patchText =
            "diff --git a/newfile.txt b/newfile.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..af8f41d\n" +
            "--- /dev/null\n" +
            "+++ b/newfile.txt\n" +
            "@@ -0,0 +1,1 @@\n" +
            "+newcontent\n" +
            "diff --git a/oldfile.txt b/oldfile.txt\n" +
            "deleted file mode 100644\n" +
            "index af8f41d..0000000\n" +
            "--- a/oldfile.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1,1 +0,0 @@\n" +
            "-oldcontent\n";

        var diff = GitDiff.FromBuffer(patchText);

        Assert.Equal(2, diff.DeltaCount);
        Assert.Equal(GitDeltaStatus.Added, diff.GetDelta(0).Status);
        Assert.Equal(GitDeltaStatus.Deleted, diff.GetDelta(1).Status);
    }

    [Fact]
    public async Task FromBuffer_RenameAndModify_ParsesCorrectly()
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

        var diff = GitDiff.FromBuffer(patchText);

        Assert.Equal(1, diff.DeltaCount);
        Assert.Equal(GitDeltaStatus.Renamed, diff.GetDelta(0).Status);
        Assert.Equal(86, diff.GetDelta(0).Similarity);
    }

    [Fact]
    public async Task FromBuffer_NoPatch_Throws()
    {
        const string text = "this is not a patch\njust some text\n";

        Assert.Throws<ArgumentException>(() => GitDiff.FromBuffer(text));
    }

    [Fact]
    public async Task FromBuffer_EmptyText_Throws()
    {
        Assert.Throws<ArgumentException>(() => GitDiff.FromBuffer(string.Empty));
    }

    [Fact]
    public async Task FromBuffer_LeadingNoise_SkipsToFirstPatch()
    {
        const string patchText =
            "Some leading noise\n" +
            "More noise\n" +
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        var diff = GitDiff.FromBuffer(patchText);

        Assert.Equal(1, diff.DeltaCount);
        Assert.Equal("file.txt", diff.GetDelta(0).Path.ToUtf8String());
    }

    [Fact]
    public async Task FromBuffer_PatchTextWithTrailingNewline_Parses()
    {
        const string patchText =
            "diff --git a/file.txt b/file.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n\n";

        var diff = GitDiff.FromBuffer(patchText);

        Assert.Equal(1, diff.DeltaCount);
    }

    [Fact]
    public async Task FromBuffer_BinaryPatch_ParsesBinaryFlag()
    {
        const string patchText =
            "diff --git a/binary.bin b/binary.bin\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "Binary files a/binary.bin and b/binary.bin differ\n";

        var diff = GitDiff.FromBuffer(patchText);

        Assert.Equal(1, diff.DeltaCount);
        Assert.True((diff.GetDelta(0).Flags & GitDiffFileFlags.Binary) != 0);
    }

    [Fact]
    public async Task FromBuffer_PatchCount_MatchesDeltaCount()
    {
        const string patchText =
            "diff --git a/a.txt b/a.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/a.txt\n" +
            "+++ b/a.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-a\n" +
            "+b\n" +
            "diff --git a/c.txt b/c.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/c.txt\n" +
            "+++ b/c.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-c\n" +
            "+d\n";

        var diff = GitDiff.FromBuffer(patchText);

        int patchCount = 0;
        await foreach (GitPatch _ in diff.PatchesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            patchCount++;
        }
        Assert.Equal(diff.DeltaCount, patchCount);
    }
}

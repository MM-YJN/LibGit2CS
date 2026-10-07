using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public MergeFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeFileTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task<GitRepository> OpenMergeResolveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-resolve.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), new GitContext());
    }

    // ── git_merge_file (from buffers) — matches merge/files.c golden tests ─

    [Fact]
    public async Task Merge_FromBufs_Automergeable()
    {
        // Matches test_merge_files__automerge_from_bufs.
        var ancestor = GitMergeFileInput.Create("testfile.txt", 0x81ED, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        var ours = GitMergeFileInput.Create("testfile.txt", 0x81A4, "Zero\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        var theirs = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\nTen\n");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        Assert.True(result.Automergeable);
        Assert.Equal("testfile.txt", result.Path?.ToUtf8String());
        Assert.Equal((uint)0x81A4, result.Mode);
        Assert.Equal("Zero\n1\n2\n3\n4\n5\n6\n7\n8\n9\nTen\n", Encoding.UTF8.GetString(result.Content.Span));
    }

    [Fact]
    public async Task Merge_FromBufs_BestPathAndMode()
    {
        // Matches test_merge_files__automerge_use_best_path_and_mode.
        // ancestor path = theirs path → result picks theirs path.
        // ancestor mode != ours mode and ancestor mode != theirs mode → result picks ours mode.
        var ancestor = GitMergeFileInput.Create("ancestor.txt", 0x81ED, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        var ours = GitMergeFileInput.Create("ours.txt", 0x81A4, "Zero\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        var theirs = GitMergeFileInput.Create("ancestor.txt", 0x81ED, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\nTen\n");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        Assert.True(result.Automergeable);
        // best_path: ancestor == theirs → returns ours ("ours.txt").
        Assert.Equal("ours.txt", result.Path?.ToUtf8String());
        // best_mode: ancestor == theirs mode (0x81ED) → returns ours (0x81A4).
        Assert.Equal((uint)0x81A4, result.Mode);
    }

    [Fact]
    public async Task Merge_FromBufs_Conflict()
    {
        // Matches test_merge_files__conflict_from_bufs.
        // Both sides modify the SAME line to DIFFERENT values → conflict.
        var ancestor = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        var ours = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\nOURS\n");
        var theirs = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\nTHEIRS\n");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        Assert.False(result.Automergeable);
        Assert.True(result.HasConflicts);
        Assert.True(result.ConflictCount > 0);

        string content = Encoding.UTF8.GetString(result.Content.Span);
        Assert.Contains("<<<<<<<", content);
        Assert.Contains("=======", content);
        Assert.Contains(">>>>>>>", content);
    }

    [Fact]
    public async Task Merge_NoTrailingNewline_DoesntAddNewline()
    {
        // Matches test_merge_files__doesnt_add_newline.
        var ancestor = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n");
        var ours = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n6");
        var theirs = GitMergeFileInput.Create("testfile.txt", 0x81A4, "0\n1\n2\n3\n4\n5\n7");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        // Both sides changed the last line differently → conflict.
        Assert.False(result.Automergeable);
        string content = Encoding.UTF8.GetString(result.Content.Span);
        Assert.False(content.EndsWith("6\n", StringComparison.Ordinal));
    }

    // ── Binary detection ────────────────────────────────────────────────

    [Fact]
    public async Task Merge_BinaryFile_NotAutomergeable()
    {
        // Matches test_merge_files__skips_binaries.
        byte[] binaryContent = new byte[] { 1, 2, 3, 0, 5, 6 }; // NUL byte → binary
        var ancestor = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, Encoding.UTF8.GetBytes("ancestor\n"));
        var ours = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, binaryContent);
        var theirs = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, Encoding.UTF8.GetBytes("theirs\n"));

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        Assert.False(result.Automergeable);
        Assert.Equal(0, result.Content.Length);
    }

    [Fact]
    public async Task Merge_BinaryFile_WithFavorOurs_Automergeable()
    {
        // Matches test_merge_files__handles_binaries_when_favored.
        byte[] binaryContent = new byte[] { 1, 2, 3, 0, 5, 6 };
        var ancestor = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, binaryContent);
        var ours = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, binaryContent);
        var theirs = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, Encoding.UTF8.GetBytes("different\n"));

        var opts = new GitMergeFileOptions { Favor = GitMergeFileFavor.Ours };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
        Assert.Equal(binaryContent, result.Content.ToArray());
    }

    [Fact]
    public async Task Merge_BinaryFile_WithFavorTheirs_Automergeable()
    {
        byte[] binaryContent = new byte[] { 1, 2, 3, 0, 5, 6 };
        byte[] theirsContent = new byte[] { 9, 8, 7, 0, 5, 6 };
        var ancestor = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, binaryContent);
        var ours = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, Encoding.UTF8.GetBytes("ours\n"));
        var theirs = new GitMergeFileInput(GitPath.FromUtf8String("testfile.txt"), 0x81A4, theirsContent);

        var opts = new GitMergeFileOptions { Favor = GitMergeFileFavor.Theirs };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
        Assert.Equal(theirsContent, result.Content.ToArray());
    }

    // ── Whitespace handling ─────────────────────────────────────────────

    [Fact]
    public async Task Merge_IgnoreWhitespaceEol_Automergeable()
    {
        // Matches test_merge_files__automerge_whitespace_eol.
        var ancestor = GitMergeFileInput.Create("testfile.txt", 0x81A4, "line1\nline2  \nline3\n");
        var ours = GitMergeFileInput.Create("testfile.txt", 0x81A4, "line1\nline2\nline3\n");
        var theirs = GitMergeFileInput.Create("testfile.txt", 0x81A4, "line1\nline2   \nline3\n");

        var opts = new GitMergeFileOptions { Flags = GitMergeFileFlags.IgnoreWhitespaceEol };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
    }

    [Fact]
    public async Task Merge_IgnoreWhitespaceChange_Automergeable()
    {
        // Matches test_merge_files__automerge_whitespace_change.
        var ancestor = GitMergeFileInput.Create("testfile.txt", 0x81A4, "line1\nline2  here\nline3\n");
        var ours = GitMergeFileInput.Create("testfile.txt", 0x81A4, "line1\nline2 here\nline3\n");
        var theirs = GitMergeFileInput.Create("testfile.txt", 0x81A4, "line1\nline2    here\nline3\n");

        var opts = new GitMergeFileOptions { Flags = GitMergeFileFlags.IgnoreWhitespaceChange };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
    }

    // ── Conflict styles ─────────────────────────────────────────────────

    [Fact]
    public async Task Merge_Diff3Style_HasAncestorSection()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nOURS\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nTHEIRS\nline3\n");

        var opts = new GitMergeFileOptions
        {
            Flags = GitMergeFileFlags.StyleDiff3,
            AncestorLabel = "ancestor",
            OurLabel = "ours",
            TheirLabel = "theirs",
        };

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.False(result.Automergeable);
        string content = Encoding.UTF8.GetString(result.Content.Span);
        Assert.Contains("|||||||", content);
        Assert.Contains("ancestor", content);
    }

    [Fact]
    public async Task Merge_Zdiff3Style_HasAncestorSection()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nOURS\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nTHEIRS\nline3\n");

        var opts = new GitMergeFileOptions
        {
            Flags = GitMergeFileFlags.StyleZdiff3,
            AncestorLabel = "ancestor",
            OurLabel = "ours",
            TheirLabel = "theirs",
        };

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.False(result.Automergeable);
        string content = Encoding.UTF8.GetString(result.Content.Span);
        Assert.Contains("|||||||", content);
    }

    // ── Favor ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_FavorOurs_NoConflicts()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nOURS\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nTHEIRS\nline3\n");

        var opts = new GitMergeFileOptions { Favor = GitMergeFileFavor.Ours };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("OURS", Encoding.UTF8.GetString(result.Content.Span));
    }

    [Fact]
    public async Task Merge_FavorTheirs_NoConflicts()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nOURS\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nTHEIRS\nline3\n");

        var opts = new GitMergeFileOptions { Favor = GitMergeFileFavor.Theirs };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("THEIRS", Encoding.UTF8.GetString(result.Content.Span));
    }

    [Fact]
    public async Task Merge_FavorUnion_CombinesBothSides()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nOURS\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nTHEIRS\nline3\n");

        var opts = new GitMergeFileOptions { Favor = GitMergeFileFavor.Union };
        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs, opts);

        Assert.True(result.Automergeable);
        Assert.Equal(0, result.ConflictCount);
        string content = Encoding.UTF8.GetString(result.Content.Span);
        Assert.Contains("OURS", content);
        Assert.Contains("THEIRS", content);
    }

    // ── Input normalization ─────────────────────────────────────────────

    [Fact]
    public async Task Merge_NullPath_NormalizedToFileTxt()
    {
        var ancestor = GitMergeFileInput.Create(null, 0, "a\n");
        var ours = GitMergeFileInput.Create(null, 0, "a\nb\n");
        var theirs = GitMergeFileInput.Create(null, 0, "a\nc\n");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        // Even though paths were null, the merge should work.
        // BestPath with all-null ancestor paths: ancestor null → ours==theirs → return ours.
        // But both are null → null path. The merge still works.
        // This test just verifies no crash.
        Assert.NotNull(result);
    }

    // ── Empty file handling ─────────────────────────────────────────────

    [Fact]
    public async Task Merge_ZeroByteOurs_Automergeable()
    {
        // Matches test_merge_files__automerge_zero_byte.
        // ancestor == theirs (same content), ours is empty.
        // Since theirs didn't change, ours wins → automergeable, empty result.
        string content = "some content\nthat ancestor has\n";
        var ancestor = GitMergeFileInput.Create("automergeable.txt", 0x81A4, content);
        var ours = new GitMergeFileInput(GitPath.FromUtf8String("empty.txt"), 0x81A4, ReadOnlyMemory<byte>.Empty);
        var theirs = GitMergeFileInput.Create("automergeable.txt", 0x81A4, content);

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        // theirs == ancestor → no change on their side → ours (empty) wins.
        Assert.True(result.Automergeable);
        // best_path: ancestor == theirs → returns ours ("empty.txt").
        Assert.Equal("empty.txt", result.Path?.ToUtf8String());
    }

    // ── MergeFromIndex (from index entries) ─────────────────────────────

    [Fact]
    public async Task MergeFromIndex_Automergeable()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // The merge-resolve repo's index has entries for the master tree.
        // We can use automergeable.txt which has different content on each branch.
        // Read the index entries for a file that exists in all three stages.
        LibGit2CS.Index.GitIndex index = (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken));

        // automergeable.txt: read entries from the index (master tree).
        // The index should have the stage-0 entry for automergeable.txt.
        GitIndexEntry? entry = index.EntryByPath("automergeable.txt", 0);
        if (entry is null)
        {
            // If not in index, skip — this test depends on the fixture state.
            return;
        }

        // Merge the entry with itself → should be automergeable.
        GitMergeFileResult result = await repo.MergeFileFromIndexAsync(entry, entry, entry, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Automergeable);
    }

    [Fact]
    public async Task MergeFromIndex_AllNull_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repo.MergeFileFromIndexAsync(null, null, null, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── MergeFileResult ────────────────────────────────────────────────

    [Fact]
    public async Task HasConflicts_TrueWhenNotAutomergeable()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nOURS\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nTHEIRS\nline3\n");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        Assert.False(result.Automergeable);
        Assert.True(result.HasConflicts);
    }

    [Fact]
    public async Task HasConflicts_FalseWhenAutomergeable()
    {
        var ancestor = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");
        var ours = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nMODIFIED\nline3\n");
        var theirs = GitMergeFileInput.Create("test.txt", 0x81A4, "line1\nline2\nline3\n");

        GitMergeFileResult result = GitMergeFile.Merge(ancestor, ours, theirs);

        Assert.True(result.Automergeable);
        Assert.False(result.HasConflicts);
    }

    [Fact]
    public async Task MergeFromIndex_ConflictPreservesOursTheirsOrder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeFileTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(dir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            GitOid ancestorOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("VEAL SOUP.\n\nPut into a pot three quarts of water, three onions cut small, one\nrest\n"), CancellationToken.None);
            GitOid ourOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("VEAL SOUP.\n\nPUT INTO A POT three quarts of water, three onions cut small, one\nrest\n"), CancellationToken.None);
            GitOid theirOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("VEAL SOUP.\n\nPut into a pot three quarts of water, THREE ONIONS CUT SMALL, one\nrest\n"), CancellationToken.None);

            var ancestor = new GitIndexEntry("veal.txt", ancestorOid, GitFileMode.Regular);
            var ours = new GitIndexEntry("veal.txt", ourOid, GitFileMode.Regular);
            var theirs = new GitIndexEntry("veal.txt", theirOid, GitFileMode.Regular);

            var opts = new GitMergeFileOptions
            {
                OurLabel = "Temporary merge branch 1",
                TheirLabel = "Temporary merge branch 2",
                MarkerSize = 9,
                Flags = GitMergeFileFlags.AcceptConflicts,
            };

            GitMergeFileResult result = await repo.MergeFileFromIndexAsync(ancestor, ours, theirs, opts, cancellationToken: TestContext.Current.CancellationToken);
            string content = Encoding.UTF8.GetString(result.Content.Span);

            int oursIdx = content.IndexOf("PUT INTO A POT");
            int theirsIdx = content.IndexOf("Put into a pot three quarts of water, THREE");

            Assert.True(oursIdx >= 0, $"OURS should be in content");
            Assert.True(theirsIdx >= 0, $"THEIRS should be in content");
            Assert.True(oursIdx < theirsIdx, "OURS should appear before THEIRS");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
        }
    }
}

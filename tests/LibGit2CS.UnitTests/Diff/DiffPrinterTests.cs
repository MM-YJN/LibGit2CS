using System.Text;

using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

public class DiffPrinterTests
{
    [Fact]
    public async Task NameOnly_EmitsChangedFilePaths()
    {
        GitDiff diff = CreateDiff("a\n", "b\n");
        string output = await Render(diff, GitDiffPrintFormat.NameOnly);

        Assert.Equal("file\n", output);
    }

    [Fact]
    public async Task NameStatus_EmitsStatusCharAndPath()
    {
        GitDiff diff = CreateDiff("a\n", "b\n");
        string output = await Render(diff, GitDiffPrintFormat.NameStatus);

        Assert.Equal("M\tfile\n", output);
    }

    [Fact]
    public async Task NameOnly_SkipsUnmodified()
    {
        GitDiff diff = CreateDiff("same\n", "same\n");
        string output = await Render(diff, GitDiffPrintFormat.NameOnly);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task NameStatus_SkipsUnmodified()
    {
        GitDiff diff = CreateDiff("same\n", "same\n");
        string output = await Render(diff, GitDiffPrintFormat.NameStatus);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Raw_EmitsModeOidStatus()
    {
        GitDiff diff = CreateDiff("a\n", "b\n");
        string output = await Render(diff, GitDiffPrintFormat.Raw);

        // Should start with ":100644 100644" and contain "M"
        Assert.Contains(":100644 100644", output);
        Assert.Contains("M", output);
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public async Task StatusChar_MapsCorrectly()
    {
        Assert.Equal('A', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Added));
        Assert.Equal('D', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Deleted));
        Assert.Equal('M', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Modified));
        Assert.Equal('R', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Renamed));
        Assert.Equal('C', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Copied));
        Assert.Equal('?', DiffPrinter.DeltaStatusChar(GitDeltaStatus.Untracked));
    }

    private static GitDiff CreateDiff(string oldContent, string newContent)
    {
        return GitDiff.Buffers(
            Encoding.UTF8.GetBytes(oldContent),
            Encoding.UTF8.GetBytes(newContent));
    }

    private static async Task<string> Render(GitDiff diff, GitDiffPrintFormat format)
    {
        var sb = new StringBuilder();
        await diff.PrintAsync(format, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        });
        return sb.ToString();
    }
}

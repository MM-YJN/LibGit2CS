using LibGit2CS.Core;
using LibGit2CS.Merge;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Recursive merge (criss-cross) tests. Ported from
/// <c>tests/libgit2/merge/trees/recursive.c</c>. Uses the
/// <c>merge-recursive</c> fixture with branches branchA-1 through branchK-2.
/// </summary>
public sealed class MergeRecursiveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public MergeRecursiveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeRecursive_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> OpenRecursiveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-recursive.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-recursive"), new GitContext());
    }

    // Octal 0100644 = 33188 decimal
    private const uint Mode0644 = 33188;

    [Fact]
    public async Task OneBaseCommit()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchA-1", "branchA-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "dea7215f259b2cced87d1bda6c72f8b4ce37a2ff", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task OneBaseCommitNoRecursive()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.NoRecursive };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchA-1", "branchA-2", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "dea7215f259b2cced87d1bda6c72f8b4ce37a2ff", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task TwoBaseCommits()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchB-1", "branchB-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "666ffdfcf1eaa5641fa31064bf2607327e843c09", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task TwoBaseCommitsNoRecursive()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.NoRecursive };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchB-1", "branchB-2", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "cb49ad76147f5f9439cbd6133708b76142660660", 1, "veal.txt"),
            new(Mode0644, "b2a81ead9e722af0099fccfb478cea88eea749a2", 2, "veal.txt"),
            new(Mode0644, "4e21d2d63357bde5027d1625f5ec6b430cdeb143", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task TwoLevelsOfMultipleBases()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchC-1", "branchC-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "15faa0c9991f2d65686e844651faa2ff9827887b", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task TwoLevelsOfMultipleBasesNoRecursive()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.NoRecursive };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchC-1", "branchC-2", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "b2a81ead9e722af0099fccfb478cea88eea749a2", 1, "veal.txt"),
            new(Mode0644, "898d12687fb35be271c27c795a6b32c8b51da79e", 2, "veal.txt"),
            new(Mode0644, "68a2e1ee61a23a4728fe6b35580fbbbf729df370", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ThreeLevelsOfMultipleBases()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchD-2", "branchD-1");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "d55e5dc038c52f1a36548625bcb666cbc06db9e6", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ThreeLevelsOfMultipleBasesNoRecursive()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.NoRecursive };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchD-2", "branchD-1", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "898d12687fb35be271c27c795a6b32c8b51da79e", 1, "veal.txt"),
            new(Mode0644, "f1b44c04989a3a1c14b036cfadfa328d53a7bc5e", 2, "veal.txt"),
            new(Mode0644, "5e8747f5200fac0f945a07daf6163ca9cb1a8da9", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ThreeBaseCommits()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchE-1", "branchE-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4f7269b07c76d02755d75ccaf05c0b4c36cdc6c", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ThreeBaseCommitsNoRecursive()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { Flags = GitMergeFlags.FindRenames | GitMergeFlags.NoRecursive };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchE-1", "branchE-2", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "9e12bce04446d097ae1782967a5888c2e2a0d35b", 1, "gravy.txt"),
            new(Mode0644, "d8dd349b78f19a4ebe3357bacb8138f00bf5ed41", 2, "gravy.txt"),
            new(Mode0644, "e50fbbd701458757bdfe9815f58ed717c588d1b5", 3, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task Conflict()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchF-1", "branchF-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "fa567f568ed72157c0c617438d077695b99d9aac", 1, "veal.txt"),
            new(Mode0644, "21950d5e4e4d1a871b4dfcf72ecb6b9c162c434e", 2, "veal.txt"),
            new(Mode0644, "3855170cef875708da06ab9ad7fc6a73b531cda1", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task OhSoManyLevelsOfRecursion()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchG-1", "branchG-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "7c7e08f9559d9e1551b91e1cf68f1d0066109add", 0, "oyster.txt"),
            new(Mode0644, "898d12687fb35be271c27c795a6b32c8b51da79e", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ConflictingMergeBase()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchH-1", "branchH-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "cfc01b0976122eae42a82064440bbf534eddd7a0", 1, "veal.txt"),
            new(Mode0644, "d604c75019c282144bdbbf3fd3462ba74b240efc", 2, "veal.txt"),
            new(Mode0644, "37a5054a9f9b4628e3924c5cb8f2147c6e2a3efc", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ConflictingMergeBaseWithDiff3()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { FileFlags = GitMergeFileFlags.StyleDiff3 };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchH-2", "branchH-1", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "0b01d2f70a1c6b9ab60c382f3f9cdc8173da6736", 1, "veal.txt"),
            new(Mode0644, "37a5054a9f9b4628e3924c5cb8f2147c6e2a3efc", 2, "veal.txt"),
            new(Mode0644, "d604c75019c282144bdbbf3fd3462ba74b240efc", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task ConflictingMergeBaseSinceResolved()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchI-1", "branchI-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "a02d4fd126e0cc8fb46ee48cf38bad36d44f2dbc", 0, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task RecursionLimit()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        var opts = new GitMergeOptions { RecursionLimit = 1 };
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchC-1", "branchC-2", opts);

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new(Mode0644, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new(Mode0644, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new(Mode0644, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new(Mode0644, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new(Mode0644, "53217e8ac3f52bccf7603b8fff0ed0f4817f9bb7", 1, "veal.txt"),
            new(Mode0644, "898d12687fb35be271c27c795a6b32c8b51da79e", 2, "veal.txt"),
            new(Mode0644, "68a2e1ee61a23a4728fe6b35580fbbbf729df370", 3, "veal.txt"),
        ]);
    }

    [Fact]
    public async Task MergeBaseForVirtualCommit()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchJ-1", "branchJ-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "1bde1883de4977ea3e664b315da951d1f614c3b1", 0, "targetfile.txt"),
            new(Mode0644, "b7de2b52ba055688061355fad1599a5d214ce8f8", 1, "version.txt"),
            new(Mode0644, "358efd6f589384fa8baf92234db9c7899a53916e", 2, "version.txt"),
            new(Mode0644, "a664873b1c0b9a1ed300f8644dde536fdaa3a34f", 3, "version.txt"),
        ]);
    }

    [Fact]
    public async Task MergeBaseForVirtualCommit2()
    {
        await using GitRepository repo = await OpenRecursiveRepoAsync();
        GitIndex index = await MergeTestHelpers.MergeCommitsFromBranchesAsync(repo, "branchK-1", "branchK-2");

        MergeTestHelpers.AssertIndex(index, [
            new(Mode0644, "4a06b258fed8a4d15967ec4253ae7366b70f727d", 0, "targetfile.txt"),
            new(Mode0644, "b6bd0f9952f396e757d3f91e08c59a7e91707201", 1, "version.txt"),
            new(Mode0644, "f0856993e005c0d8ed2dc7cdc222cc1d89fb3c77", 2, "version.txt"),
            new(Mode0644, "2cba583804a4a6fad1baf97c959be447238d1489", 3, "version.txt"),
        ]);
    }
}

using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitRepository.ApplyToTreeAsync"/> (apply bridge).
/// Ported from <c>tests/libgit2/apply/tree.c</c> — applies a diff to a tree
/// in memory and validates the resulting ephemeral index.
/// </summary>
public class ApplyToTreeTests : ApplyGoldenBase
{
    // Git file mode 0100644 (octal) = 0x81A4 = 33188 (decimal).
    private const int ModeRegular = 0x81A4;

    /// <summary>
    /// Port of <c>test_apply_tree__one</c> (tree.c:20-58). Generates a diff
    /// from commit A (539bd01) to commit B (7c7bf85), applies it to A's tree,
    /// and validates the resulting index has 6 entries with the expected
    /// OIDs/modes/paths.
    /// </summary>
    [Fact]
    public async Task ApplyToTree_ModifiesExistingFile_ProducesCorrectIndex()
    {
        const string aOid = "539bd011c4822c560c1d17cab095006b7a10f707";
        const string bOid = "7c7bf85e978f1d18c0566f702d2cb7766b9c8d4f";

        // Expected postimage index entries (tree.c:29-36):
        // asparagus.txt: ffb36e5 (modified from f516580)
        // beef.txt:      68f6182 (unchanged)
        // bouilli.txt:   4b7c565 (unchanged)
        // gravy.txt:     c4e6cca (unchanged)
        // oyster.txt:    68af1fc (unchanged)
        // veal.txt:      a7b0665 (modified from 94d2c01)
        (string, string)[] expected =
        [
            ("ffb36e513f5fdf8a6ba850a20142676a2ac4807d", "asparagus.txt"),
            ("68f6182f4c85d39e1309d97c7e456156dc9c0096", "beef.txt"),
            ("4b7c5650008b2e747fe1809eeb5a1dde0e80850a", "bouilli.txt"),
            ("c4e6cca3ec6ae0148ed231f97257df8c311e015f", "gravy.txt"),
            ("68af1fc7407fd9addf1701a87eb1c95c7494c598", "oyster.txt"),
            ("a7b066537e6be7109abfe4ff97b675d4e077da20", "veal.txt"),
        ];

        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        GitTree aTree = await ResolveTreeAsync(repo, aOid);
        GitTree bTree = await ResolveTreeAsync(repo, bOid);

        GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: TestContext.Current.CancellationToken);

        using LibGit2CS.Index.GitIndex result = await repo.ApplyToTreeAsync(aTree, diff, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Length, result.EntryCount);

        var entries = result.Entries.ToList();
        for (int i = 0; i < expected.Length; i++)
        {
            (string? oidHex, string? path) = expected[i];
            Assert.Equal(path, entries[i].Path.ToUtf8String());
            Assert.Equal(ModeRegular, (int)entries[i].Mode);
            Assert.Equal(oidHex, entries[i].Id.ToString());
        }
    }

    /// <summary>
    /// Port of <c>test_apply_tree__adds_file</c> (tree.c:60-93). Applies a
    /// parsed diff (DIFF_ADD_FILE) to A's tree (539bd01) and validates the
    /// resulting index has 7 entries (the original 6 + newfile.txt).
    /// </summary>
    [Fact]
    public async Task ApplyToTree_AddsFile_ProducesCorrectIndex()
    {
        const string aOid = "539bd011c4822c560c1d17cab095006b7a10f707";

        const string addFilePatch =
            "diff --git a/newfile.txt b/newfile.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..6370543\n" +
            "--- /dev/null\n" +
            "+++ b/newfile.txt\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+This is a new file!\n" +
            "+Added by a patch.\n";

        // Expected: original 5 files (asparagus/beef/bouilli/gravy/oyster) +
        // newfile.txt (6370543) + veal.txt (unchanged) = 7 entries.
        (string, string)[] expected =
        [
            ("f51658077d85f2264fa179b4d0848268cb3475c3", "asparagus.txt"),
            ("68f6182f4c85d39e1309d97c7e456156dc9c0096", "beef.txt"),
            ("4b7c5650008b2e747fe1809eeb5a1dde0e80850a", "bouilli.txt"),
            ("c4e6cca3ec6ae0148ed231f97257df8c311e015f", "gravy.txt"),
            ("6370543fcfedb3e6516ec53b06158f3687dc1447", "newfile.txt"),
            ("68af1fc7407fd9addf1701a87eb1c95c7494c598", "oyster.txt"),
            ("94d2c01087f48213bd157222d54edfefd77c9bba", "veal.txt"),
        ];

        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        GitTree aTree = await ResolveTreeAsync(repo, aOid);

        var diff = GitDiff.FromBuffer(addFilePatch);

        using LibGit2CS.Index.GitIndex result = await repo.ApplyToTreeAsync(aTree, diff, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Length, result.EntryCount);

        var entries = result.Entries.ToList();
        for (int i = 0; i < expected.Length; i++)
        {
            (string? oidHex, string? path) = expected[i];
            Assert.Equal(path, entries[i].Path.ToUtf8String());
            Assert.Equal(ModeRegular, (int)entries[i].Mode);
            Assert.Equal(oidHex, entries[i].Id.ToString());
        }
    }
}

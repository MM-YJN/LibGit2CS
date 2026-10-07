using System.Text;

using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Byte-exact golden tests for binary diff output, ported from libgit2's
/// <c>tests/libgit2/diff/binary.c</c> against the <c>diff_format_email</c> and
/// <c>renames</c> fixtures. Covers both the noshow path (default, "Binary files
/// differ") and the <c>SHOW_BINARY</c> path (full <c>GIT binary patch</c> with
/// literal + delta base85-encoded content).
/// </summary>
public sealed class BinaryDiffGoldenTests : DiffGoldenBase
{
    // binary.c add_normal: 873806f -> 897d3af adds binary.bin. Default options
    // (no --binary) emit "Binary files /dev/null and b/binary.bin differ".
    [Fact]
    public async Task Binary_Add_Normal_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        GitTree oldTree = await ResolveTreeAsync(repo, "873806f6f27e631eb0b23e4b56bea2bfac14a373");
        GitTree newTree = await ResolveTreeAsync(repo, "897d3af16ca9e420cd071b1c4541bd2b91d04c8c");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("binary_add_normal"), await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // The single delta is an Added binary file.
        Assert.Equal(1, diff.DeltaCount);
        GitDiffDelta delta = diff.GetDelta(0);
        Assert.Equal(GitDeltaStatus.Added, delta.Status);
        Assert.Equal("binary.bin", delta.NewFile.Path?.ToUtf8String());
        Assert.True((delta.Flags & GitDiffFileFlags.Binary) != 0);
    }

    // binary.c add: SHOW_BINARY + full OID → "GIT binary patch\nliteral 3\n..."
    [Fact]
    public async Task Binary_Add_ShowBinary_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        GitTree oldTree = await ResolveTreeAsync(repo, "873806f6f27e631eb0b23e4b56bea2bfac14a373");
        GitTree newTree = await ResolveTreeAsync(repo, "897d3af16ca9e420cd071b1c4541bd2b91d04c8c");

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.ShowBinary,
            IdAbbrevLength = 40,
        };
        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, opts, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/binary.bin b/binary.bin\n" +
            "new file mode 100644\n" +
            "index 0000000000000000000000000000000000000000..bd474b2519cc15eab801ff851cc7d50f0dee49a1\n" +
            "GIT binary patch\n" +
            "literal 3\n" +
            "Kc${Nk-~s>u4FC%O\n" +
            "\n" +
            "literal 0\n" +
            "Hc$@<O00001\n" +
            "\n";

        Assert.Equal(expected, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c modify_normal: 897d3af -> 8d7523f modifies binary.bin. Default
    // options emit "Binary files a/binary.bin and b/binary.bin differ".
    [Fact]
    public async Task Binary_Modify_Normal_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        GitTree oldTree = await ResolveTreeAsync(repo, "897d3af16ca9e420cd071b1c4541bd2b91d04c8c");
        GitTree newTree = await ResolveTreeAsync(repo, "8d7523f6fcb2404257889abe0d96f093d9f524f9");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/binary.bin b/binary.bin\n" +
            "index bd474b2..9ac35ff 100644\n" +
            "Binary files a/binary.bin and b/binary.bin differ\n";

        Assert.Equal(expected, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c modify: SHOW_BINARY → "GIT binary patch\nliteral 5\n..."
    [Fact]
    public async Task Binary_Modify_ShowBinary_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        GitTree oldTree = await ResolveTreeAsync(repo, "897d3af16ca9e420cd071b1c4541bd2b91d04c8c");
        GitTree newTree = await ResolveTreeAsync(repo, "8d7523f6fcb2404257889abe0d96f093d9f524f9");

        var opts = new GitDiffOptions { Flags = GitDiffOptionsFlags.ShowBinary };
        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, opts, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/binary.bin b/binary.bin\n" +
            "index bd474b2519cc15eab801ff851cc7d50f0dee49a1..9ac35ff15cd8864aeafd889e4826a3150f0b06c4 100644\n" +
            "GIT binary patch\n" +
            "literal 5\n" +
            "Mc${NkU}WL~000&M4gdfE\n" +
            "\n" +
            "literal 3\n" +
            "Kc${Nk-~s>u4FC%O\n" +
            "\n";

        Assert.Equal(expected, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c delete_normal: 897d3af -> 873806f deletes binary.bin. Default
    // options emit "Binary files a/binary.bin and /dev/null differ".
    [Fact]
    public async Task Binary_Delete_Normal_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        GitTree oldTree = await ResolveTreeAsync(repo, "897d3af16ca9e420cd071b1c4541bd2b91d04c8c");
        GitTree newTree = await ResolveTreeAsync(repo, "873806f6f27e631eb0b23e4b56bea2bfac14a373");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/binary.bin b/binary.bin\n" +
            "deleted file mode 100644\n" +
            "index bd474b2..0000000\n" +
            "Binary files a/binary.bin and /dev/null differ\n";

        Assert.Equal(expected, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c delete: SHOW_BINARY + full OID → "GIT binary patch\nliteral 0\n..."
    [Fact]
    public async Task Binary_Delete_ShowBinary_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        GitTree oldTree = await ResolveTreeAsync(repo, "897d3af16ca9e420cd071b1c4541bd2b91d04c8c");
        GitTree newTree = await ResolveTreeAsync(repo, "873806f6f27e631eb0b23e4b56bea2bfac14a373");

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.ShowBinary,
            IdAbbrevLength = 40,
        };
        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, opts, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/binary.bin b/binary.bin\n" +
            "deleted file mode 100644\n" +
            "index bd474b2519cc15eab801ff851cc7d50f0dee49a1..0000000000000000000000000000000000000000\n" +
            "GIT binary patch\n" +
            "literal 0\n" +
            "Hc$@<O00001\n" +
            "\n" +
            "literal 3\n" +
            "Kc${Nk-~s>u4FC%O\n" +
            "\n";

        Assert.Equal(expected, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c empty_for_no_diff: SHOW_BINARY + FORCE_BINARY, diff a commit tree
    // against itself → no deltas → empty output.
    [Fact]
    public async Task Binary_EmptyForNoDiff_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree tree = await ResolveTreeAsync(repo, "19dd32dfb1520a64e5bbaae8dce6ef423dfa2f13");

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.ShowBinary | GitDiffOptionsFlags.ForceBinary,
            IdAbbrevLength = 40,
        };
        using GitDiff diff = await repo.DiffTreeToTreeAsync(tree, tree, opts, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, diff.DeltaCount);
        Assert.Equal(string.Empty, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c index_to_workdir: append to workdir file, diff index→workdir with
    // SHOW_BINARY + FORCE_BINARY → delta binary patch. Verifies workdir OID
    // computation + DeltaEncoder + Base85 + Zlib against real content.
    [Fact]
    public async Task Binary_IndexToWorkdir_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        string workdir = repo.Workdir
            ?? throw new InvalidOperationException("no workdir");
        await File.AppendAllTextAsync(
            Path.Combine(workdir, "untimely.txt"),
            "Oh that crazy Kipling!\r\n", cancellationToken: TestContext.Current.CancellationToken);

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.ShowBinary | GitDiffOptionsFlags.ForceBinary,
            IdAbbrevLength = 40,
        };
        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/untimely.txt b/untimely.txt\n" +
            "index 9a69d960ae94b060f56c2a8702545e2bb1abb935..1111d4f11f4b35bf6759e0fb714fe09731ef0840 100644\n" +
            "GIT binary patch\n" +
            "delta 32\n" +
            "nc%1vf+QYWt3zLL@hC)e3Vu?a>QDRl4f_G*?PG(-ZA}<#J$+QbW\n" +
            "\n" +
            "delta 7\n" +
            "Oc%18D`@*{63ljhg(E~C7\n" +
            "\n";

        Assert.Equal(expected, await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // binary.c print_patch_from_diff: same as index_to_workdir but via
    // await Diff.PrintAsync(Patch) instead of ToBuffer — verifies the print callback path.
    [Fact]
    public async Task Binary_PrintPatchFromDiff_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        string workdir = repo.Workdir
            ?? throw new InvalidOperationException("no workdir");
        await File.AppendAllTextAsync(
            Path.Combine(workdir, "untimely.txt"),
            "Oh that crazy Kipling!\r\n", cancellationToken: TestContext.Current.CancellationToken);

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.ShowBinary | GitDiffOptionsFlags.ForceBinary,
            IdAbbrevLength = 40,
        };
        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        string expected =
            "diff --git a/untimely.txt b/untimely.txt\n" +
            "index 9a69d960ae94b060f56c2a8702545e2bb1abb935..1111d4f11f4b35bf6759e0fb714fe09731ef0840 100644\n" +
            "GIT binary patch\n" +
            "delta 32\n" +
            "nc%1vf+QYWt3zLL@hC)e3Vu?a>QDRl4f_G*?PG(-ZA}<#J$+QbW\n" +
            "\n" +
            "delta 7\n" +
            "Oc%18D`@*{63ljhg(E~C7\n" +
            "\n";

        using var buf = new PooledByteBufferWriter();
        await diff.PrintAsync(GitDiffPrintFormat.Patch, (delta, hunk, line) =>
        {
            XdiffBridge.RenderLine(buf, line);
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Encoding.ASCII.GetString(buf.WrittenSpan));
    }
}

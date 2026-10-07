using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Golden tests for <see cref="GitEmailFormatter"/>. Ported from
/// <c>tests/libgit2/email/create.c</c> (365 LOC, 8 tests). Uses the
/// <c>diff_format_email</c> fixture.
/// </summary>
/// <remarks>
/// The expected output strings embed <c>LIBGIT2_VERSION</c> — in C# we
/// substitute <see cref="LibGit2Version.String"/> ("1.9.4") at test time so
/// the comparison is consistent with the formatter's trailer.
/// </remarks>
public class EmailGoldenTests : DiffGoldenBase
{
    private const string VersionTrailer = "libgit2 " + LibGit2Version.String;

    private static string Expect(params string[] lines)
        => string.Join("\n", lines) + "\n";

    /// <summary>
    /// Helper: format a commit as email and compare byte-exact.
    /// </summary>
    private async Task AssertEmailMatch(string expected, string commitOidHex, GitEmailOptions? opts = null)
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        var oid = GitOid.Parse(commitOidHex.AsSpan(), GitHashAlgorithmKind.Sha1);
        Commit commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");

        string actual = await GitEmailFormatter.ToBufferTextAsync(commit, opts);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Helper: format a commit and compare only the Subject line.
    /// </summary>
    private async Task AssertSubjectMatch(string expectedSubject, string commitOidHex, GitEmailOptions opts)
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        var oid = GitOid.Parse(commitOidHex.AsSpan(), GitHashAlgorithmKind.Sha1);
        Commit commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");

        string actual = await GitEmailFormatter.ToBufferTextAsync(commit, opts);
        int subjectStart = actual.IndexOf("\nSubject: ");
        Assert.True(subjectStart >= 0, "Subject: header not found");
        subjectStart += "\nSubject: ".Length;
        int nl = actual.IndexOf('\n', subjectStart);
        string subject = nl < 0 ? actual[subjectStart..] : actual[subjectStart..nl];
        Assert.Equal(expectedSubject, subject);
    }

    [Fact]
    public async Task Commit_SimpleModification_ByteExact()
    {
        string expected =
            "From 9264b96c6d104d0e07ae33d3007b6a48246c6f92 Mon Sep 17 00:00:00 2001\n" +
            "From: Jacques Germishuys <jacquesg@striata.com>\n" +
            "Date: Wed, 9 Apr 2014 20:57:01 +0200\n" +
            "Subject: [PATCH] Modify some content\n" +
            "\n" +
            "---\n" +
            " file1.txt | 8 +++++---\n" +
            " 1 file changed, 5 insertions(+), 3 deletions(-)\n" +
            "\n" +
            "diff --git a/file1.txt b/file1.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/file1.txt\n" +
            "+++ b/file1.txt\n" +
            "@@ -1,15 +1,17 @@\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            "+_file1.txt_\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            "+\n" +
            "+\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "+_file1.txt_\n" +
            "+_file1.txt_\n" +
            " file1.txt\n" +
            "--\n" +
            VersionTrailer + "\n" +
            "\n";

        await AssertEmailMatch(expected, "9264b96c6d104d0e07ae33d3007b6a48246c6f92");
    }

    [Fact]
    public async Task Rename_WithSimilarity_ByteExact()
    {
        string expected =
            "From 6e05acc5a5dab507d91a0a0cc0fb05a3dd98892d Mon Sep 17 00:00:00 2001\n" +
            "From: Jacques Germishuys <jacquesg@striata.com>\n" +
            "Date: Wed, 9 Apr 2014 21:15:56 +0200\n" +
            "Subject: [PATCH] Renamed file1.txt -> file1.txt.renamed\n" +
            "\n" +
            "---\n" +
            " file1.txt => file1.txt.renamed | 4 ++--\n" +
            " 1 file changed, 2 insertions(+), 2 deletions(-)\n" +
            "\n" +
            "diff --git a/file1.txt b/file1.txt.renamed\n" +
            "similarity index 86%\n" +
            "rename from file1.txt\n" +
            "rename to file1.txt.renamed\n" +
            "index af8f41d..a97157a 100644\n" +
            "--- a/file1.txt\n" +
            "+++ b/file1.txt.renamed\n" +
            "@@ -3,13 +3,13 @@ file1.txt\n" +
            " _file1.txt_\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            "-file1.txt\n" +
            "+file1.txt_renamed\n" +
            " file1.txt\n" +
            " \n" +
            " \n" +
            " file1.txt\n" +
            " file1.txt\n" +
            "-file1.txt\n" +
            "+file1.txt_renamed\n" +
            " file1.txt\n" +
            " file1.txt\n" +
            " _file1.txt_\n" +
            "--\n" +
            VersionTrailer + "\n" +
            "\n";

        await AssertEmailMatch(expected, "6e05acc5a5dab507d91a0a0cc0fb05a3dd98892d");
    }

    [Fact]
    public async Task Rename_AsAddDelete_WithNoRenames_ByteExact()
    {
        string expected =
            "From 6e05acc5a5dab507d91a0a0cc0fb05a3dd98892d Mon Sep 17 00:00:00 2001\n" +
            "From: Jacques Germishuys <jacquesg@striata.com>\n" +
            "Date: Wed, 9 Apr 2014 21:15:56 +0200\n" +
            "Subject: [PATCH] Renamed file1.txt -> file1.txt.renamed\n" +
            "\n" +
            "---\n" +
            " file1.txt         | 17 -----------------\n" +
            " file1.txt.renamed | 17 +++++++++++++++++\n" +
            " 2 files changed, 17 insertions(+), 17 deletions(-)\n" +
            " delete mode 100644 file1.txt\n" +
            " create mode 100644 file1.txt.renamed\n" +
            "\n" +
            "diff --git a/file1.txt b/file1.txt\n" +
            "deleted file mode 100644\n" +
            "index af8f41d..0000000\n" +
            "--- a/file1.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1,17 +0,0 @@\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-_file1.txt_\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-\n" +
            "-\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-file1.txt\n" +
            "-_file1.txt_\n" +
            "-_file1.txt_\n" +
            "-file1.txt\n" +
            "diff --git a/file1.txt.renamed b/file1.txt.renamed\n" +
            "new file mode 100644\n" +
            "index 0000000..a97157a\n" +
            "--- /dev/null\n" +
            "+++ b/file1.txt.renamed\n" +
            "@@ -0,0 +1,17 @@\n" +
            "+file1.txt\n" +
            "+file1.txt\n" +
            "+_file1.txt_\n" +
            "+file1.txt\n" +
            "+file1.txt\n" +
            "+file1.txt_renamed\n" +
            "+file1.txt\n" +
            "+\n" +
            "+\n" +
            "+file1.txt\n" +
            "+file1.txt\n" +
            "+file1.txt_renamed\n" +
            "+file1.txt\n" +
            "+file1.txt\n" +
            "+_file1.txt_\n" +
            "+_file1.txt_\n" +
            "+file1.txt\n" +
            "--\n" +
            VersionTrailer + "\n" +
            "\n";

        await AssertEmailMatch(expected, "6e05acc5a5dab507d91a0a0cc0fb05a3dd98892d",
            new GitEmailOptions { Flags = GitEmailCreateFlags.NoRenames });
    }

    [Fact]
    public async Task CommitSubjects_AllPrefixVariations()
    {
        var opts = new GitEmailOptions();
        await AssertSubjectMatch("[PATCH] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { RerollNumber = 42 };
        await AssertSubjectMatch("[PATCH v42] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { Flags = opts.Flags | GitEmailCreateFlags.AlwaysNumber };
        await AssertSubjectMatch("[PATCH v42 1/1] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { StartNumber = 9 };
        await AssertSubjectMatch("[PATCH v42 9/9] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { SubjectPrefix = "" };
        await AssertSubjectMatch("[v42 9/9] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { RerollNumber = 0 };
        await AssertSubjectMatch("[9/9] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { StartNumber = 0 };
        await AssertSubjectMatch("[1/1] Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);

        opts = opts with { Flags = GitEmailCreateFlags.OmitNumbers };
        await AssertSubjectMatch("Modify some content",
            "9264b96c6d104d0e07ae33d3007b6a48246c6f92", opts);
    }
}

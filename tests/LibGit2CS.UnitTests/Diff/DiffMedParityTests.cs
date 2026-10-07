using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Regression tests for the diff parity behaviors in
/// libgit2 1.9.4. Expectations are
/// C-verified against libgit2 1.9.4 (diff_tform.c, diff_generate.c,
/// diff_print.c, diff_stats.c, diff_parse.c, diff_driver.c, patch_parse.c).
/// </summary>
public sealed class DiffMedParityTests : DiffGoldenBase
{
    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<string> RenderAsync(GitDiff diff, GitDiffPrintFormat format)
    {
        var sb = new StringBuilder();
        await diff.PrintAsync(format, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        });
        return sb.ToString();
    }

    // ---------------------------------------------------------------
    // exact-match-only + untracked workdir files — C computes the
    // missing OIDs and scores identical content 100 (diff_tform.c:563-577).
    // ---------------------------------------------------------------

    [Fact]
    public async Task FindSimilar_ExactMatchOnly_UntrackedIdenticalContent_DetectsRename()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "identical content\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder treeBld = repo.NewTreeBuilder();
            await treeBld.InsertAsync("old.txt", blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = TestSig(),
                Committer = TestSig(),
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, cancellationToken: TestContext.Current.CancellationToken);

            // Workdir: old.txt missing, new.txt added (untracked, same content).
            await File.WriteAllTextAsync(Path.Combine(repoPath, "new.txt"), "identical content\n", TestContext.Current.CancellationToken);
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

            using GitDiff diff = await repo.DiffTreeToWorkdirAsync(tree, new GitDiffOptions
            {
                Flags = GitDiffOptionsFlags.IncludeUntracked,
            }, TestContext.Current.CancellationToken);

            await diff.FindSimilarAsync(new GitDiffFindOptions
            {
                Flags = GitDiffFindFlags.Renames | GitDiffFindFlags.ForUntracked | GitDiffFindFlags.ExactMatchOnly,
            }, TestContext.Current.CancellationToken);

            // C: the untracked target's OID is computed (git_diff__oid_for_file)
            // → identical content scores 100 → RENAMED delta.
            var statuses = new List<GitDeltaStatus>();
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                statuses.Add(diff.GetDelta(i).Status);
            }

            Assert.Contains(GitDeltaStatus.Renamed, statuses);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // diff.renames config parsing — case-insensitive "copies"/"copy"
    // and git__parse_bool dialect ("no"/"off"/"" disable renames).
    // ---------------------------------------------------------------

    [Fact]
    public async Task FindSimilar_ByConfig_Copies_CaseInsensitive()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("diff.renames", "Copies", TestContext.Current.CancellationToken);

            // tree1: orig.txt = X; tree2: orig.txt = X' (modified — its OLD
            // file is a copy source) + copy.txt = X (identical to the old
            // content → copy at 100%).
            GitOid blobX = await repo.ObjectWriteAsync(GitObjectType.Blob, "copy me\n"u8.ToArray(), TestContext.Current.CancellationToken);
            GitOid blobXp = await repo.ObjectWriteAsync(GitObjectType.Blob, "copy me\nchanged\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("orig.txt", blobX, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);

            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("orig.txt", blobXp, GitFileMode.Regular, TestContext.Current.CancellationToken);
            await b2.InsertAsync("copy.txt", blobX, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions(), cancellationToken: TestContext.Current.CancellationToken);

            await diff.FindSimilarAsync(null, TestContext.Current.CancellationToken); // BY_CONFIG

            // C: "Copies" (strcasecmp) enables RENAMES|COPIES → a COPIED delta.
            var statuses = new List<GitDeltaStatus>();
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                statuses.Add(diff.GetDelta(i).Status);
            }

            Assert.Contains(GitDeltaStatus.Copied, statuses);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task FindSimilar_ByConfig_Off_DisablesRenames()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        await repo.Config.SetStringAsync("diff.renames", "off", TestContext.Current.CancellationToken);

        GitTree oldTree = await ResolveTreeAsync(repo, "31e47d8c1fa36d7f8d537b96158e3f024de0a9f2");
        GitTree newTree = await ResolveTreeAsync(repo, "2bc7f351d20b53f1c72c16c4b036e491c478c49a");
        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        await diff.FindSimilarAsync(null, TestContext.Current.CancellationToken); // BY_CONFIG

        // C: "off" (git__parse_bool) disables rename detection entirely.
        var statuses = new List<GitDeltaStatus>();
        for (int i = 0; i < diff.DeltaCount; i++)
        {
            statuses.Add(diff.GetDelta(i).Status);
        }

        Assert.DoesNotContain(GitDeltaStatus.Renamed, statuses);
        Assert.DoesNotContain(GitDeltaStatus.Copied, statuses);
    }

    // ---------------------------------------------------------------
    // default NULL prefixes must let diff.noprefix apply
    // (diff_generate.c:571-587); GitDiffOptions defaults a/ b/.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Diff_NoprefixConfig_Respected()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "line1\nline2\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("f.txt", blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);

            GitOid blob2 = await repo.ObjectWriteAsync(GitObjectType.Blob, "line1\nCHANGED\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("f.txt", blob2, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            await repo.Config.SetBoolAsync("diff.noprefix", true, TestContext.Current.CancellationToken);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions(), cancellationToken: TestContext.Current.CancellationToken);

            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);

            // C emits "diff --git f.txt f.txt" (no a/ b/ prefixes).
            Assert.Contains("diff --git f.txt f.txt", output);
            Assert.DoesNotContain("a/f.txt", output);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // tree-to-tree diffs are case-SENSITIVE unless the user passes
    // GIT_DIFF_IGNORE_CASE — the icase mode comes from the ITERATORS, not
    // from core.ignorecase (diff_generate.c:472-477, 1382-1387).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Diff_TreeToTree_IgnoreCaseFromIterators_NotConfig()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
            await repo.Config.SetBoolAsync("core.ignorecase", true, TestContext.Current.CancellationToken);

            GitOid blobA = await repo.ObjectWriteAsync(GitObjectType.Blob, "AAA"u8.ToArray(), TestContext.Current.CancellationToken);
            GitOid blobB = await repo.ObjectWriteAsync(GitObjectType.Blob, "BBB"u8.ToArray(), TestContext.Current.CancellationToken);

            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("A.txt", blobA, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);

            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("a.txt", blobB, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions(), cancellationToken: TestContext.Current.CancellationToken);

            var statuses = new List<GitDeltaStatus>();
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                statuses.Add(diff.GetDelta(i).Status);
            }

            // C: tree-to-tree iterators are DONT_IGNORE_CASE even on a
            // case-insensitive repo → "A.txt" deleted + "a.txt" added (2 deltas).
            Assert.Equal(2, statuses.Count);
            Assert.Contains(GitDeltaStatus.Deleted, statuses);
            Assert.Contains(GitDeltaStatus.Added, statuses);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // a COPIED delta with modifications prints NO similarity header
    // (diff_print.c:456-460 — only when unchanged).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Patch_ModifiedCopy_NoSimilarityHeader()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

            // tree1: orig.txt = X; tree2: orig.txt = X' (modified — its OLD
            // file is a copy source) + copy.txt = Y (X with one changed line:
            // similarity < 100, OIDs differ).
            GitOid blobX = await repo.ObjectWriteAsync(GitObjectType.Blob, "line one\nline two\n"u8.ToArray(), TestContext.Current.CancellationToken);
            GitOid blobXp = await repo.ObjectWriteAsync(GitObjectType.Blob, "line one\nline two\nline three\n"u8.ToArray(), TestContext.Current.CancellationToken);
            GitOid blobY = await repo.ObjectWriteAsync(GitObjectType.Blob, "line one\nline CHANGED\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("orig.txt", blobX, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);

            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("orig.txt", blobXp, GitFileMode.Regular, TestContext.Current.CancellationToken);
            await b2.InsertAsync("copy.txt", blobY, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions(), cancellationToken: TestContext.Current.CancellationToken);

            await diff.FindSimilarAsync(new GitDiffFindOptions
            {
                Flags = GitDiffFindFlags.Renames | GitDiffFindFlags.Copies,
            }, TestContext.Current.CancellationToken);

            bool sawCopy = false;
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                GitDiffDelta delta = diff.GetDelta(i);
                if (delta.Status == GitDeltaStatus.Copied)
                {
                    sawCopy = true;
                    // A copy with modifications (OIDs differ) has NO similarity
                    // header in C.
                    Assert.NotEqual(delta.OldFile.Id, delta.NewFile.Id);
                }
            }

            Assert.True(sawCopy, "the copy diff should produce a copied delta");
            string output = await RenderAsync(diff, GitDiffPrintFormat.Patch);
            Assert.DoesNotContain("similarity index", output);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // name-status prints mode suffixes and the mode-change double
    // path (diff_print.c:115-124, 171-209).
    // ---------------------------------------------------------------

    [Fact]
    public async Task NameStatus_ExecutableMode_SuffixStar()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

            GitOid blobA = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("run.sh", blobA, GitFileMode.Executable, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);

            GitOid blobB = await repo.ObjectWriteAsync(GitObjectType.Blob, "y\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("run.sh", blobB, GitFileMode.Executable, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions(), cancellationToken: TestContext.Current.CancellationToken);

            string output = await RenderAsync(diff, GitDiffPrintFormat.NameStatus);

            // C: "M\trun.sh*" (the '*' exec suffix; GIT_PERMS_IS_EXEC).
            Assert.Equal("M\trun.sh*\n", output);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // raw format similarity is zero-padded (%03u, diff_print.c:258-259).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Raw_RenameSimilarity_ZeroPadded()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, "31e47d8c1fa36d7f8d537b96158e3f024de0a9f2");
        GitTree newTree = await ResolveTreeAsync(repo, "2bc7f351d20b53f1c72c16c4b036e491c478c49a");
        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        await diff.FindSimilarAsync(new GitDiffFindOptions
        {
            Flags = GitDiffFindFlags.Renames,
        }, TestContext.Current.CancellationToken);

        string output = await RenderAsync(diff, GitDiffPrintFormat.Raw);

        // C prints "R100" (three digits) for the 100% rename.
        Assert.Contains("R100", output);
    }

    // ---------------------------------------------------------------
    // the scaled --stat bar forces at least one '+' AND one '-'
    // (diff_stats.c:119-127).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Stats_ScaledBar_PureAddition_ForcesBothChars()
    {
        // A pure addition (old tree has one line) with a scaled bar: C emits
        // "+-" (max(plus,1), max(minus,1)) — the '-' is forced even though
        // nothing was deleted.
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

            GitOid blob1 = await repo.ObjectWriteAsync(GitObjectType.Blob, "one\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("f.txt", blob1, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);

            byte[] bigContent = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("line\n", 2000)));
            GitOid blob2 = await repo.ObjectWriteAsync(GitObjectType.Blob, bigContent, TestContext.Current.CancellationToken);
            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("f.txt", blob2, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions(), cancellationToken: TestContext.Current.CancellationToken);

            GitDiffStats stats = await diff.GetStatsAsync(TestContext.Current.CancellationToken);
            using var statsWriter = new PooledByteBufferWriter();
            stats.Format(statsWriter, GitDiffStatsFormat.Full);
            string text = Encoding.UTF8.GetString(statsWriter.WrittenSpan);
            Assert.Contains("+", text);
            Assert.Contains("-", text);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // a corrupt second patch fails the whole diff parse
    // (diff_parse.c:101-112 — only GIT_ENOTFOUND after ≥1 patch is
    // forgiven).
    // ---------------------------------------------------------------

    [Fact]
    public void FromBuffer_CorruptSecondPatch_Throws()
    {
        const string patchText =
            "diff --git a/one.txt b/one.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/one.txt\n" +
            "+++ b/one.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n" +
            "diff --git a/two.txt b/two.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/two.txt\n" +
            "+++ b/two.txt\n" +
            "this is not a hunk header\n";

        // C: the corrupt second patch fails git_diff_from_buffer (GIT_ERROR_PATCH).
        Assert.Throws<GitException>(() => LibGit2CS.Diff.GitDiff.FromBuffer(patchText));
    }

    // ---------------------------------------------------------------
    // user config overrides builtin diff drivers
    // (diff_driver.c:223-347 — config first, builtin only as fallback).
    // ---------------------------------------------------------------

    [Fact]
    public async Task DriverRegistry_UserConfig_OverridesBuiltin()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("diff.java.xfuncname", "^custom pattern$", TestContext.Current.CancellationToken);
            // The .gitattributes maps *.java → diff=java; write it before the
            // attr cache is built.
            await File.WriteAllTextAsync(Path.Combine(repoPath, ".gitattributes"), "*.java diff=java\n", TestContext.Current.CancellationToken);

            AttributeCache attrCache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
            var registry = new DiffDriverRegistry(repo, ignoreCase: false, attrCache);
            DiffDriver driver = await registry.LookupAsync(GitPath.FromUtf8String("Foo.java"), TestContext.Current.CancellationToken);

            // C loads diff.java.xfuncname from config first; the builtin only
            // when no config entry exists.
            Assert.True(driver.FnPatterns.Count > 0);
            Assert.True(driver.FnPatterns[0].Regex.IsMatch("custom pattern"));
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------
    // parse errors surface as GIT_ERROR_PATCH-class GitException with
    // the specific message (patch_parse.c:36-46), not ArgumentException.
    // ---------------------------------------------------------------

    [Fact]
    public void FromBuffer_CorruptHunkHeader_ThrowsPatchError()
    {
        // C probe: a second hunk header with no body lines fails with
        // "invalid patch hunk, expected 1 old lines and 1 new lines".
        const string patchText =
            "diff --git a/f.txt b/f.txt\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n" +
            "@@ -1 +1 @@\n";

        GitException ex = Assert.Throws<GitException>(() => GitPatch.FromBuffer(patchText));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Patch, ex.Category);
        Assert.Contains("at line", ex.Message);
    }

    // ── diff.<name>.binary = "auto" does NOT force text ──────

    [Fact]
    public async Task DiffDriver_BinaryAuto_DoesNotForceText()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
            // Assign a custom diff driver to *.txt.
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt diff=foo\n", cancellationToken: TestContext.Current.CancellationToken);
            // diff.foo.binary = "auto" is not a valid boolean → C's
            // git_config__get_bool_force returns -1 and the switch's default
            // does nothing (diff_driver.c:257-271). It must NOT force text.
            await repo.Config.SetStringAsync("diff.foo.binary", "auto", TestContext.Current.CancellationToken);

            AttributeCache attrCache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
            var registry = new DiffDriverRegistry(repo, ignoreCase: false, attrCache);
            DiffDriver driver = await registry.LookupAsync("file.txt", TestContext.Current.CancellationToken);

            Assert.False(driver.BinaryFlags.HasFlag(GitDiffOptionsFlags.ForceText));
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task DiffDriver_BinaryFalse_ForcesText()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "*.txt diff=foo\n", cancellationToken: TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("diff.foo.binary", "false", TestContext.Current.CancellationToken);

            AttributeCache attrCache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
            var registry = new DiffDriverRegistry(repo, ignoreCase: false, attrCache);
            DiffDriver driver = await registry.LookupAsync("file.txt", TestContext.Current.CancellationToken);

            Assert.True(driver.BinaryFlags.HasFlag(GitDiffOptionsFlags.ForceText));
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // ── diff.context consulted only when no user options ─────

    [Fact]
    public async Task DiffContext_ConfigAppliedOnlyWhenNoUserOptions()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
            await repo.Config.SetIntAsync("diff.context", 1, TestContext.Current.CancellationToken);

            GitOid blobA = await repo.ObjectWriteAsync(GitObjectType.Blob, "line1\nline2\nline3\nline4\nline5\n"u8.ToArray(), TestContext.Current.CancellationToken);
            GitOid blobB = await repo.ObjectWriteAsync(GitObjectType.Blob, "line1\nline2\nCHANGED\nline4\nline5\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder b1 = repo.NewTreeBuilder();
            await b1.InsertAsync("f.txt", blobA, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree1 = await b1.WriteAsync(CancellationToken.None);
            using GitTreeBuilder b2 = repo.NewTreeBuilder();
            await b2.InsertAsync("f.txt", blobB, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree2 = await b2.WriteAsync(CancellationToken.None);

            // No user options → diff.context config (1) is applied.
            using GitDiff diff1 = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                null, cancellationToken: TestContext.Current.CancellationToken);
            string out1 = await RenderAsync(diff1, GitDiffPrintFormat.Patch);
            // With context=1, only 1 context line before/after the change.
            Assert.Contains("@@ -2,3 +2,3 @@", out1);

            // Explicit context_lines = 3 → config NOT applied (stays 3).
            using GitDiff diff2 = await repo.DiffTreeToTreeAsync(
                await repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken),
                await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken),
                new GitDiffOptions { ContextLines = 3 }, cancellationToken: TestContext.Current.CancellationToken);
            string out2 = await RenderAsync(diff2, GitDiffPrintFormat.Patch);
            Assert.Contains("@@ -1,5 +1,5 @@", out2);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException) { }
        }
    }
}

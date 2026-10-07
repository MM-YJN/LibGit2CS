using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Revwalk;

public sealed class DescriberTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public DescriberTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DescriberTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }

    // master tip = a65fedf39aefe402d3bb6e24df4d4f5fe4547750
    private static readonly GitOid s_master = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
    // 'test' tag (annotated) → b25fa35b38051e4ae45d4222e795f9df2e43f1d1
    private static readonly GitOid s_testTag = GitOid.Parse("b25fa35b38051e4ae45d4222e795f9df2e43f1d1".AsSpan(), GitHashAlgorithmKind.Sha1);

    [Fact]
    public async Task Describe_TaggedCommit_ExactMatch()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        // Describing the commit a tag points at should yield the tag name (exact match).
        string desc = await repo.DescribeAsync(s_testTag, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("test", desc);
    }

    [Fact]
    public async Task Describe_TaggedCommit_AlwaysUseLongFormat_IncludesSuffix()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        string desc = await repo.DescribeAsync(s_testTag, new GitDescribeOptions { AlwaysUseLongFormat = true }, cancellationToken: TestContext.Current.CancellationToken);
        // test-0-g<abbrev>
        Assert.StartsWith("test-0-g", desc);
    }

    [Fact]
    public async Task Describe_MasterTip_ExactMatchToTag()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        // master tip is exactly tagged by "hard_tag" / "wrapped_tag" (both annotated,
        // same commit; tagger-date tiebreak). Reference git emits "hard_tag".
        string desc = await repo.DescribeAsync(s_master, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(desc is "hard_tag" or "wrapped_tag", $"unexpected describe: {desc}");
    }

    [Fact]
    public async Task Describe_AncestralCommit_WithoutTaggedAncestor_Throws()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // master's first parent (be3563a) has no tagged ancestor — reference git
        // also says "No tags can describe". Assert we throw to match.
        using GitRevWalker walker = repo.NewRevWalker();
        walker.Sort = GitSortMode.Time;
        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(commits.Count >= 2);
        GitOid ahead = commits[1];

        await Assert.ThrowsAsync<GitException>(async () => await repo.DescribeAsync(ahead, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Describe_NoTagMatch_WithFallback_ReturnsAbbrevOid()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // Walk back to a root commit unlikely to have a tag → with fallback, returns abbrev.
        using GitRevWalker walker = repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid root = commits[^1]; // oldest commit (topo order: children first)

        string desc = await repo.DescribeAsync(root, new GitDescribeOptions { ShowCommitOidAsFallback = true }, cancellationToken: TestContext.Current.CancellationToken);
        // Fallback: just an abbreviated OID (hex).
        Assert.Matches(@"^[0-9a-f]+$", desc);
    }

    [Fact]
    public async Task Describe_NoTagMatch_WithoutFallback_Throws()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        using GitRevWalker walker = repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid root = commits[^1];

        await Assert.ThrowsAsync<GitException>(async () => await repo.DescribeAsync(root, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Describe_TagsStrategy_IncludesLightweightTags()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        // With Tags strategy, lightweight tags are candidates too.
        string desc = await repo.DescribeAsync(s_master, new GitDescribeOptions { Strategy = GitDescribeStrategy.Tags }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(desc);
    }

    [Fact]
    public async Task Describe_AllStrategy_IncludesAllRefs()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        string desc = await repo.DescribeAsync(s_master, new GitDescribeOptions { Strategy = GitDescribeStrategy.All }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(desc);
    }

    [Fact]
    public async Task Describe_Pattern_FiltersTags()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        // Pattern that matches no tags → should fall back or throw.
        await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(s_master, new GitDescribeOptions
            {
                Pattern = "zzz-no-such-tag-pattern",
                ShowCommitOidAsFallback = false,
            }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DescribeWorkdir_UntrackedFile_NotDirty()
    {
        // git_describe_workdir runs status with GIT_STATUS_OPTIONS_INIT (flags = 0), so untracked and ignored files are NOT enumerated — only tracked
        // changes make the workdir "dirty" (describe.c:730,748). The old default
        // (IncludeUntracked|IncludeIgnored|...) meant any
        // untracked file appended the dirty suffix.
        string dir = Path.Combine(_tempDir, "wd");
        Directory.CreateDirectory(dir);
        await using GitRepository repo = await GitRepository.InitAsync(dir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // Commit a tracked file, then add an untracked file.
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "tracked.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("tracked.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0));
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
        Commit commitObj = (await repo.ObjectLookupAsync<Commit>(commit, TestContext.Current.CancellationToken))!;
        // Annotated tag (the default describe strategy matches annotated tags only).
        await repo.TagCreateAsync("v1", commitObj, sig, "msg\n", cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(workdir, "untracked.txt"), "y\n", cancellationToken: TestContext.Current.CancellationToken);

        string desc = await repo.DescribeWorkdirAsync(
            new GitDescribeOptions { DirtySuffix = "-dirty" },
            cancellationToken: TestContext.Current.CancellationToken);

        // Untracked-only workdir: C reports clean (no suffix).
        Assert.DoesNotContain("-dirty", desc);
    }

    [Fact]
    public async Task DescribeWorkdir_TrackedModification_IsDirty()
    {
        // A tracked modification DOES make the workdir dirty (status flags = 0
        // still enumerate tracked changes).
        string dir = Path.Combine(_tempDir, "wd2");
        Directory.CreateDirectory(dir);
        await using GitRepository repo = await GitRepository.InitAsync(dir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "tracked.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("tracked.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0));
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
        Commit commitObj = (await repo.ObjectLookupAsync<Commit>(commit, TestContext.Current.CancellationToken))!;
        // Annotated tag (the default describe strategy matches annotated tags only).
        await repo.TagCreateAsync("v1", commitObj, sig, "msg\n", cancellationToken: TestContext.Current.CancellationToken);

        // Modify the tracked file.
        await File.WriteAllTextAsync(Path.Combine(workdir, "tracked.txt"), "MODIFIED\n", cancellationToken: TestContext.Current.CancellationToken);

        string desc = await repo.DescribeWorkdirAsync(
            new GitDescribeOptions { DirtySuffix = "-dirty" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("-dirty", desc);
    }

    [Fact]
    public async Task Describe_AbbrevSizeExtendsToUnique()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // Use always-use-long-format on the test-tag commit to exercise the
        // -g<abbrev> suffix path with a small minimum abbrev.
        string desc = await repo.DescribeAsync(s_testTag, new GitDescribeOptions
        {
            AlwaysUseLongFormat = true,
            MinimumAbbreviatedSize = 4,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Form: test-0-g<abbrev>
        int gIndex = desc.IndexOf("-g", StringComparison.Ordinal);
        Assert.True(gIndex >= 0, $"expected -g suffix in '{desc}'");
        string abbrev = desc[(gIndex + 2)..];
        Assert.True(abbrev.Length >= 4, $"abbrev '{abbrev}' shorter than minimum 4");
    }
}

using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using GitBlame = LibGit2CS.Blame.GitBlame;

namespace LibGit2CS.UnitTests.Revwalk;

/// <summary>
/// Regression tests for the parity behaviors in the revwalk/describe/blame
/// subsystem (libgit2 1.9.4): the revwalk/describe and blame behaviors.
/// Expected error codes/messages were verified against libgit2 1.9.4 with C probes.
/// </summary>
public sealed class RevwalkBlameMedParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RevwalkBlameMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RevwalkBlameMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── Fixture / repo helpers ──────────────────────────────────────────

    // testrepo: master tip = a65fedf39aefe402d3bb6e24df4d4f5fe4547750, 'test' tag = b25fa35b38051e4ae45d4222e795f9df2e43f1d1
    private static readonly GitOid s_master = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_testTag = GitOid.Parse("b25fa35b38051e4ae45d4222e795f9df2e43f1d1".AsSpan(), GitHashAlgorithmKind.Sha1);

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }

    private async ValueTask<GitRepository> InitRepoAsync(string name)
    {
        string dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        return await GitRepository.InitAsync(dir, isBare: false, new GitContext());
    }

    private static async ValueTask<GitOid> WriteCommitAsync(
        GitRepository repo,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyList<string> remove,
        IReadOnlyList<GitOid> parents,
        string message,
        long timeSeconds,
        string? updateRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo.Workdir);

        foreach ((string name, string content) in files)
        {
            string filePath = Path.Combine(repo.Workdir, name);
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(filePath, content, cancellationToken: cancellationToken);
        }

        GitIndex index = await repo.GetIndexAsync(cancellationToken);
        foreach (string name in remove)
        {
            index.RemoveByPath(name);
        }

        foreach (string name in files.Keys)
        {
            await index.AddByPathAsync(name, cancellationToken);
        }

        await index.WriteAsync(cancellationToken);
        GitOid tree = await index.WriteTreeAsync(cancellationToken);
        var signature = new GitSignature("Test User", "test@example.com", new GitTime(timeSeconds, 0));
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = parents,
            Author = signature,
            Committer = signature,
            Message = message,
            UpdateRef = updateRef,
        }, cancellationToken);
    }

    private static ValueTask<GitOid> WriteCommitAsync(
        GitRepository repo,
        string path,
        string content,
        IReadOnlyList<GitOid> parents,
        string message,
        long timeSeconds,
        string? updateRef,
        CancellationToken cancellationToken)
        => WriteCommitAsync(repo, new Dictionary<string, string> { [path] = content }, [], parents, message, timeSeconds, updateRef, cancellationToken);

    private static void WriteLooseRef(GitRepository repo, string refName, GitOid target)
    {
        string path = Path.Combine(repo.Path, refName.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, target.ToString() + "\n");
    }

    private static void DeleteObjectFile(GitRepository repo, GitOid oid)
    {
        string hex = oid.ToString();
        File.Delete(Path.Combine(repo.Path, "objects", hex[..2], hex[2..]));
    }

    // ── push/hide of a non-committish returns the ORIGINAL peel error code ──

    [Fact]
    public async Task PushTreeOid_ThrowsOriginalPeelCode()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Commit master = (await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken))!;
        GitOid tree = master.Tree;

        using GitRevWalker walker = repo.NewRevWalker();
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.PushAsync(tree, TestContext.Current.CancellationToken));
        // C (revwalk.c:58-65): message is set but the ORIGINAL code is returned
        // (probe: push tree oid → GIT_EINVALIDSPEC).
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("object is not a committish", ex.Message);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    [Fact]
    public async Task HideTreeOid_ThrowsOriginalPeelCode()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Commit master = (await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken))!;
        GitOid tree = master.Tree;

        using GitRevWalker walker = repo.NewRevWalker();
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.HideAsync(tree, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("object is not a committish", ex.Message);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    [Fact]
    public async Task PushAnnotatedTagToBlob_ThrowsPeelCode()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        using GitRevWalker walker = repo.NewRevWalker();
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.PushRefAsync("refs/tags/annotated_tag_to_blob", TestContext.Current.CancellationToken));
        // C probe: push refs/tags/annotated_tag_to_blob → GIT_EPEEL (-19).
        Assert.Equal(GitErrorCode.Peel, ex.Code);
        Assert.Equal("object is not a committish", ex.Message);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    // ── push_glob/hide_glob with a dangling ref fails the whole glob ──

    [Fact]
    public async Task PushGlobWithDanglingRef_FailsWholeGlob()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        WriteLooseRef(repo, "refs/tags/dangling", GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1));

        using GitRevWalker walker = repo.NewRevWalker();
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.PushGlobAsync("tags", TestContext.Current.CancellationToken));
        // C (revwalk.c:52-53): the object lookup failure is returned
        // unconditionally — the from_glob guard covers only the peel path.
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Equal("object not found - no match for id (ffffffffffffffffffffffffffffffffffffffff)", ex.Message);
    }

    [Fact]
    public async Task HideGlobWithDanglingRef_FailsWholeGlob()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        WriteLooseRef(repo, "refs/tags/dangling", GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1));

        using GitRevWalker walker = repo.NewRevWalker();
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.HideGlobAsync("tags", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Equal("object not found - no match for id (ffffffffffffffffffffffffffffffffffffffff)", ex.Message);
    }

    // ── walk/limit parse errors keep the underlying code & message ──

    [Fact]
    public async Task WalkMissingParent_PropagatesOdbError()
    {
        await using GitRepository repo = await InitRepoAsync("walk-missing-parent");
        GitOid c0 = await WriteCommitAsync(repo, "f.txt", "1\n", [], "c0\n", 1_700_000_000, "refs/heads/master", TestContext.Current.CancellationToken);
        GitOid c1 = await WriteCommitAsync(repo, "f.txt", "1\n2\n", [c0], "c1\n", 1_700_000_001, "refs/heads/master", TestContext.Current.CancellationToken);
        GitOid c2 = await WriteCommitAsync(repo, "other.txt", "o\n", [], "other\n", 1_700_000_002, "refs/heads/other", TestContext.Current.CancellationToken);
        WriteLooseRef(repo, "refs/tags/side", c2);
        DeleteObjectFile(repo, c0);

        using GitRevWalker walker = repo.NewRevWalker();
        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        // C (revwalk.c:404/425): git_commit_list_parse propagates the ODB error.
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Equal($"object not found - no match for id ({c0})", ex.Message);
    }

    [Fact]
    public async Task WalkNonCommitParent_PropagatesParseError()
    {
        using var ctx = new GitContext();
        ctx.Settings.StrictObjectCreation = false;
        string dir = Path.Combine(_tempDir, "walk-noncommit");
        Directory.CreateDirectory(dir);
        await using GitRepository repo = await GitRepository.InitAsync(dir, isBare: false, ctx, TestContext.Current.CancellationToken);

        // Tree for the commit.
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "hi\n", cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree = await index.WriteTreeAsync(TestContext.Current.CancellationToken);

        // A commit whose parent line references a BLOB oid.
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "hi\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var signature = new GitSignature("Test User", "test@example.com", new GitTime(1_700_000_000, 0));
        GitOid bad = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [blob],
            Author = signature,
            Committer = signature,
            Message = "child\n",
            UpdateRef = "refs/heads/bad",
        }, TestContext.Current.CancellationToken);

        using GitRevWalker walker = repo.NewRevWalker();
        await walker.PushRefAsync("refs/heads/bad", TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        // C (commit_list.c:210-212): git_error_set(GIT_ERROR_INVALID,
        // "object is no commit object"); error = -1.
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("object is no commit object", ex.Message);
    }

    // ── max_candidates_tags is clamped at 10 (C normalize_options) ──

    [Fact]
    public async Task MaxCandidateTags_ClampedToTen()
    {
        await using GitRepository repo = await InitRepoAsync("max-tags");

        // Linear chain C0..C11 tagged t1..t11, plus an untagged tip T.
        GitOid prev = await WriteCommitAsync(repo, "f.txt", "0\n", [], "c0\n", 1_700_000_000, "refs/heads/master", TestContext.Current.CancellationToken);
        for (int i = 1; i <= 11; i++)
        {
            prev = await WriteCommitAsync(repo, "f.txt", new string('x', i) + "\n", [prev], $"c{i}\n", 1_700_000_000 + i * 10, "refs/heads/master", TestContext.Current.CancellationToken);
            WriteLooseRef(repo, $"refs/tags/t{i}", prev);
        }

        GitOid tip = await WriteCommitAsync(repo, "f.txt", "y\n", [prev], "tip\n", 1_700_000_000 + 120, "refs/heads/master", TestContext.Current.CancellationToken);

        // C normalize_options (describe.c:647-648) clamps any value > 10 to 10;
        // verified with a C probe: the 11-tag chain describes as "t11-1-g<tip>"
        // for max_candidates_tags = 10, 100, and even 2.
        var options = new GitDescribeOptions
        {
            Strategy = GitDescribeStrategy.Tags,
            MaxCandidateTags = 100,
            AlwaysUseLongFormat = true,
            MinimumAbbreviatedSize = 40,
        };
        string desc = await repo.DescribeAsync(tip, options, TestContext.Current.CancellationToken);
        Assert.Equal($"t11-1-g{tip}", desc);

        string desc10 = await repo.DescribeAsync(tip, options with { MaxCandidateTags = 10 }, TestContext.Current.CancellationToken);
        Assert.Equal(desc, desc10);
    }

    // ── describe pattern + non-tag ref — C skips all non-tags ──

    [Fact]
    public async Task PatternWithAllStrategy_SkipsNonTagRefs()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // C (describe.c:220-223): with a pattern, every non-tag ref is rejected
        // outright (`!is_tag` short-circuit) before the wildmatch, so
        // "refs/heads/master" never matches "*heads*". C includes no refs at
        // all here → "no reference found" (probe: rc=-1, GIT_ERROR_DESCRIBE).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(s_master, new GitDescribeOptions
            {
                Strategy = GitDescribeStrategy.All,
                Pattern = "*heads*",
            }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Describe, ex.Category);
        Assert.Equal("cannot describe - no reference found, cannot describe anything.", ex.Message);
    }

    // ── a dangling/broken ref in refs/tags/ fails the whole describe ──

    [Fact]
    public async Task DanglingRefInTags_FailsDescribe()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        WriteLooseRef(repo, "refs/tags/dangling", GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1));

        // C: retrieve_peeled_tag_or_object_oid → git_reference_peel fails
        // (refs.c peel_error): GIT_ENOTFOUND "the reference 'refs/tags/dangling'
        // cannot be peeled - Cannot retrieve reference target" (probe confirmed).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(s_testTag, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("the reference 'refs/tags/dangling' cannot be peeled - Cannot retrieve reference target", ex.Message);
    }

    // ── always_use_long_format + abbreviated_size == 0 is an error ──

    [Fact]
    public async Task AlwaysLongWithZeroAbbrev_Throws()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // C (describe.c:792-797) — the message reproduces C's missing-space typo.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(s_testTag, new GitDescribeOptions
            {
                AlwaysUseLongFormat = true,
                MinimumAbbreviatedSize = 0,
            }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Describe, ex.Category);
        Assert.Equal("cannot describe - 'always_use_long_format' is incompatible with a zero'abbreviated_size'", ex.Message);
    }

    // ── missing annotated tag object errors in display_name ──

    [Fact]
    public async Task MissingAnnotatedTagObject_Throws()
    {
        await using GitRepository repo = await InitRepoAsync("missing-tag-obj");

        GitOid commit = await WriteCommitAsync(repo, "f.txt", "hi\n", [], "c0\n", 1_700_000_000, "refs/heads/master", TestContext.Current.CancellationToken);
        Commit target = (await repo.ObjectLookupAsync<Commit>(commit, TestContext.Current.CancellationToken))!;
        GitOid tagOid = await repo.TagCreateAsync("v1", target, new GitSignature("Test User", "test@example.com", new GitTime(1_700_000_001, 0)), "v1 msg\n", cancellationToken: TestContext.Current.CancellationToken);

        // Pack the ref with a peel line (as `git pack-refs` writes) and drop
        // the loose ref, then delete the tag OBJECT from the ODB. With the peel
        // line C's git_reference_peel succeeds via the packed peel, so the name
        // map is built — and display_name then fails on the missing tag object
        // (probe: rc=-1, GIT_ERROR_TAG "annotated tag 'v1' not available").
        await File.WriteAllTextAsync(
            Path.Combine(repo.Path, "packed-refs"),
            $"# pack-refs with: peeled fully-peeled sorted \n{tagOid} refs/tags/v1\n^{commit}\n",
            cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(repo.Path, "refs", "tags", "v1"));
        DeleteObjectFile(repo, tagOid);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(commit, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Tag, ex.Category);
        Assert.Equal("annotated tag 'v1' not available", ex.Message);
    }

    // ── describe walk parse failures (missing parents) propagate ──

    [Fact]
    public async Task DescribeMissingParent_PropagatesOdbError()
    {
        await using GitRepository repo = await InitRepoAsync("describe-missing-parent");
        GitOid c0 = await WriteCommitAsync(repo, "f.txt", "1\n", [], "c0\n", 1_700_000_000, "refs/heads/master", TestContext.Current.CancellationToken);
        GitOid c1 = await WriteCommitAsync(repo, "f.txt", "1\n2\n", [c0], "c1\n", 1_700_000_001, "refs/heads/master", TestContext.Current.CancellationToken);
        GitOid c2 = await WriteCommitAsync(repo, "other.txt", "o\n", [], "other\n", 1_700_000_002, "refs/heads/other", TestContext.Current.CancellationToken);
        WriteLooseRef(repo, "refs/tags/side", c2);
        DeleteObjectFile(repo, c0);

        // C (describe.c:482-484, 543): git_commit_list_parse failure aborts the
        // describe with the ODB error (probe: GIT_ENOTFOUND + "object not found").
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(c1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Equal($"object not found - no match for id ({c0})", ex.Message);
    }

    // ── blame "file too large to blame" error is not swallowed ──

    [Fact]
    public void DiffHunks_TooLarge_Throws()
    {
        // C (blame_git.c:381-385): GIT_ERROR_INVALID "file too large to blame".
        var sb = new BlameScoreboard
        {
            Repository = null!,
            Options = new GitBlameOptions(),
            Path = GitPath.FromUtf8String("f.txt"),
        };

        // Two > 1 GiB buffers that differ in the last byte (so trim_common_tail
        // stops after the first block) — the size check fires before xdiff runs.
        byte[] bigA = new byte[1024 * 1024 * 1024 + 1];
        byte[] bigB = new byte[1024 * 1024 * 1024 + 1];
        bigB[^1] = 1;

        var d = new BlameEngine.BlameChunkCbData(sb, null!, null!);
        GitException ex = Assert.Throws<GitException>(() =>
            BlameEngine.DiffHunks(bigA, bigB, d, new GitBlameOptions()));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("file too large to blame", ex.Message);
    }

    // ── blame with a missing parent commit fails ──

    [Fact]
    public async Task BlameMissingParent_Throws()
    {
        await using GitRepository repo = await InitRepoAsync("blame-missing-parent");
        GitOid c0 = await WriteCommitAsync(repo, "f.txt", "1\n", [], "c0\n", 1_700_000_000, "refs/heads/master", TestContext.Current.CancellationToken);
        await WriteCommitAsync(repo, "f.txt", "1\n2\n", [c0], "c1\n", 1_700_000_001, "refs/heads/master", TestContext.Current.CancellationToken);
        DeleteObjectFile(repo, c0);

        // C (blame_git.c:551-552): git_commit_parent failure aborts the blame
        // (probe: GIT_ENOTFOUND + ODB message).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.BlameFileAsync("f.txt", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Equal($"object not found - no match for id ({c0})", ex.Message);
    }

    // ── find_origin pathspec covers the accumulated blame->paths ──

    [Fact]
    public async Task FindOriginPathspec_UsesAccumulatedPaths()
    {
        await using GitRepository repo = await InitRepoAsync("origin-pathspec");

        // C0: a.txt = X, b.txt = Y
        // C1: rename a.txt -> c.txt (tree: b.txt, c.txt)
        // C2: rename b.txt -> a.txt (tree: a.txt, c.txt)
        GitOid c0 = await WriteCommitAsync(
            repo,
            new Dictionary<string, string> { ["a.txt"] = "X1\nX2\n", ["b.txt"] = "Y1\nY2\n" },
            [],
            [],
            "c0\n",
            1_700_000_000,
            "refs/heads/master",
            TestContext.Current.CancellationToken);
        GitOid c1 = await WriteCommitAsync(
            repo,
            new Dictionary<string, string> { ["c.txt"] = "X1\nX2\n" },
            ["a.txt"],
            [c0],
            "c1\n",
            1_700_000_001,
            "refs/heads/master",
            TestContext.Current.CancellationToken);
        await WriteCommitAsync(
            repo,
            new Dictionary<string, string> { ["a.txt"] = "Y1\nY2\n" },
            ["b.txt"],
            [c1],
            "c2\n",
            1_700_000_002,
            "refs/heads/master",
            TestContext.Current.CancellationToken);

        // Blaming a.txt at C2: after the b.txt->a.txt rename is followed back to
        // (C1, b.txt), C's find_origin diffs with pathspec {a.txt, b.txt} — the
        // a.txt deletion (from the C0->C1 rename) triggers the full rename
        // search, which finds only the a.txt->c.txt rename (c.txt is not tracked)
        // → porigin is NULL → blame stays on C1 (probe: C blames C1).
        using GitBlame blame = await repo.BlameFileAsync("a.txt", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, blame.HunkCount);
        BlameHunk hunk = blame.GetHunk(0);
        Assert.Equal(c1, hunk.FinalCommitId);
        Assert.Equal("b.txt", hunk.OrigPath.ToUtf8String());
        Assert.Equal(2, hunk.LinesInHunk);
    }
}

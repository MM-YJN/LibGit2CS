using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

/// <summary> Parity tests for the refs subsystem. </summary>
public sealed class RefsMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RefsMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefsMedParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string GitDir => Path.Combine(_tempDir, ".git");
    private string LogsDir => Path.Combine(GitDir, "logs");
    private string PackedRefsPath => Path.Combine(GitDir, "packed-refs");

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommit(string refName = "refs/heads/master")
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Message = "commit",
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            UpdateRef = refName,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Packs <paramref name="refs"/> (name → oid) by writing
    /// packed-refs directly and removing the loose files.</summary>
    private async Task PackRefsAsync(Dictionary<string, GitOid> refs)
    {
        foreach (string name in refs.Keys)
        {
            string loose = Path.Combine(GitDir, name);
            if (File.Exists(loose))
            {
                File.Delete(loose);
            }
        }

        var sb = new System.Text.StringBuilder();
        // a NON-peeled header — with a peeled/fully-peeled header,
        // C marks refs lacking a peel line PACKREF_CANNOT_PEEL and emits no
        // ^ line on rewrite (refdb_fs.c:197-202); the peel computation only
        // runs for non-peeled files.
        sb.Append("# pack-refs with: sorted \n");
        foreach ((string name, GitOid oid) in refs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(oid).Append(' ').Append(name).Append('\n');
        }

        await File.WriteAllTextAsync(PackedRefsPath, sb.ToString(), cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── git_reflog_read creates a missing reflog file ───────────────

    [Fact]
    public async Task ReadLog_CreatesMissingReflogFile()
    {
        // logallrefupdates=false → creating the ref writes no reflog.
        await _repo.Config.SetStringAsync("core.logallrefupdates", "false", TestContext.Current.CancellationToken);
        await WriteCommit("refs/heads/nolog");

        string logPath = Path.Combine(LogsDir, "refs", "heads", "nolog");
        Assert.False(File.Exists(logPath));

        // C (refdb_fs.c:2149-2155): git_reflog_read creates the missing log
        // file and returns an empty reflog.
        GitRefLog reflog = await _repo.ReferenceReadLogAsync("refs/heads/nolog", TestContext.Current.CancellationToken)
            ?? throw new Xunit.Sdk.XunitException("expected a non-null empty reflog");
        Assert.Equal(0, reflog.EntryCount);
        Assert.True(File.Exists(logPath));
        Assert.True(await _repo.Refs.HasLogAsync("refs/heads/nolog", TestContext.Current.CancellationToken));
    }

    // ── rename appends the reflog unconditionally ───────────────────

    [Fact]
    public async Task Rename_AppendsReflogUnconditionally()
    {
        await _repo.Config.SetStringAsync("core.logallrefupdates", "false", TestContext.Current.CancellationToken);
        await WriteCommit("refs/heads/a");

        GitReference a = (await _repo.ReferenceLookupAsync("refs/heads/a", TestContext.Current.CancellationToken))!;
        Assert.False(await _repo.Refs.HasLogAsync("refs/heads/a", TestContext.Current.CancellationToken));

        // C (refdb_fs.c:1878-1885): after a rename, reflog_append runs
        // regardless of core.logallrefupdates — the log file is created.
        await _repo.ReferenceRenameAsync(a, "refs/heads/b", logMessage: "renamed", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.Refs.HasLogAsync("refs/heads/b", TestContext.Current.CancellationToken));
        GitRefLog reflog = (await _repo.ReferenceReadLogAsync("refs/heads/b", TestContext.Current.CancellationToken))!;
        Assert.Equal(1, reflog.EntryCount);
    }

    // ── "always" map match is case-insensitive ────────────────────────────

    [Fact]
    public async Task CreateRef_LogAllRefUpdatesMixedCaseAlways_WritesReflogForAnyRef()
    {
        // C (config.c:1408-1411): the GIT_CONFIGMAP_STRING arm of
        // git_config_lookup_map_value uses strcasecmp — "ALWAYS" maps to
        // GIT_LOGALLREFUPDATES_ALWAYS, so a ref OUTSIDE heads/remotes/notes
        // gets a reflog (refdb.c:334-336).
        await _repo.Config.SetStringAsync("core.logallrefupdates", "ALWAYS", TestContext.Current.CancellationToken);
        GitOid oid = await WriteCommit();

        await _repo.ReferenceCreateAsync("refs/custom/x", oid, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.Refs.HasLogAsync("refs/custom/x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateRef_LogAllRefUpdatesUnmappableValue_FailsRefWrite()
    {
        // C (config.c:1415-1416 + refdb.c:306-308): a value mapping to
        // neither FALSE/TRUE nor "always" fails the configmap lookup and the
        // error propagates out of git_refdb_should_write_reflog — the ref
        // write FAILS.
        GitOid oid = await WriteCommit();
        await _repo.Config.SetStringAsync("core.logallrefupdates", "banana", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() =>
            _repo.ReferenceCreateAsync("refs/custom/x", oid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Equal("failed to map 'banana'", ex.Message);
    }

    // ── ref_is_available prefix collisions ───────────────────────────

    [Fact]
    public async Task Create_RefCollidesWithPackedRef_ThrowsError()
    {
        GitOid oid = await WriteCommit();
        await PackRefsAsync(new() { ["refs/heads/a/b"] = oid });

        // C (refdb_fs.c:1083-1144): creating refs/heads/a when
        // refs/heads/a/b is packed → "path to reference '%s' collides with
        // existing one", code -1 (GIT_ERROR).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceCreateAsync("refs/heads/a", oid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);
        Assert.Equal("path to reference 'refs/heads/a' collides with existing one", ex.Message);
    }

    [Fact]
    public async Task Create_PackedRefCollidesWithNewRef_ThrowsError()
    {
        GitOid oid = await WriteCommit();
        await PackRefsAsync(new() { ["refs/heads/a"] = oid });

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceCreateAsync("refs/heads/a/b", oid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("path to reference 'refs/heads/a/b' collides with existing one", ex.Message);
    }

    [Fact]
    public async Task Rename_CollidesWithPackedRef_ThrowsError()
    {
        GitOid oid = await WriteCommit("refs/heads/x");
        await PackRefsAsync(new() { ["refs/heads/a/b"] = oid });

        GitReference x = (await _repo.ReferenceLookupAsync("refs/heads/x", TestContext.Current.CancellationToken))!;
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceRenameAsync(x, "refs/heads/a", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("path to reference 'refs/heads/a' collides with existing one", ex.Message);
    }

    // ── negative refspec src matching keeps the '^' ──────────────────

    [Fact]
    public void NegativeRefspec_SrcMatches_NeverMatches()
    {
        // C (refspec.c:239-253): git_refspec_src_matches wildmatches the raw
        // src (with the '^' prefix) — the literal '^' never matches a refname.
        var rs = GitRefSpec.Parse("^refs/heads/secret", isFetch: true);
        Assert.False(rs.SrcMatches("refs/heads/secret"));
        Assert.False(rs.SrcMatches("refs/heads/secret/extra"));
    }

    [Fact]
    public void NegativeRefspec_SrcMatchesNegative_Matches()
    {
        var rs = GitRefSpec.Parse("^refs/heads/secret", isFetch: true);
        Assert.True(rs.SrcMatchesNegative("refs/heads/secret"));
        Assert.False(rs.SrcMatchesNegative("refs/heads/public"));
    }

    [Fact]
    public void NegativeRefspec_Transform_Throws()
    {
        var rs = GitRefSpec.Parse("^refs/heads/secret", isFetch: true);

        // C (refspec.c:311-318): transform fails with "ref '%s' doesn't match
        // the source" (GIT_ERROR -1, class GIT_ERROR_INVALID).
        GitException ex = Assert.Throws<GitException>(() => rs.Transform("refs/heads/secret"));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("ref 'refs/heads/secret' doesn't match the source", ex.Message);
    }

    // ── reflog rename onto a non-empty directory ─────────────────────

    [Fact]
    public async Task Rename_ReflogTargetIsNonEmptyDir_PreservesTree()
    {
        await WriteCommit("refs/heads/a");
        Assert.True(await _repo.Refs.HasLogAsync("refs/heads/a", TestContext.Current.CancellationToken));

        // Non-empty directory at the reflog destination.
        string blockerDir = Path.Combine(LogsDir, "refs", "heads", "b");
        Directory.CreateDirectory(blockerDir);
        await File.WriteAllTextAsync(Path.Combine(blockerDir, "other"), "x\n", cancellationToken: TestContext.Current.CancellationToken);

        GitReference a = (await _repo.ReferenceLookupAsync("refs/heads/a", TestContext.Current.CancellationToken))!;

        // C (refdb_fs.c:2410-2444): rmdir_r with GIT_RMDIR_SKIP_NONEMPTY
        // leaves the non-empty directory in place and the reflog placement
        // fails — but the ref rename itself succeeds (loose_commit runs
        // regardless), so the directory tree must NOT be deleted.
        await _repo.ReferenceRenameAsync(a, "refs/heads/b", logMessage: "renamed", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await _repo.ReferenceLookupAsync("refs/heads/b", TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(blockerDir), "blocker directory must survive");
        Assert.True(File.Exists(Path.Combine(blockerDir, "other")), "blocker file must survive");
    }

    // ── reference iteration order ────────────────────────────────────

    [Fact]
    public async Task Enumerate_Order_LoosePerDirSortedThenPackedSorted()
    {
        GitOid oid = await WriteCommit("refs/heads/master");
        await _repo.ReferenceCreateAsync("refs/heads/b", oid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/heads/a", oid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/tags/z", oid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/remotes/origin/x", oid, cancellationToken: TestContext.Current.CancellationToken);

        // Move tags/packed2 + tags/packed1 into packed-refs.
        await PackRefsAsync(new() { ["refs/tags/packed2"] = oid, ["refs/tags/packed1"] = oid });

        // C (refdb_fs.c:955-1037 + iterator.c:1482-1483): loose refs come in
        // per-directory byte-sorted depth-first order, then packed refs sorted
        // by name. Note HEAD is not under refs/ and is not enumerated.
        var names = new List<string>();
        await foreach (string name in _repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(name);
        }

        Assert.Equal(
            [
                "refs/heads/a",
                "refs/heads/b",
                "refs/heads/master",
                "refs/remotes/origin/x",
                "refs/tags/z",
                "refs/tags/packed1",
                "refs/tags/packed2",
            ],
            names);
    }

    // ── branch guard error codes — C returns -1 (GIT_ERROR) ──────────

    [Fact]
    public async Task BranchCreate_InvalidName_ThrowsError()
    {
        GitOid oid = await WriteCommit();

        // C (branch.c:86-90): "'%s' is not a valid branch name", -1.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.BranchCreateAsync("-bad", oid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);
        Assert.Equal("'-bad' is not a valid branch name", ex.Message);
    }

    [Fact]
    public async Task BranchCreate_ForceCurrentHead_ThrowsError()
    {
        GitOid oid = await WriteCommit(); // creates refs/heads/master, the current HEAD branch

        // C (branch.c:99-105): "cannot force update branch '%s' as it is the
        // current HEAD of the repository.", -1.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.BranchCreateAsync("master", oid, force: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);
        Assert.Equal("cannot force update branch 'master' as it is the current HEAD of the repository", ex.Message);
    }

    [Fact]
    public async Task BranchDelete_CurrentHead_ThrowsError()
    {
        await WriteCommit(); // master is HEAD

        GitReference master = (await _repo.BranchLookupAsync("master", GitBranchType.Local, TestContext.Current.CancellationToken))!;

        // C (branch.c:206-210): "cannot delete branch '%s' as it is the
        // current HEAD of the repository.", -1.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await master.DeleteAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);
        Assert.Equal("cannot delete branch 'refs/heads/master' as it is the current HEAD of the repository", ex.Message);
    }

    [Fact]
    public async Task BranchMove_NonBranch_ThrowsError()
    {
        GitOid oid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/tags/v1", oid, cancellationToken: TestContext.Current.CancellationToken);
        GitReference tag = (await _repo.ReferenceLookupAsync("refs/tags/v1", TestContext.Current.CancellationToken))!;

        // C (branch.c:48-54): not_a_local_branch → -1, class GIT_ERROR_INVALID,
        // "reference '%s' is not a local branch."
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await tag.MoveAsync("newname", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("reference 'refs/tags/v1' is not a local branch.", ex.Message);
    }

    // ── symbolic ref target validation ───────────────────────────────

    [Fact]
    public async Task CreateSymbolic_InvalidTarget_ThrowsInvalidSpec()
    {
        await WriteCommit();

        // C (refs.c:413-421): the symbolic target is normalized/validated
        // (".." is not a valid ref-name segment) → GIT_EINVALIDSPEC
        // "the given reference name '%s' is not valid" (C-verified probe).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceCreateSymbolicAsync("refs/heads/sym", "refs/heads/../evil", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);
        Assert.Equal("the given reference name 'refs/heads/../evil' is not valid", ex.Message);
    }

    [Fact]
    public async Task CreateSymbolic_EmptySegmentTarget_IsNormalized()
    {
        await WriteCommit();

        // C normalizes the target with buf != NULL, so empty segments are
        // skipped: "refs//heads/other" is stored as "refs/heads/other"
        // (C-verified probe: rc=0, stored='refs/heads/other').
        GitReference sym = await _repo.ReferenceCreateSymbolicAsync("refs/heads/sym", "refs//heads/other", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sym.IsSymbolic);
        Assert.Equal("refs/heads/other", ((GitSymbolicReference)sym).TargetName);
    }

    [Fact]
    public async Task CreateSymbolic_ValidTarget_Succeeds()
    {
        await WriteCommit();
        GitReference sym = await _repo.ReferenceCreateSymbolicAsync("refs/heads/sym", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sym.IsSymbolic);
        Assert.Equal("refs/heads/master", ((GitSymbolicReference)sym).TargetName);
    }

    // ── IsNameValid rejects empty segments ───────────────────────────

    [Fact]
    public void IsNameValid_EmptySegment_Invalid()
    {
        // C (refs.c:991-993): git_reference_name_is_valid runs the normalizer
        // with buf == NULL — "No empty segment is allowed when not
        // normalizing", so refs//heads is INVALID.
        Assert.False(GitReferences.IsNameValid("refs//heads"));
        Assert.True(GitReferences.IsNameValid("refs/heads"));
    }

    // ── packed-refs repack computes missing peels ────────────────────

    [Fact]
    public async Task PackedDelete_Repack_ComputesPeels()
    {
        GitOid commitOid = await WriteCommit();
        GitOid tagOid = await _repo.TagCreateAsync("v1", (await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!, TestSig(), "tag msg", cancellationToken: TestContext.Current.CancellationToken);

        // Pack both refs WITHOUT peel lines (as a non-peeled packed-refs file).
        await PackRefsAsync(new() { ["refs/heads/master"] = commitOid, ["refs/tags/v1"] = tagOid });

        // Deleting a packed ref triggers the rewrite; C's packed_find_peel
        // (refdb_fs.c:1267-1303) resolves the tag object and emits the peel.
        await _repo.Refs.DeleteAsync("refs/heads/master", TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(PackedRefsPath, cancellationToken: TestContext.Current.CancellationToken);
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains($"^{commitOid}", lines);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Refs;

/// <summary>
/// Integration tests for ref enumeration (<see cref="GitReferences.ListAsync"/>
/// and <see cref="GitReferences.ListNamesAsync"/>) exercised end-to-end against
/// locally-initialized repos with mixed loose/packed ref layouts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>ReferencesTests</c>/<c>RefWriteTests</c>
/// cover the read/write primitives against a real repo in isolation, and
/// <see cref="ReferenceIntegrationTests"/> covers DWIM/CAS/reflog/branch flows.
/// Neither drains the <see cref="FileRefBackend.EnumerateCoreAsync"/> iterator
/// over a realistic ref layout — loose refs mixed with packed-refs, marker
/// files, <c>.lock</c> files, and loose-shadowing-packed. The 81-line
/// <c>MoveNext</c> in <see cref="FileRefBackend"/> was entirely cold. These
/// tests close that gap by enumerating to completion over deliberately
/// constructed ref layouts.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/refs/refs.c</c>
/// (<c>test_ref_iters__create_delete_one</c>,
/// <c>test_ref_iters__list_all</c>,
/// <c>test_ref_iters__list_for_glob</c>), adapted to build the sandbox from
/// scratch and write <c>packed-refs</c> directly to disk.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class RefEnumerationIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-refsenum-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Inits a non-bare repo, writes an initial commit on
    /// <c>refs/heads/main</c>, sets HEAD, and returns the commit OID.
    /// </summary>
    private static async Task<GitOid> InitRepoWithCommitAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return commitOid;
    }

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes a <c>packed-refs</c> file at <c>&lt;repo&gt;/.git/packed-refs</c>
    /// with the given (refName → oid-hex) entries, one per line. Used to test
    /// the packed-only enumeration branch of
    /// <see cref="FileRefBackend.EnumerateCoreAsync"/>.
    /// </summary>
    private static async Task WritePackedRefsAsync(string gitDir, IReadOnlyDictionary<string, string> entries, CancellationToken ct)
    {
        string packedPath = Path.Combine(gitDir, "packed-refs");
        var sb = new System.Text.StringBuilder();
        sb.Append("# pack-refs with: peeled fully-peeled sorted \n");
        foreach ((string name, string oid) in entries)
        {
            sb.Append(oid).Append(' ').Append(name).Append('\n');
        }

        await File.WriteAllTextAsync(packedPath, sb.ToString(), ct);
    }

    /// <summary>Deletes a loose ref file if it exists.</summary>
    private static void DeleteLooseRef(string gitDir, string refName)
    {
        string loosePath = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loosePath))
        {
            File.Delete(loosePath);
        }
    }

    // ── ListAsync — full ref enumeration ────────────────────────────────

    /// <summary>
    /// <see cref="GitReferences.ListAsync"/> with no glob yields every
    /// loose ref in the repo. With <c>refs/heads/main</c>,
    /// <c>refs/heads/feature</c>, and <c>refs/tags/v1</c> all loose, the
    /// enumeration yields exactly three references with matching names and
    /// OIDs. Exercises the loose-ref walk loop in
    /// <see cref="FileRefBackend.EnumerateCoreAsync"/> (the
    /// <c>EnumerateFilesRecursive</c> → <c>LooseLookupByPathAsync</c> →
    /// yield path).
    /// </summary>
    [Fact]
    public async Task ListAsync_NoGlob_YieldsAllLooseRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            List<GitReference> refs = await repo.ReferenceListAsync(cancellationToken: ct).ToListAsync(ct);

            Assert.Equal(3, refs.Count);
            var byName = refs.ToDictionary(r => r.Name);
            Assert.Contains("refs/heads/main", byName);
            Assert.Contains("refs/heads/feature", byName);
            Assert.Contains("refs/tags/v1", byName);
            Assert.Equal(commitOid, Assert.IsType<GitDirectReference>(byName["refs/heads/main"]).Target);
            Assert.Equal(commitOid, Assert.IsType<GitDirectReference>(byName["refs/heads/feature"]).Target);
            Assert.Equal(commitOid, Assert.IsType<GitDirectReference>(byName["refs/tags/v1"]).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReferences.ListAsync"/> with a glob
    /// <c>refs/heads/*</c> filters out non-matching refs (tags). With
    /// <c>refs/heads/main</c>, <c>refs/heads/feature</c>, and
    /// <c>refs/tags/v1</c>, only the two heads are yielded. Exercises the
    /// <c>MatchesGlob</c> filter path in the loose-ref loop.
    /// </summary>
    [Fact]
    public async Task ListAsync_GlobHeadsOnly_FiltersTags()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            List<GitReference> heads = await repo.ReferenceListAsync("refs/heads/*", ct).ToListAsync(ct);

            Assert.Equal(2, heads.Count);
            Assert.All(heads, r => Assert.StartsWith("refs/heads/", r.Name));
            Assert.DoesNotContain(heads, r => r.Name.StartsWith("refs/tags/"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ListNamesAsync — name-only enumeration ───────────────────────────

    /// <summary>
    /// <see cref="GitReferences.ListNamesAsync"/> with no glob yields
    /// the names of all refs without resolving them. With three loose refs
    /// the enumeration yields exactly three names. Exercises the
    /// <c>includeRefs: false</c> branch of
    /// <see cref="FileRefBackend.EnumerateCoreAsync"/> (yielding a stub
    /// direct reference whose name is the only populated field).
    /// </summary>
    [Fact]
    public async Task ListNamesAsync_NoGlob_YieldsAllRefNames()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            List<string> names = await repo.Refs.ListNamesAsync(cancellationToken: ct).ToListAsync(ct);

            Assert.Equal(3, names.Count);
            Assert.Contains("refs/heads/main", names);
            Assert.Contains("refs/heads/feature", names);
            Assert.Contains("refs/tags/v1", names);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReferences.ListNamesAsync"/> with a glob
    /// <c>refs/tags/*</c> yields only tag names. Exercises the glob filter
    /// on the name-only enumeration path.
    /// </summary>
    [Fact]
    public async Task ListNamesAsync_GlobTagsOnly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v2", commitOid, cancellationToken: ct);

            List<string> tags = await repo.Refs.ListNamesAsync("refs/tags/*", ct).ToListAsync(ct);

            Assert.Equal(2, tags.Count);
            Assert.Contains("refs/tags/v1", tags);
            Assert.Contains("refs/tags/v2", tags);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── packed-refs enumeration ──────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReferences.ListAsync"/> yields refs that exist only
    /// in <c>packed-refs</c> (no loose file on disk). Writing a
    /// <c>packed-refs</c> file with two entries, deleting the corresponding
    /// loose files, and enumerating yields both packed refs with the OIDs
    /// from the file. Exercises the packed-only branch of
    /// <see cref="FileRefBackend.EnumerateCoreAsync"/> (the
    /// <c>_packedRefs</c> loop after the loose walk).
    /// </summary>
    [Fact]
    public async Task ListAsync_PackedOnly_YieldsPackedRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            // Create a second commit to use as a distinct packed-refs target.
            string gitDir;
            GitOid secondCommit;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                gitDir = repo.Path;
                GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "world\n"u8.ToArray(), ct);
                using GitTreeBuilder bld = repo.NewTreeBuilder();
                await bld.InsertAsync("world.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await bld.WriteAsync(ct);
                secondCommit = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [commitOid],
                    Author = Sig,
                    Committer = Sig,
                    Message = "second\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // Delete the loose refs/heads/main so only packed-refs remains.
            DeleteLooseRef(gitDir, "refs/heads/main");

            // Write a packed-refs file with main + a packed-only tag.
            await WritePackedRefsAsync(gitDir, new Dictionary<string, string>
            {
                ["refs/heads/main"] = commitOid.ToString(),
                ["refs/tags/packed"] = secondCommit.ToString(),
            }, ct);

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitReference> refs = await repo2.ReferenceListAsync(cancellationToken: ct).ToListAsync(ct);

            var byName = refs.ToDictionary(r => r.Name);
            Assert.Equal(2, byName.Count);
            Assert.Contains("refs/heads/main", byName);
            Assert.Contains("refs/tags/packed", byName);
            Assert.Equal(commitOid, Assert.IsType<GitDirectReference>(byName["refs/heads/main"]).Target);
            Assert.Equal(secondCommit, Assert.IsType<GitDirectReference>(byName["refs/tags/packed"]).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A loose ref shadows a packed ref with the same name: enumeration
    /// yields exactly one entry, with the loose OID. Exercises the
    /// <c>seen</c>-set dedup in <see cref="FileRefBackend.EnumerateCoreAsync"/>
    /// — loose refs are walked first and added to <c>seen</c>, then the
    /// packed loop skips any name already in <c>seen</c>.
    /// </summary>
    [Fact]
    public async Task ListAsync_LooseShadowsPacked_NoDuplicates()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string gitDir;
            GitOid secondCommit;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                gitDir = repo.Path;
                GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "world\n"u8.ToArray(), ct);
                using GitTreeBuilder bld = repo.NewTreeBuilder();
                await bld.InsertAsync("world.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await bld.WriteAsync(ct);
                secondCommit = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [commitOid],
                    Author = Sig,
                    Committer = Sig,
                    Message = "second\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // packed-refs says main → firstCommit, but the loose ref (still
            // on disk) says main → secondCommit. The loose OID must win.
            await WritePackedRefsAsync(gitDir, new Dictionary<string, string>
            {
                ["refs/heads/main"] = commitOid.ToString(),
            }, ct);

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitReference> refs = await repo2.ReferenceListAsync(cancellationToken: ct).ToListAsync(ct);

            var mainRefs = refs.Where(r => r.Name == "refs/heads/main").ToList();
            Assert.Single(mainRefs);
            Assert.Equal(secondCommit, Assert.IsType<GitDirectReference>(mainRefs[0]).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── enumeration robustness ──────────────────────────────────────────

    /// <summary>
    /// Enumeration skips <c>.lock</c> files and marker files
    /// (<c>.gitkeep</c>) that may coexist with real loose refs. With a
    /// <c>refs/heads/main.lock</c> file and a <c>refs/heads/.gitkeep</c>
    /// marker present alongside the real <c>refs/heads/main</c> loose ref,
    /// enumeration yields only <c>refs/heads/main</c>. Exercises the
    /// <c>.lock</c>-skip and the <c>LooseLookupByPathAsync</c>-returns-null
    /// skip paths in the loose-ref walk.
    /// </summary>
    [Fact]
    public async Task ListAsync_SkipsLockFilesAndMarkerFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string gitDir;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                gitDir = repo.Path;
            }

            // Drop a .lock file and a .gitkeep marker next to the loose ref.
            string headsDir = Path.Combine(gitDir, "refs", "heads");
            await File.WriteAllTextAsync(Path.Combine(headsDir, "main.lock"), commitOid.ToString() + "\n", ct);
            await File.WriteAllTextAsync(Path.Combine(headsDir, ".gitkeep"), string.Empty, ct);

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<string> names = await repo2.Refs.ListNamesAsync("refs/heads/*", ct).ToListAsync(ct);

            Assert.Single(names);
            Assert.Contains("refs/heads/main", names);
            Assert.DoesNotContain(names, n => n.EndsWith(".lock", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.EndsWith(".gitkeep", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Enumeration with a heads glob does not yield <c>HEAD</c> even
    /// though <c>HEAD</c> is a symbolic ref pointing at
    /// <c>refs/heads/main</c>. <c>HEAD</c> lives at the top of the git dir
    /// (not under <c>refs/</c>), so the loose-ref walk under
    /// <c>refs/</c> never visits it. Exercises the
    /// <c>EnumerateFilesRecursive(refsRoot)</c> scoping.
    /// </summary>
    [Fact]
    public async Task ListAsync_HeadsGlob_DoesNotYieldHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await InitRepoWithCommitAsync(NewRepoPath(), ct); // warm up temp dir but discard
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);

            List<string> names = await repo.Refs.ListNamesAsync("refs/heads/*", ct).ToListAsync(ct);

            Assert.DoesNotContain("HEAD", names);
            Assert.Contains("refs/heads/main", names);
            Assert.Contains("refs/heads/feature", names);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── glob slash semantics (regression) ───────────────────────────────

    /// <summary>
    /// A single <c>*</c> in a ref-name glob crosses <c>/</c>, matching
    /// libgit2's <c>wildmatch(glob, name, 0)</c>. With
    /// <c>refs/remotes/origin/main</c> and <c>refs/heads/feature/foo</c> on
    /// disk, the glob <c>refs/remotes/*</c> must yield the nested remote
    /// ref. Under the old <c>WildMatchFlags.Pathname</c> matcher <c>*</c>
    /// did not cross <c>/</c>, so this glob matched nothing.
    /// </summary>
    /// <remarks>
    /// This is the integration-level companion to the unit test
    /// <c>ReferencesTests.List_Glob_Star_CrossesSlash</c>: it builds the
    /// nested refs from scratch (no fixture) and drains the real
    /// <see cref="FileRefBackend.EnumerateCoreAsync"/> loose-ref walk for
    /// multi-segment names.
    /// </remarks>
    [Fact]
    public async Task ListNamesAsync_GlobRemotesStar_MatchesNestedRemoteRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/remotes/origin/main", commitOid, cancellationToken: ct);

            List<string> names = await repo.Refs.ListNamesAsync("refs/remotes/*", ct).ToListAsync(ct);

            Assert.Contains("refs/remotes/origin/main", names);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// The negotiation glob <c>refs/*</c> must yield refs of every
    /// depth (<c>refs/heads/feature/foo</c>, <c>refs/remotes/origin/main</c>,
    /// <c>refs/tags/v1</c>). This is the exact glob
    /// <see cref="Transports.GitSmartProtocol.NegotiateFetchAsync"/> uses to
    /// enumerate local tips; under the old <c>Pathname</c> matcher every
    /// concrete ref (two-or-more segments deep) was filtered out, starving
    /// fetch negotiation of <c>have</c> lines.
    /// </summary>
    [Fact]
    public async Task ListNamesAsync_GlobRootStar_MatchesDeepAndShallowRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        GitOid commitOid = await InitRepoWithCommitAsync(path, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/feature/foo", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/remotes/origin/main", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            List<string> names = await repo.Refs.ListNamesAsync("refs/*", ct).ToListAsync(ct);

            Assert.Contains("refs/heads/main", names);
            Assert.Contains("refs/heads/feature/foo", names);
            Assert.Contains("refs/remotes/origin/main", names);
            Assert.Contains("refs/tags/v1", names);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

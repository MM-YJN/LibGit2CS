using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Objects;

public sealed class CommitGraphWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public CommitGraphWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CommitGraphWriter_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string ExtractRepo()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return path;
    }

    /// <summary>
    /// Port of <c>test_graph_commitgraph__writer</c>. Walks <c>refs/*</c> in
    /// testrepo, writes a commit-graph, and asserts byte-exact equality with the
    /// pre-generated <c>objects/info/commit-graph</c> file embedded in the
    /// fixture.
    /// </summary>
    [Fact]
    public async Task Writer_ProducesByteExactMatch()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string objectsInfoDir = Path.Combine(repoPath, "objects", "info");
        string expectedPath = Path.Combine(objectsInfoDir, "commit-graph");
        byte[] expected = await File.ReadAllBytesAsync(expectedPath, cancellationToken: TestContext.Current.CancellationToken);

        // Remove the pre-existing commit-graph so the writer creates a fresh one.
        File.Delete(expectedPath);

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        using GitRevWalker walker = repo.NewRevWalker();
        await foreach (string name in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            try
            {
                await walker.PushRefAsync(name, TestContext.Current.CancellationToken);
            }
            catch (GitException)
            {
                // Skip non-committish refs (blobs, tags pointing to blobs, etc.).
            }
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.AddRevwalkAsync(walker, TestContext.Current.CancellationToken);
        byte[] actual = writer.Dump();

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Writes a commit-graph via <see cref="CommitGraphWriter.CommitAsync"/>, then
    /// reads it back via <see cref="CommitGraph.OpenAsync"/> and verifies that
    /// entries match the original commits.
    /// </summary>
    [Fact]
    public async Task Writer_Commit_RoundTripReadsBackCorrectly()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string objectsDir = Path.Combine(repoPath, "objects");
        string objectsInfoDir = Path.Combine(objectsDir, "info");

        // Remove the pre-existing commit-graph.
        string existingCg = Path.Combine(objectsInfoDir, "commit-graph");
        if (File.Exists(existingCg))
        {
            File.Delete(existingCg);
        }

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        using GitRevWalker walker = repo.NewRevWalker();
        await foreach (string name in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            try
            {
                await walker.PushRefAsync(name, TestContext.Current.CancellationToken);
            }
            catch (GitException)
            {
                // Skip non-committish refs (blobs, tags pointing to blobs, etc.).
            }
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.AddRevwalkAsync(walker, TestContext.Current.CancellationToken);
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        // Read back.
        CommitGraph? cg = await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(cg);
        Assert.True(cg!.NumCommits > 0);

        // Verify a known commit: a65fedf39aefe402d3bb6e24df4d4f5fe4547750 (HEAD of master).
        var headOid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        CommitGraphEntry? entry = cg.FindEntry(headOid);
        Assert.NotNull(entry);
        Assert.Equal(headOid, entry!.Value.Oid);
        Assert.True(entry.Value.Generation > 0);
        Assert.True(entry.Value.ParentCount >= 1);

        // Verify a root commit: 5001298e0c09ad9c34e4249bc5801c75e9754fa5 (generation 1).
        var rootOid = GitOid.Parse("5001298e0c09ad9c34e4249bc5801c75e9754fa5".AsSpan(), GitHashAlgorithmKind.Sha1);
        CommitGraphEntry? rootEntry = cg.FindEntry(rootOid);
        Assert.NotNull(rootEntry);
        Assert.Equal(1u, rootEntry!.Value.Generation);
        Assert.Equal(0, rootEntry.Value.ParentCount);
    }

    /// <summary>
    /// A writer with no commits added should produce a valid commit-graph
    /// buffer (header + empty OIDF + trailer) without throwing. Note: the
    /// commit-graph format does not support 0-commit files that can be read
    /// back — the reader rejects empty OIDL/CDAT chunks. This matches C
    /// behavior: the C writer also produces an unreadable file for 0 commits.
    /// </summary>
    [Fact]
    public void Writer_EmptyRepo_ProducesBufferWithoutThrowing()
    {
        string objectsInfoDir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        byte[] data = writer.Dump();

        // Should have header (8) + chunk table (4 × 12) + OIDF (1024) + trailer (20).
        Assert.Equal(8 + 4 * 12 + 1024 + 20, data.Length);

        // Verify the magic header.
        Assert.Equal((byte)'C', data[0]);
        Assert.Equal((byte)'G', data[1]);
        Assert.Equal((byte)'P', data[2]);
        Assert.Equal((byte)'H', data[3]);
        Assert.Equal(1, data[4]); // version
    }

    /// <summary>
    /// Verifies that root commits (no parents) get generation 1.
    /// </summary>
    [Fact]
    public async Task Writer_GenerationNumbers_RootCommitIs1()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string objectsDir = Path.Combine(repoPath, "objects");
        string objectsInfoDir = Path.Combine(objectsDir, "info");

        File.Delete(Path.Combine(objectsInfoDir, "commit-graph"));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        using GitRevWalker walker = repo.NewRevWalker();
        await foreach (string name in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            try
            {
                await walker.PushRefAsync(name, TestContext.Current.CancellationToken);
            }
            catch (GitException)
            {
                // Skip non-committish refs (blobs, tags pointing to blobs, etc.).
            }
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.AddRevwalkAsync(walker, TestContext.Current.CancellationToken);
        byte[] data = writer.Dump();
        await File.WriteAllBytesAsync(Path.Combine(objectsInfoDir, "commit-graph"), data, cancellationToken: TestContext.Current.CancellationToken);

        CommitGraph cg = (await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;

        // 5001298e0c09ad9c34e4249bc5801c75e9754fa5 is a root commit in testrepo.
        var rootOid = GitOid.Parse("5001298e0c09ad9c34e4249bc5801c75e9754fa5".AsSpan(), GitHashAlgorithmKind.Sha1);
        CommitGraphEntry? entry = cg.FindEntry(rootOid);
        Assert.NotNull(entry);
        Assert.Equal(1u, entry!.Value.Generation);
        Assert.Equal(0, entry.Value.ParentCount);
    }

    /// <summary>
    /// Verifies that a child commit has generation = max(parent generations) + 1.
    /// </summary>
    [Fact]
    public async Task Writer_GenerationNumbers_ChildIsParentPlus1()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string objectsDir = Path.Combine(repoPath, "objects");
        string objectsInfoDir = Path.Combine(objectsDir, "info");

        File.Delete(Path.Combine(objectsInfoDir, "commit-graph"));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        using GitRevWalker walker = repo.NewRevWalker();
        await foreach (string name in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            try
            {
                await walker.PushRefAsync(name, TestContext.Current.CancellationToken);
            }
            catch (GitException)
            {
                // Skip non-committish refs (blobs, tags pointing to blobs, etc.).
            }
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.AddRevwalkAsync(walker, TestContext.Current.CancellationToken);
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        CommitGraph cg = (await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;

        // be3563ae3f795b2b4353bcce3a527ad0a4f7f644 has 2 parents in testrepo.
        var mergeOid = GitOid.Parse("be3563ae3f795b2b4353bcce3a527ad0a4f7f644".AsSpan(), GitHashAlgorithmKind.Sha1);
        CommitGraphEntry? entry = cg.FindEntry(mergeOid);
        Assert.NotNull(entry);
        Assert.Equal(5u, entry!.Value.Generation);
        Assert.Equal(2, entry.Value.ParentCount);

        // First parent should have generation 4.
        CommitGraphEntry? parent0 = cg.FindEntry(entry.Value.Parents[0]);
        Assert.NotNull(parent0);
        Assert.Equal(4u, parent0!.Value.Generation);
    }

    /// <summary>
    /// Verifies that the commit-graph file written by <see cref="CommitGraphWriter.CommitAsync"/>
    /// matches the output of <see cref="CommitGraphWriter.Dump"/>.
    /// </summary>
    [Fact]
    public async Task Writer_Commit_WritesSameBytesAsDump()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string objectsInfoDir = Path.Combine(repoPath, "objects", "info");

        File.Delete(Path.Combine(objectsInfoDir, "commit-graph"));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        using GitRevWalker walker = repo.NewRevWalker();
        await foreach (string name in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            try
            {
                await walker.PushRefAsync(name, TestContext.Current.CancellationToken);
            }
            catch (GitException)
            {
                // Skip non-committish refs (blobs, tags pointing to blobs, etc.).
            }
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.AddRevwalkAsync(walker, TestContext.Current.CancellationToken);
        byte[] dumped = writer.Dump();
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(Path.Combine(objectsInfoDir, "commit-graph"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(dumped, written);
    }

    /// <summary>
    /// Verifies that the OID fanout table is monotonically increasing.
    /// </summary>
    [Fact]
    public async Task Writer_Fanout_IsMonotonic()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string objectsInfoDir = Path.Combine(repoPath, "objects", "info");

        File.Delete(Path.Combine(objectsInfoDir, "commit-graph"));

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        using GitRevWalker walker = repo.NewRevWalker();
        await foreach (string name in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            try
            {
                await walker.PushRefAsync(name, TestContext.Current.CancellationToken);
            }
            catch (GitException)
            {
                // Skip non-committish refs (blobs, tags pointing to blobs, etc.).
            }
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.AddRevwalkAsync(walker, TestContext.Current.CancellationToken);
        byte[] data = writer.Dump();

        // Read back and verify fanout monotonicity.
        await File.WriteAllBytesAsync(Path.Combine(objectsInfoDir, "commit-graph"), data, cancellationToken: TestContext.Current.CancellationToken);
        CommitGraph? cg = await CommitGraph.OpenAsync(Path.Combine(repoPath, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(cg);
        // If it parsed successfully, the read side already validated monotonicity.
        Assert.True(cg!.NumCommits > 0);
    }

    // ── commit respects commit-graph.lock ──────

    [Fact]
    public async Task Commit_ExistingLockFile_ThrowsLocked()
    {
        // C (commit_graph.c:1279-1305 + filebuf.c:44-66): git_filebuf_open
        // creates <path>.lock with O_CREAT|O_EXCL; a pre-existing lock is
        // GIT_ELOCKED ("failed to create locked file").
        string objectsInfoDir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);
        string lockPath = Path.Combine(objectsInfoDir, "commit-graph.lock");
        await File.WriteAllTextAsync(lockPath, "stale", cancellationToken: TestContext.Current.CancellationToken);

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        GitException ex = await Assert.ThrowsAsync<GitException>(() => writer.CommitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Locked, ex.Code);
    }

    [Fact]
    public async Task Commit_NoLockFile_WritesCommitGraph()
    {
        string objectsInfoDir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(objectsInfoDir, "commit-graph")));
        // The lock file must not remain after a successful commit.
        Assert.False(File.Exists(Path.Combine(objectsInfoDir, "commit-graph.lock")));
    }

    // ── commit output file mode 0644 ───────────

    [Fact]
    public async Task Commit_WrittenFileHasMode0644()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX file modes do not apply on Windows.
        }

        // C creates the temp file with mode 0644 (commit_graph.c:1293), which
        // open(2) masks with the process umask. A same-process probe created
        // with UnixCreateMode 0644 shares the ambient umask, so the assertion
        // is umask-independent (0644 under 022, 0640 under 027, …) and never
        // touches the process-global umask — safe under MTP's parallel tests.
        string objectsInfoDir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(objectsInfoDir);

        string probePath = Path.Combine(objectsInfoDir, "probe");
        using (var fs = new FileStream(probePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        }))
        {
        }

        using var writer = new CommitGraphWriter(objectsInfoDir, new CommitGraphWriterOptions(GitHashAlgorithmKind.Sha1));
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        string path = Path.Combine(objectsInfoDir, "commit-graph");
        Assert.True(File.Exists(path));
        const int PermMask = 0x1FF;
        int expected = (int)File.GetUnixFileMode(probePath) & PermMask;
        int actual = (int)File.GetUnixFileMode(path) & PermMask;
        Assert.Equal(expected, actual);
    }
}

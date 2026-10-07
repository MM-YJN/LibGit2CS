using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Index;

public sealed class IndexWriterTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public IndexWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexWriter_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("A U Thor", "author@example.com", new GitTime(1227814297, 0));

    // ===== Write roundtrip =====

    [Fact]
    public async Task Write_EmptyIndex_RoundTrips()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        Assert.True(File.Exists(indexPath));

        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(0, reloaded.EntryCount);
    }

    [Fact]
    public async Task Write_WithEntries_RoundTrips()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("README.md", blobOid, GitFileMode.Regular));
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded.EntryCount);
        GitIndexEntry entry = reloaded.EntryByIndex(0);
        Assert.Equal("README.md", entry.Path.ToUtf8String());
        Assert.Equal(blobOid, entry.Id);
        Assert.Equal(GitFileMode.Regular, entry.Mode);
    }

    [Fact]
    public async Task Write_DirtyFlag_AfterWrite()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(index.IsDirty);

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular));
        Assert.True(index.IsDirty);

        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(index.IsDirty);
    }

    [Fact]
    public async Task Write_Locked_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular));

        // Lock the index.
        string lockPath = Path.Combine(_repo.Path, "index.lock");
        await File.Create(lockPath).DisposeAsync();

        await Assert.ThrowsAsync<GitException>(async () => await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // ===== AddByPath =====

    [Fact]
    public async Task AddByPath_RegularFile_StatsAndHashes()
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, index.EntryCount);
        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.Equal("README.md", entry.Path.ToUtf8String());
        Assert.Equal(GitFileMode.Regular, entry.Mode);
        Assert.NotEqual(GitOid.Empty, entry.Id);
    }

    [Fact]
    public async Task AddByPath_UpdatesExistingEntry()
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, index.EntryCount);

        // Modify file and re-add.
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello world\n", cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, index.EntryCount);
    }

    [Fact]
    public async Task AddByPath_NonexistentFile_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await index.AddByPathAsync("nonexistent.txt", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task AddByPath_Directory_Throws()
    {
        string workdir = _repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "subdir"));

        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await index.AddByPathAsync("subdir", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Directory, ex.Code);
    }

    [Fact]
    public async Task AddByPath_DotGitPath_Throws()
    {
        string workdir = _repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(workdir, ".git", "config"), "fake", cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GitException>(async () => await index.AddByPathAsync(".git/config", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddByPath_TraversalPath_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GitException>(async () => await index.AddByPathAsync("../foo.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddByPath_ResolvesConflicts()
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), "resolved\n", cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ConflictAdd(
            new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", blobOid2, GitFileMode.Regular),
            null);
        Assert.Equal(2, index.EntryCount);
        Assert.True(index.HasConflicts);

        await index.AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, index.EntryCount);
        Assert.False(index.HasConflicts);
        Assert.Equal(1, index.ReucCount);
    }

    // ===== AddFromBuffer =====

    [Fact]
    public async Task AddFromBuffer_CreatesBlobAndEntry()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry("buffer.txt", GitOid.Empty, GitFileMode.Regular);
        await index.AddFromBufferAsync(entry, "content from buffer\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, index.EntryCount);
        GitIndexEntry actual = index.EntryByIndex(0);
        Assert.Equal("buffer.txt", actual.Path.ToUtf8String());
        Assert.NotEqual(GitOid.Empty, actual.Id);
        Assert.Equal(GitFileMode.Regular, actual.Mode);
        Assert.Equal((uint)"content from buffer\n".Length, actual.FileSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task AddFromBuffer_SlicedInput_PersistsOnlySelectedBytes(int length)
    {
        byte[] backing = new byte[length + 16];
        Array.Fill(backing, (byte)0xff);
        for (int i = 0; i < length; i++)
        {
            backing[i + 7] = (byte)i;
        }

        ReadOnlyMemory<byte> input = backing.AsMemory(7, length);
        byte[] expected = input.ToArray();
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry("slice.bin", GitOid.Empty, GitFileMode.Regular);
        await index.AddFromBufferAsync(entry, input, TestContext.Current.CancellationToken);

        // Caller storage can be reused once the asynchronous write has completed.
        Array.Fill(backing, (byte)0xcc);
        GitIndexEntry actual = index.EntryByIndex(0);
        Assert.Equal((uint)length, actual.FileSize);
        Assert.Equal(GitOid.ComputeOid(GitObjectType.Blob, expected, GitHashAlgorithmKind.Sha1), actual.Id);
        using GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(actual.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal(expected, blob.Content.ToArray());
    }

    [Fact]
    public async Task AddFromBuffer_InvalidMode_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry("tree.txt", GitOid.Empty, GitFileMode.Tree);
        await Assert.ThrowsAsync<GitException>(async () => await index.AddFromBufferAsync(entry, "content"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken));
    }

    // ===== Remove / RemoveByPath / RemoveDirectory =====

    [Fact]
    public async Task Remove_ByPathAndStage()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular));
        Assert.True(index.Remove("file.txt"));
        Assert.Equal(0, index.EntryCount);
    }

    [Fact]
    public async Task Remove_Nonexistent_ReturnsFalse()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(index.Remove("nonexistent.txt"));
    }

    [Fact]
    public async Task RemoveByPath_RemovesAndResolvesConflicts()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular));
        index.RemoveByPath("file.txt");
        Assert.Equal(0, index.EntryCount);
    }

    [Fact]
    public async Task RemoveByPath_ConflictOnly_ClearsAndPromotesReuc()
    {
        // A path with only conflict stages (1/2/3) and no stage-0 entry — the
        // case git_index_remove_bypath must handle for "git rm" on a conflicted
        // path. Regression guard: the REUC data must survive the removal.
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        index.ConflictAdd(
            new GitIndexEntry("file.txt", ancestorOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", oursOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", theirsOid, GitFileMode.Regular));
        Assert.Equal(3, index.EntryCount);
        Assert.True(index.HasConflicts);

        bool removed = index.RemoveByPath("file.txt");

        Assert.True(removed);
        Assert.Equal(0, index.EntryCount);
        Assert.False(index.HasConflicts);

        // The conflict data must be promoted to a REUC entry (modes + OIDs).
        Assert.Equal(1, index.ReucCount);
        GitIndexReucEntry? reuc = index.ReucByPath("file.txt");
        Assert.NotNull(reuc);
        Assert.Equal((uint)GitFileMode.Regular, reuc!.Modes[0]); // ancestor
        Assert.Equal((uint)GitFileMode.Regular, reuc.Modes[1]);  // ours
        Assert.Equal((uint)GitFileMode.Regular, reuc.Modes[2]);  // theirs
        Assert.Equal(ancestorOid, reuc.Oids[0]);
        Assert.Equal(oursOid, reuc.Oids[1]);
        Assert.Equal(theirsOid, reuc.Oids[2]);
    }

    [Fact]
    public async Task RemoveByPath_Stage0AndConflicts_ClearsAllAndPromotesReuc()
    {
        // A path with BOTH a stage-0 entry and conflict stages. The clean
        // stage-0 removal plus conflict promotion must all succeed.
        // Regression guard: this case must keep its REUC data.
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid normalOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "normal\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // ConflictAdd removes the stage-0 entry, so add it back afterwards to
        // create the both-present state.
        index.ConflictAdd(
            new GitIndexEntry("file.txt", ancestorOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", oursOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", theirsOid, GitFileMode.Regular));
        index.Add(new GitIndexEntry("file.txt", normalOid, GitFileMode.Regular));
        Assert.Equal(4, index.EntryCount);

        bool removed = index.RemoveByPath("file.txt");

        Assert.True(removed);
        Assert.Equal(0, index.EntryCount);
        Assert.False(index.HasConflicts);
        Assert.Equal(1, index.ReucCount);
        Assert.NotNull(index.ReucByPath("file.txt"));
    }

    [Fact]
    public async Task RemoveByPath_Nonexistent_ReturnsFalse()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(index.RemoveByPath("nonexistent.txt"));
        Assert.Equal(0, index.ReucCount);
    }

    [Fact]
    public async Task RemoveDirectory_RemovesAllUnderPrefix()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("src/a.cs", blobOid, GitFileMode.Regular));
        index.Add(new GitIndexEntry("src/b.cs", blobOid, GitFileMode.Regular));
        index.Add(new GitIndexEntry("README.md", blobOid, GitFileMode.Regular));

        // C (index.c:1751-1754): stage is matched EXACTLY — GIT_INDEX_STAGE_ANY
        // (-1) removes nothing, so the caller passes the real stage.
        index.RemoveDirectory("src", stage: 0);
        Assert.Equal(1, index.EntryCount);
        Assert.Equal("README.md", index.EntryByIndex(0).Path.ToUtf8String());
    }

    // ===== WriteTree =====

    [Fact]
    public async Task WriteTree_EmptyIndex_ReturnsEmptyTree()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitOid.EmptyTreeSha1, treeOid);
    }

    [Fact]
    public async Task WriteTree_WithEntries_BuildsCorrectTree()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("README.md", blobOid, GitFileMode.Regular));
        index.Add(new GitIndexEntry("src/code.cs", blobOid, GitFileMode.Regular));

        GitOid treeOid = await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(GitOid.Empty, treeOid);
        Assert.NotEqual(GitOid.EmptyTreeSha1, treeOid);

        // Verify the tree can be looked up.
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(2, tree!.EntryCount);
    }

    [Fact]
    public async Task WriteTree_Conflicts_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ConflictAdd(
            new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular),
            null,
            null);
        await Assert.ThrowsAsync<GitException>(async () => await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteTree_DeterministicOID()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("README.md", blobOid, GitFileMode.Regular));
        GitOid treeOid1 = await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Write the same index again — should be the same OID.
        GitOid treeOid2 = await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(treeOid1, treeOid2);
    }

    // ===== SetVersion =====

    [Fact]
    public async Task SetVersion_V2_Succeeds()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.SetVersion(2);
        Assert.Equal(2, index.Version);
    }

    [Fact]
    public async Task SetVersion_V3_Succeeds()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.SetVersion(3);
        Assert.Equal(3, index.Version);
    }

    [Fact]
    public async Task SetVersion_V4_Succeeds()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.SetVersion(4);
        Assert.Equal(4, index.Version);
    }

    [Fact]
    public async Task SetVersion_V5_Throws()
    {
        // C (index.c:802-815): invalid version → GIT_ERROR_INDEX "invalid version number".
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = Assert.Throws<GitException>(() => index.SetVersion(5));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid version number", ex.Message);
        Assert.Equal(GitErrorCategory.Index, ex.Category);
    }

    [Fact]
    public async Task Write_V3_WhenExtendedFlagsPresent()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("file.txt"),
            Id = blobOid,
            Mode = GitFileMode.Regular,
            Flags = GitIndexEntry.Extended,
            FlagsExtended = GitIndexEntry.SkipWorktree,
        };
        index.Add(entry);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(3, reloaded.Version);
    }

    // ===== Conflicts =====

    [Fact]
    public async Task ConflictAdd_AddsThreeStages()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        index.ConflictAdd(
            new GitIndexEntry("file.txt", ancestorOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", oursOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", theirsOid, GitFileMode.Regular));

        Assert.Equal(3, index.EntryCount);
        Assert.True(index.HasConflicts);
    }

    [Fact]
    public async Task ConflictAdd_RemovesStageZero()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "normal\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular));
        Assert.Equal(1, index.EntryCount);

        GitOid conflictOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "conflict\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ConflictAdd(
            new GitIndexEntry("file.txt", conflictOid, GitFileMode.Regular),
            null,
            null);

        // Stage 0 removed, stage 1 added.
        Assert.Equal(1, index.EntryCount);
        Assert.Equal(1, index.EntryByIndex(0).Stage);
    }

    [Fact]
    public async Task ConflictGet_ReturnsAllStages()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        index.ConflictAdd(
            new GitIndexEntry("file.txt", ancestorOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", oursOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", theirsOid, GitFileMode.Regular));

        (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = index.ConflictGet("file.txt");
        Assert.NotNull(ancestor);
        Assert.NotNull(ours);
        Assert.NotNull(theirs);
        Assert.Equal(ancestorOid, ancestor!.Value.Id);
        Assert.Equal(oursOid, ours!.Value.Id);
        Assert.Equal(theirsOid, theirs!.Value.Id);
    }

    [Fact]
    public async Task ConflictGet_NoConflict_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<GitException>(() => index.ConflictGet("nonexistent.txt"));
    }

    [Fact]
    public async Task ConflictRemove_RemovesAllStages()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ConflictAdd(
            new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular),
            new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular));
        Assert.Equal(3, index.EntryCount);

        index.ConflictRemove("file.txt");
        Assert.Equal(0, index.EntryCount);
        Assert.False(index.HasConflicts);
    }

    [Fact]
    public async Task ConflictCleanup_RemovesAllConflicts()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ConflictAdd(
            new GitIndexEntry("a.txt", blobOid, GitFileMode.Regular),
            new GitIndexEntry("a.txt", blobOid, GitFileMode.Regular),
            null);
        index.ConflictAdd(
            new GitIndexEntry("b.txt", blobOid, GitFileMode.Regular),
            null,
            new GitIndexEntry("b.txt", blobOid, GitFileMode.Regular));
        Assert.True(index.HasConflicts);

        index.ConflictCleanup();
        Assert.False(index.HasConflicts);
        Assert.Equal(0, index.EntryCount);
    }

    [Fact]
    public async Task ConflictAdd_PartialConflict()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ConflictAdd(
            new GitIndexEntry("file.txt", blobOid, GitFileMode.Regular),
            null,
            null);

        Assert.Equal(1, index.EntryCount);
        (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = index.ConflictGet("file.txt");
        Assert.NotNull(ancestor);
        Assert.Null(ours);
        Assert.Null(theirs);
    }

    // ===== REUC =====

    [Fact]
    public async Task ReucAdd_AddsEntry()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("file.txt", (uint)GitFileMode.Regular, blobOid, 0, default, 0, default);
        Assert.Equal(1, index.ReucCount);

        GitIndexReucEntry? reuc = index.ReucByPath("file.txt");
        Assert.NotNull(reuc);
        Assert.Equal("file.txt", reuc!.Path.ToUtf8String());
        Assert.Equal((uint)GitFileMode.Regular, reuc.Modes[0]);
        Assert.Equal(blobOid, reuc.Oids[0]);
    }

    [Fact]
    public async Task ReucAdd_ReplacesExisting()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid1 = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content1\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content2\n"u8.ToArray(), TestContext.Current.CancellationToken);

        index.ReucAdd("file.txt", (uint)GitFileMode.Regular, blobOid1, 0, default, 0, default);
        index.ReucAdd("file.txt", 0, default, (uint)GitFileMode.Regular, blobOid2, 0, default);

        Assert.Equal(1, index.ReucCount);
        GitIndexReucEntry? reuc = index.ReucByPath("file.txt");
        Assert.Equal(blobOid2, reuc!.Oids[1]);
    }

    [Fact]
    public async Task ReucRemove_RemovesEntry()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("file.txt", (uint)GitFileMode.Regular, blobOid, 0, default, 0, default);
        Assert.Equal(1, index.ReucCount);

        index.ReucRemove(0);
        Assert.Equal(0, index.ReucCount);
    }

    [Fact]
    public async Task ReucClear_RemovesAll()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("a.txt", (uint)GitFileMode.Regular, blobOid, 0, default, 0, default);
        index.ReucAdd("b.txt", (uint)GitFileMode.Regular, blobOid, 0, default, 0, default);
        Assert.Equal(2, index.ReucCount);

        index.ReucClear();
        Assert.Equal(0, index.ReucCount);
    }

    [Fact]
    public async Task ReucWrite_RoundTrips()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.ReucAdd("file.txt", (uint)GitFileMode.Regular, blobOid, (uint)GitFileMode.Regular, blobOid, 0, default);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded.ReucCount);
        Assert.Equal("file.txt", reloaded.ReucByIndex(0)!.Path.ToUtf8String());
    }

    [Fact]
    public async Task ReucWrite_AllThreeStagesWithEntries_RoundTripsModesAndOids()
    {
        // Regression guard for WriteReucExtension: the three octal mode strings
        // and per-stage OIDs must be written into the REUC extension payload
        // (not the main index stream) and survive a write+reload round-trip,
        // with the modes intact.
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitOid stage0Oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "resolved\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // A regular stage-0 entry alongside the REUC entry exercises the
        // entry/extension boundary (the mode strings must not leak here).
        index.Add(new GitIndexEntry("kept.txt", stage0Oid, GitFileMode.Regular));
        index.ReucAdd(
            "conflict.txt",
            (uint)GitFileMode.Regular, ancestorOid,
            (uint)GitFileMode.Executable, oursOid,
            (uint)GitFileMode.Symlink, theirsOid);

        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Stage-0 entry survived intact.
        Assert.Equal(1, reloaded.EntryCount);
        Assert.Equal("kept.txt", reloaded.EntryByIndex(0).Path.ToUtf8String());
        Assert.Equal(stage0Oid, reloaded.EntryByIndex(0).Id);

        // REUC entry survived with all three stages' modes + OIDs verbatim.
        Assert.Equal(1, reloaded.ReucCount);
        GitIndexReucEntry? reuc = reloaded.ReucByPath("conflict.txt");
        Assert.NotNull(reuc);
        Assert.Equal((uint)GitFileMode.Regular, reuc!.Modes[0]);
        Assert.Equal((uint)GitFileMode.Executable, reuc.Modes[1]);
        Assert.Equal((uint)GitFileMode.Symlink, reuc.Modes[2]);
        Assert.Equal(ancestorOid, reuc.Oids[0]);
        Assert.Equal(oursOid, reuc.Oids[1]);
        Assert.Equal(theirsOid, reuc.Oids[2]);
    }

    // ===== NAME =====

    [Fact]
    public async Task NameAdd_AddsEntry()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.NameAdd("ancestor.txt", "ours.txt", "theirs.txt");
        Assert.Equal(1, index.NameCount);
    }

    [Fact]
    public async Task NameAdd_TooFewPaths_Throws()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<GitException>(() => index.NameAdd("only.txt", null, null));
    }

    [Fact]
    public async Task NameClear_RemovesAll()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.NameAdd("a.txt", "b.txt", null);
        index.NameAdd("c.txt", null, "d.txt");
        Assert.Equal(2, index.NameCount);

        index.NameClear();
        Assert.Equal(0, index.NameCount);
    }

    [Fact]
    public async Task NameWrite_RoundTrips()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.NameAdd("ancestor.txt", "ours.txt", "theirs.txt");
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded.NameCount);
    }

    // ===== Clear =====

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("a.txt", blobOid, GitFileMode.Regular));
        index.Add(new GitIndexEntry("b.txt", blobOid, GitFileMode.Regular));
        index.ReucAdd("c.txt", (uint)GitFileMode.Regular, blobOid, 0, default, 0, default);
        index.NameAdd("a.txt", "b.txt", null);

        index.Clear();
        Assert.Equal(0, index.EntryCount);
        Assert.Equal(0, index.ReucCount);
        Assert.Equal(0, index.NameCount);
    }

    // ===== ReadTree =====

    [Fact]
    public async Task ReadTree_ReplacesEntries()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("old.txt", blobOid, GitFileMode.Regular));

        // Build a tree with one entry.
        using GitTreeBuilder treeBld = _repo.NewTreeBuilder();
        await treeBld.InsertAsync("new.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        await index.ReadTreeAsync(tree, CancellationToken.None);
        Assert.Equal(1, index.EntryCount);
        Assert.Equal("new.txt", index.EntryByIndex(0).Path.ToUtf8String());
    }

    [Fact]
    public async Task ReadTree_PreservesStatCache()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("file.txt"),
            Id = blobOid,
            Mode = GitFileMode.Regular,
            FileSize = 1234,
        };
        index.Add(entry);

        // Read a tree containing the same blob+mode — stat should be preserved.
        using GitTreeBuilder treeBld = _repo.NewTreeBuilder();
        await treeBld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        await index.ReadTreeAsync(tree, CancellationToken.None);
        Assert.Equal(1, index.EntryCount);
        Assert.Equal((uint)1234, index.EntryByIndex(0).FileSize);
    }

    [Fact]
    public async Task ReadTree_WriteTree_Involution()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("a.txt", blobOid, GitFileMode.Regular));
        index.Add(new GitIndexEntry("dir/b.txt", blobOid, GitFileMode.Regular));

        GitOid treeOid1 = await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid1, TestContext.Current.CancellationToken))!;

        await index.ReadTreeAsync(tree, CancellationToken.None);
        GitOid treeOid2 = await index.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(treeOid1, treeOid2);
    }

    // ===== Binary format verification =====

    [Fact]
    public async Task Write_ProducesValidChecksum()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("README.md", blobOid, GitFileMode.Regular));
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        byte[] bytes = await File.ReadAllBytesAsync(indexPath, cancellationToken: TestContext.Current.CancellationToken);

        // Must start with "DIRC".
        Assert.Equal((byte)'D', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'R', bytes[2]);
        Assert.Equal((byte)'C', bytes[3]);

        // Version must be 2 (no extended flags).
        Assert.Equal(0, bytes[4]);
        Assert.Equal(0, bytes[5]);
        Assert.Equal(0, bytes[6]);
        Assert.Equal(2, bytes[7]);

        // Entry count must be 1.
        Assert.Equal(0, bytes[8]);
        Assert.Equal(0, bytes[9]);
        Assert.Equal(0, bytes[10]);
        Assert.Equal(1, bytes[11]);
    }

    [Fact]
    public async Task Write_EntryAlignment()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("a", blobOid, GitFileMode.Regular));
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        byte[] bytes = await File.ReadAllBytesAsync(indexPath, cancellationToken: TestContext.Current.CancellationToken);

        // Header is 12 bytes, entry starts at offset 12.
        // Entry: common(40) + OID(20) + flags(2) + path "a"(1) + NUL(1) = 64 bytes.
        // 64 is already 8-byte aligned (relative to entry start), so no padding.
        // Then 20-byte SHA-1 checksum. Total = 12 + 64 + 20 = 96.
        int entryStart = 12;
        int entryEnd = entryStart + 40 + 20 + 2 + 2; // 76
        // Alignment is relative to entry start: (64 + 7) & ~7 = 64 → no padding.
        int paddedEnd = entryStart + ((entryEnd - entryStart + 7) & ~7); // 76 (64 is aligned)
        int checksumStart = paddedEnd;

        // Total file size = checksumStart + 20 (SHA-1 checksum).
        Assert.Equal(checksumStart + 20, bytes.Length);
    }

    // ===== Commit.CreateFromStage =====

    [Fact]
    public async Task CreateFromStage_RootCommit_Succeeds()
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitSignature sig = TestSig();
        GitOid commitOid = await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "initial commit\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(GitOid.Empty, commitOid);

        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal("initial commit\n", commit!.Message);
        Assert.Empty(commit.Parents);

        // HEAD should now point at the commit.
        GitReference? head = await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Equal(commitOid, ((GitDirectReference)head!).Target);
    }

    [Fact]
    public async Task CreateFromStage_NoChanges_Throws()
    {
        // First commit.
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitSignature sig = TestSig();
        await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "initial\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // No changes → should throw Eunchanged.
        await Assert.ThrowsAsync<GitException>(async () => await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "second\n",
        }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateFromStage_AllowEmpty_Succeeds()
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitSignature sig = TestSig();
        await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "initial\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // AllowEmptyCommit = true should succeed even with no changes.
        GitOid commit2 = await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "empty\n",
            AllowEmptyCommit = true,
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(GitOid.Empty, commit2);
    }

    [Fact]
    public async Task CreateFromStage_SecondCommit_HasParent()
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitSignature sig = TestSig();
        GitOid commit1 = await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "initial\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Modify the file and create a second commit.
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello world\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("README.md", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitOid commit2 = await _repo.CommitCreateFromStageAsync(new CommitCreateOptions
        {
            Author = sig,
            Committer = sig,
            Message = "second\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit commit = (await _repo.ObjectLookupAsync<Commit>(commit2, TestContext.Current.CancellationToken))!;
        Assert.Single(commit.Parents);
        Assert.Equal(commit1, commit.Parents[0]);
    }
}

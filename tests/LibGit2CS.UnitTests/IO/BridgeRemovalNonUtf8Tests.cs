using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;
using LibGit2CS.Submodule;
using LibGit2CS.Utils;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Regression test. After removing the temporary implicit <c>GitPath &lt;-&gt; string</c> bridge, this test pins that every public
/// API surface that accepts <see cref="GitPath"/> round-trips a non-UTF-8 path byte-exact. The <c>string</c> overloads encode via <see
/// cref="GitPath.FromUtf8String"/> (lossy for non-UTF-8 — expected); the <see cref="GitPath"/> overloads must preserve raw bytes end-to-end. </summary>
public sealed class BridgeRemovalNonUtf8Tests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // 0xFF 0xFE 0x80 — invalid UTF-8 (0xFF is never a valid leading byte).
    // UTF-8.GetString decodes to U+FFFD sequences that cannot round-trip.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];
    private static readonly GitPath s_nonUtf8Path = GitPath.FromUtf8Bytes(s_nonUtf8);

    public BridgeRemovalNonUtf8Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_BridgeRemoval_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, _context);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ===== GitIndex public GitPath overloads =====

    [Fact]
    public async Task Index_AddByPathAsync_GitPath_RoundTripsNonUtf8()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        string fullPath = Path.Combine(_repo.Workdir!, s_nonUtf8Path.ToFileSystemString());
        await File.WriteAllBytesAsync(fullPath, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        await index.AddByPathAsync(s_nonUtf8Path, cancellationToken: TestContext.Current.CancellationToken);

        GitIndexEntry entry = index.Entries.First();
        Assert.Equal(s_nonUtf8Path, entry.Path);
        Assert.Equal(s_nonUtf8, entry.Path.Span.ToArray());
    }

    [Fact]
    public async Task Index_EntryByPath_GitPath_FindsNonUtf8()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "y"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry(s_nonUtf8Path, oid, GitFileMode.Regular));

        GitIndexEntry? found = index.EntryByPath(s_nonUtf8Path);
        Assert.NotNull(found);
        Assert.Equal(s_nonUtf8Path, found!.Value.Path);
    }

    [Fact]
    public async Task Index_Find_GitPath_LocatesNonUtf8()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "y"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry(s_nonUtf8Path, oid, GitFileMode.Regular));

        int pos = index.Find(s_nonUtf8Path);
        Assert.True(pos >= 0);
    }

    [Fact]
    public async Task Index_Remove_GitPath_RemovesNonUtf8()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "y"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry(s_nonUtf8Path, oid, GitFileMode.Regular));

        Assert.True(index.Remove(s_nonUtf8Path));
        Assert.Equal(0, index.EntryCount);
    }

    // ===== StatusFileAsync(GitPath) =====

    [Fact]
    public async Task StatusFileAsync_GitPath_ReportsNonUtf8()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        string fullPath = Path.Combine(_repo.Workdir!, s_nonUtf8Path.ToFileSystemString());
        await File.WriteAllBytesAsync(fullPath, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        await index.AddByPathAsync(s_nonUtf8Path, cancellationToken: TestContext.Current.CancellationToken);

        GitStatusFlags flags = await _repo.StatusFileAsync(s_nonUtf8Path, cancellationToken: TestContext.Current.CancellationToken);
        // The staged entry is found byte-exact (IndexNew set). The workdir
        // side depends on the OS filesystem's handling of non-UTF-8 names.
        Assert.True((flags & GitStatusFlags.IndexNew) != 0);
    }

    // ===== GitDiffDelta.Path is now GitPath =====

    [Fact]
    public async Task Delta_Path_IsGitPath_AndRoundTripsNonUtf8()
    {
        _ = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "z"u8.ToArray(), TestContext.Current.CancellationToken);

        // Build two trees: one without the entry, one with the non-UTF-8 entry.
        GitTreeBuilder bld1 = _repo.NewTreeBuilder();
        GitOid tree1 = await bld1.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        bld1.Dispose();

        GitTreeBuilder bld2 = _repo.NewTreeBuilder();
        await bld2.InsertAsync(s_nonUtf8Path, oid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid tree2 = await bld2.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        bld2.Dispose();

        GitTree t1 = (await _repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken))!;
        GitTree t2 = (await _repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken))!;

        using GitDiff diff = await GitDiff.TreeToTreeAsync(_repo, t1, t2, cancellationToken: TestContext.Current.CancellationToken);
        GitDiffDelta delta = diff.GetDelta(0);

        // GitDiffDelta.Path is now GitPath — the byte-faithful view.
        Assert.Equal(s_nonUtf8Path, delta.Path);
        Assert.Equal(s_nonUtf8, delta.Path.Span.ToArray());

        // PathString convenience decodes for display.
        string decoded = delta.PathString;
        Assert.NotEqual(s_nonUtf8Path.ToUtf8String(), string.Empty);
    }

    // ===== GitDiffFileStat.Path is now GitPath =====

    [Fact]
    public async Task DiffFileStat_Path_IsGitPath()
    {
        _ = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "abc\n"u8.ToArray(), TestContext.Current.CancellationToken);

        GitTreeBuilder bld1 = _repo.NewTreeBuilder();
        GitOid tree1 = await bld1.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        bld1.Dispose();

        GitTreeBuilder bld2 = _repo.NewTreeBuilder();
        await bld2.InsertAsync(s_nonUtf8Path, oid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid tree2 = await bld2.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        bld2.Dispose();

        GitTree t1 = (await _repo.ObjectLookupAsync<GitTree>(tree1, TestContext.Current.CancellationToken))!;
        GitTree t2 = (await _repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken))!;

        using GitDiff diff = await GitDiff.TreeToTreeAsync(_repo, t1, t2, cancellationToken: TestContext.Current.CancellationToken);
        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Stats format emits the raw non-UTF-8 path bytes verbatim (the
        // Format API now writes bytes, not a lossy string). Verify the path
        // bytes survive end-to-end.
        using var statsWriter = new PooledByteBufferWriter();
        stats.Format(statsWriter, GitDiffStatsFormat.Number);
        byte[] statsBytes = statsWriter.WrittenSpan.ToArray();
        Assert.True(statsBytes.Length > 0);
        Assert.True(statsBytes.AsSpan().IndexOf(s_nonUtf8) >= 0,
            $"non-UTF-8 path bytes not found in stats output: {Convert.ToHexString(statsBytes)}");
    }

    // ===== GitTreeUpdate.Path is now GitPath =====

    [Fact]
    public async Task TreeUpdate_Path_IsGitPath_CtorOverload()
    {
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "t"u8.ToArray(), TestContext.Current.CancellationToken);

        // GitPath ctor overload.
        var update = new GitTreeUpdate(GitTreeUpdateAction.Upsert, s_nonUtf8Path, oid, GitFileMode.Regular);
        Assert.Equal(s_nonUtf8Path, update.Path);

        GitOid treeOid = await _repo.TreeCreateUpdatedAsync(null, [update], cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        Assert.NotNull(tree.EntryByName(s_nonUtf8Path));
    }

    // ===== GitFilterSource.Path is now GitPath? =====

    [Fact]
    public async Task FilterSource_Path_IsGitPathNullable()
    {
        var source = new GitFilterSource(_repo, s_nonUtf8Path, default, default, GitFilterMode.ToWorktree, default);
        Assert.Equal(s_nonUtf8Path, source.Path);
        Assert.Equal(s_nonUtf8Path.ToUtf8String(), source.SourcePath);
    }

    // ===== GitSubmodule.Path is now GitPath? =====

    [Fact]
    public void Submodule_Path_IsGitPathNullable()
    {
        // The Path property type is GitPath? (Name stays string).
        GitSubmodule sm = new(_repo, "name");
        Assert.Equal(GitPath.FromUtf8String("name"), sm.Path);
        // PathString convenience.
        Assert.Equal("name", sm.PathString);
    }

    // ===== GitMergeFileInput.Path is now GitPath? =====

    [Fact]
    public void MergeFileInput_Path_IsGitPathNullable()
    {
        var input = new GitMergeFileInput(s_nonUtf8Path, (uint)GitFileMode.Regular, "x"u8.ToArray());
        Assert.Equal(s_nonUtf8Path, input.Path);

        // string Create factory.
        var input2 = GitMergeFileInput.Create("foo.txt", 0, "bar");
        Assert.Equal(GitPath.FromUtf8String("foo.txt"), input2.Path);
    }

    // ===== GitMergeFileResult.Path is now GitPath? =====

    [Fact]
    public void MergeFileResult_Path_IsGitPathNullable()
    {
        var result = new GitMergeFileResult { Path = s_nonUtf8Path };
        Assert.Equal(s_nonUtf8Path, result.Path);
    }

    // ===== Options structs: PathSpecs is now GitPath[]? =====

    [Fact]
    public void DiffOptions_PathSpecs_IsGitPathArray()
    {
        var opts = new GitDiffOptions { PathSpecs = [s_nonUtf8Path] };
        Assert.NotNull(opts.PathSpecs);
        Assert.Single(opts.PathSpecs);
        Assert.Equal(s_nonUtf8Path, opts.PathSpecs![0]);

        // PathSpecStrings convenience.
        var opts2 = new GitDiffOptions { PathSpecStrings = ["foo.txt"] };
        Assert.NotNull(opts2.PathSpecs);
        Assert.Equal(GitPath.FromUtf8String("foo.txt"), opts2.PathSpecs![0]);
    }

    [Fact]
    public void StatusOptions_PathSpecs_IsGitPathArray()
    {
        var opts = new GitStatusOptions { PathSpecs = [s_nonUtf8Path] };
        Assert.Equal(s_nonUtf8Path, opts.PathSpecs![0]);
    }

    [Fact]
    public void CheckoutOptions_Paths_IsGitPathArray()
    {
        var opts = new GitCheckoutOptions { Paths = [s_nonUtf8Path] };
        Assert.Equal(s_nonUtf8Path, opts.Paths![0]);

        // PathsStrings convenience.
        var opts2 = new GitCheckoutOptions { PathsStrings = ["foo.txt"] };
        Assert.Equal(GitPath.FromUtf8String("foo.txt"), opts2.Paths![0]);
    }

    // ===== GitDiffNotificationCallback takes GitPath? =====

    [Fact]
    public async Task DiffNotificationCallback_TakesGitPath()
    {
        GitPath? received = null;
        var opts = new GitDiffOptions
        {
            Notify = (_, matched) =>
            {
                received = matched;
                return 0;
            },
            PathSpecs = [s_nonUtf8Path],
        };

        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync(s_nonUtf8Path, oid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        bld.Dispose();

        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        _ = await GitDiff.TreeToTreeAsync(_repo, null, tree, opts, cancellationToken: TestContext.Current.CancellationToken);

        // The notify callback should have received the non-UTF-8 pathspec match.
        Assert.NotNull(received);
        Assert.Equal(s_nonUtf8Path, received);
    }

    // ===== Tree.EntryByPathAsync(GitPath) is public =====

    [Fact]
    public async Task Tree_EntryByPathAsync_GitPath_PublicOverload()
    {
        GitOid oid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "e"u8.ToArray(), TestContext.Current.CancellationToken);
        GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync(s_nonUtf8Path, oid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        bld.Dispose();

        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        GitTreeEntry? entry = await tree.EntryByPathAsync(s_nonUtf8Path, TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(s_nonUtf8Path, entry!.Value.Name);
    }
}

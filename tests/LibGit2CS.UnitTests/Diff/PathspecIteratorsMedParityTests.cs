using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Parity regression tests for the pathspec/iterators/fs behaviors in
/// libgit2 1.9.4.
/// Expectations are C-verified against libgit2 (pathspec.c, iterator.c,
/// fs_path.c, str.c, filebuf.c, index.c — 1.9.4).
/// </summary>
public sealed class PathspecIteratorsMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public PathspecIteratorsMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PathspecIteratorsMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitOid Blob(GitRepository repo, string content)
        => repo.ObjectWriteAsync(
            GitObjectType.Blob,
            Encoding.UTF8.GetBytes(content),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static List<string> DiffPaths(GitDiff diff)
        => diff.Deltas.Select(d => d.NewFile.Path?.ToUtf8String() ?? string.Empty).ToList();

    // ── negative pathspec must exclude, not include ────────────────

    [Fact]
    public async Task NegativePathspec_ExcludesNegativeMatchedPath()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("foo", Blob(repo, "foo"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("bar", Blob(repo, "bar"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("baz", Blob(repo, "baz"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // C (pathspec.c:196-230): git_pathspec__match returns result > 0 — a
        // negative match (result == 0) is NOT a match. With ["!foo"], "foo" is
        // negatively matched and "bar"/"baz" are outside the prefix-bounded
        // walk — the diff must be empty.
        using GitDiff diff = await repo.DiffTreeToTreeAsync(null, tree, new GitDiffOptions
        {
            PathSpecStrings = ["!foo"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(DiffPaths(diff));

        // Positive control: ["bar"] yields exactly the bar delta.
        using GitDiff diff2 = await repo.DiffTreeToTreeAsync(null, tree, new GitDiffOptions
        {
            PathSpecStrings = ["bar"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["bar"], DiffPaths(diff2));
    }

    [Fact]
    public async Task NegativePathspec_MixedPatterns_ExcludesNegativeMatched()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a.txt", Blob(repo, "a"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("b.txt", Blob(repo, "b"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("secret.txt", Blob(repo, "s"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // C (pathspec.c:196-230): git_pathspec__match_at breaks on the FIRST
        // pattern with result >= 0 — pattern ORDER matters. With the negative
        // pattern first, secret.txt is negatively matched (result == 0) and
        // excluded even though "*.txt" would also match it.
        using GitDiff diff = await repo.DiffTreeToTreeAsync(null, tree, new GitDiffOptions
        {
            PathSpecStrings = ["!secret.txt", "*.txt"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a.txt", "b.txt"], DiffPaths(diff));
    }

    // ── tree iterator icase sort/dedup ──────────────────────────────

    [Fact]
    public async Task TreeIterator_IcaseSort_UsesDirAsSlashRule()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        // A/ contains tree x/ (which contains blob y); a/ contains blob x.
        GitOid y = Blob(repo, "y");
        GitOid bx = Blob(repo, "x");
        using GitTreeBuilder xb = repo.NewTreeBuilder();
        await xb.InsertAsync("y", y, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid xt = await xb.WriteAsync(CancellationToken.None); // tree x = { y }
        using GitTreeBuilder bldA = repo.NewTreeBuilder();
        await bldA.InsertAsync("x", xt, GitFileMode.Tree, TestContext.Current.CancellationToken);        // tree A = { tree x }
        GitOid oidTreeA = await bldA.WriteAsync(CancellationToken.None);
        using GitTreeBuilder blda = repo.NewTreeBuilder();
        await blda.InsertAsync("x", bx, GitFileMode.Regular, TestContext.Current.CancellationToken);     // tree a = { blob x }
        GitOid oidTreea = await blda.WriteAsync(CancellationToken.None);
        using GitTreeBuilder root = repo.NewTreeBuilder();
        await root.InsertAsync("A", oidTreeA, GitFileMode.Tree, TestContext.Current.CancellationToken);
        await root.InsertAsync("a", oidTreea, GitFileMode.Tree, TestContext.Current.CancellationToken);
        GitOid rootOid = await root.WriteAsync(CancellationToken.None);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(rootOid, TestContext.Current.CancellationToken))!;

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IgnoreCase,
        });

        var paths = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            paths.Add(entry.Path.ToUtf8String());
        }

        // C (iterator.c:479-515): git_fs_path_cmp appends a virtual '/' to
        // tree names, so the same-named blob sorts BEFORE the tree — even
        // when the blob lives under the case-larger parent directory.
        Assert.Equal(["a/x", "A/x/y"], paths);
    }

    [Fact]
    public async Task TreeIterator_IcaseDedup_CoalescesCaseCollisions()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        // A/ contains blob a; a/ contains blob A — coalesced siblings whose
        // children collide case-insensitively (A/a vs a/A).
        GitOid a = Blob(repo, "a");
        GitOid bigA = Blob(repo, "A");
        using GitTreeBuilder bldA = repo.NewTreeBuilder();
        await bldA.InsertAsync("a", a, GitFileMode.Regular, TestContext.Current.CancellationToken);         // tree A = { blob a }
        GitOid oidTreeA = await bldA.WriteAsync(CancellationToken.None);
        using GitTreeBuilder blda = repo.NewTreeBuilder();
        await blda.InsertAsync("A", bigA, GitFileMode.Regular, TestContext.Current.CancellationToken);      // tree a = { blob A }
        GitOid oidTreea = await blda.WriteAsync(CancellationToken.None);
        using GitTreeBuilder root = repo.NewTreeBuilder();
        await root.InsertAsync("A", oidTreeA, GitFileMode.Tree, TestContext.Current.CancellationToken);
        await root.InsertAsync("a", oidTreea, GitFileMode.Tree, TestContext.Current.CancellationToken);
        GitOid rootOid = await root.WriteAsync(CancellationToken.None);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(rootOid, TestContext.Current.CancellationToken))!;

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IgnoreCase,
        });

        var paths = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            paths.Add(entry.Path.ToUtf8String());
        }

        // C (iterator.c:792-795): dedup uses tree_iterator_entry_cmp_icase
        // (name + dirness only, no parent tiebreak) — A/a and a/A collapse
        // to a single entry.
        Assert.Equal(["A/a"], paths);
    }

    // ── advance_into on a non-directory is a no-op ──────────────────

    [Fact]
    public async Task TreeIterator_AdvanceInto_OnBlob_IsNoOp()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("bar", Blob(repo, "b"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("foo", Blob(repo, "f"), GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.DontAutoexpand,
        });

        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("bar", first!.Value.Path.ToUtf8String());

        // C (iterator.c:871-873): non-tree current entry → returns 0 with
        // *out = NULL, current unchanged.
        Assert.Null(await iter.AdvanceIntoAsync(TestContext.Current.CancellationToken));
        GitIndexEntry? cur = await iter.CurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal("bar", cur!.Value.Path.ToUtf8String());

        GitIndexEntry? next = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("foo", next!.Value.Path.ToUtf8String());
    }

    [Fact]
    public async Task FilesystemIterator_AdvanceInto_OnFile_IsNoOp()
    {
        string dir = NewDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "b", TestContext.Current.CancellationToken);

        using IIterator iter = await FilesystemIterator.ForFilesystemAsync(dir, new IteratorOptions
        {
            Flags = IteratorFlags.DontAutoexpand,
        }, TestContext.Current.CancellationToken);

        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a.txt", first!.Value.Path.ToUtf8String());

        // C (iterator.c:1678-1681): non-dir current entry → NULL, unchanged.
        Assert.Null(await iter.AdvanceIntoAsync(TestContext.Current.CancellationToken));
        GitIndexEntry? cur = await iter.CurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a.txt", cur!.Value.Path.ToUtf8String());
    }

    [Fact]
    public async Task IndexIterator_AdvanceInto_OnFile_IsNoOp()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("a.txt", Blob(repo, "a"), GitFileMode.Regular));
        index.Add(new GitIndexEntry("b.txt", Blob(repo, "b"), GitFileMode.Regular));

        using var iter = new IndexIterator(index, repo);

        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a.txt", first!.Value.Path.ToUtf8String());

        // C (iterator.c:2208-2213): non-tree current entry → NULL, unchanged.
        Assert.Null(await iter.AdvanceIntoAsync(TestContext.Current.CancellationToken));
        GitIndexEntry? cur = await iter.CurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a.txt", cur!.Value.Path.ToUtf8String());
    }

    // ── workdir advance_over on dir with only empty subdirs ─────────

    [Fact]
    public async Task WorkdirAdvanceOver_DirWithOnlyEmptySubdir_ReportsEmpty()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(repoPath, "d", "empty"));
        await File.WriteAllTextAsync(Path.Combine(repoPath, "e.txt"), "e", TestContext.Current.CancellationToken);

        using IIterator iter = await FilesystemIterator.ForWorkdirAsync(repo, index: null, tree: null, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees,
        }, TestContext.Current.CancellationToken);

        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("d/", first!.Value.Path.ToUtf8String());

        // C (iterator.c:1826-1856): the scan loop re-checks
        // !prefixcomp(entry->path, base) at the top; when advance_into exits
        // the base directory, the status stays EMPTY.
        (GitIndexEntry? entry, IteratorStatus status) = await iter.AdvanceOverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IteratorStatus.Empty, status);
        Assert.Equal("e.txt", entry!.Value.Path.ToUtf8String());
    }

    // ── CompareIgnoreCase family folds ASCII only ───────────────────

    [Fact]
    public void CompareIgnoreCase_Bounded_NonAsciiBytes_DoNotFold()
    {
        // C git__tolower folds only A-Z (util.c:162-167); bytes >= 0x80 pass
        // through. .NET's char.ToLowerInvariant maps 0xC0 (À) to 0xE0 (à),
        // which would make these two single bytes compare equal.
        var c0 = GitPath.FromUtf8Bytes(new byte[] { 0xC0 });
        var e0 = GitPath.FromUtf8Bytes(new byte[] { 0xE0 });

        Assert.NotEqual(0, GitPath.CompareIgnoreCase(c0, e0, 1));
        Assert.NotEqual(0, GitPath.CompareIgnoreCase(c0, e0));
    }

    [Fact]
    public void CompareCaseSort_NonAsciiBytes_DoNotFold()
    {
        // C: tolower(0xC0)=0xC0 < tolower(0xDF)=0xDF → negative. Unicode
        // folding maps 0xC0→0xE0 which flips the sign (0xE0 > 0xDF).
        var c0 = GitPath.FromUtf8Bytes(new byte[] { 0xC0 });
        var df = GitPath.FromUtf8Bytes(new byte[] { 0xDF });
        Assert.True(GitPath.CompareCaseSort(c0, df) < 0);
    }

    [Fact]
    public void ComparePrefixIgnoreCase_Bounded_NonAsciiBytes_DoNotFold()
    {
        var c0 = GitPath.FromUtf8Bytes(new byte[] { 0xC0 });
        var e0 = GitPath.FromUtf8Bytes(new byte[] { 0xE0 });
        Assert.NotEqual(0, GitPath.ComparePrefixIgnoreCase(c0, strLen: 1, e0));
    }

    // ── index iterator advance_over before first access ─────────────

    [Fact]
    public async Task IndexIterator_AdvanceOver_BeforeFirstAccess_SkipsPseudotree()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("dir/file.txt", Blob(repo, "f"), GitFileMode.Regular));
        index.Add(new GitIndexEntry("other.txt", Blob(repo, "o"), GitFileMode.Regular));

        using var iter = new IndexIterator(index, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees,
        });

        // C (iterator.c:2228-2235): index_iterator_current first triggers the
        // initial advance; a first-entry pseudo-tree is skipped before the
        // advance. So the first advance_over yields "other.txt", not "dir/".
        (GitIndexEntry? entry, IteratorStatus status) = await iter.AdvanceOverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IteratorStatus.Normal, status);
        Assert.Equal("other.txt", entry!.Value.Path.ToUtf8String());
    }

    // ── index iterator common-dir-len (file/dir conflict) ───────────

    [Fact]
    public async Task IndexIterator_FileDirConflict_EmitsPseudotree()
    {
        // git_index_add resolves file/dir collisions (index.c has_dir_name),
        // so a file "abc" + file "abc/def" index can only come from disk.
        // Hand-craft a v2 index (all-zero stat fields, zero checksum accepted
        // as skipHash) containing both entries in sorted order.
        string indexPath = Path.Combine(NewDir(), "index");
        await WriteRawIndexAsync(indexPath, ["abc", "abc/def"], TestContext.Current.CancellationToken);

        GitIndex index = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        using var iter = new IndexIterator(index, null, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees,
        });

        var paths = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            paths.Add(entry.Path.ToUtf8String());
        }

        // C (fs_path.c:932-944): git_fs_path_common_dirlen("abc","abc/def") == 0
        // (no shared '/'), so the iterator synthesizes the "abc/" pseudo-tree
        // entry for the file/dir conflict.
        Assert.Equal(["abc", "abc/", "abc/def"], paths);
    }

    /// <summary>
    /// Writes a minimal v2 index file containing one blob entry per path
    /// (all-zero stat/oid fields, zero checksum = skipHash).
    /// </summary>
    private static async Task WriteRawIndexAsync(string indexPath, string[] paths, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write("DIRC"u8);
            WriteBe(bw, 2); // version
            WriteBe(bw, paths.Length);

            foreach (string path in paths)
            {
                bw.Write(new byte[40]); // ctime(8) + mtime(8) + dev/ino/mode/uid/gid/size (32)
                // Fix the mode field (bytes 24..28 of the stat block): 0100644.
                long statStart = ms.Position - 40;
                WriteBeAt(ms, statStart + 24, 0x81A4u);

                bw.Write(new byte[20]); // zero OID
                WriteBe(bw, (ushort)path.Length); // flags: name length
                bw.Write(Encoding.UTF8.GetBytes(path));

                // Pad the entry to a multiple of 8.
                long entryLen = ms.Position - statStart;
                int pad = (int)((8 - (entryLen % 8)) % 8);
                bw.Write(new byte[pad]);
            }
        }

        // Zero checksum (skipHash) is accepted by the parser.
        byte[] body = ms.ToArray();
        byte[] file = new byte[body.Length + 20];
        Array.Copy(body, file, body.Length);
        await File.WriteAllBytesAsync(indexPath, file, cancellationToken);
    }

    private static void WriteBe(BinaryWriter bw, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buf, value);
        bw.Write(buf);
    }

    private static void WriteBe(BinaryWriter bw, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        bw.Write(buf);
    }

    private static void WriteBeAt(MemoryStream ms, long offset, uint value)
    {
        long saved = ms.Position;
        ms.Position = offset;
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        ms.Write(buf);
        ms.Position = saved;
    }

    // ── config lock contention → GIT_ELOCKED ───────────────────────

    [Fact]
    public async Task ConfigLock_Contention_ThrowsLocked()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // Pre-create the .lock file (C filebuf.c:44-51: exists → GIT_ELOCKED).
        string lockPath = Path.Combine(repo.Path, "config.lock");
        await File.WriteAllTextAsync(lockPath, "stale", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() =>
            repo.Config.LockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Locked, ex.Code);
    }

    // ── index write dev field = st_rdev ────────────────────────────

    [Fact]
    public async Task IndexWrite_DevField_UsesStRdev()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "f.txt"), "content", TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(GitPath.FromUtf8String("f.txt"), TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        // Parse the first entry's dev field: header (12) + ctime(8) + mtime(8).
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(repo.Path, "index"), TestContext.Current.CancellationToken);
        int entryCount = (data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11];
        Assert.Equal(1, entryCount);
        int devOffset = 12 + 16;
        uint dev = ((uint)data[devOffset] << 24) | ((uint)data[devOffset + 1] << 16) | ((uint)data[devOffset + 2] << 8) | data[devOffset + 3];

        // C (index.c:909): git_index_entry__init_from_stat stores st_rdev,
        // which is 0 for regular files on Linux/macOS/Windows (st_dev is the
        // containing device — 64769 on this host — and must NOT be written).
        Assert.Equal(0u, dev);
    }

    // ── PathHelpers.Join / PathByteHelpers.Join = git_str_join ─────

    [Theory]
    [InlineData("a", "//b", "a/b")]
    [InlineData("a/", "//b", "a/b")]
    [InlineData("a", "\\b", "a/\\b")]
    [InlineData("a", "/b", "a/b")]
    [InlineData("a/", "b", "a/b")]
    [InlineData("a", "b", "a/b")]
    [InlineData("a", "", "a/")]
    [InlineData("a/", "", "a/")]
    public void PathHelpersJoin_MatchesGitStrJoin(string a, string b, string expected)
    {
        // C git_str_join (str.c:760-807): skip ALL leading '/' of b, insert a
        // separator only when a is non-empty and does not end with one, and
        // treat only '/' as the separator (backslash is a literal byte).
        Assert.Equal(expected, PathHelpers.Join(a, b));
    }

    [Fact]
    public void PathByteHelpersJoin_RepeatedLeadingSlashes_SingleSeparator()
    {
        GitPath result = PathByteHelpers.Join("a"u8, "//b"u8);
        Assert.Equal("a/b", result.ToUtf8String());

        GitPath backslash = PathByteHelpers.Join("a"u8, "\\b"u8);
        Assert.Equal("a/\\b", backslash.ToUtf8String());
    }
}

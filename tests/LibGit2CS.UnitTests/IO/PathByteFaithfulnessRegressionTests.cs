using System.Text;

using LibGit2CS.Blame;
using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;

using GitBlame = LibGit2CS.Blame.GitBlame;
using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Consolidated byte-faithfulness regression suite. These end-to-end tests pin the invariant that a git path survives the full
/// tree → index → diff → status → checkout → blame chain byte-exact for ANY byte sequence, matching libgit2's bag-of-bytes path model. The per-subsystem
/// byte-faithfulness tests (under <c>Diff/</c>, <c>Index/</c>, <c>Objects/</c>, <c>Status/</c>, <c>Checkout/</c>, <c>Blame/</c>, <c>IO/</c>) remain the
/// granular pins; this class is the cross-cutting proof that the whole chain agrees byte-for-byte and that a phantom rename from a tree/index encoding mismatch
/// cannot recur. </summary> <remarks> <para> <b>Failure mode pinned here.</b> A tree name decoded with a one-byte-per-char mapping while the index path
/// decoded with <c>Encoding.UTF8</c> yields two unequal .NET strings for the same raw bytes: the two sides of a HEAD→index diff disagree, a spurious
/// delete + add appears, and rename detection pairs them into a phantom "renamed" entry on an otherwise clean repository. Paths are therefore raw bytes
/// (<see cref="GitPath"/>) compared byte-wise end-to-end. The first test below is the canonical pin. </para> <para> <b>Proof path.</b> <c>0xFF 0xFE 0x80</c>
/// is invalid UTF-8 (0xFF is never a valid leading
/// byte); <c>UTF8.GetString</c> decodes each byte to U+FFFD, so it cannot round-trip through a decoded string. A second sequence <c>0xFF 0xFE 0x81</c> decodes
/// to the SAME lossy string (three U+FFFD) but is distinct as bytes — the discriminating case for the byte-vs-string comparison in
/// <c>DiffPrinter.PrintOneRaw</c>. </para> </remarks>
public sealed class PathByteFaithfulnessRegressionTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // Invalid UTF-8 (0xFF is never a valid leading byte); decodes to three
    // U+FFFD and cannot round-trip through a decoded string.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    // Distinct bytes that decode to the SAME lossy string as s_nonUtf8 — the
    // discriminating case for the byte-wise (not string) comparison.
    private static readonly byte[] s_nonUtf8SameLossy = [0xFF, 0xFE, 0x81];

    public PathByteFaithfulnessRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PathByteRegression_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ===== Canonical pin: HEAD→index clean (no phantom rename) =====

    /// <summary> A tracked non-UTF-8 path stays a single clean entry on a HEAD→index diff because the tree and the index carry the same raw bytes. This test
    /// populates the index from the tree via <see cref="GitIndex.ReadTreeAsync"/> (the index←tree path that <see cref="TreeCache"/> mediates byte-keyed), then
    /// runs an <see cref="GitStatusShow.IndexOnly"/> status walk with rename detection. A
    /// byte-faithful chain yields ZERO entries (HEAD == index, byte-for-byte). Under a string model the same bytes decoded two ways and rename detection
    /// produced a phantom RENAMED entry. </summary> <remarks> Exercises the full HEAD→index chain: tree entry (<c>GitTreeEntry.Name</c> is <see
    /// cref="GitPath"/>) → <see cref="TreeCache"/> byte-keyed → index entry (<c>GitIndexEntry.Path</c> is <see cref="GitPath"/>) → diff delta → status paired walk
    /// + sort. IndexOnly avoids the workdir round-trip (a file on disk cannot hold invalid-UTF-8
    /// bytes through the UTF-8 OS boundary on Linux — that is a filesystem limitation, not a path-handling gap). </remarks>
    [Fact]
    public async Task Status_HeadToIndex_NonUtf8Path_PopulatedIndex_IsCleanNoPhantomRename()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("tracked content\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));
        await CreateCommitAsync(tree, parents: [], "root\n", "refs/heads/main");
        await _repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        // Populate the index from HEAD's tree (the index←tree path that
        // TreeCache mediates — byte-keyed).
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.ReadTreeAsync(tree, TestContext.Current.CancellationToken);
        Assert.Equal(1, index.EntryCount);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(index.EntryByIndex(0).Path.Span));

        using GitStatusList list = await _repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexOnly,
            Flags = GitStatusFlags.RenamesHeadToIndex,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // HEAD == index, byte-for-byte → ZERO entries. No phantom IndexRenamed.
        Assert.Empty(list.Entries);
    }

    // ===== Checkout + Blame round-trip =====

    /// <summary>
    /// Checkout a tree with a non-UTF-8 path, then blame the file. Pins that
    /// the single FS-boundary egress (<see cref="GitPath.ToFileSystemString"/>)
    /// writes the workdir file, that the index entry's path stays byte-exact
    /// (not the transcoded OS name), and that blame's origin path round-trips
    /// byte-exact through the sorted-vector <see cref="BlameScoreboard.Paths"/>.
    /// </summary>
    [Fact]
    public async Task Checkout_Then_Blame_NonUtf8Path_RoundTripByteExact()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("line1\nline2\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));
        GitOid commitOid = await CreateCommitAsync(tree, parents: [], "root\n", "refs/heads/main");
        await _repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        }, TestContext.Current.CancellationToken);

        // The index entry's path is the byte-exact GitPath (the FS-boundary
        // transcode only affects the OS filename, not the index path).
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitIndexEntry entry = Assert.Single(index.Entries);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Path.Span));

        // The workdir file exists at the transcoded OS path.
        string workdirPath = Path.Combine(_repo.Workdir!, entry.Path.ToFileSystemString());
        Assert.True(File.Exists(workdirPath), $"expected file at {workdirPath}");

        // Blame the byte-exact GitPath (the public GitPath overload).
        using GitBlame blame = await _repo.BlameFileAsync(nonUtf8, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, blame.LineCount);
        BlameHunk hunk = blame.GetHunk(0);
        Assert.Equal(commitOid, hunk.FinalCommitId);

        // OrigPath round-trips byte-exact — the proof that blame carries the
        // path as raw bytes (BlameOrigin.Path / BlameHunk.OrigPath are GitPath).
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(hunk.OrigPath.Span));
        string lossy = hunk.OrigPath.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
    }

    // ===== Diff raw output: byte-wise (not string) path comparison =====

    /// <summary> Pins the <c>DiffPrinter.PrintOneRaw</c> byte-faithful path comparison. A rename from <c>0xFF 0xFE 0x80</c> to <c>0xFF 0xFE 0x81</c> has
    /// DISTINCT raw bytes but the SAME lossy UTF-8 decode (three U+FFFD). The raw-format output prints BOTH paths when they differ byte-wise. A string
    /// comparison of the two decodes would see equal strings and print only ONE path — the decode-then-compare failure mode this byte-wise comparison
    /// eliminates. </summary>
    [Fact]
    public async Task Diff_RawOutput_DistinctNonUtf8Rename_PrintsBothPaths()
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("same\n"), TestContext.Current.CancellationToken);
        var src = GitPath.FromUtf8Bytes(s_nonUtf8);
        var dst = GitPath.FromUtf8Bytes(s_nonUtf8SameLossy);

        // Sanity: the two paths are distinct bytes but decode to the same lossy
        // string — the discriminating case for byte-vs-string comparison.
        Assert.False(src.Equals(dst));
        Assert.Equal(src.ToUtf8String(), dst.ToUtf8String());

        GitTree treeA = await BuildTreeAsync((src, blobOid));
        GitTree treeB = await BuildTreeAsync((dst, blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        GitDiffDelta delta = Assert.Single(diff.Deltas);
        Assert.Equal(GitDeltaStatus.Renamed, delta.Status);

        string raw = await diff.ToBufferTextAsync(GitDiffPrintFormat.Raw, TestContext.Current.CancellationToken);
        Assert.NotEmpty(raw);

        // Raw line: ":<omode> <nmode> <oid>... <oid>... R100\t<old> <new>\n". PrintOneRaw prints BOTH paths when the pointers/bytes differ, with a SPACE
        // between them (C diff_print.c:262-265: "\t%s %s\n"). → 2 tab-segment fields with the paths in the second.
        string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string rawLine = Assert.Single(lines);
        string[] tabFields = rawLine.Split('\t');
        Assert.True(tabFields.Length == 2, $"raw rename line should have 2 tab-fields (header + \"old new\"); got {tabFields.Length}: '{rawLine}'");
        Assert.Contains(' ', tabFields[1]);
    }

    // ===== helpers =====

    private async ValueTask<GitTree> BuildTreeAsync(params (GitPath Name, GitOid Oid)[] entries)
    {
        GitTreeBuilder builder = _repo.NewTreeBuilder();
        foreach ((GitPath name, GitOid oid) in entries)
        {
            await builder.InsertAsync(name, oid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        }

        GitOid treeOid = await builder.WriteAsync(TestContext.Current.CancellationToken);
        return (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
    }

    private async ValueTask<GitOid> CreateCommitAsync(GitTree tree, IReadOnlyList<GitOid> parents, string message, string? updateRef)
    {
        var sig = new GitSignature("Test", "test@example.com", new GitTime(0, 0));
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree.Id,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = updateRef,
        }, TestContext.Current.CancellationToken);
    }
}

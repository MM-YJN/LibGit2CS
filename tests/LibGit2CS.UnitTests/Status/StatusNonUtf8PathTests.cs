using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Status;

/// <summary> Byte-faithful status path tests. Pins that the status engine carries paths as raw bytes through the HEAD→index and
/// index→workdir diffs and the paired walk, matching libgit2's <c>git__strcmp</c>-based <c>status_entry_cmp</c> model. A non-UTF-8 path (invalid in any
/// encoding) survives a status walk byte-exact — no phantom rename from a tree/index encoding mismatch. </summary>
public sealed class StatusNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    public StatusNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StatusNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ===== Status reports a non-UTF-8 tracked path byte-exact =====

    [Fact]
    public async Task Status_NonUtf8TrackedPath_ReportsByteExact()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("content\n");

        // Build a tree with the non-UTF-8 path, commit it, and set HEAD. The index is empty (fresh repo) so the HEAD→index diff reports the path as
        // IndexDeleted. The point is that GitStatusEntry.Path carries the raw bytes from the diff delta through the status paired walk and out the public API
        // (byte-keyed status walk).
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));
        var sig = new GitSignature("Test", "test@example.com", new GitTime(0, 0));
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree.Id,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "root\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        using GitStatusList list = await _repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The non-UTF-8 path appears byte-exact on GitStatusEntry.Path. The
        // HEAD→index diff (IndexDeleted — HEAD has it, index is empty) and
        // the HEAD→workdir diff (WorkdirDeleted — workdir is empty) both
        // carry the raw bytes; the paired walk merges them into one entry.
        GitStatusEntry entry = Assert.Single(list.Entries);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Path.Span));
        Assert.True((entry.Status & GitStatusFlags.IndexDeleted) != 0);

        // Lossy-string reconstruction does NOT match.
        string lossy = entry.Path.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
    }

    // ===== No phantom rename from tree/index encoding mismatch =====

    [Fact]
    public async Task Status_NonUtf8Path_NoPhantomRenameFromEncodingMismatch()
    {
        // A tree name decoded with a one-byte-per-char mapping while GitIndexEntry.Path decodes with UTF-8 produces two unequal
        // strings for the same raw bytes → status reports a spurious delete + add → rename detection pairs them as a phantom "renamed" entry on a clean repo.
        // The byte-faithful conversion keeps tree→tree compares (GitTreeEntry.Name is GitPath), the diff pipeline, and the status
        // paired walk + sort byte-keyed. TreeCache closes the index←tree path: ReadTreeAsync → GitTree.WalkAsync (byte-faithful) →
        // TreeCache (byte-faithful) → index entries. This test exercises the HEAD→index diff path (byte-faithful end-to-end); the ReadTreeAsync index
        // population path is covered by TreeCacheNonUtf8PathTests.
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("same content\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));
        var sig = new GitSignature("Test", "test@example.com", new GitTime(0, 0));
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree.Id,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "root\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        using GitStatusList list = await _repo.StatusNewAsync(new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RenamesHeadToIndex,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // With rename detection enabled, there must be NO phantom RENAMED
        // entry — the HEAD tree carries the non-UTF-8 path byte-faithfully
        // (GitTreeEntry.Name is GitPath), the diff compares bytes, and the status
        // paired walk + sort use byte-wise GitPath. A single
        // IndexDeleted entry (HEAD has it, index/workdir don't) is the
        // correct result — not a delete+add→rename pair.
        GitStatusEntry entry = Assert.Single(list.Entries);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Path.Span));
        Assert.True((entry.Status & GitStatusFlags.IndexDeleted) != 0);
        Assert.True((entry.Status & GitStatusFlags.IndexRenamed) == 0);
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
}

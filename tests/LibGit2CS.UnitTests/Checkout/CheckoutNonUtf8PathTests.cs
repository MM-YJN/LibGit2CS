using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary> Byte-faithful checkout path tests. Pins that the checkout engine writes non-UTF-8 paths to the workdir byte-exact (via the
/// single FS-boundary transcode in <see cref="GitPath.ToFileSystemString"/>), and that the checkout walk's lockstep comparator + pathspec + conflict logic all
/// operate on raw bytes — matching libgit2's <c>data-&gt;diff-&gt;strcomp</c> / <c>pfxcomp</c> model (checkout.c:678-679). A non-UTF-8 path (invalid in any
/// encoding) survives a checkout byte-exact. </summary>
public sealed class CheckoutNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    public CheckoutNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ===== Checkout writes a non-UTF-8 path to the workdir byte-exact =====

    [Fact]
    public async Task Checkout_NonUtf8Path_FileWrittenByteExact()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("file content\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));

        // Force checkout the tree into the (empty) workdir.
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        }, TestContext.Current.CancellationToken);

        // The file appears in the workdir at the OS-transcoded path. On a
        // byte-faithful checkout the file's OS name is the UTF-8 decode
        // (replacement fallback for invalid bytes). The byte-exact git path
        // is preserved in the index entry.
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, index.EntryCount);
        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Path.Span));

        // The file exists in the workdir at the transcoded path.
        string workdirPath = Path.Combine(_repo.Workdir!, entry.Path.ToFileSystemString());
        Assert.True(File.Exists(workdirPath), $"expected file at {workdirPath}");
        Assert.Equal(content, await File.ReadAllBytesAsync(workdirPath, TestContext.Current.CancellationToken));

        // Lossy-string reconstruction does NOT match the original bytes.
        string lossy = entry.Path.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
    }

    // ===== Checkout notification carries the byte-exact path =====

    [Fact]
    public async Task Checkout_NonUtf8Path_NotificationCarriesByteExactPath()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("notified\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));

        GitPath? notifiedPath = null;
        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
            NotifyFlags = GitCheckoutNotifyFlags.Updated,
            Notify = n =>
            {
                notifiedPath = n.Path;
                return false; // false = continue (true = abort)
            },
        };

        await _repo.CheckoutTreeAsync(tree, opts, TestContext.Current.CancellationToken);

        Assert.NotNull(notifiedPath);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(notifiedPath.Value.Span));
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

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.IntegrationTests.Pack;

/// <summary>
/// End-to-end regression tests for the behaviors in the
/// objects/odb/pack area (libgit2 1.9.4).
/// </summary>
public sealed class ObjectsParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ObjectsParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectsParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary> After writing a pack whose objects are delta-compressed, <c>ReadHeaderAsync</c> must report each object's RESULT size (C's
    /// <c>git_packfile_resolve_header</c> semantics), and the write-order computation must terminate on branching delta chains (guarded by a watchdog
    /// here, so a non-terminating write-order computation fails the test). </summary>
    [Fact]
    public async Task PackWriter_DeltaCompressedObjects_HeaderSizesMatchContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "r"), isBare: true, ctx, ct);

        // Similar, large-ish blobs (>= 50 bytes) so the pack writer delta-
        // compresses them against each other.
        var contents = new Dictionary<GitOid, byte[]>();
        for (int i = 0; i < 40; i++)
        {
            var sb = new StringBuilder();
            for (int j = 0; j < 80; j++)
            {
                sb.Append($"{i:D3}:{j:D3}: line of shared content\n");
            }

            sb.Append($"tail marker {i}\n");
            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
            GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Blob, body, ct);
            contents[oid] = body;
        }

        using (GitPackWriter writer = repo.NewPackWriter())
        {
            foreach (GitOid oid in contents.Keys)
            {
                await writer.InsertAsync(oid, ct);
            }

            string packDir = Path.Combine(repo.Path, "objects", "pack");
            Directory.CreateDirectory(packDir);

            // Watchdog: AddDescendantsToOrder must terminate on branching delta
            // chains, so the whole write must complete in time.
            Task<string> writeTask = writer.WriteToDirectoryAsync(packDir, progress: null, ct);
            Task finished = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(60), ct));
            Assert.Same(writeTask, finished);
            await writeTask;
        }

        // Refresh the ODB so the new pack is visible.
        await repo.Objects.RefreshPackBackendsAsync(ct);

        foreach ((GitOid oid, byte[] body) in contents)
        {
            GitObjectHeader? header = await repo.Objects.ReadHeaderAsync(oid, ct);
            Assert.NotNull(header);
            Assert.Equal(GitObjectType.Blob, header!.Value.Type);
            Assert.Equal(body.Length, header.Value.Size);

            // Full read must still return the exact content.
            GitBlob? blob = await repo.ObjectLookupAsync<GitBlob>(oid, ct);
            Assert.NotNull(blob);
            Assert.Equal(body, blob!.Content);
        }
    }

    /// <summary> The tree-entry homing search must skip non-monotonic entries between prefix matches (C breaks only on &lt; 0 / &gt; 0), so
    /// <c>EntryByName</c> finds an exact match sitting after a positive comparison entry. </summary>
    [Fact]
    public async Task Tree_EntryByName_PositiveEntryBetweenPrefixMatches_FindsExact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "tree-entry-prefix"), isBare: true, ctx, ct);

        byte[] body = "x\n"u8.ToArray();
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, body, ct);

        using var builder = new GitTreeBuilder(repo);
        foreach (string name in new[] { "fo", "foa", "foo", "foobar", "fop" })
        {
            await builder.InsertAsync(GitPath.FromUtf8String(name), blobOid, GitFileMode.Regular, ct);
        }

        GitOid treeOid = await builder.WriteAsync(ct);
        GitTree? tree = await repo.ObjectLookupAsync<GitTree>(treeOid, ct);
        Assert.NotNull(tree);

        GitTreeEntry? found = tree!.EntryByName("foo");
        Assert.NotNull(found);
        Assert.Equal("foo", found!.Value.Name.ToUtf8String());

        // Exact matches still win over prefixes; missing names stay missing.
        Assert.Equal("fo", tree.EntryByName("fo")!.Value.Name.ToUtf8String());
        Assert.Equal("foobar", tree.EntryByName("foobar")!.Value.Name.ToUtf8String());
        Assert.Null(tree.EntryByName("fooo"));
    }

    /// <summary> The stable timsort backs <c>git_vector_sort</c>; the status pairing sorts 100+ entries here, which must come out complete and in path
    /// order (a short first run would corrupt sorts of ≥64 elements). </summary>
    [Fact]
    public async Task Status_ManyEntries_SortedComplete()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitContext ctx = new();
        string workdir = Path.Combine(_tempDir, "status-many");
        await using GitRepository repo = await GitRepository.InitAsync(workdir, isBare: false, ctx, ct);

        // Create 120 files (deliberately NOT in path-sorted creation order so
        // the status pairing's sort sees short natural runs).
        var names = new List<string>();
        for (int i = 119; i >= 0; i--)
        {
            string name = $"dir{i % 7:D1}/file{i:D3}.txt";
            names.Add(name);
            string full = Path.Combine(workdir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, $"content {i}\n", ct);
        }

        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddAllAsync(pathspec: null, cancellationToken: ct);
        await index.WriteAsync(ct);

        using GitStatusList status = await repo.StatusNewAsync(null, ct);
        Assert.Equal(120, status.EntryCount);

        string[] paths = status.Entries.Select(e => e.Path.ToUtf8String()).ToArray();
        string[] sorted = [.. paths.OrderBy(p => p, StringComparer.Ordinal)];
        Assert.Equal(sorted, paths);

        // Every file is represented.
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), paths);
    }
}

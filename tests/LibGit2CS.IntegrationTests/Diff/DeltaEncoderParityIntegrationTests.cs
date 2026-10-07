using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// Integration tests for the delta-create parity behavior in
/// libgit2 1.9.4 exercised
/// end-to-end through the binary-patch pipeline (blobs in a real repo →
/// tree-to-tree diff → <see cref="GitPatch.GetBinaryAsync"/> →
/// <see cref="DeltaEncoder.Create"/>).
/// The C reference delta for this content (verified with a harness linking
/// the built static library) is
/// <c>808104908104809401801043434343434343434343434343434343</c> — 27
/// bytes: copy(0x10000@0), copy(127@0x10000), insert 'B' + 16 'C'.
/// </summary>
public sealed class DeltaEncoderParityIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private static byte[] OldContent
    {
        get
        {
            byte[] bytes = new byte[1 + 0x1003f + 64];
            bytes[0] = 0x00; // NUL → the file is binary.
            Array.Fill(bytes, (byte)'A', 1, 0x1003f);
            Array.Fill(bytes, (byte)'B', 1 + 0x1003f, 64);
            return bytes;
        }
    }

    private static byte[] NewContent
    {
        get
        {
            byte[] bytes = new byte[1 + 0x1003f + 64 + 16];
            bytes[0] = 0x00;
            Array.Fill(bytes, (byte)'A', 1, 0x1003f);
            Array.Fill(bytes, (byte)'B', 1 + 0x1003f, 64);
            Array.Fill(bytes, (byte)'C', 1 + 0x1003f + 64, 16);
            return bytes;
        }
    }

    private static async Task<GitOid> CommitAsync(GitRepository repo, GitOid? parent, byte[] content, string message, CancellationToken ct)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllBytesAsync(Path.Combine(workdir, "big.bin"), content, ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("big.bin", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is { } p ? [p] : [],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    [Fact]
    public async Task BinaryPatch_LargeMatchRemainder_DeltaBytesMatchC()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-delta-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                GitOid first = await CommitAsync(repo, null, OldContent, "init\n", ct);
                await CommitAsync(repo, first, NewContent, "modify\n", ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitReference? head = await repo2.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
            Commit? headCommit = await repo2.ObjectLookupAsync<Commit>(direct.Target, ct);
            Assert.NotNull(headCommit);
            GitTree? newTree = await repo2.ObjectLookupAsync<GitTree>(headCommit!.Tree, ct);
            Assert.NotNull(newTree);
            Commit? parentCommit = await repo2.ObjectLookupAsync<Commit>(headCommit.Parents[0], ct);
            Assert.NotNull(parentCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(parentCommit!.Tree, ct);
            Assert.NotNull(oldTree);

            var diffOpts = new GitDiffOptions { Flags = GitDiffOptionsFlags.ShowBinary };
            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, diffOpts, ct);
            using GitPatch patch = await repo2.PatchFromDiffAsync(diff, 0, ct);

            Assert.True(await patch.GetIsBinaryAsync(ct), "the NUL-prefixed file must be binary");

            GitBinaryPatch? binary = await patch.GetBinaryAsync(ct);
            Assert.NotNull(binary);
            Assert.True(binary!.ContainsData);
            Assert.Equal(GitBinaryPatchType.Delta, binary.NewFile.Type);

            // The new-side delta (old → new direction) is the C-verified
            // 27-byte delta; the remainder after the >64 KB copy persists
            // into the next match/copy decision. (C harness on the exact
            // NUL-prefixed content: 80810490810480940180104343434343...)
            Assert.Equal(27, binary.NewFile.InflatedLength);
            using PooledByteBufferWriter delta = Zlib.DecompressPackObject(binary.NewFile.Data);
            Assert.Equal("808104908104809401801043434343434343434343434343434343", Convert.ToHexString(delta.WrittenSpan).ToLowerInvariant());

            // Round-trip: the delta applies back to the target content.
            byte[] applied = DeltaEncoder.Apply(OldContent, delta.WrittenSpan);
            Assert.Equal(NewContent, applied);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}

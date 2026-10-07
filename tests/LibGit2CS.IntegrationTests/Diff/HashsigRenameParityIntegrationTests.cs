using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// Integration tests for the hashsig parity behaviors in
/// libgit2 1.9.4 exercised end-to-end
/// through rename detection (<see cref="GitDiff.FindSimilarAsync"/>).
/// The two file contents are 128-line buffers whose rolling hashes
/// ("baz%04d" lines, strictly increasing, plus a trailing "0000" line
/// hashing below every "baz" line) saturate the bounded similarity heaps
/// asymmetrically. The C reference (libgit2 1.9.4 hashsig.c) scores this
/// pair 50 — exactly the default rename threshold — and reports a rename.
/// </summary>
public sealed class HashsigRenameParityIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Builds "baz%04d\n" lines for i in [from, to).</summary>
    private static string BazRange(int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < to; i++)
        {
            sb.Append("baz").Append(i.ToString("D4")).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The old side: 64 shared lines + 63 unique high-hash lines + a
    /// low-hash tail line (128 lines, 1024 bytes).
    /// </summary>
    private static string OldContent => BazRange(0, 64) + BazRange(200, 263) + "0000\n";

    /// <summary>
    /// The new side: the same 64 shared lines + 63 different high-hash
    /// lines + the same low-hash tail (128 lines, 1024 bytes). The C
    /// reference scores OldContent vs NewContent exactly (the default
    /// rename threshold), so rename detection pairs them.
    /// </summary>
    private static string NewContent => BazRange(0, 64) + BazRange(263, 326) + "0000\n";

    /// <summary>
    /// A delete + add whose similarity hash score is exactly at the default
    /// rename threshold (50) must be reported as a single
    /// <see cref="GitDeltaStatus.Renamed"/> delta with similarity 50 — not
    /// as separate Deleted + Added deltas. C-verified against libgit2 1.9.4
    /// (git_hashsig_compare = 50 on these buffers; diff_tform.c pairs when
    /// <c>similarity &gt;= rename_threshold</c>).
    /// </summary>
    [Fact]
    public async Task FindSimilar_ThresholdSimilarity_ReportsRename()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-hashsig-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Initial commit: old.txt with the old content.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "old.txt"), OldContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("old.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            GitOid first;
            await using (GitRepository reopen = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                GitReference? head = await reopen.ReferenceResolveAsync("HEAD", ct);
                Assert.NotNull(head);
                first = Assert.IsType<GitDirectReference>(head).Target;
            }

            // Second commit: delete old.txt, add new.txt with the new content.
            GitOid second;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                File.Delete(Path.Combine(workdir, "old.txt"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "new.txt"), NewContent, ct);

                GitIndex idx = await repo.GetIndexAsync(ct);
                idx.RemoveByPath("old.txt");
                await idx.AddByPathAsync("new.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                second = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "replace old with new\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo2.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo2.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            // Before FindSimilar: a delete + an add.
            Assert.Equal(2, diff.DeltaCount);
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Deleted && d.Path.ToUtf8String() == "old.txt");
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Added && d.Path.ToUtf8String() == "new.txt");

            await diff.FindSimilarAsync(cancellationToken: ct);

            // After FindSimilar: a single rename at exactly the default
            // threshold (50), matching the C reference.
            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta renamed = diff.GetDelta(0);
            Assert.Equal(GitDeltaStatus.Renamed, renamed.Status);
            Assert.Equal(50, renamed.Similarity);
            Assert.Equal("old.txt", renamed.OldFile.Path?.ToUtf8String());
            Assert.Equal("new.txt", renamed.NewFile.Path?.ToUtf8String());
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

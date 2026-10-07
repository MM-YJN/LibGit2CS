using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// Integration tests for the DiffPrinter path-quoting parity behavior in
/// libgit2 1.9.4 exercised end-to-end
/// against a real repository with a non-ASCII filename.
/// The C reference (libgit2 1.9.4, verified with a differential harness)
/// prints this diff as
/// <code>
/// diff --git "a/caf\303\251.txt" "b/caf\303\251.txt"
/// --- "a/caf\303\251.txt"
/// +++ "b/caf\303\251.txt"
/// </code>
/// and the binary variant as
/// <code>
/// Binary files "a/bin\303\251.dat" and "b/bin\303\251.dat" differ
/// </code>
/// </summary>
public sealed class DiffPrinterQuoteParityIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// Commits <paramref name="content"/> under <paramref name="fileName"/>
    /// on top of <paramref name="parent"/> (or as the root commit) and
    /// returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFileAsync(
        GitRepository repo, GitOid? parent, string fileName, byte[] content, string message, CancellationToken ct)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllBytesAsync(Path.Combine(workdir, fileName), content, ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync(fileName, ct);
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
    public async Task PatchOutput_NonAsciiPath_QuotedWithOctalEscapes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-quote-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                GitOid first = await CommitFileAsync(repo, null, "caf\u00e9.txt", "a\n"u8.ToArray(), "init\n", ct);
                await CommitFileAsync(repo, first, "caf\u00e9.txt", "b\n"u8.ToArray(), "modify\n", ct);
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

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            // C-verified: the prefixed paths are quoted with octal escapes.
            Assert.Contains("diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n", patch);
            Assert.Contains("--- \"a/caf\\303\\251.txt\"\n", patch);
            Assert.Contains("+++ \"b/caf\\303\\251.txt\"\n", patch);
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

    [Fact]
    public async Task PatchOutput_BinaryNonAsciiPath_QuotedInBinaryFilesLine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-quote-bin-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                GitOid first = await CommitFileAsync(repo, null, "bin\u00e9.dat", [0x61, 0x00, 0x62, 0x00, 0x63], "init\n", ct);
                await CommitFileAsync(repo, first, "bin\u00e9.dat", [0x61, 0x00, 0x58, 0x00, 0x63], "modify\n", ct);
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

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);

            // C-verified: the Binary files line quotes the prefixed paths.
            Assert.Contains("diff --git \"a/bin\\303\\251.dat\" \"b/bin\\303\\251.dat\"\n", patch);
            Assert.Contains("Binary files \"a/bin\\303\\251.dat\" and \"b/bin\\303\\251.dat\" differ\n", patch);
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

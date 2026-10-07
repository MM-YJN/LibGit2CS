using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.IO;

/// <summary>
/// Integration tests for the pathspec/iterator parity behaviors in
/// libgit2 1.9.4 exercised
/// end-to-end through real repositories and tree/workdir/index iterators.
///
/// <list type="bullet">
/// <item>pathspec.c:161-166: a file literally named
/// <c>!foo</c> must match the negative pattern <c>!foo</c> (the negation is
/// inverted for a <c>!</c>-prefixed name), so that exact-name branch is
/// live.</item>
/// <item>iterator.c:302-356: a pathspec-restricted workdir
/// diff whose pathlist entry is nested (<c>foo/bar.txt</c>) must descend
/// into the parent directory <c>foo</c> (IS_PARENT), so the nested file
/// appears in the diff.</item>
/// </list>
/// </summary>
public sealed class PathspecParityIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private static async Task<GitOid> InitRepoAsync(string path, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "foo"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "foo", "bar.txt"), "bar\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "other.txt"), "other\n", ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("foo/bar.txt", ct);
        await idx.AddByPathAsync("other.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return commitOid;
    }

    /// <summary>
    /// A pathspec-restricted diff with the nested pathlist entry
    /// <c>foo/bar.txt</c> must report the delta for the modified nested
    /// file. The flipped <c>ComparePrefix(path, p)</c> returned NONE for
    /// the <c>foo</c> directory, so the workdir/tree iterators skipped the
    /// whole subtree and the diff was empty.
    /// </summary>
    [Fact]
    public async Task DiffWithNestedPathSpec_ReportsNestedDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-pathspec-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "foo", "bar.txt"), "modified\n", ct);

            var opts = new GitDiffOptions { PathSpecStrings = ["foo/bar.txt"] };
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, ct);

            GitDiffDelta delta = Assert.Single(diff.Deltas);
            Assert.Equal("foo/bar.txt", delta.Path.ToUtf8String());
            Assert.Equal(GitDeltaStatus.Modified, delta.Status);
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

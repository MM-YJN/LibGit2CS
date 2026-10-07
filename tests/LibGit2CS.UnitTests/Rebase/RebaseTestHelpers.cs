using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Test helpers for rebase tests. Mirrors libgit2's rebase test suite patterns.
/// </summary>
internal static class RebaseTestHelpers
{
    /// <summary>
    /// Opens the rebase fixture repo (from <c>Fixtures/rebase/rebase.zip</c>).
    /// Sets <c>core.autocrlf</c> to false and returns the opened repository.
    /// </summary>
    public static string OpenRebaseRepo(List<string> extractedPaths)
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/rebase/rebase.zip");
        extractedPaths.Add(path);
        return Path.Combine(path, "rebase");
    }

    /// <summary>Creates a signature for rebase commits.</summary>
    public static GitSignature CreateSignature()
        => GitSignature.Create("Rebaser", "rebaser@rebaser.rb", new GitTime(1405694510, 0));

    /// <summary>Parses a SHA-1 OID from hex.</summary>
    public static GitOid Oid(string hex) => GitOid.Parse(hex.AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>Looks up a commit by hex OID.</summary>
    public static async Task<Commit> LookupCommitAsync(GitRepository repo, string hex)
    {
        Commit? commit = await repo.ObjectLookupAsync<Commit>(Oid(hex), TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        return commit;
    }

    /// <summary>
    /// Creates an <see cref="GitAnnotatedCommit"/> from a branch ref name
    /// (e.g. <c>"refs/heads/beef"</c>).
    /// </summary>
    public static async Task<GitAnnotatedCommit> FromRefAsync(GitRepository repo, string refName)
    {
        GitReference reference = await repo.ReferenceLookupAsync(refName)
            ?? throw new InvalidOperationException($"ref '{refName}' not found");
        return await repo.AnnotatedCommitFromRefAsync(reference);
    }

    /// <summary>
    /// Asserts that a state file in <c>.git/rebase-merge/</c> contains the
    /// expected content.
    /// </summary>
    public static void AssertStateFile(string gitdir, string filename, string expected)
    {
        string path = Path.Combine(gitdir, "rebase-merge", filename);
        Assert.True(File.Exists(path), $"state file '{filename}' not found");
        string content = File.ReadAllText(path);
        Assert.Equal(expected, content);
    }

    /// <summary>
    /// Asserts that a gitdir-level file (e.g. <c>ORIG_HEAD</c>) contains
    /// the expected content.
    /// </summary>
    public static void AssertGitdirFile(string gitdir, string filename, string expected)
    {
        string path = Path.Combine(gitdir, filename);
        Assert.True(File.Exists(path), $"gitdir file '{filename}' not found");
        string content = File.ReadAllText(path);
        Assert.Equal(expected, content);
    }
}

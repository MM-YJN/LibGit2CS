using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitBlame = LibGit2CS.Blame.GitBlame;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Shared base for blame golden tests. Mirrors <c>DiffGoldenBase</c> /
/// <c>StatusGoldenBase</c>: extracts embedded zip fixtures to temp dirs,
/// opens them as repositories, and provides helpers for hunk assertions.
/// </summary>
public abstract class BlameGoldenBase : IDisposable
{
    private readonly List<string> _extractedPaths = [];
    private bool _disposed;
    private readonly GitContext _context = new();
    protected GitContext Context => _context;

    protected BlameGoldenBase()
    {
        TempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_BlameGolden_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(TempDir);
    }

    protected string TempDir { get; }

    /// <summary>
    /// Extracts an embedded fixture zip and opens it as a repository.
    /// For bare repos (e.g. <c>blametest.git</c>), the extracted directory
    /// IS the git dir — use <see cref="OpenBareFixtureRepo"/>.
    /// </summary>
    protected async ValueTask<GitRepository> OpenFixtureRepoAsync(string fixtureName)
    {
        string extracted = FixtureLoader.ExtractTreeToTemp($"Fixtures.blame.{fixtureName}.zip");
        _extractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, fixtureName);
        return await GitRepository.OpenAsync(repoPath, Context);
    }

    /// <summary>
    /// Opens a bare fixture repo (the extracted directory is the git dir itself).
    /// </summary>
    protected async ValueTask<GitRepository> OpenBareFixtureRepoAsync(string fixtureName)
    {
        string extracted = FixtureLoader.ExtractTreeToTemp($"Fixtures.blame.{fixtureName}.zip");
        _extractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, fixtureName);
        return await GitRepository.OpenBareAsync(repoPath, Context);
    }

    /// <summary>
    /// Resolves a commit by abbreviated OID prefix.
    /// </summary>
    protected static async ValueTask<Commit> ResolveCommitAsync(GitRepository repo, string oidPrefix)
    {
        var oid = GitOid.Parse(oidPrefix.AsSpan(), repo.ObjectFormat);
        return await repo.ObjectLookupPrefixAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Commit {oidPrefix} not found");
    }

    /// <summary>
    /// Gets the full OID for an abbreviated commit prefix.
    /// </summary>
    protected static async ValueTask<GitOid> ResolveOidAsync(GitRepository repo, string oidPrefix)
    {
        return (await ResolveCommitAsync(repo, oidPrefix)).Id;
    }

    /// <summary>
    /// Opens the mailmap fixture repo (non-bare, has .mailmap + file.txt).
    /// </summary>
    public static async ValueTask<GitRepository> OpenMailmapRepoAsync()
    {
        string extracted = FixtureLoader.ExtractTreeToTemp("Fixtures.blame.mailmap.zip");
        s_globalExtractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, "mailmap");
        return await GitRepository.OpenAsync(repoPath, new GitContext());
    }

    private static readonly List<string> s_globalExtractedPaths = [];

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.Dispose();

        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // Swallow — temp dir cleanup is best-effort.
            }
        }

        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
            // Swallow.
        }
    }
}

/// <summary>
/// Helper for asserting blame hunk properties. Mirrors clar's
/// <c>check_blame_hunk_index</c> (<c>blame_helpers.c</c>).
/// </summary>
internal static class BlameHunkVerifier
{
    /// <summary>
    /// Asserts a hunk's <c>final_start_line_number</c>, <c>lines_in_hunk</c>,
    /// <c>boundary</c>, <c>final_commit_id</c> (resolved from the prefix),
    /// and <c>orig_path</c>.
    /// </summary>
    public static async Task AssertHunkAsync(
        GitRepository repo, GitBlame blame, int idx,
        int startLine, int len, int boundary, string commitId, string origPath)
    {
        BlameHunk hunk = blame.GetHunk(idx);

        Assert.Equal(startLine, hunk.FinalStartLineNumber);
        Assert.Equal(len, hunk.LinesInHunk);

        if (commitId.StartsWith("0000", StringComparison.Ordinal))
        {
            // Zero OID (buffer-blame hunk).
            Assert.True(hunk.FinalCommitId.IsZero,
                $"hunk {idx}: expected zero OID, got {hunk.FinalCommitId}");
        }
        else
        {
            GitOid expected = await ResolveOidAsync(repo, commitId);
            Assert.Equal(expected, hunk.FinalCommitId);
            Assert.Equal(expected, hunk.OrigCommitId);
        }

        Assert.Equal(boundary, hunk.Boundary);
        Assert.Equal(origPath, hunk.OrigPath.ToUtf8String());
    }

    private static async Task<GitOid> ResolveOidAsync(GitRepository repo, string prefix)
    {
        var oid = GitOid.Parse(prefix.AsSpan(), repo.ObjectFormat);
        return (await repo.ObjectLookupPrefixAsync<Commit>(oid, TestContext.Current.CancellationToken))?.Id
            ?? throw new InvalidOperationException($"Commit {prefix} not found");
    }
}

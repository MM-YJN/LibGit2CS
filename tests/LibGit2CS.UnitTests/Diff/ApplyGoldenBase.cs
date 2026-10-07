using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Shared base for apply bridge tests (apply bridge step). Handles fixture extraction
/// for the <c>merge-recursive</c> repo + tree resolution. Mirrors
/// <see cref="DiffGoldenBase"/> but extracts from <c>Fixtures/apply/</c>.
/// </summary>
public abstract class ApplyGoldenBase : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];
    private readonly GitContext _context = new();
    protected GitContext Context => _context;

    protected ApplyGoldenBase()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_ApplyGolden_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _context.Dispose();
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Extracts <c>Fixtures/apply/merge-recursive.zip</c> to a temp dir and
    /// opens the repository. The fixture is pre-reset to commit
    /// <c>539bd01</c> (baked into the zip by <c>generate-goldens.sh</c>).
    /// </summary>
    protected async ValueTask<GitRepository> OpenMergeRecursiveRepoAsync()
    {
        string extracted = FixtureLoader.ExtractTreeToTemp("Fixtures/apply/merge-recursive.zip");
        _extractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, "merge-recursive");
        return await GitRepository.OpenAsync(repoPath, Context);
    }

    /// <summary>
    /// Resolves a full 40-char commit OID hex string to its <see cref="GitTree"/>.
    /// Matches clar's <c>resolve_commit_oid_to_tree</c>.
    /// </summary>
    protected static async Task<GitTree> ResolveTreeAsync(GitRepository repo, string commitOidHex)
    {
        var oid = GitOid.Parse(commitOidHex.AsSpan(), repo.ObjectFormat);
        Commit commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");
        return await repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"tree for {commitOidHex} not found");
    }
}

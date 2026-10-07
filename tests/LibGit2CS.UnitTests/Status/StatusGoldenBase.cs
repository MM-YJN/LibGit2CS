using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Shared base for repository-backed status + ignore golden tests. Handles
/// fixture extraction, repo open, temp-dir cleanup. Mirrors
/// <see cref="Diff.DiffGoldenBase"/>.
/// </summary>
public abstract class StatusGoldenBase : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];
    private readonly GitContext _context = new();
    protected GitContext Context => _context;

    protected StatusGoldenBase()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_StatusGolden_" + Guid.NewGuid().ToString("N")[..8]);
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
    /// Extracts <c>Fixtures/diff/&lt;<paramref name="fixtureName"/>&gt;.zip</c>
    /// and opens the repository at the working-tree root.
    /// </summary>
    protected async ValueTask<GitRepository> OpenFixtureRepoAsync(string fixtureName)
    {
        string extracted = FixtureLoader.ExtractTreeToTemp($"Fixtures/diff/{fixtureName}.zip");
        _extractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, fixtureName);
        return await GitRepository.OpenAsync(repoPath, Context);
    }

    /// <summary>
    /// Extracts <c>Fixtures/status/&lt;<paramref name="fixtureName"/>&gt;.zip</c>
    /// and opens the repository at the working-tree root. The status fixtures
    /// (packaged from libgit2's <c>tests/resources/</c>) extract to
    /// <c>&lt;fixtureName&gt;/</c> with <c>.git</c> already renamed (the
    /// packaging script renames <c>.gitted</c> → <c>.git</c> before zipping).
    /// </summary>
    protected async ValueTask<GitRepository> OpenStatusFixtureAsync(string fixtureName)
    {
        string extracted = FixtureLoader.ExtractTreeToTemp($"Fixtures/status/{fixtureName}.zip");
        _extractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, fixtureName);
        return await GitRepository.OpenAsync(repoPath, Context);
    }

    /// <summary>
    /// Extracts <c>Fixtures/repo/empty_standard.zip</c> and opens the
    /// repository at the working-tree root. The fixture extracts to
    /// <c>empty_standard_repo/</c> with <c>.gitted</c> — renamed to
    /// <c>.git</c> after extraction (matches <c>cl_git_sandbox_init</c>).
    /// </summary>
    protected async ValueTask<GitRepository> OpenRepoFixtureAsync(string fixtureName)
    {
        string extracted = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/{fixtureName}.zip");
        _extractedPaths.Add(extracted);
        // The repo fixtures extract to "<name>_repo/" (e.g. empty_standard_repo).
        string repoPath = Path.Combine(extracted, fixtureName + "_repo");

        // Rename .gitted → .git (matches cl_git_sandbox_init / generate-goldens.sh).
        string gitted = Path.Combine(repoPath, ".gitted");
        string gitdir = Path.Combine(repoPath, ".git");
        if (Directory.Exists(gitted) && !Directory.Exists(gitdir))
        {
            Directory.Move(gitted, gitdir);
        }

        return await GitRepository.OpenAsync(repoPath, Context);
    }

    /// <summary>
    /// Resolves a full 40-char commit OID hex string to its <see cref="GitTree"/>.
    /// </summary>
    protected static async Task<GitTree> ResolveTreeAsync(GitRepository repo, string commitOidHex)
    {
        var oid = GitOid.Parse(commitOidHex.AsSpan(), repo.ObjectFormat);
        Commit commit = (await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");
        return (await repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException($"tree for {commitOidHex} not found");
    }
}

/// <summary>
/// Aggregates status entry counts, mirroring libgit2's
/// <c>status_entry_counts</c> + <c>cb_status__normal</c>
/// (<c>tests/libgit2/status/status_helpers.c</c>). Validates per-entry
/// path + status flags in sorted order.
/// </summary>
internal static class StatusCounter
{
    /// <summary>
    /// Asserts that <paramref name="list"/> has exactly <paramref name="expected"/>
    /// entries, in order, matching each entry's path and status flags.
    /// Matches <c>cb_status__normal</c> + <c>check_status</c>.
    /// </summary>
    public static void AssertEntries(
        GitStatusList list,
        string[] expectedPaths,
        GitStatusFlags[] expectedStatuses)
    {
        Assert.Equal(expectedPaths.Length, expectedStatuses.Length);
        Assert.Equal(expectedPaths.Length, list.EntryCount);

        int i = 0;
        foreach (GitStatusEntry entry in list.Entries)
        {
            Assert.True(i < expectedPaths.Length,
                $"too many status entries: got at least {i + 1}, expected {expectedPaths.Length}");
            Assert.Equal(expectedPaths[i], entry.Path.ToUtf8String());
            Assert.Equal(expectedStatuses[i], entry.Status);
            i++;
        }
    }

    /// <summary>
    /// Counts entries by status flag (e.g. how many are WorkdirNew, Ignored, etc.).
    /// Useful for statistical assertions without strict ordering.
    /// </summary>
    public static Dictionary<GitStatusFlags, int> CountByStatus(
        GitStatusList list)
    {
        var counts = new Dictionary<GitStatusFlags, int>();
        foreach (GitStatusEntry entry in list.Entries)
        {
            counts.TryGetValue(entry.Status, out int c);
            counts[entry.Status] = c + 1;
        }

        return counts;
    }
}

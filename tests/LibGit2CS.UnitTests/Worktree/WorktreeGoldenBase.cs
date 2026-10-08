using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Worktree;

/// <summary>
/// Shared base for worktree golden tests. Creates a fresh repo with 2
/// commits (deterministic content) matching the
/// <c>generate-goldens.sh</c> worktree setup. Provides path
/// normalization for golden comparison (replaces absolute temp paths with
/// a placeholder).
/// </summary>
public abstract partial class WorktreeGoldenBase : IDisposable
{
    private readonly string _tempDir;
    private readonly GitContext _context = new();
    protected GitContext Context => _context;

    protected WorktreeGoldenBase()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_WorktreeGolden_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public string TempDir => _tempDir;

    public void Dispose()
    {
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Creates a fresh repo matching the golden script's
    /// <c>worktree_setup_repo</c>: 2 commits with deterministic content.
    /// After setup, HEAD is at the second commit.
    /// </summary>
    protected async ValueTask<GitRepository> CreateMainRepo()
    {
        string repoPath = Path.Combine(_tempDir, "main");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, Context);
        GitOid firstOid = await WriteCommit(repo, "file1.txt", "hello\n", "first commit", 1, null);
        await WriteCommit(repo, "file2.txt", "world\n", "second commit", 2, firstOid);
        return repo;
    }

    /// <summary>
    /// Loads the byte-exact golden output for the given worktree case, with
    /// absolute temp paths normalized to <c>&lt;TEMP&gt;</c>. The golden
    /// files are generated with the script's temp dir (e.g.
    /// <c>/tmp/tmp.XXXXX</c>); both the golden and the C# output are
    /// normalized so only the structure (not the temp dir) is compared.
    /// </summary>
    protected static string LoadExpected(string caseName)
    {
        string raw = FixtureLoader.LoadText($"Fixtures/worktree/expected/{caseName}.txt");
        return NormalizeTempPaths(raw);
    }

    /// <summary>
    /// Normalizes absolute paths in the output to <c>&lt;TEMP&gt;</c>.
    /// The golden files use paths like <c>/tmp/tmp.XXXXX/main</c>; the C#
    /// test uses paths like <c>/tmp/LibGit2CS_WorktreeGolden_XXXXXXXX/main</c>
    /// on POSIX or <c>C:/Users/.../Temp/2/LibGit2CS_WorktreeGolden_XXXXXXXX/main</c>
    /// on Windows. This replaces any such prefix with <c>&lt;TEMP&gt;</c> so
    /// only the structure (not the temp dir) is compared.
    /// </summary>
    protected static string NormalizeTempPaths(string output)
    {
        // Replace /tmp/tmp.XXXXXX (golden script temp dirs).
        string normalized = TmpTempDirPattern().Replace(output, "<TEMP>");
        // Match the test directory under any temporary root, including macOS
        // /var/folders and canonical paths reached through symlinked roots.
        return WorktreeGoldenTempDirPattern().Replace(normalized, "worktree <TEMP>");
    }

    [GeneratedRegex(@"/tmp/tmp\.[A-Za-z0-9]+")]
    private static partial Regex TmpTempDirPattern();

    [GeneratedRegex(@"(?m)^worktree [^\r\n]*?/LibGit2CS_WorktreeGolden_[A-Za-z0-9]+(?=/)")]
    private static partial Regex WorktreeGoldenTempDirPattern();

    private static async Task<GitOid> WriteCommit(GitRepository repo, string fileName, string content, string message, int day, GitOid? parent)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync();
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        var sig = new GitSignature("Test User", "test@example.com", new GitTime(946684800 + (day - 1) * 86400, 0));
        GitOid[] parents = parent is { } p ? [p] : Array.Empty<GitOid>();
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = message + "\n",
            UpdateRef = "refs/heads/master",
            Parents = parents,
        });
    }
}

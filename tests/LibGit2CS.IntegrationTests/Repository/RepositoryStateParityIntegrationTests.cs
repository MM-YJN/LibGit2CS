using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// End-to-end tests for the RepositoryState parity behavior in
/// libgit2 1.9.4 — the C
/// enum values and check order (repository.h:916-928, repository.c:3699-3737).
/// </summary>
public sealed class RepositoryStateParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryStateParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StateParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task State_MatchesCEnum()
    {
        string path = Path.Combine(_tempDir, "r");
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        string gitdir = repo.Path;

        Assert.Equal(0, (int)repo.State);

        await File.WriteAllTextAsync(Path.Combine(gitdir, "MERGE_HEAD"), string.Empty, ct);
        Assert.Equal(1, (int)repo.State);
        File.Delete(Path.Combine(gitdir, "MERGE_HEAD"));

        Directory.CreateDirectory(Path.Combine(gitdir, "rebase-apply"));
        await File.WriteAllTextAsync(Path.Combine(gitdir, "rebase-apply", "applying"), string.Empty, ct);
        Assert.Equal(10, (int)repo.State);
        File.Delete(Path.Combine(gitdir, "rebase-apply", "applying"));
        Assert.Equal(11, (int)repo.State);
        Directory.Delete(Path.Combine(gitdir, "rebase-apply"), recursive: true);

        Directory.CreateDirectory(Path.Combine(gitdir, "rebase-merge"));
        await File.WriteAllTextAsync(Path.Combine(gitdir, "rebase-merge", "interactive"), string.Empty, ct);
        Assert.Equal(8, (int)repo.State);
    }
}

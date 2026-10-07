using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Revwalk;

// revparse
// '^{/pattern}' compiled attacker-controlled patterns with .NET's default
// InfiniteMatchTimeout — a pathological pattern like "(a+)+$" against a
// commit message whose content ends in a long run of 'a' causes
// catastrophic backtracking (verified: 5.3 s for 27 chars). Upstream
// PCRE2 enforces a default match-step limit (regexp.c), so the managed
// port must bound each match too.
public sealed class RevparseGrepReDoSRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RevparseGrepReDoSRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_RevparseGrep_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task RevparseGrep_PathologicalPattern_Terminates()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // A commit whose message ends in a long run of 'a' followed by a
        // non-matching char — "(a+)+$" must partition the run, and every
        // partition fails the '$' anchor → catastrophic backtracking on an
        // unbounded regex engine.
        string run = new('a', 40);
        string message = $"subject\n{run}!";
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "f.txt"), "x\n",
            cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeId = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = "HEAD",
        }, TestContext.Current.CancellationToken);

        // Run on a pool thread with a hard guard so a regression (infinite
        // backtracking) surfaces as a TimeoutException — a test failure —
        // instead of hanging the runner.
        Task<GitObject?> revparse = Task.Run(() =>
            repo.RevparseSingleAsync("HEAD^{/(a+)+$}", TestContext.Current.CancellationToken));

        GitObject? result;
        GitException? gitEx = null;
        try
        {
            result = await revparse.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("revparse '^{/(a+)+$}' did not terminate — catastrophic backtracking.");
            return;
        }
        catch (GitException ex)
        {
            result = null;
            gitEx = ex;
        }

        // Fixed behavior: the match is bounded. The pattern does not match
        // this message (the trailing '!' breaks the '$' anchor), so the
        // outcome is either NotFound, or InvalidSpec when the 1-second
        // match budget expires first — never an unbounded hang.
        if (gitEx is not null)
        {
            Assert.True(
                gitEx.Code is GitErrorCode.NotFound or GitErrorCode.InvalidSpec,
                $"unexpected error {gitEx.Code}: {gitEx.Message}");
        }
        else
        {
            Assert.True(result is null, "pathological pattern must not match");
        }
    }
}

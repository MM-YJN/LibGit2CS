using LibGit2CS.Core;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.IntegrationTests.Status;

/// <summary>
/// Integration tests for the status/ignore parity behavior in
/// libgit2 1.9.4. IgnoreContext depth accounting undercounts .gitignore-less directories.
/// Layout under test: the repo root has NO <c>.gitignore</c>;
/// <c>x/</c> and <c>x/mid/</c> have none; <c>x/mid/deep/</c> and
/// <c>z/</c> DO have <c>.gitignore</c> files. C increments the ignore
/// depth per directory walked unconditionally (ignore.c:268-273,
/// 378-387), so each <c>PopDir</c> truncates exactly one level and every
/// sibling directory's <c>.gitignore</c> is found. Counting only
/// .gitignore-bearing directories meant that after leaving a
/// .gitignore-less directory the accumulated path was stale and the later
/// directories' rules never applied (their ignored files were reported
/// untracked).
/// </summary>
public sealed class IgnoreContextDepthParityIntegrationTests
{
    private static async Task<string> MakeRepoAsync(CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-ignore-depth-int-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "x", "mid", "deep"));
        Directory.CreateDirectory(Path.Combine(workdir, "z"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "x", "mid", "deep", "secret.txt"), "secret\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "x", "mid", "deep", ".gitignore"), "secret.txt\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "x", "mid", "keep.txt"), "keep\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "z", "vault.txt"), "vault\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "z", ".gitignore"), "vault.txt\n", ct);
        return path;
    }

    /// <summary>
    /// After walking .gitignore-less <c>x/</c> and <c>x/mid/</c>, the
    /// <c>.gitignore</c> files in <c>x/mid/deep/</c> and the sibling
    /// <c>z/</c> must still apply: <c>secret.txt</c> and <c>vault.txt</c>
    /// are IGNORED, <c>keep.txt</c> is UNTRACKED.
    /// </summary>
    [Fact]
    public async Task Status_MultiLevelGitignoreLessDirs_SiblingRulesApply()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await MakeRepoAsync(ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitStatusList status = await repo.StatusNewAsync(
                new GitStatusOptions
                {
                    Flags = GitStatusFlags.IncludeIgnored | GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
                },
                ct);

            var byPath = new Dictionary<string, GitStatusFlags>();
            for (int i = 0; i < status.EntryCount; i++)
            {
                GitStatusEntry entry = status.GetEntry(i);
                byPath[entry.Path.ToUtf8String()] = entry.Status;
            }

            Assert.True(byPath.TryGetValue("x/mid/deep/secret.txt", out GitStatusFlags deepStatus), "x/mid/deep/secret.txt must be in the status list");
            Assert.True((deepStatus & GitStatusFlags.Ignored) != 0, $"expected Ignored, got {deepStatus}");

            Assert.True(byPath.TryGetValue("z/vault.txt", out GitStatusFlags vaultStatus), "z/vault.txt must be in the status list");
            Assert.True((vaultStatus & GitStatusFlags.Ignored) != 0, $"expected Ignored, got {vaultStatus}");

            Assert.True(byPath.TryGetValue("x/mid/keep.txt", out GitStatusFlags keepStatus), "x/mid/keep.txt must be in the status list");
            Assert.True((keepStatus & GitStatusFlags.WorkdirNew) != 0, $"expected untracked, got {keepStatus}");
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

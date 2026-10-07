using LibGit2CS.Core;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Regression tests for the status/ignore parity behavior in
/// libgit2 1.9.4: IgnoreContext depth accounting must count every directory
/// walked.
///
/// C increments <c>ign-&gt;depth</c> per directory walked — unconditionally
/// (ignore.c:268-273 <c>push_one_ignore</c>, 378-387
/// <c>git_ignore__push_dir</c>) — even when the directory has no
/// <c>.gitignore</c>. Otherwise <c>PopDir</c>'s truncation bottoms out one
/// level early, so sibling directories' <c>.gitignore</c> files would be
/// looked up from stale paths and their rules would never apply (ignored
/// files reported untracked).
///
/// Layout under test: root has NO <c>.gitignore</c>,
/// <c>a/</c> has none, <c>b/</c> HAS one ignoring <c>secret.txt</c>.
/// </summary>
public sealed class IgnoreContextDepthParityTests
{
    private static async Task<string> MakeRepoAsync(CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-ignore-depth-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "a"));
        Directory.CreateDirectory(Path.Combine(workdir, "b"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "a", "foo.txt"), "foo\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "b", "secret.txt"), "secret\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "b", ".gitignore"), "secret.txt\n", ct);
        return path;
    }

    [Fact]
    public async Task Status_NoRootGitignore_SiblingDirGitignoreApplies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await MakeRepoAsync(ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            using GitStatusList status = await repo.StatusNewAsync(
                new GitStatusOptions { Flags = GitStatusFlags.IncludeIgnored | GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs },
                ct);

            var byPath = new Dictionary<string, GitStatusFlags>();
            for (int i = 0; i < status.EntryCount; i++)
            {
                GitStatusEntry entry = status.GetEntry(i);
                byPath[entry.Path.ToUtf8String()] = entry.Status;
            }

            // b/secret.txt is matched by b/.gitignore — C reports IGNORED.
            // A depth undercount would make the walk probe a/b/.gitignore
            // and report the file UNTRACKED (and b/.gitignore itself
            // untracked).
            Assert.True(byPath.TryGetValue("b/secret.txt", out GitStatusFlags secretStatus), "b/secret.txt must be in the status list");
            Assert.True((secretStatus & GitStatusFlags.Ignored) != 0, $"expected Ignored, got {secretStatus}");

            Assert.True(byPath.TryGetValue("a/foo.txt", out GitStatusFlags fooStatus), "a/foo.txt must be in the status list");
            Assert.True((fooStatus & GitStatusFlags.WorkdirNew) != 0, $"expected Untracked, got {fooStatus}");
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

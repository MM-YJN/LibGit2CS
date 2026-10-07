using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

namespace LibGit2CS.UnitTests.Stash;

/// <summary>
/// ASCII-trim parity: C's git_str_rtrim (stash.c:745, rebase.c:158,
/// read_gitfile, loose symref reads) trims ONLY ASCII whitespace — trailing
/// Unicode whitespace (e.g. U+00A0 NO-BREAK SPACE) is preserved in the
/// written output.
/// </summary>
public sealed class StashAsciiTrimParityTests
{
    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    [Fact]
    public async Task StashSave_TrailingNonAsciiSpace_KeptInReflogMessage()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashAscii_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

            GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "one\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder bld = repo.NewTreeBuilder();
            await bld.InsertAsync("a.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree = await bld.WriteAsync(CancellationToken.None);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = tree,
                Parents = [],
                Author = TestSig(),
                Committer = TestSig(),
                Message = "c\n",
                UpdateRef = "refs/heads/master",
            }, cancellationToken: TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "a.txt"), "changed\n", cancellationToken: TestContext.Current.CancellationToken);

            // C (stash.c:745): git_str_rtrim is ASCII-only — the trailing
            // U+00A0 stays in the refs/stash reflog message.
            await repo.StashSaveAsync(TestSig(), "wip\u00A0", GitStashFlags.Default, TestContext.Current.CancellationToken);

            GitRefLog reflog = (await repo.ReferenceReadLogAsync("refs/stash", TestContext.Current.CancellationToken))!;
            Assert.Equal(1, reflog.EntryCount);
            Assert.EndsWith("\u00A0", reflog[0].Message);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

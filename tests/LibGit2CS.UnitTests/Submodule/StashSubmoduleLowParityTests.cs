using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

namespace LibGit2CS.UnitTests.Submodule;

/// <summary> Parity tests for stash/rebase/submodule/worktree: (mailmap name trimming uses the ASCII whitespace set) and (submodule Set*
/// sentinel values throw GitException, not ArgumentException). Expectations are C-verified against libgit2 1.9.4 (mailmap.c:120, 137; parse.c:65-71;
/// submodule.c:1159-1173). </summary>
public sealed class StashSubmoduleLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public StashSubmoduleLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashSubmoduleLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── mailmap names keep non-ASCII trailing whitespace ───────

    [Fact]
    public void Mailmap_RealName_TrailingNbspPreserved()
    {
        // C (mailmap.c:120, parse.c:65-71): names are rtrimmed with git_str_rtrim (ASCII git__isspace set only) — a trailing NBSP is part of the name.
        // string.TrimEnd removed it.
        const string name = "John Doe\u00A0";
        var mm = GitMailmap.FromBuffer($"{name} <jd@example.com>\n");

        MailmapEntry? entry = mm.Lookup(name: null, email: "jd@example.com");

        Assert.NotNull(entry);
        Assert.Equal(name, entry.RealName);
    }

    [Fact]
    public void Mailmap_ReplaceName_TrailingNbspPreserved()
    {
        // C (mailmap.c:137): the replace name is also ASCII-rtrimmed.
        // "Jane Doe\u00A0" must be preserved so the two-name lookup matches.
        const string replaceName = "Jane Doe\u00A0";
        var mm = GitMailmap.FromBuffer(
            $"Real Person <real@example.com> {replaceName} <jd@example.com>\n");

        MailmapEntry? entry = mm.Lookup(name: replaceName, email: "jd@example.com");

        Assert.NotNull(entry);
        Assert.Equal("Real Person", entry.RealName);
    }

    [Fact]
    public void Mailmap_NonAsciiWhitespaceBeforeAngle_IsNameContent()
    {
        // C: whitespace skipping between the name and '<' uses the ASCII set, so "\u00A0" before '<' is part of the name.
        const string name = "John Doe\u00A0";
        var mm = GitMailmap.FromBuffer($"{name} <jd@example.com>\n");

        MailmapEntry? entry = mm.Lookup(name: null, email: "jd@example.com");

        Assert.NotNull(entry);
        Assert.Equal(name, entry.RealName);
    }

    // ── submodule Set* sentinel values ─────────────────────────

    [Fact]
    public async Task SetIgnore_Unspecified_ThrowsGitException()
    {
        // C (submodule.c:1159-1173): write_mapped_var → GIT_ERROR_SUBMODULE "invalid value for ignore".
        string repoDir = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitSubmodule.SetIgnoreAsync(repo, "test", SubmoduleIgnore.Unspecified, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid value for ignore", ex.Message);
        Assert.Equal(GitErrorCategory.Submodule, ex.Category);
    }

    [Fact]
    public async Task SetUpdate_Default_ThrowsGitException()
    {
        string repoDir = Path.Combine(_tempDir, "repo2");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitSubmodule.SetUpdateAsync(repo, "test", SubmoduleUpdateStrategy.Default, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid value for update", ex.Message);
        Assert.Equal(GitErrorCategory.Submodule, ex.Category);
    }

    [Fact]
    public async Task SetFetchRecurse_Unspecified_ThrowsGitException()
    {
        string repoDir = Path.Combine(_tempDir, "repo3");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitSubmodule.SetFetchRecurseAsync(repo, "test", (SubmoduleRecurse)99, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid value for fetchRecurseSubmodules", ex.Message);
        Assert.Equal(GitErrorCategory.Submodule, ex.Category);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Blame;

/// <summary> Parity tests for <c>mailmap.file</c> config-value <c>~</c> expansion: C expands a leading <c>~</c> to the home directory via
/// <c>git_config__parse_path</c>. </summary>
public sealed class MailmapTildePathParityTests : IAsyncDisposable
{
    private GitRepository? _repo;
    private string? _extracted;
    private string? _fakeHome;

    public async ValueTask DisposeAsync()
    {
        if (_repo is not null)
        {
            await _repo.DisposeAsync();
        }
        if (_extracted is not null)
        {
            try
            {
                Directory.Delete(_extracted, recursive: true);
            }
            catch (IOException) { }
        }
        if (_fakeHome is not null)
        {
            try
            {
                Directory.Delete(_fakeHome, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private string FakeHome
    {
        get
        {
            _fakeHome ??= Path.Combine(Path.GetTempPath(), "LibGit2CS_MailmapHome_" + Guid.NewGuid().ToString("N")[..8]);
            return _fakeHome;
        }
    }

    private async ValueTask<(GitRepository Repo, GitContext Ctx)> OpenMailmapRepoWithHomeAsync()
    {
        Directory.CreateDirectory(FakeHome);
        _extracted = FixtureLoader.ExtractTreeToTemp("Fixtures.blame.mailmap.zip");
        string repoPath = Path.Combine(_extracted, "mailmap");

        var ctx = new GitContext();
        ctx.Env["HOME"] = FakeHome;
        ctx.Dirs.Reset();

        _repo = await GitRepository.OpenAsync(repoPath, ctx);
        return (_repo, ctx);
    }

    /// <summary>
    /// mailmap.file = ~/name — C reads $HOME/name (config.c
    /// git_config__parse_path).
    /// </summary>
    [Fact]
    public async Task Mailmap_FileConfig_TildeExpandsToHome()
    {
        // Override file in the fake home directory.
        const string overrideName = "Tilde Override";
        string overridePath = Path.Combine(FakeHome, "mailmap_tilde_override");
        Directory.CreateDirectory(FakeHome);
        await File.WriteAllTextAsync(
            overridePath,
            $"{overrideName} <phil@company.xx>\n",
            cancellationToken: TestContext.Current.CancellationToken);

        (GitRepository repo, _) = await OpenMailmapRepoWithHomeAsync();

        await repo.Config.SetStringAsync("mailmap.file", "~/mailmap_tilde_override", cancellationToken: TestContext.Current.CancellationToken);

        using GitMailmap mm = await repo.MailmapFromRepositoryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The override entry must win over the fixture .mailmap's
        // "Phil Hill <phil@company.xx>".
        (string? name, string? email) = mm.Resolve("Phil Hill", "phil@company.xx");
        Assert.Equal(overrideName, name);
        Assert.Equal("phil@company.xx", email);
    }

    /// <summary>
    /// mailmap.file = "~" alone expands to $HOME itself (C passes NULL to
    /// git_sysdir_expand_homedir_file — no "/." suffix). C then tries to read
    /// the home *directory* and silently fails (EISDIR, errors ignored), so
    /// no entries are loaded.
    /// </summary>
    [Fact]
    public async Task Mailmap_FileConfig_TildeAlone_IsHomeDirectory()
    {
        (GitRepository repo, _) = await OpenMailmapRepoWithHomeAsync();

        // "~" alone must resolve to the home directory itself — not $HOME/. —
        // matching git_config__parse_path (config.c:926-933).
        await repo.Config.SetStringAsync("mailmap.file", "~", cancellationToken: TestContext.Current.CancellationToken);
        string? expanded = (await repo.Config.GetPathAsync("mailmap.file", cancellationToken: TestContext.Current.CancellationToken))?.ToFileSystemString();
        Assert.Equal(FakeHome, expanded);

        // The home directory is not a readable mailmap file: C ignores the
        // read error, so the fixture .mailmap entries stay in effect.
        using GitMailmap mm = await repo.MailmapFromRepositoryAsync(cancellationToken: TestContext.Current.CancellationToken);
        (string? name, string? email) = mm.Resolve("Phil Hill", "phil@company.xx");
        Assert.Equal("Phil Hill", name);
        Assert.Equal("phil@company.xx", email);
    }

    /// <summary>
    /// mailmap.file = ~user/... — C's git_config__parse_path errors
    /// ("retrieving a homedir by name is not supported"); git_config__get_path
    /// fails and the mailmap file is silently skipped.
    /// </summary>
    [Fact]
    public async Task Mailmap_FileConfig_TildeUser_IsSkipped()
    {
        (GitRepository repo, _) = await OpenMailmapRepoWithHomeAsync();

        await repo.Config.SetStringAsync("mailmap.file", "~someuser/mailmap", cancellationToken: TestContext.Current.CancellationToken);

        using GitMailmap mm = await repo.MailmapFromRepositoryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // No override applied: the fixture .mailmap's entry still resolves.
        (string? name, string? email) = mm.Resolve("Phil Hill", "phil@company.xx");
        Assert.Equal("Phil Hill", name);
        Assert.Equal("phil@company.xx", email);
    }
}

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Mailmap config-write tests, ported from libgit2's
/// <c>tests/libgit2/mailmap/parsing.c</c> (file_config, blob_config,
/// bare_blob_config).
/// </summary>
/// <remarks>
/// <para>
/// These tests verify that <see cref="GitMailmap.FromRepository"/> reads the
/// <c>mailmap.file</c> and <c>mailmap.blob</c> config keys (written via
/// <see cref="Config.Configuration.SetString"/>) and applies the override
/// file/blob on top of the workdir <c>.mailmap</c>.
/// </para>
/// <para>
/// The fixture <c>Fixtures/blame/mailmap.zip</c> (packaged from libgit2's
/// <c>tests/resources/mailmap/</c>) contains:
/// <list type="bullet">
/// <item><c>.mailmap</c> — 9-entry mailmap (the base).</item>
/// <item><c>file_override</c> — 2-entry mailmap (for <c>mailmap.file</c>).</item>
/// <item><c>blob_override</c> — 2-entry mailmap committed as a blob in HEAD's
/// tree (for <c>mailmap.blob</c>, referenced as <c>HEAD:blob_override</c>).</item>
/// <item><c>file.txt</c> — blame test file (not used here).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class MailmapConfigWriteTests : IAsyncDisposable
{
    private GitRepository? _repo;
    private string? _extracted;

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
    }

    /// <summary>Opens the mailmap fixture repo (non-bare).</summary>
    private async ValueTask<GitRepository> OpenMailmapRepoAsync()
    {
        _extracted = FixtureLoader.ExtractTreeToTemp("Fixtures.blame.mailmap.zip");
        string repoPath = Path.Combine(_extracted, "mailmap");
        _repo = await GitRepository.OpenAsync(repoPath, new GitContext());
        return _repo!;
    }

    /// <summary>Opens the mailmap fixture repo as bare.</summary>
    private async ValueTask<GitRepository> OpenMailmapBareRepoAsync()
    {
        // Open the .git directory directly as a bare repo.
        _extracted = FixtureLoader.ExtractTreeToTemp("Fixtures.blame.mailmap.zip");
        string gitPath = Path.Combine(_extracted, "mailmap", ".git");
        _repo = await GitRepository.OpenBareAsync(gitPath, new GitContext());
        return _repo!;
    }

    // ── mailmap.file config (parsing.c:178-195) ──────────────────────────

    [Fact]
    public async Task Mailmap_FileConfig_OverridesWorkdirMailmap()
    {
        // Matches libgit2's test_mailmap_parsing__file_config: set
        // mailmap.file to point at file_override, then load the mailmap
        // from the repo. The file_override entries should appear on top of
        // the .mailmap entries.
        await using GitRepository repo = await OpenMailmapRepoAsync();

        string fileOverridePath = Path.Combine(repo.Workdir!, "file_override");
        Assert.True(File.Exists(fileOverridePath));
        await repo.Config.SetStringAsync("mailmap.file", fileOverridePath, cancellationToken: TestContext.Current.CancellationToken);

        using GitMailmap mm = await repo.MailmapFromRepositoryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // file_override content:
        //   File Override <phil@company.xx>
        //   Other Name <fileoverridename@company.xx>
        // The .mailmap has "Phil Hill <phil@company.xx>" — the file_override
        // replaces "Phil Hill" with "File Override" for phil@company.xx.
        AssertResolved(mm, "File Override", "phil@company.xx",
            "Phil Hill", "phil@company.xx");

        // file_override also adds "Other Name" for fileoverridename@company.xx
        // (a new email not in .mailmap).
        AssertResolved(mm, "Other Name", "fileoverridename@company.xx",
            "unknown", "fileoverridename@company.xx");

        // Entries from .mailmap that aren't overridden should still resolve.
        AssertResolved(mm, "Some Dude", "some@dude.xx",
            "nick1", "bugs@company.xx");
    }

    // ── mailmap.blob config (parsing.c:213-230) ──────────────────────────

    [Fact]
    public async Task Mailmap_BlobConfig_OverridesWorkdirMailmap()
    {
        // Matches libgit2's test_mailmap_parsing__blob_config: set
        // mailmap.blob to "HEAD:blob_override", then load the mailmap from
        // the repo. The blob_override entries should appear on top of the
        // .mailmap entries.
        await using GitRepository repo = await OpenMailmapRepoAsync();

        await repo.Config.SetStringAsync("mailmap.blob", "HEAD:blob_override", cancellationToken: TestContext.Current.CancellationToken);

        using GitMailmap mm = await repo.MailmapFromRepositoryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // blob_override content:
        //   Blob Override <phil@company.xx>
        //   Other Name <bloboverridename@company.xx>
        // The .mailmap has "Phil Hill <phil@company.xx>" — the blob_override
        // replaces "Phil Hill" with "Blob Override" for phil@company.xx.
        AssertResolved(mm, "Blob Override", "phil@company.xx",
            "Phil Hill", "phil@company.xx");

        // blob_override also adds "Other Name" for bloboverridename@company.xx.
        AssertResolved(mm, "Other Name", "bloboverridename@company.xx",
            "unknown", "bloboverridename@company.xx");

        // Entries from .mailmap that aren't overridden should still resolve.
        AssertResolved(mm, "Some Dude", "some@dude.xx",
            "nick1", "bugs@company.xx");
    }

    // ── bare repo + mailmap.blob (parsing.c:249-269) ─────────────────────

    [Fact]
    public async Task Mailmap_BareRepo_BlobConfig_OnlyBlobEntries()
    {
        // Matches libgit2's test_mailmap_parsing__bare_blob_config: open the
        // repo as bare, set mailmap.blob to "HEAD:blob_override". A bare repo
        // has no workdir, so .mailmap is not loaded — only the blob entries
        // appear (2 entries, not 9+2=11).
        await using GitRepository repo = await OpenMailmapBareRepoAsync();
        Assert.True(repo.IsBare);

        await repo.Config.SetStringAsync("mailmap.blob", "HEAD:blob_override", cancellationToken: TestContext.Current.CancellationToken);

        using GitMailmap mm = await repo.MailmapFromRepositoryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Only the 2 blob_override entries should resolve.
        AssertResolved(mm, "Blob Override", "phil@company.xx",
            "Phil Hill", "phil@company.xx");
        AssertResolved(mm, "Other Name", "bloboverridename@company.xx",
            "unknown", "bloboverridename@company.xx");

        // An email only in .mailmap (not in blob_override) should NOT resolve.
        // "nick1 <bugs@company.xx>" → would resolve to "Some Dude" if .mailmap
        // were loaded, but bare repos skip .mailmap, so it stays unchanged.
        AssertResolved(mm, "nick1", "bugs@company.xx",
            "nick1", "bugs@company.xx");
    }

    // ── Helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that <paramref name="mm"/> resolves
    /// <c>(<paramref name="origName"/>, <paramref name="origEmail"/>)</c> to
    /// <c>(<paramref name="expName"/>, <paramref name="expEmail"/>)</c>.
    /// </summary>
    private static void AssertResolved(
        GitMailmap mm,
        string expName, string expEmail,
        string origName, string origEmail)
    {
        (string? name, string? email) = mm.Resolve(origName, origEmail);
        Assert.Equal(expName, name);
        Assert.Equal(expEmail, email);
    }
}

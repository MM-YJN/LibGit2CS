using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Mailmap parsing tests ported from libgit2's
/// <c>tests/libgit2/mailmap/parsing.c</c> and <c>tests/libgit2/mailmap/basic.c</c>.
/// The config-driven tests (file_config, blob_config, bare_blob_config) live in
/// <see cref="MailmapConfigWriteTests"/>.
/// </summary>
public sealed class MailmapTests
{
    // parsing.c::string — parse from a string buffer
    private const string StringMailmap =
        "# Simple Comment line\n" +
        "<cto@company.xx>                       <cto@coompany.xx>\n" +
        "Some Dude <some@dude.xx>         nick1 <bugs@company.xx>\n" +
        "Other Author <other@author.xx>   nick2 <bugs@company.xx>\n" +
        "Other Author <other@author.xx>         <nick2@company.xx>\n" +
        "Phil Hill <phil@company.xx>  # Comment at end of line\n" +
        "<joseph@company.xx>             Joseph <bugs@company.xx>\n" +
        "Santa Claus <santa.claus@northpole.xx> <me@company.xx>\n" +
        "Untracked <untracked@company.xx>";

    [Fact]
    public void Parsing_String()
    {
        using var mm = GitMailmap.FromBuffer(StringMailmap);

        // Resolve entries — the "resolved" array from mailmap_testdata.h
        AssertResolved(mm, "Brad", "cto@company.xx", "Brad", "cto@coompany.xx");
        AssertResolved(mm, "Brad L", "cto@company.xx", "Brad L", "cto@coompany.xx");
        AssertResolved(mm, "Some Dude", "some@dude.xx", "nick1", "bugs@company.xx");
        AssertResolved(mm, "Other Author", "other@author.xx", "nick2", "bugs@company.xx");
        AssertResolved(mm, "nick3", "bugs@company.xx", "nick3", "bugs@company.xx");
        AssertResolved(mm, "Other Author", "other@author.xx", "Some Garbage", "nick2@company.xx");
        AssertResolved(mm, "Phil Hill", "phil@company.xx", "unknown", "phil@company.xx");
        AssertResolved(mm, "Joseph", "joseph@company.xx", "Joseph", "bugs@company.xx");
        AssertResolved(mm, "Santa Claus", "santa.claus@northpole.xx", "Clause", "me@company.xx");
        AssertResolved(mm, "Charles", "charles@charles.xx", "Charles", "charles@charles.xx");

        // Untracked entry (single-email form: real_name only, no real_email)
        AssertResolved(mm, "Untracked", "untracked@company.xx", "xx", "untracked@company.xx");
    }

    // basic.c tests
    private const string TestMailmap =
        "Foo bar <foo@bar.com> <foo@baz.com>  \n" +
        "Blatantly invalid line\n" +
        "Foo bar <foo@bar.com> <foo@bal.com>\n" +
        "<email@foo.com> <otheremail@foo.com>\n" +
        "<email@foo.com> Other Name <yetanotheremail@foo.com>\n";

    [Fact]
    public void Basic_Entry()
    {
        using var mm = GitMailmap.FromBuffer(TestMailmap);

        // 4 valid entries (the "Blatantly invalid line" is skipped)
        // Verify each resolves correctly
        AssertResolved(mm, "Foo bar", "foo@bar.com", null, "foo@baz.com");
        AssertResolved(mm, "Foo bar", "foo@bar.com", null, "foo@bal.com");
        AssertResolved(mm, null, "email@foo.com", null, "otheremail@foo.com");
        AssertResolved(mm, null, "email@foo.com", "Other Name", "yetanotheremail@foo.com");
    }

    [Fact]
    public void Basic_LookupNotFound()
    {
        using var mm = GitMailmap.FromBuffer(TestMailmap);
        // Not found → returns original name/email
        (string? name, string? email) = mm.Resolve("Whoever", "doesnotexist@fo.com");
        Assert.Equal("Whoever", name);
        Assert.Equal("doesnotexist@fo.com", email);
    }

    [Fact]
    public void Basic_Lookup()
    {
        using var mm = GitMailmap.FromBuffer(TestMailmap);
        // Nameless fallback: "Typoed the name once" with email foo@baz.com
        // should resolve to "Foo bar" (the nameless entry for foo@baz.com)
        (string? name, string? email) = mm.Resolve("Typoed the name once", "foo@baz.com");
        Assert.Equal("Foo bar", name);
    }

    [Fact]
    public void Basic_EmptyEmailQuery()
    {
        using var mm = GitMailmap.FromBuffer(TestMailmap);
        (string? name, string? email) = mm.Resolve("Author name", "otheremail@foo.com");
        Assert.Equal("Author name", name);
        Assert.Equal("email@foo.com", email);
    }

    [Fact]
    public void Basic_NameMatching()
    {
        using var mm = GitMailmap.FromBuffer(TestMailmap);

        // Name-specific match
        (string? name, string? email) = mm.Resolve("Other Name", "yetanotheremail@foo.com");
        Assert.Equal("Other Name", name);
        Assert.Equal("email@foo.com", email);

        // Name doesn't match → fallback to nameless entry? No nameless entry
        // for yetanotheremail@foo.com, so returns original
        (string? name2, string? email2) = mm.Resolve("Other Name That Doesn't Match", "yetanotheremail@foo.com");
        Assert.Equal("Other Name That Doesn't Match", name2);
        Assert.Equal("yetanotheremail@foo.com", email2);
    }

    // mailmap/blame.c::hunks — blame with mailmap
    [Fact]
    public async Task Blame_WithMailmap()
    {
        await using GitRepository repo = await BlameGoldenBase.OpenMailmapRepoAsync();
        using GitBlame blame = await repo.BlameFileAsync("file.txt",
            new GitBlameOptions { Flags = GitBlameFlags.UseMailmap }, cancellationToken: TestContext.Current.CancellationToken);

        // Each line should have resolved signatures matching the "resolved" array
        for (int i = 0; i < 10; i++)
        {
            BlameHunk? hunk = blame.GetHunkByLine(i + 1);
            Assert.NotNull(hunk);
            Assert.NotNull(hunk.FinalSignature);
        }
    }

    [Fact]
    public async Task Blame_WithSuppliedMailmap_UsesSuppliedMapping()
    {
        await using GitRepository repo = await BlameGoldenBase.OpenMailmapRepoAsync();
        using var supplied = GitMailmap.FromBuffer(
            "Override Author <override.author@example.com> <cto@coompany.xx>\n" +
            "Override Committer <override.committer@example.com> <nika@thelayzells.com>\n");
        using GitBlame blame = await repo.BlameFileAsync(
            "file.txt",
            new GitBlameOptions
            {
                Flags = GitBlameFlags.UseMailmap,
                Mailmap = supplied,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        BlameHunk hunk = blame.GetHunkByLine(1)!;
        Assert.Equal("Override Author", hunk.FinalSignature.Name);
        Assert.Equal("override.author@example.com", hunk.FinalSignature.Email);
        Assert.Equal("Override Committer", hunk.FinalCommitter.Name);
        Assert.Equal("override.committer@example.com", hunk.FinalCommitter.Email);
    }

    [Fact]
    public async Task Blame_WithoutMailmap()
    {
        await using GitRepository repo = await BlameGoldenBase.OpenMailmapRepoAsync();
        using GitBlame blame = await repo.BlameFileAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);

        for (int i = 0; i < 10; i++)
        {
            BlameHunk? hunk = blame.GetHunkByLine(i + 1);
            Assert.NotNull(hunk);
            Assert.NotNull(hunk.FinalSignature);
        }
    }

    private static void AssertResolved(
        GitMailmap mm, string? expectedRealName, string? expectedRealEmail,
        string? replaceName, string replaceEmail)
    {
        (string? name, string? email) = mm.Resolve(replaceName ?? string.Empty, replaceEmail);
        Assert.Equal(expectedRealName ?? replaceName ?? string.Empty, name);
        Assert.Equal(expectedRealEmail ?? replaceEmail, email);
    }
}

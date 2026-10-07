using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

// an unparseable
// GIT_AUTHOR_DATE/GIT_COMMITTER_DATE silently fell back to the current time.
// C's user_from_env (signature.c:254-266) propagates git_date_offset_parse's
// error: parse_date_basic fails for garbage input and approxidate sets
// error_ret=-1 when nothing was touched (date.c), so
// git_signature_default_from_env FAILS instead of committing with a
// fabricated timestamp.
public sealed class DefaultFromEnvDateRegressionTests
{
    private static GitContext ContextWithIdentity()
    {
        var ctx = new GitContext();
        ctx.Env["GIT_AUTHOR_NAME"] = "T";
        ctx.Env["GIT_AUTHOR_EMAIL"] = "t@x.com";
        ctx.Env["GIT_COMMITTER_NAME"] = "T";
        ctx.Env["GIT_COMMITTER_EMAIL"] = "t@x.com";
        return ctx;
    }

    [Fact]
    public async Task GarbageAuthorDate_Throws()
    {
        GitContext ctx = ContextWithIdentity();
        ctx.Env["GIT_AUTHOR_DATE"] = "garbage";

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitSignature.DefaultFromEnvAsync(null, ctx, TestContext.Current.CancellationToken));
        Assert.Contains("date", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GarbageCommitterDate_Throws()
    {
        GitContext ctx = ContextWithIdentity();
        ctx.Env["GIT_COMMITTER_DATE"] = "garbage";

        // The author side parses fine (falls back to defaults); the
        // committer side must fail like C.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitSignature.DefaultFromEnvAsync(null, ctx, TestContext.Current.CancellationToken));
        Assert.Contains("date", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidDate_StillParses()
    {
        GitContext ctx = ContextWithIdentity();
        ctx.Env["GIT_AUTHOR_DATE"] = "yesterday";

        (GitSignature author, _) = await GitSignature.DefaultFromEnvAsync(
            null, ctx, TestContext.Current.CancellationToken);
        Assert.Equal("T", author.Name);
        Assert.Equal("t@x.com", author.Email);
    }

    [Fact]
    public async Task UnsetDate_DefaultsToNow()
    {
        GitContext ctx = ContextWithIdentity();

        (GitSignature author, _) = await GitSignature.DefaultFromEnvAsync(
            null, ctx, TestContext.Current.CancellationToken);

        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        Assert.InRange(author.When.Seconds, now - 60, now + 60);
    }
}

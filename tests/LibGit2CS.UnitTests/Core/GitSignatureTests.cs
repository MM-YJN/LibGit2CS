using System.Globalization;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class GitSignatureTests
{
    [Fact]
    public void Create_ValidNameEmail_ReturnsTrimmed()
    {
        var sig = GitSignature.Create("  Alice  ", "alice@example.com", new GitTime(0, 0));

        Assert.Equal("Alice", sig.Name);
        Assert.Equal("alice@example.com", sig.Email);
    }

    [Fact]
    public void Create_NameWithAngleBrackets_Throws()
    {
        Assert.Throws<FormatException>(() =>
            GitSignature.Create("<bad>", "a@b.com", new GitTime(0, 0)));
    }

    [Fact]
    public void Create_EmptyName_Throws()
    {
        Assert.Throws<FormatException>(() =>
            GitSignature.Create("   ", "a@b.com", new GitTime(0, 0)));
    }

    [Fact]
    public void Now_ProducesCurrentTime()
    {
        long before = DateTimeOffset.Now.ToUnixTimeSeconds();
        var sig = GitSignature.Now("Alice", "alice@example.com");
        long after = DateTimeOffset.Now.ToUnixTimeSeconds();

        Assert.InRange(sig.When.Seconds, before, after);
    }

    [Fact]
    public void FromBuffer_ValidFormat_ParsesCorrectly()
    {
        var sig = GitSignature.FromBuffer("A U Thor <author@example.com> 1461698037 +0200".AsSpan());

        Assert.Equal("A U Thor", sig.Name);
        Assert.Equal("author@example.com", sig.Email);
        Assert.Equal(1461698037, sig.When.Seconds);
        Assert.Equal(120, sig.When.OffsetMinutes);
    }

    [Fact]
    public void FromBuffer_NameWithAngleBracket_ParsesCorrectly()
    {
        // libgit2 finds the LAST <>, allowing '>' in names.
        var sig = GitSignature.FromBuffer("Weird > Name <user@example.com> 100 +0000".AsSpan());

        Assert.Equal("Weird > Name", sig.Name);
        Assert.Equal("user@example.com", sig.Email);
    }

    [Fact]
    public void FromBuffer_TrimsCrudFromName()
    {
        var sig = GitSignature.FromBuffer("  Alice  <alice@example.com> 100 +0000".AsSpan());

        Assert.Equal("Alice", sig.Name);
    }

    [Fact]
    public void FromBuffer_NegativeOffset_Parses()
    {
        var sig = GitSignature.FromBuffer("Bob <bob@example.com> 200 -0530".AsSpan());

        Assert.Equal(-330, sig.When.OffsetMinutes);
    }

    [Fact]
    public void FromBuffer_MalformedEmail_Throws()
    {
        Assert.Throws<FormatException>(() =>
            GitSignature.FromBuffer("No Email Here 100 +0000".AsSpan()));
    }

    [Fact]
    public void TryParse_WithHeader_RequiresMatch()
    {
        bool ok = GitSignature.TryParse(
            "author Alice <alice@example.com> 100 +0000\n".AsSpan(),
            out GitSignature? sig,
            out int consumed,
            "author ");

        Assert.True(ok);
        Assert.NotNull(sig);
        Assert.Equal("Alice", sig.Name);
        // Consumed includes the trailing newline.
        Assert.True(consumed > "author ".Length);
    }

    [Fact]
    public void TryParse_WithWrongHeader_Fails()
    {
        bool ok = GitSignature.TryParse(
            "committer Alice <alice@example.com> 100 +0000\n".AsSpan(),
            out _,
            out _,
            "author ");

        Assert.False(ok);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var sig = new GitSignature("Alice", "alice@example.com", new GitTime(1461698037, 120));

        string buffer = sig.ToString();

        Assert.Equal("Alice <alice@example.com> 1461698037 +0200", buffer);
    }

    // ── TryFormat: git_signature__writebuf form (Name <email> Seconds +HHMM) ──
    // The sign derivation comes from GitTime: (offset < 0 || sign == '-') ? '-' : '+',
    // so a preserved '-' re-serializes as '-0000' even for a zero/positive offset.

    [Theory]
    [InlineData("A U Thor", "author@example.com", 1461698037, 120, '\0', "A U Thor <author@example.com> 1461698037 +0200")]
    [InlineData("Bob", "bob@example.com", 200, -330, '\0', "Bob <bob@example.com> 200 -0530")]
    [InlineData("Dev", "dev@example.com", 0, 0, '\0', "Dev <dev@example.com> 0 +0000")]
    [InlineData("Dev", "dev@example.com", 0, 0, '-', "Dev <dev@example.com> 0 -0000")]
    [InlineData("Dev", "dev@example.com", 100, 120, '-', "Dev <dev@example.com> 100 -0200")]
    [InlineData("M", "m@x.io", 1, -1, '\0', "M <m@x.io> 1 -0001")]
    public void TryFormat_RendersNameEmailTimeOffset(string name, string email, long seconds, int offsetMinutes, char sign, string expected)
    {
        var sig = new GitSignature(name, email, new GitTime(seconds, offsetMinutes, sign));

        Span<char> buffer = new char[256];
        bool ok = sig.TryFormat(buffer, out int charsWritten, default, provider: null);

        Assert.True(ok);
        Assert.Equal(expected.Length, charsWritten);
        Assert.Equal(expected, new string(buffer.Slice(0, charsWritten)));
    }

    [Fact]
    public void TryFormat_ExactBufferSize_Succeeds()
    {
        var sig = new GitSignature("Alice", "alice@example.com", new GitTime(1461698037, 120));

        Span<char> buffer = new char["Alice <alice@example.com> 1461698037 +0200".Length];
        bool ok = sig.TryFormat(buffer, out int charsWritten, default, provider: null);

        Assert.True(ok);
        Assert.Equal(buffer.Length, charsWritten);
        Assert.Equal("Alice <alice@example.com> 1461698037 +0200", new string(buffer));
    }

    [Fact]
    public void TryFormat_BufferTooSmall_ReturnsFalseAndZeroCharsWritten()
    {
        var sig = new GitSignature("Alice", "alice@example.com", new GitTime(1461698037, 120));

        Span<char> buffer = new char["Alice <alice@example.com> 1461698037 +0200".Length - 1];
        bool ok = sig.TryFormat(buffer, out int charsWritten, default, provider: null);

        Assert.False(ok);
        Assert.Equal(0, charsWritten);
    }

    [Fact]
    public void TryFormat_IgnoresFormatAndProvider()
    {
        var sig = new GitSignature("Alice", "alice@example.com", new GitTime(1461698037, 120));

        Span<char> buffer = new char[256];
        bool ok = sig.TryFormat(buffer, out int charsWritten, "g".AsSpan(), CultureInfo.InvariantCulture);

        Assert.True(ok);
        Assert.Equal("Alice <alice@example.com> 1461698037 +0200", new string(buffer.Slice(0, charsWritten)));
    }

    [Theory]
    [InlineData("A U Thor", "author@example.com", 1461698037, 120, '\0', "A U Thor <author@example.com> 1461698037 +0200")]
    [InlineData("Bob", "bob@example.com", 200, -330, '\0', "Bob <bob@example.com> 200 -0530")]
    [InlineData("Dev", "dev@example.com", 0, 0, '\0', "Dev <dev@example.com> 0 +0000")]
    [InlineData("Dev", "dev@example.com", 0, 0, '-', "Dev <dev@example.com> 0 -0000")]
    [InlineData("Dev", "dev@example.com", 100, 120, '-', "Dev <dev@example.com> 100 -0200")]
    public void ToString_WithFormatAndProvider_RendersBufferForm(string name, string email, long seconds, int offsetMinutes, char sign, string expected)
    {
        var sig = new GitSignature(name, email, new GitTime(seconds, offsetMinutes, sign));

        Assert.Equal(expected, sig.ToString(format: null, formatProvider: null));
    }

    [Fact]
    public void ToString_Parameterless_MatchesToStringWithNullArgs()
    {
        var sig = new GitSignature("Alice", "alice@example.com", new GitTime(1461698037, -330));

        Assert.Equal(sig.ToString(format: null, formatProvider: null), sig.ToString());
    }

    [Fact]
    public void ToString_IgnoresFormatAndProvider()
    {
        var sig = new GitSignature("Alice", "alice@example.com", new GitTime(1461698037, 120));

        Assert.Equal("Alice <alice@example.com> 1461698037 +0200", sig.ToString("g", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("A U Thor <author@example.com> 1461698037 +0200")]
    [InlineData("Bob <bob@example.com> 200 -0530")]
    [InlineData("Dev <dev@example.com> 0 -0000")]
    public void RoundTrip_FromBufferAndBack(string buffer)
    {
        var sig = GitSignature.FromBuffer(buffer.AsSpan());

        Assert.Equal(buffer, sig.ToString());
    }

    [Fact]
    public void GitTime_FormatOffset_PositiveOffset()
    {
        var t = new GitTime(0, 120);

        Assert.Equal("+0200", t.FormatOffset());
    }

    [Fact]
    public void GitTime_FormatOffset_NegativeOffset()
    {
        var t = new GitTime(0, -330);

        Assert.Equal("-0530", t.FormatOffset());
    }

    [Fact]
    public void GitTime_FormatOffset_ZeroOffset()
    {
        var t = new GitTime(0, 0);

        Assert.Equal("+0000", t.FormatOffset());
    }

    [Fact]
    public async Task Default_ReadsFromConfig()
    {
        await using var config = new GitConfiguration(new GitContext());
        await config.AddBackendAsync(new MemoryConfigBackend("[user]\n\tname = Alice\n\temail = alice@example.com\n"), GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitSignature sig = await GitSignature.DefaultAsync(config, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Alice", sig.Name);
        Assert.Equal("alice@example.com", sig.Email);
    }

    [Fact]
    public async Task Default_MissingName_Throws()
    {
        await using var config = new GitConfiguration(new GitContext());
        await config.AddBackendAsync(new MemoryConfigBackend("[user]\n\temail = alice@example.com\n"), GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => GitSignature.DefaultAsync(config, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Default_MissingEmail_Throws()
    {
        await using var config = new GitConfiguration(new GitContext());
        await config.AddBackendAsync(new MemoryConfigBackend("[user]\n\tname = Alice\n"), GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => GitSignature.DefaultAsync(config, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task DefaultFromEnv_UsesEnvVars()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_AUTHOR_NAME"] = "EnvAuthor";
        ctx.Env["GIT_AUTHOR_EMAIL"] = "env@author.com";
        ctx.Env["GIT_COMMITTER_NAME"] = "EnvCommitter";
        ctx.Env["GIT_COMMITTER_EMAIL"] = "env@committer.com";

        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(config: null, ctx, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("EnvAuthor", author.Name);
        Assert.Equal("env@author.com", author.Email);
        Assert.Equal("EnvCommitter", committer.Name);
        Assert.Equal("env@committer.com", committer.Email);
    }

    [Fact]
    public async Task DefaultFromEnv_FallsBackToConfig()
    {
        using GitContext ctx = new();
        // Ensure the env vars are unset in this context's snapshot so the
        // config fallback path is exercised (regardless of what the process
        // environment happens to carry).
        ctx.Env["GIT_AUTHOR_NAME"] = null;
        ctx.Env["GIT_AUTHOR_EMAIL"] = null;

        await using var config = new GitConfiguration(ctx);
        await config.AddBackendAsync(new MemoryConfigBackend("[user]\n\tname = Bob\n\temail = bob@example.com\n"), GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        (GitSignature author, _) = await GitSignature.DefaultFromEnvAsync(config, ctx, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bob", author.Name);
        Assert.Equal("bob@example.com", author.Email);
    }

    [Fact]
    public async Task DefaultFromEnv_MissingBoth_Throws()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_AUTHOR_NAME"] = null;
        ctx.Env["GIT_AUTHOR_EMAIL"] = null;

        await Assert.ThrowsAsync<GitException>(() => GitSignature.DefaultFromEnvAsync(config: null, ctx, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }
}

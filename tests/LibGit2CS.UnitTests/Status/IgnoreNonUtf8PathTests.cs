using System.Text;

using LibGit2CS.IO;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Tests for the byte-faithful ignore engine. These pin
/// that a <c>.gitignore</c> with a non-UTF-8 pattern matches a non-UTF-8 file
/// path byte-exact (a string bridge would U+FFFD-replace the
/// invalid bytes on both the pattern and the path, corrupting the match).
/// Uses the <c>attr</c> fixture repo.
/// </summary>
public class IgnoreNonUtf8PathTests : StatusGoldenBase, IAsyncDisposable
{
    private GitRepository? _repo;
    private string _gitignorePath = null!;

    private async Task<GitRepository> GetRepoAsync()
    {
        if (_repo is null)
        {
            _repo = await OpenFixtureRepoAsync("attr");
            _gitignorePath = Path.Combine(_repo.Workdir!, ".gitignore");
        }

        return _repo;
    }

    public async ValueTask DisposeAsync()
    {
        if (_repo is not null)
        {
            await _repo.DisposeAsync();
        }
    }

    private void RewriteGitignoreBytes(byte[] content)
        => File.WriteAllBytes(_gitignorePath, content);

    private static GitPath P(params byte[] bytes) => GitPath.FromUtf8Bytes(bytes);

    // A .gitignore with a non-UTF-8 pattern matches a non-UTF-8 path byte-exact.
    [Fact]
    public async Task NonUtf8Pattern_MatchesNonUtf8Path()
    {
        await GetRepoAsync();
        byte[] pattern = [0xFF, 0xFE, 0x80, (byte)'\n'];
        RewriteGitignoreBytes(pattern);

        bool ignored = await _repo!.IsIgnoredAsync(P(0xFF, 0xFE, 0x80), TestContext.Current.CancellationToken);
        Assert.True(ignored);
    }

    // A non-UTF-8 pattern does NOT match a different non-UTF-8 path.
    [Fact]
    public async Task NonUtf8Pattern_DoesNotMatchDifferentPath()
    {
        await GetRepoAsync();
        byte[] pattern = [0xFF, 0xFE, 0x80, (byte)'\n'];
        RewriteGitignoreBytes(pattern);

        bool ignored = await _repo!.IsIgnoredAsync(P(0xFF, 0xFE, 0x81), TestContext.Current.CancellationToken);
        Assert.False(ignored);
    }

    // A negative rule with a non-UTF-8 pattern un-ignores a non-UTF-8 path.
    [Fact]
    public async Task NegativeRule_NonUtf8_Unignores()
    {
        await GetRepoAsync();
        // Ignore the exact non-UTF-8 path, then un-ignore it with a negative.
        // (Using the exact path as both the positive and negative avoids the
        // does_negate_rule "useless negative" drop — the positive covers the
        // negative exactly.)
        byte[] gitignore = [0xFF, 0xFE, 0x80, (byte)'\n', (byte)'!', 0xFF, 0xFE, 0x80, (byte)'\n'];
        RewriteGitignoreBytes(gitignore);

        bool ignored = await _repo!.IsIgnoredAsync(P(0xFF, 0xFE, 0x80), TestContext.Current.CancellationToken);
        Assert.False(ignored);
    }

    // The string overload and the GitPath overload agree on ASCII (parity).
    [Fact]
    public async Task StringAndGitPathOverloads_AgreeOnAscii()
    {
        await GetRepoAsync();
        RewriteGitignoreBytes("*.txt\n"u8.ToArray());

        bool strResult = await _repo!.IsIgnoredAsync("foo.txt", TestContext.Current.CancellationToken);
        bool byteResult = await _repo!.IsIgnoredAsync(GitPath.FromUtf8String("foo.txt"), TestContext.Current.CancellationToken);
        Assert.True(strResult);
        Assert.Equal(strResult, byteResult);
    }

    // core.ignorecase: ASCII letters in ignore patterns fold; non-ASCII do NOT
    // (ASCII-fold parity fix vs OrdinalIgnoreCase). Tests the ParseBuffer
    // ignoreCase=true path directly (avoids config-file write timing).
    [Fact]
    public async Task IgnoreCase_AsciiFoldsButNonAsciiDoesNot()
    {
        await GetRepoAsync();

        // Parse a FOO ignore rule with ignoreCase=true and verify "foo" matches.
        var f = new IgnoreFile("test");
        f.ParseBuffer("FOO\n"u8.ToArray(), ignoreCase: true, default);

        var apFoo = new AttrPath();
        apFoo.Init(GitPath.FromUtf8String("foo"), default, AttrPath.DirFlag.False);
        Assert.True(f.Rules[0].Match(apFoo));

        // Non-ASCII: ä (0xC3 0xA4) does NOT fold to Ä (0xC3 0x84) even with
        // ignoreCase=true (ASCII-only fold — the parity fix vs OrdinalIgnoreCase).
        var f2 = new IgnoreFile("test2");
        f2.ParseBuffer(new byte[] { 0xC3, 0x84, (byte)'\n' }, ignoreCase: true, default); // Ä

        var apAe = new AttrPath();
        apAe.Init(GitPath.FromUtf8Bytes(new byte[] { 0xC3, 0xA4 }), default, AttrPath.DirFlag.False); // ä
        Assert.False(f2.Rules[0].Match(apAe));
    }
}

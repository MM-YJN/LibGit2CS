using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Tests for the byte-faithful <see cref="GitPathValidator"/>.
/// These pin the byte-domain walk (non-UTF-8 paths validate byte-exact) and
/// the faithful HFS <c>.git</c> normalization (a char-based port
/// approximated <c>next_hfs_char</c> by lowercasing non-ASCII chars directly,
/// which did NOT skip the ignored Unicode codepoints — a latent bug for
/// non-ASCII paths like <c>.g\u200cit</c>).
/// </summary>
public class ValidatorNonUtf8PathTests
{
    // A non-UTF-8 path with valid bytes (no .git, no traversal, no NUL) is accepted.
    [Fact]
    public async Task NonUtf8Path_ValidBytes_Accepted()
    {
        byte[] raw = [0xFF, 0xFE, 0x80];
        Assert.True(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8Bytes(raw),
            PathRejectPresets.WorkdirDefaults,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // A path with a NUL byte is always rejected, even mid-non-UTF-8.
    [Fact]
    public async Task NonUtf8Path_WithNulByte_Rejected()
    {
        byte[] raw = [0xFF, 0x00, 0x80];
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8Bytes(raw),
            GitPathRejectFlags.NtChars,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // HFS .git with ignored codepoint: ".g\u200cit" should be rejected as .git.
    // A char-based port did NOT skip 0x200C (ZWNJ) — it lowercased
    // it and compared against "git", failing to detect the attack. The byte-port
    // faithfully ports next_hfs_char's UTF-8 codepoint iteration + skip set.
    [Fact]
    public async Task HfsDotGit_WithIgnoredCodepoint_Rejected()
    {
        // .g\u200cit = "." + "g" + U+200C (ZWNJ, 0xE2 0x80 0x8C in UTF-8) + "it"
        byte[] dotgitHfs = [.. ".g"u8, 0xE2, 0x80, 0x8C, .. "it"u8];
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8Bytes(dotgitHfs),
            GitPathRejectFlags.DotGitHfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // HFS .git without ignored codepoints: ".git" is rejected.
    [Fact]
    public async Task HfsDotGit_Plain_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String(".git"),
            GitPathRejectFlags.DotGitHfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // HFS .git with mixed-case: ".GiT" is rejected (ASCII fold).
    [Fact]
    public async Task HfsDotGit_MixedCase_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String(".GiT"),
            GitPathRejectFlags.DotGitHfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // HFS: a non-.git path with an ignored codepoint is accepted.
    [Fact]
    public async Task Hfs_NonDotGit_WithIgnoredCodepoint_Accepted()
    {
        // ".foo\u200C" with ZWNJ — not .git, so accepted.
        byte[] foo = [.. ".foo"u8, 0xE2, 0x80, 0x8C];
        Assert.True(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8Bytes(foo),
            GitPathRejectFlags.DotGitHfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // NTFS .git with trailing space: ".git " is rejected.
    [Fact]
    public async Task NtfsDotGit_TrailingSpace_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String(".git "),
            GitPathRejectFlags.DotGitNtfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // NTFS .git with trailing dot: ".git." is rejected.
    [Fact]
    public async Task NtfsDotGit_TrailingDot_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String(".git."),
            GitPathRejectFlags.DotGitNtfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // NTFS .git with backslash: ".git\" is rejected.
    [Fact]
    public async Task NtfsDotGit_Backslash_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String(@".git\"),
            GitPathRejectFlags.DotGitNtfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // NTFS .git with colon (alternate data stream): ".git:" is rejected.
    [Fact]
    public async Task NtfsDotGit_Colon_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String(".git:"),
            GitPathRejectFlags.DotGitNtfs,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // Traversal: ".." component is rejected.
    [Fact]
    public async Task Traversal_DotDot_Rejected()
    {
        Assert.False(await GitPathValidator.IsValidAsync(
            GitPath.FromUtf8String("foo/../bar"),
            GitPathRejectFlags.Traversal,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // Byte-overload agrees with string-overload on ASCII (parity).
    [Fact]
    public async Task ByteOverload_AgreesWithStringOverload_OnAscii()
    {
        foreach (string p in new string[] { "src/main.cs", ".git", "foo/../bar", ".git ", ".GiT" })
        {
            GitPathRejectFlags flags = PathRejectPresets.WorkdirDefaults;
            bool strResult = await GitPathValidator.IsValidAsync(p, flags, cancellationToken: TestContext.Current.CancellationToken);
            bool byteResult = await GitPathValidator.IsValidAsync(GitPath.FromUtf8String(p), flags, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(strResult, byteResult);
        }
    }
}

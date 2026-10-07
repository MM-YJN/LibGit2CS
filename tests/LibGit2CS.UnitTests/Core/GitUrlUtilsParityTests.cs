using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary> Parity tests for <see cref="GitUrlUtils.LocalPathFromUrl"/> vs C <c>git_fs_path_fromurl</c> (src/util/fs_path.c). Covers:
/// (double-slash rejection). </summary>
public sealed class GitUrlUtilsParityTests
{
    // C (fs_path.c, git_fs_path_fromurl): on POSIX the leading '/' of the
    // absolute path is retained (offset--); on Windows the path starts just
    // past the third slash, so file:///abs/path → "abs/path".
    private static string PosixPath(string path) => OperatingSystem.IsWindows() ? path.TrimStart('/') : path;

    // ── file:////x / file://localhost//x rejected ─────────

    [Fact]
    public void LocalPathFromUrl_DoubleSlashAfterPrefix_ThrowsInvalid()
    {
        // C: git_fs_path_fromurl rejects when file_url[offset] == '/'
        // (fs_path.c:511-513) — a path component starting with another '/'
        // is "not a valid local file URI".
        GitException ex = Assert.Throws<GitException>(() => GitUrlUtils.LocalPathFromUrl("file:////x"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("not a valid local file URI", ex.Message);
    }

    [Fact]
    public void LocalPathFromUrl_LocalhostDoubleSlash_ThrowsInvalid()
    {
        GitException ex = Assert.Throws<GitException>(() => GitUrlUtils.LocalPathFromUrl("file://localhost//x"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("not a valid local file URI", ex.Message);
    }

    // ── Regression: valid forms still work ───────────────────────────────

    [Fact]
    public void LocalPathFromUrl_ValidForms_Unchanged()
    {
        Assert.Equal(PosixPath("/abs/path"), GitUrlUtils.LocalPathFromUrl("file:///abs/path"));
        Assert.Equal(PosixPath("/abs"), GitUrlUtils.LocalPathFromUrl("file://localhost/abs"));
        Assert.Equal(PosixPath("/a b"), GitUrlUtils.LocalPathFromUrl("file:///a%20b"));
        Assert.Equal(PosixPath("/x"), GitUrlUtils.LocalPathFromUrl("file:///x"));
        Assert.Equal(PosixPath("/x"), GitUrlUtils.LocalPathFromUrl("file://localhost/x"));
    }

    [Fact]
    public void LocalPathFromUrl_NonLocalForms_PassThrough()
    {
        Assert.Equal("file://", GitUrlUtils.LocalPathFromUrl("file://"));
        Assert.Equal("file://host/x", GitUrlUtils.LocalPathFromUrl("file://host/x"));
        Assert.Equal("plain/path", GitUrlUtils.LocalPathFromUrl("plain/path"));
    }

    [Fact]
    public void LocalPathFromUrl_TrailingSlashEmptyPath_ThrowsInvalid()
    {
        GitException ex = Assert.Throws<GitException>(() => GitUrlUtils.LocalPathFromUrl("file:///"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    // ── percent-decoding of non-UTF-8 bytes ───────────────

    [Fact]
    public void LocalPathFromUrl_PercentEncodedHighByte_DecodesToRawByte()
    {
        // C's git__percent_decode decodes %E9 to the raw byte 0xE9 (Latin-1
        // 'é'). Uri.UnescapeDataString leaves "%E9" literal because it would
        // produce a byte >= 0x80 in isolation; the raw byte must be emitted
        // instead (represented as the Latin-1 char U+00E9).
        string result = GitUrlUtils.LocalPathFromUrl("file:///caf%E9");
        Assert.Equal(PosixPath("/caf\u00E9"), result);
    }

    [Fact]
    public void LocalPathFromUrl_PercentEncodedUtf8Sequence_Decodes()
    {
        // Valid UTF-8 sequences decode in both C and C#.
        Assert.Equal(PosixPath("/caf\u00E9"), GitUrlUtils.LocalPathFromUrl("file:///caf%C3%A9"));
    }

    [Fact]
    public void LocalPathFromUrl_InvalidEscape_PreservedLiterally()
    {
        // Invalid hex and trailing '%' are appended literally (fs_path.c).
        Assert.Equal(PosixPath("/a%zzb"), GitUrlUtils.LocalPathFromUrl("file:///a%zzb"));
        Assert.Equal(PosixPath("/a%"), GitUrlUtils.LocalPathFromUrl("file:///a%"));
    }
}

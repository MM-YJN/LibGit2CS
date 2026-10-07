using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Diff;

// Parity cases verified against libgit2 1.9.4:
//  - the scaled-bar computation multiplied two ints unchecked,
//    wrapping negative for files with >~26.8M changed lines; C computes in
//    size_t (diff_stats.c:119-127).
//  - stats output decoded paths lossily through ToUtf8String; C emits
//    the raw bytes (diff_stats.c:89, 147-150).
//  - a negative hunk_cb return was collapsed to GitErrorCode.Error;
//    C propagates the callback's code verbatim (apply.c:198-210).
public sealed class DiffStatsLowRegressionTests
{
    private static GitDiffStats MakeStats(GitDiffFileStat stat, int maxFilestat, int maxDigits = 8)
    {
        return new GitDiffStats(
            [stat], filesChanged: 1,
            insertions: Math.Max(stat.Insertions, 0), deletions: Math.Max(stat.Deletions, 0),
            maxName: stat.Path.Length, maxFilestat, maxDigits);
    }

    /// <summary>
    /// Formats <paramref name="stats"/> to a byte buffer and decodes via
    /// UTF-8 (with replacement) for string-based
    /// assertions. The raw-byte tests bypass this and assert on the
    /// buffer directly.
    /// </summary>
    private static string FormatStats(GitDiffStats stats, GitDiffStatsFormat format, int width = 80)
    {
        using var writer = new PooledByteBufferWriter();
        stats.Format(writer, format, width);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    /// <summary>
    /// Formats <paramref name="stats"/> to a byte buffer and returns the raw
    /// bytes for direct byte-level assertions (non-UTF-8 path tests).
    /// </summary>
    private static byte[] FormatStatsBytes(GitDiffStats stats, GitDiffStatsFormat format, int width = 80)
    {
        using var writer = new PooledByteBufferWriter();
        stats.Format(writer, format, width);
        return writer.WrittenSpan.ToArray();
    }

    // ---- scaled-bar arithmetic is 64-bit ----

    [Fact]
    public void FormatFull_HugeChangeCount_DoesNotOverflow()
    {
        // 20M insertions + 20M deletions with barWidth 80: 40M × 63 = 2.52e9
        // overflows int — an int computation wraps negative and collapses the
        // bar to "+-". C computes in size_t (diff_stats.c:119-127).
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8String("big"), 20_000_000, 20_000_000);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 40_000_000);

        string output = FormatStats(stats, GitDiffStatsFormat.Full, width: 80);

        // An int overflow would collapse the bar to a single
        // "+-" pair; the 64-bit computation renders a full bar.
        Assert.True(output.Count(c => c == '+') > 20, $"bar too small: {output}");
        Assert.True(output.Count(c => c == '-') > 20, $"bar too small: {output}");
    }

    [Fact]
    public void FormatFull_NormalChangeCount_Unchanged()
    {
        // Control: a normal change count renders the same bars.
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8String("small"), 3, 1);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 4);

        string output = FormatStats(stats, GitDiffStatsFormat.Full, width: 80);
        Assert.Contains("+", output);
        Assert.Contains("-", output);
    }

    // ---- stats paths emit raw bytes (no Latin-1 round-trip) ----

    [Fact]
    public void FormatNumber_NonUtf8Path_EmitsRawBytes()
    {
        // A path with an isolated 0xE9 byte (invalid UTF-8). C emits the raw
        // bytes via git_str_printf("%s", path) (diff_stats.c:89, 147-150);
        // ToUtf8String would replace it with U+FFFD. Because
        // Format writes raw bytes, assert directly on the buffer — no Latin-1
        // decoding involved.
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8Bytes(new byte[] { 0x61, 0xE9, 0x62 }), 1, 0);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 1);

        byte[] bytes = FormatStatsBytes(stats, GitDiffStatsFormat.Number);
        Assert.Contains((byte)0xE9, bytes);
        // U+FFFD in UTF-8 is 0xEF 0xBF 0xBD — must not appear anywhere.
        Assert.False(bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xEF, 0xBF, 0xBD]) >= 0,
            $"output contains U+FFFD replacement bytes: {Convert.ToHexString(bytes)}");
    }

    [Fact]
    public void FormatFull_NonUtf8Path_EmitsRawBytes()
    {
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8Bytes(new byte[] { 0x61, 0xE9, 0x62 }), 1, 0);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 1);

        byte[] bytes = FormatStatsBytes(stats, GitDiffStatsFormat.Full);
        Assert.Contains((byte)0xE9, bytes);
        Assert.False(bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xEF, 0xBF, 0xBD]) >= 0,
            $"output contains U+FFFD replacement bytes: {Convert.ToHexString(bytes)}");
    }

    [Fact]
    public void FormatNumber_AsciiPath_Unchanged()
    {
        // Control: ASCII paths render identically.
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8String("plain.txt"), 1, 0);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 1);

        string output = FormatStats(stats, GitDiffStatsFormat.Number);
        Assert.Contains("plain.txt", output);
    }

    // ---- %06o mode lines: 6-digit octal, zero-padded ----

    // Regression guard: %06o mode lines are six-digit octal, zero-padded
    // (an 8-wide format would emit "00100644" instead of "100644").
    // Modes are POSIX octal: 33188 = 100644 (Regular), 33261 = 100755 (Executable).
    [Theory]
    [InlineData(0u, 33188u, " create mode 100644 f.txt\n")]
    [InlineData(33188u, 0u, " delete mode 100644 f.txt\n")]
    [InlineData(33188u, 33261u, " mode change 100644 => 100755 f.txt\n")]
    public void FormatSummary_ModeLine_EmitsSixDigitOctal(uint oldMode, uint newMode, string expected)
    {
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8String("f.txt"), 0, 0, OldMode: oldMode, NewMode: newMode);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 0);

        string output = FormatStats(stats, GitDiffStatsFormat.IncludeSummary);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void FormatSummary_EqualModes_EmitsNothing()
    {
        // Control: no mode line when old and new modes are equal.
        var stat = new GitDiffFileStat(
            GitPath.FromUtf8String("f.txt"), 0, 0, OldMode: 33188u, NewMode: 33188u);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 0);

        string output = FormatStats(stats, GitDiffStatsFormat.IncludeSummary);

        Assert.Equal(string.Empty, output);
    }

    // ---- DecimalPadLeftFormatter in FULL: space-padded count field ----

    [Fact]
    public void FormatFull_SmallCount_SpacePadsToMaxDigits()
    {
        // total = 5 (1 digit) with MaxDigits = 2 → " 5" (space-padded, not
        // zero-padded). barWidth 80 → 80-(1+2+5)=72 > MaxFilestat(5) → raw bars.
        var stat = new GitDiffFileStat(GitPath.FromUtf8String("f"), 3, 2);
        GitDiffStats stats = MakeStats(stat, maxFilestat: 5, maxDigits: 2);

        string output = FormatStats(stats, GitDiffStatsFormat.Full, width: 80);

        // FULL implies the SHORT summary line (diff_stats.c:294).
        Assert.Equal(" f |  5 +++--\n 1 file changed, 3 insertions(+), 2 deletions(-)\n", output);
    }

    // ---- negative hunk_cb return preserves the code ----

    [Fact]
    public async Task ApplyPatch_HunkCallbackNegative_PreservesCode()
    {
        (GitRepository repo, _) = await CreateRepoAsync();
        var oldBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, "a\nb\nc\n"u8.ToArray(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var newBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, "a\nB\nc\n"u8.ToArray(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var patch = GitPatch.FromBlobs(repo, oldBlob, newBlob);

        var options = new GitApplyOptions
        {
            HunkCallback = _ => -7, // GIT_EUSER
        };

        // C propagates the callback's negative return verbatim (apply.c:198-210);
        // collapsing it to a generic Error code would lose the distinction.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => GitPatchApplier.ApplyPatchAsync(
                "a\nb\nc\n"u8.ToArray(), patch, options, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.User, ex.Code);
    }

    [Fact]
    public async Task ApplyPatch_HunkCallbackZero_Applies()
    {
        // Control: a zero-returning callback lets the patch apply.
        (GitRepository repo, _) = await CreateRepoAsync();
        var oldBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, "a\nb\nc\n"u8.ToArray(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var newBlob = (GitBlob)(await repo.ObjectLookupAsync(
            await repo.ObjectWriteAsync(GitObjectType.Blob, "a\nB\nc\n"u8.ToArray(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken))!;
        var patch = GitPatch.FromBlobs(repo, oldBlob, newBlob);

        var options = new GitApplyOptions { HunkCallback = _ => 0 };
        GitApplyResult result = await GitPatchApplier.ApplyPatchAsync(
            "a\nb\nc\n"u8.ToArray(), patch, options, TestContext.Current.CancellationToken);
        Assert.Equal("a\nB\nc\n", Encoding.UTF8.GetString(result.Content.Span));
    }

    private static async Task<(GitRepository Repo, string RepoPath)> CreateRepoAsync()
    {
        string repoPath = Path.Combine(
            Path.GetTempPath(), "LibGit2CS_DiffStatsLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }
}

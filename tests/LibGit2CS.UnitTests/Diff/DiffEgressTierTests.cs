using System.Buffers;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Diff;

/// <summary> The three-tier byte egress (writer-first / <c>byte[]</c> / UTF-8 text) is content-identical across tiers, and the
/// byte round-trip <c>ToBufferAsync → FromBuffer(bytes) → ToBufferAsync</c> is byte-exact for invalid-UTF-8 content — no string intermediate anywhere.
/// </summary>
public sealed class DiffEgressTierTests
{
    // A single-file patch whose content lines carry invalid-UTF-8 bytes (FF, FE, 80) and whose hunk header funcname is a raw 0xC3 byte (invalid UTF-8 alone).
    // Built from explicit bytes — a "..."u8 literal would UTF8-encode its chars.
    private static byte[] SinglePatchBytes => [
        .. "diff --git a/bin b/bin\n"u8.ToArray(),
        .. "index 94aaae8..af8f41d 100644\n"u8.ToArray(),
        .. "--- a/bin\n"u8.ToArray(),
        .. "+++ b/bin\n"u8.ToArray(),
        .. "@@ -1,1 +1,1 @@ fn="u8.ToArray(),
        0xC3,
        .. "\n"u8.ToArray(),
        .. "-old "u8.ToArray(),
        0xFF,
        0xFE,
        .. "\n"u8.ToArray(),
        .. "+new "u8.ToArray(),
        0x80,
        .. "\n"u8.ToArray(),
    ];

    private static async Task<byte[]> ToBytesViaWriterAsync(
        GitDiff diff, GitDiffPrintFormat format, CancellationToken cancellationToken = default)
    {
        using var writer = new PooledByteBufferWriter();
        await diff.ToBufferAsync((IBufferWriter<byte>)writer, format, cancellationToken).ConfigureAwait(false);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public async Task Tiers_AreContentEqual_AllFormats()
    {
        var diff = GitDiff.FromBuffer(SinglePatchBytes);
        byte[] viaWriter = await ToBytesViaWriterAsync(diff, GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);
        byte[] viaBytes = await diff.ToBufferAsync(GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);
        string viaText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);

        Assert.Equal(viaBytes, viaWriter);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(viaBytes), viaText);
    }

    [Fact]
    public async Task NameOnly_Raw_Tiers_AreContentEqual_WithInvalidUtf8Paths()
    {
        // Rename to a path with invalid-UTF-8 bytes via octal escapes.
        byte[] renamePatch =
        [
            .. "diff --git a/x \"b/x\\303\\251\"\n"u8.ToArray(),
            .. "similarity index 100%\n"u8.ToArray(),
            .. "rename from x\n"u8.ToArray(),
            .. "rename to \"x\\303\\251\"\n"u8.ToArray(),
        ];

        var diff = GitDiff.FromBuffer(renamePatch);
        byte[] nameOnly = await ToBytesViaWriterAsync(diff, GitDiffPrintFormat.NameOnly, TestContext.Current.CancellationToken);
        byte[] nameOnlyBytes = await diff.ToBufferAsync(GitDiffPrintFormat.NameOnly, TestContext.Current.CancellationToken);

        Assert.Equal(nameOnlyBytes, nameOnly);
        // The NAME_ONLY path is the raw bytes x C3 A9 — no UTF-8 replacement.
        Assert.Equal<byte>([.. "x"u8.ToArray(), 0xC3, 0xA9, .. "\n"u8.ToArray()], nameOnly);
    }

    [Fact]
    public async Task Stat_Summary_Tiers_AreContentEqual()
    {
        var diff = GitDiff.FromBuffer(SinglePatchBytes);
        byte[] statWriter = await ToBytesViaWriterAsync(diff, GitDiffPrintFormat.Stat, TestContext.Current.CancellationToken);
        byte[] statBytes = await diff.ToBufferAsync(GitDiffPrintFormat.Stat, TestContext.Current.CancellationToken);
        string statText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Stat, TestContext.Current.CancellationToken);

        Assert.Equal(statBytes, statWriter);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(statBytes), statText);
        Assert.Contains("1 file changed", statText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoundTrip_InvalidUtf8_ByteExactBothDirections()
    {
        byte[] original = SinglePatchBytes;

        var first = GitDiff.FromBuffer(original);
        byte[] rendered1 = await first.ToBufferAsync(GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);
        Assert.Equal(original, rendered1);

        var second = GitDiff.FromBuffer(rendered1);
        byte[] rendered2 = await second.ToBufferAsync(GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);
        Assert.Equal(rendered1, rendered2);
    }

    [Fact]
    public async Task RoundTrip_HunkHeaderFuncName_InvalidUtf8_SurvivesVerbatim()
    {
        var diff = GitDiff.FromBuffer(SinglePatchBytes);

        GitPatch patch = await GitPatch.FromDiffAsync(diff, 0, TestContext.Current.CancellationToken);
        GitDiffHunk hunk = await patch.GetHunkAsync(0, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("hunk 0 not found");

        // The hunk header ends with the raw 0xC3 funcname byte + '\n' — the
        // parse copied the input slice verbatim (C memcpy model), and the
        // print path emits the stored bytes (no UTF-8 boundary truncation
        // applies on the parse path).
        byte[] header = hunk.Header.ToArray();
        Assert.Equal((byte)0xC3, header[^2]);
        Assert.Equal((byte)'\n', header[^1]);
    }

    [Fact]
    public async Task GitPatch_Tiers_AreContentEqual()
    {
        byte[] original = SinglePatchBytes;
        var diff = GitDiff.FromBuffer(original);

        GitPatch patch = await GitPatch.FromDiffAsync(diff, 0, TestContext.Current.CancellationToken);
        using var writer = new PooledByteBufferWriter();
        await patch.ToBufferAsync((IBufferWriter<byte>)writer, TestContext.Current.CancellationToken);
        byte[] viaBytes = await patch.ToBufferAsync(TestContext.Current.CancellationToken);
        string viaText = await patch.ToBufferTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(viaBytes, writer.WrittenSpan.ToArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetString(viaBytes), viaText);
        Assert.Equal(original, viaBytes);
    }

    [Fact]
    public async Task GitPatch_FromBufferBytes_ParsesInvalidUtf8Content()
    {
        var patch = GitPatch.FromBuffer(SinglePatchBytes);
        GitDiffHunk hunk = await patch.GetHunkAsync(0, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("hunk 0 not found");

        // Content lines carry the raw FF FE / 80 bytes (the origin sigil is
        // not part of Content).
        Assert.Contains(hunk.Lines, l => l.Content.Span.SequenceEqual<byte>([.. "old "u8.ToArray(), 0xFF, 0xFE, .. "\n"u8.ToArray()]));
        Assert.Contains(hunk.Lines, l => l.Content.Span.SequenceEqual<byte>([.. "new "u8.ToArray(), 0x80, .. "\n"u8.ToArray()]));
    }

    [Fact]
    public async Task GitPatch_TryFromBufferBytes_SingleVsMultiFile()
    {
        Assert.True(GitPatch.TryFromBuffer(SinglePatchBytes, out GitPatch? patch));
        Assert.NotNull(patch);

        // git_patch_from_buffer parses the FIRST patch and ignores trailing
        // content (no garbage check, patch_parse.c:1222-1238) — the byte
        // overload matches.
        byte[] multi =
        [
            .. SinglePatchBytes,
            .. "diff --git a/y b/y\nindex 94aaae8..af8f41d 100644\n--- a/y\n+++ b/y\n@@ -1,1 +1,1 @@\n-a\n+b\n"u8.ToArray(),
        ];
        Assert.True(GitPatch.TryFromBuffer(multi, out GitPatch? first));
        Assert.NotNull(first);
        Assert.Equal(1, await first!.GetHunkCountAsync(TestContext.Current.CancellationToken));
        first.Dispose();
        patch!.Dispose();
    }

    [Fact]
    public async Task StringOverload_Utf8Encodes_MatchesEquivalentBytes()
    {
        // The string convenience tier UTF-8-encodes: for an ASCII patch it is
        // byte-identical to the byte overload.
        const string patchText =
            "diff --git a/f b/f\n" +
            "index 94aaae8..af8f41d 100644\n" +
            "--- a/f\n" +
            "+++ b/f\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n";

        var fromString = GitDiff.FromBuffer(patchText);
        var fromBytes = GitDiff.FromBuffer(System.Text.Encoding.UTF8.GetBytes(patchText));

        byte[] a = await fromString.ToBufferAsync(GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);
        byte[] b = await fromBytes.ToBufferAsync(GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);
        Assert.Equal(b, a);
    }

    [Fact]
    public async Task Email_Tiers_AreContentEqual()
    {
        var diff = GitDiff.FromBuffer(SinglePatchBytes);
        var author = new GitSignature("T", "t@t", new GitTime(1577836800, 0));

        using var writer = new PooledByteBufferWriter();
        await GitEmailFormatter.ToBufferAsync(
            (IBufferWriter<byte>)writer, diff, 1, 1, GitOid.Empty, "subject", "body", author,
            cancellationToken: TestContext.Current.CancellationToken);
        byte[] viaBytes = await GitEmailFormatter.ToBufferAsync(
            diff, 1, 1, GitOid.Empty, "subject", "body", author,
            cancellationToken: TestContext.Current.CancellationToken);
        string viaText = await GitEmailFormatter.ToBufferTextAsync(
            diff, 1, 1, GitOid.Empty, "subject", "body", author,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(viaBytes, writer.WrittenSpan.ToArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetString(viaBytes), viaText);

        // The email embeds the patch content bytes verbatim (the invalid-UTF-8
        // content lines survive into the byte tier).
        Assert.Contains<byte>([.. "+new "u8.ToArray(), 0x80, .. "\n"u8.ToArray()], viaBytes);
    }
}

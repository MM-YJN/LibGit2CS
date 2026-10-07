using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using Xdiff;
using Xdiff.Emit;

namespace LibGit2CS.UnitTests.Diff;

/// <summary> Byte-native egress gate: hunk headers are raw bytes with the port of <c>git_utf8_valid_buf_length</c>
/// sanitization and the <c>GIT_DIFF_HUNK_HEADER_SIZE</c> cap, matching <c>git_xdiff_cb</c> (diff_xdiff.c:126-140) over the header assembled by xdiff's
/// <c>xdl_format_hunk_hdr</c> (deps/xdiff/xutils.c:343-390). </summary>
public sealed class XdiffBridgeFuncnameParityTests
{
    private static readonly byte[] s_eol = "\n"u8.ToArray();

    [Fact]
    public void ValidUtf8Funcname_IsByteExact()
    {
        // A UTF-8 funcname was previously decoded into a string and re-encoded as a single byte per char on print (é → E9, 中文 → ??). Now the header
        // is bytes end-to-end, so the UTF-8 funcname bytes must appear verbatim. Change is at the last line so the funcname search (backward from s1-1) reaches
        // line 0.
        string oldText = "static int 中文(void)\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n";
        string newText = "static int 中文(void)\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n";

        CapturingSink sink = ComputeHeader(oldText, newText);
        ReadOnlyMemory<byte> header = Assert.Single(sink.Headers);
        byte[] expected = "@@ -2,4 +2,4 @@ static int "u8.ToArray()
            .Concat("中文"u8.ToArray())
            .Concat("(void)"u8.ToArray())
            .Concat(s_eol)
            .ToArray();
        Assert.Equal(expected, header.ToArray());

        // The FuncName slice is the raw bytes — no decode, no replacement.
        Assert.Equal("static int 中文(void)"u8.ToArray(), Assert.Single(sink.Funcs).ToArray());
    }

    [Fact]
    public async Task ValidUtf8Funcname_EndToEndPrint_ContainsVerbatimBytes()
    {
        // Same scenario through the printer path (DiffPrinter emits
        // hunk.Header bytes directly — no re-encode). "é" is the sharp
        // regression pin: the funcname must stay byte-native, not decoded and
        // re-encoded one byte per char (which would emit a single 0xE9 byte
        // instead of C3 A9).
        string oldText = "static int é(void)\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n";
        string newText = "static int é(void)\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n";

        await using GitRepository repo = await CreateRepoAsync("funcname-e2e");
        using GitPatch patch = repo.PatchFromBuffers(Encoding.UTF8.GetBytes(oldText), Encoding.UTF8.GetBytes(newText));
        byte[] output = RenderPatch(patch);

        // Byte-native output must contain the UTF-8 é bytes C3 A9 verbatim
        // (not a replacement char).
        Assert.Contains("@@ -2,4 +2,4 @@ static int "u8.ToArray().Concat("é"u8.ToArray()).Concat("(void)"u8.ToArray()).Concat(s_eol).ToArray(), output);
        Assert.DoesNotContain((byte)0xE9, output);
    }

    [Fact]
    public void InvalidUtf8Funcname_TruncatesAtValidBoundaryAndSplicesNewline()
    {
        // Port of diff_xdiff.c:133-138: git_utf8_valid_buf_length stops at the
        // first invalid byte; the truncation may delete the trailing '\n', so
        // it is spliced back into the vacated position.
        // Funcname bytes: "module fn_" + 0xC3 0x28 (0xC3 expects a continuation
        // byte, 0x28 '(' is not one) — invalid at offset 12. The change is on
        // the last line so the backward funcname search reaches line 0.
        // NOTE: built via explicit byte literals — a `"\xC3\x28"u8` source
        // literal would UTF-8-encode the characters U+00C3 / U+0028 instead.
        byte[] oldBytes = [.. "module fn_"u8, 0xC3, 0x28, .. "\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n"u8];
        byte[] newBytes = [.. "module fn_"u8, 0xC3, 0x28, .. "\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n"u8];

        CapturingSink sink = ComputeHeader(oldBytes, newBytes);

        // "@@ -2,4 +2,4 @@ module fn_" (valid prefix) + spliced '\n'.
        Assert.Equal("@@ -2,4 +2,4 @@ module fn_\n"u8.ToArray(), Assert.Single(sink.Headers).ToArray());
    }

    [Fact]
    public async Task InvalidUtf8Funcname_EndToEnd_TruncatesAndSplices()
    {
        // The same truncation through the real emitter: the funcname line
        // contains bytes that are not valid UTF-8 (0xC3 0x28), which must be
        // truncated and spliced byte-natively — no UTF-8 decode to U+FFFD and
        // re-encode to "??". Change is at the last line
        // so the backward funcname search finds the module line.
        // NOTE: built via explicit byte literals — a `"\xC3\x28"u8` source
        // literal would UTF-8-encode the characters U+00C3 / U+0028 instead.
        byte[] oldBytes = [.. "module fn_"u8, 0xC3, 0x28, .. "\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n"u8];
        byte[] newBytes = [.. "module fn_"u8, 0xC3, 0x28, .. "\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n"u8];

        await using GitRepository repo = await CreateRepoAsync("funcname-invalid");
        using GitPatch patch = repo.PatchFromBuffers(oldBytes, newBytes);
        byte[] output = RenderPatch(patch);

        // Header truncated at the last valid UTF-8 boundary ("fn_" — the
        // trailing 0xC3 0x28 dropped) with '\n' spliced back.
        Assert.Contains("@@ -2,4 +2,4 @@ module fn_\n"u8.ToArray(), output);
    }

    [Fact]
    public void AsciiFuncname_Unchanged()
    {
        string oldText = "def foo():\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n";
        string newText = "def foo():\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n";

        CapturingSink sink = ComputeHeader(oldText, newText);

        Assert.Equal("@@ -2,4 +2,4 @@ def foo():\n"u8.ToArray(),
            Assert.Single(sink.Headers).ToArray());
    }

    [Fact]
    public void HeaderCap_TruncatesFuncnameAt128ByteBoundary()
    {
        // xdl_format_hunk_hdr caps the funcname so the trailing '\n' always
        // fits its 128-byte buffer ("@@ -2,4 +2,4 @@" prefix is 15 bytes, plus
        // ' ' = 16, plus '\n' = 17, leaving 111 funcname bytes)
        // (deps/xdiff/xutils.c:371-375).
        // The assembled header is 128 bytes; git_xdiff_cb then caps at
        // GIT_DIFF_HUNK_HEADER_SIZE - 1 = 127 (diff_xdiff.c:129-131). That
        // truncation is not UTF-8-driven, so no '\n' is spliced back — the
        // header ends mid-funcname, exactly as C.
        // The default (no-extractor) path caps the funcname at 80 bytes, so a
        // full-line extractor (libgit2 funcname-driver shape) is used to reach
        // the header cap with a 150-byte name.
        string funcname = new('a', 150);
        string oldText = funcname + "\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n";
        string newText = funcname + "\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n";

        CapturingSink sink = ComputeHeader(oldText, newText, ExtractWholeLine);
        ReadOnlyMemory<byte> header = Assert.Single(sink.Headers);

        Assert.Equal(127, header.Length);
        Assert.Equal('a', (char)header.Span[126]); // '\n' was dropped by the 127 cap
        Assert.Equal("@@ -2,4 +2,4 @@ "u8.ToArray().Concat(Encoding.ASCII.GetBytes(new string('a', 110))).ToArray(),
            header[..126].ToArray());
    }

    [Fact]
    public void FunctionNameExtractor_NoMatch_EmitsNoFuncname()
    {
        // A never-matching extractor is consulted for the match decision
        // (IsFuncRec), so no line is found and no funcname is emitted.
        string oldText = "# def foo():\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n";
        string newText = "# def foo():\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n";

        static (bool IsMatch, Range NameRange) NeverMatches(ReadOnlySpan<byte> line)
            => (false, default);

        CapturingSink sink = ComputeHeader(oldText, newText, NeverMatches);

        Assert.Equal("@@ -2,4 +2,4 @@\n"u8.ToArray(), Assert.Single(sink.Headers).ToArray());
        Assert.True(Assert.Single(sink.Funcs).IsEmpty);
    }

    [Fact]
    public void FunctionNameExtractor_SliceDisagrees_FallsBackToFullLineTrimming()
    {
        // SliceFuncName falls through to the default whole-line (trimmed and
        // capped) path when the extractor reports IsMatch = false while
        // slicing, even though the match phase accepted the same line. The
        // two phases are separate extractor calls, so a stateful extractor
        // can reach this branch; the fallback keeps the phases from
        // disagreeing (the default xdiff whole-line semantics win).
        string oldText = "# def foo():\n    a = 1\n    b = 2\n    c = 3\n    x = 1\n";
        string newText = "# def foo():\n    a = 1\n    b = 2\n    c = 3\n    x = 2\n";

        int calls = 0;
        (bool IsMatch, Range NameRange) Extractor(ReadOnlySpan<byte> line)
        {
            calls++;
            return calls == 1 ? (true, 0..line.Length) : (false, default);
        }

        CapturingSink sink = ComputeHeader(oldText, newText, Extractor);

        // Match phase (call 1) says yes; slice phase (call 2) says no →
        // SliceFuncName ignores the range and uses the whole line with the
        // trailing newline trimmed.
        Assert.Equal("@@ -2,4 +2,4 @@ # def foo():\n"u8.ToArray(), Assert.Single(sink.Headers).ToArray());
        Assert.Equal("# def foo():"u8.ToArray(), Assert.Single(sink.Funcs).ToArray());
    }

    private static (bool IsMatch, Range NameRange) ExtractWholeLine(ReadOnlySpan<byte> line)
        => (true, 0..(line.Length - 1));

    private static CapturingSink ComputeHeader(string oldText, string newText, Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)>? extractor = null)
        => ComputeHeader(Encoding.UTF8.GetBytes(oldText), Encoding.UTF8.GetBytes(newText), extractor);

    private static CapturingSink ComputeHeader(byte[] oldBytes, byte[] newBytes, Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)>? extractor = null)
    {
        var sink = new CapturingSink();
        Xdiff.Diff.Compute(
            sink,
            oldBytes,
            newBytes,
            new DiffOptions { IncludeFunctionNames = true, FunctionNameExtractor = extractor });
        return sink;
    }

    private sealed class CapturingSink : HunkSinkBase
    {
        public List<ReadOnlyMemory<byte>> Headers { get; } = [];

        public List<ReadOnlyMemory<byte>> Funcs { get; } = [];

        public override void EndHunk()
        {
            Headers.Add(XdiffBridge.BuildHunkHeader(this, hasFuncname: true));
            Funcs.Add(Func);
        }
    }

    private static async Task<GitRepository> CreateRepoAsync(string name)
    {
        string repoPath = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_Funcname_" + Guid.NewGuid().ToString("N")[..8] + "_" + name);
        Directory.CreateDirectory(repoPath);
        return await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static byte[] RenderPatch(GitPatch patch)
    {
        using var ms = new MemoryStream();
        DiffPrinter.PrintPatchAsync(patch, (_, _, line) =>
        {
            if (line.Origin is GitDiffLineOrigin.Addition or GitDiffLineOrigin.Deletion or GitDiffLineOrigin.Context)
            {
                ms.WriteByte((byte)line.Origin);
            }

            ms.Write(line.Content.Span);
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return ms.ToArray();
    }
}

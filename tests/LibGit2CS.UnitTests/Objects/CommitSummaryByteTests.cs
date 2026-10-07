using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> <see cref="Commit.SummaryBytes"/> is the byte-parity surface of <c>git_commit_summary</c> (commit.c:601-655) — C
/// folds the raw message bytes with the ASCII <c>git__isspace</c> class; non-UTF-8 bytes pass through verbatim (never U+FFFD, never re-encoded). </summary>
public sealed class CommitSummaryByteTests
{
    private static Commit ParseWithMessage(params byte[] messageBytes)
    {
        byte[] raw =
        [
            .. "tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n"u8.ToArray(),
            .. "author A U Thor <author@example.com> 1227814297 +0000\n"u8.ToArray(),
            .. "committer C O Mitter <committer@example.com> 1227814297 +0000\n"u8.ToArray(),
            .. "\n"u8.ToArray(),
            .. messageBytes,
        ];
        return Commit.Parse(owner: null, ObjectFixtures.CommitId, raw, GitHashAlgorithmKind.Sha1);
    }

    [Fact]
    public void AsciiMessage_MatchesStringSummary()
    {
        byte[] summary = Commit.ExtractSummaryBytes("one line subject\n\nbody\n"u8);

        Assert.Equal("one line subject"u8.ToArray(), summary);
    }

    [Fact]
    public void AsciiCommit_SummaryBytesEqualUtf8OfSummary()
    {
        Commit commit = ParseWithMessage([.. "summary line\n\nbody text\n"u8.ToArray()]);

        Assert.Equal(Encoding.UTF8.GetBytes(commit.Summary), commit.SummaryBytes.ToArray());
    }

    [Fact]
    public void WhitespaceRunWithoutNewline_CopiedVerbatim()
    {
        // A run of spaces/tabs with no '\n' inside is copied byte-for-byte
        // (commit.c:641-643, git_str_put of the run).
        byte[] summary = Commit.ExtractSummaryBytes([.. "a \t b"u8.ToArray()]);

        Assert.Equal("a \t b"u8.ToArray(), summary);
    }

    [Fact]
    public void WhitespaceRunContainingNewline_CollapsesToSingleSpace()
    {
        // A run containing '\n' collapses to one ' ' (commit.c:639-640).
        byte[] summary = Commit.ExtractSummaryBytes([.. "a \n\t b"u8.ToArray()]);

        Assert.Equal("a b"u8.ToArray(), summary);
    }

    [Fact]
    public void ParagraphEnd_StopsAtTrailingNewline()
    {
        byte[] summary = Commit.ExtractSummaryBytes([.. "subject\n"u8.ToArray()]);

        Assert.Equal("subject"u8.ToArray(), summary);
    }

    [Fact]
    public void ParagraphEnd_StopsAtBlankLine()
    {
        byte[] summary = Commit.ExtractSummaryBytes([.. "subject\n\nbody"u8.ToArray()]);

        Assert.Equal("subject"u8.ToArray(), summary);
    }

    [Fact]
    public void ParagraphEnd_StopsAtWhitespaceOnlyNextLine()
    {
        // "\n" followed by a whitespace-only line stops the paragraph
        // (commit.c:618-630).
        byte[] summary = Commit.ExtractSummaryBytes([.. "subject\n \t\nbody"u8.ToArray()]);

        Assert.Equal("subject"u8.ToArray(), summary);
    }

    [Fact]
    public void NonUtf8Bytes_PreservedVerbatim()
    {
        // Raw 0xE9 / 0xFF bytes are copied verbatim — C's git_commit_summary
        // has no charset concept (one byte = one character).
        byte[] summary = Commit.ExtractSummaryBytes([.. "s"u8.ToArray(), 0xE9, 0xFF, .. "bject"u8.ToArray()]);

        Assert.Equal([.. "s"u8.ToArray(), 0xE9, 0xFF, .. "bject"u8.ToArray()], summary);
    }

    [Fact]
    public void NonUtf8Message_ParsedCommitSummaryBytesAreRaw()
    {
        // End-to-end: a parsed commit with a non-UTF-8 message keeps the raw bytes in SummaryBytes; the string Summary is the U+FFFD display decode.
        Commit commit = ParseWithMessage([.. "s"u8.ToArray(), 0xE9, .. "ujet\n"u8.ToArray()]);

        Assert.Equal([.. "s"u8.ToArray(), 0xE9, .. "ujet"u8.ToArray()], commit.SummaryBytes.ToArray());
        Assert.Equal("s\uFFFDujet", commit.Summary);
    }

    [Fact]
    public void MultiByteUtf8_NotPerturbed()
    {
        // Valid multi-byte UTF-8 sequences pass through untouched.
        byte[] summary = Commit.ExtractSummaryBytes([.. "中文 subject"u8.ToArray()]);

        Assert.Equal("中文 subject"u8.ToArray(), summary);
    }

    [Fact]
    public void HighBytesAroundWhitespaceFold_FoldIsAsciiOnly()
    {
        // The whitespace class is ASCII-only: high bytes adjacent to a
        // folded run are copied verbatim, and the fold itself is ASCII ' '.
        byte[] summary = Commit.ExtractSummaryBytes([.. "a"u8.ToArray(), 0xC3, .. "\n"u8.ToArray(), 0xA9, .. "b"u8.ToArray()]);

        Assert.Equal([.. "a"u8.ToArray(), 0xC3, (byte)' ', 0xA9, .. "b"u8.ToArray()], summary);
    }

    [Fact]
    public void EmptyMessage_EmptySummary()
    {
        Assert.Empty(Commit.ExtractSummaryBytes([]));
    }

    [Fact]
    public void LeadingNewlinesTrimmed_MessageBytesVsSummary()
    {
        // git_commit_message trims leading '\n' before summary extraction
        // (commit.c:586-599) — SummaryBytes is computed over MessageBytes,
        // so a leading-newline message still yields the real subject.
        Commit commit = ParseWithMessage([.. "\n\nsubject\n"u8.ToArray()]);

        Assert.Equal("subject"u8.ToArray(), commit.SummaryBytes.ToArray());
        Assert.Equal("subject", commit.Summary);
    }
}

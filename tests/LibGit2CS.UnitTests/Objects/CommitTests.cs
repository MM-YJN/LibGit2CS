using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class CommitTests
{
    [Fact]
    public void Parse_CanonicalCommit_PopulatesAllFields()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        Assert.Equal(ObjectFixtures.InnerTreeId, commit.Tree);
        Assert.Empty(commit.Parents);
        Assert.Equal("A U Thor", commit.Author.Name);
        Assert.Equal("author@example.com", commit.Author.Email);
        Assert.Equal(1227814297, commit.Author.When.Seconds);
        Assert.Equal(0, commit.Author.When.OffsetMinutes);
        Assert.Equal("C O Mitter", commit.Committer.Name);
        Assert.Equal("committer@example.com", commit.Committer.Email);
        Assert.Equal(1227814297, commit.Time.Seconds);
        Assert.Null(commit.Encoding);
    }

    [Fact]
    public void Parse_CanonicalCommit_MessageTrimsLeadingNewlines()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        // The canonical commit has no leading blank lines in the message body.
        Assert.Equal(commit.RawMessage, commit.Message);
        Assert.StartsWith("A one-line commit summary", commit.Message);
    }

    [Fact]
    public void Parse_CanonicalCommit_SummaryIsFirstParagraph()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        Assert.Equal("A one-line commit summary", commit.Summary);
    }

    [Fact]
    public void Parse_CanonicalCommit_BodyIsAfterFirstParagraph()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        Assert.Contains("The body of the commit message", commit.Body);
        Assert.Contains("Signed-off-by: A U Thor", commit.Body);
    }

    [Fact]
    public void Body_TrimsAsciiWhitespaceOnly_KeepsUnicodeWhitespace()
    {
        // C (commit.c:670-676): git_commit_body trims git__isspace (ASCII only); the NBSP bytes (C2 A0) are NOT whitespace in C and stay in the body.
        // string.Trim would strip the NBSP chars. The raw object bytes are UTF-8-decoded by the port, so the C2 A0 bytes surface as the NBSP character U+00A0.
        string message =
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "summary\n" +
            "\n";
        byte[] bodyBytes = [.. Encoding.UTF8.GetBytes(message), 0xC2, 0xA0, .. "body"u8, 0xC2, 0xA0];
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, bodyBytes, GitHashAlgorithmKind.Sha1);

        Assert.Equal("\u00A0body\u00A0", commit.Body);
    }

    [Fact]
    public void Parse_CanonicalCommit_RawHeaderExcludesBlankLine()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        // C (commit.c:488-499): raw_header ends with the last header line's
        // \n — the blank separator line is NOT included.
        Assert.EndsWith("\n", commit.RawHeader);
        Assert.DoesNotContain("\n\n", commit.RawHeader);
        Assert.Contains("tree dff2da90b254e1beb889d1f1f1288be1803782df", commit.RawHeader);
        Assert.Contains("author A U Thor", commit.RawHeader);
    }

    [Fact]
    public void Parse_WithEncoding_PopulatesEncoding()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "encoding UTF-8\n" +
            "\n" +
            "Message\n");

        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal("UTF-8", commit.Encoding);
    }

    [Fact]
    public void Parse_WithParents_PopulatesParentOids()
    {
        var parent1 = GitOid.Parse("0000000000000000000000000000000000000001".AsSpan(), GitHashAlgorithmKind.Sha1);
        var parent2 = GitOid.Parse("0000000000000000000000000000000000000002".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            $"parent {parent1}\n" +
            $"parent {parent2}\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "Merge commit\n");

        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal(2, commit.Parents.Count);
        Assert.Equal(parent1, commit.Parents[0]);
        Assert.Equal(parent2, commit.Parents[1]);
        Assert.Equal(parent1, commit.ParentId(0));
        Assert.Equal(parent2, commit.ParentId(1));
    }

    [Fact]
    public void Parse_DuplicateAuthorLines_SkipsExtras()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "author Extra Author <extra@example.com> 1227814298 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "Message\n");

        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal("A U Thor", commit.Author.Name);
        Assert.Equal("author@example.com", commit.Author.Email);
    }

    [Fact]
    public void Parse_NoMessage_RawMessageEmpty()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n");

        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal(string.Empty, commit.RawMessage);
        Assert.Equal(string.Empty, commit.Message);
    }

    [Fact]
    public void Parse_LeadingNewlines_MessageTrimsRawMessageDoesNot()
    {
        // Commit with extra blank lines after the header.
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "\n\n" +
            "real message\n");

        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.StartsWith("\n\n", commit.RawMessage);
        Assert.Equal("real message\n", commit.Message);
    }

    [Fact]
    public void Parse_UnknownHeaders_PreservedInRawHeader()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "gpgsig -----BEGIN PGP SIGNATURE-----\n" +
            " \n" +
            " abc123\n" +
            " -----END PGP SIGNATURE-----\n" +
            "\n" +
            "Message\n");

        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.Contains("gpgsig -----BEGIN PGP SIGNATURE-----", commit.RawHeader);
    }

    [Fact]
    public void Parse_MissingTree_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "Message\n");

        GitException ex = Assert.Throws<GitException>(() =>
            Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_MissingAuthor_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "Message\n");

        GitException ex = Assert.Throws<GitException>(() =>
            Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_MissingCommitter_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "\n" +
            "Message\n");

        GitException ex = Assert.Throws<GitException>(() =>
            Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_InvalidTreeOid_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree xxx\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "Message\n");

        GitException ex = Assert.Throws<GitException>(() =>
            Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_Sha256Tree_WorksInSha256Mode()
    {
        var sha256Tree = GitOid.Parse(
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".AsSpan(),
            GitHashAlgorithmKind.Sha256);
        byte[] body = Encoding.UTF8.GetBytes(
            $"tree {sha256Tree}\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "Message\n");
        var sha256Id = GitOid.FromRaw(new byte[32], GitHashAlgorithmKind.Sha256);

        var commit = Commit.Parse(owner: null, sha256Id, body, GitHashAlgorithmKind.Sha256);

        Assert.Equal(sha256Tree, commit.Tree);
    }

    [Fact]
    public void HeaderField_UnknownField_ReturnsNull()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        Assert.Null(commit.HeaderField("nonexistent"));
    }

    [Fact]
    public void HeaderField_Tree_ReturnsOid()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        Assert.Equal("dff2da90b254e1beb889d1f1f1288be1803782df", commit.HeaderField("tree"));
    }

    [Fact]
    public void HeaderField_MultiLineGpgSig_JoinsContinuations()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "gpgsig -----BEGIN PGP SIGNATURE-----\n" +
            " \n" +
            " iQIzBAABCgAd\n" +
            " -----END PGP SIGNATURE-----\n" +
            "\n" +
            "Message\n");
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        string? sig = commit.HeaderField("gpgsig");
        Assert.NotNull(sig);
        Assert.StartsWith("-----BEGIN PGP SIGNATURE-----", sig);
        // Continuations are joined with \n, with leading SP stripped.
        Assert.Contains("\n\niQIzBAABCgAd", sig);
        Assert.EndsWith("\n-----END PGP SIGNATURE-----", sig);
    }

    [Fact]
    public void ExtractSignature_NoGpgSig_ThrowsNotFound()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        // C (commit.c:919-921): "this commit is not signed", GIT_ENOTFOUND.
        GitException ex = Assert.Throws<GitException>(() => commit.ExtractSignature());
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("this commit is not signed", ex.Message);
    }

    [Fact]
    public void ExtractSignature_WithGpgSig_ReturnsBoth()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "gpgsig -----BEGIN PGP SIGNATURE-----\n" +
            " \n" +
            " iQIzBAABCgAd\n" +
            " -----END PGP SIGNATURE-----\n" +
            "\n" +
            "Message\n");
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        (ReadOnlyMemory<byte> signedData, string? signature) = commit.ExtractSignature();

        Assert.NotNull(signature);
        Assert.Contains("-----BEGIN PGP SIGNATURE-----", signature);
        string signedText = Encoding.UTF8.GetString(signedData.Span);
        Assert.DoesNotContain("gpgsig", signedText);
        Assert.Contains("tree dff2da90b254e1beb889d1f1f1288be1803782df", signedText);
        Assert.Contains("Message", signedText);
    }

    [Fact]
    public void ParentId_OutOfRange_Throws()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        Assert.Throws<ArgumentOutOfRangeException>(() => commit.ParentId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => commit.ParentId(-1));
    }

    [Fact]
    public void Summary_WithMultilineMessage_CollapsesWhitespace()
    {
        // First paragraph that wraps lines should be collapsed to a single line.
        byte[] body = Encoding.UTF8.GetBytes(
            "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "First line of summary\n" +
            " continued from previous\n" +
            "\n" +
            "Body paragraph.\n");
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal("First line of summary continued from previous", commit.Summary);
    }
}

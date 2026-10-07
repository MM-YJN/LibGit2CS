using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class TagTests
{
    [Fact]
    public void Parse_CanonicalTag_PopulatesAllFields()
    {
        var tag = GitTag.Parse(owner: null, ObjectFixtures.TagId, ObjectFixtures.TagBody, GitHashAlgorithmKind.Sha1);

        Assert.Equal(ObjectFixtures.CommitId, tag.Target);
        Assert.Equal(GitObjectType.Commit, tag.TargetType);
        Assert.Equal("v0.0.1", tag.Name);
        Assert.Equal("C O Mitter", tag.Tagger!.Name);
        Assert.Equal("committer@example.com", tag.Tagger!.Email);
        Assert.Equal(1227814297, tag.Tagger!.When.Seconds);
        Assert.Equal("This is the tag object for release v0.0.1\n", tag.Message);
    }

    [Fact]
    public void Parse_MissingTagger_MessageStillParsed()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type commit\n" +
            "tag v0.0.1\n" +
            "\n" +
            "tag message\n");

        var tag = GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1);

        Assert.Null(tag.Tagger);
        Assert.Equal("tag message\n", tag.Message);
    }

    [Fact]
    public void Parse_MissingMessage_MessageIsNull()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type commit\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n");

        var tag = GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1);

        Assert.Null(tag.Message);
    }

    [Fact]
    public void Parse_NoBlankLineBeforeMessage_Throws()
    {
        // C (tag.c:144-153): without a blank line, the message must be found
        // after a "\n\n" — "tag contains no message".
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type commit\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "Message directly after tagger line.\n");

        GitException ex = Assert.Throws<GitException>(() => GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
        Assert.Contains("tag contains no message", ex.Message);
    }

    [Fact]
    public void Parse_UnknownField_AfterTagger_SkipsAndParsesMessage()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type commit\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "custom-header some-value\n" +
            "\n" +
            "Message\n");

        var tag = GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1);

        // The "custom-header" line is part of the message since the parser
        // treats anything after tagger as message (or unknown).
        // libgit2: tagger is the last recognized header; everything else is message.
        Assert.NotNull(tag.Message);
    }

    [Fact]
    public void Parse_MissingObject_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "type commit\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "msg\n");

        GitException ex = Assert.Throws<GitException>(() =>
            GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_MalformedObjectOid_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n" +
            "type commit\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "msg\n");

        Assert.Throws<GitException>(() =>
            GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_MissingType_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "msg\n");

        Assert.Throws<GitException>(() =>
            GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_InvalidType_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type garbage\n" +
            "tag v0.0.1\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "msg\n");

        Assert.Throws<GitException>(() =>
            GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_MissingTagName_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type commit\n" +
            "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "msg\n");

        Assert.Throws<GitException>(() =>
            GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_MalformedTagger_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
            "type commit\n" +
            "tag v0.0.1\n" +
            "tagger no angle brackets\n" +
            "\n" +
            "msg\n");

        Assert.Throws<GitException>(() =>
            GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_AllTargetTypes_Accepted()
    {
        foreach (string? typeStr in new[] { "commit", "tree", "blob", "tag" })
        {
            byte[] body = Encoding.UTF8.GetBytes(
                "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
                $"type {typeStr}\n" +
                "tag v0.0.1\n" +
                "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
                "\n" +
                "msg\n");

            var tag = GitTag.Parse(owner: null, ObjectFixtures.TagId, body, GitHashAlgorithmKind.Sha1);
            Assert.Equal(typeStr, tag.TargetType.ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public void Parse_Sha256Target_Works()
    {
        var sha256Target = GitOid.Parse(
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".AsSpan(),
            GitHashAlgorithmKind.Sha256);
        byte[] body = Encoding.UTF8.GetBytes(
            $"object {sha256Target}\n" +
            "type commit\n" +
            "tag v1\n" +
            "tagger A <a@b.c> 1234567890 +0000\n" +
            "\n" +
            "msg\n");
        var sha256Id = GitOid.FromRaw(new byte[32], GitHashAlgorithmKind.Sha256);

        var tag = GitTag.Parse(owner: null, sha256Id, body, GitHashAlgorithmKind.Sha256);

        Assert.Equal(sha256Target, tag.Target);
    }

    [Fact]
    public async Task Peel_NoOwner_Throws()
    {
        var tag = GitTag.Parse(owner: null, ObjectFixtures.TagId, ObjectFixtures.TagBody, GitHashAlgorithmKind.Sha1);

        await Assert.ThrowsAsync<GitException>(async () =>
            await tag.PeelAsync<Commit>(TestContext.Current.CancellationToken));
    }
}

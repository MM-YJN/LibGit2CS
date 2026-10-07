using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class ObjectDecodingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tag_InvalidUtf8_UsesReplacementAndRetainsRawBytes(bool extraHeader)
    {
        byte[] name = [.. "v-é-"u8, 0xFF];
        byte[] message = [.. "hello 😀 "u8, 0xC3, (byte)'\n'];
        byte[] body = [.. Encoding.UTF8.GetBytes($"object {GitOid.Empty}\ntype commit\ntag "), .. name,
            .. "\ntagger T <t@t> 0 +0000\n"u8,
            .. Encoding.UTF8.GetBytes(extraHeader ? "extra value\n\n" : "\n"), .. message];
        using var tag = GitTag.Parse(null, GitOid.Empty, body, GitHashAlgorithmKind.Sha1);
        Assert.Equal("v-é-\uFFFD", tag.Name);
        Assert.Equal("hello 😀 \uFFFD\n", tag.Message);
        Assert.Equal(name, tag.NameBytes.ToArray());
        Assert.Equal(message, tag.MessageBytes!.Value.ToArray());
    }

    [Fact]
    public void Tag_InvalidUtf8Type_ReportsReplacementInError()
    {
        byte[] body = [.. Encoding.UTF8.GetBytes($"object {GitOid.Empty}\ntype "), 0xFF, .. "\ntag v1\n"u8];
        GitException exception = Assert.Throws<GitException>(() => GitTag.Parse(null, GitOid.Empty, body, GitHashAlgorithmKind.Sha1));
        Assert.Contains("tag has invalid type '\uFFFD'", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UTF-8")]
    [InlineData("é😀")]
    public void Commit_EncodingHeader_DecodesUtf8(string encoding)
    {
        byte[] body = Encoding.UTF8.GetBytes($"tree {GitOid.Empty}\nauthor T <t@t> 0 +0000\ncommitter T <t@t> 0 +0000\nencoding {encoding}\n\nmessage\n");
        using var commit = Commit.Parse(null, GitOid.Empty, body, GitHashAlgorithmKind.Sha1);
        Assert.Equal(encoding, commit.Encoding);
    }

    [Fact]
    public void Commit_InvalidUtf8EncodingHeader_UsesReplacement()
    {
        byte[] body = [.. Encoding.UTF8.GetBytes($"tree {GitOid.Empty}\nauthor T <t@t> 0 +0000\ncommitter T <t@t> 0 +0000\nencoding "),
            0xFF, 0xC3, .. "\n\nmessage\n"u8];
        using var commit = Commit.Parse(null, GitOid.Empty, body, GitHashAlgorithmKind.Sha1);
        Assert.Equal("\uFFFD\uFFFD", commit.Encoding);
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Refs;

public sealed class ReflogSerializationTests
{
    [Theory]
    [InlineData(null, "A\nB", "")]
    [InlineData("", "A B", "")]
    [InlineData("x\nY", "A B", "\tx\nY")]
    [InlineData("x\nYZ", "A B", "\tx YZ")]
    [InlineData(" x \t\r\n", "A B", "\t x")]
    public void WriteEntry_PreservesFoldingAndTrimming(string? message, string expectedName, string expectedMessage)
    {
        using var output = new PooledByteBufferWriter();
        using var scratch = new PooledByteBufferWriter();
        var signature = new GitSignature("A\nB", "e", new GitTime(0, 0));
        ReadOnlyMemory<byte>? bytes = message is null ? (ReadOnlyMemory<byte>?)null : Encoding.UTF8.GetBytes(message);
        ReflogFormatter.WriteEntry(output, scratch, GitOid.Empty, GitOid.Empty, signature, bytes);
        byte[] expected = Encoding.UTF8.GetBytes($"{new string('0', 40)} {new string('0', 40)} {expectedName} <e> 0 +0000{expectedMessage}\n");
        Assert.Equal(expected, output.WrittenSpan.ToArray());
    }

    [Fact]
    public void SerializeBytes_ReusesScratchAcrossDifferentEntryLengths()
    {
        var log = new GitRefLog("HEAD", GitHashAlgorithmKind.Sha1);
        var signature = new GitSignature("A", "e", new GitTime(0, 0));
        byte[] longMessage = new byte[4096];
        Array.Fill(longMessage, (byte)0xff);
        byte[][] messages = [longMessage, [(byte)'x'], []];
        using var expected = new MemoryStream();
        foreach (byte[] message in messages)
        {
            log.Append(GitOid.Empty, signature, message);
            expected.Write(Encoding.UTF8.GetBytes($"{new string('0', 40)} {new string('0', 40)} A <e> 0 +0000"));
            if (message.Length > 0)
            {
                expected.WriteByte((byte)'\t');
                expected.Write(message);
            }
            expected.WriteByte((byte)'\n');
        }

        Assert.Equal(expected.ToArray(), log.SerializeBytes());
    }
}

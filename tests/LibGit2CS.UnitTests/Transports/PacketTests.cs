using System.Buffers;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

public class PacketTests
{
    private static readonly string s_sha1Zero = new('0', 40);
    private const string Sha1SampleA = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

    [Fact]
    public void ParseLength_TooShort_ReturnsFalse()
    {
        byte[] buffer = "00"u8.ToArray();
        Assert.False(GitPacketReader.TryParseLength(buffer, out _));
    }

    [Fact]
    public void ParseLength_FourBytes_ReturnsValue()
    {
        byte[] buffer = "002b"u8.ToArray();
        Assert.True(GitPacketReader.TryParseLength(buffer, out int len));
        Assert.Equal(0x2b, len);
    }

    [Fact]
    public void ParseLength_Flush_ReturnsZero()
    {
        byte[] buffer = "0000"u8.ToArray();
        Assert.True(GitPacketReader.TryParseLength(buffer, out int len));
        Assert.Equal(0, len);
    }

    [Fact]
    public void ParseLength_Delim_ReturnsOne()
    {
        byte[] buffer = "0001"u8.ToArray();
        Assert.True(GitPacketReader.TryParseLength(buffer, out int len));
        Assert.Equal(1, len);
    }

    [Fact]
    public void ParseLength_InvalidHex_Throws()
    {
        byte[] buffer = "00xy"u8.ToArray();
        Assert.Throws<GitException>(() => GitPacketReader.TryParseLength(buffer, out _));
    }

    [Fact]
    public void Parse_FlushPacket()
    {
        byte[] buffer = "0000"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out int consumed, ref state);
        Assert.IsType<GitFlushPacket>(pkt);
        Assert.Equal(4, consumed);
    }

    [Fact]
    public void Parse_NakPacket()
    {
        // "0007NAK\n" — length 7 = 4 + 3 ("NAK\n")
        byte[] buffer = "0007NAK\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out int consumed, ref state);
        Assert.IsType<GitNakPacket>(pkt);
        Assert.Equal(7, consumed);
    }

    [Fact]
    public void Parse_AckPacket_WithOid()
    {
        // "0031ACK <40-hex-oid>\n" — length = 4 + "ACK " + 40 + "\n" = 4 + 4 + 40 + 1 = 49 = 0x31
        byte[] buffer = Encoding.ASCII.GetBytes($"0031ACK {Sha1SampleA}\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out int consumed, ref state);
        GitAckPacket ack = Assert.IsType<GitAckPacket>(pkt);
        Assert.Equal(Sha1SampleA, ack.Oid.ToString());
        Assert.Equal(GitAckStatus.None, ack.Status);
        Assert.Equal(0x31, consumed);
    }

    [Fact]
    public void Parse_AckPacket_WithContinueStatus()
    {
        // "003aACK <40-hex-oid> continue\n" — 4 + "ACK " + 40 + " continue\n" = 4 + 4 + 40 + 10 = 58 = 0x3a
        byte[] buffer = Encoding.ASCII.GetBytes($"003aACK {Sha1SampleA} continue\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out int consumed, ref state);
        GitAckPacket ack = Assert.IsType<GitAckPacket>(pkt);
        Assert.Equal(GitAckStatus.Continue, ack.Status);
        Assert.Equal(0x3a, consumed);
    }

    [Fact]
    public void Parse_AckPacket_WithCommonStatus()
    {
        byte[] buffer = Encoding.ASCII.GetBytes($"0038ACK {Sha1SampleA} common\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitAckPacket ack = Assert.IsType<GitAckPacket>(pkt);
        Assert.Equal(GitAckStatus.Common, ack.Status);
    }

    [Fact]
    public void Parse_AckPacket_WithReadyStatus()
    {
        byte[] buffer = Encoding.ASCII.GetBytes($"0036ACK {Sha1SampleA} ready\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitAckPacket ack = Assert.IsType<GitAckPacket>(pkt);
        Assert.Equal(GitAckStatus.Ready, ack.Status);
    }

    [Fact]
    public void Parse_CommentPacket()
    {
        // "000e# comment\n" — length = 4 + "# comment\n" = 4 + 10 = 14 = 0x0e
        byte[] buffer = "000e# comment\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out int consumed, ref state);
        GitCommentPacket comment = Assert.IsType<GitCommentPacket>(pkt);
        Assert.Equal("# comment\n", comment.Text);
        Assert.Equal(0x0e, consumed);
    }

    [Fact]
    public void Parse_ErrorPacket()
    {
        // "0019ERR something broke\n" — 4 + "ERR " + "something broke\n" = 4 + 4 + 16 + 1 = 25? 
        // Actually: "ERR something broke\n" = 20 chars, total = 24 = 0x18
        string msg = "ERR something broke\n";
        int len = 4 + msg.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(msg).CopyTo(buffer, 4);
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitErrorPacket err = Assert.IsType<GitErrorPacket>(pkt);
        Assert.Equal("something broke\n", err.Message);
    }

    [Fact]
    public void Parse_SidebandData()
    {
        // Side-band data: first byte 0x01, rest is data
        byte[] dataBytes = new byte[] { 0x01, 0x50, 0x41, 0x43, 0x4b }; // \x01PACK
        int len = 4 + dataBytes.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        dataBytes.CopyTo(buffer, 4);
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitDataPacket data = Assert.IsType<GitDataPacket>(pkt);
        Assert.Equal(dataBytes[1..], data.Data);
    }

    [Fact]
    public void Parse_SidebandProgress()
    {
        // Side-band progress: first byte 0x02, rest is text
        byte[] dataBytes = new byte[] { 0x02 }.Concat("Counting objects: 100"u8.ToArray()).ToArray();
        int len = 4 + dataBytes.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        dataBytes.CopyTo(buffer, 4);
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitProgressPacket progress = Assert.IsType<GitProgressPacket>(pkt);
        Assert.Equal(dataBytes[1..], progress.Data);
    }

    [Fact]
    public void Parse_SidebandError()
    {
        byte[] dataBytes = new byte[] { 0x03 }.Concat("unexpected error"u8.ToArray()).ToArray();
        int len = 4 + dataBytes.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        dataBytes.CopyTo(buffer, 4);
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitErrorPacket err = Assert.IsType<GitErrorPacket>(pkt);
        Assert.Equal("unexpected error", err.Message);
    }

    [Fact]
    public void Parse_RefPacket_WithCapabilities()
    {
        // "<oid> HEAD\0<caps>\n" — first ref with capabilities
        // Format: "00xx<40-hex> HEAD\0caps\n"
        string caps = "multi_ack_detailed side-band-64k thin-pack ofs-delta";
        string content = $"{s_sha1Zero} HEAD\0{caps}\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState();
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitRefPacket refPkt = Assert.IsType<GitRefPacket>(pkt);
        Assert.Equal("HEAD", refPkt.Head.Name);
        Assert.Equal(caps, refPkt.Capabilities);
        Assert.True(state.SeenCapabilities);
    }

    [Fact]
    public void Parse_RefPacket_WithoutCapabilities()
    {
        // Second ref: no NUL, no capabilities
        string content = $"{Sha1SampleA} refs/heads/main\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1, SeenCapabilities = true };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitRefPacket refPkt = Assert.IsType<GitRefPacket>(pkt);
        Assert.Equal("refs/heads/main", refPkt.Head.Name);
        Assert.Null(refPkt.Capabilities);
    }

    [Fact]
    public void Parse_RefPacket_DetectsOidType()
    {
        string caps = "object-format=sha256";
        string content = $"{new string('0', 64)} HEAD\0{caps}\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState();
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        Assert.IsType<GitRefPacket>(pkt);
        Assert.Equal(GitHashAlgorithmKind.Sha256, state.OidType);
    }

    [Fact]
    public void Parse_OkPacket()
    {
        // "0011ok refs/heads/main\n" — 4 + "ok refs/heads/main\n" = 4 + 18 = 22? No: 4 + "ok " + "refs/heads/main\n" = 4 + 3 + 15 = 22? 
        // Actually: "ok refs/heads/main\n".Length = 19, total = 23 = 0x17
        string content = "ok refs/heads/main\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitOkPacket ok = Assert.IsType<GitOkPacket>(pkt);
        Assert.Equal("refs/heads/main", ok.Ref);
    }

    [Fact]
    public void Parse_NgPacket()
    {
        string content = "ng refs/heads/main failed\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitNgPacket ng = Assert.IsType<GitNgPacket>(pkt);
        Assert.Equal("refs/heads/main", ng.Ref);
        Assert.Equal("failed", ng.Message);
    }

    [Fact]
    public void Parse_UnpackOk()
    {
        // "000bunpack ok\n" — 4 + "unpack ok\n" = 4 + 10 = 14 = 0x0e? 
        // "unpack ok\n".Length = 10, total = 14 = 0x0e
        string content = "unpack ok\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitUnpackPacket unpack = Assert.IsType<GitUnpackPacket>(pkt);
        Assert.True(unpack.UnpackOk);
    }

    [Fact]
    public void Parse_UnpackFail()
    {
        string content = "unpack error: barf\n";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitUnpackPacket unpack = Assert.IsType<GitUnpackPacket>(pkt);
        Assert.False(unpack.UnpackOk);
    }

    [Fact]
    public void Parse_ShallowPacket()
    {
        // C (smart_pkt.c:471-476): the payload after "shallow " must be
        // EXACTLY the OID hex length — a trailing newline (len 41) is
        // rejected. (The C writer emits "\n", so libgit2 rejects its own
        // shallow handshake packets; the parser requires the bare OID.)
        string content = $"shallow {Sha1SampleA}";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitShallowPacket shallow = Assert.IsType<GitShallowPacket>(pkt);
        Assert.Equal(Sha1SampleA, shallow.Oid.ToString());
    }

    [Fact]
    public void Parse_UnshallowPacket()
    {
        // C (smart_pkt.c:506-511): exactly the OID hex length, no newline.
        string content = $"unshallow {Sha1SampleA}";
        int len = 4 + content.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(content).CopyTo(buffer, 4);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitUnshallowPacket unshallow = Assert.IsType<GitUnshallowPacket>(pkt);
        Assert.Equal(Sha1SampleA, unshallow.Oid.ToString());
    }

    [Fact]
    public void Parse_TooShort_ThrowsBufferTooShort()
    {
        // Length says 100 bytes but only 4 available
        byte[] buffer = "0064"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.BufferTooShort, ex.Code);
    }

    [Fact]
    public void Parse_InvalidEmptyPacket_Throws()
    {
        // Length 0004 = 4, which is the length prefix itself — empty packet.
        // C (smart_pkt.c:617-621): GIT_ERROR (-1), "Invalid empty packet".
        byte[] buffer = "0004"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public void Parse_InvalidShortLength_Throws()
    {
        // Length 0003 = 3, less than 4 but not 0 (flush).
        // C (smart_pkt.c:613-615): bare GIT_ERROR (-1).
        byte[] buffer = "0003x"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };
        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    // ── TryParse: never-throw variant ────────────────────────────────────

    [Fact]
    public void TryParse_TooShortForLength_ReturnsFalse_BufferTooShort()
    {
        // Only 2 bytes — not enough for the 4-byte length prefix.
        byte[] buffer = "00"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out int consumed, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.BufferTooShort, error);
        Assert.Null(pkt);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_TooShortForPayload_ReturnsFalse_BufferTooShort()
    {
        // Length says 0x64 (100) bytes but only the 4-byte prefix is present.
        byte[] buffer = "0064"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out int consumed, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.BufferTooShort, error);
        Assert.Null(pkt);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_InvalidHexLength_ReturnsFalse_InvalidHexLength()
    {
        // 4 bytes available, but 'x'/'y' are not hex digits.
        byte[] buffer = "00xy"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out int consumed, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.InvalidHexLength, error);
        Assert.Null(pkt);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_InvalidEmptyPacket_ReturnsFalse_InvalidEmptyPacket()
    {
        // Length 0004 = 4 (the prefix itself) — empty packet.
        byte[] buffer = "0004"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out _, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.InvalidEmptyPacket, error);
        Assert.Null(pkt);
    }

    [Fact]
    public void TryParse_MalformedPayload_ReturnsFalse_InvalidPayload()
    {
        // "0009ACK x" — well-formed length (9) and complete, but the ACK body
        // is too short to hold a 40-hex OID, which the payload parser rejects.
        byte[] buffer = "0009ACK x"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out _, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.InvalidPayload, error);
        Assert.Null(pkt);
    }

    [Fact]
    public void TryParse_CompletePacket_ReturnsTrue()
    {
        // "0007NAK\n" — a fully available NAK packet.
        byte[] buffer = "0007NAK\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out int consumed, out GitPacketParseError error, ref state);

        Assert.True(result);
        Assert.Equal(GitPacketParseError.None, error);
        Assert.IsType<GitNakPacket>(pkt);
        Assert.Equal(7, consumed);
    }

    [Fact]
    public void TryParse_FlushPacket_ReturnsTrue()
    {
        byte[] buffer = "0000"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out int consumed, out GitPacketParseError error, ref state);

        Assert.True(result);
        Assert.Equal(GitPacketParseError.None, error);
        Assert.IsType<GitFlushPacket>(pkt);
        Assert.Equal(4, consumed);
    }

    [Fact]
    public void WriteFlush_Produces0000()
    {
        var buf = new ArrayBufferWriter<byte>();
        GitPacketWriter.WriteFlush(buf);
        Assert.Equal("0000", Encoding.ASCII.GetString(buf.WrittenSpan));
    }

    [Fact]
    public void WriteDone_Produces0009done()
    {
        var buf = new ArrayBufferWriter<byte>();
        GitPacketWriter.WriteDone(buf);
        Assert.Equal("0009done\n", Encoding.ASCII.GetString(buf.WrittenSpan));
    }

    [Fact]
    public void WriteHave_ProducesCorrectFormat()
    {
        var oid = GitOid.Parse(Sha1SampleA, GitHashAlgorithmKind.Sha1);
        var buf = new ArrayBufferWriter<byte>();
        GitPacketWriter.WriteHave(buf, oid);
        // "002bhave <40hex>\n" = 4 + 5 + 40 + 1 = 50 = 0x32
        // Wait: "have " = 5 chars, oid = 40, "\n" = 1, total = 46 + 4 = 50 = 0x32
        // Actually: len = 4 + 5 + 40 + 1 = 50 = 0x32
        Assert.Equal($"0032have {Sha1SampleA}\n", Encoding.ASCII.GetString(buf.WrittenSpan));
    }
}

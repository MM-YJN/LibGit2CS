using System.Text;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Slim copy of the <c>MockTransportBuilder</c> pkt-line helpers used by
/// <see cref="HttpTestServer"/>. The full <c>MockTransportBuilder</c> (with
/// ref-advertisement/ACK builders used by fetch-negotiation unit tests) stays
/// in <c>LibGit2CS.UnitTests</c>; only the side-band/NAK/flush helpers needed
/// by the HTTP integration tests are duplicated here.
/// </summary>
public static class MockTransportBuilder
{
    /// <summary>Build a NAK response packet.</summary>
    public static byte[] BuildNak() => Encoding.ASCII.GetBytes("0008NAK\n");

    /// <summary>Build a flush packet.</summary>
    public static byte[] BuildFlush() => Encoding.ASCII.GetBytes("0000");

    /// <summary>Build a side-band data packet containing pack data.</summary>
    public static byte[] BuildSidebandData(byte[] packData)
    {
        var result = new List<byte>();
        int offset = 0;
        while (offset < packData.Length)
        {
            int chunkSize = Math.Min(8192, packData.Length - offset);
            // Side-band: first byte is channel (0x01 = data)
            string lenHex = (4 + 1 + chunkSize).ToString("x4");
            result.AddRange(Encoding.ASCII.GetBytes(lenHex));
            result.Add(0x01); // Data channel
            result.AddRange(packData.AsSpan(offset, chunkSize).ToArray());
            offset += chunkSize;
        }

        // End with flush
        result.AddRange(BuildFlush());
        return [.. result];
    }
}

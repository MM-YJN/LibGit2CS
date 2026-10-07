using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Mock subtransport that can respond to negotiation requests with
/// configurable ACK/NAK packets and serve a pack stream. Used by
/// fetch negotiation tests.
/// </summary>
public sealed class MockNegotiationTransport : IGitSubtransport
{
    private readonly byte[] _refAdvertisement;
    private readonly byte[] _negotiationResponse;
    private readonly byte[] _packData;
    private readonly bool _isRpc;
    private readonly List<byte> _receivedData = [];
    private int _refReadOffset;
    private int _negotiationReadOffset;
    private int _packReadOffset;
    private MockStream? _currentStream;

    /// <summary>
    /// Creates a mock transport with the given ref advertisement,
    /// negotiation response, and pack data.
    /// </summary>
    /// <param name="refAdvertisement">Raw pkt-line ref advertisement bytes.</param>
    /// <param name="negotiationResponse">ACK/NAK packets sent in response to wants/haves.</param>
    /// <param name="packData">Pack data (with side-band framing if needed).</param>
    /// <param name="isRpc">Whether to behave as RPC (HTTP) or stateful (git://).</param>
    public MockNegotiationTransport(
        byte[] refAdvertisement,
        byte[] negotiationResponse,
        byte[] packData,
        bool isRpc = false)
    {
        _refAdvertisement = refAdvertisement;
        _negotiationResponse = negotiationResponse;
        _packData = packData;
        _isRpc = isRpc;
    }

    /// <summary>Whether this is an RPC transport.</summary>
    public bool IsRpc => _isRpc;

    /// <summary>Data received from the client (wants/haves/done).</summary>
    public byte[] ReceivedData => [.. _receivedData];

    /// <summary>Number of service actions (connections) opened. Used by the
    /// clone-reconnect regression test to assert a single connection.</summary>
    public int ActionCount { get; private set; }

    /// <inheritdoc/>
    public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
    {
        ActionCount++;
        var stream = new MockStream(this, service);
        _currentStream = stream;
        return Task.FromResult<IGitSubtransportStream>(stream);
    }

    /// <inheritdoc/>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync();
        }
        _currentStream = null;
    }

    internal int ReadData(GitSmartService service, Memory<byte> buffer)
    {
        byte[] source;
        int offset;

        switch (service)
        {
            case GitSmartService.UploadPackLs:
                source = _refAdvertisement;
                offset = _refReadOffset;
                break;

            case GitSmartService.UploadPack:
                // First serve negotiation response, then pack data
                if (_negotiationReadOffset < _negotiationResponse.Length)
                {
                    source = _negotiationResponse;
                    offset = _negotiationReadOffset;
                }
                else
                {
                    source = _packData;
                    offset = _packReadOffset;
                }
                break;

            default:
                return 0;
        }

        if (offset >= source.Length)
        {
            return 0;
        }

        int toRead = Math.Min(buffer.Length, source.Length - offset);
        source.AsSpan(offset, toRead).CopyTo(buffer.Span);

        if (service == GitSmartService.UploadPackLs)
        {
            _refReadOffset += toRead;
        }
        else if (_negotiationReadOffset < _negotiationResponse.Length)
        {
            _negotiationReadOffset += toRead;
        }
        else
        {
            _packReadOffset += toRead;
        }

        return toRead;
    }

    internal void WriteData(ReadOnlyMemory<byte> data)
    {
        _receivedData.AddRange(data.ToArray());
    }

    private sealed class MockStream : IGitSubtransportStream
    {
        private readonly MockNegotiationTransport _owner;
        private readonly GitSmartService _service;

        public MockStream(MockNegotiationTransport owner, GitSmartService service)
        {
            _owner = owner;
            _service = service;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(_owner.ReadData(_service, buffer));

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _owner.WriteData(data);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Helper to build pkt-line ref advertisements for tests.
/// </summary>
public static class MockTransportBuilder
{
    /// <summary>Build a ref advertisement with the given refs and capabilities.</summary>
    public static byte[] BuildRefAdvertisement(
        (string Oid, string Name)[] refs,
        string? capabilities = null,
        bool isRpc = false,
        GitHashAlgorithmKind oidType = GitHashAlgorithmKind.Sha1)
    {
        var sb = new StringBuilder();

        if (isRpc)
        {
            // RPC has a leading comment packet
            sb.Append("001e# service=git-upload-pack\n");
            sb.Append("0000"); // First flush (RPC expects 2)
        }

        for (int i = 0; i < refs.Length; i++)
        {
            (string? oid, string? name) = refs[i];
            string line;
            if (i == 0 && capabilities is not null)
            {
                // First ref carries capabilities after NUL
                line = $"{oid} {name}\0{capabilities}\n";
            }
            else
            {
                line = $"{oid} {name}\n";
            }

            int len = line.Length + 4;
            sb.Append(len.ToString("x4"));
            sb.Append(line);
        }

        sb.Append("0000"); // Flush

        if (isRpc)
        {
            sb.Append("0000"); // Second flush for RPC
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Build a NAK response packet.</summary>
    public static byte[] BuildNak() => Encoding.ASCII.GetBytes("0008NAK\n");

    /// <summary>Build an ACK response packet for the given OID.</summary>
    public static byte[] BuildAck(string oid, string? status = null, GitHashAlgorithmKind oidType = GitHashAlgorithmKind.Sha1)
    {
        string line = status is null ? $"ACK {oid}\n" : $"ACK {oid} {status}\n";
        int len = line.Length + 4;
        return Encoding.ASCII.GetBytes($"{len:x4}{line}");
    }

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

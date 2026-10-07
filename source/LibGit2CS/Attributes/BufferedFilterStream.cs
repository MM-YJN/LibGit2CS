// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A filter stream that buffers all writes, then on <see cref="CloseAsync"/>
/// calls <see cref="IFilter.ApplyAsync"/> and writes the result to the target
/// stream. Managed port of <c>buffered_stream</c>
/// (<c>src/libgit2/filter.c:874-1000</c>).
/// </summary>
/// <remarks>
/// If <see cref="IFilter.ApplyAsync"/> returns <see cref="GitApplyResult.Passthrough"/>,
/// the original buffered input is written to the target unchanged.
/// Otherwise, the filtered output is written. Matches
/// <c>buffered_stream_close</c> (filter.c:907-960).
/// </remarks>
internal sealed class BufferedFilterStream : IFilterWriteStream
{
    private readonly IFilter _filter;
    private readonly GitFilterSource _source;
    private readonly IFilterWriteStream _target;
    private readonly MemoryStream _buffer = new();
    private bool _closed;

    public BufferedFilterStream(IFilter filter, GitFilterSource source, IFilterWriteStream target)
    {
        _filter = filter;
        _source = source;
        _target = target;
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        _buffer.Write(buffer);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        ReadOnlyMemory<byte> input = _buffer.TryGetBuffer(out ArraySegment<byte> buffer) ? buffer : _buffer.ToArray();
        GitApplyResult result = await _filter.ApplyAsync(_source, input, cancellationToken).ConfigureAwait(false);

        ReadOnlySpan<byte> output;
        if (result.Applied && result.Output is not null)
        {
            output = result.Output;
        }
        else
        {
            output = input.Span; // Passthrough: write original input.
        }

        _target.Write(output);
        await _target.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Caller must call CloseAsync before Dispose to flush the buffer.
        // Here we just release the buffer and target.
        _buffer.Dispose();
        _target.Dispose();
    }
}

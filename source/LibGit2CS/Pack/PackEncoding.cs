// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Objects;

namespace LibGit2CS.Pack;

/// <summary>
/// Pack wire-format encoding helpers shared by <see cref="GitPackWriter"/>
/// (pack creation) and <see cref="GitPackIndexer"/> (pack indexing + thin-pack
/// fixup). Managed port of <c>git_packfile__object_header</c>
/// (<c>src/libgit2/pack.c:388</c>).
/// </summary>
internal static class PackEncoding
{
    /// <summary>
    /// Writes a pack object header (type + size variable-length encoding) into
    /// <paramref name="buffer"/> and returns the number of bytes written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Encoding: first byte is <c>(typeCode &lt;&lt; 4) | (size &amp; 0x0F)</c>,
    /// with MSB=1 meaning continuation. Subsequent bytes give 7 more size bits
    /// each, MSB=1 for continuation. Matches <c>git_packfile__object_header</c>.
    /// </para>
    /// <para>
    /// <paramref name="buffer"/> must be at least 16 bytes — sufficient for any
    /// object size up to 2<sup>60</sup> (the pack format's practical limit).
    /// </para>
    /// </remarks>
    public static int WriteObjectHeader(Span<byte> buffer, GitObjectType type, long size)
    {
        int typeCode = type switch
        {
            GitObjectType.Commit => 1,
            GitObjectType.Tree => 2,
            GitObjectType.Blob => 3,
            GitObjectType.Tag => 4,
            GitObjectType.OfsDelta => 6,
            GitObjectType.RefDelta => 7,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unsupported pack object type"),
        };

        int firstByte = (typeCode << 4) | ((int)size & 0x0F);
        size >>= 4;

        if (size > 0)
        {
            firstByte |= 0x80;
        }

        int offset = 0;
        buffer[offset++] = (byte)firstByte;

        while (size > 0)
        {
            int b = (int)(size & 0x7F);
            size >>= 7;
            if (size > 0)
            {
                b |= 0x80;
            }

            buffer[offset++] = (byte)b;
        }

        return offset;
    }
}

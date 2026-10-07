// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;

using LibGit2CS.Core;
using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <summary>Byte-native serialize_reflog_entry (libgit2 refdb_fs.c:2174-2213).</summary>
internal static class ReflogFormatter
{
    internal static void WriteEntry(IBufferWriter<byte> output, PooledByteBufferWriter scratch,
        GitOid oldId, GitOid newId, GitSignature signature, ReadOnlyMemory<byte>? message)
    {
        scratch.ResetWrittenCount();
        signature.WriteTo(scratch);
        ReadOnlySpan<byte> signatureBytes = AsciiText.Rtrim(scratch.WrittenSpan);
        int prefixLength = checked(oldId.HexSize + newId.HexSize + 2 + signatureBytes.Length);
        int length = checked(prefixLength + (message is { } value ? 1 + value.Length : 0));
        Span<byte> line = output.GetSpan(checked(length + 1));
        int pos = oldId.FormatHex(line);
        line[pos++] = (byte)' ';
        pos += newId.FormatHex(line[pos..]);
        line[pos++] = (byte)' ';
        signatureBytes.CopyTo(line[pos..]);

        if (message is { } msg)
        {
            line[prefixLength] = (byte)'\t';
            msg.Span.CopyTo(line[(prefixLength + 1)..]);
            // Preserve C's i < size - 2 quirk, including folding signature bytes.
            for (int i = 0; i < length - 2; i++)
            {
                if (line[i] == (byte)'\n')
                {
                    line[i] = (byte)' ';
                }
            }

            length = AsciiText.Rtrim(line[..length]).Length;
        }

        line[length] = (byte)'\n';
        output.Advance(length + 1);
    }
}

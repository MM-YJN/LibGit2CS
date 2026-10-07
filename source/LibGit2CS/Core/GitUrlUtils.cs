// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// URL utility methods for transport URL parsing.
/// Managed port of <c>git_fs_path_from_url_or_path</c> + <c>git_fs_path_fromurl</c>
/// in <c>src/util/fs_path.c</c>.
/// </summary>
public static class GitUrlUtils
{
    private const string FileScheme = "file://";
    private const string LocalhostPrefix = "localhost/";

    /// <summary>
    /// Returns <c>true</c> if the URL is a <c>file://</c> URL.
    /// Matches <c>git_fs_path_is_local_file_url</c>.
    /// </summary>
    public static bool IsLocalFileUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return LocalFileUrlPrefixLen(url) > 0;
    }

    /// <summary>
    /// Convert a <c>file://</c> URL (or plain path) to a filesystem path.
    /// Matches <c>git_fs_path_from_url_or_path</c> + <c>git_fs_path_fromurl</c>
    /// in <c>src/util/fs_path.c</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// libgit2 only accepts two forms:
    /// <c>file:///&lt;path&gt;</c> (three slashes — the path is absolute) and
    /// <c>file://localhost/&lt;path&gt;</c>. Any other <c>file://host/...</c>
    /// form is rejected as an invalid local file URI. This matches
    /// <c>local_file_url_prefixlen</c> + <c>git_fs_path_fromurl</c> exactly.
    /// </para>
    /// <para>
    /// On POSIX, the returned path retains the leading <c>/</c>
    /// (<c>file:///abs/path</c> → <c>/abs/path</c>). On Windows, a leading
    /// drive letter is preserved (<c>file:///C:/repo</c> → <c>C:\repo</c>)
    /// and forward slashes are normalized to backslashes via
    /// <see cref="System.IO.Path.GetFullPath(string)"/>. Percent-encoding is decoded
    /// via <see cref="System.Uri.UnescapeDataString(System.ReadOnlySpan{char})"/>.
    /// </para>
    /// <para>
    /// Plain paths (no <c>file://</c> prefix) are returned unchanged.
    /// </para>
    /// </remarks>
    public static string LocalPathFromUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!IsLocalFileUrl(url))
        {
            return url;
        }

        int offset = LocalFileUrlPrefixLen(url);

        // libgit2 rejects `file://` (offset points at NUL) and
        // `file:///` (offset points at the third slash with nothing after)
        // as invalid: the path must be non-empty and not start with another
        // slash (fs_path.c:511-513 — `file:////x` and
        // `file://localhost//x` both fail). A path component that starts
        // with another '/' right after the prefix is not a valid local file
        // URI, on every platform.
        if (offset >= url.Length || url[offset] == '/')
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"'{url}' is not a valid local file URI",
                GitErrorCategory.Config);
        }

        // On POSIX, libgit2 does `offset--` so the leading '/' is retained.
        // On Windows, the offset points just past the third slash, so
        // `file:///C:/repo` → `C:/repo` (forward slashes preserved, matching
        // the C reference which does not normalize separators here).
        int pathStart = OperatingSystem.IsWindows() ? offset : offset - 1;
        string rest = url[pathStart..];

        if (rest.Length == 0)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"'{url}' is not a valid local file URI",
                GitErrorCategory.Config);
        }

        // Percent-decode (matches git__percent_decode in fs_path.c).
        // No separator normalization or canonicalization — libgit2 returns
        // the decoded path verbatim and lets downstream PathHelpers handle it.
        return DecodePathBytes(PercentDecode(rest));
    }

    /// <summary>
    /// Decodes <c>%XX</c> escapes into raw bytes, matching
    /// <c>git__percent_decode</c> (fs_path.c:442-470). Every <c>%XX</c> with
    /// valid hex is decoded to the raw byte; invalid sequences and a trailing
    /// <c>%</c> are appended literally.
    /// </summary>
    private static byte[] PercentDecode(string input)
    {
        var bytes = new List<byte>(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c != '%' || i >= input.Length - 2)
            {
                bytes.Add((byte)c);
                i++;
                continue;
            }

            int hi = FromHex(input[i + 1]);
            int lo = FromHex(input[i + 2]);
            if (hi < 0 || lo < 0)
            {
                bytes.Add((byte)c);
                i++;
                continue;
            }

            bytes.Add((byte)((hi << 4) | lo));
            i += 3;
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Converts a raw-byte path to a .NET string. Valid UTF-8 sequences are
    /// decoded to their Unicode chars (so <c>%C3%A9</c> → <c>é</c>); bytes
    /// that are not valid UTF-8 are mapped via Latin-1 to the char with the
    /// same code point (so an isolated <c>%E9</c> → <c>é</c>, matching C's
    /// raw-byte path semantics).
    /// </summary>
    private static string DecodePathBytes(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            if (b < 0x80)
            {
                sb.Append((char)b);
                i++;
                continue;
            }

            int seqLen = b switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };

            if (seqLen > 0 && i + seqLen <= bytes.Length)
            {
                bool valid = true;
                for (int k = 1; k < seqLen; k++)
                {
                    if (bytes[i + k] is < 0x80 or > 0xBF)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    int cp = seqLen switch
                    {
                        2 => ((b & 0x1F) << 6) | (bytes[i + 1] & 0x3F),
                        3 => ((b & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F),
                        _ => ((b & 0x07) << 18) | ((bytes[i + 1] & 0x3F) << 12) | ((bytes[i + 2] & 0x3F) << 6) | (bytes[i + 3] & 0x3F),
                    };

                    // Reject overlong encodings (E0 80..9F, F0 80..8F),
                    // surrogates, and out-of-range code points.
                    bool overlong = seqLen switch
                    {
                        2 => cp < 0x80,
                        3 => cp < 0x800,
                        _ => cp < 0x10000,
                    };

                    if (!overlong && cp is <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                        i += seqLen;
                        continue;
                    }
                }
            }

            // Invalid byte → Latin-1 (char with the same code point).
            sb.Append((char)b);
            i++;
        }

        return sb.ToString();
    }

    private static int FromHex(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }

        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }

    /// <summary>
    /// Returns the byte offset into <paramref name="fileUrl"/> just past the
    /// <c>file://</c> prefix and the optional <c>localhost/</c> host segment.
    /// Matches <c>local_file_url_prefixlen</c> in <c>fs_path.c</c>.
    /// </summary>
    /// <remarks>
    /// Returns <c>8</c> for <c>file:///path</c> (points at <c>path</c>) and
    /// <c>17</c> for <c>file://localhost/path</c> (points at <c>path</c>).
    /// Returns <c>-1</c> for any other form (no <c>file://</c> prefix,
    /// or a non-localhost host).
    /// </remarks>
    private static int LocalFileUrlPrefixLen(string fileUrl)
    {
        if (!fileUrl.StartsWith(FileScheme, StringComparison.Ordinal))
        {
            return -1;
        }

        // file:///... → offset 8 points just past the third slash.
        if (fileUrl.Length > FileScheme.Length && fileUrl[FileScheme.Length] == '/')
        {
            return FileScheme.Length + 1;
        }

        // file://localhost/... → offset 17 points just past the localhost slash.
        if (fileUrl.Length >= FileScheme.Length + LocalhostPrefix.Length &&
            fileUrl.AsSpan(FileScheme.Length, LocalhostPrefix.Length).SequenceEqual(LocalhostPrefix))
        {
            return FileScheme.Length + LocalhostPrefix.Length;
        }

        return -1;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Serializes the workdir file tree to a compact, stable snapshot format
/// for byte-exact comparison against reference <c>git</c> output. Each
/// tracked file is emitted as:
/// <c>&lt;relative-path&gt;\t&lt;sha1-hex&gt;\t&lt;size&gt;\n</c>
/// sorted by path. The SHA-1 is computed over the raw file bytes (not the
/// git blob OID, to avoid filter/autocrlf ambiguity).
/// </summary>
internal static class CheckoutGoldenFormatter
{
    /// <summary>
    /// Captures the workdir snapshot: for each file in <paramref name="trackedPaths"/>,
    /// emits <c>&lt;path&gt;\t&lt;sha1&gt;\t&lt;size&gt;\n</c>, sorted by path.
    /// </summary>
    /// <param name="workdirPath">The absolute path to the working directory.</param>
    /// <param name="trackedPaths">The list of tracked file paths (relative
    /// to <paramref name="workdirPath"/>), typically from the index.</param>
    public static string FormatWorkdirSnapshot(string workdirPath, IEnumerable<string> trackedPaths)
    {
        ArgumentNullException.ThrowIfNull(workdirPath);
        ArgumentNullException.ThrowIfNull(trackedPaths);

        var entries = new List<(string Path, string Sha1, long Size)>();

        foreach (string relativePath in trackedPaths)
        {
            string fullPath = Path.Combine(workdirPath, relativePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            string sha1 = ComputeSha1Hex(bytes);
            entries.Add((relativePath, sha1, bytes.Length));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var sb = new StringBuilder();
        foreach ((string Path, string Sha1, long Size) entry in entries)
        {
            sb.Append(entry.Path);
            sb.Append('\t');
            sb.Append(entry.Sha1);
            sb.Append('\t');
            sb.Append(entry.Size);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    [SuppressMessage("Security", "CA5350:DoNotUseWeakCryptographicAlgorithms", Justification = "Git's object format is defined by SHA-1; this matches `git hash-object` for byte-exact golden comparison.")]
    private static string ComputeSha1Hex(byte[] data)
    {
        // Compute the git blob OID: SHA-1 of "blob <size>\0<content>".
        // This matches `git hash-object` output, which is what the golden
        // script uses.
        byte[] header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        byte[] combined = new byte[header.Length + data.Length];
        Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        Buffer.BlockCopy(data, 0, combined, header.Length, data.Length);

        byte[] hash = SHA1.HashData(combined);
        var sb = new StringBuilder(40);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}

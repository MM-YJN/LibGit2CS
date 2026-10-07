using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Shared helpers for building and writing loose objects in ODB tests.
/// Replaces the per-file <c>WriteLooseObject</c> helpers that used the
/// removed <see cref="GitObjectDb"/> header formatter.
/// </summary>
internal static class OdbTestHelpers
{
    /// <summary>
    /// Builds the raw (uncompressed) bytes of a loose object:
    /// <c>"&lt;type&gt; &lt;declaredSize&gt;\0"</c> followed by the body.
    /// </summary>
    /// <param name="declaredSize">
    /// Size written into the header. May intentionally disagree with
    /// <paramref name="body"/>.Length to build malformed objects.
    /// </param>
    public static byte[] BuildObjectBytes(GitObjectType type, ReadOnlySpan<byte> body, long declaredSize)
    {
        using var buffer = new PooledByteBufferWriter(32 + body.Length);
        Span<byte> header = stackalloc byte[32];
        int headerLength = GitOid.WriteHeader(header, type, declaredSize);
        buffer.Write(header[..headerLength]);
        buffer.Write(body);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes a loose object file — zlib(header + body) at the OID-derived
    /// path — with a header size matching the body length, and returns its OID.
    /// </summary>
    public static GitOid WriteLooseObject(string objectsDir, GitObjectType type, byte[] body)
        => WriteLooseObject(objectsDir, type, body, body.Length);

    /// <summary>
    /// Writes a loose object file — zlib(header + body) at the OID-derived
    /// path — and returns the true OID of the body (matching
    /// <see cref="GitObjectDb.HashObject"/>), which is independent of
    /// <paramref name="declaredSize"/>.
    /// </summary>
    /// <param name="declaredSize">
    /// Size written into the header. May intentionally disagree with the body
    /// length to test declared-size mismatch handling.
    /// </param>
    public static GitOid WriteLooseObject(string objectsDir, GitObjectType type, byte[] body, long declaredSize)
    {
        GitOid oid = GitObjectDb.HashObject(type, body, GitHashAlgorithmKind.Sha1);
        byte[] compressed = ZlibTestHelpers.CompressLooseObject(BuildObjectBytes(type, body, declaredSize));
        string path = Path.Combine(objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, compressed);
        return oid;
    }
}

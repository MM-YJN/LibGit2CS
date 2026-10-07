using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Inline golden fixtures copied from libgit2's
/// <c>tests/libgit2/object/raw/data.h</c>. These are the canonical known
/// object bodies + SHA-1 OIDs used to validate parsers and hashing.
/// </summary>
internal static class ObjectFixtures
{
    /// <summary>
    /// SHA-1 OID of <see cref="CommitBody"/>: <c>3d7f8a6af076c8c3f20071a8935cdbe8228594d1</c>.
    /// </summary>
    public static readonly GitOid CommitId = GitOid.Parse(
        "3d7f8a6af076c8c3f20071a8935cdbe8228594d1".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-1 OID of <see cref="TreeBody"/>: <c>dff2da90b254e1beb889d1f1f1288be1803782df</c>.
    /// </summary>
    public static readonly GitOid TreeId = GitOid.Parse(
        "dff2da90b254e1beb889d1f1f1288be1803782df".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-1 OID of <see cref="TagBody"/>: <c>09d373e1dfdc16b129ceec6dd649739911541e05</c>.
    /// </summary>
    public static readonly GitOid TagId = GitOid.Parse(
        "09d373e1dfdc16b129ceec6dd649739911541e05".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-1 OID of the empty blob: <c>e69de29bb2d1d6434b8b29ae775ad8c2e48c5391</c>.
    /// </summary>
    public static readonly GitOid ZeroId = GitOid.Parse(
        "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-1 OID of the 1-byte blob <c>\n</c>: <c>8b137891791fe96927ad78e64b0aad7bded08bdc</c>.
    /// </summary>
    public static readonly GitOid OneId = GitOid.Parse(
        "8b137891791fe96927ad78e64b0aad7bded08bdc".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-1 OID of the 2-byte blob <c>a\n</c>: <c>78981922613b2afb6025042ff6bd878ac1994e85</c>.
    /// </summary>
    public static readonly GitOid TwoId = GitOid.Parse(
        "78981922613b2afb6025042ff6bd878ac1994e85".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>Tree OID referenced inside <see cref="CommitBody"/>.</summary>
    public static readonly GitOid InnerTreeId = GitOid.Parse(
        "dff2da90b254e1beb889d1f1f1288be1803782df".AsSpan(), GitHashAlgorithmKind.Sha1);

    /// <summary>Raw commit body (no <c>"commit &lt;size&gt;\0"</c> header).</summary>
    public static ReadOnlyMemory<byte> CommitBody => Encoding.UTF8.GetBytes(
        "tree dff2da90b254e1beb889d1f1f1288be1803782df\n" +
        "author A U Thor <author@example.com> 1227814297 +0000\n" +
        "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
        "\n" +
        "A one-line commit summary\n" +
        "\n" +
        "The body of the commit message, containing further explanation\n" +
        "of the purpose of the changes introduced by the commit.\n" +
        "\n" +
        "Signed-off-by: A U Thor <author@example.com>\n");

    /// <summary>Raw tag body (no <c>"tag &lt;size&gt;\0"</c> header).</summary>
    public static ReadOnlyMemory<byte> TagBody => Encoding.UTF8.GetBytes(
        "object 3d7f8a6af076c8c3f20071a8935cdbe8228594d1\n" +
        "type commit\n" +
        "tag v0.0.1\n" +
        "tagger C O Mitter <committer@example.com> 1227814297 +0000\n" +
        "\n" +
        "This is the tag object for release v0.0.1\n");

    /// <summary>
    /// Raw tree body with 4 entries: one, some, two, zero (each Regular mode 100644).
    /// </summary>
    public static ReadOnlyMemory<byte> TreeBody => BuildTreeBody();

    /// <summary>1-byte blob <c>\n</c>.</summary>
    public static ReadOnlyMemory<byte> OneBody => "\n"u8.ToArray();

    /// <summary>2-byte blob <c>a\n</c>.</summary>
    public static ReadOnlyMemory<byte> TwoBody => "a\n"u8.ToArray();

    /// <summary>Empty blob (zero bytes).</summary>
    public static ReadOnlyMemory<byte> ZeroBody => Array.Empty<byte>();

    /// <summary>
    /// Builds the canonical tree body with 4 entries (one/some/two/zero), each
    /// a Regular blob. Entries are pre-sorted in tree order (all Regular, so
    /// bytewise name comparison). Matches <c>tree_data</c> in <c>data.h</c>.
    /// </summary>
    private static byte[] BuildTreeBody()
    {
        using var ms = new MemoryStream();
        // Entry: "100644 one\0" + raw OID (20 bytes for SHA-1)
        AppendEntry(ms, "100644", "one", OneId);
        AppendEntry(ms, "100644", "some", GitOid.Parse(
            "fd8430bc864cfcd5f10e5590f8a447e01b942bfe".AsSpan(), GitHashAlgorithmKind.Sha1));
        AppendEntry(ms, "100644", "two", TwoId);
        AppendEntry(ms, "100644", "zero", ZeroId);
        return ms.ToArray();
    }

    private static void AppendEntry(MemoryStream ms, string mode, string name, GitOid oid)
    {
        byte[] header = Encoding.UTF8.GetBytes($"{mode} {name}\0").ToArray();
        ms.Write(header);
        ms.Write(oid.RawBytes.ToArray());
    }
}

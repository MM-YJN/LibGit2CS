using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class TreeTests
{
    [Fact]
    public void Parse_CanonicalTree_FourEntries()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        Assert.Equal(4, tree.EntryCount);
    }

    [Fact]
    public void Parse_CanonicalTree_EntriesInSortedOrder()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        string[] names = tree.Select(e => e.Name.ToUtf8String()).ToArray();
        Assert.Equal(["one", "some", "two", "zero"], names);
    }

    [Fact]
    public void Parse_CanonicalTree_EntryFieldsCorrect()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        GitTreeEntry? one = tree["one"];
        Assert.NotNull(one);
        Assert.Equal(GitFileMode.Regular, one!.Value.Mode);
        Assert.Equal(GitObjectType.Blob, one.Value.Type);
        Assert.Equal("one", one.Value.Name.ToUtf8String());
        Assert.Equal(ObjectFixtures.OneId, one.Value.Id);
    }

    [Fact]
    public void Parse_EmptyTree_ZeroEntries()
    {
        var tree = GitTree.Parse(owner: null, GitOid.EmptyTreeSha1, ReadOnlyMemory<byte>.Empty, GitHashAlgorithmKind.Sha1);

        Assert.Equal(0, tree.EntryCount);
    }

    // ----- Byte-faithful tree entry names -----

    [Fact]
    public void Parse_NonUtf8EntryName_BytesPreservedAndLookupWorks()
    {
        // An entry whose name is invalid UTF-8 (0xFF 0xFE 0x80). A decode with
        // UTF-8 replacement fallback would make the original bytes
        // irrecoverable and break byte-equality with the index side (surfacing
        // as a phantom rename). The name is stored byte-faithfully.
        byte[] nameBytes = [0xFF, 0xFE, 0x80];
        GitOid entryOid = ObjectFixtures.OneId;

        using var body = new MemoryStream();
        body.Write("100644 "u8);             // mode + space
        body.Write(nameBytes);               // raw (non-UTF-8) name bytes
        body.WriteByte(0);                   // NUL terminator
        body.Write(entryOid.RawBytes.ToArray()); // raw OID (SHA-1 = 20 bytes)
        ReadOnlyMemory<byte> treeBody = body.ToArray();

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, treeBody, GitHashAlgorithmKind.Sha1);

        Assert.Equal(1, tree.EntryCount);

        // The entry name bytes are preserved EXACTLY (zero-copy slice of raw).
        GitTreeEntry entry = tree.EntryByIndex(0)!.Value;
        Assert.Equal(nameBytes, entry.Name.Span.ToArray());

        // Display decode is lossy (U+FFFD replacement) — matches git's own display
        // limitation. This is egress-only; the stored bytes are intact.
        Assert.Equal("\uFFFD\uFFFD\uFFFD", entry.Name.ToUtf8String());

        // Byte-faithful lookup by the ORIGINAL bytes finds the entry.
        GitTreeEntry? found = tree.EntryByName(GitPath.FromUtf8Bytes(nameBytes));
        Assert.NotNull(found);
        Assert.Equal(entry, found!.Value);

        // A lookup by the lossy decoded string does NOT match: the decoded bytes
        // (0xEF 0xBF 0xBD x3) differ from the original (0xFF 0xFE 0x80). This is
        // the proof that a string-based design could not round-trip
        // this name and would have failed to match it against the index side.
        var lossyPath = GitPath.FromUtf8String(entry.Name.ToUtf8String());
        Assert.NotEqual(entry.Name, lossyPath);
        Assert.Null(tree.EntryByName(lossyPath));
    }

    [Fact]
    public void Parse_NonUtf8EntryName_TreeOrderingUsesRawBytes()
    {
        // Tree ordering must operate on raw bytes: here a non-ASCII-byte prefix
        // name (0xFF...) sorts after ASCII names since 0xFF > 0x7F > all ASCII.
        GitOid oid = ObjectFixtures.OneId;
        byte[] hiName = [0xFF];

        using var body = new MemoryStream();
        body.Write("100644 ascii\0"u8);
        body.Write(oid.RawBytes.ToArray());
        body.Write("100644 "u8);
        body.Write(hiName);
        body.WriteByte(0);
        body.Write(oid.RawBytes.ToArray());
        ReadOnlyMemory<byte> treeBody = body.ToArray();

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, treeBody, GitHashAlgorithmKind.Sha1);

        // Entries are pre-sorted on disk (ascii < 0xFF); parser preserves order.
        Assert.Equal("ascii", (string)tree.EntryByIndex(0)!.Value.Name.ToUtf8String());
        Assert.Equal(hiName, tree.EntryByIndex(1)!.Value.Name.Span.ToArray());
    }

    [Fact]
    public void Parse_SingleEntry_TreeMode()
    {
        var oid = GitOid.Parse("1234567890123456789012345678901234567890".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTree([("040000", "subdir", oid)]);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        GitTreeEntry? entry = tree["subdir"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.Tree, entry!.Value.Mode);
        Assert.Equal(GitObjectType.Tree, entry.Value.Type);
        Assert.True(entry.Value.IsTree);
    }

    [Fact]
    public void Parse_SingleEntry_ExecutableMode()
    {
        var oid = GitOid.Parse("1234567890123456789012345678901234567890".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTree([("100755", "script.sh", oid)]);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        GitTreeEntry? entry = tree["script.sh"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.Executable, entry!.Value.Mode);
    }

    [Fact]
    public void Parse_SingleEntry_SymlinkMode()
    {
        var oid = GitOid.Parse("1234567890123456789012345678901234567890".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTree([("120000", "link", oid)]);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        GitTreeEntry? entry = tree["link"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.Symlink, entry!.Value.Mode);
    }

    [Fact]
    public void Parse_SingleEntry_GitLinkMode()
    {
        var oid = GitOid.Parse("1234567890123456789012345678901234567890".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTree([("160000", "submodule", oid)]);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        GitTreeEntry? entry = tree["submodule"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.GitLink, entry!.Value.Mode);
        Assert.Equal(GitObjectType.Commit, entry!.Value.Type);
        Assert.True(entry.Value.IsGitLink);
        Assert.False(entry.Value.IsTree);
    }

    [Fact]
    public void Parse_MultipleEntries_LeadingSpacesInName()
    {
        // libgit2 test: tree names can have leading whitespace.
        var oid = GitOid.Parse("1234567890123456789012345678901234567890".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTree([("100644", "       bar", oid)]);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal(1, tree.EntryCount);
        Assert.Equal("       bar", tree.EntryByIndex(0)!.Value.Name.ToUtf8String());
    }

    [Fact]
    public void Parse_InvalidMode_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes("10x644 foo\0" + new string('x', 20));
        GitException ex = Assert.Throws<GitException>(() =>
            GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_MissingModeSeparator_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes("100644foo\0" + new string('x', 20));
        Assert.Throws<GitException>(() =>
            GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_EmptyFilename_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes("100644 \0" + new string('x', 20));
        Assert.Throws<GitException>(() =>
            GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_TruncatedOid_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes("100644 foo\0" + new string('x', 19));
        Assert.Throws<GitException>(() =>
            GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void EntryByName_Nonexistent_ReturnsNull()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        Assert.Null(tree["nonexistent"]);
    }

    [Fact]
    public void EntryByName_PrefixMatch_ReturnsExactOnly()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        // "tw" is a prefix of "two" but we need exact match.
        Assert.Null(tree["tw"]);
        Assert.NotNull(tree["two"]);
    }

    [Fact]
    public void EntryByIndex_OutOfRange_ReturnsNull()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        Assert.Null(tree.EntryByIndex(-1));
        Assert.Null(tree.EntryByIndex(4));
        Assert.NotNull(tree.EntryByIndex(0));
    }

    [Fact]
    public void EntryById_Existing_ReturnsEntry()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        GitTreeEntry? entry = tree.EntryById(ObjectFixtures.OneId);
        Assert.NotNull(entry);
        Assert.Equal("one", entry!.Value.Name.ToUtf8String());
    }

    [Fact]
    public void EntryById_Nonexistent_ReturnsNull()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        var other = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Null(tree.EntryById(other));
    }

    [Fact]
    public void GetEnumerator_YieldsAllEntries()
    {
        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, ObjectFixtures.TreeBody, GitHashAlgorithmKind.Sha1);

        var entries = tree.ToList();
        Assert.Equal(4, entries.Count);
        Assert.Equal("one", entries[0].Name.ToUtf8String());
        Assert.Equal("zero", entries[3].Name.ToUtf8String());
    }

    [Fact]
    public void TreeEntryCompareTo_TreeVsBlob_TreeSortsBefore()
    {
        // Per git tree sort order: directory entry "foo" sorts as "foo/"
        // which sorts AFTER "foo" (blob) because '/' > '\0'.
        // So foo_blob < foo_tree in tree order.
        GitOid oid = GitOid.Empty;
        var blob = new GitTreeEntry(GitFileMode.Regular, oid, GitPath.FromUtf8String("foo"), GitObjectType.Blob);
        var dir = new GitTreeEntry(GitFileMode.Tree, oid, GitPath.FromUtf8String("foo"), GitObjectType.Tree);

        Assert.True(blob.CompareTo(dir) < 0);
        Assert.True(dir.CompareTo(blob) > 0);
    }

    [Fact]
    public void TreeEntryCompareTo_FooVsFooC_FooFirst()
    {
        // "." (0x2E) < "/" (0x2F), so "foo.c" sorts before "foo/" (foo as tree).
        GitOid oid = GitOid.Empty;
        var fooBlob = new GitTreeEntry(GitFileMode.Regular, oid, GitPath.FromUtf8String("foo.c"), GitObjectType.Blob);
        var fooTree = new GitTreeEntry(GitFileMode.Tree, oid, GitPath.FromUtf8String("foo"), GitObjectType.Tree);

        Assert.True(fooBlob.CompareTo(fooTree) < 0);
    }

    [Fact]
    public void TreeEntryCompareTo_PlainBlobsBytewise()
    {
        GitOid oid = GitOid.Empty;
        var a = new GitTreeEntry(GitFileMode.Regular, oid, GitPath.FromUtf8String("abc"), GitObjectType.Blob);
        var b = new GitTreeEntry(GitFileMode.Regular, oid, GitPath.FromUtf8String("abd"), GitObjectType.Blob);

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(0, a.CompareTo(a).CompareTo(0));
    }

    [Fact]
    public void Parse_Sha256Entries_Works()
    {
        var sha256Oid = GitOid.Parse(
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".AsSpan(),
            GitHashAlgorithmKind.Sha256);
        byte[] body = BuildTreeSha256([("100644", "name", sha256Oid)]);

        var tree = GitTree.Parse(owner: null, GitOid.EmptyTreeSha256, body, GitHashAlgorithmKind.Sha256);

        Assert.Equal(1, tree.EntryCount);
        Assert.Equal(sha256Oid, tree["name"]!.Value.Id);
    }

    // --- Regression: tree entry names must decode as UTF-8 ---

    [Fact]
    public void Parse_NonAsciiUtf8Name_DecodesAsUtf8()
    {
        // The HEAD tree stores entry names as raw bytes; for "汉语.txt" those
        // bytes are the UTF-8 encoding. The parser must decode them as UTF-8
        // (matching the index reader and TreeCache), not Latin-1, otherwise the
        // same on-disk bytes yield unequal strings across the two sides of a
        // HEAD→Index diff.
        const string name = "汉语.txt";
        byte[] nameUtf8 = Encoding.UTF8.GetBytes(name);
        var oid = GitOid.Parse("1234567890123456789012345678901234567890".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTreeRawName("100644", nameUtf8, oid);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal(1, tree.EntryCount);
        GitTreeEntry? entry = tree[name];
        Assert.NotNull(entry);
        Assert.Equal(name, entry!.Value.Name.ToUtf8String());
        // Round-trips byte-for-byte through UTF-8 (would fail under Latin-1).
        Assert.Equal(nameUtf8, Encoding.UTF8.GetBytes(entry.Value.Name.ToUtf8String()));
    }

    [Fact]
    public void Parse_NonAsciiUtf8Name_EntryByPathResolves()
    {
        // The homing binary search compares caller-supplied names (UTF-8 strings
        // from the index/workdir) against decoded tree names. Both sides must
        // be UTF-8 for a non-ASCII lookup to resolve.
        const string name = "üm laut.txt";
        byte[] nameUtf8 = Encoding.UTF8.GetBytes(name);
        var oid = GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] body = BuildTreeRawName("100644", nameUtf8, oid);

        var tree = GitTree.Parse(owner: null, ObjectFixtures.TreeId, body, GitHashAlgorithmKind.Sha1);

        Assert.NotNull(tree[name]);
    }

    private static byte[] BuildTreeRawName(string mode, byte[] nameBytes, GitOid oid)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(mode + " "));
        ms.Write(nameBytes);
        ms.WriteByte(0);
        ms.Write(oid.RawBytes.ToArray());
        return ms.ToArray();
    }

    private static byte[] BuildTree((string mode, string name, GitOid oid)[] entries)
    {
        using var ms = new MemoryStream();
        foreach ((string? mode, string? name, GitOid oid) in entries)
        {
            byte[] header = Encoding.UTF8.GetBytes($"{mode} {name}\0");
            ms.Write(header);
            ms.Write(oid.RawBytes.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] BuildTreeSha256((string mode, string name, GitOid oid)[] entries)
        => BuildTree(entries); // Same format, different OID size — OID already SHA-256.
}

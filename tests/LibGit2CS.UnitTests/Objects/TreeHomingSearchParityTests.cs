using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Regression tests for the tree-entry homing search. </summary> <remarks> C <c>tree.c:184</c> breaks the forward linear scan only when
/// <c>homing_search_cmp &lt; 0</c> and the backward scan only when <c>&gt; 0</c>. A positive entry
/// sitting between prefix-matches (e.g. <c>"foa"</c> between <c>"fo"</c> and <c>"foo"</c>) must not stop the scan, since the homing
/// comparator is not monotonic over tree order. </remarks>
public sealed class TreeHomingSearchParityTests
{
    private static GitTree ParseTree(params string[] names)
    {
        var oid = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);
        using var body = new MemoryStream();
        foreach (string name in names)
        {
            body.Write(Encoding.UTF8.GetBytes($"100644 {name}\0"));
            body.Write(oid.RawBytes.ToArray());
        }

        return GitTree.Parse(owner: null, GitOid.EmptyTreeSha1, body.ToArray(), GitHashAlgorithmKind.Sha1);
    }

    [Fact]
    public void EntryByName_NonMonotonicHoming_PositiveEntryBetweenPrefixes_FindsExact()
    {
        // Entries sorted in canonical tree order; homing comparator values for
        // key "foo" are 0, +, 0, 0, -. The "+" entry ("foa") sits between
        // prefix matches and must be SKIPPED, not stop the scan.
        GitTree tree = ParseTree("fo", "foa", "foo", "foobar", "fop");

        GitTreeEntry? found = tree.EntryByName("foo");
        Assert.NotNull(found);
        Assert.Equal("foo", found!.Value.Name.ToUtf8String());
    }

    [Fact]
    public void EntryByName_NonMonotonicHoming_PrefixChain_FindsLongestExact()
    {
        // Key "foob": homing values 0, 0, +, 0, - — the "+" entry ("fooc") is
        // AFTER the match here; the backward scan must also survive non-zero
        // entries between prefix matches.
        GitTree tree = ParseTree("fo", "foo", "foob", "fooc", "foobar");

        GitTreeEntry? found = tree.EntryByName("foob");
        Assert.NotNull(found);
        Assert.Equal("foob", found!.Value.Name.ToUtf8String());
    }

    [Fact]
    public void EntryByName_NonMonotonicHoming_NoExactMatch_ReturnsNull()
    {
        // "fooo" and "fopx" are not present; scans must terminate without a
        // false hit. "fo" IS present (as its own entry) and must be found.
        GitTree tree = ParseTree("fo", "foa", "foo", "foobar", "fop");

        Assert.Null(tree.EntryByName("fooo"));
        Assert.Null(tree.EntryByName("fopx"));
        Assert.NotNull(tree.EntryByName("fo"));
    }

    [Fact]
    public void EntryByName_MatchAtHomigStart_StillFinds()
    {
        GitTree tree = ParseTree("a", "b", "b1", "ba", "c");
        GitTreeEntry? found = tree.EntryByName("b");
        Assert.NotNull(found);
        Assert.Equal("b", found!.Value.Name.ToUtf8String());
    }
}

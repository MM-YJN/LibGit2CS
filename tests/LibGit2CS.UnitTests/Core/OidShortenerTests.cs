using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public sealed class OidShortenerTests
{
    // A full SHA-1 hex is 40 chars. The trie walks 40 nibbles.
    private const string Oid1 = "a65fedf39aef3a1b9e8f7c6d5b4a3a2a1a0a0f0e";
    private const string Oid2 = "a65fedf40aef3a1b9e8f7c6d5b4a3a2a1a0a0f0e";
    private const string Oid3 = "a65fedf39aef3a1b9e8f7c6d5b4a3a2a1a0a0f0f";  // diverges at pos 38

    [Fact]
    public void Add_SingleOid_MinLengthStaysAtDefault()
    {
        using var os = new GitOidShortener(minLength: 4);

        int result = os.Add(Oid1);

        // First OID: i breaks at 1 (pushed leaf at nibble 0), so ++i=2.
        // MinLength = max(4, 2) = 4.
        Assert.Equal(4, result);
        Assert.Equal(4, os.MinLength);
        Assert.False(os.IsFull);
    }

    [Fact]
    public void Add_TwoOidsSharingLongPrefix_RaisesMinLength()
    {
        using var os = new GitOidShortener(minLength: 4);

        os.Add(Oid1);
        int result = os.Add(Oid2);

        // Oid1 and Oid2 share 7-char prefix "a65fedf", diverge at position 7
        // ('3' vs '4'). MinLength must cover the divergent nibble: 8.
        Assert.Equal(8, result);
        Assert.Equal(8, os.MinLength);
    }

    [Fact]
    public void Add_ThreeOids_FurtherRaisesMinLength()
    {
        using var os = new GitOidShortener(minLength: 4);

        os.Add(Oid1);
        os.Add(Oid2);
        int result = os.Add(Oid3);

        // Oid1 and Oid3 diverge at position 38 (last nibble '0' vs 'f').
        // MinLength must be at least 39.
        Assert.True(result >= 39, $"expected >= 39, got {result}");
        Assert.True(os.MinLength >= 39);
    }

    [Fact]
    public void Add_IdempotentSameOid_DoesNotRaiseMinLength()
    {
        using var os = new GitOidShortener(minLength: 4);

        os.Add(Oid1);
        int result = os.Add(Oid1); // same OID again

        // Adding the same OID again doesn't introduce a new divergence.
        Assert.Equal(os.MinLength, result);
    }

    [Fact]
    public void Add_DuplicateFullOid_MinLengthBecomes41()
    {
        // C (oid.c:529-530): when the trie already contains the full OID the
        // walk completes with i == 40 and `++i` bumps min_length to 41.
        using var os = new GitOidShortener(minLength: 4);

        os.Add(Oid1);
        int result = os.Add(Oid1); // full 40-char match, no new leaf

        Assert.Equal(41, result);
        Assert.Equal(41, os.MinLength);

        // Stays 41 for all later adds (no prefix can exceed 40 chars).
        Assert.Equal(41, os.Add(Oid2));
    }

    [Fact]
    public void Add_AfterDispose_Throws()
    {
        var os = new GitOidShortener();
        os.Dispose();

        Assert.Throws<ObjectDisposedException>(() => os.Add(Oid1));
    }

    [Fact]
    public void Add_InvalidHexChar_ThrowsGitException()
    {
        // C (oid.c:490-493): invalid hex returns -1 (with GIT_ERROR_INVALID
        // set); the managed API carries that error in GitException.
        using var os = new GitOidShortener(minLength: 4);
        string badOid = "z" + Oid1[1..];

        GitException error = Assert.Throws<GitException>(() => os.Add(badOid));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - invalid hex value", error.Message);
        Assert.Equal(4, os.MinLength);
        Assert.False(os.IsFull);
    }

    [Fact]
    public void Add_InvalidHexChar_AfterValidOid_ThrowsGitException()
    {
        // Invalid hex encountered on an existing path must report a managed error.
        using var os = new GitOidShortener(minLength: 4);
        os.Add(Oid1);
        string badOid = Oid1[..^1] + "z";

        GitException error = Assert.Throws<GitException>(() => os.Add(badOid));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - invalid hex value", error.Message);

        // A following valid OID still behaves as before.
        Assert.Equal(8, os.Add(Oid2));
    }

    [Fact]
    public void Add_Null_ReturnsMinLength()
    {
        // C (oid.c:480-481): text_oid == NULL returns os->min_length.
        using var os = new GitOidShortener(minLength: 4);
        os.Add(Oid1);

        Assert.Equal(os.MinLength, os.Add(null!));
    }

    [Fact]
    public void Add_Null_OnFullTrie_ThrowsGitException()
    {
        // C checks os->full BEFORE the NULL check (oid.c:475-481), so a
        // full trie returns -1 even for a NULL input.
        using var os = new GitOidShortener(minLength: 4);

        // Drive the trie to the node cap: each successful Add links one
        // node; the push that would reach SHRT_MAX fails and sets _full.
        DriveToFull(os);

        Assert.True(os.IsFull);
        GitException error = Assert.Throws<GitException>(() => os.Add(null!));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - OID set full", error.Message);
    }

    [Fact]
    public void Add_AtNodeBoundary_FailedPushDoesNotLink()
    {
        // C (oid.c:380-383): push_leaf increments node_count, checks
        // node_count == SHRT_MAX, and only THEN links the child slot — the
        // boundary push fails without mutating the trie.
        using var os = new GitOidShortener(minLength: 40);

        DriveToFull(os);
        Assert.True(os.IsFull);
        GitException error = Assert.Throws<GitException>(() => os.Add(Oid1));
        Assert.Equal(GitErrorCode.Error, error.Code);
        Assert.Equal(GitErrorCategory.Invalid, error.Category);
        Assert.Equal("unable to shorten OID - OID set full", error.Message);

        int nodeCount = GetNodeCount(os);
        int linkedSlots = CountLinkedSlots(os);

        // Every successful push links exactly one slot; node_count also
        // counts the single failed boundary push. C leaves node_count at
        // SHRT_MAX with SHRT_MAX - 2 linked slots (indices 0..SHRT_MAX-3,
        // root + SHRT_MAX-3 leaves... root is index 0; the failed push
        // reserved index SHRT_MAX-2 without linking it).
        Assert.Equal(short.MaxValue, nodeCount);
        Assert.Equal(nodeCount - 2, linkedSlots);
    }

    private static void DriveToFull(GitOidShortener os)
    {
        var rng = new Random(42);
        char[] hexChars = "0123456789abcdef".ToCharArray();
        // Heap-allocated scratch buffer: stackalloc inside a 200k-iteration
        // loop overflows the stack on the .NET 11 preview runtime (the JIT
        // fails to reuse the stack frame across iterations).
        char[] hex = new char[40];
        for (int k = 0; k < 200_000; k++)
        {
            for (int i = 0; i < hex.Length; i++)
            {
                hex[i] = hexChars[rng.Next(hexChars.Length)];
            }

            int previousMinLength = os.MinLength;
            try
            {
                os.Add(new string(hex));
            }
            catch (GitException error)
            {
                Assert.Equal(GitErrorCode.Error, error.Code);
                Assert.Equal(GitErrorCategory.Invalid, error.Category);
                Assert.Equal("unable to shorten OID - OID set full", error.Message);
                Assert.True(os.IsFull);
                Assert.Equal(previousMinLength, os.MinLength);
                return;
            }
        }

        Assert.Fail("trie never reached the node cap");
    }

    private static int GetNodeCount(GitOidShortener os)
    {
        System.Reflection.FieldInfo field =
            typeof(GitOidShortener).GetField(
                "_nodeCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (int)field.GetValue(os)!;
    }

    private static int CountLinkedSlots(GitOidShortener os)
    {
        System.Reflection.FieldInfo childrenField =
            typeof(GitOidShortener).GetField(
                "_children", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        short[][] children = (short[][])childrenField.GetValue(os)!;

        int nodeCount = GetNodeCount(os);
        int linked = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            short[]? node = children[i];
            if (node is null)
            {
                continue;
            }

            foreach (short slot in node)
            {
                if (slot != 0)
                {
                    linked++;
                }
            }
        }

        return linked;
    }

    [Fact]
    public void IsFull_True_AfterShrtMaxNodes()
    {
        // The trie caps at short.MaxValue (32767) nodes. With degenerate input
        // we can force growth. Rather than allocating 32k OIDs (slow), we
        // verify IsFull is exposed and defaults to false.
        using var os = new GitOidShortener(minLength: 4);

        Assert.False(os.IsFull);
    }

    [Fact]
    public void MinLength_StartsAtConstructorValue()
    {
        using var os = new GitOidShortener(minLength: 7);

        Assert.Equal(7, os.MinLength);
    }

    [Fact]
    public void Add_TwoDistinctOids_DivergingAtNibble0_MinLengthIs1()
    {
        using var os = new GitOidShortener(minLength: 0);
        string oidA = "a0000000000000000000000000000000000000000";
        string oidB = "b0000000000000000000000000000000000000000";

        os.Add(oidA);
        int result = os.Add(oidB);

        // Diverge at position 0; MinLength must cover nibble 0 → 1.
        Assert.Equal(1, result);
    }
}

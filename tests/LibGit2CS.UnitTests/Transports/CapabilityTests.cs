using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

public class CapabilityTests
{
    [Fact]
    public void DetectCapabilities_AllFlags()
    {
        string caps = "ofs-delta multi_ack_detailed side-band-64k include-tag delete-refs thin-pack shallow";
        var refPkt = new GitRefPacket(
            new GitRemoteHead(false, default, default, "HEAD", null),
            caps);

        var set = new GitSmartCapabilitySet();
        bool found = GitSmartProtocol.DetectCapabilities(refPkt, set);

        Assert.True(found);
        Assert.True(set.HasCapabilities);
        Assert.Equal(
            GitSmartCapabilities.OfsDelta |
            GitSmartCapabilities.MultiAckDetailed |
            GitSmartCapabilities.SideBand64k |
            GitSmartCapabilities.IncludeTag |
            GitSmartCapabilities.DeleteRefs |
            GitSmartCapabilities.ThinPack |
            GitSmartCapabilities.Shallow,
            set.Flags);
    }

    [Fact]
    public void DetectCapabilities_MultiAck_Basic()
    {
        GitRefPacket refPkt = MakeRef("multi_ack");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.True((set.Flags & GitSmartCapabilities.MultiAck) != 0);
        Assert.True((set.Flags & GitSmartCapabilities.MultiAckDetailed) == 0);
    }

    [Fact]
    public void DetectCapabilities_SideBand_Basic()
    {
        GitRefPacket refPkt = MakeRef("side-band");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.True((set.Flags & GitSmartCapabilities.SideBand) != 0);
        Assert.True((set.Flags & GitSmartCapabilities.SideBand64k) == 0);
    }

    [Fact]
    public void DetectCapabilities_PushOptions()
    {
        GitRefPacket refPkt = MakeRef("push-options");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.True((set.Flags & GitSmartCapabilities.PushOptions) != 0);
    }

    [Fact]
    public void DetectCapabilities_WantTipSha1()
    {
        GitRefPacket refPkt = MakeRef("allow-tip-sha1-in-want");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.True((set.Flags & GitSmartCapabilities.WantTipSha1) != 0);
    }

    [Fact]
    public void DetectCapabilities_WantReachableSha1()
    {
        GitRefPacket refPkt = MakeRef("allow-reachable-sha1-in-want");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.True((set.Flags & GitSmartCapabilities.WantReachableSha1) != 0);
    }

    [Fact]
    public void DetectCapabilities_ObjectFormat()
    {
        GitRefPacket refPkt = MakeRef("object-format=sha256");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.Equal("sha256", set.ObjectFormat);
    }

    [Fact]
    public void DetectCapabilities_Agent()
    {
        GitRefPacket refPkt = MakeRef("agent=git/2.43.0");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.Equal("git/2.43.0", set.Agent);
    }

    [Fact]
    public void DetectCapabilities_Symref()
    {
        GitRefPacket refPkt = MakeRef("symref=HEAD:refs/heads/main");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.Single(set.Symrefs);
        Assert.Equal(("HEAD", "refs/heads/main"), set.Symrefs[0]);
    }

    [Fact]
    public void DetectCapabilities_UnknownCapability_SilentlySkipped()
    {
        GitRefPacket refPkt = MakeRef("unknown-cap foo bar");
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);
        // Should not throw, should not set any flags
        Assert.Equal(GitSmartCapabilities.None, set.Flags);
    }

    [Fact]
    public void DetectCapabilities_Mixed()
    {
        string caps = "multi_ack_detailed side-band-64k ofs-delta thin-pack agent=git/github-gabcdef1 object-format=sha1 symref=HEAD:refs/heads/main";
        GitRefPacket refPkt = MakeRef(caps);
        var set = new GitSmartCapabilitySet();
        GitSmartProtocol.DetectCapabilities(refPkt, set);

        Assert.True((set.Flags & GitSmartCapabilities.MultiAckDetailed) != 0);
        Assert.True((set.Flags & GitSmartCapabilities.SideBand64k) != 0);
        Assert.True((set.Flags & GitSmartCapabilities.OfsDelta) != 0);
        Assert.True((set.Flags & GitSmartCapabilities.ThinPack) != 0);
        Assert.Equal("git/github-gabcdef1", set.Agent);
        Assert.Equal("sha1", set.ObjectFormat);
        Assert.Single(set.Symrefs);
    }

    [Fact]
    public void DetectCapabilities_NullCapabilities_ReturnsFalse()
    {
        var refPkt = new GitRefPacket(
            new GitRemoteHead(false, default, default, "HEAD", null),
            null);
        var set = new GitSmartCapabilitySet();
        bool found = GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.False(found);
    }

    [Fact]
    public void DetectCapabilities_NullRef_ReturnsFalse()
    {
        GitRefPacket? refPkt = null;
        var set = new GitSmartCapabilitySet();
        bool found = GitSmartProtocol.DetectCapabilities(refPkt, set);
        Assert.False(found);
    }

    private static GitRefPacket MakeRef(string caps)
    {
        return new GitRefPacket(
            new GitRemoteHead(false, default, default, "HEAD", null),
            caps);
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class GitObjectTests : IDisposable
{
    private readonly string _tempDir;

    public GitObjectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_GitObjectTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RawGitObject_PropertiesSet()
    {
        var oid = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] raw = "hello"u8.ToArray();

        var obj = new RawGitObject(owner: null, oid, GitObjectType.Blob, raw.Length, raw);

        Assert.Equal(oid, obj.Id);
        Assert.Equal(GitObjectType.Blob, obj.Type);
        Assert.Equal(5, obj.Size);
        Assert.Equal("hello", Encoding.ASCII.GetString(obj.Raw.Span));
        Assert.Null(obj.Owner);
    }

    [Fact]
    public async Task RawGitObject_PeelToSameType_ReturnsSelf()
    {
        var oid = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);
        var obj = new RawGitObject(owner: null, oid, GitObjectType.Blob, 0, default);

        RawGitObject peeled = await obj.PeelAsync<RawGitObject>(TestContext.Current.CancellationToken);

        Assert.Same(obj, peeled);
    }

    [Fact]
    public async Task RawGitObject_PeelToDifferentType_Throws()
    {
        var oid = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);
        var obj = new RawGitObject(owner: null, oid, GitObjectType.Blob, 0, default);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await obj.PeelAsync<OtherGitObject>(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Peel, ex.Code);
    }

    [Fact]
    public void ObjectHeader_RecordStruct()
    {
        var header = new GitObjectHeader(GitObjectType.Tree, 123L);

        Assert.Equal(GitObjectType.Tree, header.Type);
        Assert.Equal(123L, header.Size);
    }

    [Fact]
    public void ObjectType_EnumValues()
    {
        Assert.Equal(0, (int)GitObjectType.Ext1);
        Assert.Equal(1, (int)GitObjectType.Commit);
        Assert.Equal(2, (int)GitObjectType.Tree);
        Assert.Equal(3, (int)GitObjectType.Blob);
        Assert.Equal(4, (int)GitObjectType.Tag);
        Assert.Equal(5, (int)GitObjectType.Ext2);
        Assert.Equal(6, (int)GitObjectType.OfsDelta);
        Assert.Equal(7, (int)GitObjectType.RefDelta);
    }

    private sealed class OtherGitObject : GitObject
    {
        public OtherGitObject()
            : base(owner: null, GitOid.Empty, GitObjectType.Commit, 0, default)
        {
        }
    }
}

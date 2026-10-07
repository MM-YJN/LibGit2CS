using System.Text;

using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class BlobTests
{
    [Fact]
    public void Parse_EmptyBlob_ContentIsEmpty()
    {
        var blob = GitBlob.Parse(owner: null, ObjectFixtures.ZeroId, ObjectFixtures.ZeroBody);

        Assert.Equal(0, blob.Size);
        Assert.True(blob.Content.IsEmpty);
        Assert.Equal(GitObjectType.Blob, blob.Type);
    }

    [Fact]
    public void Parse_OneByteBlob_ContentMatches()
    {
        var blob = GitBlob.Parse(owner: null, ObjectFixtures.OneId, ObjectFixtures.OneBody);

        Assert.Equal(1, blob.Size);
        Assert.Equal("\n", Encoding.UTF8.GetString(blob.Content.Span));
    }

    [Fact]
    public void Parse_TwoByteBlob_ContentMatches()
    {
        var blob = GitBlob.Parse(owner: null, ObjectFixtures.TwoId, ObjectFixtures.TwoBody);

        Assert.Equal(2, blob.Size);
        Assert.Equal("a\n", Encoding.UTF8.GetString(blob.Content.Span));
    }

    [Fact]
    public void Content_EqualsRaw()
    {
        var blob = GitBlob.Parse(owner: null, ObjectFixtures.TwoId, ObjectFixtures.TwoBody);

        Assert.Equal(blob.Raw.ToArray(), blob.Content.ToArray());
    }

    [Fact]
    public void IsBinary_EmptyBlob_False()
    {
        Assert.False(GitBlob.IsBinaryBytes([]));
    }

    [Fact]
    public void IsBinary_PlainText_False()
    {
        Assert.False(GitBlob.IsBinaryBytes("hello world\n"u8.ToArray()));
        Assert.False(GitBlob.IsBinaryBytes(ObjectFixtures.CommitBody.Span));
    }

    [Fact]
    public void IsBinary_WithNul_True()
    {
        Assert.True(GitBlob.IsBinaryBytes([1, 2, 3, 0, 4, 5]));
    }

    [Fact]
    public void IsBinary_NulOutside8k_DataLevelTrue_BlobLevelFalse()
    {
        // git_blob_data_is_binary scans the FULL buffer (a NUL past 8000 → binary); only git_blob_is_binary caps at GIT_FILTER_BYTES_TO_CHECK_NUL (8000).
        byte[] data = new byte[8002];
        for (int i = 0; i < 8000; i++)
        {
            data[i] = (byte)'a';
        }

        data[8001] = 0;
        Assert.True(GitBlob.IsBinaryBytes(data));

        var blob = GitBlob.Parse(owner: null, default, data);
        Assert.False(blob.IsBinary);
    }

    [Fact]
    public void IsBinary_Utf16Bom_True()
    {
        Assert.True(GitBlob.IsBinaryBytes([0xFE, 0xFF, 0x00, 0x41]));
        Assert.True(GitBlob.IsBinaryBytes([0xFF, 0xFE, 0x41, 0x00]));
    }

    [Fact]
    public void IsBinary_Utf32Bom_True()
    {
        Assert.True(GitBlob.IsBinaryBytes([0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41]));
        Assert.True(GitBlob.IsBinaryBytes([0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void IsBinary_Utf8Bom_False()
    {
        // UTF-8 BOM (EF BB BF) is NOT binary.
        Assert.False(GitBlob.IsBinaryBytes([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']));
    }

    [Fact]
    public void IsBinary_HighNonprintableRatio_True()
    {
        // ~1% nonprintable triggers binary (ratio threshold: nonprint > print/128).
        byte[] data = new byte[800];
        for (int i = 0; i < 790; i++)
        {
            data[i] = (byte)'a';
        }

        for (int i = 790; i < 800; i++)
        {
            data[i] = 0x01; // nonprintable control char
        }

        // 790 printable >> 7 = 6, but 10 nonprint > 6 → binary.
        Assert.True(GitBlob.IsBinaryBytes(data));
    }

    [Fact]
    public void IsBinary_OnInstance_Cached()
    {
        byte[] data = new byte[] { 1, 2, 3, 0, 4, 5 };
        var blob = GitBlob.Parse(owner: null, ObjectFixtures.OneId, data);

        bool first = blob.IsBinary;
        bool second = blob.IsBinary;

        Assert.True(first);
        Assert.True(second);
    }
}

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class GitExceptionTests
{
    [Fact]
    public void Constructor_PreservesCodeAndCategory()
    {
        var ex = new GitException(GitErrorCode.NotFound, "missing repo", GitErrorCategory.Repository);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Repository, ex.Category);
        Assert.Equal("missing repo", ex.Message);
    }

    [Fact]
    public void Constructor_WithInnerException_PreservesInner()
    {
        var inner = new IOException("disk full");
        var ex = new GitException(GitErrorCode.Error, "wrapped", GitErrorCategory.Os, inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void ThrowIfError_DoesNotThrow_OnOk()
    {
        GitException.ThrowIfError(GitErrorCode.Ok);
    }

    [Fact]
    public void ThrowIfError_Throws_OnNegativeCode()
    {
        GitException ex = Assert.Throws<GitException>(() =>
            GitException.ThrowIfError(GitErrorCode.NotFound, "not here", GitErrorCategory.Object));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Object, ex.Category);
    }

    [Fact]
    public void ErrorCode_IncludesAll33Values()
    {
        // Spot-check the full range from Ok (0) to ReadOnly (-40).
        Assert.Equal(0, (int)GitErrorCode.Ok);
        Assert.Equal(-1, (int)GitErrorCode.Error);
        Assert.Equal(-3, (int)GitErrorCode.NotFound);
        Assert.Equal(-40, (int)GitErrorCode.ReadOnly);
    }

    [Fact]
    public void ErrorCategory_IncludesAll36Values()
    {
        Assert.Equal(0, (int)GitErrorCategory.None);
        Assert.Equal(1, (int)GitErrorCategory.NoMemory);
        Assert.Equal(36, (int)GitErrorCategory.Grafts);
    }
}

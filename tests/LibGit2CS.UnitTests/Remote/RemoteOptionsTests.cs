using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

public sealed class RemoteOptionsTests
{
    [Fact]
    public void FetchOptions_Defaults_AreCorrect()
    {
        var opts = new GitFetchOptions();

        Assert.Equal(0, opts.Depth);
        Assert.Equal(GitAutoTagOption.Unspecified, opts.DownloadTags);
        // C zero-inits follow_redirects to 0 (unspecified → config lookup).
        Assert.Equal(GitRemoteRedirect.Unspecified, opts.FollowRedirects);
        Assert.Null(opts.RemoteCallbacks);
        Assert.Null(opts.ProxyConfig);
        Assert.Null(opts.CustomHeaders);
        Assert.Equal(GitFetchPrune.Unspecified, opts.Prune);
        Assert.True(opts.UpdateFetchhead);
    }

    [Fact]
    public void FetchOptions_WithValues_PreservesThem()
    {
        var callbacks = new GitRemoteCallbacks();
        var opts = new GitFetchOptions
        {
            Depth = 10,
            DownloadTags = GitAutoTagOption.All,
            FollowRedirects = GitRemoteRedirect.All,
            RemoteCallbacks = callbacks,
            Prune = GitFetchPrune.Prune,
            UpdateFetchhead = false,
        };

        Assert.Equal(10, opts.Depth);
        Assert.Equal(GitAutoTagOption.All, opts.DownloadTags);
        Assert.Equal(GitRemoteRedirect.All, opts.FollowRedirects);
        Assert.Same(callbacks, opts.RemoteCallbacks);
        Assert.Equal(GitFetchPrune.Prune, opts.Prune);
        Assert.False(opts.UpdateFetchhead);
    }

    [Fact]
    public void AutoTagOption_HasFourValues()
    {
        // C (remote.h:755-768): UNSPECIFIED=0, AUTO=1, NONE=2, ALL=3.
        Assert.Equal(0, (int)GitAutoTagOption.Unspecified);
        Assert.Equal(1, (int)GitAutoTagOption.Auto);
        Assert.Equal(2, (int)GitAutoTagOption.None);
        Assert.Equal(3, (int)GitAutoTagOption.All);
    }

    [Fact]
    public void FetchPrune_HasThreeValues()
    {
        Assert.Equal(0, (int)GitFetchPrune.Unspecified);
        Assert.Equal(1, (int)GitFetchPrune.Prune);
        Assert.Equal(2, (int)GitFetchPrune.NoPrune);
    }

    [Fact]
    public void FetchNegotiation_RecordsFields()
    {
        GitRemoteHead[] refs =
        [
            new GitRemoteHead(false, default, default, "HEAD", null),
            new GitRemoteHead(false, default, default, "refs/heads/main", null),
        ];
        GitOid[] shallowRoots = Array.Empty<GitOid>();

        var nego = new GitFetchNegotiation(refs, shallowRoots, Depth: 5);

        Assert.Equal(2, nego.Refs.Count);
        Assert.Equal(5, nego.Depth);
    }

    [Fact]
    public void RemoteCallbacks_UpdateRefs_CanBeSet()
    {
        var callbacks = new GitRemoteCallbacks
        {
            UpdateRefs = (refName, oldId, newId, spec) => true,
        };

        Assert.NotNull(callbacks.UpdateRefs);
        Assert.True(callbacks.UpdateRefs!("refs/heads/main", default, default, null));
    }

    [Fact]
    public void RemoteCallbacks_UpdateRefs_CanReturnFalse()
    {
        var callbacks = new GitRemoteCallbacks
        {
            UpdateRefs = (refName, oldId, newId, spec) => false,
        };

        Assert.False(callbacks.UpdateRefs!("refs/heads/main", default, default, null));
    }
}

using System.IO.Compression;
using System.Reflection;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Regression tests for the Remote/Clone parity behaviors:
/// (no_proxy wildcard subdomain boundary + case sensitivity),
/// (SCP-style user@[IPv6]:path parsing) (CopyObjectsDir skipping
/// symlinked directories) (fast-forward gate proceeding when the existing
/// ref target object is missing locally).
/// </summary>
public sealed class RemoteMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RemoteMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteMedium_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateBareRepoWithCommit(string name, string message = "initial\n")
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = "refs/heads/main",
        });
        return repo;
    }

    private static bool InvokeUrlMatchesNoProxy(string url, string patternList)
    {
        // UrlMatchesNoProxy is a private static helper (GitRemote.cs:386);
        // pin it via reflection so the regression tests exercise the exact
        // code path the fetch/connect layer uses.
        MethodInfo method = typeof(GitRemote).GetMethod(
            "UrlMatchesNoProxy",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("UrlMatchesNoProxy not found");
        return (bool)method.Invoke(null, [url, patternList])!;
    }

    // ── no_proxy wildcard subdomain boundary + case sensitivity ────

    [Theory]
    [InlineData("http://www.example.com/x", true)]       // proper subdomain
    [InlineData("http://example.com/x", true)]           // host == pattern domain
    [InlineData("http://a.b.example.com/x", true)]       // deeper subdomain
    [InlineData("http://badexample.com/x", false)]       // no '.' boundary
    [InlineData("http://notexample.com/x", false)]       // no '.' boundary
    [InlineData("http://foo.badexample.com/x", false)]   // suffix, no boundary
    public void NoProxy_Wildcard_RequiresSubdomainDotBoundary(string url, bool expected)
    {
        // C's matches_pattern (net.c:1130-1142): the wildcard matches only
        // when host[host_len - domain_len - 1] == '.' for hosts longer than
        // the pattern, so "*.example.com" does not match "badexample.com".
        Assert.Equal(expected, InvokeUrlMatchesNoProxy(url, "*.example.com"));
    }

    [Theory]
    [InlineData("*.Example.COM", "http://www.example.com/x")]  // host lowercase, pattern mixed
    [InlineData("*.example.com", "http://WWW.EXAMPLE.COM/x")]  // host uppercase
    [InlineData("example.com", "http://EXAMPLE.com/x")]        // exact pattern, mixed case
    public void NoProxy_Wildcard_IsCaseSensitive(string pattern, string url)
    {
        // C compares host vs domain with memcmp/git__strlcmp (net.c:1105,
        // 1134) — case-sensitive.
        Assert.False(InvokeUrlMatchesNoProxy(url, pattern));
    }

    [Theory]
    [InlineData("example.com", "http://example.com/x", true)]
    [InlineData("example.com", "http://example.com.evil.com/x", false)]
    [InlineData(".example.com", "http://example.com/x", true)]
    [InlineData(".example.com", "http://badexample.com/x", false)]
    [InlineData("*", "http://anything.example/x", true)]
    [InlineData("example.com:8080", "http://example.com:8080/x", true)]
    [InlineData("example.com:8080", "http://example.com:9090/x", false)]
    public void NoProxy_ExactDotStarAndPortPatterns(string pattern, string url, bool expected)
    {
        Assert.Equal(expected, InvokeUrlMatchesNoProxy(url, pattern));
    }

    // ── SCP-style user@[IPv6]:path ────────────────────────────────

    [Fact]
    public void ScpStyle_UserAtIpv6_ParsesHostAndPath()
    {
        // C's url_parse_scp enters IPV6 from HOST_START (net.c:696-699), so
        // "user@[::1]:path" parses host "::1" and path "path".
        var url = GitSshUrl.Parse("user@[::1]:path");
        Assert.Equal("user", url.User);
        Assert.Equal("::1", url.Host);
        Assert.Null(url.Port);
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void ScpStyle_UserAtIpv6_NoNormalization()
    {
        // Same no-canonicalization invariant as the bracket-first form.
        var url = GitSshUrl.Parse("git@[0:0:0:0:0:0:0:1]:repo.git");
        Assert.Equal("git", url.User);
        Assert.Equal("0:0:0:0:0:0:0:1", url.Host);
        Assert.Equal("repo.git", url.Path);
    }

    [Fact]
    public void ScpStyle_UserAtIpv6_WithPortOverride_Applies()
    {
        var url = GitSshUrl.Parse("user@[::1]:path", scpPortOverride: 2222);
        Assert.Equal("::1", url.Host);
        Assert.Equal(2222, url.Port);
        Assert.Equal("path", url.Path);
    }

    [Theory]
    [InlineData("a[b]:path")]
    [InlineData("a]b:path")]
    [InlineData("user@a[b]:path")]
    [InlineData("user@host]x:path")]
    public void ScpStyle_StrayClosingBracket_Rejects(string input)
    {
        // C's HOST state rejects a ']' with bracket count 0 (net.c:714-717
        // "unexpected ']'").
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse(input));
        Assert.Contains("']'", ex.Message);
    }

    [Fact]
    public void ScpStyle_UserAtIpv6_GarbageAfterClosingBracket_Rejects()
    {
        // IPV6_END requires ':' right after ']' (net.c:742-749).
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("user@[::1]x:path"));
        Assert.Contains("ipv6 address", ex.Message);
    }

    [Fact]
    public void ScpStyle_UserAtIpv6_NoClosingBracket_Rejects()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("user@[::1"));
        Assert.Contains("IPv6", ex.Message);
    }

    [Fact]
    public void ScpStyle_UserAtIpv6_InvalidInner_Rejects()
    {
        // Non-hex content inside the brackets is rejected like the
        // bracket-first branch; C keeps the garbage host
        // "[foo]" which fails resolution at connect time.
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("user@[foo]:path"));
        Assert.Contains("IPv6", ex.Message);
    }

    [Fact]
    public void ScpStyle_MultipleAt_UserSplitsAtFirst()
    {
        // C's USER state breaks at the FIRST '@' (net.c:687-693), so
        // "a@b@host:path" gives user "a", host "b@host".
        var url = GitSshUrl.Parse("a@b@host:path");
        Assert.Equal("a", url.User);
        Assert.Equal("b@host", url.Host);
        Assert.Equal("path", url.Path);
    }

    // ── CopyObjectsDir must not recurse through symlinked dirs ────

    [Fact]
    public async Task CloneLocal_CopyObjectsDir_SkipsSymlinkedDirectories()
    {
        // Directory.GetDirectories lists a symlink-to-directory as a
        // directory; C's lstat-based _cp_r_callback never sees a symlink as
        // S_ISDIR (futils.c:1034) and skips it, so a symlink loop in the
        // source objects dir cannot recurse without bound.
        string sourcePath = Path.Combine(_tempDir, "src-looped");
        await using GitRepository source = await CreateBareRepoWithCommit("src-looped");
        string loopLink = Path.Combine(sourcePath, "objects", "loop");
        File.CreateSymbolicLink(loopLink, ".");
        Assert.True(File.GetAttributes(loopLink).HasFlag(FileAttributes.ReparsePoint));

        string targetPath = Path.Combine(_tempDir, "dst-looped");
        await using GitRepository cloned = await GitClone.RunAsync(
            sourcePath,
            targetPath,
            new GitCloneOptions(),
            new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // The symlinked directory must not be copied or recursed into.
        Assert.False(Directory.Exists(Path.Combine(targetPath, ".git", "objects", "loop")));

        // And the clone actually completed its fetch/checkout.
        Assert.NotNull(await cloned.ReferenceLookupAsync("refs/remotes/origin/main", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── fast-forward gate with a missing 'old' object ─────────────

    [Fact]
    public async Task Fetch_MissingOldTargetObject_ProceedsWithRefUpdate()
    {
        // C's update_one_tip tolerates the graph error
        // (remote.c:1869-1874) and PROCEEDS with the update: the
        // DescendantOfAsync GitException must not abort the entire fetch and
        // leave the ref stale. Uses a NON-forced refspec so the gate is
        // actually reached (the default clone refspec is forced).
        await using GitRepository remoteRepo = await CreateBareRepoWithCommit("l05-remote");
        GitOid commitA = ((GitDirectReference)(await remoteRepo.ReferenceLookupAsync("refs/heads/main", cancellationToken: TestContext.Current.CancellationToken))!).Target;

        string localPath = Path.Combine(_tempDir, "l05-local");
        await using GitRepository local = await GitRepository.InitAsync(localPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        await local.Config.SetStringAsync("remote.origin.url", remoteRepo.Path, cancellationToken: TestContext.Current.CancellationToken);
        await local.Config.SetStringAsync("remote.origin.fetch", "refs/heads/main:refs/remotes/origin/main", cancellationToken: TestContext.Current.CancellationToken);

        // Plant the stale tracking ref: A exists in the local ODB at create
        // time (ref creation validates the target), then A is pruned.
        byte[] aRaw = ReadLooseObject(Path.Combine(remoteRepo.Path, "objects"), commitA);
        await local.ObjectWriteAsync(GitObjectType.Commit, aRaw, TestContext.Current.CancellationToken);
        await local.ReferenceCreateAsync("refs/remotes/origin/main", commitA, force: false, logMessage: "stale", cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(localPath, ".git", "objects", commitA.ToString()[..2], commitA.ToString()[2..]));

        // Remote: rewrite main to an orphan commit B; A is unreachable and
        // never re-sent (its object is absent from the remote too).
        GitOid blobB = await remoteRepo.ObjectWriteAsync(GitObjectType.Blob, "bye\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid treeB;
        using (GitTreeBuilder treeBld = remoteRepo.NewTreeBuilder())
        {
            await treeBld.InsertAsync("README.md", blobB, GitFileMode.Regular, TestContext.Current.CancellationToken);
            treeB = await treeBld.WriteAsync(CancellationToken.None);
        }

        GitSignature sig = TestSig();
        GitOid commitB = await remoteRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeB,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "rewritten\n",
        }, TestContext.Current.CancellationToken);
        await remoteRepo.ReferenceCreateAsync("refs/heads/main", commitB, force: true, logMessage: "rewrite", cancellationToken: TestContext.Current.CancellationToken);

        // The fetch must succeed and move the stale ref forward (C proceeds
        // on graph error).
        GitRemote remote = await local.RemoteLookupAsync("origin", cancellationToken: TestContext.Current.CancellationToken);
        await remote.FetchAsync(refspecs: null, new GitFetchOptions(), "second", TestContext.Current.CancellationToken);

        var updated = (GitDirectReference)(await local.ReferenceLookupAsync("refs/remotes/origin/main", cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(commitB, updated.Target);
    }

    private static byte[] ReadLooseObject(string objectsDir, GitOid oid)
    {
        // Reads + inflates a loose object, stripping the "type <len>\0"
        // header so the body can be re-written content-addressed.
        string path = Path.Combine(objectsDir, oid.ToString()[..2], oid.ToString()[2..]);
        using FileStream fs = File.OpenRead(path);
        using ZLibStream z = new(fs, CompressionMode.Decompress);
        using MemoryStream ms = new();
        z.CopyTo(ms);
        byte[] all = ms.ToArray();
        int nul = Array.IndexOf(all, (byte)0);
        return all[(nul + 1)..];
    }
}

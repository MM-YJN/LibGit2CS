using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Integration tests for the transports/remote parity behaviors in
/// libgit2 1.9.4 exercised
/// end-to-end between real repositories over the local transport:
/// FETCH_HEAD merge entries from the branch upstream, tag auto-following,
/// refspec DWIM, the no-refspec HEAD fetch, and the non-fast-forward skip
/// policy.
/// </summary>
public sealed class TransportsParityIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TransportsInt_" + Guid.NewGuid().ToString("N")[..8]);

    public TransportsParityIntegrationTests()
    {
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

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Converts a filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, GitOid? parent, string fileName, string content, string message, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync(fileName, blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        // C (commit.c:109-117): with update_ref, parent[0] must equal the
        // ref tip — chain onto the current tip when no explicit parent is
        // given, so a second CommitFileAsync call on the same ref is valid.
        GitOid[] parents;
        if (parent is { } p)
        {
            parents = [p];
        }
        else
        {
            GitReference? tip = await repo.ReferenceLookupAsync("refs/heads/main", ct);
            parents = tip is GitDirectReference direct ? [direct.Target] : [];
        }

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// A server with two commits plus a client whose main branch tracks it
    /// through <c>branch.main.remote/merge</c>. A fetch must write the
    /// merge entry into FETCH_HEAD, update the tracking ref, and (AUTO tag
    /// policy) create local tags for advertised tags whose objects are
    /// already in the local ODB.
    /// </summary>
    [Fact]
    public async Task Fetch_UpstreamConfigured_MergeEntryTrackingAndAutoTags()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string serverPath = Path.Combine(_tempDir, "server");
        string clientPath = Path.Combine(_tempDir, "client");

        // Server: two commits + a tag on the tip.
        GitOid tagTarget;
        await using (GitRepository server = await GitRepository.InitAsync(serverPath, isBare: true, new GitContext(), cancellationToken: ct))
        {
            GitOid a = await CommitFileAsync(server, null, "f.txt", "one\n", "one\n", ct);
            tagTarget = await CommitFileAsync(server, a, "f.txt", "two\n", "two\n", ct);
            await server.SetHeadAsync("refs/heads/main", ct);
            Commit? tip = await server.ObjectLookupAsync<Commit>(tagTarget, ct);
            Assert.NotNull(tip);
            await server.TagCreateAsync("v1", tip!, Sig, "v1\n", cancellationToken: ct);
            tip!.Dispose();
        }

        // Client: one commit on main + the upstream configuration.
        GitOid clientCommit;
        await using (GitRepository client = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct))
        {
            clientCommit = await CommitFileAsync(client, null, "f.txt", "one\n", "one\n", ct);
            await client.SetHeadAsync("refs/heads/main", ct);
            await client.Config.SetStringAsync("remote.origin.url", FileUrl(serverPath), ct);
            await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);
            await client.Config.SetStringAsync("branch.main.remote", "origin", ct);
            await client.Config.SetStringAsync("branch.main.merge", "refs/heads/main", ct);
        }

        await using GitRepository client2 = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
        GitRemote remote = await client2.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);

        // 1. FETCH_HEAD: the upstream head is the merge entry (empty merge
        //    flag); the
        //    auto-followed tag line legitimately carries "not-for-merge".
        string fetchHead = await File.ReadAllTextAsync(Path.Combine(client2.Path, "FETCH_HEAD"), ct);
        Assert.Contains("\t\tbranch 'main' of ", fetchHead);
        Assert.Contains("not-for-merge\ttag 'v1' of ", fetchHead);
        string mainLine = fetchHead.Split('\n').First(l => l.Contains("branch 'main'"));
        Assert.DoesNotContain("not-for-merge", mainLine);

        // 2. The tracking ref was updated to the server tip.
        GitReference? tracking = await client2.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(tracking);
        Assert.Equal(tagTarget, Assert.IsType<GitDirectReference>(tracking).Target);

        // 3. Auto-tag: refs/tags/v1 was created (the tag object was
        //    delivered with the pack).
        GitReference? tag = await client2.ReferenceResolveAsync("refs/tags/v1", ct);
        Assert.NotNull(tag);
        Assert.True(await client2.Objects.ExistsAsync(Assert.IsType<GitDirectReference>(tag).Target, ct));
    }

    /// <summary>
    /// A non-forced fetch spec skips a non-fast-forward update silently
    /// (remote.c:1873-1880)
    /// </summary>
    [Fact]
    public async Task Fetch_NonFastForwardUpdate_SkippedSilently()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string serverPath = Path.Combine(_tempDir, "server2");
        string clientPath = Path.Combine(_tempDir, "client2");

        GitOid serverTip;
        await using (GitRepository server = await GitRepository.InitAsync(serverPath, isBare: true, new GitContext(), cancellationToken: ct))
        {
            GitOid a = await CommitFileAsync(server, null, "f.txt", "one\n", "one\n", ct);
            serverTip = await CommitFileAsync(server, a, "f.txt", "two\n", "two\n", ct);
            await server.SetHeadAsync("refs/heads/main", ct);
        }

        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

        _ = await CommitFileAsync(client, null, "f.txt", "one\n", "one\n", ct);
        await client.SetHeadAsync("refs/heads/main", ct);
        await client.Config.SetStringAsync("remote.origin.url", FileUrl(serverPath), ct);
        // Non-forced spec (no '+').
        await client.Config.SetStringAsync("remote.origin.fetch", "refs/heads/*:refs/remotes/origin/*", ct);

        GitRemote remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);

        // Divergence: rewrite the tracking ref to an unrelated commit.
        GitOid local = await CommitFileAsync(client, null, "local.txt", "local\n", "local\n", ct);
        GitReference? tracking = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(tracking);
        await client.ReferenceSetTargetAsync(tracking!, local, logMessage: "rewrite", cancellationToken: ct);

        // Non-forced fetch: the non-FF update is skipped; the tracking
        // ref keeps the local commit (not the server's new tip).
        await remote.FetchAsync(cancellationToken: ct);
        GitReference? after = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(after);
        Assert.Equal(local, Assert.IsType<GitDirectReference>(after).Target);
        Assert.NotEqual(serverTip, Assert.IsType<GitDirectReference>(after).Target);
    }

    /// <summary>
    /// A shorthand fetch refspec ("main:remotes/origin/main") DWIMs against
    /// the advertised heads (refspec.c:377-435).
    /// </summary>
    [Fact]
    public async Task Fetch_ShorthandRefspec_DwimsAgainstAdvertisedHeads()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string serverPath = Path.Combine(_tempDir, "server3");
        string clientPath = Path.Combine(_tempDir, "client3");

        GitOid serverTip;
        await using (GitRepository server = await GitRepository.InitAsync(serverPath, isBare: true, new GitContext(), cancellationToken: ct))
        {
            GitOid a = await CommitFileAsync(server, null, "f.txt", "one\n", "one\n", ct);
            serverTip = await CommitFileAsync(server, a, "f.txt", "two\n", "two\n", ct);
            await server.SetHeadAsync("refs/heads/main", ct);
        }

        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

        _ = await CommitFileAsync(client, null, "f.txt", "one\n", "one\n", ct);
        await client.SetHeadAsync("refs/heads/main", ct);
        await client.Config.SetStringAsync("remote.origin.url", FileUrl(serverPath), ct);

        GitRemote remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(refspecs: ["main:remotes/origin/main"], cancellationToken: ct);

        GitReference? tracking = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(tracking);
        Assert.Equal(serverTip, Assert.IsType<GitDirectReference>(tracking).Target);
    }
}

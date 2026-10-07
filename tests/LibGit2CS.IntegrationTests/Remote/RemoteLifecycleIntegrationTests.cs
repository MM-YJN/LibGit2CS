using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

using LibGit2CSRemote = LibGit2CS.Remote;

namespace LibGit2CS.IntegrationTests.Remote;

/// <summary>
/// Integration tests for the connected-remote lifecycle surface of
/// <see cref="LibGit2CSRemote.GitRemote"/> that the existing
/// <c>LibGit2CS.UnitTests/Remote/RemoteTests.cs</c> does <b>not</b> cover:
/// <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/>,
/// <see cref="LibGit2CSRemote.GitRemote.SetAutoTagAsync"/>,
/// <see cref="LibGit2CSRemote.GitRemote.Stop"/>, and the symbolic-ref
/// target rewrite inside the rename path
/// (<c>RenameRemoteReferencesAsync</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>RemoteTests.cs</c> is already
/// integration-flavored (real repos, real <c>file://</c> fetches) and
/// covers the config-only side of Create/Lookup/List/SetUrl/SetPushUrl/
/// AddFetch/AddPush/Delete/Rename/ApplyInsteadOf/NameIsValid/DefaultBranch.
/// However, <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/> requires a
/// <i>connected</i> transport and was entirely cold; the symbolic-ref
/// target rewrite inside <c>RenameRemoteReferencesAsync</c> (the
/// <c>refs/remotes/&lt;old&gt;/HEAD</c> →
/// <c>refs/remotes/&lt;new&gt;/HEAD</c> fixup) was cold because no existing
/// rename test stages an <c>origin/HEAD</c> symref; and
/// <see cref="LibGit2CSRemote.GitRemote.SetAutoTagAsync"/>'s
/// <c>--no-tags</c> / <c>--tags</c> / clear branches were cold. These
/// tests close exactly those gaps.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/network/remote/rename.c</c>
/// (<c>symref_head</c> — symref target rewrite) and
/// <c>tests/libgit2/network/remote/remotes.c</c>
/// (<c>tagopt</c> — <c>--tags</c>/<c>--no-tags</c> persistence). Prune
/// has no direct libgit2 counterpart test (it is exercised inline by the
/// fetch-with-prune paths); this file tests the standalone
/// <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/> entry point directly.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Uses <c>file://</c> transport over locally-
/// initialized bare source repos. Runs on every build.
/// </para>
/// </remarks>
public sealed class RemoteLifecycleIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a repo.</summary>
    private static string NewRepoPath(string tag)
        => Path.Combine(Path.GetTempPath(), "libgit2cs-rl-" + tag + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Inits a bare source repo with one commit on
    /// <c>refs/heads/master</c> plus a <c>feature</c> branch pointing at
    /// the same commit. The bare repo is suitable as a fetch source over
    /// <c>file://</c>. Returns the on-disk path (repo is closed before
    /// returning so the client process can read it freely).
    /// </summary>
    private static async Task<(string Path, GitOid CommitOid)> InitSourceWithTwoBranchesAsync(
        string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: true, new GitContext(), cancellationToken: ct);
        await using (repo.ConfigureAwait(false))
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder bld = repo.NewTreeBuilder();
            await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await bld.WriteAsync(ct);
            GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, ct);
            // Second branch at the same commit — gives Prune something to remove.
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, force: true, cancellationToken: ct);
            return (path, commitOid);
        }
    }

    /// <summary>
    /// Inits a bare source repo with a single commit on
    /// <c>refs/heads/master</c>. HEAD points at master so the advertised
    /// HEAD symref resolves.
    /// </summary>
    private static async Task<string> InitSourceWithMasterAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: true, new GitContext(), cancellationToken: ct);
        await using (repo.ConfigureAwait(false))
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder bld = repo.NewTreeBuilder();
            await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await bld.WriteAsync(ct);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, ct);
            return path;
        }
    }

    /// <summary>Converts a local filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    // ── 1. Prune ─────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/> after a remote
    /// branch has been deleted server-side removes the corresponding
    /// tracking ref while leaving refs that still exist on the remote
    /// untouched. Exercises the connected-transport prune loop in
    /// <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/>: <c>LsAsync</c>
    /// to read advertised heads, reverse-transform each local tracking ref
    /// via the fetch refspec, and <see cref="GitReferences.DeleteAsync"/>
    /// those whose remote counterpart is gone.
    /// </summary>
    [Fact]
    public async Task Prune_RemovesStaleTrackingRefs_KeepsLiveOnes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("prune-src");
        string clientPath = NewRepoPath("prune-dst");
        await InitSourceWithTwoBranchesAsync(sourcePath, ct);
        try
        {
            // First fetch: tracking refs for both master and feature land.
            await using (GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
                LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", FileUrl(sourcePath), ct);
                await remote.FetchAsync(cancellationToken: ct);
                Assert.NotNull(await client.ReferenceLookupAsync("refs/remotes/origin/master", ct));
                Assert.NotNull(await client.ReferenceLookupAsync("refs/remotes/origin/feature", ct));
            }

            // Server side: delete the feature branch.
            await using (GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct))
            {
                GitReference feature = (await source.ReferenceLookupAsync("refs/heads/feature", ct))!;
                await feature.DeleteAsync(ct);
            }

            // Reopen client, reconnect, prune.
            await using GitRepository client2 = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
            LibGit2CSRemote.GitRemote remote2 = await client2.RemoteLookupAsync("origin", ct);
            await remote2.ConnectAsync(GitDirection.Fetch, new LibGit2CSRemote.GitRemoteConnectOptions(), ct);
            try
            {
                await remote2.PruneAsync(cancellationToken: ct);

                Assert.Null(await client2.ReferenceLookupAsync("refs/remotes/origin/feature", ct));
                Assert.NotNull(await client2.ReferenceLookupAsync("refs/remotes/origin/master", ct));
            }
            finally
            {
                await remote2.DisconnectAsync(ct);
            }
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(clientPath);
        }
    }

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/> on an unconnected
    /// remote throws <see cref="GitErrorCode.Invalid"/> (Net category) —
    /// the <c>_transport is null</c> guard at the top of the method.
    /// </summary>
    [Fact]
    public async Task Prune_NotConnected_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = NewRepoPath("prune-notconn");
        await using (GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct))
        {
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", "file:///nonexistent", ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(async () => await remote.PruneAsync(cancellationToken: ct));
            Assert.Equal(GitErrorCode.Invalid, ex.Code);
        }
        Cleanup(clientPath);
    }

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.PruneAsync"/> does not touch
    /// symbolic refs like <c>refs/remotes/origin/HEAD</c> — the
    /// <c>localRef is GitSymbolicReference</c> continue branch.
    /// </summary>
    [Fact]
    public async Task Prune_PreservesSymrefHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("prune-sym-src");
        string clientPath = NewRepoPath("prune-sym-dst");
        await InitSourceWithMasterAsync(sourcePath, ct);
        try
        {
            await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", FileUrl(sourcePath), ct);
            await remote.FetchAsync(cancellationToken: ct);

            // Stage a HEAD symref — it must survive prune even when there
            // is no remote HEAD advertisement matching the refspec.
            await client.ReferenceCreateSymbolicAsync(
                "refs/remotes/origin/HEAD",
                "refs/remotes/origin/master",
                force: true,
                cancellationToken: ct);

            await remote.ConnectAsync(GitDirection.Fetch, new LibGit2CSRemote.GitRemoteConnectOptions(), ct);
            try
            {
                await remote.PruneAsync(cancellationToken: ct);

                GitReference? head = await client.ReferenceLookupAsync("refs/remotes/origin/HEAD", ct);
                Assert.NotNull(head);
                Assert.True(head is GitSymbolicReference);
                Assert.NotNull(await client.ReferenceLookupAsync("refs/remotes/origin/master", ct));
            }
            finally
            {
                await remote.DisconnectAsync(ct);
            }
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(clientPath);
        }
    }

    // ── 2. Rename with symbolic HEAD target rewrite ─────────────────────

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.RenameAsync"/> rewrites the
    /// target of a <c>refs/remotes/&lt;old&gt;/HEAD</c> symbolic ref to
    /// point under the new prefix. Mirrors libgit2
    /// <c>test_network_remote_rename__symref_head</c>. Exercises the
    /// <c>renamed is GitSymbolicReference sym</c> branch in
    /// <c>RenameRemoteReferencesAsync</c> (GitRemote.cs:1164-1178) that
    /// re-creates the symref with the rewritten target.
    /// </summary>
    /// <remarks>
    /// The existing unit <c>Rename_MovesTrackingRefs</c> covers direct-ref
    /// moves but stages no <c>origin/HEAD</c> symref, so the symref-target
    /// rewrite path was cold.
    /// </remarks>
    [Fact]
    public async Task Rename_OriginHeadSymref_TargetRewrittenToNewPrefix()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("rn-sym-src");
        string clientPath = NewRepoPath("rn-sym-dst");
        await InitSourceWithMasterAsync(sourcePath, ct);
        try
        {
            await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", FileUrl(sourcePath), ct);
            await remote.FetchAsync(cancellationToken: ct);

            // Stage the HEAD symref as a real on-disk symref so the rename
            // path has something to rewrite (rather than relying on the
            // transport's optional symref capability advertisement).
            await client.ReferenceCreateSymbolicAsync(
                "refs/remotes/origin/HEAD",
                "refs/remotes/origin/master",
                force: true,
                cancellationToken: ct);

            IReadOnlyList<string> problems = await client.RemoteRenameAsync("origin", "upstream", ct);
            Assert.Empty(problems);

            GitReference? head = await client.ReferenceLookupAsync("refs/remotes/upstream/HEAD", ct);
            Assert.NotNull(head);
            GitSymbolicReference sym = Assert.IsType<GitSymbolicReference>(head);
            Assert.Equal("refs/remotes/upstream/master", sym.TargetName);

            // Old ref entirely gone.
            Assert.Null(await client.ReferenceLookupAsync("refs/remotes/origin/HEAD", ct));
            Assert.Null(await client.ReferenceLookupAsync("refs/remotes/origin/master", ct));
            Assert.NotNull(await client.ReferenceLookupAsync("refs/remotes/upstream/master", ct));
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(clientPath);
        }
    }

    // ── 3. SetAutoTag (tagopt persistence) ───────────────────────────────

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.SetAutoTagAsync"/> persists the
    /// tag-download policy to <c>remote.&lt;name&gt;.tagopt</c> config:
    /// <see cref="LibGit2CSRemote.GitAutoTagOption.None"/> writes
    /// <c>--no-tags</c>, <see cref="LibGit2CSRemote.GitAutoTagOption.All"/>
    /// writes <c>--tags</c>, and
    /// <see cref="LibGit2CSRemote.GitAutoTagOption.Auto"/>/
    /// <see cref="LibGit2CSRemote.GitAutoTagOption.Unspecified"/> delete
    /// the key. Mirrors libgit2
    /// <c>test_network_remote_remotes__tagopt</c>.
    /// </summary>
    /// <remarks>
    /// Exercises the switch in <c>SetAutoTagAsync</c> (GitRemote.cs:543-555)
    /// — all three branches were previously cold.
    /// </remarks>
    [Theory]
    [InlineData(LibGit2CSRemote.GitAutoTagOption.None, "--no-tags")]
    [InlineData(LibGit2CSRemote.GitAutoTagOption.All, "--tags")]
    public async Task SetAutoTag_PersistsTagoptString(LibGit2CSRemote.GitAutoTagOption value, string expected)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = NewRepoPath("tagopt-" + value);
        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", "https://example.com/repo.git", ct);

            await remote.SetAutoTagAsync(value, ct);

            Assert.Equal(expected, await client.Config.GetStringAsync("remote.origin.tagopt", ct));
            Assert.Equal(value, remote.AutoTag);
        }
        finally
        {
            Cleanup(clientPath);
        }
    }

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.SetAutoTagAsync"/> with
    /// <see cref="LibGit2CSRemote.GitAutoTagOption.Auto"/> removes the
    /// <c>remote.&lt;name&gt;.tagopt</c> config key entirely (the
    /// <c>DeleteAsync</c> branch of the switch).
    /// </summary>
    [Fact]
    public async Task SetAutoTag_Auto_DeletesTagoptKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = NewRepoPath("tagopt-auto");
        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", "https://example.com/repo.git", ct);

            // Establish the key first, then clear it.
            await remote.SetAutoTagAsync(LibGit2CSRemote.GitAutoTagOption.All, ct);
            Assert.Equal("--tags", await client.Config.GetStringAsync("remote.origin.tagopt", ct));

            await remote.SetAutoTagAsync(LibGit2CSRemote.GitAutoTagOption.Auto, ct);
            Assert.Null(await client.Config.GetStringAsync("remote.origin.tagopt", ct));
            Assert.Equal(LibGit2CSRemote.GitAutoTagOption.Auto, remote.AutoTag);
        }
        finally
        {
            Cleanup(clientPath);
        }
    }

    // ── 4. Stop ──────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.Stop"/> cancels any in-progress
    /// operation on a connected remote. With no operation in flight it is
    /// a no-op that must not throw. Exercises the <c>_transport?.Cancel()</c>
    /// dispatch (GitRemote.cs:529) on a live transport.
    /// </summary>
    [Fact]
    public async Task Stop_OnConnectedRemote_DoesNotThrow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("stop-src");
        string clientPath = NewRepoPath("stop-dst");
        await InitSourceWithMasterAsync(sourcePath, ct);
        try
        {
            await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", FileUrl(sourcePath), ct);
            await remote.ConnectAsync(GitDirection.Fetch, new LibGit2CSRemote.GitRemoteConnectOptions(), ct);
            try
            {
                // No operation in flight — Stop is a no-op.
                remote.Stop();
                Assert.True(remote.IsConnected);
            }
            finally
            {
                await remote.DisconnectAsync(ct);
            }
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(clientPath);
        }
    }

    /// <summary>
    /// <see cref="LibGit2CSRemote.GitRemote.Stop"/> on an unconnected
    /// remote is a no-op (<c>_transport?.Cancel()</c> short-circuits on
    /// the null transport) and must not throw.
    /// </summary>
    [Fact]
    public async Task Stop_OnUnconnectedRemote_DoesNotThrow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = NewRepoPath("stop-unconn");
        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            LibGit2CSRemote.GitRemote remote = await client.RemoteCreateAsync("origin", "file:///nonexistent", ct);
            remote.Stop();
            Assert.False(remote.IsConnected);
        }
        finally
        {
            Cleanup(clientPath);
        }
    }
}

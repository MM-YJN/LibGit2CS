using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Integration tests for the <b>default</b> local-clone path
/// (<see cref="GitCloneLocal.Auto"/> and <see cref="GitCloneLocal.NoLinks"/>)
/// which routes through <c>CloneLocalIntoAsync</c> +
/// <c>CopyObjectsDir</c> + <c>CanLink</c> in
/// <see cref="GitClone"/>. These paths were entirely cold in the default
/// integration run because the existing <see cref="LocalTransportTests"/>
/// explicitly sets <see cref="GitCloneLocal.NoLocal"/> to force the
/// smart-transport path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The two clone strategies diverge at
/// <c>GitClone.Run</c>'s <c>ShouldCloneLocal</c> decision:
/// <c>NoLocal</c> → <c>CloneIntoAsync</c> (transport connect + ls +
/// negotiate + pack-download), <c>Auto</c>/<c>Local</c>/<c>NoLinks</c> →
/// <c>CloneLocalIntoAsync</c> (filesystem copy of <c>objects/</c>, then a
/// fetch purely for refs). The latter is what real <c>git clone /local/path</c>
/// uses; it was untested.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/clone/local.c</c>:
/// <c>test_clone_local__should_clone_local</c> (decision matrix),
/// <c>test_clone_local__hardlinks</c> (object-file copy semantics — adapted
/// below to assert the current managed behavior), and the bare/branch-name
/// scenarios from <c>tests/libgit2/clone/clone.c</c>.
/// </para>
/// <para>
/// <b>Parity note.</b> libgit2's <c>can_link</c> compares <c>st_dev</c> and
/// hardlinks objects (<c>st_nlink == 2</c>). The managed port's
/// <c>CanLink</c> compares path strings (always false for distinct repos)
/// and <c>CopyObjectsDir</c> uses <see cref="File.CreateSymbolicLink"/>
/// (symlink) when <c>CanLink</c> is true. As a result, on the current
/// implementation every cloned loose object is a regular file. The tests
/// below assert that behavior rather than C's hardlink semantics.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class LocalCloneDefaultIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a clone source/destination.</summary>
    private static string NewRepoPath(string tag)
        => Path.Combine(Path.GetTempPath(), "libgit2cs-lc-" + tag + "-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a non-bare source repo with one commit on
    /// <c>refs/heads/master</c> containing <c>hello.txt</c>. Disposes
    /// before returning so the destination process can read it freely.
    /// </summary>
    private static async Task<(string Path, GitOid CommitOid)> InitSourceAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        await using (repo.ConfigureAwait(false))
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello-local\n"u8.ToArray(), ct);
            using GitTreeBuilder bld = new(repo);
            await bld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await bld.WriteAsync(ct);
            GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "local-init\n",
                UpdateRef = "refs/heads/master",
            }, ct);
            await repo.SetHeadAsync("refs/heads/master", ct);
            return (path, commitOid);
        }
    }

    /// <summary>Converts a local filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    // ── 1. Default Auto clone, plain local path ─────────────────────────

    /// <summary>
    /// <see cref="GitClone.RunAsync"/> with a plain local path source and
    /// default options (<see cref="GitCloneLocal.Auto"/>) routes through
    /// <c>CloneLocalIntoAsync</c>: copies <c>objects/</c> from source, then
    /// fetches purely for refs and checks out HEAD. Mirrors libgit2
    /// <c>test_clone_local__should_clone_local</c>.
    /// </summary>
    /// <remarks>
    /// Exercises <c>ShouldCloneLocal</c> (non-URL branch returning
    /// <c>Directory.Exists(path)</c>), <c>CloneLocalIntoAsync</c> (full
    /// body), <c>CopyObjectsDir</c> (file copy + recursive subdir walk),
    /// and <c>CanLink</c> (always-false path-comparison branch).
    /// </remarks>
    [Fact]
    public async Task Clone_PlainLocalPath_Auto_CopiesObjectsAndChecksOut()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("src");
        string destPath = NewRepoPath("dst");
        (string _, GitOid commitOid) = await InitSourceAsync(sourcePath, ct);
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(sourcePath, destPath, options: null, new GitContext(), ct);

            // Tracking ref exists and resolves to the source commit.
            GitReference? remoteMaster = await cloned.ReferenceResolveAsync("refs/remotes/origin/master", ct);
            Assert.NotNull(remoteMaster);
            Assert.Equal(commitOid, ((GitDirectReference)remoteMaster!).Target);

            // Object lookup succeeds without a pack refresh — the loose
            // object was copied directly into objects/.
            Commit? tip = await cloned.ObjectLookupAsync<Commit>(commitOid, ct);
            Assert.NotNull(tip);
            Assert.Equal("local-init\n", tip!.Message);

            // Working tree was checked out.
            Assert.True(File.Exists(Path.Combine(destPath, "hello.txt")));
            Assert.Equal("hello-local\n", await File.ReadAllTextAsync(Path.Combine(destPath, "hello.txt"), ct));

            // No pack file should be present — CloneLocalIntoAsync copies
            // loose objects, not a generated pack. (Distinguishes this path
            // from the NoLocal smart-transport path exercised by
            // LocalTransportTests.)
            string packDir = Path.Combine(cloned.Path, "objects", "pack");
            if (Directory.Exists(packDir))
            {
                Assert.Empty(Directory.EnumerateFiles(packDir, "pack-*.pack"));
            }
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(destPath);
        }
    }

    // ── 2. Default Auto clone, file:// URL ───────────────────────────────

    /// <summary>
    /// <see cref="GitClone.RunAsync"/> with a <c>file:///</c> URL source
    /// and default options still routes through
    /// <c>CloneLocalIntoAsync</c> (the <c>file://</c>+<c>Directory.Exists</c>
    /// branch of <c>ShouldCloneLocal</c>). Exercises
    /// <see cref="GitUrlUtils.LocalPathFromUrl"/> in the clone-context
    /// call site and the file-URL parsing matrix.
    /// </summary>
    [Fact]
    public async Task Clone_FileUrl_Auto_RoutesThroughCloneLocalInto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("fsrc");
        string destPath = NewRepoPath("fdst");
        (string _, GitOid commitOid) = await InitSourceAsync(sourcePath, ct);
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(FileUrl(sourcePath), destPath, options: null, new GitContext(), ct);

            GitReference? remoteMaster = await cloned.ReferenceResolveAsync("refs/remotes/origin/master", ct);
            Assert.NotNull(remoteMaster);
            Assert.Equal(commitOid, ((GitDirectReference)remoteMaster!).Target);

            Commit? tip = await cloned.ObjectLookupAsync<Commit>(commitOid, ct);
            Assert.NotNull(tip);
            Assert.True(File.Exists(Path.Combine(destPath, "hello.txt")));
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(destPath);
        }
    }

    // ── 3. Explicit NoLinks forces regular file copy ────────────────────

    /// <summary>
    /// <see cref="GitCloneLocal.NoLinks"/> disables the link attempt
    /// outright: <c>CanLink</c> is short-circuited to <c>false</c>, and
    /// every loose object in <c>objects/??/</c> lands as a regular file
    /// (not a symlink). Mirrors libgit2
    /// <c>test_clone_local__hardlinks</c>'s <c>SHALLOW_CLONE_LOCAL_NO_LINKS</c>
    /// sub-scenario.
    /// </summary>
    [Fact]
    public async Task Clone_NoLinks_ProducesRegularFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("nl-src");
        string destPath = NewRepoPath("nl-dst");
        (string _, GitOid commitOid) = await InitSourceAsync(sourcePath, ct);
        try
        {
            var opts = new GitCloneOptions { CloneLocal = GitCloneLocal.NoLinks };
            await using GitRepository cloned = await GitClone.RunAsync(sourcePath, destPath, opts, new GitContext(), ct);

            // Find the copied loose object file for the commit OID and
            // assert it's a regular file, not a symlink.
            string oidHex = commitOid.ToString();
            string looseFile = Path.Combine(cloned.Path, "objects", oidHex[..2], oidHex[2..]);
            Assert.True(File.Exists(looseFile), $"loose object not found at {looseFile}");

            var info = new FileInfo(looseFile);
            Assert.Null(info.LinkTarget);
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(destPath);
        }
    }

    // ── 4. Bare destination ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitCloneOptions.Bare"/> = true with a local source
    /// produces a bare destination (no working tree, no checkout). The
    /// post-fetch checkout step in <c>CheckoutBranchAsync</c> must
    /// short-circuit on <c>options.Bare</c>.
    /// </summary>
    [Fact]
    public async Task Clone_Bare_Auto_ProducesBareRepoWithoutCheckout()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("bare-src");
        string destPath = NewRepoPath("bare-dst");
        (string _, GitOid commitOid) = await InitSourceAsync(sourcePath, ct);
        try
        {
            var opts = new GitCloneOptions { Bare = true };
            await using GitRepository cloned = await GitClone.RunAsync(sourcePath, destPath, opts, new GitContext(), ct);

            Assert.True(cloned.IsBare);
            Assert.Null(cloned.Workdir);

            // No working-tree file.
            Assert.False(File.Exists(Path.Combine(destPath, "hello.txt")));

            // But the object store and tracking ref are populated.
            GitReference? remoteMaster = await cloned.ReferenceResolveAsync("refs/remotes/origin/master", ct);
            Assert.NotNull(remoteMaster);
            Assert.Equal(commitOid, ((GitDirectReference)remoteMaster!).Target);
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(destPath);
        }
    }

    // ── 5. Custom remote name ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitCloneOptions.RemoteName"/> propagates into the
    /// default-fetch refspec and the tracking-ref prefix when the
    /// local-clone path is used. Mirrors libgit2
    /// <c>test_clone_nonetwork__clone_custom_remote_name</c>.
    /// </summary>
    [Fact]
    public async Task Clone_CustomRemoteName_LandsTrackingRefsUnderThatName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("rn-src");
        string destPath = NewRepoPath("rn-dst");
        (string _, GitOid commitOid) = await InitSourceAsync(sourcePath, ct);
        try
        {
            var opts = new GitCloneOptions { RemoteName = "upstream" };
            await using GitRepository cloned = await GitClone.RunAsync(sourcePath, destPath, opts, new GitContext(), ct);

            // Tracking ref lives under refs/remotes/upstream/*, not origin/*.
            Assert.Null(await cloned.ReferenceLookupAsync("refs/remotes/origin/master", ct));
            GitReference? remoteMaster = await cloned.ReferenceResolveAsync("refs/remotes/upstream/master", ct);
            Assert.NotNull(remoteMaster);
            Assert.Equal(commitOid, ((GitDirectReference)remoteMaster!).Target);

            // The default fetch refspec was written with the custom name.
            Assert.Equal(
                "+refs/heads/*:refs/remotes/upstream/*",
                await cloned.Config.GetStringAsync("remote.upstream.fetch", ct));
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(destPath);
        }
    }

    // ── 6. Destination exists and is non-empty ───────────────────────────

    /// <summary>
    /// <see cref="GitClone.RunAsync"/> refuses to clone into a non-empty
    /// destination directory — the <c>IsDirEmpty</c> guard at the top of
    /// <c>Run</c> throws <see cref="GitErrorCode.Exists"/>. Mirrors libgit2
    /// <c>test_clone_nonetwork__clone_into_nonempty_dir</c>.
    /// </summary>
    [Fact]
    public async Task Clone_IntoNonEmptyDir_ThrowsExists()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = NewRepoPath("exists-src");
        string destPath = NewRepoPath("exists-dst");
        await InitSourceAsync(sourcePath, ct);
        try
        {
            // Pre-create the destination with a stray file.
            Directory.CreateDirectory(destPath);
            await File.WriteAllTextAsync(Path.Combine(destPath, "blocker.txt"), "x\n", ct);

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await GitClone.RunAsync(sourcePath, destPath, options: null, new GitContext(), ct));
            Assert.Equal(GitErrorCode.Exists, ex.Code);
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(destPath);
        }
    }

    // ── 7. GitUrlUtils edge cases reached through the clone path ─────────

    /// <summary>
    /// <see cref="GitUrlUtils.LocalPathFromUrl"/> with a <c>file://</c>
    /// URL whose path is empty throws — there is nothing after the
    /// <c>file:///</c> prefix, so the URL is rejected as an invalid local
    /// file URI. Reached here via the public clone entry point so the test
    /// mirrors how a user would actually hit it.
    /// </summary>
    [Fact]
    public async Task Clone_FileUrl_EmptyPath_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string destPath = NewRepoPath("empty-url-dst");
        try
        {
            await Assert.ThrowsAnyAsync<GitException>(async () =>
                await GitClone.RunAsync("file:///", destPath, options: null, new GitContext(), ct));
        }
        finally
        {
            Cleanup(destPath);
        }
    }
}

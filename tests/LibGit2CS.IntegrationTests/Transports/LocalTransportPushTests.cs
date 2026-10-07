using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Integration tests for <see cref="Transports.GitLocalTransport"/> push path
/// and for progress-callback emission on the local transport fetch path.
/// </summary>
/// <remarks>
/// <b>Why this file exists.</b> <see cref="LocalTransportTests"/> covers only
/// the clone (fetch) direction through <see cref="GitClone.RunAsync"/>. The
/// push half of <see cref="Transports.GitLocalTransport"/> — which writes a
/// pack into a bare target's <c>objects/pack/</c> and updates the target's refs
/// directly (no smart-protocol wire format) — and the
/// <see cref="GitRemoteCallbacks.TransferProgress"/>/
/// <see cref="GitRemoteCallbacks.PackProgress"/> callback emission paths were
/// previously 0% covered by the integration suite. These tests close that gap
/// without Docker: <c>file://</c> dispatches to
/// <see cref="Transports.GitLocalTransport"/> via
/// <see cref="GitTransportRegistry"/>, and a local bare repo stands in for the
/// remote server.
/// <para>
/// Like <see cref="LocalTransportTests"/>, these tests are not Docker-gated
/// and run on every build.
/// </para>
/// </remarks>
public sealed class LocalTransportPushTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// Builds a non-bare source repo with one commit on
    /// <c>refs/heads/main</c> containing <c>hello.txt</c>, sets HEAD, and
    /// returns the path + the commit OID. Mirrors the source-repo setup in
    /// <see cref="LocalTransportTests.CloneFromFile_WithNoLocal_ExercisesLocalTransport"/>.
    /// </summary>
    private static async Task<(string Path, GitOid CommitOid)> BuildSourceRepoAsync(CancellationToken ct)
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-src-" + Guid.NewGuid().ToString("N"));
        await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
        // Write hello.txt to the workdir AND to the ODB so the second commit
        // can re-read the workdir content to preserve the blob in its tree.
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "hello.txt"), "hello\n", ct);
        GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
        using GitTreeBuilder treeBld = new(source);
        await treeBld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        GitOid commitOid = await source.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await source.SetHeadAsync("refs/heads/main", ct);
        return (sourcePath, commitOid);
    }

    /// <summary>
    /// Adds a second commit on top of <paramref name="baseOid"/> in
    /// <paramref name="repo"/>: writes <c>pushed.txt</c>, updates the index,
    /// writes the tree, and creates a commit updating <c>refs/heads/main</c>.
    /// Returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> AddSecondCommitAsync(GitRepository repo, string workdir, GitOid baseOid, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "pushed\n"u8.ToArray(), ct);
        using GitTreeBuilder treeBld = new(repo);
        await treeBld.InsertAsync("hello.txt", await LookupBlobOidAsync(repo, workdir, "hello.txt", ct), GitFileMode.Regular, ct);
        await treeBld.InsertAsync("pushed.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [baseOid],
            Author = Sig,
            Committer = Sig,
            Message = "pushed\n",
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// Reads <paramref name="fileName"/> from the workdir and re-inserts it
    /// into a fresh tree builder by looking up its blob OID via the repo's
    /// ODB. Used to preserve <c>hello.txt</c> across the second commit's tree.
    /// </summary>
    private static async Task<GitOid> LookupBlobOidAsync(GitRepository repo, string workdir, string fileName, CancellationToken ct)
    {
        // Re-read the existing blob's OID by hashing the workdir content —
        // the source repo already has it as a loose/packed object from the
        // first commit, so a WriteAsync of the same bytes returns the same OID.
        byte[] content = await File.ReadAllBytesAsync(Path.Combine(workdir, fileName), ct);
        return await repo.ObjectWriteAsync(GitObjectType.Blob, content, ct);
    }

    /// <summary>Converts a filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    // ─── 1.A: Local fetch with TransferProgress callback ───────────────

    /// <summary>
    /// Clone via <c>file://</c> URL with
    /// <see cref="GitCloneLocal.NoLocal"/> and a
    /// <see cref="GitRemoteCallbacks.TransferProgress"/> callback wired:
    /// <see cref="Transports.GitLocalTransport.DownloadPackAsync"/> reports
    /// <see cref="GitTransferProgress"/> after each object insertion and a
    /// final bridged report from the pack-write phase. The callback must fire
    /// at least once with <see cref="GitTransferProgress.TotalObjects"/> ≥ 1.
    /// </summary>
    [Fact]
    public async Task LocalFetch_WithTransferProgressCallback_FiresReports()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        CapturingProgress<GitTransferProgress> progress = new();

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct))
            {
                GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
                using GitTreeBuilder treeBld = new(source);
                await treeBld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await treeBld.WriteAsync(ct);
                await source.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await source.SetHeadAsync("refs/heads/main", ct);
            }

            GitCloneOptions cloneOpts = new()
            {
                CloneLocal = GitCloneLocal.NoLocal,
                FetchOptions = new GitFetchOptions
                {
                    RemoteCallbacks = new GitRemoteCallbacks { TransferProgress = progress },
                },
            };

            await using (GitRepository cloned = await GitClone.RunAsync(FileUrl(sourcePath), targetPath, cloneOpts, new GitContext(), ct))
            {
                // The clone must succeed — guards against false positives
                // where an early throw skips progress reporting entirely.
                Assert.True(File.Exists(Path.Combine(targetPath, "hello.txt")));
            }

            Assert.NotEmpty(progress.Reports);
            Assert.All(progress.Reports, r => Assert.True(r.TotalObjects >= 1));
            GitTransferProgress last = progress.Reports[^1];
            Assert.Equal(last.ReceivedObjects, last.TotalObjects);
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 1.B: Local push with PackProgress callback ────────────────────

    /// <summary>
    /// Push over <c>file://</c> into a bare target with a
    /// <see cref="GitRemoteCallbacks.PackProgress"/> callback wired:
    /// <see cref="Transports.GitLocalTransport.PushAsync"/> passes the
    /// callback to <see cref="Pack.GitPackWriter.PrepareAsync"/>, which reports
    /// the C packbuilder stages (ADDING_OBJECTS 0, DELTAFICATION 1). The
    /// callback must fire at least once with
    /// <see cref="GitPackProgress.Total"/> ≥ 1.
    /// </summary>
    /// <remarks>
    /// <b>Why not local fetch.</b> The C <c>local_download_pack</c> fires
    /// only <c>transfer_progress</c> during the pack write (it has no
    /// separate <c>packbuilder_progress</c> wiring), and the managed port
    /// faithfully matches: <see cref="Transports.GitLocalTransport.DownloadPackAsync"/>
    /// bridges <see cref="GitRemoteCallbacks.TransferProgress"/> into the
    /// pack-write phase but does NOT forward
    /// <see cref="GitRemoteCallbacks.PackProgress"/>. The local <b>push</b>
    /// path, by contrast, wires <see cref="GitRemoteCallbacks.PackProgress"/>
    /// directly (matching the C# <see cref="GitRemoteCallbacks"/> API split
    /// between pack-builder progress and transfer progress).
    /// </remarks>
    [Fact]
    public async Task LocalPush_WithPackProgressCallback_FiresOnPackWrite()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        CapturingProgress<GitPackProgress> progress = new();

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-pack-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-pack-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string srcPath, GitOid baseOid) = await BuildSourceRepoAsync(ct);
            sourcePath = srcPath;

            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);
            await AddSecondCommitAsync(source, sourcePath, baseOid, ct);

            await using GitRemote remote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct);
            GitPushOptions pushOpts = new()
            {
                RemoteCallbacks = new GitRemoteCallbacks { PackProgress = progress },
            };
            GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], pushOpts, reflogMessage: null, ct);

            Assert.True(result.UnpackOk);
            Assert.NotEmpty(progress.Reports);
            // The writer reports the C packbuilder stages while preparing:
            // DELTAFICATION (stage 1) during the delta search. Stage 0
            // (ADDING_OBJECTS) fires on the coordinator's inserts, which do
            // not carry the callback on this path.
            Assert.Contains(progress.Reports, r => r.Stage == 1);
            Assert.All(progress.Reports, r => Assert.True(r.Total >= 1));
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 2.A: Local push into a bare repo ──────────────────────────────

    /// <summary>
    /// Push over <c>file://</c> into a bare target repo:
    /// <see cref="GitRemote.PushAsync"/> dispatches to
    /// <see cref="Transports.GitLocalTransport.PushAsync"/> via
    /// <see cref="GitTransportRegistry"/>, which builds a pack from the
    /// source's new commit, writes it to the target's
    /// <c>objects/pack/</c>, and updates the target's
    /// <c>refs/heads/main</c>. The push result must report
    /// <see cref="GitPushResult.UnpackOk"/> with one ok status, the target
    /// ref must move to the pushed commit, and a pack file must appear on
    /// disk in the target.
    /// </summary>
    [Fact]
    public async Task LocalPush_IntoBareRepo_UpdatesRemoteRefAndWritesPack()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Source repo with one commit on refs/heads/main.
            (string srcPath, GitOid baseOid) = await BuildSourceRepoAsync(ct);
            sourcePath = srcPath;

            // 2. Bare target repo (the "remote"). Init and immediately dispose
            //    so we can reopen the source alongside (no two GitRepository
            //    instances over the same path needed here — the target is
            //    only re-opened for verification at the end).
            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            // 3. Reopen source, add a second commit, and create a 'target' remote
            //    pointing at the bare repo via file://.
            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);
            GitOid pushedOid = await AddSecondCommitAsync(source, sourcePath, baseOid, ct);

            await using GitRemote remote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct);
            GitPushOptions pushOpts = new();
            GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], pushOpts, reflogMessage: null, ct);

            // 4. Push result invariants.
            Assert.True(result.UnpackOk, $"push UnpackOk=False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);
            Assert.Equal("refs/heads/main", result.Status[0].Ref);

            // 5. The target's refs/heads/main must point at the pushed commit.
            //    Reopen to pick up any ref-db changes written by the push.
            await using GitRepository targetReopened = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct);
            GitReference? targetMain = await targetReopened.ReferenceResolveAsync("refs/heads/main", ct);
            Assert.NotNull(targetMain);
            Assert.IsType<GitDirectReference>(targetMain);
            Assert.Equal(pushedOid, ((GitDirectReference)targetMain).Target);

            // 6. A pack file must be present in the target's objects/pack/ —
            //    proves GitLocalTransport.PushAsync's pack-write path ran.
            string packDir = Path.Combine(targetPath, "objects", "pack");
            Assert.True(Directory.Exists(packDir), $"pack dir missing: {packDir}");
            Assert.NotEmpty(Directory.EnumerateFiles(packDir, "pack-*.pack"));
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 2.B: Local push into a non-bare repo throws BareRepo ──────────

    /// <summary>
    /// Push over <c>file://</c> into a <b>non-bare</b> target repo throws
    /// <see cref="GitException"/> with <see cref="GitException.Code"/> ==
    /// <see cref="GitErrorCode.BareRepo"/>: <see cref="Transports.GitLocalTransport.PushAsync"/>
    /// rejects non-bare targets (matching C's <c>GIT_EBAREREPO</c>) because
    /// pushing into a checked-out branch would diverge HEAD from the workdir.
    /// </summary>
    [Fact]
    public async Task LocalPush_IntoNonBareRepo_ThrowsBareRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-src-nb-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-nb-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string srcPath, GitOid baseOid) = await BuildSourceRepoAsync(ct);
            sourcePath = srcPath;

            // Non-bare target.
            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: false, new GitContext(), cancellationToken: ct))
            {
            }

            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);
            await AddSecondCommitAsync(source, sourcePath, baseOid, ct);

            await using GitRemote remote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct));
            Assert.Equal(GitErrorCode.BareRepo, ex.Code);
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 2.C: Local push deleting a ref ────────────────────────────────

    /// <summary>
    /// Push a ref delete over <c>file://</c>: a push spec with
    /// <c>Loid = zero</c> (<c>:refs/heads/feature</c>) deletes the target's
    /// <c>refs/heads/feature</c>. <see cref="Transports.GitLocalTransport.PushAsync"/>'s
    /// delete branch (lines 473–484) calls
    /// <see cref="GitReferences.DeleteAsync"/> on the target and reports ok
    /// status. After the push, re-opening the target must show the ref gone.
    /// </summary>
    [Fact]
    public async Task LocalPush_DeleteRef_RemovesRemoteRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-del-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-del-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Source repo with one commit on refs/heads/main.
            (string srcPath, GitOid _) = await BuildSourceRepoAsync(ct);
            sourcePath = srcPath;

            // 2. Bare target, seeded with refs/heads/feature pointing at the
            //    same commit (so we have something to delete). We seed by
            //    pushing refs/heads/main:refs/heads/feature first.
            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);
            GitReference? head = await source.ReferenceResolveAsync("refs/heads/main", ct);
            GitOid baseOid = ((GitDirectReference)head!).Target;

            await using (GitRemote seedRemote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct))
            {
                await seedRemote.PushAsync(["refs/heads/main:refs/heads/feature"], new GitPushOptions(), reflogMessage: null, ct);
            }

            // 3. Now delete refs/heads/feature on the target. The remote is
            //    already configured — git_remote_create returns GIT_EEXISTS
            //    for a duplicate name, so look it up instead.
            await using GitRemote remote = await source.RemoteLookupAsync("target", ct);
            GitPushResult result = await remote.PushAsync([":refs/heads/feature"], new GitPushOptions(), reflogMessage: null, ct);

            Assert.True(result.UnpackOk);
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);
            Assert.Equal("refs/heads/feature", result.Status[0].Ref);

            // 4. Reopen target and confirm the ref is gone.
            await using GitRepository targetReopened = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct);
            GitReference? feature = await targetReopened.ReferenceResolveAsync("refs/heads/feature", ct);
            Assert.Null(feature);
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 2.D: Local push when already up-to-date ───────────────────────

    /// <summary>
    /// Push over <c>file://</c> into a bare target, then push the same
    /// ref again. The second push is a no-op (remote already has the commit),
    /// but must succeed without error: <see cref="Remote.PushCoordinator"/>
    /// creates a <see cref="Pack.GitPackWriter"/> (even empty) for non-delete
    /// specs so the transport can fulfil the git protocol requirement of
    /// sending a pack-file for create/update commands (push.c:451-455).
    /// Verifies <see cref="GitPushResult.UnpackOk"/> and ok status.
    /// </summary>
    [Fact]
    public async Task LocalPush_AlreadyUpToDate_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-uptodate-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-uptodate-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string srcPath, GitOid _) = await BuildSourceRepoAsync(ct);
            sourcePath = srcPath;

            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);

            // First push: creates refs/heads/main on the target.
            await using (GitRemote firstRemote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct))
            {
                await firstRemote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);
            }

            // Second push: same ref — already up-to-date. git_remote_create
            // would return GIT_EEXISTS for the duplicate name; look up the
            // configured remote instead.
            await using GitRemote remote = await source.RemoteLookupAsync("target", ct);
            GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);

            Assert.True(result.UnpackOk, $"second push UnpackOk=False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);
            Assert.Equal("refs/heads/main", result.Status[0].Ref);
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 2.E: Local push when the remote tip is absent from the source ODB ──

    /// <summary>
    /// Advances a bare target repo one commit past the source (so the target
    /// has <c>C1→C2</c> on <c>refs/heads/main</c> while the source only has
    /// <c>C1</c>), then attempts a non-force push of the source's
    /// <c>refs/heads/main</c> (<c>C1</c>) over <c>file://</c>. The remote tip
    /// (<c>C2</c>) is by construction absent from the source's object
    /// database.
    /// <para>
    /// <see cref="Remote.PushCoordinator"/> must classify this as
    /// <see cref="GitErrorCode.NonFastForward"/> — mirroring
    /// <c>queue_objects</c>'s <c>git_odb_exists(roid)</c> gate (push.c:343-348)
    /// — rather than letting <see cref="GitRepository.DescendantOfAsync"/>
    /// surface a misleading <see cref="GitErrorCode.NotFound"/> from
    /// <c>git_graph_reachable_from_any</c>'s missing-commit handling.
    /// </para>
    /// <para>
    /// This is the only non-FF gate for <c>file://</c> pushes:
    /// <c>local_push_update_remote_ref</c> (local.c:354-355) force-overwrites
    /// the target ref with no ancestry verification, so the client-side check
    /// is load-bearing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LocalPush_RemoteTipMissingFromSourceOdb_ThrowsNonFastForward()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-nff-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-nff-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Source A with C1 on refs/heads/main.
            (string srcPath, GitOid c1Oid) = await BuildSourceRepoAsync(ct);
            sourcePath = srcPath;

            // 2. Bare target B, then seed it with C1 via a create-ref push
            //    (roid == zero ⇒ non-FF guard skipped).
            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            await using (GitRepository sourceForSeed = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct))
            {
                await using GitRemote seedRemote = await sourceForSeed.RemoteCreateAsync("target", FileUrl(targetPath), ct);
                await seedRemote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);
            }

            // The seed created remote 'target' in the source config — a
            // second RemoteCreateAsync would hit GIT_EEXISTS (parity);
            // look the configured remote up on the reopened repo instead.
            // 3. Advance B's main to C2 (a child of C1) directly in the target
            //    repo, so B's ODB gains C2 but A's ODB never sees it. Opening
            //    the bare target as a GitRepository lets us write blobs/trees/
            //    commits with no workdir.
            await using (GitRepository target = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct))
            {
                GitOid blobOid = await target.ObjectWriteAsync(GitObjectType.Blob, "c2\n"u8.ToArray(), ct);
                using GitTreeBuilder treeBld = new(target);
                await treeBld.InsertAsync("c2.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await treeBld.WriteAsync(ct);
                await target.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [c1Oid],
                    Author = Sig,
                    Committer = Sig,
                    Message = "advance to C2\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // 4. From A (still at C1, C2 not in A's ODB), non-force push
            //    main:main. B's main is C2. Expect NonFastForward, not NotFound.
            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);

            // Sanity: C2 really is absent from A's ODB — otherwise the
            // regression under test doesn't reproduce.
            GitReference? aMain = await source.ReferenceResolveAsync("refs/heads/main", ct);
            Assert.NotNull(aMain);
            Assert.Equal(c1Oid, ((GitDirectReference)aMain!).Target);

            await using GitRemote remote = await source.RemoteLookupAsync("target", ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct));

            Assert.Equal(GitErrorCode.NonFastForward, ex.Code);
            Assert.Equal(GitErrorCategory.Reference, ex.Category);
            Assert.Contains("not present locally", ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 3: Multi-commit carry-forward push (delta/base desync regression) ──

    /// <summary>
    /// Builds a non-bare source repo with <paramref name="commitCount"/>
    /// linear carry-forward commits on <c>refs/heads/main</c>: commit 1
    /// introduces <c>hello.txt</c>; commit i &gt; 1 adds <c>file{i}.txt</c>
    /// while carrying forward all prior files in the tree. Each tree is a
    /// superset of the previous one, giving the pack writer's sliding delta
    /// window multiple same-type candidates — the exact scenario that exposed
    /// the REF_DELTA base/bytes desync. Returns the source path and the
    /// commit OIDs in creation order.
    /// </summary>
    private static async Task<(string Path, List<GitOid> Commits)> BuildMultiCommitSourceAsync(int commitCount, CancellationToken ct)
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-multi-src-" + Guid.NewGuid().ToString("N"));
        await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);

        var files = new Dictionary<string, byte[]>();
        var commits = new List<GitOid>(commitCount);

        for (int i = 1; i <= commitCount; i++)
        {
            string name = i == 1 ? "hello.txt" : $"file{i}.txt";
            byte[] content = i == 1 ? "hello\n"u8.ToArray() : System.Text.Encoding.UTF8.GetBytes($"file{i}\n");
            files[name] = content;

            using GitTreeBuilder treeBld = new(source);
            foreach (KeyValuePair<string, byte[]> entry in files)
            {
                GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, entry.Value, ct);
                await treeBld.InsertAsync(entry.Key, blobOid, GitFileMode.Regular, ct);
            }

            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid commitOid = await source.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = commits.Count > 0 ? [commits[^1]] : [],
                Author = Sig,
                Committer = Sig,
                Message = $"commit {i}\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            commits.Add(commitOid);
        }

        await source.SetHeadAsync("refs/heads/main", ct);
        return (sourcePath, commits);
    }

    /// <summary>
    /// Push over <c>file://</c> into a bare target with 3 linear
    /// carry-forward commits (3C/3T/3B). The sliding delta window sees two
    /// equal-size delta candidates for the smallest tree;
    /// <see cref="Pack.GitPackWriter"/> must keep each REF_DELTA's base OID
    /// and encoded delta bytes consistent. The target's <see cref="Pack.GitPackIndexer"/>
    /// must not throw "base size does not match given data" during
    /// <see cref="Pack.GitPackIndexer.CommitAsync"/> and roll back to an
    /// empty <c>objects/pack/</c>. The push must
    /// finalise the pack, move the target ref, and make all 3 commits
    /// resolvable from the target.
    /// </summary>
    [Fact]
    public async Task LocalPush_ThreeCommitsIntoBareRepo_UpdatesRefAndWritesPack()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-3c-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-3c-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string srcPath, List<GitOid> commits) = await BuildMultiCommitSourceAsync(commitCount: 3, ct);
            sourcePath = srcPath;

            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);
            await using GitRemote remote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct);
            GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);

            Assert.True(result.UnpackOk, $"push UnpackOk=False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);
            Assert.Equal("refs/heads/main", result.Status[0].Ref);

            // The target's refs/heads/main must point at the pushed tip.
            await using GitRepository targetReopened = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct);
            GitReference? targetMain = await targetReopened.ReferenceResolveAsync("refs/heads/main", ct);
            Assert.NotNull(targetMain);
            Assert.IsType<GitDirectReference>(targetMain);
            Assert.Equal(commits[^1], ((GitDirectReference)targetMain).Target);

            // A finalised pack file must be present (not a leftover tmp_pack_*).
            string packDir = Path.Combine(targetPath, "objects", "pack");
            Assert.True(Directory.Exists(packDir), $"pack dir missing: {packDir}");
            Assert.NotEmpty(Directory.GetFiles(packDir, "pack-*.pack"));

            // All 3 commits must resolve from the pushed target.
            foreach (GitOid commit in commits)
            {
                Commit? c = await targetReopened.ObjectLookupAsync<Commit>(commit, ct);
                Assert.NotNull(c);
                c!.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Push over <c>file://</c> into a bare target with 5 linear
    /// carry-forward commits (5C/5T/5B). Widens the delta window and deepens
    /// the candidate pool per target versus the 3-commit case, exercising the
    /// atomic base+bytes selection under more probes. Must finalise the pack
    /// and move the target ref to the pushed tip.
    /// </summary>
    [Fact]
    public async Task LocalPush_FiveCommitsIntoBareRepo_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-5c-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-5c-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string srcPath, List<GitOid> commits) = await BuildMultiCommitSourceAsync(commitCount: 5, ct);
            sourcePath = srcPath;

            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            await using GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct);
            await using GitRemote remote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct);
            GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);

            Assert.True(result.UnpackOk, $"push UnpackOk=False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");

            await using GitRepository targetReopened = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct);
            GitReference? targetMain = await targetReopened.ReferenceResolveAsync("refs/heads/main", ct);
            Assert.NotNull(targetMain);
            Assert.Equal(commits[^1], ((GitDirectReference)targetMain).Target);

            string packDir = Path.Combine(targetPath, "objects", "pack");
            Assert.NotEmpty(Directory.GetFiles(packDir, "pack-*.pack"));

            foreach (GitOid commit in commits)
            {
                Commit? c = await targetReopened.ObjectLookupAsync<Commit>(commit, ct);
                Assert.NotNull(c);
                c!.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Push 3 carry-forward commits into a bare target, then clone that
    /// target back into a fresh working repo via <c>file://</c> with
    /// <see cref="GitCloneLocal.NoLocal"/>. The clone forces the receiver-side
    /// pack-indexer to resolve every REF_DELTA the push produced — a stronger
    /// end-to-end check that the pushed pack is not just present but readable
    /// by an independent client. All carry-forward files must appear in the
    /// clone's working tree with byte-exact content.
    /// </summary>
    [Fact]
    public async Task LocalPush_ThreeCommitsThenClone_RoundTripsAllObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-clone-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-clone-bare-" + Guid.NewGuid().ToString("N"));
        string clonePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-clone-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string srcPath, List<GitOid> _) = await BuildMultiCommitSourceAsync(commitCount: 3, ct);
            sourcePath = srcPath;

            await using (GitRepository _ = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
            }

            // Push 3 commits into the bare target.
            await using (GitRepository source = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct))
            {
                await using GitRemote remote = await source.RemoteCreateAsync("target", FileUrl(targetPath), ct);
                GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);
                Assert.True(result.UnpackOk);
            }

            // Clone from the bare target — exercises the receiver-side pack
            // indexer against the push's output. The bare target was
            // init'd with the default HEAD (refs/heads/master), and the push
            // only created refs/heads/main, so the target's HEAD is unborn.
            // An unspecified-branch clone (like `git clone` with no -b) would
            // therefore leave the working tree empty (matching libgit2/real
            // git: "remote HEAD refers to nonexistent ref, unable to
            // checkout"). Clone the pushed branch explicitly so the pack
            // round-trip is observable in the working tree.
            GitCloneOptions cloneOpts = new() { CloneLocal = GitCloneLocal.NoLocal, BranchName = "main" };
            await using GitRepository cloned = await GitClone.RunAsync(FileUrl(targetPath), clonePath, cloneOpts, new GitContext(), ct);

            // Every carry-forward file must be present in the clone's working tree.
            Assert.True(File.Exists(Path.Combine(clonePath, "hello.txt")));
            Assert.True(File.Exists(Path.Combine(clonePath, "file2.txt")));
            Assert.True(File.Exists(Path.Combine(clonePath, "file3.txt")));
            Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(clonePath, "hello.txt"), ct));
            Assert.Equal("file2\n", await File.ReadAllTextAsync(Path.Combine(clonePath, "file2.txt"), ct));
            Assert.Equal("file3\n", await File.ReadAllTextAsync(Path.Combine(clonePath, "file3.txt"), ct));
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
            try
            {
                Directory.Delete(clonePath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> capture: invokes the callback inline
/// on the reporting thread (no <see cref="SynchronizationContext"/> posting).
/// Mirrors <c>SyncProgress&lt;T&gt;</c> in the unit-test project.
/// </summary>
internal sealed class CapturingProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value) => Reports.Add(value);
}

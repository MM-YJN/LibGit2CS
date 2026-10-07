using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Checkout;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

/// <summary> Regression tests for the remote parity behaviors (libgit2 1.9.4): (lookup name validation), (create_with_opts), (rename
/// multivar failure), (remote_list url/pushurl only), (prune callback + any-match-keeps), (OID-refspec wants), (REPORT_UNCHANGED via
/// GitFetchOptions), (PbParallelism), (push update_refs callback + reflog), (clone object-format propagation), (EEXISTS branch-create),
/// (RemoteName tracking config), (regression: capability + credential-mask), (custom-header validation), (local transport progress routing). </summary>
public sealed class RemoteMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public RemoteMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoAsync(string name, bool bare = true)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, bare, new GitContext());
    }

    private static async Task<GitOid> CreateCommitAsync(GitRepository repo, string message, string content = "data\n", string branch = "master", IReadOnlyList<GitOid>? parents = null)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("f.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents ?? [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = $"refs/heads/{branch}",
        });
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }

    // ── lookup never validates the remote name ────────────────

    [Fact]
    public async Task Lookup_InvalidName_ThrowsInvalidSpec()
    {
        // C (remote.c:477, 88-102): ensure_remote_name_is_valid runs FIRST —
        // lookup of "bad name!" is GIT_EINVALIDSPEC (class GIT_ERROR_CONFIG),
        // not GIT_ENOTFOUND.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("lookup-name");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteLookupAsync("bad name!", ct));

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Contains("not a valid remote name", ex.Message);
    }

    // ── create variants and SKIP_* flags ──────────────────────

    [Fact]
    public async Task CreateWithOptions_CustomFetchSpec_WritesConfigAndSkipsDefault()
    {
        // C (remote.c:347-375, git_remote_create_with_fetchspec):
        // GIT_REMOTE_CREATE_SKIP_DEFAULT_FETCHSPEC + a custom fetchspec.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("create-fetchspec");

        GitRemote remote = await repo.RemoteCreateWithOptionsAsync("ssh://host/repo",
            new GitRemoteCreateOptions
            {
                Name = "custom",
                FetchRefSpecs = ["refs/tags/*:refs/tags/*"],
                Flags = GitRemoteCreateFlags.SkipDefaultFetchSpec,
            }, ct);

        Assert.Equal("custom", remote.Name);
        Assert.Equal("ssh://host/repo", remote.Url);

        // Only the custom spec — no default refs/remotes/custom/* spec.
        GitRefSpec spec = Assert.Single(remote.RefSpecs);
        Assert.Equal("refs/tags/*", spec.Source);
        Assert.Equal("refs/tags/*", spec.Destination);

        Assert.Equal("ssh://host/repo", await repo.Config.GetStringAsync("remote.custom.url", ct));
        Assert.Equal("refs/tags/*:refs/tags/*", await repo.Config.GetStringAsync("remote.custom.fetch", ct));
    }

    [Fact]
    public async Task CreateWithOptions_DefaultFetchSpec_WhenNoFlags()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("create-default");

        GitRemote remote = await repo.RemoteCreateWithOptionsAsync("ssh://host/repo",
            new GitRemoteCreateOptions { Name = "plain" }, ct);

        GitRefSpec spec = Assert.Single(remote.RefSpecs);
        Assert.True(spec.Force);
        Assert.Equal("refs/heads/*", spec.Source);
        Assert.Equal("refs/remotes/plain/*", spec.Destination);
        Assert.Equal("+refs/heads/*:refs/remotes/plain/*", await repo.Config.GetStringAsync("remote.plain.fetch", ct));
    }

    [Fact]
    public async Task CreateWithOptions_SkipInsteadOf_KeepsRawUrl()
    {
        // C (remote.c:253, 273-274): SKIP_INSTEADOF leaves the in-memory URL
        // as the canonical original; otherwise apply_insteadof rewrites it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("create-skip-insteadof");
        await repo.Config.SetStringAsync("url.https://new.example/.insteadof", "https://old.example/", ct);

        GitRemote rewritten = await repo.RemoteCreateWithOptionsAsync("https://old.example/r",
            new GitRemoteCreateOptions { Name = "rewritten" }, ct);
        Assert.Equal("https://new.example/r", rewritten.Url);

        GitRemote raw = await repo.RemoteCreateWithOptionsAsync("https://old.example/r",
            new GitRemoteCreateOptions { Name = "raw", Flags = GitRemoteCreateFlags.SkipInsteadOf }, ct);
        Assert.Equal("https://old.example/r", raw.Url);
    }

    [Fact]
    public async Task CreateAnonymous_NoConfigWritten()
    {
        // C (remote.c:365-368, git_remote_create_anonymous): no name → no
        // config writes, no default fetchspec, download_tags = NONE.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("create-anon");

        GitRemote remote = await repo.RemoteCreateAnonymousAsync("ssh://host/repo", ct);

        Assert.Null(remote.Name);
        Assert.Equal("ssh://host/repo", remote.Url);
        Assert.Empty(remote.RefSpecs);
        Assert.Equal(GitAutoTagOption.None, remote.AutoTag);
        Assert.Null(await repo.Config.GetStringAsync("remote.ssh://host/repo.url", ct));
    }

    // ── rename with ≥2 fetch refspecs ─────────────────────────

    [Fact]
    public async Task Rename_TwoFetchRefspecs_FailsLikeC()
    {
        // C (remote.c:2541-2543): the default fetchspec is rewritten with
        // git_config_set_string, which FAILS on a multivar key ("entry is
        // not unique due to being a multivar"). A C probe confirmed: rename
        // two-fetchspec → -1 (GIT_ERROR / GIT_ERROR_CONFIG).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("rename-twospecs");

        await repo.RemoteCreateAsync("two", "ssh://h/t", ct);
        await repo.Config.SetMultiAsync("remote.two.fetch", new Regex("^$"), "+refs/other/*:refs/remotes/two-other/*", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteRenameAsync("two", "three", ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Contains("multivar", ex.Message);
    }

    // ── remote_list key match too broad ───────────────────────

    [Fact]
    public async Task List_OnlyUrlOrPushUrlRemotes()
    {
        // C (remote.c:2270-2271): git_config_foreach_match
        // "^remote\\..*\\.(push)?url$" — a section with only
        // remote.<n>.fetch is NOT listed (only "deploy" is listed).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("list-url-only");

        GitConfiguration cfg = repo.Config;
        await cfg.SetStringAsync("remote.deploy.pushurl", "ssh://host/repo", ct);
        await cfg.SetStringAsync("remote.fetchonly.fetch", "+refs/heads/*:refs/remotes/fetchonly/*", ct);
        await cfg.SetStringAsync("remote.plain.url", "ssh://h/p", ct);

        IReadOnlyList<string> names = await repo.RemoteListAsync(ct);

        Assert.Equal(["deploy", "plain"], names);
    }

    // ── prune never fires the update_refs callback ────────────

    [Fact]
    public async Task Prune_FiresUpdateRefsCallback()
    {
        // C (remote.c:1734-1740): each pruned ref fires
        // callbacks->update_refs(refname, &id, &zero_id, NULL, ...) after
        // the delete; a false return aborts the prune.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("prune-callback-src");
        GitOid masterOid = await CreateCommitAsync(source, "m");
        await using GitRepository target = await InitRepoAsync("prune-callback-dst");

        GitRemote remote = await target.RemoteCreateAsync("origin", source.Path, ct);
        await remote.FetchAsync(cancellationToken: ct);
        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/master", ct));

        // Remote branch disappears.
        await source.Refs.DeleteAsync("refs/heads/master", ct);

        var callbacks = new GitRemoteCallbacks();
        var pruned = new List<(string RefName, GitOid OldId, GitOid NewId, GitRefSpec? Spec)>();
        callbacks = callbacks with
        {
            UpdateRefs = (refName, oldId, newId, spec) =>
            {
                pruned.Add((refName, oldId, newId, spec));
                return true;
            },
        };

        await remote.FetchAsync(
            options: new GitFetchOptions { Prune = GitFetchPrune.Prune, RemoteCallbacks = callbacks },
            cancellationToken: ct);

        Assert.Null(await target.ReferenceLookupAsync("refs/remotes/origin/master", ct));

        (string refName, GitOid oldId, GitOid newId, GitRefSpec? spec) = Assert.Single(pruned);
        Assert.Equal("refs/remotes/origin/master", refName);
        Assert.Equal(masterOid, oldId);
        Assert.True(newId.IsZero);
        Assert.Null(spec);
    }

    // ── prune any-match vs any-miss ───────────────────────────

    [Fact]
    public async Task Prune_AnySpecReverseTransformKeepsRef()
    {
        // C (remote.c:1669-1699): a candidate is removed from the prune set
        // only when a matching spec reverse-transforms to an ADVERTISED head
        // (then `break`) — ANY matching spec keeps the ref.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("prune-anyspec-src");
        GitOid masterOid = await CreateCommitAsync(source, "m", branch: "master");
        await CreateCommitAsync(source, "f", branch: "feature");
        await using GitRepository target = await InitRepoAsync("prune-anyspec-dst");

        GitConfiguration cfg = target.Config;
        await cfg.SetStringAsync("remote.origin.url", source.Path, ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);
        await cfg.SetMultiAsync("remote.origin.fetch", new Regex("^$"), "+refs/heads/master:refs/remotes/origin/feature", ct);

        GitRemote remote = await target.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);
        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/feature", ct));

        // The feature branch disappears from the remote; master stays.
        await source.Refs.DeleteAsync("refs/heads/feature", ct);

        await remote.FetchAsync(
            options: new GitFetchOptions { Prune = GitFetchPrune.Prune },
            cancellationToken: ct);

        // spec2 (+refs/heads/master:refs/remotes/origin/feature) reverse-
        // transforms refs/remotes/origin/feature to refs/heads/master which
        // IS advertised → C keeps the ref.
        GitReference? kept = await target.ReferenceLookupAsync("refs/remotes/origin/feature", ct);
        Assert.NotNull(kept);
        Assert.Equal(masterOid, Assert.IsType<GitDirectReference>(kept).Target);
    }

    // ── OID-refspec wants ─────────────────────────────────────

    [Fact]
    public async Task Fetch_OidRefspec_FetchesObject()
    {
        // C (fetch.c:142-155): a hex-OID refspec calls maybe_want_oid (the
        // local transport advertises TIP_OID|REACHABLE_OID) and the OID-src
        // update-tips path (remote.c:1961-1977) writes the FETCH_HEAD entry.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("fetch-oid-spec-src");
        GitOid masterOid = await CreateCommitAsync(source, "m");
        await using GitRepository target = await InitRepoAsync("fetch-oid-spec-dst");

        GitRemote remote = await target.RemoteCreateAsync("origin", source.Path, ct);

        string hex = masterOid.ToString();
        await remote.FetchAsync(refspecs: [hex], cancellationToken: ct);

        // The object is now present in the target ODB.
        Assert.True(await target.Objects.ExistsAsync(masterOid, ct));

        // FETCH_HEAD carries the OID entry (ref name = the OID hex).
        string fetchHead = await File.ReadAllTextAsync(Path.Combine(target.Path, "FETCH_HEAD"), ct);
        Assert.Contains(hex, fetchHead, StringComparison.Ordinal);
    }

    // ── REPORT_UNCHANGED via GitFetchOptions ──────────────────

    [Fact]
    public async Task Fetch_ReportUnchanged_FiresUpdateRefsCallback()
    {
        // C (remote.c:1364,1382): update_fetchhead is a bitmask and
        // GIT_REMOTE_UPDATE_REPORT_UNCHANGED is requestable through
        // git_fetch_options — an unchanged tip then still fires the
        // update_refs callback (remote.c:1900-1908).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("fetch-report-src");
        await CreateCommitAsync(source, "m");
        await using GitRepository target = await InitRepoAsync("fetch-report-dst");

        GitRemote remote = await target.RemoteCreateAsync("origin", source.Path, ct);
        await remote.FetchAsync(cancellationToken: ct);
        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/master", ct));

        var reported = new List<(string RefName, GitOid OldId, GitOid NewId)>();
        var callbacks = new GitRemoteCallbacks
        {
            UpdateRefs = (refName, oldId, newId, _) =>
            {
                reported.Add((refName, oldId, newId));
                return true;
            },
        };

        // Second fetch: everything is unchanged, but REPORT_UNCHANGED forces
        // the callback.
        await remote.FetchAsync(
            options: new GitFetchOptions { ReportUnchanged = true, RemoteCallbacks = callbacks },
            cancellationToken: ct);

        (string refName, GitOid oldId, GitOid newId) = Assert.Single(reported);
        Assert.Equal("refs/remotes/origin/master", refName);
        Assert.Equal(oldId, newId);
        Assert.False(newId.IsZero);
    }

    // ── PbParallelism propagated ──────────────────────────────

    [Fact]
    public async Task Push_PbParallelism_ThreadsIntoPackWriter()
    {
        // C (push.c:460): git_packbuilder_set_threads(push->pb,
        // push->pb_parallelism). The managed writer is single-threaded
        // (parity for the default 1); the option must at least reach it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository packRepo = await InitRepoAsync("push-parallelism");
        await CreateCommitAsync(packRepo, "m");

        using GitPackWriter writer = packRepo.NewPackWriter(threads: 4);
        Assert.Equal(4, writer.Threads);

        // 0 = auto-detect (C: prepare_pack maps 0 → online cpus).
        using GitPackWriter autoWriter = packRepo.NewPackWriter(threads: 0);
        Assert.Equal(0, autoWriter.Threads);

        // End-to-end: a push with PbParallelism=4 completes and updates the
        // tracking ref (create on an empty target — no FF gate).
        await using GitRepository source = await InitRepoAsync("push-parallelism-src");
        GitOid local = await CreateCommitAsync(source, "m");
        await using GitRepository target = await InitRepoAsync("push-parallelism-dst");

        GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, ct);

        await remote.PushAsync(
            ["refs/heads/master:refs/heads/master"],
            new GitPushOptions { PbParallelism = 4 },
            cancellationToken: ct);

        GitReference? tracking = await source.ReferenceLookupAsync("refs/remotes/origin/master", ct);
        Assert.NotNull(tracking);
        Assert.Equal(local, Assert.IsType<GitDirectReference>(tracking).Target);
    }

    // ── push update_refs callback + reflog ────────────────

    [Fact]
    public async Task Push_FiresUpdateRefsCallback()
    {
        // C (push.c:227-238, git_push_update_tips): per successfully-updated
        // ref, callbacks->update_refs(remote_ref_name, &roid, &loid,
        // &refspec, ...) fires; the ref is created with the "update by push"
        // reflog message (push.c:211-213).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("push-update-refs-src");
        GitOid c1 = await CreateCommitAsync(source, "c1");
        await using GitRepository target = await InitRepoAsync("push-update-refs-dst");

        // Enable reflogs so the "update by push" message is persisted
        // (bare repos default core.logallrefupdates=false).
        await source.Config.SetBoolAsync("core.logallrefupdates", true, ct);

        GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, ct);

        // First push creates refs/heads/master on the target.
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: ct);
        Assert.NotNull(await target.ReferenceLookupAsync("refs/heads/master", ct));
        GitOid oldTracking = Assert.IsType<GitDirectReference>(await source.ReferenceLookupAsync("refs/remotes/origin/master", ct)).Target;

        // New local commit (child of c1) to push — a fast-forward update.
        GitOid local = await CreateCommitAsync(source, "c2", parents: [c1]);

        var updates = new List<(string RefName, GitOid OldId, GitOid NewId, GitRefSpec? Spec)>();
        var callbacks = new GitRemoteCallbacks
        {
            UpdateRefs = (refName, oldId, newId, spec) =>
            {
                updates.Add((refName, oldId, newId, spec));
                return true;
            },
        };

        await remote.PushAsync(
            ["refs/heads/master:refs/heads/master"],
            new GitPushOptions { RemoteCallbacks = callbacks },
            cancellationToken: ct);

        (string refName, GitOid oldId, GitOid newId, GitRefSpec? spec) = Assert.Single(updates);
        Assert.Equal("refs/remotes/origin/master", refName);
        Assert.Equal(c1, oldId);
        Assert.Equal(local, newId);
        Assert.NotNull(spec);
        Assert.Equal("refs/heads/master", spec!.Destination);
        Assert.Equal(c1, oldTracking);
        Assert.NotEqual(oldTracking, local);

        // the tracking ref's reflog message is "update by push" (push.c:211-213), not "push".
        string logPath = Path.Combine(source.Path, "logs", "refs", "remotes", "origin", "master");
        string[] lines = await File.ReadAllLinesAsync(logPath, ct);
        Assert.Contains(lines, l => l.EndsWith("\tupdate by push", StringComparison.Ordinal));
    }

    // ── clone object-format propagation ───────────────────────

    [Fact]
    public async Task SetObjectFormat_PropagatesToObjects()
    {
        // C (clone.c:444-450, 522-526): git_repository__set_objectformat
        // updates repo->oid_type AND the ODB's oid type.
        await using GitRepository repo = await InitRepoAsync("object-format-a");

        Assert.Equal(GitHashAlgorithmKind.Sha1, repo.ObjectFormat);

        repo.SetObjectFormat(GitHashAlgorithmKind.Sha256);

        Assert.Equal(GitHashAlgorithmKind.Sha256, repo.ObjectFormat);
        Assert.Equal(GitHashAlgorithmKind.Sha256, repo.Objects.Algorithm);
    }

    [Fact]
    public async Task Clone_Local_PropagatesSourceObjectFormat()
    {
        // clone_local_into (clone.c:522-526) sets the target's object format
        // from the SOURCE repository before copying objects + fetching.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("object-format-src");

        // Make the source a genuine SHA-256 repository: config (so a fresh
        // open by the local transport sees it) + in-memory format, then
        // create the commit so the objects are SHA-256-hashed.
        await source.Config.SetIntAsync("core.repositoryformatversion", 1, ct);
        await source.Config.SetStringAsync("extensions.objectformat", "sha256", ct);
        source.SetObjectFormat(GitHashAlgorithmKind.Sha256);
        await CreateCommitAsync(source, "m");

        string targetPath = Path.Combine(_tempDir, "object-format-dst");
        await using GitRepository cloned = await GitClone.RunAsync(
            source.Path, targetPath,
            new GitCloneOptions
            {
                CloneLocal = GitCloneLocal.Local,
                Bare = true,
                CheckoutOptions = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.None },
            },
            new GitContext(), ct);

        Assert.Equal(GitHashAlgorithmKind.Sha256, cloned.ObjectFormat);
        Assert.NotNull(await cloned.ReferenceLookupAsync("refs/remotes/origin/master", ct));
    }

    // ── EEXISTS branch-create skips tracking config + HEAD ────

    [Fact]
    public async Task Clone_BranchAlreadyExists_SkipsTrackingConfig()
    {
        // C (clone.c:93-135): create_tracking_branch returns early on a
        // branch-create error (EEXISTS skips setup_tracking_config) and
        // update_head_to_new_branch swallows GIT_EEXISTS and skips set_head.
        // Reachable with a custom RemoteCreate mapping into refs/heads/*.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("clone-branch-src");
        await CreateCommitAsync(source, "m");

        string targetPath = Path.Combine(_tempDir, "clone-branch-dst");
        await using GitRepository cloned = await GitClone.RunAsync(
            source.Path, targetPath,
            new GitCloneOptions
            {
                RemoteCreate = (repo, name, url, callbackToken) => repo.RemoteCreateWithOptionsAsync(url,
                    new GitRemoteCreateOptions
                    {
                        Name = name,
                        FetchRefSpecs = ["+refs/heads/*:refs/heads/*"],
                        Flags = GitRemoteCreateFlags.SkipDefaultFetchSpec,
                    }, callbackToken),
                CheckoutOptions = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.None },
            },
            new GitContext(), ct);

        // The fetch created refs/heads/master via the wildcard spec.
        Assert.NotNull(await cloned.ReferenceLookupAsync("refs/heads/master", ct));

        // EEXISTS on the branch create → NO tracking config is written.
        Assert.Null(await cloned.Config.GetStringAsync("branch.master.remote", ct));
        Assert.Null(await cloned.Config.GetStringAsync("branch.master.merge", ct));
    }

    // ── RemoteName option vs tracking config ──────────────────

    [Fact]
    public async Task Clone_RemoteNameOption_TrackingConfigUsesRemoteName()
    {
        // C hardcodes "origin" (clone.c:108-109); the C# RemoteName extension
        // must be threaded through setup_tracking_config so the remote and
        // the tracking config agree.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("clone-remote-name-src");
        await CreateCommitAsync(source, "m");

        string targetPath = Path.Combine(_tempDir, "clone-remote-name-dst");
        await using GitRepository cloned = await GitClone.RunAsync(
            source.Path, targetPath,
            new GitCloneOptions
            {
                RemoteName = "upstream",
                CheckoutOptions = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.None },
            },
            new GitContext(), ct);

        Assert.Equal("upstream", await cloned.Config.GetStringAsync("branch.master.remote", ct));
        Assert.Equal("refs/heads/master", await cloned.Config.GetStringAsync("branch.master.merge", ct));
        Assert.NotNull(await cloned.RemoteLookupAsync("upstream", ct));
    }

    // ── regression: Capabilities reports PushOptions ─────────

    [Fact]
    public void Capabilities_PushOptions_MapsFromSmartCaps()
    {
        // C (smart.c:252-253, sys/remote.h:34): t->caps.push_options →
        // GIT_REMOTE_CAPABILITY_PUSH_OPTIONS (1 << 2).
        Assert.Equal(1 << 2, (int)GitRemoteCapability.PushOptions);
    }

    // ── regression: per-scheme credential masks ──────────────

    [Fact]
    public void CredentialTypes_ForScheme_MatchesC()
    {
        // C (httpclient.c:26-30): Negotiate → GIT_CREDENTIAL_DEFAULT,
        // NTLM → USERPASS_PLAINTEXT, Basic → USERPASS_PLAINTEXT.
        Assert.Equal(GitCredentialType.Default, AuthHandlers.CredentialTypesFor(GitAuthSchemeType.Negotiate));
        Assert.Equal(GitCredentialType.UserPassPlaintext, AuthHandlers.CredentialTypesFor(GitAuthSchemeType.Ntlm));
        Assert.Equal(GitCredentialType.UserPassPlaintext, AuthHandlers.CredentialTypesFor(GitAuthSchemeType.Basic));
    }

    // ── custom-header validation ──────────────────────────────

    [Fact]
    public void CustomHeaders_Malformed_Throws()
    {
        // C (remote.c:804-864, validate_custom_headers): CR/LF or a missing
        // "name:" prefix → GIT_ERROR_INVALID "custom HTTP header '%s' is
        // malformed".
        AssertMalformed("no-colon-header");
        AssertMalformed("X-Custom\r\nInjected: x");
        AssertMalformed("X-Custom: v\nInjected: x");
        AssertMalformed(": no-name");
    }

    private static void AssertMalformed(string header)
    {
        GitException ex = Assert.Throws<GitException>(() => GitRemote.ValidateCustomHeaders([header]));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("is malformed", ex.Message);
    }
    [Fact]
    public void CustomHeaders_Forbidden_Throws()
    {
        // C (remote.c:837-840): strncmp of the header name against the
        // forbidden names → GIT_ERROR_INVALID "custom HTTP header '%s' is
        // already set by libgit2".
        string[] forbidden = ["User-Agent: x", "Host: x", "Accept: x", "Content-Type: x", "Transfer-Encoding: x", "Content-Length: x", "User: x"];
        foreach (string header in forbidden)
        {
            GitException ex = Assert.Throws<GitException>(() => GitRemote.ValidateCustomHeaders([header]));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Invalid, ex.Category);
            Assert.Contains("is already set by libgit2", ex.Message);
        }
    }

    [Fact]
    public void CustomHeaders_Valid_Accepted()
    {
        // Non-forbidden names (even with dots/prefixes) pass.
        GitRemote.ValidateCustomHeaders(["X-Custom: value", "User-Agentx: v", "Accept-Language: en"]);
    }

    // ── local transport progress routing ──────────────────────

    [Fact]
    public async Task LocalPush_Progress_RoutedToPushTransfer()
    {
        // C (local.c:371-380, local_push): pack-write progress routes to
        // push_transfer_progress — NOT pack_progress.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("push-progress-src");
        await CreateCommitAsync(source, "m");
        await using GitRepository target = await InitRepoAsync("push-progress-dst");
        await CreateCommitAsync(target, "t");

        var transfer = new RecordingProgress<GitPushTransferProgress>();
        var packProgress = new RecordingProgress<GitPackProgress>();

        GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, ct);
        await remote.PushAsync(
            ["+refs/heads/master:refs/heads/master"],
            new GitPushOptions
            {
                RemoteCallbacks = new GitRemoteCallbacks
                {
                    PushTransferProgress = transfer,
                    PackProgress = packProgress,
                },
            },
            cancellationToken: ct);

        Assert.NotEmpty(transfer.Items);
        GitPushTransferProgress last = transfer.Items[^1];
        Assert.True(last.Total > 0);
    }

    [Fact]
    public async Task LocalFetch_EmitsSidebandCountingProgress()
    {
        // C (local.c:637-667, local_download_pack): "Counting objects" /
        // "Compressing objects" progress via sideband_progress.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("fetch-progress-src");
        await CreateCommitAsync(source, "m");
        await using GitRepository target = await InitRepoAsync("fetch-progress-dst");

        var sideband = new RecordingProgress<string>();

        GitRemote remote = await target.RemoteCreateAsync("origin", source.Path, ct);
        await remote.FetchAsync(
            options: new GitFetchOptions { RemoteCallbacks = new GitRemoteCallbacks { SidebandProgress = sideband } },
            cancellationToken: ct);

        Assert.NotEmpty(sideband.Items);
        Assert.Contains(sideband.Items, m => m.Contains("Counting objects", StringComparison.Ordinal));
    }
}

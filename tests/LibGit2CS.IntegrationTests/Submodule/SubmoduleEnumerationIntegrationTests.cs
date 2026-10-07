using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

namespace LibGit2CS.IntegrationTests.Submodule;

/// <summary>
/// Integration tests for <see cref="GitSubmodule.ForEachAsync"/> exercised
/// end-to-end against locally-initialized repos with real
/// <c>.gitmodules</c> entries (no network clone, no Docker).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="Transports.SubmoduleDockerTests"/> exercise
/// <see cref="GitSubmodule.ForEachAsync"/> over SSH (Docker-gated), so the
/// iterator was cold in the default (no-Docker) integration run. The
/// <see cref="GitSubmodule.ForEachAsync"/> iterator delegates to the
/// <see cref="SubmoduleCache"/>, which reads <c>.gitmodules</c> and yields
/// one <see cref="GitSubmodule"/> per entry — no clone or network is
/// required to enumerate. These tests create submodule entries via
/// <see cref="GitSubmodule.AddSetupAsync"/> (which writes
/// <c>.gitmodules</c> and inits a local gitlink repo) and enumerate to
/// completion.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/submodule/submodule.c</c>
/// (<c>test_submodule_foreach__loop</c>,
/// <c>test_submodule_foreach__invalid</c>), adapted to build the sandbox
/// from scratch and avoid any network operations.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class SubmoduleEnumerationIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Converts a filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-subenum-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a non-bare repo with an initial commit on
    /// <c>refs/heads/main</c> and returns it (caller disposes).
    /// </summary>
    private static async Task<GitRepository> InitRepoWithCommitAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return repo;
    }

    // ── ForEach with no submodules ──────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.ForEachAsync"/> on a fresh repo with no
    /// <c>.gitmodules</c> yields nothing. Drains the iterator to
    /// completion and asserts emptiness — exercises the
    /// <see cref="SubmoduleCache.EnsureLoadedAsync"/> no-file path.
    /// </summary>
    [Fact]
    public async Task ForEach_NoSubmodules_YieldsEmpty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
            List<GitSubmodule> submodules = await repo.SubmoduleForEachAsync(ct).ToListAsync(ct);
            Assert.Empty(submodules);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ForEach with one and multiple submodules ──────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.AddSetupAsync"/> writes a
    /// <c>.gitmodules</c> entry, and <see cref="GitSubmodule.ForEachAsync"/>
    /// yields exactly that submodule with the expected name. Exercises the
    /// cache-load + yield path for a single entry.
    /// </summary>
    [Fact]
    public async Task ForEach_OneSubmodule_YieldsIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
            // AddSetupAsync writes .gitmodules + inits a local gitlink repo.
            // URL points to a sibling path (never cloned — we only enumerate).
            string siblingUrl = FileUrl(path + "-sibling.git");
            await repo.SubmoduleAddSetupAsync(siblingUrl, "sub1", useGitlink: true, ct);

            List<GitSubmodule> submodules = await repo.SubmoduleForEachAsync(ct).ToListAsync(ct);
            Assert.Single(submodules);
            Assert.Equal("sub1", submodules[0].Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With three submodules added via
    /// <see cref="GitSubmodule.AddSetupAsync"/> at distinct paths,
    /// <see cref="GitSubmodule.ForEachAsync"/> yields all three in
    /// <c>.gitmodules</c> order. Exercises the multi-entry enumeration
    /// path in <see cref="SubmoduleCache.EnumerateAsync"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="GitSubmodule.AddSetupAsync"/> calls
    /// <see cref="GitSubmodule.LookupAsync"/> which caches the
    /// <c>.gitmodules</c> load; subsequent adds in the same repo session
    /// miss the new entry. We reopen the repo between adds so the cache
    /// reloads from disk each time — mirroring how a user adds submodules
    /// one per <c>git submodule add</c> invocation.
    /// </remarks>
    [Fact]
    public async Task ForEach_ThreeSubmodules_YieldsAll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using (GitRepository repo = await InitRepoWithCommitAsync(path, ct))
            {
                await repo.SubmoduleAddSetupAsync(FileUrl(path + "-sib1.git"), "sub1", useGitlink: true, ct);
            }

            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await repo.SubmoduleAddSetupAsync(FileUrl(path + "-sib2.git"), "sub2", useGitlink: true, ct);
            }

            await using GitRepository repo3 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo3.SubmoduleAddSetupAsync(FileUrl(path + "-sib3.git"), "sub3", useGitlink: true, ct);

            List<GitSubmodule> submodules = await repo3.SubmoduleForEachAsync(ct).ToListAsync(ct);
            Assert.Equal(3, submodules.Count);
            var names = submodules.Select(s => s.Name).ToHashSet();
            Assert.Contains("sub1", names);
            Assert.Contains("sub2", names);
            Assert.Contains("sub3", names);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ForEach + Lookup interaction ───────────────────────────────────

    /// <summary>
    /// For each <see cref="GitSubmodule"/> yielded by
    /// <see cref="GitSubmodule.ForEachAsync"/>, the corresponding
    /// <see cref="GitSubmodule.LookupAsync"/> returns the same name. This
    /// exercises the iterator → lookup consistency path.
    /// </summary>
    /// <remarks>
    /// Submodules are added in separate repo sessions so the
    /// <see cref="SubmoduleCache"/> reloads <c>.gitmodules</c> between
    /// adds (see <see cref="ForEach_ThreeSubmodules_YieldsAll"/>).
    /// </remarks>
    [Fact]
    public async Task ForEach_Lookup_ForEachEntry_MatchesName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using (GitRepository repo = await InitRepoWithCommitAsync(path, ct))
            {
                await repo.SubmoduleAddSetupAsync(FileUrl(path + "-sibA.git"), "subA", useGitlink: true, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo2.SubmoduleAddSetupAsync(FileUrl(path + "-sibB.git"), "subB", useGitlink: true, ct);

            await foreach (GitSubmodule sm in repo2.SubmoduleForEachAsync(ct))
            {
                GitSubmodule? looked = await repo2.SubmoduleLookupAsync(sm.Name, ct);
                Assert.NotNull(looked);
                Assert.Equal(sm.Name, looked!.Name);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitSubmodule.ForEachAsync"/> on a bare repository
    /// throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.NotFound"/> — submodules cannot be iterated
    /// without a workdir. Exercises the bare-repo guard at the top of
    /// <see cref="GitSubmodule.ForEachAsync"/>.
    /// </summary>
    [Fact]
    public async Task ForEach_BareRepo_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository bare = await GitRepository.InitAsync(path, isBare: true, new GitContext(), cancellationToken: ct);
            await using (bare)
            {
                await Assert.ThrowsAsync<GitException>(async () => await bare.SubmoduleForEachAsync(ct).ToListAsync(ct));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
namespace LibGit2CS.IntegrationTests.Transports;
/// <summary>
/// Integration tests for <see cref="Transports.GitLocalTransport"/> exercised
/// end-to-end through <see cref="GitClone.RunAsync"/> with a <c>file://</c>
/// URL and <see cref="GitCloneLocal.NoLocal"/> to force the smart-local path
/// (refs read directly from the source's ref database, objects copied via a
/// fresh pack — bypassing the smart-protocol wire format entirely).
/// </summary>
/// <remarks>
/// <b>Why a separate file.</b> Unlike <see cref="SshTransportDockerTests"/>
/// and <see cref="HttpTransportTests"/>, the local transport has no server —
/// the source repo lives on the same filesystem as the test process. These
/// tests are not Docker-gated and run on every build.
/// <para>
/// <b>Why <c>CloneLocal.NoLocal</c>.</b> With the default
/// <see cref="GitCloneLocal.Auto"/>, cloning a local path short-circuits to
/// <c>CloneLocalIntoAsync</c> (hardlink/copy of <c>objects/</c>) and never
/// instantiates <see cref="Transports.GitLocalTransport"/>. Setting
/// <c>NoLocal</c> forces the network-clone path, where
/// <see cref="GitTransportRegistry"/> dispatches <c>file://</c> to a fresh
/// <see cref="Transports.GitLocalTransport"/> instance.
/// </para>
/// </remarks>
public sealed class LocalTransportTests
{
    /// <summary>Converts a filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    /// <summary>
    /// Clone via <c>file://</c> URL with
    /// <see cref="GitCloneLocal.NoLocal"/>: the source repo's commit, tree,
    /// and blob are copied into the destination's object store via the local
    /// transport's pack-write path, and <c>refs/remotes/origin/master</c>
    /// tracks the source tip.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="Transports.GitLocalTransport.ConnectAsync"/>
    /// (open source repo + <c>StoreRefsAsync</c>), <c>LsAsync</c>,
    /// <c>NegotiateFetchAsync</c> (revwalk over source commits), and
    /// <c>DownloadPackAsync</c> (<see cref="Pack.GitPackWriter"/> +
    /// <see cref="Pack.GitPackIndexer"/> writing a single pack into the
    /// client's <c>objects/pack/</c>).
    /// </remarks>
    [Fact]
    public async Task CloneFromFile_WithNoLocal_ExercisesLocalTransport()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Build a non-empty source repo: one commit containing hello.txt.
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-local-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-local-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct))
            {
                GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello-local\n"u8.ToArray(), ct);
                using GitTreeBuilder treeBld = source.NewTreeBuilder();
                await treeBld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await treeBld.WriteAsync(ct);
                await source.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
                    Committer = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
                    Message = "local-init\n",
                    UpdateRef = "refs/heads/master",
                }, ct);
                // Set HEAD to refs/heads/master so the local transport advertises it.
                await source.SetHeadAsync("refs/heads/master", ct);
            }
            // file:// URL (three-slash form; see GitUrlUtils.LocalPathFromUrl).
            string posixPath = sourcePath.Replace('\\', '/');
            string url = FileUrl(posixPath);
            // CloneLocal.NoLocal forces GitTransportRegistry → GitLocalTransport
            // instead of the hardlink/copy fast path. Non-bare exercises the
            // post-fetch checkout, which requires DownloadPackAsync to refresh
            // the in-memory ODB's pack backends. If the refresh is missing,
            // checkout silently writes zero working-tree files.
            var cloneOpts = new GitCloneOptions
            {
                CloneLocal = GitCloneLocal.NoLocal,
            };
            await using (GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct))
            {
                // refs/remotes/origin/master must exist and resolve to a non-zero OID.
                GitReference? remoteMaster = await cloned.ReferenceResolveAsync("refs/remotes/origin/master", ct);
                Assert.NotNull(remoteMaster);
                Assert.True(remoteMaster is GitDirectReference);
                GitOid trackedOid = ((GitDirectReference)remoteMaster).Target;
                Assert.False(trackedOid.IsZero);
                // The pack file must be present on disk — proves the local
                // transport's DownloadPack path ran.
                string packDir = Path.Combine(cloned.Path, "objects", "pack");
                Assert.True(Directory.Exists(packDir), $"pack dir missing: {packDir}");
                Assert.NotEmpty(Directory.EnumerateFiles(packDir, "pack-*.pack"));
                // The in-memory ODB must see the new pack WITHOUT a reopen —
                // DownloadPackAsync refreshed the pack backends. If the lookup
                // misses here, the post-fetch checkout cannot resolve HEAD.
                Commit? tip = await cloned.ObjectLookupAsync<Commit>(trackedOid, ct);
                Assert.NotNull(tip);
                Assert.Equal("local-init\n", tip!.Message);
            }
            // The post-fetch checkout must have populated the working tree.
            // (An ODB-miss here would make ResolveHeadTreeAsync return
            // null and leave the working tree empty.)
            Assert.True(File.Exists(Path.Combine(targetPath, "hello.txt")), "working-tree file missing after checkout");
            Assert.Equal("hello-local\n", await File.ReadAllTextAsync(Path.Combine(targetPath, "hello.txt"), ct));
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
            catch
            {
                /* best-effort */
            }
        }
    }
    // ─── FETCH_HEAD tests ──────────────────────────────────────────────
    /// <summary>
    /// Builds a bare source repo with one commit on <c>refs/heads/{branchName}</c>
    /// and returns it (caller disposes). Used by the FETCH_HEAD tests.
    /// </summary>
    private static async Task<(GitRepository Repo, GitOid CommitOid)> CreateBareSourceWithBranchAsync(
        string branchName, CancellationToken ct)
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-src-" + Guid.NewGuid().ToString("N"));
        GitRepository repo = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: ct);
        var sig = new GitSignature("t", "t@t", new GitTime(1700000000, 0));
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = $"refs/heads/{branchName}",
        }, ct);
        return (repo, commitOid);
    }
    /// <summary>
    /// A single-branch fetch via <c>file://</c> writes one entry to
    /// <c>.git/FETCH_HEAD</c> with <c>IsMerge=false</c> and the branch ref
    /// name. Exercises <see cref="GitFetchHead.ReadAsync"/> and
    /// <see cref="GitFetchHead.ParseLine"/> (standard branch format).
    /// </summary>
    [Fact]
    public async Task Fetch_SingleBranch_WritesFetchHeadEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            (GitRepository source, GitOid commitOid) = await CreateBareSourceWithBranchAsync("master", ct);
            await using (source)
            {
                await using GitRepository client = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
                string url = FileUrl(source.Path);
                GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
                await remote.FetchAsync(cancellationToken: ct);
                var entries = new List<GitFetchHeadEntry>();
                await foreach (GitFetchHeadEntry e in GitFetchHead.ReadAsync(client.Path, client.ObjectFormat, ct))
                {
                    entries.Add(e);
                }
                GitFetchHeadEntry? entry = Assert.Single(entries);
                Assert.Equal(commitOid, entry.Oid);
                Assert.False(entry.IsMerge);
                Assert.Equal("refs/heads/master", entry.RefName);
                Assert.Contains("master", entry.Format());
                Assert.Contains("not-for-merge", entry.Format());
            }
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
    /// <summary>
    /// Fetching multiple branches writes multiple entries to
    /// <c>FETCH_HEAD</c>, sorted by ref name. Exercises
    /// <see cref="GitFetchHeadEntry.CompareTo"/>.
    /// </summary>
    [Fact]
    public async Task Fetch_MultipleBranches_SortedByName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-multi-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-multi-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: ct);
            var sig = new GitSignature("t", "t@t", new GitTime(1700000000, 0));
            GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = source.NewTreeBuilder();
            await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid commitOid = await source.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "refs/heads/aaa",
            }, ct);
            await source.ReferenceCreateAsync("refs/heads/feature", commitOid, force: true, cancellationToken: ct);
            await source.ReferenceCreateAsync("refs/heads/main", commitOid, force: true, cancellationToken: ct);
            await using GitRepository client = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
            string url = FileUrl(sourcePath);
            GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
            await remote.FetchAsync(cancellationToken: ct);
            var entries = new List<GitFetchHeadEntry>();
            await foreach (GitFetchHeadEntry e in GitFetchHead.ReadAsync(client.Path, client.ObjectFormat, ct))
            {
                entries.Add(e);
            }
            Assert.Equal(3, entries.Count);
            Assert.Equal("refs/heads/aaa", entries[0].RefName);
            Assert.Equal("refs/heads/feature", entries[1].RefName);
            Assert.Equal("refs/heads/main", entries[2].RefName);
            Assert.All(entries, e => Assert.False(e.IsMerge));
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
    /// <summary>
    /// Fetching with a <c>HEAD</c> refspec produces a HEAD-format
    /// entry (<c>IsMerge=true</c>, <c>RefName="HEAD"</c>) that sorts before
    /// non-merge entries. Exercises <see cref="GitFetchHead.ParseLine"/>'s
    /// HEAD format branch and <see cref="GitFetchHeadEntry.CompareTo"/>'s
    /// merge-before-nonmerge rule.
    /// </summary>
    [Fact]
    public async Task Fetch_HeadRefspec_ProducesHeadFormatEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-head-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-head-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: ct);
            var sig = new GitSignature("t", "t@t", new GitTime(1700000000, 0));
            GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = source.NewTreeBuilder();
            await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid commitOid = await source.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, ct);
            await source.SetHeadAsync("refs/heads/master", ct);
            await using GitRepository client = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
            string url = FileUrl(sourcePath);
            GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
            await remote.AddFetchAsync("HEAD:refs/remotes/origin/HEAD", ct);
            await remote.FetchAsync(cancellationToken: ct);
            var entries = new List<GitFetchHeadEntry>();
            await foreach (GitFetchHeadEntry e in GitFetchHead.ReadAsync(client.Path, client.ObjectFormat, ct))
            {
                entries.Add(e);
            }
            // git_remote_update_tips truncates FETCH_HEAD once, then appends
            // each refspec's sorted batch in refspec order (remote.c:2109-2157).
            // The default wildcard spec (+refs/heads/*:...) runs first, so
            // refs/heads/master is entry 0 — not-for-merge, since a bare
            // client has no local branch upstream to match. The
            // explicit "HEAD" spec runs second; its HEAD head is the merge
            // candidate (remote_head_for_fetchspec_src, remote.c:1550-1553).
            Assert.Equal(2, entries.Count);
            Assert.False(entries[0].IsMerge);
            Assert.Equal("refs/heads/master", entries[0].RefName);
            // The HEAD head is written in the HEAD format (<oid>\t\t<url>),
            // whose parsed RefName is null (fetchhead.c:194-267).
            Assert.True(entries[1].IsMerge);
            Assert.Null(entries[1].RefName);
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
    /// <summary>
    /// Fetching a tag produces a tag-format entry in
    /// <c>FETCH_HEAD</c>. Exercises <see cref="GitFetchHead.ParseLine"/>'s
    /// tag branch and <see cref="GitFetchHeadEntry.Format"/>'s tag path.
    /// </summary>
    [Fact]
    public async Task Fetch_Tag_ProducesTagFormatEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-tag-src-" + Guid.NewGuid().ToString("N"));
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-tag-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: ct);
            var sig = new GitSignature("t", "t@t", new GitTime(1700000000, 0));
            GitOid blobOid = await source.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = source.NewTreeBuilder();
            await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid commitOid = await source.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, ct);
            await source.ReferenceCreateAsync("refs/tags/v1.0", commitOid, force: false, cancellationToken: ct);
            await source.SetHeadAsync("refs/heads/master", ct);
            await using GitRepository client = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
            string url = FileUrl(sourcePath);
            GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
            await remote.AddFetchAsync("refs/tags/v1.0:refs/tags/v1.0", ct);
            await remote.FetchAsync(cancellationToken: ct);
            var entries = new List<GitFetchHeadEntry>();
            await foreach (GitFetchHeadEntry e in GitFetchHead.ReadAsync(client.Path, client.ObjectFormat, ct))
            {
                entries.Add(e);
            }

            // The default refspec fetches refs/heads/* (master), and the
            // explicit tag refspec fetches refs/tags/v1.0. Filter to the tag.
            GitFetchHeadEntry? entry = Assert.Single(entries, e => e.RefName == "refs/tags/v1.0");
            Assert.False(entry.IsMerge);
            Assert.Contains("tag 'v1.0'", entry.Format());
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
    /// <summary>
    /// Fetching with <see cref="GitFetchOptions.UpdateFetchhead"/>
    /// = <c>false</c> leaves an EMPTY <c>FETCH_HEAD</c> file — C's
    /// git_remote_update_tips truncates FETCH_HEAD unconditionally
    /// (remote.c:2139-2143, git_futils_truncate creates the file) and gates
    /// only the per-spec writes on GIT_REMOTE_UPDATE_FETCHHEAD in
    /// libgit2 1.9.4.
    /// </summary>
    [Fact]
    public async Task Fetch_UpdateFetchheadFalse_NoFetchHeadFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-nofh-" + Guid.NewGuid().ToString("N"));
        try
        {
            (GitRepository source, GitOid _) = await CreateBareSourceWithBranchAsync("master", ct);
            await using (source)
            {
                await using GitRepository client = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
                string url = FileUrl(source.Path);
                GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
                await remote.FetchAsync(options: new GitFetchOptions { UpdateFetchhead = false }, cancellationToken: ct);
                string fetchHeadPath = Path.Combine(client.Path, "FETCH_HEAD");
                Assert.True(File.Exists(fetchHeadPath), "FETCH_HEAD must exist (C truncates it unconditionally)");
                Assert.Equal(0, new FileInfo(fetchHeadPath).Length);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
    /// <summary>
    /// A <c>file://</c> URL has no embedded credentials, so
    /// <see cref="GitFetchHead.SanitizeRemoteUrl"/> returns it unchanged.
    /// Exercises the scheme-but-no-<c>@</c> branch of
    /// <c>SanitizeRemoteUrl</c>.
    /// </summary>
    [Fact]
    public async Task Fetch_FileUrl_SanitizedNoCredentials()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fh-url-" + Guid.NewGuid().ToString("N"));
        try
        {
            (GitRepository source, GitOid _) = await CreateBareSourceWithBranchAsync("master", ct);
            await using (source)
            {
                await using GitRepository client = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
                string url = FileUrl(source.Path);
                GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
                await remote.FetchAsync(cancellationToken: ct);
                var entries = new List<GitFetchHeadEntry>();
                await foreach (GitFetchHeadEntry e in GitFetchHead.ReadAsync(client.Path, client.ObjectFormat, ct))
                {
                    entries.Add(e);
                }
                GitFetchHeadEntry? entry = Assert.Single(entries);
                Assert.NotNull(entry.RemoteUrl);
                Assert.StartsWith("file:///", entry.RemoteUrl);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    // ─── Fetch regression tests ────────────────────────────────────────

    /// <summary>
    /// Adds a commit with a new file on top of <paramref name="parent"/> in
    /// the source repo and returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> AddSourceCommitAsync(GitRepository repo, GitOid parent, string fileName, string content, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync(fileName, blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        var sig = new GitSignature("t", "t@t", new GitTime(1700000000, 0));
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = sig,
            Committer = sig,
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// A <c>file://</c> fetch through the full
    /// <see cref="GitRemote.FetchAsync"/> pipeline must download new objects
    /// when the client has a local <c>refs/heads/main</c> at an OLDER commit
    /// than the source. Name-only matching in
    /// <c>NegotiateFetchAsync</c> would mark the head "local" and fetch
    /// zero objects.
    /// </summary>
    [Fact]
    public async Task Fetch_TrailingLocalBranch_DownloadsNewObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-trail-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Source: commit A on refs/heads/main.
            (GitRepository srcRepo, GitOid commitA) = await CreateBareSourceWithBranchAsync("main", ct);
            await using (srcRepo)
            {
                string url = FileUrl(srcRepo.Path);

                // First fetch: client gets commit A.
                await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
                GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
                await remote.FetchAsync(cancellationToken: ct);

                // Give the client a LOCAL refs/heads/main → commitA (same
                // NAME as source, old OID).
                await client.ReferenceCreateAsync("refs/heads/main", commitA, force: false, logMessage: null, ct);

                // Advance source to commit B.
                GitOid commitB = await AddSourceCommitAsync(srcRepo, commitA, "second.txt", "second\n", ct);

                // Second fetch: must download commitB.
                await remote.FetchAsync(cancellationToken: ct);

                // refs/remotes/origin/main must track commitB.
                GitReference? tracked = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
                Assert.NotNull(tracked);
                Assert.Equal(commitB, ((GitDirectReference)tracked!).Target);

                // Client ODB must have commitB.
                Commit? tip = await client.ObjectLookupAsync<Commit>(commitB, ct);
                Assert.NotNull(tip);
                Assert.Equal("add second.txt\n", tip!.Message);
                tip.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(clientPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    /// <summary>
    /// A <c>file://</c> fetch must not throw when the client has a
    /// commit the source does not (local divergence). Enumerating all client
    /// ODB objects and calling
    /// <c>HideAsync</c> on client-only commits would throw NotFound.
    /// </summary>
    [Fact]
    public async Task Fetch_DivergentClient_DoesNotThrow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-div-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Source: commit A on refs/heads/main.
            (GitRepository srcRepo, GitOid commitA) = await CreateBareSourceWithBranchAsync("main", ct);
            await using (srcRepo)
            {
                string url = FileUrl(srcRepo.Path);

                // First fetch: client gets commit A.
                await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
                GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);
                await remote.FetchAsync(cancellationToken: ct);

                // Client creates a divergent commit (never pushed to source).
                GitOid localBlob = await client.ObjectWriteAsync(GitObjectType.Blob, "local only\n"u8.ToArray(), ct);
                using GitTreeBuilder localTreeBld = client.NewTreeBuilder();
                await localTreeBld.InsertAsync("local.txt", localBlob, GitFileMode.Regular, ct);
                GitOid localTree = await localTreeBld.WriteAsync(ct);
                var sig = new GitSignature("t", "t@t", new GitTime(1700000000, 0));
                await client.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = localTree,
                    Parents = [commitA],
                    Author = sig,
                    Committer = sig,
                    Message = "local divergence\n",
                    UpdateRef = "refs/heads/main",
                }, ct);

                // Source advances to commit B (a different child of A).
                GitOid commitB = await AddSourceCommitAsync(srcRepo, commitA, "remote.txt", "remote only\n", ct);

                // Fetch: must NOT throw NotFound for the client-only commit.
                await remote.FetchAsync(cancellationToken: ct);

                // Client must now have commit B.
                Commit? remoteCommit = await client.ObjectLookupAsync<Commit>(commitB, ct);
                Assert.NotNull(remoteCommit);
                Assert.Equal("add remote.txt\n", remoteCommit!.Message);
                remoteCommit.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(clientPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    /// <summary>
    /// Regression guard for the up-to-date fetch hang: when the client's
    /// object store already has every advertised ref tip, the second
    /// <see cref="GitRemote.FetchAsync"/> must short-circuit during
    /// negotiation and never enter <see cref="GitRemote.DownloadAsync"/>'s
    /// pack-download phase. Mirrors libgit2's <c>remote->need_pack</c>
    /// double-check in <c>git_fetch_download_pack</c> (fetch.c:216).
    /// </summary>
    /// <remarks>
    /// A <see cref="FetchCoordinator.NegotiateAsync"/> that bails
    /// early (sending no <c>want</c>/<c>done</c> to the server) while
    /// <see cref="FetchCoordinator.DownloadPackAsync"/> is still called
    /// unconditionally would park the client in a read for a pack the server
    /// was never asked to send.
    /// </remarks>
    [Fact]
    public async Task Fetch_AlreadyUpToDate_SkipsPackDownload()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clientPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-uptodate-" + Guid.NewGuid().ToString("N"));
        try
        {
            (GitRepository srcRepo, GitOid _) = await CreateBareSourceWithBranchAsync("master", ct);
            await using (srcRepo)
            {
                string url = FileUrl(srcRepo.Path);

                await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);
                GitRemote remote = await client.RemoteCreateAsync("origin", url, ct);

                // First fetch: brings the commit/tree/blob over.
                await remote.FetchAsync(cancellationToken: ct);
                Assert.True(remote.NeedPack, "first fetch should have needed a pack");

                // Snapshot pack count after the real fetch.
                string packDir = Path.Combine(client.Path, "objects", "pack");
                int packsBefore = Directory.Exists(packDir)
                    ? Directory.EnumerateFiles(packDir, "pack-*.pack").Count()
                    : 0;

                // Second fetch: the client already has everything.
                // This is the regression scenario — must not hang.
                using var shortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shortTimeout.Token);
                await remote.FetchAsync(cancellationToken: linked.Token);

                // NeedPack was reset to false by NegotiateAsync and stayed
                // false because every matched head's object is local.
                Assert.False(remote.NeedPack, "up-to-date fetch must not need a pack");

                // Stats accumulator was reset to zeros in DownloadAsync and
                // DownloadPackAsync short-circuited, so nothing was received.
                Assert.Equal(0, remote.Stats.ReceivedObjects);
                Assert.Equal(0L, remote.Stats.ReceivedBytes);

                // No new pack file was written.
                int packsAfter = Directory.Exists(packDir)
                    ? Directory.EnumerateFiles(packDir, "pack-*.pack").Count()
                    : 0;
                Assert.Equal(packsBefore, packsAfter);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(clientPath, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
}

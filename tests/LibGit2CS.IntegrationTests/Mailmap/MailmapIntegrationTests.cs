using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Mailmap;

/// <summary>
/// Integration tests for the mailmap engine
/// (<see cref="GitMailmap"/>) exercised end-to-end against
/// locally-initialized non-bare repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>MailmapTests</c> cover
/// <see cref="GitMailmap.FromBuffer"/> + parsing, and the fixture-based
/// config tests cover config resolution on a packaged repo; these tests
/// build the sandbox from scratch and drive the full path:
/// <see cref="GitMailmap.FromRepositoryAsync"/> loading against a real
/// on-disk ODB + workdir + config, and the commit-resolution helpers
/// (<see cref="GitMailmap.ApplyAuthor"/>/
/// <see cref="GitMailmap.ApplyCommitter"/>/
/// <see cref="GitMailmap.ResolveSignature"/>) resolving real commits.
/// The tests write <c>.mailmap</c> to the workdir root, write
/// <c>mailmap.file</c>/<c>mailmap.blob</c> config, then load + resolve.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Ports scenarios from
/// <c>tests/libgit2/mailmap/basic.c</c> (<c>mailmap_from_repository</c>,
/// <c>mailmap_resolve</c>, <c>mailmap_resolve_custom</c>) and
/// <c>tests/libgit2/mailmap/config.c</c> (<c>mailmap_file</c>,
/// <c>mailmap_blob</c>), adapted to build the sandbox from scratch instead
/// of using the libgit2 <c>testrepo</c> fixture.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class MailmapIntegrationTests
{
    private static GitSignature Sig(string name, string email)
        => new(name, email, new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-mailmap-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes a file to the workdir, stages it, writes the tree, and creates
    /// a commit updating <paramref name="refName"/> with the given author
    /// signature. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFileWithAuthorAsync(
        GitRepository repo,
        string workdir,
        string path,
        string content,
        string refName,
        GitSignature author,
        CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);

        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync(path, ct);
        await index.WriteAsync(ct);

        GitOid treeOid = await index.WriteTreeAsync(ct);

        // C (commit.c:109-117): with update_ref, parent[0] must equal the
        // ref tip — chain onto the current tip so repeated calls on the
        // same ref create linear history.
        GitReference? tip = await repo.ReferenceLookupAsync(refName, ct);
        GitOid[] parents = tip is GitDirectReference direct ? [direct.Target] : [];

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = author,
            Committer = author,
            Message = $"add {path}\n",
            UpdateRef = refName,
        }, ct);
    }

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

    // ── FromRepository (workdir .mailmap) ────────────────────────────────

    /// <summary>
    /// <see cref="GitMailmap.FromRepositoryAsync"/> reads a
    /// <c>.mailmap</c> file in the workdir root of a non-bare repo and
    /// resolves a commit author's name and email to their canonical form.
    /// Matches <c>mailmap_from_repository</c> (basic.c).
    /// </summary>
    [Fact]
    public async Task FromRepository_ReadsWorkdirMailmap_AndResolvesAuthor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Commit authored by the "replace" identity.
            GitOid commitOid = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "hello\n", "refs/heads/main",
                Sig("nick1", "bugs@company.xx"), ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Write .mailmap in the workdir root.
            await File.WriteAllTextAsync(
                Path.Combine(path, ".mailmap"),
                "Some Dude <some@dude.xx> nick1 <bugs@company.xx>\n",
                ct);

            using GitMailmap mm = await repo.MailmapFromRepositoryAsync(ct);
            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;

            // Resolve the author through the mailmap.
            (string name, string email) = mm.Resolve(commit.Author.Name, commit.Author.Email);
            Assert.Equal("Some Dude", name);
            Assert.Equal("some@dude.xx", email);

            // ApplyAuthor returns a new signature with the canonical identity.
            GitSignature resolved = mm.ApplyAuthor(commit);
            Assert.Equal("Some Dude", resolved.Name);
            Assert.Equal("some@dude.xx", resolved.Email);
            Assert.Equal(commit.Author.When, resolved.When);

            // ApplyCommitter resolves the committer too.
            GitSignature resolvedCommitter = mm.ApplyCommitter(commit);
            Assert.Equal("Some Dude", resolvedCommitter.Name);
            Assert.Equal("some@dude.xx", resolvedCommitter.Email);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A mailmap with no matching entry leaves the original name and
    /// email unchanged (the <see cref="GitMailmap.Lookup"/> null path).
    /// Matches <c>mailmap_resolve</c> with an unmatched signature (basic.c).
    /// </summary>
    [Fact]
    public async Task FromRepository_UnmatchedSignature_ReturnsOriginal()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid commitOid = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "hello\n", "refs/heads/main",
                Sig("Joe", "joe@example.com"), ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await File.WriteAllTextAsync(
                Path.Combine(path, ".mailmap"),
                "Some Dude <some@dude.xx> nick1 <bugs@company.xx>\n",
                ct);

            using GitMailmap mm = await repo.MailmapFromRepositoryAsync(ct);
            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;

            (string name, string email) = mm.Resolve(commit.Author.Name, commit.Author.Email);
            Assert.Equal("Joe", name);
            Assert.Equal("joe@example.com", email);

            // ResolveSignature returns the original signature unchanged.
            GitSignature resolved = mm.ResolveSignature(commit.Author);
            Assert.Equal(commit.Author.Name, resolved.Name);
            Assert.Equal(commit.Author.Email, resolved.Email);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Multiple entries in the same <c>.mailmap</c> resolve different
    /// commits to their canonical identities, including the nameless
    /// (single-email) form. Matches <c>mailmap_from_repository</c> with the
    /// multi-entry string fixture from <c>MailmapTests.Parsing_String</c>.
    /// </summary>
    [Fact]
    public async Task FromRepository_MultipleEntries_ResolveByReplaceEmailAndName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Two commits by two distinct replace identities.
            GitOid c1 = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "a\n", "refs/heads/main",
                Sig("nick1", "bugs@company.xx"), ct);
            GitOid c2 = await CommitFileWithAuthorAsync(
                repo, path, "b.txt", "b\n", "refs/heads/main",
                Sig("nick2", "bugs@company.xx"), ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Multi-entry mailmap with both named and nameless forms.
            await File.WriteAllTextAsync(
                Path.Combine(path, ".mailmap"),
                "# comment\n" +
                "Some Dude <some@dude.xx>         nick1 <bugs@company.xx>\n" +
                "Other Author <other@author.xx>   nick2 <bugs@company.xx>\n" +
                "Phil Hill <phil@company.xx>\n",
                ct);

            using GitMailmap mm = await repo.MailmapFromRepositoryAsync(ct);
            Commit commit1 = (await repo.ObjectLookupAsync<Commit>(c1, ct))!;
            Commit commit2 = (await repo.ObjectLookupAsync<Commit>(c2, ct))!;

            // nick1@bugs → Some Dude
            (string n1, string e1) = mm.Resolve(commit1.Author.Name, commit1.Author.Email);
            Assert.Equal("Some Dude", n1);
            Assert.Equal("some@dude.xx", e1);

            // nick2@bugs → Other Author (named match wins over nameless)
            (string n2, string e2) = mm.Resolve(commit2.Author.Name, commit2.Author.Email);
            Assert.Equal("Other Author", n2);
            Assert.Equal("other@author.xx", e2);

            // Nameless single-email form: any name with replace email phil@company.xx
            // resolves to "Phil Hill" (the email gets replaced too — the single-email
            // form rewrites both the name and the email's domain).
            (string n3, string e3) = mm.Resolve("unknown", "phil@company.xx");
            Assert.Equal("Phil Hill", n3);
            Assert.Equal("phil@company.xx", e3);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── mailmap.file config ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitMailmap.FromRepositoryAsync"/> reads the
    /// <c>mailmap.file</c> config entry to locate a mailmap file outside the
    /// workdir root. Matches <c>mailmap_file</c> (config.c).
    /// </summary>
    [Fact]
    public async Task FromRepository_MailmapFileConfig_LoadsExternalFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid commitOid = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "hello\n", "refs/heads/main",
                Sig("nick1", "bugs@company.xx"), ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Write the mailmap to a sibling file *not* at the workdir root,
            // and point at it via the mailmap.file config.
            string externalMailmap = Path.Combine(path, "custom-mailmap.txt");
            await File.WriteAllTextAsync(
                externalMailmap,
                "Some Dude <some@dude.xx> nick1 <bugs@company.xx>\n",
                ct);

            await repo.Config.SetStringAsync("mailmap.file", externalMailmap, ct);

            // No .mailmap in the workdir — only the config-pointed file is read.
            using GitMailmap mm = await repo.MailmapFromRepositoryAsync(ct);
            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;

            (string name, string email) = mm.Resolve(commit.Author.Name, commit.Author.Email);
            Assert.Equal("Some Dude", name);
            Assert.Equal("some@dude.xx", email);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── mailmap.blob config ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitMailmap.FromRepositoryAsync"/> reads the
    /// <c>mailmap.blob</c> config entry (a revspec) to load a mailmap from a
    /// blob in the object database. Matches <c>mailmap_blob</c> (config.c).
    /// </summary>
    [Fact]
    public async Task FromRepository_MailmapBlobConfig_LoadsBlobFromOdb()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Commit a.txt authored by the replace identity.
            GitOid commitOid = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "hello\n", "refs/heads/main",
                Sig("nick1", "bugs@company.xx"), ct);

            // Stage and commit the .mailmap file as a second commit, so it
            // exists as a blob in the ODB reachable via HEAD:.mailmap.
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(
                Path.Combine(workdir, ".mailmap"),
                "Some Dude <some@dude.xx> nick1 <bugs@company.xx>\n",
                ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync(".mailmap", ct);
            await index.WriteAsync(ct);
            GitOid treeOid = await index.WriteTreeAsync(ct);

            GitOid mailmapCommit = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [commitOid],
                Author = Sig("t", "t@t"),
                Committer = Sig("t", "t@t"),
                Message = "add mailmap\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // mailmap.blob = HEAD:.mailmap — load the blob from HEAD.
            await repo.Config.SetStringAsync("mailmap.blob", "HEAD:.mailmap", ct);

            using GitMailmap mm = await repo.MailmapFromRepositoryAsync(ct);
            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;

            (string name, string email) = mm.Resolve(commit.Author.Name, commit.Author.Email);
            Assert.Equal("Some Dude", name);
            Assert.Equal("some@dude.xx", email);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── FromBuffer + manual AddEntry ────────────────────────────────────

    /// <summary>
    /// <see cref="GitMailmap.AddEntry"/> adds an entry programmatically
    /// and <see cref="GitMailmap.Resolve"/> returns the canonical form. The
    /// same mailmap can be applied to multiple commits. Mirrors
    /// <c>mailmap_add_entry</c> + <c>mailmap_resolve</c> round-trip from
    /// basic.c.
    /// </summary>
    [Fact]
    public async Task AddEntry_Programmatic_ResolvesCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid commitOid = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "hello\n", "refs/heads/main",
                Sig("nick1", "bugs@company.xx"), ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            using var mm = new GitMailmap();
            mm.AddEntry("Some Dude", "some@dude.xx", "nick1", "bugs@company.xx");

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            (string name, string email) = mm.Resolve(commit.Author.Name, commit.Author.Email);
            Assert.Equal("Some Dude", name);
            Assert.Equal("some@dude.xx", email);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitMailmap.FromBuffer(string)"/> parses a mailmap from
    /// an in-memory string (no repo), and the resulting mailmap resolves the
    /// same way as one loaded from a repository. Mirrors the buffer-entry
    /// path of <c>mailmap_from_buffer</c> from basic.c, but pairs it with a
    /// real commit to ensure cross-API parity.
    /// </summary>
    [Fact]
    public async Task FromBuffer_ProducesEquivalentResolutions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid commitOid = await CommitFileWithAuthorAsync(
                repo, path, "a.txt", "hello\n", "refs/heads/main",
                Sig("nick1", "bugs@company.xx"), ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            const string buffer =
                "Some Dude <some@dude.xx> nick1 <bugs@company.xx>\n" +
                "Other Author <other@author.xx> nick2 <bugs@company.xx>\n";
            using var mmBuffer = GitMailmap.FromBuffer(buffer);

            await File.WriteAllTextAsync(
                Path.Combine(path, ".mailmap"), buffer, ct);
            using GitMailmap mmRepo = await repo.MailmapFromRepositoryAsync(ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            (string nBuf, string eBuf) = mmBuffer.Resolve(commit.Author.Name, commit.Author.Email);
            (string nRepo, string eRepo) = mmRepo.Resolve(commit.Author.Name, commit.Author.Email);

            Assert.Equal(nBuf, nRepo);
            Assert.Equal(eBuf, eRepo);
            Assert.Equal("Some Dude", nBuf);
            Assert.Equal("some@dude.xx", eBuf);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

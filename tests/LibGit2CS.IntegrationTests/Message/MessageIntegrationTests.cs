using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Message;

/// <summary>
/// Integration tests for the commit-message utilities
/// (<see cref="GitMessage"/> and <see cref="GitTrailers"/>)
/// exercised end-to-end against locally-initialized repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>MessageTests</c> +
/// <c>TrailerTests</c> cover the pure parse/prettify paths against in-memory
/// strings, but never round-trip a real commit through the ODB. These tests
/// build a repo, create a commit with a multi-paragraph + trailer-laden
/// message, read the commit back through <see cref="GitObjectDb.LookupAsync{T}"/>,
/// and apply <see cref="GitMessage.Prettify"/> + <see cref="GitTrailers.Parse"/> to
/// the stored message — exercising the full round-trip through the ODB,
/// the commit parser, and the message helpers.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Ports scenarios from
/// <c>tests/libgit2/message/prettify.c</c> and
/// <c>tests/libgit2/trailer/basic.c</c>, adapted to build the sandbox from
/// scratch and read the commit back from the ODB instead of feeding the
/// parser a string literal.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class MessageIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-msg-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates a commit on <paramref name="refName"/> with the given message
    /// and a single file <c>a.txt</c>. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitWithMessageAsync(
        GitRepository repo, string workdir, string message, string refName, CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, "a.txt");
        await File.WriteAllTextAsync(fullPath, "hello\n", ct);

        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync("a.txt", ct);
        await index.WriteAsync(ct);

        GitOid treeOid = await index.WriteTreeAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = message,
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

    // ── GitMessage.Prettify round-trip ──────────────────────────────────

    /// <summary>
    /// A commit message stored in the ODB survives the round-trip and
    /// <see cref="GitMessage.Prettify"/> collapses runs of blank lines,
    /// strips trailing whitespace from each line, and ensures a trailing
    /// newline. Mirrors <c>test_message__prettify</c> (prettify.c) but uses
    /// a real commit message read back from the ODB.
    /// </summary>
    [Fact]
    public async Task Prettify_CollapsesBlanks_AndStripsTrailingWhitespace()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Note: excess blank lines and trailing spaces in the stored message.
            string message =
                "Subject line\n" +
                "\n" +
                "Body paragraph one   \n" +
                "\n" +
                "\n" +
                "Body paragraph two\n" +
                "\n";
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            string stored = commit.Message;

            string prettified = GitMessage.Prettify(stored, '#', stripComments: false);
            Assert.Equal(
                "Subject line\n" +
                "\n" +
                "Body paragraph one\n" +
                "\n" +
                "Body paragraph two\n",
                prettified);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitMessage.Prettify"/> with <c>stripComments=true</c>
    /// removes lines starting with the comment character, but preserves
    /// the rest of the message. Mirrors <c>test_message__prettify</c>
    /// (prettify.c) with the comment-strip flag.
    /// </summary>
    [Fact]
    public async Task Prettify_StripsCommentLines_WhenStripComments()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            string message =
                "Subject line\n" +
                "\n" +
                "# This is a comment\n" +
                "Body line\n" +
                "# Another comment\n";
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;

            // Strip comments.
            string stripped = GitMessage.Prettify(commit.Message, '#', stripComments: true);
            Assert.Equal(
                "Subject line\n" +
                "\n" +
                "Body line\n",
                stripped);

            // Without stripping, comment lines survive.
            string kept = GitMessage.Prettify(commit.Message, '#', stripComments: false);
            Assert.Contains("# This is a comment\n", kept);
            Assert.Contains("# Another comment\n", kept);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitMessage.Prettify"/> preserves the trailers block
    /// intact — trailers look like regular content to the prettifier
    /// (no leading whitespace, no trailing whitespace) and survive
    /// unchanged. This is the precondition for the trailer-parsing test below.
    /// </summary>
    [Fact]
    public async Task Prettify_PreservesTrailerBlock()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            string message =
                "Subject line\n" +
                "\n" +
                "Body\n" +
                "\n" +
                "Signed-off-by: Alice <alice@example.com>\n" +
                "Reviewed-by: Bob <bob@example.com>\n";
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            string prettified = GitMessage.Prettify(commit.Message, '#', stripComments: false);
            Assert.EndsWith(
                "Signed-off-by: Alice <alice@example.com>\n" +
                "Reviewed-by: Bob <bob@example.com>\n",
                prettified);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── GitTrailers.Parse round-trip ────────────────────────────────────

    /// <summary>
    /// <see cref="GitTrailers.Parse"/> extracts the trailer block from a
    /// real commit's message and returns the parsed
    /// <see cref="GitMessageTrailer"/> list. Mirrors
    /// <c>test_trailer__simple</c> (basic.c) but reads the message from a
    /// committed-and-read-back commit instead of an in-memory string.
    /// </summary>
    [Fact]
    public async Task Trailers_Parse_FromCommitMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            string message =
                "Add feature X\n" +
                "\n" +
                "This adds the X feature to the Y subsystem.\n" +
                "\n" +
                "Signed-off-by: Alice <alice@example.com>\n" +
                "Signed-off-by: Bob <bob@example.com>\n";
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            IReadOnlyList<GitMessageTrailer> trailers = GitTrailers.Parse(commit.Message);

            Assert.Equal(2, trailers.Count);
            Assert.Equal("Signed-off-by", trailers[0].Key);
            Assert.Equal("Alice <alice@example.com>", trailers[0].Value);
            Assert.Equal("Signed-off-by", trailers[1].Key);
            Assert.Equal("Bob <bob@example.com>", trailers[1].Value);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitTrailers.Parse"/> on a commit with no trailer block
    /// (single paragraph) returns an empty list. Mirrors
    /// <c>test_trailer__no_trailers</c> (basic.c).
    /// </summary>
    [Fact]
    public async Task Trailers_Parse_NoTrailers_ReturnsEmpty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            string message = "Just a one-line subject\n";
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            IReadOnlyList<GitMessageTrailer> trailers = GitTrailers.Parse(commit.Message);
            Assert.Empty(trailers);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitTrailers.Parse"/> extracts custom (non-Signed-off-by)
    /// trailer keys, including keys without the conventional space after
    /// the separator. Mirrors <c>test_trailer__no_whitespace</c> +
    /// <c>test_trailer__custom</c> (basic.c).
    /// </summary>
    [Fact]
    public async Task Trailers_Parse_CustomKeys_NoWhitespace()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            string message =
                "Subject\n" +
                "\n" +
                "Body\n" +
                "\n" +
                "Bug:42\n" +
                "Fixes:abc123\n";
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
            IReadOnlyList<GitMessageTrailer> trailers = GitTrailers.Parse(commit.Message);
            Assert.Equal(2, trailers.Count);
            Assert.Equal("Bug", trailers[0].Key);
            Assert.Equal("42", trailers[0].Value);
            Assert.Equal("Fixes", trailers[1].Key);
            Assert.Equal("abc123", trailers[1].Value);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Prettify → Parse composition ────────────────────────────────────

    /// <summary>
    /// <see cref="GitMessage.Prettify"/> + <see cref="GitTrailers.Parse"/>
    /// compose: prettifying a real commit message before parsing its
    /// trailers produces the same trailer set as parsing the raw message —
    /// the prettifier does not damage trailer syntax. This mirrors the
    /// <c>git commit -F</c> + <c>git interpret-trailers</c> workflow.
    /// </summary>
    [Fact]
    public async Task Prettify_ThenParse_ProducesSameTrailers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            string message =
                "Subject\n" +
                "\n" +
                "Body  \n" +                       // trailing whitespace
                "\n" +
                "\n" +                             // extra blank
                "Signed-off-by: Alice <alice@example.com>\n" +
                "Reviewed-by: Bob <bob@example.com>\n" +
                "# please review\n";               // trailing comment
            GitOid commitOid = await CommitWithMessageAsync(repo, path, message, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;

            IReadOnlyList<GitMessageTrailer> raw = GitTrailers.Parse(commit.Message);
            string prettified = GitMessage.Prettify(commit.Message, '#', stripComments: true);
            IReadOnlyList<GitMessageTrailer> after = GitTrailers.Parse(prettified);

            Assert.Equal(raw.Count, after.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                Assert.Equal(raw[i].Key, after[i].Key);
                Assert.Equal(raw[i].Value, after[i].Value);
            }

            Assert.Equal("Signed-off-by", after[0].Key);
            Assert.Equal("Alice <alice@example.com>", after[0].Value);
            Assert.Equal("Reviewed-by", after[1].Key);
            Assert.Equal("Bob <bob@example.com>", after[1].Value);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

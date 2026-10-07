using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Parity tests for the objects subsystem. </summary>
public sealed class ObjectsLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public ObjectsLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectsLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> InitRepoAsync()
        => await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());

    // ── tree filename longer than UINT16_MAX is rejected ─────────────

    [Fact]
    public async Task TreeParse_FilenameOverUint16Max_Throws()
    {
        // C (tree.c:423): filename_len > UINT16_MAX fails with
        // "can't parse filename".
        await using GitRepository repo = await InitRepoAsync();

        using var treeMs = new MemoryStream();
        treeMs.Write("100644 "u8);
        byte[] name = new byte[65536];
        Array.Fill(name, (byte)'a');
        treeMs.Write(name);
        treeMs.WriteByte(0);
        treeMs.Write(new byte[20]); // oid bytes

        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Tree, treeMs.ToArray(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.ObjectLookupAsync(oid, TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("can't parse filename", ex.Message);
    }

    // ── tag ref existence is checked BEFORE the annotation write ─────

    [Fact]
    public async Task TagCreate_ExistingRef_DoesNotWriteAnnotation()
    {
        // C (tag.c:302-320): the ref existence is checked BEFORE
        // write_tag_annotation — an existing tag with allowOverwrite=0 fails
        // with GIT_EEXISTS "tag already exists" and writes NOTHING.
        await using GitRepository repo = await InitRepoAsync();

        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
        Commit? target = await repo.ObjectLookupAsync<Commit>(commit, TestContext.Current.CancellationToken);
        Assert.NotNull(target);
        GitSignature tagger = TestSig();

        long before = CountLooseObjects(repo.Path);
        GitOid first = await repo.TagCreateAsync("v1", target!, tagger, "message one\n", allowOverwrite: false, TestContext.Current.CancellationToken);
        long afterFirst = CountLooseObjects(repo.Path);
        Assert.Equal(before + 1, afterFirst); // the annotation object

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.TagCreateAsync("v1", target!, tagger, "message two\n", allowOverwrite: false, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("tag already exists", ex.Message);

        // No new annotation was written (the object count is unchanged).
        Assert.Equal(afterFirst, CountLooseObjects(repo.Path));
        Assert.Equal(first, ((GitDirectReference)(await repo.ReferenceResolveAsync("refs/tags/v1", TestContext.Current.CancellationToken))!).Target);
    }

    // ── revwalk quick-parse committer strictness + parent gate ───────

    [Fact]
    public async Task RevWalk_DualAngleCommitter_UsesLastAngleTimestamp()
    {
        // C (signature.c:346-349): git_signature__parse uses memrchr — the
        // LAST '<' / '>' win. A committer with two angle pairs ("A <a@x>
        // <b@x> 123 +0000") yields time 123.
        await using GitRepository repo = await InitRepoAsync();

        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);

        byte[] c1 = CommitBuffer(tree, [], "A <a@x> 100 +0000", "A <a@x> 100 +0000", "m1\n");
        GitOid c1Oid = await repo.ObjectWriteAsync(GitObjectType.Commit, c1, TestContext.Current.CancellationToken);

        byte[] c2 = CommitBuffer(tree, [c1Oid], "A <a@x> 100 +0000", "A <a@x> <b@x> 123 +0000", "m2\n");
        GitOid c2Oid = await repo.ObjectWriteAsync(GitObjectType.Commit, c2, TestContext.Current.CancellationToken);

        using GitRevWalker walker = repo.NewRevWalker();
        walker.Sort = GitSortMode.Time; // newest first
        await walker.PushAsync(c2Oid, TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([c2Oid, c1Oid], commits);
    }

    [Fact]
    public async Task RevWalk_TooManyParents_Throws()
    {
        // C (commit_list.c:149-152): a commit with more than 2^16 parents
        // fails the quick parse with rc=-1, GIT_ERROR_INVALID class,
        // "commit has more than 2^16 parents".
        await using GitRepository repo = await InitRepoAsync();

        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);

        var sb = new StringBuilder();
        sb.Append("tree ").Append(tree).Append('\n');
        string parent = "1111111111111111111111111111111111111111";
        for (int i = 0; i < 65536; i++)
        {
            sb.Append("parent ").Append(parent).Append('\n');
        }

        sb.Append("author A <a@x> 100 +0000\n");
        sb.Append("committer A <a@x> 200 +0000\n\nm\n");
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Commit, Encoding.ASCII.GetBytes(sb.ToString()), TestContext.Current.CancellationToken);

        using GitRevWalker walker = repo.NewRevWalker();
        await walker.PushAsync(oid, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => ConsumeAsync(walker));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("commit has more than 2^16 parents", ex.Message);
    }

    private static async Task ConsumeAsync(GitRevWalker walker)
    {
        await foreach (GitOid _ in walker.WalkAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    private static byte[] CommitBuffer(GitOid tree, GitOid[] parents, string author, string committer, string message)
    {
        var sb = new StringBuilder();
        sb.Append("tree ").Append(tree).Append('\n');
        foreach (GitOid parent in parents)
        {
            sb.Append("parent ").Append(parent).Append('\n');
        }

        sb.Append("author ").Append(author).Append('\n');
        sb.Append("committer ").Append(committer).Append('\n');
        sb.Append('\n');
        sb.Append(message);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static long CountLooseObjects(string gitDir)
    {
        string objectsDir = Path.Combine(gitDir, "objects");
        long count = 0;
        foreach (string f in Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories))
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(f)!);
            if (dir is not "pack" and not "info")
            {
                count++;
            }
        }

        return count;
    }
}

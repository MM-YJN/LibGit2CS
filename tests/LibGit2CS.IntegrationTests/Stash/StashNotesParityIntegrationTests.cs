using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Stash;

/// <summary>
/// End-to-end tests for the stash/notes parity behaviors (index commit
/// message, nothing-to-stash detection, workdir tree, notes
/// commit messages) in libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class StashNotesParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public StashNotesParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashNotesParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string fileName, string content)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stash_StagedThenReverted_Proceeds_WithCMessagesAndTree()
    {
        // A staged change + reverted workdir — stash proceeds
        // (status-based detection), the index commit message ends with "\n\n",
        // and the stash tree holds the WORKDIR content (v1).
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        _ = await CommitFileAsync(repo, "f.txt", "v1\n");

        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "v2\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "v1\n", cancellationToken: TestContext.Current.CancellationToken);

        GitOid stashOid = await repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, TestContext.Current.CancellationToken);
        Assert.False(stashOid.IsZero);

        Commit stash = await repo.ObjectLookupAsync<Commit>(stashOid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("stash commit missing");
        Commit indexCommit = await repo.ObjectLookupAsync<Commit>(stash.ParentId(1), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("index commit missing");
        Assert.StartsWith("index on ", indexCommit.Message, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", indexCommit.Message, StringComparison.Ordinal);

        GitTree? tree = await repo.ObjectLookupAsync<GitTree>(stash.Tree, TestContext.Current.CancellationToken);
        GitTreeEntry? entry = await tree!.EntryByPathAsync(GitPath.FromUtf8String("f.txt"), TestContext.Current.CancellationToken);
        GitBlob? blob = await repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal("v1\n", Encoding.UTF8.GetString(blob!.Raw.Span));
    }

    [Fact]
    public async Task Notes_CommitMessage_MatchesC()
    {
        // the notes commit message is libgit2's constant.
        string path = Path.Combine(_tempDir, "notes-message");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        GitOid targetOid = await CommitFileAsync(repo, "t.txt", "target\n");
        _ = await repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "my note", allowOverwrite: false, TestContext.Current.CancellationToken);

        GitReference? notesRef = await repo.ReferenceResolveAsync("refs/notes/commits", TestContext.Current.CancellationToken);
        Assert.NotNull(notesRef);
        Commit notesCommit = await repo.ObjectLookupAsync<Commit>(((GitDirectReference)notesRef!).Target, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("notes commit missing");
        Assert.Equal("Notes added by 'git_note_create' from libgit2", notesCommit.Message);
    }
}

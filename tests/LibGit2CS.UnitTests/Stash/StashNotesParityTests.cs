using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Stash;

/// <summary>
/// Regression tests for the stash/notes parity behaviors (index/untracked
/// commit messages, nothing-to-stash detection, workdir tree
/// builder, notes commit messages) in
/// libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class StashNotesParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StashNotesParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashNotesParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
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

    private async Task<GitOid> CommitFileAsync(string fileName, string content)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds the fixture: HEAD has f.txt=v1; the index has v2
    /// (staged); the workdir is reverted to v1.
    /// </summary>
    private async Task SetupStagedThenRevertedAsync()
    {
        _ = await CommitFileAsync("f.txt", "v1\n");
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "v2\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "v1\n", cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<Commit> LookupAsync(GitOid oid)
        => await _repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("commit not found");

    [Fact]
    public async Task Stash_StagedOnlyChange_Proceeds()
    {
        // C's ensure_there_are_changes_to_stash (stash.c:575-600) is
        // STATUS-based (INDEX_AND_WORKDIR) — a staged change counts even when
        // the workdir matches HEAD (C-verified: rc=0).
        await SetupStagedThenRevertedAsync();

        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, TestContext.Current.CancellationToken);
        Assert.False(stashOid.IsZero);
    }

    [Fact]
    public async Task Stash_IndexCommitMessage_DoubleTrailingNewline()
    {
        // C formats "index on %s\n" against the already-newline-
        // terminated base message (stash.c:137) → the index commit ends with
        // "\n\n" (C-verified: "index on master: 97d9f23 c1\n\n").
        await SetupStagedThenRevertedAsync();
        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, TestContext.Current.CancellationToken);

        Commit stash = await LookupAsync(stashOid);
        Commit indexCommit = await LookupAsync(stash.ParentId(1));
        string message = indexCommit.Message ?? string.Empty;
        Assert.StartsWith("index on ", message, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stash_StagedThenReverted_WorkdirTreeHasWorkdirContent()
    {
        // build_workdir_tree merges tree→index with index→workdir and
        // re-reads the WORKDIR blob for changed entries (stash.c:379-414,
        // stash_to_index) — the stash tree holds v1, not the staged v2
        // (C-verified: stash tree f.txt = "v1\n").
        await SetupStagedThenRevertedAsync();
        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, TestContext.Current.CancellationToken);

        Commit stash = await LookupAsync(stashOid);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(stash.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        GitTreeEntry? entry = await tree!.EntryByPathAsync(GitPath.FromUtf8String("f.txt"), TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("v1\n", System.Text.Encoding.UTF8.GetString(blob!.Raw.Span));
    }

    [Fact]
    public async Task Notes_CommitMessage_MatchesC()
    {
        // the notes commit message is libgit2's constant
        // GIT_NOTES_DEFAULT_MSG_ADD (notes.h:12-13), not the git-CLI wording.
        GitOid targetOid = await CommitFileAsync("t.txt", "target\n");
        _ = await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "my note", allowOverwrite: false, TestContext.Current.CancellationToken);

        GitReference? notesRef = await _repo.ReferenceResolveAsync("refs/notes/commits", TestContext.Current.CancellationToken);
        Assert.NotNull(notesRef);
        GitOid notesCommitOid = ((GitDirectReference)notesRef!).Target;
        Commit notesCommit = await LookupAsync(notesCommitOid);
        Assert.Equal("Notes added by 'git_note_create' from libgit2", notesCommit.Message);
    }
}

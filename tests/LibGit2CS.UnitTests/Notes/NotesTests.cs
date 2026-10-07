using LibGit2CS.Core;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Notes;

public sealed class NotesTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public NotesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_NotesTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    // ── Create + Read ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_AndRead_DefaultNamespace()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        GitOid blobOid = await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "reviewed-by: Alice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(blobOid.IsZero);

        using GitNote? note = await _repo.NotesReadAsync(null, targetOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal("reviewed-by: Alice", note!.Message);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("café", "café")]
    [InlineData("café\0ignored", "café")]
    [InlineData("\0ignored", "")]
    public async Task Message_DecodesUtf8ThroughFirstNul(string message, string expected)
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, message, cancellationToken: TestContext.Current.CancellationToken);
        using GitNote? note = await _repo.NotesReadAsync(null, targetOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal(expected, note.Message);
    }

    // ── Create with custom namespace ─────────────────────────────────────

    [Fact]
    public async Task Create_CustomNamespace_ReadsFromCustomRef()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        await _repo.NotesCreateAsync("refs/notes/review", TestSig(), TestSig(), targetOid, "LGTM", cancellationToken: TestContext.Current.CancellationToken);

        using GitNote? note = await _repo.NotesReadAsync("refs/notes/review", targetOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal("LGTM", note!.Message);
    }

    // ── Create duplicate → error ────────────────────────────────────────

    [Fact]
    public async Task Create_Duplicate_ThrowsExists()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "first note", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "second note", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Create overwrite ────────────────────────────────────────────────

    [Fact]
    public async Task Create_Overwrite_UpdatesNote()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "original", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "updated", allowOverwrite: true, cancellationToken: TestContext.Current.CancellationToken);

        using GitNote? note = await _repo.NotesReadAsync(null, targetOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("updated", note!.Message);
    }

    // ── Remove ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_DeletesNote()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), targetOid, "a note", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.NotesRemoveAsync(null, TestSig(), TestSig(), targetOid, cancellationToken: TestContext.Current.CancellationToken);

        // C's git_note_read fails with GIT_ENOTFOUND "note could not be
        // found" when no note exists for the target (the ref still exists).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.NotesReadAsync(null, targetOid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Remove non-existent → error ─────────────────────────────────────

    [Fact]
    public async Task Remove_NonExistent_ThrowsNotFound()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        await Assert.ThrowsAsync<GitException>(async () => await _repo.NotesRemoveAsync(null, TestSig(), TestSig(), targetOid, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Read non-existent → GIT_ENOTFOUND  ─────────────────────────

    [Fact]
    public async Task Read_NonExistent_ThrowsNotFound()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        // C (notes.c:477-495): git_note_read fails with GIT_ENOTFOUND when
        // the notes ref is missing.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.NotesReadAsync(null, targetOid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── DefaultRef ──────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultRef_NoConfig_ReturnsDefault()
    {
        string refName = await _repo.NotesDefaultRefNameAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/notes/commits", refName);
    }

    [Fact]
    public async Task DefaultRef_WithConfig_ReturnsConfigValue()
    {
        await _repo.Config.SetStringAsync("core.notesref", "refs/notes/custom", cancellationToken: TestContext.Current.CancellationToken);

        string refName = await _repo.NotesDefaultRefNameAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/notes/custom", refName);
    }

    // ── ForEach ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ForEach_EmptyRepo_ThrowsNotFound()
    {
        // C's git_note_foreach errors on a missing notes ref.
        await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitNoteEntry e in _repo.NotesForEachAsync(null, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task ForEach_WithNotes_ReturnsEntries()
    {
        GitOid target1 = await WriteCommitWithFile("file1.txt", "content1\n");
        GitOid target2 = await WriteCommitWithFile("file2.txt", "content2\n", target1);

        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), target1, "note1", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), target2, "note2", cancellationToken: TestContext.Current.CancellationToken);

        var entries = new List<GitNoteEntry>();
        await foreach (GitNoteEntry e in _repo.NotesForEachAsync(null, cancellationToken: TestContext.Current.CancellationToken))
        {
            entries.Add(e);
        }
        Assert.Equal(2, entries.Count);
    }

    // ── CommitCreate + CommitRead ───────────────────────────────────────

    [Fact]
    public async Task CommitCreate_AndCommitRead_Roundtrip()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        GitOid commitOid = await _repo.NotesCommitCreateAsync(null, TestSig(), TestSig(), targetOid, "test note", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(commitOid.IsZero);

        Commit notesCommit = (await _repo.ObjectLookupAsync<Commit>(commitOid, CancellationToken.None))!;
        using GitNote? note = await _repo.NotesCommitReadAsync(notesCommit, targetOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal("test note", note!.Message);
    }

    // ── CommitRemove ────────────────────────────────────────────────────

    [Fact]
    public async Task CommitRemove_RemovesNote()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        GitOid commitOid = await _repo.NotesCommitCreateAsync(null, TestSig(), TestSig(), targetOid, "note to remove", cancellationToken: TestContext.Current.CancellationToken);
        Commit notesCommit = (await _repo.ObjectLookupAsync<Commit>(commitOid, CancellationToken.None))!;

        GitOid newCommitOid = await _repo.NotesCommitRemoveAsync(notesCommit, TestSig(), TestSig(), targetOid, cancellationToken: TestContext.Current.CancellationToken);

        Commit newNotesCommit = (await _repo.ObjectLookupAsync<Commit>(newCommitOid, CancellationToken.None))!;
        // C's git_note_commit_read fails with GIT_ENOTFOUND.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.NotesCommitReadAsync(newNotesCommit, targetOid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Multiple notes on different targets ─────────────────────────────

    [Fact]
    public async Task Create_MultipleTargets_AllReadable()
    {
        GitOid target1 = await WriteCommitWithFile("file1.txt", "content1\n");
        GitOid target2 = await WriteCommitWithFile("file2.txt", "content2\n", target1);

        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), target1, "note for target1", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), target2, "note for target2", cancellationToken: TestContext.Current.CancellationToken);

        using GitNote? note1 = await _repo.NotesReadAsync(null, target1, cancellationToken: TestContext.Current.CancellationToken);
        using GitNote? note2 = await _repo.NotesReadAsync(null, target2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("note for target1", note1!.Message);
        Assert.Equal("note for target2", note2!.Message);
    }

    // ── Note author/committer ───────────────────────────────────────────

    [Fact]
    public async Task Note_AuthorCommitter_FromNotesCommit()
    {
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        var author = new GitSignature("Author Name", "author@example.com", new GitTime(1700000000, 0));
        var committer = new GitSignature("Committer Name", "committer@example.com", new GitTime(1700000001, 0));

        await _repo.NotesCreateAsync(null, author, committer, targetOid, "test", cancellationToken: TestContext.Current.CancellationToken);

        using GitNote? note = await _repo.NotesReadAsync(null, targetOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Author Name", note!.Author.Name);
        Assert.Equal("Committer Name", note.Committer.Name);
    }
}

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Notes;

/// <summary>
/// Regression tests for the notes parity behaviors (read errors,
/// empty fanout subtree kept on removal, non-commit notes ref) in
/// libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class NotesMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public NotesMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_NotesMedParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ── reads raise GIT_ENOTFOUND ──────────────────────────────────

    [Fact]
    public async Task Read_MissingRef_ThrowsNotFound()
    {
        // (notes.c:477-495): git_note_read errors when the notes ref is
        // missing (retrieve_note_commit → git_reference_name_to_id
        // GIT_ENOTFOUND).
        GitOid targetOid = await WriteCommitWithFile("file.txt", "hello\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.NotesReadAsync(null, targetOid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Read_NoNoteForTarget_ThrowsNotFound()
    {
        // (notes.c:17-21): no note for the target → GIT_ENOTFOUND
        // "note could not be found" (C-verified).
        GitOid target1 = await WriteCommitWithFile("f1.txt", "1\n");
        GitOid target2 = await WriteCommitWithFile("f2.txt", "2\n", target1);
        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), target1, "a note\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.NotesReadAsync(null, target2, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("note could not be found", ex.Message);
    }

    [Fact]
    public async Task CommitRead_NoNote_ThrowsNotFound()
    {
        // (notes.c:455-475): git_note_commit_read propagates note_lookup's
        // GIT_ENOTFOUND.
        GitOid target = await WriteCommitWithFile("f.txt", "1\n");
        GitOid commitOid = await _repo.NotesCommitCreateAsync(null, TestSig(), TestSig(), target, "a note\n", cancellationToken: TestContext.Current.CancellationToken);
        Commit notesCommit = (await _repo.ObjectLookupAsync<Commit>(commitOid, CancellationToken.None))!;
        GitOid other = await WriteCommitWithFile("g.txt", "2\n", target);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.NotesCommitReadAsync(notesCommit, other, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("note could not be found", ex.Message);
    }

    [Fact]
    public async Task ForEach_MissingRef_ThrowsNotFound()
    {
        // (notes.c:714-739): git_note_foreach errors on a missing ref
        // (via git_note_iterator_new) — it does not yield an empty sequence.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitNoteEntry _ in _repo.NotesForEachAsync(null, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── removal keeps the empty fanout subtree ─────────────────────

    [Fact]
    public async Task Remove_LastNoteInFanout_KeepsEmptySubtree()
    {
        // (notes.c:179-191): after removing the last note inside a fanout
        // directory, the (now EMPTY) subtree is written back into the parent
        // tree unconditionally (C-verified) — the removal commit OID depends
        // on it.
        GitOid noteBlob = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes("a note\n"), TestContext.Current.CancellationToken);

        // Find a target blob whose hex starts with "aa" (the fanout prefix).
        GitOid target;
        string targetHex;
        for (int i = 0; ; i++)
        {
            target = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes($"seed {i}\n"), TestContext.Current.CancellationToken);
            targetHex = target.ToString();
            if (targetHex.StartsWith("aa", StringComparison.Ordinal))
            {
                break;
            }
        }

        // Hand-craft the notes tree: aa/<targetHex[2..]> → note blob.
        var subBuilder = new GitTreeBuilder(_repo);
        await subBuilder.InsertAsync(GitPath.FromUtf8String(targetHex[2..]), noteBlob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid subTree = await subBuilder.WriteAsync(TestContext.Current.CancellationToken);
        var rootBuilder = new GitTreeBuilder(_repo);
        await rootBuilder.InsertAsync(GitPath.FromUtf8String("aa"), subTree, GitFileMode.Tree, TestContext.Current.CancellationToken);
        GitOid rootTree = await rootBuilder.WriteAsync(TestContext.Current.CancellationToken);

        GitOid notesCommit = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = rootTree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "notes\n",
        }, TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/notes/commits", notesCommit, force: true, logMessage: null, TestContext.Current.CancellationToken);

        // Remove the only note inside the fanout directory.
        await _repo.NotesRemoveAsync(null, TestSig(), TestSig(), target, TestContext.Current.CancellationToken);

        // The removal commit's tree must still contain the (empty) "aa" subtree.
        GitReference? notesRef = await _repo.ReferenceResolveAsync("refs/notes/commits", TestContext.Current.CancellationToken);
        Assert.NotNull(notesRef);
        GitOid newCommitOid = ((GitDirectReference)notesRef!).Target;
        Commit newCommit = (await _repo.ObjectLookupAsync<Commit>(newCommitOid, CancellationToken.None))!;
        GitTree? newRoot = await _repo.ObjectLookupAsync<GitTree>(newCommit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(newRoot);

        GitTreeEntry? aaEntry = null;
        foreach (GitTreeEntry entry in newRoot!)
        {
            if (entry.Name.ToUtf8String() == "aa")
            {
                aaEntry = entry;
                break;
            }
        }

        Assert.NotNull(aaEntry);
        GitTree? aaSubtree = await _repo.ObjectLookupAsync<GitTree>(aaEntry!.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(aaSubtree);
        Assert.Equal(0, aaSubtree!.EntryCount);
    }

    // ── non-commit notes ref ───────────────────────────────────────

    [Fact]
    public async Task Create_NonCommitRef_OverwritesLikeC()
    {
        // (notes.c:544-548): retrieve_note_commit's type-mismatch lookup
        // is GIT_ENOTFOUND ("the requested type does not match the type in
        // the ODB"), which passes the `error != GIT_ENOTFOUND` guard — C
        // creates a fresh notes commit and force-overwrites the ref
        // (C-verified with a probe: rc=0, ref rewritten). The create must
        // not be refused.
        GitOid target = await WriteCommitWithFile("f.txt", "1\n");
        GitOid blob = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes("not a commit\n"), TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/notes/commits", blob, force: true, logMessage: null, TestContext.Current.CancellationToken);

        await _repo.NotesCreateAsync(null, TestSig(), TestSig(), target, "hello\n", allowOverwrite: false, TestContext.Current.CancellationToken);

        GitReference? notesRef = await _repo.ReferenceResolveAsync("refs/notes/commits", TestContext.Current.CancellationToken);
        Assert.NotNull(notesRef);
        Commit? notesCommit = await _repo.ObjectLookupAsync<Commit>(((GitDirectReference)notesRef!).Target, CancellationToken.None);
        Assert.NotNull(notesCommit);

        using GitNote? note = await _repo.NotesReadAsync(null, target, TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal("hello\n", note!.Message);
    }
}

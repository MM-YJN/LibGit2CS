using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Notes;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the notes engine
/// (<see cref="GitRepository.NotesCreateAsync"/>/<see cref="GitRepository.NotesReadAsync"/>/
/// <see cref="GitRepository.NotesRemoveAsync"/>/<see cref="GitRepository.NotesForEachAsync"/>)
/// exercised end-to-end against a commit fetched from a real OpenSSH+git
/// container via <see cref="SshGitDockerFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers notes against
/// locally-initialized repos, but the <see cref="Notes"/> namespace
/// had <c>0%</c> integration coverage — no end-to-end path through fetch →
/// clone (which populates the object database from a fetched pack) → note
/// creation (blob write + notes-tree manipulation + notes-commit write +
/// <c>refs/notes/*</c> update) → read/remove/iterate. These tests close that
/// gap by annotating the cloned HEAD commit.
/// </para>
/// </remarks>
public sealed class NotesDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public NotesDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
    {
        _alpine = alpine;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    private static GitRemoteCallbacks PasswordCallbacks => new()
    {
        Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
            new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
        CertificateCheck = _ => true,
    };

    private static string Url(SshGitDockerContainer fixture)
    {
        return $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
    }

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Collects note entries into a list.</summary>
    private static async Task<List<GitNoteEntry>> NoteListAsync(GitRepository repo, string? notesRef, CancellationToken ct)
    {
        var list = new List<GitNoteEntry>();
        await foreach (GitNoteEntry entry in repo.NotesForEachAsync(notesRef, ct))
        {
            list.Add(entry);
        }

        return list;
    }

    // ── Create + Read (default namespace) ───────────────────────────────

    /// <summary>
    /// Create + read a note on the cloned HEAD commit in the default
    /// notes namespace. <see cref="GitRepository.NotesCreateAsync"/> writes the note
    /// blob, updates the notes tree/commit, and points
    /// <c>refs/notes/commits</c> at it; <see cref="GitRepository.NotesReadAsync"/>
    /// reads it back; <see cref="GitRepository.NotesForEachAsync"/> lists it. Exercises
    /// the full create/read/iterate path against a fetched object database.
    /// </summary>
    [Fact]
    public async Task Notes_CreateAndRead_DefaultNamespace()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-notes-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Annotate the cloned HEAD commit.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            GitOid targetOid = ((GitDirectReference)head!).Target;

            GitOid blobOid = await cloned.NotesCreateAsync(notesRef: null, Sig, Sig, targetOid, "review note", cancellationToken: ct);
            Assert.False(blobOid.IsZero);

            using GitNote? note = await cloned.NotesReadAsync(notesRef: null, targetOid, ct);
            Assert.NotNull(note);
            Assert.Equal("review note", note!.Message);

            // The default notes ref must now exist.
            Assert.NotNull(await cloned.ReferenceLookupAsync("refs/notes/commits", ct));

            // ForEach lists exactly one entry, annotating the HEAD commit.
            List<GitNoteEntry> entries = await NoteListAsync(cloned, notesRef: null, ct);
            Assert.Single(entries);
            Assert.Equal(targetOid, entries[0].AnnotatedId);
            Assert.Equal(blobOid, entries[0].NoteId);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Remove ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.NotesRemoveAsync"/> deletes a note: after create +
    /// remove, <see cref="GitRepository.NotesReadAsync"/> returns null and
    /// <see cref="GitRepository.NotesForEachAsync"/> is empty. Exercises the
    /// note-tree-removal + notes-commit write path.
    /// </summary>
    [Fact]
    public async Task Notes_Remove_DeletesNote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-notes-remove-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            GitOid targetOid = ((GitDirectReference)head!).Target;

            await cloned.NotesCreateAsync(notesRef: null, Sig, Sig, targetOid, "a note", cancellationToken: ct);
            Assert.Single(await NoteListAsync(cloned, notesRef: null, ct));

            await cloned.NotesRemoveAsync(notesRef: null, Sig, Sig, targetOid, ct);

            // git_note_read returns GIT_ENOTFOUND for a missing note
            // (notes.c:477-495), so a missing note throws instead of returning null.
            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => cloned.NotesReadAsync(notesRef: null, targetOid, ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);

            Assert.Empty(await NoteListAsync(cloned, notesRef: null, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Custom namespace ─────────────────────────────────────────────────

    /// <summary>
    /// Notes in a custom namespace (<c>refs/notes/review</c>) are
    /// isolated from the default namespace: a note created there reads back
    /// from the custom ref but is NOT visible via the default ref (null).
    /// Exercises the namespace-normalization + per-ref notes-tree path.
    /// </summary>
    [Fact]
    public async Task Notes_CustomNamespace_IsolatesFromDefault()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-notes-custom-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            GitOid targetOid = ((GitDirectReference)head!).Target;

            const string customRef = "refs/notes/review";
            await cloned.NotesCreateAsync(customRef, Sig, Sig, targetOid, "LGTM", cancellationToken: ct);

            // Readable from the custom ref.
            using GitNote? note = await cloned.NotesReadAsync(customRef, targetOid, ct);
            Assert.NotNull(note);
            Assert.Equal("LGTM", note!.Message);

            // NOT visible via the default namespace — git_note_read returns
            // GIT_ENOTFOUND for a missing notes ref.
            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => cloned.NotesReadAsync(notesRef: null, targetOid, ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);

            // The custom ref exists; the default notes ref does not.
            Assert.NotNull(await cloned.ReferenceLookupAsync(customRef, ct));
            Assert.Null(await cloned.ReferenceLookupAsync("refs/notes/commits", ct));

            // ForEach over the custom ref lists exactly one entry.
            Assert.Single(await NoteListAsync(cloned, customRef, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Refs;

namespace LibGit2CS.Repository;

/// <content> Notes operations. Managed port of libgit2's <c>src/libgit2/notes.c</c> public entry points. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>The default notes ref.</summary>
    public const string NotesDefaultRef = "refs/notes/commits";

    /// <summary>The default add message.</summary>
    // C (notes.h:12-13): GIT_NOTES_DEFAULT_MSG_ADD — libgit2's own wording,
    // NOT the git-CLI message. The notes commit message is written verbatim,
    // so every notes commit OID depends on this constant.
    public const string NotesDefaultMsgAdd = "Notes added by 'git_note_create' from libgit2";

    /// <summary>The default remove message.</summary>
    // C (notes.h:14-15): GIT_NOTES_DEFAULT_MSG_RM.
    public const string NotesDefaultMsgRemove = "Notes removed by 'git_note_remove' from libgit2";

    // ── Create ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a note on the target object. Matches <c>git_note_create</c>
    /// (notes.c:528-569).
    /// </summary>
    /// <param name="notesRef">The notes ref (e.g. <c>refs/notes/commits</c>), or null for default.</param>
    /// <param name="author">Author signature.</param>
    /// <param name="committer">Committer signature.</param>
    /// <param name="targetOid">The OID of the object to annotate.</param>
    /// <param name="note">The note content.</param>
    /// <param name="allowOverwrite">True to overwrite an existing note; false to reject duplicates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the created note blob.</returns>
    public async Task<GitOid> NotesCreateAsync(
        string? notesRef,
        GitSignature author,
        GitSignature committer,
        GitOid targetOid,
        string note,
        bool allowOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(committer);
        ArgumentNullException.ThrowIfNull(note);

        string resolvedRef = await NotesNormalizeNamespaceAsync(notesRef, cancellationToken).ConfigureAwait(false);

        // Look up existing notes commit (if any).
        Commit? existingCommit = null;
        GitReference? existingRef = await Refs.LookupAsync(resolvedRef, cancellationToken).ConfigureAwait(false);
        if (existingRef is not null)
        {
            GitOid? target = await NotesResolveRefOidAsync(existingRef, cancellationToken).ConfigureAwait(false);
            if (target is not null)
            {
                try
                {
                    existingCommit = await Objects.LookupAsync<Commit>(target.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                {
                    // C (notes.c:544-548): retrieve_note_commit returns
                    // GIT_ENOTFOUND both for a missing ref and for a ref
                    // pointing at a non-commit (the type-mismatch lookup is
                    // also ENOTFOUND, object.c:126) — git_note_create
                    // proceeds with NO existing notes commit and overwrites
                    // the ref (C-verified: rc=0, ref force-rewritten).
                    existingCommit = null;
                }
            }
        }

        (GitOid commitOid, GitOid blobOid) = await NotesCommitCreateInternalAsync(
            existingCommit, author, committer, targetOid, note, allowOverwrite, cancellationToken).ConfigureAwait(false);

        // Update the notes ref.
        await Refs.CreateAsync(resolvedRef, commitOid, force: true, logMessage: null, cancellationToken).ConfigureAwait(false);

        return blobOid;
    }

    /// <summary>
    /// Creates a note on the target object from an existing notes commit.
    /// Matches <c>git_note_commit_create</c> (notes.c:497-526).
    /// </summary>
    /// <param name="parent">The existing notes commit, or null for a new notes tree.</param>
    /// <param name="author">Author signature.</param>
    /// <param name="committer">Committer signature.</param>
    /// <param name="targetOid">The OID of the object to annotate.</param>
    /// <param name="note">The note content.</param>
    /// <param name="allowOverwrite">True to overwrite; false to reject duplicates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OID of the created notes commit.</returns>
    public async Task<GitOid> NotesCommitCreateAsync(
        Commit? parent,
        GitSignature author,
        GitSignature committer,
        GitOid targetOid,
        string note,
        bool allowOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(committer);
        ArgumentNullException.ThrowIfNull(note);

        (GitOid commitOid, GitOid _) = await NotesCommitCreateInternalAsync(
            parent, author, committer, targetOid, note, allowOverwrite, cancellationToken).ConfigureAwait(false);
        return commitOid;
    }

    private async Task<(GitOid commitOid, GitOid blobOid)> NotesCommitCreateInternalAsync(
        Commit? parent,
        GitSignature author,
        GitSignature committer,
        GitOid targetOid,
        string note,
        bool allowOverwrite,
        CancellationToken cancellationToken)
    {
        // Get the target OID hex string.
        string targetHex = targetOid.ToString();

        // Get the parent tree (if any).
        GitTree? parentTree = null;
        if (parent is not null)
        {
            parentTree = await Objects.LookupAsync<GitTree>(parent.Tree, cancellationToken).ConfigureAwait(false);
        }

        // Write the note blob.
        byte[] noteBytes = System.Text.Encoding.UTF8.GetBytes(note);
        GitOid blobOid = await Objects.WriteAsync(GitObjectType.Blob, noteBytes, cancellationToken).ConfigureAwait(false);

        // Manipulate the tree to insert the note.
        GitOid newTree = await NotesManipulateNoteInTreeAsync(
            parentTree, blobOid, targetHex, fanout: 0, isInsert: true, allowOverwrite, cancellationToken).ConfigureAwait(false);

        // Create the notes commit.
        GitOid[] parents = parent is not null ? [parent.Id] : Array.Empty<GitOid>();
        GitOid commitOid = await Commit.CreateAsync(this, new CommitCreateOptions
        {
            Tree = newTree,
            Parents = parents,
            Author = author,
            Committer = committer,
            Message = NotesDefaultMsgAdd,
        }, cancellationToken).ConfigureAwait(false);

        return (commitOid, blobOid);
    }

    // ── Remove ───────────────────────────────────────────────────────────

    /// <summary>
    /// Removes a note from the target object. Matches <c>git_note_remove</c>
    /// (notes.c:596-625).
    /// </summary>
    public async Task NotesRemoveAsync(
        string? notesRef,
        GitSignature author,
        GitSignature committer,
        GitOid targetOid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(committer);

        string resolvedRef = await NotesNormalizeNamespaceAsync(notesRef, cancellationToken).ConfigureAwait(false);

        // C (notes.c:596-625): retrieve_note_commit — a missing ref is
        // GIT_ENOTFOUND; symbolic refs are resolved (git_reference_name_to_id).
        Commit? existingCommit = await NotesRetrieveCommitAsync(notesRef, cancellationToken).ConfigureAwait(false);
        if (existingCommit is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{resolvedRef}' not found",
                GitErrorCategory.Reference);
        }

        GitOid newCommitOid = await NotesCommitRemoveAsync(existingCommit, author, committer, targetOid, cancellationToken).ConfigureAwait(false);

        await Refs.CreateAsync(resolvedRef, newCommitOid, force: true, logMessage: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a note from an existing notes commit. Matches
    /// <c>git_note_commit_remove</c> (notes.c:571-594).
    /// </summary>
    public async Task<GitOid> NotesCommitRemoveAsync(
        Commit notesCommit,
        GitSignature author,
        GitSignature committer,
        GitOid targetOid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notesCommit);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(committer);

        string targetHex = targetOid.ToString();
        GitTree tree = (await Objects.LookupAsync<GitTree>(notesCommit.Tree, cancellationToken).ConfigureAwait(false))!;

        // Manipulate the tree to remove the note.
        GitOid newTree = await NotesManipulateNoteInTreeAsync(
            tree, oid: default, targetHex, fanout: 0, isInsert: false, allowOverwrite: false, cancellationToken).ConfigureAwait(false);

        return await Commit.CreateAsync(this, new CommitCreateOptions
        {
            Tree = newTree,
            Parents = [notesCommit.Id],
            Author = author,
            Committer = committer,
            Message = NotesDefaultMsgRemove,
        }, cancellationToken).ConfigureAwait(false);
    }

    // ── Read ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a note from the target object. Matches <c>git_note_read</c>
    /// (notes.c:477-495).
    /// </summary>
    public async Task<GitNote?> NotesReadAsync(string? notesRef, GitOid targetOid, CancellationToken cancellationToken = default)
    {
        // C (notes.c:477-495, git_note_read): retrieve_note_commit errors
        // (missing ref / non-commit ref) propagate as GIT_ENOTFOUND.
        string resolvedRef = await NotesNormalizeNamespaceAsync(notesRef, cancellationToken).ConfigureAwait(false);
        Commit? commit = await NotesRetrieveCommitAsync(notesRef, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{resolvedRef}' not found",
                GitErrorCategory.Reference);
        }

        return await NotesCommitReadAsync(commit, targetOid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a note from an existing notes commit. Matches
    /// <c>git_note_commit_read</c> (notes.c:455-475).
    /// </summary>
    public async Task<GitNote?> NotesCommitReadAsync(Commit notesCommit, GitOid targetOid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notesCommit);

        string targetHex = targetOid.ToString();
        GitTree tree = (await Objects.LookupAsync<GitTree>(notesCommit.Tree, cancellationToken).ConfigureAwait(false))!;

        // Find the subtree containing the note (may use fanout).
        (GitTree? subtree, int fanout) = await NotesFindSubtreeRAsync(tree, targetHex, 0, cancellationToken).ConfigureAwait(false);
        if (subtree is null)
        {
            // C (notes.c:17-21, note_error_notfound): GIT_ENOTFOUND
            // "note could not be found".
            throw NoteErrorNotFound();
        }

        // Find the blob within the subtree.
        string remainingHex = targetHex[fanout..];
        GitOid? blobOid = NotesFindBlob(subtree, remainingHex);
        if (blobOid is null)
        {
            throw NoteErrorNotFound();
        }

        GitBlob? blob = await Objects.LookupAsync<GitBlob>(blobOid.Value, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            // C: note_lookup propagates the blob-lookup GIT_ENOTFOUND.
            throw NoteErrorNotFound();
        }

        return new GitNote(blobOid.Value, notesCommit, blob);
    }

    // ── Default ref ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the default notes ref. Matches <c>git_note_default_ref</c>
    /// (notes.c:627-630).
    /// </summary>
    public async ValueTask<string> NotesDefaultRefNameAsync(CancellationToken cancellationToken = default)
    {
        // No byte-domain gap: the value feeds the string ref-name tier (Refs.LookupAsync/ResolveAsync); GetBytesAsync+decode would be identical to
        // GetStringAsync, so the display tier is final here.
        string? val = await Config.GetStringAsync("core.notesref", cancellationToken).ConfigureAwait(false);
        if (val is not null)
        {
            return val;
        }

        return NotesDefaultRef;
    }

    // ── ForEach / Iterator ───────────────────────────────────────────────

    /// <summary>
    /// Iterates over all notes in a notes ref. Matches
    /// <c>git_note_foreach</c> (notes.c:714-739).
    /// </summary>
    public async IAsyncEnumerable<GitNoteEntry> NotesForEachAsync(
        string? notesRef,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // C (notes.c:714-739): git_note_foreach errors (GIT_ENOTFOUND) when
        // the notes ref is missing — it does not yield an empty sequence.
        string resolvedRef = await NotesNormalizeNamespaceAsync(notesRef, cancellationToken).ConfigureAwait(false);
        if (await Refs.LookupAsync(resolvedRef, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{resolvedRef}' not found",
                GitErrorCategory.Reference);
        }

        using GitNoteIterator iter = await NotesIteratorNewAsync(notesRef, cancellationToken).ConfigureAwait(false);
        await foreach (GitNoteEntry entry in iter.ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Creates a note iterator over a notes ref. Matches
    /// <c>git_note_iterator_new</c> (notes.c:768-788).
    /// </summary>
    public async Task<GitNoteIterator> NotesIteratorNewAsync(string? notesRef, CancellationToken cancellationToken = default)
    {
        string resolvedRef = await NotesNormalizeNamespaceAsync(notesRef, cancellationToken).ConfigureAwait(false);
        Commit? commit = await NotesRetrieveCommitAsync(notesRef, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{resolvedRef}' not found",
                GitErrorCategory.Reference);
        }

        return await NotesCommitIteratorNewAsync(commit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a note iterator from a notes commit. Matches
    /// <c>git_note_commit_iterator_new</c> (notes.c:749-766).
    /// </summary>
    /// <remarks>
    /// The <paramref name="notesCommit"/> must belong to this repository
    /// (<see cref="GitObject.Owner"/> == <c>this</c>); otherwise an
    /// <see cref="GitErrorCode.Invalid"/> exception is thrown. This makes the
    /// ownership invariant explicit at the instance-method boundary (the
    /// original static method trusted <c>notesCommit.Owner</c> implicitly).
    /// </remarks>
    public async Task<GitNoteIterator> NotesCommitIteratorNewAsync(Commit notesCommit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notesCommit);

        if (notesCommit.Owner is null)
        {
            throw new GitException(GitErrorCode.Invalid, "notes commit has no owning repository", GitErrorCategory.Repository);
        }

        if (notesCommit.Owner != this)
        {
            throw new GitException(GitErrorCode.Invalid, "notes commit does not belong to this repository", GitErrorCategory.Repository);
        }

        GitTree tree = (await Objects.LookupAsync<GitTree>(notesCommit.Tree, cancellationToken).ConfigureAwait(false))!;
        var iterOpts = new IteratorOptions();
        IIterator treeIter = TreeIterator.ForTree(tree, this, iterOpts);
        return new GitNoteIterator(treeIter, this);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// C (notes.c:434-453, retrieve_note_commit): resolves the notes ref
    /// (defaulting via <c>core.notesref</c>), resolves symbolic refs
    /// (git_reference_name_to_id), and looks up the commit. Returns null when
    /// the ref is missing (GIT_ENOTFOUND); a ref pointing at a non-commit
    /// throws the commit-lookup GIT_ENOTFOUND.
    /// </summary>
    private async Task<Commit?> NotesRetrieveCommitAsync(string? notesRef, CancellationToken cancellationToken)
    {
        string resolvedRef = await NotesNormalizeNamespaceAsync(notesRef, cancellationToken).ConfigureAwait(false);
        GitReference? reference = await Refs.LookupAsync(resolvedRef, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return null;
        }

        GitOid? target = await NotesResolveRefOidAsync(reference, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        return await Objects.LookupAsync<Commit>(target.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a reference to its target OID, walking symbolic refs. Matches
    /// <c>git_reference_name_to_id</c> (notes.c:446). Returns null when the
    /// chain is dangling.
    /// </summary>
    private ValueTask<GitOid?> NotesResolveRefOidAsync(GitReference reference, CancellationToken cancellationToken)
    {
        if (reference is GitDirectReference dr)
        {
            return ValueTask.FromResult<GitOid?>(dr.Target);
        }

        if (reference.IsSymbolic)
        {
            return new ValueTask<GitOid?>(NotesResolveRefOidSlowAsync(reference.NameKey, cancellationToken));
        }

        return ValueTask.FromResult<GitOid?>(null);
    }

    /// <summary>Slow path of <see cref="NotesResolveRefOidAsync"/>: resolves a symbolic ref chain (disk IO).</summary>
    private async Task<GitOid?> NotesResolveRefOidSlowAsync(RefNameKey referenceName, CancellationToken cancellationToken)
    {
        GitReference? resolved = await Refs.ResolveAsync(referenceName, cancellationToken).ConfigureAwait(false);
        return resolved is GitDirectReference rdr ? rdr.Target : null;
    }

    /// <summary>
    /// C (notes.c:17-21, note_error_notfound): GIT_ENOTFOUND with category
    /// GIT_ERROR_INVALID.
    /// </summary>
    private static GitException NoteErrorNotFound()
        => new(GitErrorCode.NotFound, "note could not be found", GitErrorCategory.Invalid);

    private async ValueTask<string> NotesNormalizeNamespaceAsync(string? notesRef, CancellationToken cancellationToken)
    {
        if (notesRef is not null)
        {
            return notesRef;
        }

        return await NotesDefaultRefNameAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recursively walks the notes tree to insert or remove a note, rebuilding
    /// parent trees bottom-up. Matches <c>manipulate_note_in_tree_r</c>
    /// (notes.c:133-198).
    /// </summary>
    private async Task<GitOid> NotesManipulateNoteInTreeAsync(
        GitTree? parent,
        GitOid oid,
        string targetHex,
        int fanout,
        bool isInsert,
        bool allowOverwrite,
        CancellationToken cancellationToken)
    {
        if (parent is null)
        {
            if (isInsert)
            {
                // No tree exists — insert the note at the top level.
                var tb = new GitTreeBuilder(this);
                await tb.InsertAsync(GitPath.FromUtf8String(targetHex[fanout..]), oid, GitFileMode.Regular, cancellationToken).ConfigureAwait(false);
                return await tb.WriteAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new GitException(GitErrorCode.NotFound, $"object '{targetHex}' has no note", GitErrorCategory.Repository);
        }

        // Look for a 2-char directory prefix or a matching blob at this level.
        GitPath? subtreeName = targetHex.Length > fanout + 2
            ? GitPath.FromUtf8String(targetHex[fanout..(fanout + 2)])
            : null;
        var blobName = GitPath.FromUtf8String(targetHex[fanout..]);

        // Check if there's a directory at this fanout level.
        GitTree? subTree = null;
        GitPath? subDirName = null;
        bool blobExists = false;

        foreach (GitTreeEntry entry in parent)
        {
            if (!NotesIsHex(entry.Name))
            {
                continue;
            }

            if (entry.IsTree && entry.Name.Length == 2 &&
                subtreeName is not null &&
                entry.Name == subtreeName.Value)
            {
                subTree = await Objects.LookupAsync<GitTree>(entry.Id, cancellationToken).ConfigureAwait(false);
                subDirName = entry.Name;
                break;
            }

            if (entry.Name == blobName)
            {
                blobExists = true;
                break;
            }
        }

        if (isInsert)
        {
            if (blobExists && !allowOverwrite)
            {
                throw new GitException(
                    GitErrorCode.Exists,
                    $"note for '{targetHex}' exists already",
                    GitErrorCategory.Repository);
            }

            if (subTree is not null && subDirName is not null)
            {
                // Recurse into the subtree.
                GitOid newSubTreeOid = await NotesManipulateNoteInTreeAsync(
                    subTree, oid, targetHex, fanout + 2, isInsert, allowOverwrite, cancellationToken).ConfigureAwait(false);

                // Rebuild parent tree with updated subtree.
                var tb = new GitTreeBuilder(this, parent);
                tb.Remove(subDirName.Value);
                await tb.InsertAsync(subDirName.Value, newSubTreeOid, GitFileMode.Tree, cancellationToken).ConfigureAwait(false);
                return await tb.WriteAsync(cancellationToken).ConfigureAwait(false);
            }

            // Insert the note blob at this level.
            var tb2 = new GitTreeBuilder(this, parent);
            if (blobExists)
            {
                tb2.Remove(blobName);
            }

            await tb2.InsertAsync(blobName, oid, GitFileMode.Regular, cancellationToken).ConfigureAwait(false);
            return await tb2.WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Remove.
            if (blobExists)
            {
                var tb = new GitTreeBuilder(this, parent);
                tb.Remove(blobName);
                return await tb.WriteAsync(cancellationToken).ConfigureAwait(false);
            }

            if (subTree is not null && subDirName is not null)
            {
                GitOid newSubTreeOid = await NotesManipulateNoteInTreeAsync(
                    subTree, oid, targetHex, fanout + 2, isInsert: false, allowOverwrite: false, cancellationToken).ConfigureAwait(false);

                var tb = new GitTreeBuilder(this, parent);
                tb.Remove(subDirName.Value);

                // C (notes.c:179-191): the (possibly empty) subtree is
                // written back UNCONDITIONALLY — after removing the last
                // note inside a fanout directory, an empty tree entry
                // remains in the parent (C-verified: the notes tree
                // keeps the empty fanout dir, so the removal commit OID
                // matches).
                await tb.InsertAsync(subDirName.Value, newSubTreeOid, GitFileMode.Tree, cancellationToken).ConfigureAwait(false);

                return await tb.WriteAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new GitException(GitErrorCode.NotFound, $"object '{targetHex}' has no note", GitErrorCategory.Repository);
        }
    }

    /// <summary>
    /// Recursively descends through fanout directories to find the subtree
    /// containing the note. Matches <c>find_subtree_r</c> (notes.c:57-77).
    /// </summary>
    private async Task<(GitTree? subtree, int fanout)> NotesFindSubtreeRAsync(
        GitTree root, string targetHex, int fanout, CancellationToken cancellationToken)
    {
        // Look for a 2-char directory or a matching blob at this level.
        GitPath? subtreeName = targetHex.Length > fanout + 2
            ? GitPath.FromUtf8String(targetHex[fanout..(fanout + 2)])
            : null;
        var blobName = GitPath.FromUtf8String(targetHex[fanout..]);

        foreach (GitTreeEntry entry in root)
        {
            if (!NotesIsHex(entry.Name))
            {
                continue;
            }

            if (entry.IsTree && entry.Name.Length == 2 &&
                subtreeName is not null &&
                entry.Name == subtreeName.Value)
            {
                GitTree? sub = await Objects.LookupAsync<GitTree>(entry.Id, cancellationToken).ConfigureAwait(false);
                if (sub is null)
                {
                    return (null, fanout);
                }

                // Recurse deeper.
                return await NotesFindSubtreeRAsync(sub, targetHex, fanout + 2, cancellationToken).ConfigureAwait(false);
            }

            if (entry.Name == blobName)
            {
                // Found the blob at this level — the subtree is the root.
                return (root, fanout);
            }
        }

        return (null, fanout);
    }

    /// <summary>
    /// Finds a blob by name within a tree. Matches <c>find_blob</c>
    /// (notes.c:79-96).
    /// </summary>
    private static GitOid? NotesFindBlob(GitTree tree, string target)
    {
        var targetName = GitPath.FromUtf8String(target);
        foreach (GitTreeEntry entry in tree)
        {
            if (entry.Name == targetName)
            {
                return entry.Id;
            }
        }

        return null;
    }

    private static bool NotesIsHex(GitPath s)
    {
        foreach (byte b in s.Span)
        {
            if (!NotesIsHexDigit((char)b))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NotesIsHexDigit(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

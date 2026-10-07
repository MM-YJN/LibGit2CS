// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Checkout;

/// <summary>
/// Writes blob content to the working directory, applying smudge filters.
/// Matches <c>blob_content_to_file</c> / <c>blob_content_to_link</c> /
/// <c>checkout_write_content</c> in <c>checkout.c:1520-1630</c>.
/// </summary>
internal sealed class WorkdirWriter
{
    private readonly GitRepository _repo;
    private readonly GitCheckoutOptions _opts;
    private readonly string _workdirRoot;
    // repo.Workdir
    // always ends with a separator, so the string-equality bound against
    // _workdirRoot never matched Path.GetDirectoryName output — the
    // ancestor walks in Remove/PruneEmptyParents were effectively unbounded
    // and could delete files above the workdir root. Compare against the
    // separator-trimmed root instead.
    private readonly string _workdirRootBound;
    private readonly bool _respectFilemode;
    // C's
    // should_remove_existing (checkout.c:1407-1417) — core.ignorecase &&
    // !GIT_CHECKOUT_DONT_REMOVE_EXISTING. When set, writes remove any
    // pre-existing entry at the target path (mkpath2file, checkout.c:1449-1483)
    // so O_CREAT|O_TRUNC cannot follow a symlink and write through it.
    private readonly bool _shouldRemoveExisting;

    /// <summary>
    /// when
    /// GIT_CHECKOUT_SKIP_LOCKED_DIRECTORIES is set, directories that cannot
    /// be emptied are left in place (C ORs GIT_RMDIR_SKIP_NONEMPTY into the
    /// removal flags, checkout.c:1831-1832).
    /// </summary>
    private readonly bool _skipLockedDirectories;
    private GitCheckoutPerformance _perf;

    /// <summary>
    /// The accumulated performance counters for this checkout. Callers
    /// (e.g. <see cref="CheckoutContext"/>) read this after checkout
    /// completes to report via <see cref="GitCheckoutOptions.Perfdata"/>.
    /// </summary>
    internal GitCheckoutPerformance Performance => _perf;

    /// <summary>
    /// The filesystem root all write paths are constructed against — either
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> (when set, matches
    /// libgit2's <c>target_directory</c>, checkout.c:2399-2401) or
    /// <see cref="GitRepository.Workdir"/>.
    /// </summary>
    internal string WorkdirRoot => _workdirRoot;

    /// <summary>
    /// Creates a workdir writer.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="opts">Checkout options (uses <see cref="GitCheckoutOptions.DisableFilters"/>,
    /// <see cref="GitCheckoutOptions.DirMode"/>, <see cref="GitCheckoutOptions.FileMode"/>,
    /// <see cref="GitCheckoutOptions.TargetDirectory"/>).</param>
    /// <param name="respectFilemode">Whether <c>core.filemode</c> is true (chmod
    /// the exec bit to match the target mode in both directions). Matches
    /// libgit2's <c>data->respect_filemode</c> (checkout.c:69-70, 2472-2474).</param>
    /// <param name="shouldRemoveExisting">Whether a pre-existing entry at the
    /// target path must be removed before writing (C's
    /// <c>should_remove_existing</c>, checkout.c:1407-1417).</param>
    /// <param name="skipLockedDirectories">Whether GIT_CHECKOUT_SKIP_LOCKED_DIRECTORIES
    /// is set — non-empty (locked) directories are kept on removal
    /// (checkout.c:1831-1832).</param>
    /// <param name="perf">Performance counters to increment. The writer keeps
    /// its own copy (struct value semantics) and exposes it via
    /// <see cref="Performance"/> for the caller to read back after checkout.</param>
    internal WorkdirWriter(GitRepository repo, GitCheckoutOptions opts, bool respectFilemode, bool shouldRemoveExisting, bool skipLockedDirectories, ref GitCheckoutPerformance perf)
    {
        _repo = repo;
        _opts = opts;
        _respectFilemode = respectFilemode;
        _shouldRemoveExisting = shouldRemoveExisting;
        _skipLockedDirectories = skipLockedDirectories;
        _perf = perf;
        _workdirRoot = opts.TargetDirectory ?? repo.Workdir
            ?? throw new GitException(GitErrorCode.BareRepo, "cannot checkout without a workdir or target directory", GitErrorCategory.Repository);

        // trim trailing separators for the ancestor-walk bound. A bare
        // root ("/" or "C:\") is kept as-is (trimming it would produce "" or
        // "C:", which GetDirectoryName output never equals).
        string trimmed = _workdirRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _workdirRootBound = trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':')
            ? _workdirRoot
            : trimmed;
    }

    /// <summary> Writes a blob to the workdir at the given relative path. Applies smudge filters (unless <see cref="GitCheckoutOptions.DisableFilters"/> is
    /// set), creates parent directories, and sets file mode. </summary> <param name="path">Relative path within the workdir (byte-faithful GitPath; transcode
    /// to OS encoding happens once at the <c>Path.Join</c> boundary, matching libgit2's single FS transcode point — the byte-domain contract).</param> <param
    /// name="blobOid">The blob OID to write.</param> <param name="mode">The git file mode (Regular/Executable/Symlink/GitLink).</param> <param
    /// name="canSymlink">Whether symlinks are supported (from <c>core.symlinks</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task WriteBlobAsync(GitPath path, GitOid blobOid, GitFileMode mode, bool canSymlink, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.Join(_workdirRoot, path.ToFileSystemString());

        switch (mode)
        {
            case GitFileMode.Symlink:
                await WriteSymlinkAsync(fullPath, blobOid, canSymlink, cancellationToken).ConfigureAwait(false);
                break;
            case GitFileMode.GitLink:
                // Record the gitlink in the index but don't create the
                // submodule workdir.
                // Just ensure the directory doesn't exist as a file.
                break;
            default:
                await WriteRegularFileAsync(fullPath, path, blobOid, mode, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task WriteRegularFileAsync(string fullPath, GitPath relPath, GitOid blobOid, GitFileMode mode, CancellationToken cancellationToken)
    {
        // C's mkpath2file (checkout.c:1449-1483) removes any existing
        // entry at the final path when should_remove_existing (case-insensitive
        // filesystem) — otherwise File.WriteAllBytesAsync (O_CREAT|O_TRUNC)
        // follows a pre-existing symlink and writes through it to its target.
        if (_shouldRemoveExisting)
        {
            RemoveExistingEntry(fullPath);
        }

        // Create parent directories.
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _perf = _perf with { MkdirCalls = _perf.MkdirCalls + 1 };
        }

        // Load blob content from ODB.
        GitBlob blob = await _repo.Objects.LookupAsync<GitBlob>(blobOid, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"blob {blobOid} not found", GitErrorCategory.Object);
        using GitBlob _ = blob;
        ReadOnlyMemory<byte> content = blob.Content;

        // Apply smudge filters (ToWorktree) unless disabled. Passes
        // the byte-faithful GitPath to the GitPath overload of LoadAsync (no
        // UTF-8 round-trip on the attribute path).
        if (!_opts.DisableFilters)
        {
            GitFilterList? filters = await GitFilterList.LoadAsync(_repo, relPath, blobOid, GitFilterMode.ToWorktree, GitFilterListFlags.None, attrCommitId: null, cancellationToken).ConfigureAwait(false);
            if (filters is not null)
            {
                byte[] filtered = await filters.ApplyToBufferAsync(content, cancellationToken).ConfigureAwait(false);
                content = filtered;
                filters.Dispose();
            }
        }

        // Write the content to disk.
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken).ConfigureAwait(false);

        // Set the on-disk file mode to match the target git mode. Matches
        // libgit2's blob_content_to_file (checkout.c:1520-1548) which creates
        // the file with the target mode via p_open(path, flags, mode) —
        // REGARDLESS of core.filemode. We chmod after writing because
        // File.WriteAllBytesAsync doesn't take a mode: an executable target
        // always gets the exec bit; when core.filemode is respected, a
        // regular target is normalized to non-exec as well.
        if (!OperatingSystem.IsWindows() && (mode == GitFileMode.Executable || _respectFilemode))
        {
            try
            {
                UnixFileMode newMode = mode == GitFileMode.Executable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                File.SetUnixFileMode(fullPath, newMode);
                _perf = _perf with { ChmodCalls = _perf.ChmodCalls + 1 };
            }
            catch (IOException)
            {
                // chmod failure is non-fatal
            }
        }
    }

    /// <summary>
    /// lstat-style removal of any pre-existing entry at <paramref name="fullPath"/>:
    /// a symlink (including a dangling one — never following it), a directory,
    /// or a regular file. Mirrors the <c>git_futils_rmdir_r(path, NULL,
    /// GIT_RMDIR_REMOVE_FILES)</c> call in mkpath2file (checkout.c:1474-1482).
    /// </summary>
    private static void RemoveExistingEntry(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.LinkTarget is not null)
        {
            // Symlink (incl. dangling) — unlink the link itself, never the
            // target (File.Exists/Directory.Exists would follow or miss it).
            File.Delete(fullPath);
            return;
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
            return;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    /// <summary>
    /// Creates the submodule's workdir directory. Mirrors the
    /// <c>checkout_mkdir</c> call in <c>checkout_submodule</c>
    /// (checkout.c:1676-1686): with <c>MKDIR_REMOVE_EXISTING</c>
    /// (should_remove_existing, checkout.c:1407-1417) any pre-existing entry
    /// is removed first, so gitlinks get a directory and a stat'd index
    /// entry.
    /// </summary>
    internal void MkdirSubmodule(GitPath path, bool removeExisting)
    {
        string fullPath = Path.Join(_workdirRoot, path.ToFileSystemString());
        if (removeExisting && (File.Exists(fullPath) || Directory.Exists(fullPath) || new FileInfo(fullPath).LinkTarget is not null))
        {
            RemoveExistingEntry(fullPath);
        }

        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
            _perf = _perf with { MkdirCalls = _perf.MkdirCalls + 1 };
        }
    }

    /// <summary>
    /// Removes a directory tree, keeping any (sub)directory that cannot be
    /// emptied in place. Ports <c>git_futils_rmdir_r</c> with
    /// <c>GIT_RMDIR_SKIP_NONEMPTY</c> (futils.c:699-770): files are unlinked
    /// (symlinks unlinked, never followed), directories are removed after
    /// their contents, and an entry or directory whose removal fails
    /// (ENOTEMPTY/EBUSY/EACCES — a locked directory) is kept — the
    /// GIT_CHECKOUT_SKIP_LOCKED_DIRECTORIES behavior (checkout.c:1831-
    /// 1832 — attempt removal and skip on failure).
    /// </summary>
    private static void RemoveDirectorySkipNonEmpty(string fullPath)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(fullPath))
        {
            try
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null)
                {
                    // Symlink (incl. dangling) — unlink the link itself.
                    File.Delete(entry);
                }
                else if (Directory.Exists(entry))
                {
                    RemoveDirectorySkipNonEmpty(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cannot remove this entry — keep it (SKIP_NONEMPTY).
            }
        }

        try
        {
            Directory.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ENOTEMPTY/EBUSY/EACCES — locked; keep the directory.
        }
    }

    private async Task WriteSymlinkAsync(string fullPath, GitOid blobOid, bool canSymlink, CancellationToken cancellationToken)
    {
        // Create parent directories.
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _perf = _perf with { MkdirCalls = _perf.MkdirCalls + 1 };
        }

        // Load the symlink target from the blob.
        GitBlob blob = await _repo.Objects.LookupAsync<GitBlob>(blobOid, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"blob {blobOid} not found", GitErrorCategory.Object);
        using GitBlob _ = blob;
        string target = Encoding.UTF8.GetString(blob.Content.Span);

        if (canSymlink && !OperatingSystem.IsWindows())
        {
            // Remove existing file/symlink first (also when
            // should_remove_existing, matching mkpath2file's pre-removal —
            // including dangling symlinks, which File.Exists misses).
            if (_shouldRemoveExisting || File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                RemoveExistingEntry(fullPath);
            }

            // C's
            // blob_content_to_link passes the raw blob bytes to p_symlink
            // (checkout.c:1596-1630) — the UTF-8 string round-trip
            // corrupted non-UTF-8 link targets. Create the link from the
            // raw bytes; fall back to the string API only on failure.
            if (!NativeStat.TryCreateSymbolicLinkRaw(fullPath, blob.Content.Span))
            {
                File.CreateSymbolicLink(fullPath, target);
            }
        }
        else
        {
            // Fake symlink: write a regular file containing the target path.
            // Matches git_futils_fake_symlink (futils.c).
            await File.WriteAllTextAsync(fullPath, target, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes a file or directory from the workdir. Matches
    /// <c>git_futils_rmdir_r</c> (futils.c:855-877) with
    /// <c>GIT_RMDIR_EMPTY_PARENTS | GIT_RMDIR_REMOVE_FILES |
    /// GIT_RMDIR_REMOVE_BLOCKERS</c> as used by checkout_remove_the_old
    /// (checkout.c:1828-1829): when a parent component of the path is a
    /// regular file (e.g. "a/b" blocking "a/b/c"), the blocker is removed
    /// (futils__rm_first_parent, futils.c:746-769).
    /// </summary>
    internal void Remove(GitPath path)
    {
        string fullPath = Path.Join(_workdirRoot, path.ToFileSystemString());
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        else if (Directory.Exists(fullPath))
        {
            // C ORs
            // GIT_RMDIR_SKIP_NONEMPTY into the removal flags when
            // GIT_CHECKOUT_SKIP_LOCKED_DIRECTORIES is set (checkout.c:1831-
            // 1832) — directories that cannot be emptied are left in place
            // (futils__rmdir_recurs_foreach treats ENOTEMPTY as success under
            // SKIP_NONEMPTY).
            if (_skipLockedDirectories)
            {
                RemoveDirectorySkipNonEmpty(fullPath);
            }
            else
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        else
        {
            // The path itself is absent — an ancestor may be a regular file
            // blocking it (futils.c:784-791: lstat fails with ENOTDIR when a
            // parent is a file). Remove the first file ancestor, never above
            // the workdir root.
            StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string? dir = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, _workdirRootBound, cmp))
            {
                if (File.Exists(dir))
                {
                    File.Delete(dir);
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    /// <summary>
    /// Removes empty parent directories after a file deletion.
    /// Matches <c>git_futils_rmdir_r</c> with <c>EMPTY_PARENTS</c>.
    /// </summary>
    internal void PruneEmptyParents(GitPath path)
    {
        string fullPath = Path.Join(_workdirRoot, path.ToFileSystemString());
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir) &&
               !string.Equals(dir, _workdirRootBound, cmp) &&
               Directory.Exists(dir))
        {
            if (Directory.GetFileSystemEntries(dir).Length > 0)
            {
                break; // not empty
            }

            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}

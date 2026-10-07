// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IO;

/// <summary> Reads files from the working directory. Matches <c>workdir_reader</c> in <c>reader.c</c> (lines 85-151). </summary> <remarks> <para> Applies the
/// workdir→ODB (clean) filter pipeline to the on-disk content before hashing, matching git's <c>git_filter_list__apply_to_file</c>. The <see
/// cref="GitFilterList"/> applies real CRLF/ident/custom filters with no bridge
/// changes. </para> <para> When <c>validateIndex</c> is set, the read content's OID and the file's mode are compared
/// against the index entry for <c>path</c>. A mismatch produces <see cref="ReadStatus.Mismatch"/> (C <c>GIT_READER_MISMATCH</c>), signaling the
/// apply bridge that the workdir has been modified from the index (apply.c:527-528). </para> </remarks>
internal sealed class WorkdirReader : IObjectReader
{
    private readonly GitRepository _repo;
    private readonly bool _validateIndex;
    private GitIndex? _index;
    private bool _indexLoaded;

    /// <summary>
    /// Creates a workdir reader.
    /// </summary>
    /// <param name="repo">The repository whose workdir to read.</param>
    /// <param name="validateIndex">
    /// If <c>true</c>, reads validate the workdir content against the
    /// repository index (mode + OID) and signal
    /// <see cref="ReadStatus.Mismatch"/> on divergence. Matches
    /// <c>git_reader_for_workdir(repo, validate_index)</c> (reader.c:153-178).
    /// </param>
    public WorkdirReader(GitRepository repo, bool validateIndex)
    {
        _repo = repo;
        _validateIndex = validateIndex;
    }

    private async ValueTask<GitIndex?> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (!_indexLoaded)
        {
            _indexLoaded = true;
            _index = _validateIndex ? await _repo.GetIndexAsync(cancellationToken).ConfigureAwait(false) : null;
        }

        return _index;
    }

    /// <inheritdoc/>
    public async Task<ReaderReadResult> ReadAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        if (_repo.Workdir is null)
        {
            return new ReaderReadResult(ReadStatus.NotFound, null);
        }

        // FS boundary: single transcode point at the Path.Join.
        string fullPath = Path.Join(_repo.Workdir, path.ToFileSystemString());
        if (!File.Exists(fullPath))
        {
            return new ReaderReadResult(ReadStatus.NotFound, null);
        }

        byte[] rawContent = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        GitFileMode filemode = GetFileMode(fullPath);

        // Apply workdir→ODB (clean) filters. The real FilterList.Load
        // resolves attributes and builds the filter chain. When no filters
        // apply (no text/crlf/eol/ident/filter attrs set), Load returns null
        // and the content passes through unchanged. Passes the
        // byte-faithful GitPath to the GitPath overload of LoadAsync (no
        // UTF-8 round-trip on the attribute path).
        GitFilterList? filters = await GitFilterList.LoadAsync(_repo, path, null, GitFilterMode.ToOdb, GitFilterListFlags.None, attrCommitId: null, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> content = filters is not null
            ? await filters.ApplyToBufferAsync(rawContent, cancellationToken).ConfigureAwait(false)
            : rawContent;

        // Compute the OID of the (filtered) content.
        var oid = GitOid.ComputeOid(GitObjectType.Blob, content.Span, _repo.ObjectFormat);

        // Index validation: compare the workdir content's OID and mode
        // against the index entry for this path. A mismatch signals the
        // apply bridge that the workdir has diverged from the index.
        // uses the GitPath overload of EntryByPath directly (no string
        // round-trip).
        GitIndex? index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is not null)
        {
            GitIndexEntry? idxEntry = index.EntryByPath(path, stage: 0);
            if (idxEntry is null ||
                filemode != idxEntry.Value.Mode ||
                !oid.Equals(idxEntry.Value.Id))
            {
                return new ReaderReadResult(ReadStatus.Mismatch, null);
            }
        }

        return new ReaderReadResult(ReadStatus.Found, new ReaderResult(content, oid, filemode));
    }

    private static GitFileMode GetFileMode(string fullPath)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(fullPath);

            // a symlink
            // must map to GIT_FILEMODE_LINK (0120000) like C's p_lstat +
            // git_futils_canonical_mode (workdir_reader_read, reader.c). The
            // ReparsePoint bit is tested BEFORE the Directory bit — on .NET
            // a symlink-to-directory reports both, and a symlink-to-file
            // would otherwise fall through to the executable-bit test
            // (symlink mode 0777 has UserExecute → wrong Executable mode).
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return GitFileMode.Symlink;
            }

            if ((attrs & FileAttributes.Directory) != 0)
            {
                return GitFileMode.Tree;
            }

            // Check executable bit on Unix.
            if (!OperatingSystem.IsWindows())
            {
                var info = new FileInfo(fullPath);
                if ((info.UnixFileMode & UnixFileMode.UserExecute) != 0)
                {
                    return GitFileMode.Executable;
                }
            }

            return GitFileMode.Regular;
        }
        catch (IOException)
        {
            return GitFileMode.Regular;
        }
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;
using SysFile = System.IO.File;

namespace LibGit2CS.Diff;

/// <summary>
/// Per-side lazy content wrapper for a <see cref="GitDiffFile"/>. Loads blob,
/// workdir file, symlink target, or submodule-commit placeholder on demand.
/// Managed port of <c>src/libgit2/diff_file.c</c> +
/// <c>git_diff__oid_for_entry</c> (diff_generate.c:628–724).
/// </summary>
/// <remarks>
/// Consumed by <c>PatchGenerator</c> (<c>patch->ofile</c>/<c>nfile</c>), NOT
/// by the <c>Diff</c> facade. Has zero coupling to <c>Diff</c>. The two-phase
/// split is preserved: delta generation does NOT load content;
/// content loads only when a <c>Patch</c> is materialized.
/// </remarks>
internal sealed class DiffFileContent
{
    private const long DefaultMaxFileSize = 0x20000000; // 512 MB — DIFF_MAX_FILESIZE

    private const GitDiffFileFlags KnownBinary =
        GitDiffFileFlags.Binary | GitDiffFileFlags.NotBinary;

    public required GitRepository? Repo { get; init; }
    public required GitDiffFile File { get; init; }
    public required DiffDriver Driver { get; init; }
    public DiffInternalFlags Flags { get; set; }
    public GitDiffOptionsFlags OptsFlags { get; set; }
    public long OptsMaxSize { get; set; }
    public IteratorType Src { get; set; }
    public ReadOnlyMemory<byte> Data { get; set; }

    /// <summary>
    /// Initializes from a diff delta's file. Matches
    /// <c>git_diff_file_content__init_from_diff</c> (diff_file.c:92-132) +
    /// <c>diff_file_contentinit_common</c> (diff_file.c:44-90).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="file">The delta's old or new file.</param>
    /// <param name="src">Iterator source (Tree or Workdir).</param>
    /// <param name="opts">Diff options.</param>
    /// <param name="driver">The diff driver for this path.</param>
    /// <param name="hasData">Whether this side has content to load (based on delta status).</param>
    public static DiffFileContent FromDiff(
        GitRepository repo, GitDiffFile file, IteratorType src,
        GitDiffOptions opts, DiffDriver driver, bool hasData)
    {
        var fc = new DiffFileContent
        {
            Repo = repo,
            File = file,
            Src = src == IteratorType.Empty ? IteratorType.Tree : src,
            Driver = driver,
        };

        if (!hasData)
        {
            fc.Flags |= DiffInternalFlags.NoData;
        }

        fc.InitCommon(opts);
        return fc;
    }

    /// <summary>
    /// Initializes from a blob. Matches the blob path in
    /// <c>git_diff_file_content__init_from_src</c> (diff_file.c:153-162).
    /// </summary>
    public static DiffFileContent FromBlob(
        GitRepository repo, GitBlob? blob, GitDiffOptions opts, DiffDriver driver, string path)
    {
        var file = new GitDiffFile { Path = GitPath.FromUtf8String(path), Mode = GitFileMode.Regular };

        if (blob is null)
        {
            return FromDiff(repo, file, IteratorType.Tree, opts, driver, hasData: false);
        }

        file.Size = blob.Size;
        file.Id = blob.Id;
        file.IdAbbrev = blob.Id.HexLength;
        file.Flags = GitDiffFileFlags.ValidId;

        var fc = new DiffFileContent
        {
            Repo = repo,
            File = file,
            Src = IteratorType.Tree,
            Driver = driver,
            Flags = DiffInternalFlags.Loaded,
            Data = blob.Content,
        };

        fc.InitCommon(opts);
        return fc;
    }

    /// <summary>
    /// Initializes from a raw buffer. Matches the buffer path in
    /// <c>git_diff_file_content__init_from_src</c> (diff_file.c:163-172).
    /// The repo may be null for bare buffer diffs (git_diff_buffers).
    /// </summary>
    public static DiffFileContent FromBuffer(
        GitRepository? repo, ReadOnlyMemory<byte> buffer, GitDiffOptions opts,
        DiffDriver driver, string path)
    {
        var file = new GitDiffFile
        {
            Path = GitPath.FromUtf8String(path),
            Mode = GitFileMode.Regular,
            Size = buffer.Length,
            Flags = GitDiffFileFlags.ValidId,
            Id = GitObjectDb.HashObject(GitObjectType.Blob, buffer.Span, repo?.ObjectFormat ?? GitHashAlgorithmKind.Sha1)
        };
        file.IdAbbrev = file.Id.HexLength;

        var fc = new DiffFileContent
        {
            Repo = repo,
            File = file,
            Src = IteratorType.Tree,
            Driver = driver,
            Flags = DiffInternalFlags.Loaded,
            Data = buffer,
        };

        fc.InitCommon(opts);
        return fc;
    }

    /// <summary>
    /// Common initialization: copy opts, apply driver flags, binary-by-size.
    /// Matches <c>diff_file_contentinit_common</c> (diff_file.c:44-90).
    /// </summary>
    private void InitCommon(GitDiffOptions opts)
    {
        OptsFlags = opts.Flags;
        OptsMaxSize = opts.MaxSize > 0 ? opts.MaxSize : DefaultMaxFileSize;

        // Give driver a chance to modify options (force text/binary).
        GitDiffOptionsFlags flags = OptsFlags;
        Driver.UpdateOptions(ref flags);
        OptsFlags = flags;

        // Size overflow check.
        if ((ulong)File.Size != (ulong)File.Size)
        {
            File.Flags |= GitDiffFileFlags.Binary;
        }
        else if ((OptsFlags & GitDiffOptionsFlags.ForceText) != 0)
        {
            File.Flags &= ~GitDiffFileFlags.Binary;
            File.Flags |= GitDiffFileFlags.NotBinary;
        }
        else if ((OptsFlags & GitDiffOptionsFlags.ForceBinary) != 0)
        {
            File.Flags &= ~GitDiffFileFlags.NotBinary;
            File.Flags |= GitDiffFileFlags.Binary;
        }

        BinaryBySize();

        if ((Flags & DiffInternalFlags.NoData) != 0)
        {
            Flags |= DiffInternalFlags.Loaded;
            Data = ReadOnlyMemory<byte>.Empty;
        }

        if ((Flags & DiffInternalFlags.Loaded) != 0)
        {
            BinaryByContent();
        }
    }

    /// <summary>
    /// Loads the content if not already loaded. Matches
    /// <c>git_diff_file_content__load</c> (diff_file.c:431-456).
    /// </summary>
    public async Task LoadAsync(GitDiffOptions opts, CancellationToken cancellationToken)
    {
        if ((Flags & DiffInternalFlags.Loaded) != 0)
        {
            return;
        }

        // Skip binary files unless SHOW_BINARY is set.
        if ((File.Flags & GitDiffFileFlags.Binary) != 0 &&
            (opts.Flags & GitDiffOptionsFlags.ShowBinary) == 0)
        {
            return;
        }

        if (Src == IteratorType.Workdir)
        {
            await LoadWorkdirAsync(opts, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await LoadBlobAsync(opts, cancellationToken).ConfigureAwait(false);
        }

        Flags |= DiffInternalFlags.Loaded;
        BinaryByContent();
    }

    /// <summary>
    /// Releases loaded content. Matches <c>git_diff_file_content__unload</c>
    /// (diff_file.c:458-483). In managed code, the GC reclaims memory, so
    /// this just clears the references and the LOADED flag.
    /// </summary>
    public void Unload()
    {
        if ((Flags & DiffInternalFlags.Loaded) == 0)
        {
            return;
        }

        Data = ReadOnlyMemory<byte>.Empty;
        Flags &= ~DiffInternalFlags.Loaded;
    }

    /// <summary>Unloads and releases resources. Matches <c>git_diff_file_content__clear</c>.</summary>
    public void Clear() => Unload();

    // ━━ Binary detection ━━

    /// <summary>
    /// Marks the file binary if its size exceeds <see cref="OptsMaxSize"/>.
    /// Matches <c>diff_file_content_binary_by_size</c> (diff_file.c:20-29).
    /// </summary>
    private bool BinaryBySize()
    {
        if ((File.Flags & KnownBinary) == 0 && OptsMaxSize > 0 && File.Size > OptsMaxSize)
        {
            File.Flags |= GitDiffFileFlags.Binary;
        }

        return (File.Flags & GitDiffFileFlags.Binary) != 0;
    }

    /// <summary>
    /// Scans loaded content for NUL bytes. Matches
    /// <c>diff_file_content_binary_by_content</c> (diff_file.c:31-42).
    /// </summary>
    private void BinaryByContent()
    {
        if ((File.Flags & KnownBinary) != 0)
        {
            return;
        }

        if (DiffDriver.ContentIsBinary(Data.Span))
        {
            File.Flags |= GitDiffFileFlags.Binary;
        }
        else
        {
            File.Flags |= GitDiffFileFlags.NotBinary;
        }
    }

    // ━━ Blob loading (TREE source) ━━

    /// <summary>
    /// Loads content from the ODB. Matches <c>diff_file_content_load_blob</c>
    /// (diff_file.c:231-271).
    /// </summary>
    private async Task LoadBlobAsync(GitDiffOptions opts, CancellationToken cancellationToken)
    {
        if (File.Id.IsZero)
        {
            return;
        }

        // GitLink (submodule) — generate placeholder string.
        if (File.Mode == GitFileMode.GitLink)
        {
            await LoadSubmodulePlaceholderAsync(checkStatus: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Blob loading requires a repository (Tree source). Buffer-sourced
        // content sets DiffInternalFlags.Loaded and never reaches here.
        GitRepository repo = Repo ?? throw new GitException(GitErrorCode.Invalid, "diff content has no owning repository", GitErrorCategory.Repository);

        // Resolve zero-size: peek at object header.
        if (File.Size == 0 && (File.Flags & GitDiffFileFlags.ValidSize) == 0)
        {
            GitBlob? blob = await repo.Objects.LookupAsync<GitBlob>(File.Id, cancellationToken).ConfigureAwait(false);
            if (blob is not null)
            {
                File.Size = blob.Size;
                File.Flags |= GitDiffFileFlags.ValidSize;
            }
        }

        // Skip binary if SHOW_BINARY not set.
        if ((opts.Flags & GitDiffOptionsFlags.ShowBinary) == 0 && BinaryBySize())
        {
            return;
        }

        GitBlob? loaded = await repo.Objects.LookupAsync<GitBlob>(File.Id, cancellationToken).ConfigureAwait(false);
        if (loaded is not null)
        {
            Data = loaded.Content;
        }
    }

    // ━━ Workdir loading (WORKDIR source) ━━

    /// <summary>
    /// Loads content from the workdir. Matches
    /// <c>diff_file_content_load_workdir</c> (diff_file.c:398-429).
    /// </summary>
    private async Task LoadWorkdirAsync(GitDiffOptions opts, CancellationToken cancellationToken)
    {
        // Workdir source requires a repository.
        GitRepository repo = Repo ?? throw new GitException(GitErrorCode.Invalid, "workdir diff source has no owning repository", GitErrorCategory.Repository);

        // GitLink (submodule) — generate placeholder string.
        if (File.Mode == GitFileMode.GitLink)
        {
            await LoadSubmodulePlaceholderAsync(checkStatus: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Tree entries have no content.
        if (File.Mode == GitFileMode.Tree)
        {
            return;
        }

        // GitPath->filesystem transcode is the single-point hook
        // (ToFileSystemString — no precompose on egress; macOS auto-converts
        // NFC->NFD on write, matching libgit2's no-egress-iconv design).
        string fullPath = Path.Join(
            repo.Workdir ?? string.Empty,
            (File.Path?.ToFileSystemString()) ?? string.Empty);

        if (File.Mode == GitFileMode.Symlink)
        {
            LoadWorkdirSymlink(fullPath);
        }
        else
        {
            await LoadWorkdirFileAsync(repo, fullPath, opts, cancellationToken).ConfigureAwait(false);
        }

        // Update OID if not previously known.
        if ((File.Flags & GitDiffFileFlags.ValidId) == 0)
        {
            File.Id = GitObjectDb.HashObject(GitObjectType.Blob, Data.Span, repo.ObjectFormat);
            File.IdAbbrev = File.Id.HexLength;
            File.Flags |= GitDiffFileFlags.ValidId;
        }
    }

    /// <summary>
    /// Loads a regular workdir file. Matches
    /// <c>diff_file_content_load_workdir_file</c> (diff_file.c:323-396).
    /// The raw bytes are run through the clean (<c>GIT_FILTER_TO_ODB</c>)
    /// filter list before being stored on <see cref="Data"/> so workdir
    /// content is normalized (e.g. CRLF&#8594;LF) before diffing against the
    /// ODB/blob side. When no filters apply (fast path), the raw bytes are
    /// returned unchanged — equivalent to upstream's <c>fl == NULL</c> mmap
    /// branch (diff_file.c:368-377).
    /// </summary>
    private async Task LoadWorkdirFileAsync(GitRepository repo, string fullPath, GitDiffOptions opts, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            return;
        }

        long fileSize = fileInfo.Length;

        if ((File.Flags & GitDiffFileFlags.ValidSize) != 0)
        {
            // C (diff_file.c:342-349): when the size is already valid, re-stat
            // and error if the on-disk size differs — the file changed between
            // delta generation and content load.
            if (File.Size != fileSize)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "file changed before we could read it",
                    GitErrorCategory.Filesystem);
            }
        }
        else
        {
            File.Size = fileSize;
            File.Flags |= GitDiffFileFlags.ValidSize;
        }

        // Empty file — no content to load.
        if (fileSize == 0)
        {
            Data = ReadOnlyMemory<byte>.Empty;
            return;
        }

        // Skip binary if SHOW_BINARY not set.
        if ((opts.Flags & GitDiffOptionsFlags.ShowBinary) == 0 && BinaryBySize())
        {
            return;
        }

        // Read the raw workdir bytes, then run them through the clean
        // (GIT_FILTER_TO_ODB) filter list so the content is normalized (e.g.
        // CRLF→LF for `text`/`eol`-attributed files) before diffing against
        // the ODB/blob side. This mirrors diff_file_content_load_workdir_file
        // (diff_file.c:362-389): git_filter_list_load + git_filter_list__convert_buf.
        // The fast path (no filters apply) returns the raw bytes unchanged.
        byte[] rawContent = await SysFile.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        Data = await GitFilterList.ApplyCleanAsync(
            repo, File.Path ?? default, rawContent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a symlink target. Matches
    /// <c>diff_file_content_load_workdir_symlink</c> (diff_file.c:290-321).
    /// </summary>
    private void LoadWorkdirSymlink(string fullPath)
    {
        // C's
        // diff_file_content_load_workdir_symlink errors with "failed to read
        // symlink" when readlink fails (diff_file.c:313-317) — a symlink
        // replaced/removed between delta generation and content load must
        // error, not silently hash empty content.
        string? target = new FileInfo(fullPath).LinkTarget;
        if (target is null)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to read symlink '{File.Path}'",
                GitErrorCategory.Os);
        }

        Data = Encoding.UTF8.GetBytes(target);
    }

    /// <summary>
    /// Generates the "Subproject commit &lt;oid&gt;\n" placeholder string for
    /// gitlinks. Matches <c>diff_file_content_commit_to_str</c>
    /// (diff_file.c:178-229). Appends <c>-dirty</c> when the submodule has
    /// workdir changes.
    /// </summary>
    private async Task LoadSubmodulePlaceholderAsync(bool checkStatus, CancellationToken cancellationToken)
    {
        GitRepository repo = Repo ?? throw new GitException(GitErrorCode.Invalid, "submodule placeholder requires an owning repository", GitErrorCategory.Repository);

        string oidHex = File.Id.IsZero
            ? new string('0', repo.ObjectFormat == GitHashAlgorithmKind.Sha256 ? 64 : 40)
            : File.Id.ToString();

        string placeholder = $"Subproject commit {oidHex}";

        // C (diff_file.c:191-215, diff_file_content_commit_to_str): git_submodule_status with GIT_SUBMODULE_IGNORE_UNSPECIFIED — the submodule's CONFIGURED
        // ignore policy is honored (a raw index→workdir diff reported untracked-only dirt too), and the check runs even when the gitlink OID is not yet valid.
        // IS_WD_DIRTY = WD_INDEX_MODIFIED | WD_WD_MODIFIED | WD_UNTRACKED (submodule.h:112-115).
        if (checkStatus && repo.Workdir is not null)
        {
            try
            {
                string smPath = (File.Path?.ToFileSystemString()) ?? string.Empty;
                SubmoduleStatus status = await GitSubmodule.StatusAsync(repo, smPath, SubmoduleIgnore.Unspecified, cancellationToken).ConfigureAwait(false);
                if ((status & (SubmoduleStatus.WdIndexModified | SubmoduleStatus.WdWdModified | SubmoduleStatus.WdUntracked)) != 0)
                {
                    placeholder += "-dirty";
                }
            }
            catch (GitException)
            {
                // Lookup/status failure (e.g. not-yet-added gitlink) — no
                // -dirty suffix (C clears GIT_EEXISTS here).
            }
        }

        Data = Encoding.UTF8.GetBytes(placeholder + "\n");
    }

    // ━━ Workdir OID computation (for DiffGenerator + CheckoutContext) ━━

    /// <summary>
    /// Computes the blob OID for a workdir file given its absolute path and
    /// git mode. The pure content-hash portion of
    /// <c>git_diff__oid_for_entry</c> (diff_generate.c:628-724): branches on
    /// mode — gitlink → submodule HEAD OID, symlink → hash of the link-target
    /// string (<c>git_odb__hashlink</c>, odb.c:302-348), regular file → read +
    /// clean-filter (<c>GIT_FILTER_TO_ODB</c>) + hash. No index refresh is
    /// performed; callers that need stat-cache refresh
    /// (<see cref="ComputeWorkdirOidAsync"/>) wrap this.
    /// </summary>
    /// <param name="repo">The repository (for filter list, submodule lookup, hash algorithm).</param>
    /// <param name="path">The workdir-relative path (byte-faithful GitPath; used for filter attribute lookup).</param>
    /// <param name="fullPath">The OS-native absolute path to the workdir entry.</param>
    /// <param name="mode">The git file mode (Regular, Executable, Symlink, GitLink).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The blob OID, or zero OID for gitlinks / missing files.</returns>
    internal static async Task<GitOid> HashWorkdirEntryAsync(
        GitRepository repo, GitPath path, string fullPath, GitFileMode mode,
        CancellationToken cancellationToken)
    {
        GitHashAlgorithmKind algorithm = repo.ObjectFormat;

        if (mode == GitFileMode.GitLink)
        {
            // Submodule OID: read the submodule's HEAD commit OID.
            // Matches diff_generate.c:665-678 (S_ISGITLINK branch).
            return await ReadSubmoduleHeadOidAsync(repo, path, cancellationToken).ConfigureAwait(false);
        }

        if (mode == GitFileMode.Symlink)
        {
            // Hash the symlink target as a blob — matches git_odb__hashlink
            // (odb.c:302-348): lstat to size the buffer, readlink to read the
            // target string, then hash as a blob. Returns zero OID if the path
            // is not a symlink.
            // the raw link bytes are read via readlink — FileInfo.LinkTarget
            // decodes as UTF-8, corrupting non-UTF-8 targets.
            if (!NativeStat.TryReadLinkTarget(fullPath, out byte[] targetBytes))
            {
                return default;
            }

            return GitObjectDb.HashObject(GitObjectType.Blob, targetBytes, algorithm);
        }

        // Regular file: read, apply clean filter, hash as blob.
        // Matches diff_generate.c:683-705 (regular-file branch).
        if (!SysFile.Exists(fullPath))
        {
            return default;
        }

        byte[] rawContent = await SysFile.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        // Apply clean filter (GIT_FILTER_TO_ODB) before hashing.
        // Passes the byte-faithful GitPath to the GitPath overload of
        // ApplyCleanAsync (no UTF-8 round-trip on the attribute path).
        ReadOnlyMemory<byte> filteredContent = await GitFilterList.ApplyCleanAsync(
            repo, path, rawContent, cancellationToken).ConfigureAwait(false);

        return GitObjectDb.HashObject(GitObjectType.Blob, filteredContent.Span, algorithm);
    }

    /// <summary> Computes the blob OID for a workdir file. Matches <c>git_diff__oid_for_entry</c> (diff_generate.c:628-724). Used by <c>DiffGenerator</c>
    /// during delta generation for stat-based change detection. Delegates the content hash to <see cref="HashWorkdirEntryAsync"/> and adds the optional
    /// stat-cache refresh tail (diff_generate.c:706-720) when <paramref name="updateMatch"/> matches. </summary> <param name="repo">The repository.</param>
    /// <param name="path">The workdir-relative path (byte-faithful GitPath; transcode to OS encoding happens once at the <c>Path.Join</c> boundary, matching
    /// libgit2's single FS transcode point — the byte-domain contract).</param> <param name="mode">The file mode (Regular, Symlink, GitLink).</param> <param
    /// name="updateMatch"> If non-null and the computed OID matches this value, the refreshed entry is written to the index (stat cache refresh). Only
    /// meaningful when <see cref="GitDiffOptionsFlags.UpdateIndex"/> is set. </param> <param name="gen"> The owning diff generator, or null. When non-null, its
    /// <see cref="DiffGenerator._indexUpdated"/> flag is set if the index was refreshed. </param> <returns>The blob OID, or zero OID for
    /// gitlinks/submodules.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<GitOid> ComputeWorkdirOidAsync(
        GitRepository repo, GitPath path, GitFileMode mode,
        GitOid? updateMatch = null, DiffGenerator? gen = null,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.Join(repo.Workdir ?? string.Empty, path.ToFileSystemString());

        GitOid oid = await HashWorkdirEntryAsync(repo, path, fullPath, mode, cancellationToken).ConfigureAwait(false);

        // Update index if requested and OID matches (diff_generate.c:706-720).
        // Reachable for all mode branches (gitlink/symlink/regular) — the C
        // flow has no early return before this tail; the refresh condition
        // gates on the OID matching update_match (= old file's OID).
        if (updateMatch is { } match && oid == match && gen is not null)
        {
            var fi = new FileInfo(fullPath);
            (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size) = StatUtil.GetStatInfoForIndex(fi); // C index-write path stores st_rdev (index.c:909)
            GitFileMode fileMode = StatUtil.GetFileMode(fi);

            var refreshed = new GitIndexEntry
            {
                Path = path,
                Id = oid,
                Mode = fileMode,
                Ctime = ctime,
                Mtime = mtime,
                Dev = dev,
                Ino = ino,
                Uid = uid,
                Gid = gid,
                FileSize = size,
                Flags = (ushort)Math.Min(path.Length, GitIndexEntry.NameMask),
            };

            GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
            index.Add(refreshed);
            gen._indexUpdated = true;
        }

        return oid;
    }

    /// <summary>
    /// Reads the submodule's HEAD commit OID (zero when unavailable).
    /// </summary>
    private static async Task<GitOid> ReadSubmoduleHeadOidAsync(GitRepository repo, GitPath path, CancellationToken cancellationToken)
    {
        string smPath = Path.Join(repo.Workdir ?? string.Empty, path.ToFileSystemString());
        string gitPath = Path.Join(smPath, ".git");

        if (!SysFile.Exists(gitPath) && !Directory.Exists(gitPath))
        {
            return default;
        }

        try
        {
            GitRepository smRepo = await GitRepository.OpenAsync(smPath, repo.Context, cancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable smRepoDisposable = smRepo.ConfigureAwait(false);
            GitReference? head = await smRepo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            return head is GitDirectReference dr ? dr.Target : default;
        }
        catch (GitException)
        {
            return default;
        }
    }
}

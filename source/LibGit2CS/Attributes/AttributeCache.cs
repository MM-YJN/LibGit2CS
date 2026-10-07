// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.Attributes;

/// <summary>
/// Per-repository attribute cache. Managed port of
/// <c>src/libgit2/attrcache.c</c> — holds the macro registry and cached
/// <c>.gitattributes</c> files from all sources (info, workdir, index,
/// global, system). Lazily initialized on first access via
/// <see cref="GitRepository.GetAttributeCacheAsync"/>.
/// </summary>
/// <remarks>
/// Reads the system/global/info/workdir/index/HEAD/commit sources. The macro registry includes the
/// built-in <c>binary</c> macro (<c>-diff -merge -text -crlf</c>) and any
/// runtime-registered macros via <see cref="AddMacro"/>.
/// </remarks>
internal sealed class AttributeCache
{
    private readonly GitRepository _repo;
    private readonly bool _ignoreCase;
    private AttributesFile? _systemFile;
    private AttributesFile? _globalFile;
    private AttributesFile? _infoFile;

    /// <summary> Cached per-directory attribute files, keyed by (relative dir, source). Matches the C attr cache (attrcache.c) file memoization. The dir key is
    /// a byte-faithful <see cref="GitPath"/> — C memoizes the raw dir bytes, and a string key would collapse distinct non-UTF-8 dirs onto one U+FFFD decode.
    /// </summary>
    private readonly Dictionary<(GitPath Dir, AttrSource Source), AttributesFile?> _dirFiles = new();

    /// <summary>
    /// The macro registry, including the built-in <c>binary</c> macro.
    /// Matches <c>git_attr_cache.macros</c> (attrcache.c:28).
    /// </summary>
    public AttrMacroRegistry Macros { get; }

    private AttributeCache(GitRepository repo, bool ignoreCase)
    {
        _repo = repo;
        _ignoreCase = ignoreCase;
        Macros = new AttrMacroRegistry();
    }

    /// <summary>
    /// Creates the attribute cache and loads all attr file sources for the
    /// repository. Matches <c>git_attr_cache__init</c> (attrcache.c:401-452)
    /// + <c>collect_attr_files</c> (attr.c:630-717).
    /// </summary>
    public static async ValueTask<AttributeCache> CreateAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        // GIT_IGNORECASE_DEFAULT = GIT_CONFIGMAP_FALSE (repository.h:96-97):
        // when core.ignorecase is unset, patterns are case-sensitive on every
        // platform. C (attr_file.c:381-384, git_attr_file__parse_buffer):
        // an unparseable value FAILS the per-file parse (goto out), so it
        // propagates out of the attr cache init.
        bool ignoreCase = await repo.Config.GetConfigmapBoolOrThrowAsync(
            "core.ignorecase",
            defaultValue: false,
            cancellationToken).ConfigureAwait(false);
        return new AttributeCache(repo, ignoreCase);
    }

    /// <summary>
    /// Registers a runtime macro. Matches <c>git_attr_add_macro</c>
    /// (attr.c:464-501).
    /// </summary>
    public void AddMacro(string name, string values)
    {
        Macros.AddMacro(name, values.AsSpan());
    }

    /// <summary>
    /// Flushes the cache, clearing all loaded files and re-registering the
    /// built-in macros. Matches <c>git_attr_cache_flush</c>
    /// (attrcache.c:454-465).
    /// </summary>
    public void Flush()
    {
        _systemFile = null;
        _globalFile = null;
        _infoFile = null;
        _dirFiles.Clear();
    }

    /// <summary>
    /// Looks up a single attribute for a path. Matches
    /// <c>git_attr_get_with_session</c> (attr.c:60-98): collects the
    /// attribute files for the path honoring <paramref name="flags"/>, then
    /// scans them highest-precedence-first.
    /// </summary>
    public async ValueTask<GitAttrValue> LookupOneAsync(AttrPath path, string attrName, GitAttrCheckFlags flags = GitAttrCheckFlags.FileThenIndex, CancellationToken cancellationToken = default)
    {
        GitAttrValue[] values = await LookupManyAsync(path, [attrName], flags, cancellationToken).ConfigureAwait(false);
        return values[0];
    }

    /// <summary>
    /// Looks up multiple attributes for a path. Matches
    /// <c>git_attr_get_many_with_session</c> (attr.c:122-154).
    /// </summary>
    public async ValueTask<GitAttrValue[]> LookupManyAsync(AttrPath path, IReadOnlyList<string> attrNames, GitAttrCheckFlags flags = GitAttrCheckFlags.FileThenIndex, CancellationToken cancellationToken = default)
    {
        List<AttributesFile> files = await CollectFilesAsync(path, flags, cancellationToken).ConfigureAwait(false);
        return LookupManyIn(files, path, attrNames);
    }

    /// <summary>
    /// Collects the attribute files for a path, ordered lowest → highest
    /// precedence. Exact port of <c>collect_attr_files</c> (attr.c:630-717):
    /// system (unless NO_SYSTEM), global (<c>core.attributesfile</c>), the
    /// per-directory walk from the path's dir up to the workdir root, then
    /// <c>.git/info/attributes</c>.
    /// </summary>
    private async ValueTask<List<AttributesFile>> CollectFilesAsync(AttrPath path, GitAttrCheckFlags flags, CancellationToken cancellationToken)
    {
        var files = new List<AttributesFile>();

        // Load order follows C's attr_setup preload (attr.c:378-462) —
        // system → global → info → workdir root → index — because macro
        // definitions are registered as files are parsed. The lookup list
        // below stays lowest → highest precedence.

        // 1. System file (attr.c:701-708) — skipped under NO_SYSTEM. Bare
        //    repositories load it too; bare-ness only affects the per-path
        //    dir flag (attr.c:69-70).
        if ((flags & GitAttrCheckFlags.NoSystem) == 0)
        {
            if (_systemFile is { } sf && IsPathFileStale(sf))
            {
                _systemFile = null;
            }

            _systemFile ??= await AttributesFile.LoadFromSystemAsync(_repo.Context.Dirs, Macros, _ignoreCase, cancellationToken).ConfigureAwait(false);
            if (_systemFile is not null)
            {
                files.Add(_systemFile);
            }
        }

        // 2. Global file (core.attributesfile, with the XDG fallback;
        //    attrcache.c:322-353) (attr.c:691-697).
        if (_globalFile is { } gf && IsPathFileStale(gf))
        {
            _globalFile = null;
        }

        _globalFile ??= await AttributesFile.LoadFromGlobalAsync(_repo, Macros, _ignoreCase, cancellationToken).ConfigureAwait(false);
        if (_globalFile is not null)
        {
            files.Add(_globalFile);
        }

        // 3. .git/info/attributes — parsed BEFORE the workdir files so its
        //    macros are available to them (attr.c:418-424), but appended
        //    LAST in the lookup list (highest precedence, attr.c:664-672).
        if (_infoFile is { } inf && IsPathFileStale(inf))
        {
            _infoFile = null;
        }

        _infoFile ??= await AttributesFile.LoadFromInfoAsync(_repo, Macros, _ignoreCase, cancellationToken).ConfigureAwait(false);
        AttributesFile? infoFile = _infoFile;

        // 4. Per-directory walk (attr.c:650-688): C walks up from the path's directory — the workdir-rooted dir in non-bare repos, the bare path's dirname
        // otherwise (git_fs_path_dirname_r) — pushing each level's sources. The walk runs UNCONDITIONALLY: in a bare repo only the FILE source is skipped
        // (attr_decide_sources gates FILE on has_wd); the INDEX source still contributes.
        bool hasWorkdir = _repo.Workdir is not null;
        foreach (GitPath dir in DirChain(path))
        {
            // C (attr.c:596-597): allow_macros = (workdir == path) — only the
            //    ROOT level of a NON-BARE repo may define macros (bare:
            //    workdir is NULL → false).
            bool allowMacros = hasWorkdir && dir.IsEmpty;
            foreach (AttrSource src in SourcesFor(flags, hasWorkdir))
            {
                AttributesFile? f = await LoadDirFileAsync(dir, src, allowMacros, cancellationToken).ConfigureAwait(false);
                if (f is not null)
                {
                    files.Add(f);
                }
            }
        }

        if (infoFile is not null)
        {
            files.Add(infoFile);
        }

        return files;
    }

    /// <summary>
    /// Revalidates a memoized FILE-source file against its on-disk stamp.
    /// Matches the FILE branch of <c>git_attr_file__out_of_date</c>
    /// (attrcache.c:284-286, git_futils_filestamp_check): a missing file is
    /// stale; otherwise the (length, mtime) stamp must match.
    /// </summary>
    private static bool IsPathFileStale(AttributesFile file)
    {
        if (file.SourcePath is not { } path)
        {
            return false; // not a FILE source
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return true;
        }

        return file.FileStamp is not { } stamp
            || stamp.Length != info.Length
            || stamp.MtimeUtc != info.LastWriteTimeUtc;
    }

    /// <summary> The ancestor directories of the path's directory, ROOT FIRST (the C# lowest-precedence-first order). For <c>sub/deep/file.txt</c>: <c>["",
    /// "sub", "sub/deep"]</c>. byte-domain — C walks the raw path bytes (<c>git_fs_path_dirname_r</c>, attr.c:650-688); a decode→re-encode round-trip corrupted
    /// non-UTF-8 paths in the per-directory attribute lookup. </summary>
    private static List<GitPath> DirChain(AttrPath path)
    {
        ReadOnlySpan<byte> p = path.Path.Span;
        int slash = p.LastIndexOf((byte)'/');
        GitPath dir = slash < 0 ? default : path.Path.Slice(0, slash);

        var chain = new List<GitPath>();
        while (!dir.IsEmpty)
        {
            chain.Add(dir);
            int idx = dir.Span.LastIndexOf((byte)'/');
            dir = idx < 0 ? default : dir.Slice(0, idx);
        }

        chain.Reverse();
        chain.Insert(0, default);
        return chain;
    }

    /// <summary> The per-level sources, LOWEST → HIGHEST precedence (the lookup list is scanned from the end). Equivalent to <c>attr_decide_sources</c>
    /// (attr.c:512-546): FILE/INDEX per the low two flag bits (FILE gated on <paramref name="hasWorkdir"/> — bare repos have no FILE source), then HEAD, then
    /// COMMIT. The invalid low-bit value 3 adds NO sources (C has no default case). </summary>
    private static List<AttrSource> SourcesFor(GitAttrCheckFlags flags, bool hasWorkdir)
    {
        var srcs = new List<AttrSource>(4);
        switch (flags & (GitAttrCheckFlags)0x3)
        {
            case GitAttrCheckFlags.FileThenIndex:
                srcs.Add(AttrSource.Index);
                if (hasWorkdir)
                {
                    srcs.Add(AttrSource.File);
                }

                break;
            case GitAttrCheckFlags.IndexThenFile:
                if (hasWorkdir)
                {
                    srcs.Add(AttrSource.File);
                }

                srcs.Add(AttrSource.Index);
                break;
            case GitAttrCheckFlags.IndexOnly:
                srcs.Add(AttrSource.Index);
                break;
            default:
                break;
        }

        if ((flags & GitAttrCheckFlags.IncludeHead) != 0)
        {
            srcs.Add(AttrSource.Head);
        }

        if ((flags & GitAttrCheckFlags.IncludeCommit) != 0)
        {
            srcs.Add(AttrSource.Commit);
        }

        return srcs;
    }

    /// <summary>
    /// Loads (and memoizes) the <c>.gitattributes</c> file for one directory
    /// level and source.
    /// </summary>
    private async ValueTask<AttributesFile?> LoadDirFileAsync(GitPath dir, AttrSource source, bool allowMacros, CancellationToken cancellationToken)
    {
        if (!_dirFiles.TryGetValue((dir, source), out AttributesFile? cached))
        {
            cached = null;
        }

        // C revalidates cached files on every lookup (git_attr_cache__get →
        // git_attr_file__out_of_date, attrcache.c:253-302): FILE sources are
        // re-statted, INDEX sources re-compare the blob OID, HEAD sources
        // re-resolve HEAD. A stale or now-existing file is reloaded.
        if (cached is not null
            ? await IsDirFileStaleAsync(dir, source, cached, cancellationToken).ConfigureAwait(false)
            : await DirFileExistsAsync(dir, source, cancellationToken).ConfigureAwait(false))
        {
            GitPath rel = BuildGitattributesPath(dir);
            AttrMacroRegistry? macros = allowMacros ? Macros : null;
            cached = source switch
            {
                AttrSource.File => await AttributesFile.LoadFromWorkdirAsync(_repo, macros, _ignoreCase, rel, cancellationToken).ConfigureAwait(false),
                AttrSource.Index => await AttributesFile.LoadFromIndexAsync(_repo, macros, _ignoreCase, rel, cancellationToken).ConfigureAwait(false),
                AttrSource.Head => await AttributesFile.LoadFromHeadAsync(_repo, macros, _ignoreCase, rel, cancellationToken).ConfigureAwait(false),
                _ => await AttributesFile.LoadFromCommitAsync(_repo, macros, _ignoreCase, SourceCommitId, rel, cancellationToken).ConfigureAwait(false),
            };
            _dirFiles[(dir, source)] = cached;
        }

        return cached;
    }

    /// <summary> Builds <c>&lt;dir&gt;/.gitattributes</c> (or <c>.gitattributes</c> at the root) as a byte-faithful path — C splices the raw dir bytes
    /// (attr_file.c). </summary>
    private static GitPath BuildGitattributesPath(GitPath dir)
    {
        if (dir.IsEmpty)
        {
            return GitPath.FromUtf8Bytes(".gitattributes"u8.ToArray());
        }

        ReadOnlySpan<byte> dirSpan = dir.Span;
        byte[] bytes = new byte[dirSpan.Length + 1 + ".gitattributes"u8.Length];
        dirSpan.CopyTo(bytes);
        bytes[dirSpan.Length] = (byte)'/';
        ".gitattributes"u8.CopyTo(bytes.AsSpan(dirSpan.Length + 1));
        return GitPath.FromUtf8Bytes(bytes);
    }

    /// <summary>
    /// Matches the per-source branches of <c>git_attr_file__out_of_date</c>
    /// (attrcache.c:263-336).
    /// </summary>
    private async ValueTask<bool> IsDirFileStaleAsync(GitPath dir, AttrSource source, AttributesFile file, CancellationToken cancellationToken)
    {
        GitPath rel = BuildGitattributesPath(dir);
        switch (source)
        {
            case AttrSource.File:
                {
                    // FS boundary: single display/OS decode — the sanctioned FS-boundary shape.
                    string fullPath = Path.Join(_repo.Workdir!, rel.ToFileSystemString());
                    var info = new FileInfo(fullPath);
                    if (!info.Exists)
                    {
                        return true;
                    }

                    return file.FileStamp is not { } stamp
                        || stamp.Length != info.Length
                        || stamp.MtimeUtc != info.LastWriteTimeUtc;
                }

            case AttrSource.Index:
                {
                    // C (attr.c:668-670): a corrupt index is treated as no
                    // index — the cached file is stale (reload attempt will
                    // find nothing).
                    GitIndex? index;
                    try
                    {
                        index = await _repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (GitException)
                    {
                        index = null;
                    }

                    // byte-keyed index probe — the string bridge re-encoded the dir and missed non-UTF-8 entries.
                    GitIndexEntry? entry = index?.EntryByPath(rel);
                    return !entry.HasValue
                        || file.BlobStamp is not { } blob
                        || !blob.Equals(entry.Value.Id);
                }

            case AttrSource.Head:
                {
                    GitReference? head = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
                    if (head is not GitDirectReference direct)
                    {
                        return true;
                    }

                    // C (attr_file.c:243-244, git_repository_head_tree): HEAD files are stamped with the TREE oid — two commits that share a tree share the
                    // cache entry.
                    Commit? headCommit = await _repo.Objects.LookupAsync<Commit>(direct.Target, cancellationToken).ConfigureAwait(false);
                    return headCommit is null
                        || file.HeadStamp is not { } hs
                        || !hs.Equals(headCommit.Tree);
                }

            default:
                // COMMIT source is pinned by the caller's commit id.
                return false;
        }
    }

    /// <summary>
    /// Whether a not-yet-cached (or cached-as-missing) file now exists.
    /// Matches C's "load file if we don't have one" (attrcache.c:292-295).
    /// </summary>
    private async ValueTask<bool> DirFileExistsAsync(GitPath dir, AttrSource source, CancellationToken cancellationToken)
    {
        GitPath rel = BuildGitattributesPath(dir);
        switch (source)
        {
            case AttrSource.File:
                return File.Exists(Path.Join(_repo.Workdir!, rel.ToFileSystemString()));

            case AttrSource.Index:
                {
                    // C (attr.c:668-670): a missing/corrupt index is cleared
                    // ("no error even if there is no index") — the walk
                    // proceeds without the INDEX source.
                    GitIndex? index;
                    try
                    {
                        index = await _repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (GitException)
                    {
                        index = null;
                    }

                    return index is not null && index.EntryByPath(rel).HasValue;
                }

            case AttrSource.Head:
                {
                    GitReference? head = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
                    return head is GitDirectReference;
                }

            default:
                return false; // COMMIT source pinned by the caller's commit id
        }
    }

    /// <summary>
    /// The commit whose tree supplies the COMMIT source
    /// (GIT_ATTR_CHECK_INCLUDE_COMMIT). Set by the filter-list path from the
    /// filter source's blob commit.
    /// </summary>
    internal GitOid SourceCommitId { get; set; }

    private static GitAttrValue[] LookupManyIn(List<AttributesFile> files, AttrPath path, IReadOnlyList<string> attrNames)
    {
        var results = new GitAttrValue[attrNames.Count];
        for (int i = files.Count - 1; i >= 0; i--)
        {
            AttributesFile file = files[i];
            bool anyMissing = false;
            for (int j = 0; j < attrNames.Count; j++)
            {
                if (results[j].Kind is GitAttrValueKind.None)
                {
                    GitAttrValue value = file.LookupOne(path, attrNames[j]);
                    if (value.Kind is not GitAttrValueKind.None)
                    {
                        results[j] = value;
                    }
                    else
                    {
                        anyMissing = true;
                    }
                }
            }

            if (!anyMissing)
            {
                break; // All attrs resolved from this file.
            }
        }

        return results;
    }

    private async Task<List<AttributesFile>> LoadFilesAsync(CancellationToken cancellationToken)
    {
        var files = new List<AttributesFile>();

        // Precedence (lowest to highest, so lookups iterate in reverse):
        // 1. System gitattributes
        // 2. core.attributesfile (global)
        // 3. Workdir root .gitattributes (and per-dir, but per-dir is path-specific)
        // 4. Index .gitattributes
        // 5. .git/info/attributes
        // Matches collect_attr_files (attr.c:661-708).

        // 1. System
        if (!_repo.IsBare)
        {
            AttributesFile? systemFile = await AttributesFile.LoadFromSystemAsync(_repo.Context.Dirs, Macros, _ignoreCase, cancellationToken).ConfigureAwait(false);
            if (systemFile is not null)
            {
                files.Add(systemFile);
            }
        }

        // 2. Global (core.attributesfile)
        AttributesFile? globalFile = await AttributesFile.LoadFromGlobalAsync(_repo, Macros, _ignoreCase, cancellationToken).ConfigureAwait(false);
        if (globalFile is not null)
        {
            files.Add(globalFile);
        }

        // 3. Workdir root .gitattributes
        if (_repo.Workdir is not null)
        {
            AttributesFile? workdirFile = await AttributesFile.LoadFromWorkdirAsync(_repo, Macros, _ignoreCase, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (workdirFile is not null)
            {
                files.Add(workdirFile);
            }
        }

        // 4. Index .gitattributes
        AttributesFile? indexFile = await AttributesFile.LoadFromIndexAsync(_repo, Macros, _ignoreCase, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (indexFile is not null)
        {
            files.Add(indexFile);
        }

        // 5. .git/info/attributes (highest precedence)
        AttributesFile? infoFile = await AttributesFile.LoadFromInfoAsync(_repo, Macros, _ignoreCase, cancellationToken).ConfigureAwait(false);
        if (infoFile is not null)
        {
            files.Add(infoFile);
        }

        return files;
    }
}

/// <summary>
/// Attribute file sources per directory level. Maps to
/// <c>git_attr_file_source_t</c>.
/// </summary>
internal enum AttrSource
{
    /// <summary>Workdir file. (<c>GIT_ATTR_FILE_SOURCE_FILE</c>)</summary>
    File,

    /// <summary>Index (staged) file. (<c>GIT_ATTR_FILE_SOURCE_INDEX</c>)</summary>
    Index,

    /// <summary>HEAD tree file. (<c>GIT_ATTR_FILE_SOURCE_HEAD</c>)</summary>
    Head,

    /// <summary>Commit tree file. (<c>GIT_ATTR_FILE_SOURCE_COMMIT</c>)</summary>
    Commit,
}

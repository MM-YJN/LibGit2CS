// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary> A decomposed filesystem path for pattern matching. Managed port of <c>git_attr_path</c> in <c>src/libgit2/attr_file.h</c> + the
/// <c>git_attr_path__init</c>/<c>git_attr_path__free</c> functions in <c>attr_file.c</c>. </summary> <remarks> <para> Splits a path into its full form, the
/// relative portion (from the base root), the basename, and whether it is a directory. Used by <see cref="FnMatchPattern.Match"/> to evaluate glob rules with
/// <c>containing_dir</c> prefixes, <c>FULLPATH</c> semantics, and <c>DIRECTORY</c> constraints. </para> <para> <b>Byte-faithful.</b> <see cref="Full"/> owns
/// the joined byte buffer (a single owned <see cref="GitPath"/>); <see cref="Path"/> and <see cref="Basename"/> are zero-copy <see cref="GitPath"/> slices into
/// <see cref="Full"/>'s backing memory, mirroring the C <c>info->path = info->full.ptr + root</c> and <c>info->basename = strrchr(info->path, '/')</c> pointer
/// assignments in <c>attr_file.c:578,593</c>. This is the <c>tree.c:435</c> zero-copy model applied to the attr/ignore path decomposition: one
/// allocation, two slices, byte-wise comparison end-to-end. </para> <para> The <see cref="string"/>-taking <see cref="Init(string, string, DirFlag)"/> overload
/// remains as a convenience overload (encodes to UTF-8 and delegates to the byte-faithful <see cref="Init(GitPath, GitPath, DirFlag)"/>). The structural rules are
/// ASCII, so the lossy decode is safe for the workdir stat; the pattern-match path operates on the raw bytes. </para> </remarks>
internal sealed class AttrPath
{
    /// <summary>Directory flag. Matches <c>git_dir_flag</c>.</summary>
    public enum DirFlag
    {
        /// <summary>Path is known to be a file.</summary>
        False = 0,

        /// <summary>Path is known to be a directory.</summary>
        True = 1,

        /// <summary>Directory status unknown — stat to determine.</summary>
        Unknown = -1,
    }

    /// <summary>
    /// Full path (base + relative). Owns the byte buffer that
    /// <see cref="Path"/>/<see cref="Basename"/> slice into.
    /// </summary>
    public GitPath Full { get; private set; }

    /// <summary>
    /// Relative path (from the base root). This is what patterns match
    /// against. A zero-copy slice into <see cref="Full"/>'s backing buffer.
    /// </summary>
    public GitPath Path { get; private set; }

    /// <summary>
    /// Basename component (the last path segment). A zero-copy slice into
    /// <see cref="Full"/>'s backing buffer.
    /// </summary>
    public GitPath Basename { get; private set; }

    /// <summary>True if the path is a directory.</summary>
    public bool IsDir { get; private set; }

    /// <summary>
    /// Initializes the path decomposition from byte-faithful paths. Matches
    /// <c>git_attr_path__init</c> (<c>attr_file.c:564-616</c>).
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <param name="baseDir">The base directory (e.g. workdir root), or empty.</param>
    /// <param name="dirFlag">Directory status hint.</param>
    public void Init(GitPath path, GitPath baseDir, DirFlag dirFlag)
    {
        // Build full path: base + path (git_fs_path_join_unrooted equivalent).
        // The C git_attr_path__init keeps full = base+path and path = full+root
        // (a pointer into full). We mirror that with a single owned buffer
        // and a slice for the relative portion.
        //
        // We join with '/' only (matching git_str_joinpath = git_str_join with
        // separator '/'), NOT via PathByteHelpers.Join which treats '\' as a
        // separator. A git path like "\c" is a literal backslash-c name on a
        // case-sensitive filesystem, not a rooted path; PathByteHelpers.Join
        // would wrongly strip the leading '\'.
        ReadOnlySpan<byte> pathSpan = path.Span;
        ReadOnlySpan<byte> baseSpan = baseDir.Span;

        int root;
        byte[] fullBytes;
        // C joins only
        // when git_fs_path_root returns -1 (unrooted). A POSIX absolute path
        // ("/foo") is rooted at offset 0 and must NOT be joined with the
        // base, while a drive/UNC-shaped POSIX filename ("\\server",
        // "x:\\y") is unrooted and MUST be joined.
        int r = PathByteHelpers.GetRootLengthInternal(pathSpan);
        if (!baseSpan.IsEmpty && r < 0)
        {
            // Relative path with a base: base + '/' + path (dedup a trailing
            // '/' on base against a leading '/' on path).
            int baseLen = baseSpan.Length;
            int pathLen = pathSpan.Length;
            bool baseEndsSlash = baseLen > 0 && baseSpan[baseLen - 1] == (byte)'/';
            bool pathStartsSlash = pathLen > 0 && pathSpan[0] == (byte)'/';
            int sepLen = (baseEndsSlash || pathStartsSlash) ? 0 : 1;
            int pathOff = pathStartsSlash ? 1 : 0;
            fullBytes = new byte[baseLen + sepLen + (pathLen - pathOff)];
            int off = 0;
            baseSpan.CopyTo(fullBytes.AsSpan(off, baseLen));
            off += baseLen;
            if (sepLen == 1)
            {
                fullBytes[off] = (byte)'/';
                off++;
            }

            pathSpan.Slice(pathOff).CopyTo(fullBytes.AsSpan(off, pathLen - pathOff));
            root = baseLen;
        }
        else
        {
            fullBytes = pathSpan.ToArray();
            root = r < 0 ? 0 : r;
        }

        var full = GitPath.FromUtf8Bytes(fullBytes);
        ReadOnlySpan<byte> fullSpan = full.Span;
        if (root > fullSpan.Length)
        {
            root = fullSpan.Length;
        }

        // Strip trailing slashes from Full (attr_file.c:581-586), but never
        // below the root offset (the C code keeps the NUL at full.size; slicing
        // below root would make path/basename point before the relative start).
        int fullLen = fullSpan.Length;
        while (fullLen > root && fullSpan[fullLen - 1] == (byte)'/')
        {
            fullLen--;
        }

        Full = fullLen < fullSpan.Length ? full.Slice(0, fullLen) : full;

        // Skip leading slashes in the relative path (attr_file.c:589-590).
        ReadOnlySpan<byte> fullAfterRoot = Full.Span.Slice(root);
        int leadingSlashes = 0;
        while (leadingSlashes < fullAfterRoot.Length && fullAfterRoot[leadingSlashes] == (byte)'/')
        {
            leadingSlashes++;
        }

        int relStart = root + leadingSlashes;
        int relEnd = fullLen;
        while (relEnd > relStart && Full.Span[relEnd - 1] == (byte)'/')
        {
            relEnd--;
        }

        Path = relEnd > relStart ? Full.Slice(relStart, relEnd - relStart) : default;

        // Find trailing basename component (attr_file.c:593-597).
        // info->basename = strrchr(info->path, '/'); if found, basename++;
        // if !basename || !*basename, basename = info->path.
        ReadOnlySpan<byte> relSpan = Path.Span;
        int lastSlash = relSpan.LastIndexOf((byte)'/');
        if (lastSlash >= 0)
        {
            int basenameStart = relStart + lastSlash + 1;
            int basenameLen = relEnd - basenameStart;
            Basename = basenameLen > 0 ? Full.Slice(basenameStart, basenameLen) : Path;
        }
        else
        {
            Basename = Path;
        }

        // Determine directory status (attr_file.c:599-613).
        IsDir = dirFlag switch
        {
            DirFlag.True => true,
            DirFlag.False => false,
            _ => Directory.Exists(Full.ToFileSystemString()),
        };
    }

    /// <summary> Initializes the path decomposition from string paths. Encodes to UTF-8 and delegates to <see cref="Init(GitPath, GitPath, DirFlag)"/>.
    /// Convenience overload for string callers. </summary>
    public void Init(string path, string baseDir, DirFlag dirFlag)
        => Init(GitPath.FromUtf8String(path), GitPath.FromUtf8String(baseDir), dirFlag);

    /// <summary>Resets the path to an empty state. Matches <c>git_attr_path__free</c>.</summary>
    public void Free()
    {
        Full = default;
        Path = default;
        Basename = default;
        IsDir = false;
    }
}

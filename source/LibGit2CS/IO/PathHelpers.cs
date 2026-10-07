// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

/*
 * Based on the Android implementation, BSD licensed.
 * http://android.git.kernel.org/
 *
 * Copyright (C) 2008 The Android Open Source Project
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * * Redistributions of source code must retain the above copyright
 *   notice, this list of conditions and the following disclaimer.
 * * Redistributions in binary form must reproduce the above copyright
 *   notice, this list of conditions and the following disclaimer in
 *   the documentation and/or other materials provided with the
 *   distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * AS IS AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
 * COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
 * INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS
 * OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED
 * AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
 * OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System.Collections.Immutable;
using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.IO;

/// <summary> Path manipulation and filesystem helpers. Managed port of the <c>src/util/fs_path.c</c> subset used by the port. </summary> <remarks> <para> Git
/// paths always use forward slashes internally, even on Windows. This helper normalizes to forward slashes and uses <see cref="Path"/> for platform-specific
/// resolution. </para> <para> <b>Skipped from fs_path.c</b>: Win32 drive/registry blocks, <c>diriter</c>, <c>walk_up</c>, <c>basename</c>, and macOS iconv
/// (Unicode decomposition — handled by <see cref="PathPrecompose"/> at the <see cref="GitPath.FromFileSystemString(string, bool)"/> ingress hook).
/// </para> </remarks>
internal static class PathHelpers
{
    private static readonly ImmutableArray<char> s_separatorChars = ['/', '\\'];

    /// <summary>
    /// True if <paramref name="path"/> is "." or "..".
    /// Matches <c>git_fs_path_is_dot_or_dotdot</c>.
    /// </summary>
    public static bool IsDotOrDotDot(ReadOnlySpan<char> path)
    {
        return path.Length == 1 && path[0] == '.' ||
               (path.Length == 2 && path[0] == '.' && path[1] == '.');
    }

    /// <summary>
    /// True if <paramref name="c"/> is a forward or backward slash.
    /// Matches <c>git_fs_path_is_dirsep</c>.
    /// </summary>
    public static bool IsDirSeparator(char c) => c is '/' or '\\';

    /// <summary>
    /// Normalizes backslashes to forward slashes in-place (semantically).
    /// Matches <c>git_fs_path_mkposix</c>.
    /// </summary>
    public static string MakePosix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Returns the directory part of <paramref name="path"/> (everything up to the
    /// last separator). Empty string if no separator. Matches <c>git_fs_path_dirname</c>.
    /// </summary>
    public static string Dirname(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return ".";
        }

        ReadOnlySpan<char> trimmed = TrimTrailingSeparators(path);
        int lastSep = trimmed.LastIndexOfAny(s_separatorChars.AsSpan());
        if (lastSep < 0)
        {
            return ".";
        }

        if (lastSep == 0)
        {
            return "/";
        }

        // Handle root like "/" or "\".
        int rootLen = GetRootLength(trimmed);
        if (lastSep + 1 == rootLen)
        {
            return trimmed[..rootLen].ToString();
        }

        return trimmed[..lastSep].ToString();
    }

    /// <summary>
    /// Returns the root component of <paramref name="path"/> (e.g. <c>"/"</c> on POSIX,
    /// <c>"C:\\"</c> on Windows). Empty string if path is relative.
    /// Matches <c>git_fs_path_root</c>.
    /// </summary>
    public static ReadOnlySpan<char> Root(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return ReadOnlySpan<char>.Empty;
        }

        int rootLen = GetRootLength(path);
        return path[..rootLen];
    }

    /// <summary>
    /// Joins two path components with a single separator. Matches
    /// <c>git_str_join</c> with separator <c>'/'</c> (str.c:760-807, as used
    /// by <c>git_str_joinpath</c>): when <paramref name="a"/> is non-empty,
    /// ALL leading <c>'/'</c> of <paramref name="b"/> are skipped, a
    /// separator is inserted only when <paramref name="a"/> does not end with
    /// one, and only <c>'/'</c> counts as a separator (a backslash is a
    /// literal byte).
    /// </summary>
    public static string Join(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty)
        {
            return b.ToString();
        }

        while (!b.IsEmpty && b[0] == '/')
        {
            b = b[1..];
        }

        if (a[^1] == '/')
        {
            return string.Concat(a, b);
        }

        return string.Concat(a, "/", b);
    }

    /// <summary>
    /// True if <paramref name="path"/> is absolute. Matches <c>git_fs_path_root</c>
    /// in <c>fs_path.c</c>: recognizes POSIX <c>/foo</c>, Windows drive-letter
    /// paths <c>C:\foo</c> / <c>C:/foo</c>, and UNC paths <c>\\server\share</c>
    /// / <c>//server/share</c>.
    /// </summary>
    public static bool IsAbsolute(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return false;
        }

        // POSIX root; on Windows a leading backslash is also a root
        // (C git_fs_path_root: backslash is NOT a root on POSIX).
        if (path[0] == '/' || (OperatingSystem.IsWindows() && path[0] == '\\'))
        {
            return true;
        }

        // Windows drive-letter path: "C:\" or "C:/" (letter + colon + separator).
        if (path.Length >= 3 &&
            (uint)(char.ToLowerInvariant(path[0]) - 'a') <= 'z' - 'a' &&
            path[1] == ':' &&
            IsDirSeparator(path[2]))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ensures <paramref name="path"/> ends with a separator, appending one if missing.
    /// Returns the path unchanged if it already ends with a separator.
    /// Matches <c>git_fs_path_to_dir</c>.
    /// </summary>
    public static string ToDir(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return IsDirSeparator(path[^1]) ? path : path + "/";
    }

    /// <summary>
    /// Squashes runs of consecutive separators into a single separator.
    /// Matches <c>git_fs_path_squash_slashes</c>.
    /// </summary>
    public static string SquashSlashes(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return string.Empty;
        }

        var result = new StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (IsDirSeparator(c))
            {
                result.Append('/');
                while (i + 1 < path.Length && IsDirSeparator(path[i + 1]))
                {
                    i++;
                }
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Canonicalizes <paramref name="path"/> by resolving <c>.</c> and <c>..</c>
    /// components AND following symlinks (realpath), requiring the path to exist.
    /// Matches <c>git_fs_path_prettify</c> (fs_path.c:387-403), which is
    /// p_realpath and fails with GIT_ENOTFOUND "failed to resolve path" on
    /// ENOENT/ENOTDIR.
    /// Callers that must canonicalize a not-yet-created path use
    /// <see cref="PrettifyLexical"/> instead (C creates before prettifying,
    /// e.g. repo_init_directories repository.c:2782-2787).
    /// </summary>
    public static string Prettify(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // realpath(3) requires an existing path; base-joining is done by the
        // call sites (C's `base` parameter is always null in this port).
        string fullPath = Path.GetFullPath(path);
        string? resolved = NativeStat.TryRealpath(fullPath);
        if (resolved is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"failed to resolve path '{path}'",
                GitErrorCategory.Os);
        }

        return resolved.Replace('\\', '/');
    }

    /// <summary>
    /// Lexical canonicalization — resolves <c>.</c> and <c>..</c> components and
    /// makes the path absolute, WITHOUT following symlinks or requiring the path
    /// to exist. Used where C constructs paths before creating them
    /// (repo_init_directories, git_worktree_add) or performs no realpath at all
    /// (git_worktree__read_link's git_fs_path_apply_relative, repository_odb_path).
    /// </summary>
    public static string PrettifyLexical(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return Path.GetFullPath(path).Replace('\\', '/');
    }

    /// <summary>
    /// Canonicalizes <paramref name="path"/> and ensures it is a directory (appends
    /// a trailing slash). Matches <c>git_fs_path_prettify_dir</c>.
    /// </summary>
    public static string PrettifyDir(string path) => ToDir(Prettify(path));

    /// <summary>
    /// Lexical variant of <see cref="PrettifyDir"/> (see
    /// <see cref="PrettifyLexical"/>).
    /// </summary>
    public static string PrettifyLexicalDir(string path) => ToDir(PrettifyLexical(path));
    /// <summary>
    /// Makes <paramref name="path"/> relative to <paramref name="parent"/> using
    /// <c>"../"</c> components; the result keeps the input's trailing slash (or
    /// lack of one) and is empty when the paths are identical. Errors with
    /// GIT_ENOTFOUND when the paths share no common segment. Matches
    /// <c>git_fs_path_make_relative</c> (fs_path.c:946-1008).
    /// </summary>
    public static string MakeRelative(string path, string parent)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(parent);

        // Exact port of git_fs_path_make_relative; the C char-pointer
        // semantics are represented by ' ' sentinels for "end of string".
        string p = path.Replace('\\', '/');
        string q = parent.Replace('\\', '/');

        int pi = 0;
        int qi = 0;
        int pDirSep = 0;
        int qDirSep = 0;

        while (pi < p.Length && qi < q.Length)
        {
            if (p[pi] == '/' && q[qi] == '/')
            {
                pDirSep = pi;
                qDirSep = qi;
            }
            else if (p[pi] != q[qi])
            {
                break;
            }

            pi++;
            qi++;
        }

        char pEnd = pi < p.Length ? p[pi] : '\0';
        char qEnd = qi < q.Length ? q[qi] : '\0';
        char pDirSepChar = pDirSep < p.Length ? p[pDirSep] : '\0';
        char qDirSepChar = qDirSep < q.Length ? q[qDirSep] : '\0';

        // need at least 1 common path segment
        if ((pDirSep == 0 || qDirSep == 0)
            && (pDirSepChar != '/' || qDirSepChar != '/'))
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"{parent} is not a parent of {path}",
                GitErrorCategory.Invalid);
        }

        int plen;
        if (pEnd == '/' && qEnd == '\0')
        {
            pi++;
        }
        else if (pEnd == '\0' && qEnd == '/')
        {
            qi++;
        }
        else if (pEnd == '\0' && qEnd == '\0')
        {
            return string.Empty;
        }
        else
        {
            pi = pDirSep + 1;
            qi = qDirSep + 1;
        }

        plen = p.Length - pi;

        if (qi >= q.Length)
        {
            return p.Substring(pi, plen);
        }

        int depth = 1;
        int qScan = qi;
        while (qScan < q.Length)
        {
            int slash = q.IndexOf('/', qScan, StringComparison.Ordinal);
            if (slash < 0 || slash + 1 >= q.Length)
            {
                break;
            }

            depth++;
            qScan = slash + 1;
        }

        var sb = new StringBuilder((depth * 3) + plen);
        for (int i = 0; i < depth; i++)
        {
            sb.Append("../");
        }

        sb.Append(p, pi, plen);
        return sb.ToString();
    }

    /// <summary>
    /// True if <paramref name="path"/> exists on disk. Matches <c>git_fs_path_exists</c>.
    /// </summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// True if <paramref name="path"/> is a directory. Matches <c>git_fs_path_isdir</c>.
    /// </summary>
    public static bool IsDirectory(string path) => Directory.Exists(path);

    /// <summary>
    /// True if <paramref name="path"/> is a regular file. Matches <c>git_fs_path_isfile</c>.
    /// </summary>
    public static bool IsFile(string path) => File.Exists(path);

    /// <summary>
    /// True if <paramref name="dir"/> contains a subdirectory named <paramref name="name"/>.
    /// Matches <c>git_fs_path_contains_dir</c>.
    /// </summary>
    public static bool ContainsDirectory(string dir, string name) => Directory.Exists(Join(dir, name));

    /// <summary>
    /// True if <paramref name="dir"/> contains a file named <paramref name="name"/>.
    /// Matches <c>git_fs_path_contains_file</c>.
    /// </summary>
    public static bool ContainsFile(string dir, string name) => File.Exists(Join(dir, name));

    /// <summary>
    /// Computes the common directory prefix length of two paths.
    /// Matches <c>git_fs_path_common_dirlen</c> (fs_path.c:932-944): the last
    /// position where BOTH bytes are <c>'/'</c>, plus one; 0 when there is no
    /// shared <c>'/'</c> (a single-component common prefix like <c>abc</c> vs
    /// <c>abc/def</c> yields 0).
    /// </summary>
    public static int CommonDirLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        int dirsep = -1;

        for (int i = 0; i < minLen; i++)
        {
            if (a[i] == '/' && b[i] == '/')
            {
                dirsep = i;
            }
            else if (a[i] != b[i])
            {
                break;
            }
        }

        return dirsep < 0 ? 0 : dirsep + 1;
    }

    /// <summary>
    /// True if the filesystem at <c>path</c> supports symlinks.
    /// Matches <c>git_fs_path_supports_symlinks</c>. On Linux/macOS: always true.
    /// </summary>
    public static bool SupportsSymlinks() => !OperatingSystem.IsWindows();

    /// <summary>
    /// Iterates over entries in <paramref name="directory"/>, invoking
    /// <paramref name="action"/> for each. The action receives the full path of
    /// each entry. Matches <c>git_fs_path_direach</c>.
    /// </summary>
    /// <param name="directory">The directory to iterate.</param>
    /// <param name="action">Callback receiving each full entry path. Return false to stop iteration.</param>
    /// <param name="includeDotEntries">If true, include <c>.</c> and <c>..</c> entries.</param>
    public static void DirEach(string directory, Func<string, bool> action, bool includeDotEntries = false)
    {
        ArgumentNullException.ThrowIfNull(action);

        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!includeDotEntries)
            {
                string name = Path.GetFileName(entry);
                if (name is "." or "..")
                {
                    continue;
                }
            }

            if (!action(entry))
            {
                break;
            }
        }
    }

    private static ReadOnlySpan<char> TrimTrailingSeparators(ReadOnlySpan<char> path)
    {
        int end = path.Length;
        while (end > 1 && IsDirSeparator(path[end - 1]))
        {
            end--;
        }

        return path[..end];
    }

    private static int GetRootLength(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return 0;
        }

        // POSIX root: "/".
        if (path[0] == '/')
        {
            return 1;
        }

        // UNC: "\\server\share".
        if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
        {
            int sep = path[2..].IndexOf('\\');
            return sep < 0 ? path.Length : sep + 3;
        }

        // Windows drive: "C:\".
        if (path.Length >= 3 && path[1] == ':' && IsDirSeparator(path[2]))
        {
            return 3;
        }

        return 0;
    }
}

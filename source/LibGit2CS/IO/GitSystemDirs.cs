// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.IO;

/// <summary> Resolves global, system, XDG, and template directories. Managed port of libgit2's <c>sysdir.c</c>. </summary> <remarks> <para> Replaces libgit2's
/// static <c>git_sysdir__dirs[]</c> + <c>git_sysdir_global_init</c> mechanism with a per-context instance held by <see cref="GitContext.Dirs"/>. Each directory
/// is resolved lazily on first access and cached. <see cref="Reset"/> re-resolves from environment variables. </para> <para> <b>Linux-first</b>: Windows
/// registry/PATH-based discovery is stubbed (returns empty). macOS <c>getpwuid_r</c> fallback for setuid/sandbox cases is also stubbed — the <c>HOME</c>
/// environment variable is used directly. </para> <para> <b>Env-var source.</b> Directory resolution reads <c>HOME</c> / <c>XDG_CONFIG_HOME</c> from the <see
/// cref="GitEnvironment"/> snapshot passed to the constructor, not from <c>Environment.GetEnvironmentVariable</c> directly. When constructed via <see
/// cref="GitContext"/>, the snapshot is shared with <see cref="GitContext.Env"/>, so callers that mutate <c>ctx.Env["HOME"]</c> affect re-resolution after <see
/// cref="Reset"/>. The parameterless constructor creates its own snapshot (used by tests that don't share a context). </para> <para> <b>Precedence</b>: <see
/// cref="Set(GitSystemDir, string?)"/> wins at the directory-cache layer (it records the override directly). <c>ctx.Env</c> mutations only affect the env-var
/// source consulted on a cache miss — i.e. before first <see cref="Get(GitSystemDir)"/> resolution or after <see cref="Reset"/>. This mirrors libgit2's
/// layering where <c>git_sysdir_set</c> shadows <c>getenv</c>. </para> <para>
/// Per-context instance: callers reach it via <c>ctx.Dirs</c> (or
/// <c>repo.Context.Dirs</c>); static helpers that build config/attribute state before
/// a repo exists receive a <see cref="GitSystemDirs"/> parameter. </para> </remarks>
public sealed class GitSystemDirs
{
    private readonly DirectoryCache _cache = new();
    private readonly GitEnvironment _env;

    private sealed class DirectoryCache
    {
        public readonly string[] _dirs = new string[6];
        public readonly bool[] _resolved = new bool[6];
    }

    /// <summary>
    /// Creates a resolver backed by a fresh snapshot of the process environment.
    /// Used by callers that don't share a <see cref="GitContext"/> (e.g.
    /// <c>FileConfigBackend</c> construction in config tests).
    /// </summary>
    public GitSystemDirs()
    {
        _env = new GitEnvironment();
    }

    /// <summary>
    /// Creates a resolver backed by the given environment snapshot. When
    /// constructed via <see cref="GitContext"/>, the snapshot is shared so
    /// <c>ctx.Env</c> mutations are visible to re-resolution after
    /// <see cref="Reset"/>.
    /// </summary>
    /// <param name="env">The environment snapshot to read <c>HOME</c>/<c>XDG_CONFIG_HOME</c> from.</param>
    public GitSystemDirs(GitEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        _env = env;
    }

    /// <summary>
    /// Resolves the given directory, caching the result. Returns empty string if
    /// the directory cannot be resolved (e.g. HOME is unset).
    /// </summary>
    public string Get(GitSystemDir which)
    {
        int idx = (int)which;
        if (!_cache._resolved[idx])
        {
            _cache._dirs[idx] = GuessDir(which);
            _cache._resolved[idx] = true;
        }

        return _cache._dirs[idx];
    }

    /// <summary>
    /// Overrides the given directory. Pass <c>null</c> to reset to the default.
    /// Matches libgit2's <c>git_sysdir_set</c>.
    /// </summary>
    public void Set(GitSystemDir which, string? path)
    {
        int idx = (int)which;
        if (path is null)
        {
            _cache._dirs[idx] = GuessDir(which);
        }
        else
        {
            // C (sysdir.c:504-546, git_sysdir_set): a literal "$PATH" inside
            // the new value is a placeholder for the CURRENT value — the
            // result is before + old + after joined with the path-list
            // separator.
            const string pathMagic = "$PATH";
            int magic = path.IndexOf(pathMagic, StringComparison.Ordinal);
            if (magic >= 0)
            {
                string before = path[..magic];
                string after = path[(magic + pathMagic.Length)..];
                string old = _cache._dirs[idx];

                // git_str_join semantics: a leading separator on the second
                // part is skipped, and no separator is added when the first
                // part already ends with one.
                string merged = before;
                if (old.Length > 0)
                {
                    merged = JoinPathList(merged, old);
                }

                if (after.Length > 0)
                {
                    merged = JoinPathList(merged, after);
                }

                _cache._dirs[idx] = merged;
            }
            else
            {
                _cache._dirs[idx] = path;
            }
        }

        _cache._resolved[idx] = true;
    }

    /// <summary>
    /// Resets all directories to their defaults (re-reads environment variables).
    /// Matches libgit2's <c>git_sysdir_reset</c>.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_cache._dirs);
        Array.Clear(_cache._resolved);
    }

    /// <summary>
    /// Finds <paramref name="filename"/> in the system directory.
    /// Matches libgit2's <c>git_sysdir_find_system_file</c>.
    /// </summary>
    /// <returns>The full path if found; <c>null</c> otherwise.</returns>
    public string? FindSystemFile(string filename) => FindInDirlist(Get(GitSystemDir.System), filename);

    /// <summary>
    /// Finds <paramref name="filename"/> in the global (user home) directory.
    /// Matches libgit2's <c>git_sysdir_find_global_file</c>.
    /// </summary>
    public string? FindGlobalFile(string filename) => FindInDirlist(Get(GitSystemDir.Global), filename);

    /// <summary>
    /// Finds <paramref name="filename"/> in the XDG config directory.
    /// Matches libgit2's <c>git_sysdir_find_xdg_file</c>.
    /// </summary>
    public string? FindXdgFile(string filename) => FindInDirlist(Get(GitSystemDir.Xdg), filename);

    /// <summary>
    /// Finds <paramref name="filename"/> in the ProgramData directory (Windows only).
    /// Returns <c>null</c> on POSIX.
    /// </summary>
    public string? FindProgramDataFile(string filename) => FindInDirlist(Get(GitSystemDir.ProgramData), filename);

    /// <summary>
    /// Returns the resolved template directory, or <c>null</c> if it doesn't exist.
    /// </summary>
    public string? FindTemplateDir()
    {
        string dir = Get(GitSystemDir.Template);
        return PathHelpers.IsDirectory(dir) ? dir : null;
    }

    /// <summary>
    /// Returns the resolved home directory, or <c>null</c> if unset.
    /// </summary>
    public string? FindHomeDir()
    {
        string dir = Get(GitSystemDir.Home);
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    /// <summary>
    /// Returns the full path to <paramref name="filename"/> in the global directory,
    /// without checking existence. Matches libgit2's <c>git_sysdir_expand_global_file</c>.
    /// </summary>
    public string ExpandGlobalFile(ReadOnlySpan<char> filename)
    {
        string dir = Get(GitSystemDir.Global);
        return PathHelpers.Join(dir, filename);
    }

    /// <summary>
    /// Returns the full path to <paramref name="filename"/> in the home directory,
    /// without checking existence. Matches libgit2's <c>git_sysdir_expand_homedir_file</c>.
    /// </summary>
    public string ExpandHomedirFile(ReadOnlySpan<char> filename)
    {
        string dir = Get(GitSystemDir.Home);
        return PathHelpers.Join(dir, filename);
    }

    /// <summary>Ports <c>git_str_join</c> with the path-list separator.</summary>
    private static string JoinPathList(string a, string b)
    {
        // C (str.c git_str_join): leading separators of b are skipped and a
        // separator is inserted only when a is non-empty. With an empty a,
        // b is copied verbatim (leading separators kept).
        if (a.Length == 0)
        {
            return b;
        }

        string bb = b.TrimStart(PathListSeparator);
        if (a.EndsWith(PathListSeparator, StringComparison.Ordinal))
        {
            return a + bb;
        }

        return a + PathListSeparator + bb;
    }

    /// <summary>
    /// The path-list separator: <c>;</c> on Windows, <c>:</c> elsewhere.
    /// Matches <c>GIT_PATH_LIST_SEPARATOR</c> (git2_util.h:31-34).
    /// </summary>
    private static char PathListSeparator => OperatingSystem.IsWindows() ? ';' : ':';

    private static string? FindInDirlist(string dirlist, ReadOnlySpan<char> filename)
    {
        if (string.IsNullOrEmpty(dirlist))
        {
            return null;
        }

        char sep = PathListSeparator;

        // C (sysdir.c:548-590): walk the entries, splitting on an UNescaped
        // GIT_PATH_LIST_SEPARATOR. A separator preceded by a backslash stays
        // inside the entry (backslash retained); empty entries are skipped.
        int scan = 0;
        while (scan < dirlist.Length)
        {
            int next = scan;
            while (next < dirlist.Length &&
                   !(dirlist[next] == sep && (next == scan || dirlist[next - 1] != '\\')))
            {
                next++;
            }

            int len = next - scan;
            bool more = next < dirlist.Length;
            if (more)
            {
                next++;
            }

            if (len == 0)
            {
                scan = next;
                continue;
            }

            string dir = dirlist.Substring(scan, len);
            string candidate = PathHelpers.Join(dir, filename);
            if (PathHelpers.IsFile(candidate))
            {
                return candidate;
            }

            scan = next;
        }

        return null;
    }

    private string GuessDir(GitSystemDir which) => which switch
    {
        GitSystemDir.System => "/etc",
        GitSystemDir.Global => GuessHomeDir(),
        GitSystemDir.Xdg => GuessXdgDir(),
        GitSystemDir.ProgramData => string.Empty, // Windows only
        GitSystemDir.Template => "/usr/share/git-core/templates",
        GitSystemDir.Home => GuessHomeDir(),
        _ => string.Empty,
    };

    private string GuessHomeDir()
    {
        string? home = _env["HOME"];
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        if (OperatingSystem.IsWindows())
        {
            // C (sysdir.c, git_sysdir_guess_home_dirs): on Windows the home
            // dirlist falls back through %HOME%, %HOMEDRIVE%%HOMEPATH% and
            // %USERPROFILE% (find_win32_dirs keeps only existing directories).
            string? drive = _env["HOMEDRIVE"];
            string? path = _env["HOMEPATH"];
            if (!string.IsNullOrEmpty(drive) && !string.IsNullOrEmpty(path))
            {
                string combined = drive + path;
                if (Directory.Exists(combined))
                {
                    return combined;
                }
            }

            string? profile = _env["USERPROFILE"];
            if (!string.IsNullOrEmpty(profile) && Directory.Exists(profile))
            {
                return profile;
            }
        }

        return string.Empty;
    }

    private string GuessXdgDir()
    {
        // C (sysdir.c:401-410): git__getenv distinguishes SET (even to "")
        // from unset - an empty XDG_CONFIG_HOME yields the relative path
        // "git", not a fallback to $HOME/.config/git.
        string? xdgConfigHome = _env["XDG_CONFIG_HOME"];
        if (xdgConfigHome is not null)
        {
            return PathHelpers.Join(xdgConfigHome, "git");
        }

        if (OperatingSystem.IsWindows())
        {
            // C (sysdir.c, git_sysdir_guess_xdg_dirs): %XDG_CONFIG_HOME%\git,
            // %APPDATA%\git, %LOCALAPPDATA%\git, then the home-dir variants.
            string? appData = _env["APPDATA"];
            if (!string.IsNullOrEmpty(appData) && Directory.Exists(PathHelpers.Join(appData, "git")))
            {
                return PathHelpers.Join(appData, "git");
            }

            string? localAppData = _env["LOCALAPPDATA"];
            if (!string.IsNullOrEmpty(localAppData) && Directory.Exists(PathHelpers.Join(localAppData, "git")))
            {
                return PathHelpers.Join(localAppData, "git");
            }
        }

        string? home = _env["HOME"];
        if (!string.IsNullOrEmpty(home))
        {
            return PathHelpers.Join(home, ".config/git");
        }

        if (OperatingSystem.IsWindows())
        {
            string homeDir = GuessHomeDir();
            if (!string.IsNullOrEmpty(homeDir))
            {
                return PathHelpers.Join(homeDir, ".config/git");
            }
        }

        return string.Empty;
    }
}

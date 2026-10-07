namespace LibGit2CS.UnitTests.IO;

/// <summary> Shared helper for the source-scanning convention tests. Locates the <c>source/LibGit2CS/</c> tree from the test bin directory and enumerates its
/// <c>.cs</c> files as (relativePath, content) pairs. Extracted from <see cref="AsyncConventionTests"/> so both the async-IO and
/// static-state tripwires share one path-resolution strategy. </summary>
internal static class SourceScanner
{
    private static readonly string s_sourceDir = FindSourceDir();

    /// <summary>
    /// The absolute path to <c>source/LibGit2CS/</c>.
    /// </summary>
    public static string SourceDir => s_sourceDir;

    /// <summary>
    /// Enumerates every <c>.cs</c> file under <c>source/LibGit2CS/</c> as a
    /// (relativePath, content) pair. The relative path uses forward slashes
    /// and is matched against the convention-test allowlists.
    /// </summary>
    public static IEnumerable<(string relPath, string content)> EnumerateSourceFiles()
    {
        foreach (string file in Directory.EnumerateFiles(s_sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(s_sourceDir, file).Replace('\\', '/');
            yield return (relPath, File.ReadAllText(file));
        }
    }

    /// <summary> Enumerates every <c>.cs</c> file under the whole <c>source/</c> tree as a (relativePath,
    /// content) pair — the relative path is prefixed with the project folder (e.g. <c>LibGit2CS/Core/RegexAdapter.cs</c>). Used by the encoding-hygiene
    /// tripwires that must cover every project under <c>source/</c>. </summary>
    public static IEnumerable<(string relPath, string content)> EnumerateAllSourceFiles()
    {
        string root = Path.GetFullPath(Path.Combine(s_sourceDir, ".."));
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build-artifact directories (generated stubs are not
            // hand-written source).
            string fullPath = Path.GetFullPath(file);
            if (fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // Normalize to forward slashes — the allowlists in the
            // convention tests (e.g. the Latin1 confinement entry
            // "LibGit2CS/Core/RegexAdapter.cs") are written with forward
            // slashes, and Path.GetRelativePath yields the OS separator.
            string relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            yield return (relPath, File.ReadAllText(file));
        }
    }

    /// <summary>
    /// Computes the 1-based line number of <paramref name="offset"/> within
    /// <paramref name="content"/>.
    /// </summary>
    public static int LineOf(string content, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string FindSourceDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "source", "LibGit2CS");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate source/LibGit2CS/ from " + AppContext.BaseDirectory);
    }
}

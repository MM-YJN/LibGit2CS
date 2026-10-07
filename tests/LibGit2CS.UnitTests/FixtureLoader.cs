using System.IO.Compression;
using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests;

internal static class FixtureLoader
{
    /// <summary>
    /// Builds a <c>file://</c> URL from a filesystem path that is accepted
    /// by <see cref="GitUrlUtils.LocalPathFromUrl"/> on both Windows and POSIX.
    /// </summary>
    /// <remarks>
    /// libgit2's <c>git_fs_path_fromurl</c> only accepts <c>file:///&lt;path&gt;</c>
    /// (three slashes) and <c>file://localhost/&lt;path&gt;</c>. The naive
    /// <c>file://{path}</c> form produces <c>file://C:/...</c> on Windows
    /// (two slashes), which libgit2 rejects as a non-localhost host. This
    /// helper always emits the three-slash form, normalizing the path to
    /// forward slashes first so <c>C:\Users\repo</c> becomes
    /// <c>file:///C:/Users/repo</c>. A POSIX absolute path already starts
    /// with <c>/</c>, so concatenating (not interpolating with a third
    /// slash) yields exactly three slashes — <c>file:////tmp/...</c> (four
    /// slashes) is rejected by C as "not a valid local file URI"
    /// (fs_path.c:511-513).
    /// </remarks>
    public static string TestFileUrl(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }
    public static byte[] LoadBytes(string relativePath)
    {
        string name = $"LibGit2CS.UnitTests.{relativePath.Replace('/', '.').Replace('\\', '.')}";
        using Stream stream = typeof(FixtureLoader).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded fixture not found: {name}");
        byte[] buffer = new byte[stream.Length];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
        {
            throw new IOException($"Short read for fixture {name}: {read}/{buffer.Length}");
        }

        return buffer;
    }

    public static string LoadText(string relativePath)
    {
        return Encoding.UTF8.GetString(LoadBytes(relativePath));
    }

    /// <summary>
    /// Extracts an embedded zip fixture to a unique temp directory and returns
    /// the path to the extracted directory. Used by repo/odb/pack tests that
    /// need real <c>.git</c> directory trees on disk.
    /// </summary>
    /// <param name="zipResourceName">
    /// The dotted resource name of the embedded zip, e.g.
    /// <c>"Fixtures.repo.empty_bare.zip"</c>.
    /// </param>
    /// <returns>The path to the extracted directory.</returns>
    public static string ExtractTreeToTemp(string zipResourceName)
    {
        byte[] bytes = LoadBytes(zipResourceName);
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_Fixtures_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.ExtractToDirectory(tempDir);

        return tempDir;
    }
}

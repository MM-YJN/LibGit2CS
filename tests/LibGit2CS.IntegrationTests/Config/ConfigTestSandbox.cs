using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.IntegrationTests.Config;

/// <summary>
/// Creates a <see cref="GitContext"/> whose global/XDG/system/ProgramData
/// config search paths and <c>HOME</c> point at an empty temp directory, so
/// the real <c>~/.gitconfig</c>, <c>$XDG_CONFIG_HOME/git/config</c>, and
/// <c>/etc/gitconfig</c> never leak into config integration tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The repo config stack falls back to the global
/// level (libgit2 parity): a <c>user.name</c>/<c>user.email</c> key absent
/// from <c>.git/config</c> resolves from <c>~/.gitconfig</c>. Tests that
/// assert <c>null</c> for an unset <c>user.*</c> key fail on developer
/// machines where <c>~/.gitconfig</c> is populated, and would also fail in
/// CI that inherits a real home dir. libgit2's clar harness prevents this
/// with <c>cl_sandbox_set_search_path_defaults()</c> (tests/clar/clar_libgit2.c:663),
/// which redirects all four search paths to a temp <c>__config</c> dir.
/// This helper is the managed analogue.
/// </para>
/// <para>
/// <b>Scope.</b> Redirects <see cref="GitSystemDir.Global"/>,
/// <see cref="GitSystemDir.Xdg"/>, <see cref="GitSystemDir.System"/>,
/// <see cref="GitSystemDir.ProgramData"/>, and <see cref="GitSystemDir.Home"/>
/// to a single fresh empty temp directory, then calls
/// <see cref="GitSystemDirs.Reset"/> so the cached resolutions are dropped
/// and the next <see cref="GitSystemDirs.Get"/> re-reads from the overridden
/// <c>HOME</c>. The temp dir is owned by the caller and must be deleted in a
/// <c>finally</c> block via <see cref="Cleanup"/>.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
internal static class ConfigTestSandbox
{
    /// <summary>
    /// Creates a sandboxed <see cref="GitContext"/> and returns it together
    /// with the temp directory path the caller must delete in <c>finally</c>.
    /// </summary>
    public static (GitContext Context, string SandboxDir) Create()
    {
        string sandboxDir = Path.Combine(Path.GetTempPath(), "libgit2cs-cfgsandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxDir);

        GitContext ctx = new();
        ctx.Env["HOME"] = sandboxDir;
        ctx.Env["XDG_CONFIG_HOME"] = sandboxDir;
        ctx.Dirs.Set(GitSystemDir.Global, sandboxDir);
        ctx.Dirs.Set(GitSystemDir.Xdg, sandboxDir);
        ctx.Dirs.Set(GitSystemDir.System, sandboxDir);
        ctx.Dirs.Set(GitSystemDir.ProgramData, sandboxDir);
        ctx.Dirs.Set(GitSystemDir.Home, sandboxDir);

        return (ctx, sandboxDir);
    }

    /// <summary>
    /// Best-effort recursive delete of the sandbox temp directory and disposal
    /// of the context. Safe to call from <c>finally</c>.
    /// </summary>
    public static async ValueTask CleanupAsync(GitContext? context, string? sandboxDir)
    {
        context?.Dispose();

        if (sandboxDir is not null)
        {
            try
            {
                Directory.Delete(sandboxDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Best-effort recursive delete of the sandbox temp directory. Safe to
    /// call from <c>finally</c> when the context is disposed separately.
    /// </summary>
    public static void Cleanup(string? sandboxDir)
    {
        if (sandboxDir is null)
        {
            return;
        }

        try
        {
            Directory.Delete(sandboxDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

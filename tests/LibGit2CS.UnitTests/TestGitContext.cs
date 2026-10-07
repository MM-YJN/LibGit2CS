using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests;

/// <summary>
/// Test-only <see cref="GitContext"/> factories.
/// </summary>
internal static class TestGitContext
{
    /// <summary>
    /// Creates a <see cref="GitContext"/> isolated from the host's
    /// global/XDG/system/ProgramData config so tests assert deterministic,
    /// config-derived behavior independent of the developer's
    /// <c>~/.gitconfig</c> (e.g. <c>core.autocrlf</c>). <c>HOME</c> points at a
    /// unique non-existent path (no file is created, so nothing to clean up),
    /// <c>XDG_CONFIG_HOME</c> is cleared, and the system/ProgramData directory
    /// levels are blanked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This isolation matters because <c>RepositoryOpener.OpenRepoConfigAsync</c>
    /// correctly merges the upper config levels (matching libgit2's
    /// <c>load_config</c>), so a repo opened with a plain
    /// <c>new GitContext()</c> inherits whatever the host has in
    /// <c>~/.gitconfig</c>. Tests that assert default config values must opt
    /// out of the host environment via this helper.
    /// </para>
    /// <para>
    /// The <c>GIT_CONFIG_GLOBAL</c>/<c>GIT_CONFIG_SYSTEM</c>/<c>GIT_CONFIG_NOSYSTEM</c>
    /// env overrides are consulted only on the <c>FromEnv</c> open path, which
    /// these tests do not exercise, so the system/ProgramData levels are
    /// neutralized via the directory cache.
    /// </para>
    /// </remarks>
    public static GitContext CreateIsolatedFromHostConfig()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Env["XDG_CONFIG_HOME"] = null;
        ctx.Dirs.Reset();
        ctx.Dirs.Set(GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }
}

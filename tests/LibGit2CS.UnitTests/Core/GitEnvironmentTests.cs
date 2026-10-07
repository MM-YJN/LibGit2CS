using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class GitEnvironmentTests
{
    /// <summary>
    /// A unique variable name suffixed with a GUID so these tests never collide
    /// with each other or with any real process environment variable.
    /// </summary>
    private static string UniqueVar => "LIBGIT2CS_ENV_TEST_" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void Snapshot_CapturesProcessEnv()
    {
        string name = UniqueVar;
        Environment.SetEnvironmentVariable(name, "snapshotted");
        try
        {
            GitEnvironment env = new();

            // The snapshot was taken while the variable was set, so it must
            // reflect that value — even after we clear the process env below.
            Environment.SetEnvironmentVariable(name, null);

            Assert.Equal("snapshotted", env[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Indexer_GetReturnsSnapshotValue()
    {
        string name = UniqueVar;
        Environment.SetEnvironmentVariable(name, "before");
        try
        {
            GitEnvironment env = new();

            Assert.Equal("before", env[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Indexer_GetReturnsNullForAbsentVariable()
    {
        GitEnvironment env = new();

        Assert.Null(env[UniqueVar]);
    }

    [Fact]
    public void Indexer_SetOverwritesExistingEntry()
    {
        GitEnvironment env = new();
        string name = UniqueVar;

        env[name] = "first";
        env[name] = "second";

        Assert.Equal("second", env[name]);
    }

    [Fact]
    public void Indexer_SetNullRemovesEntry()
    {
        GitEnvironment env = new();
        string name = UniqueVar;

        env[name] = "present";
        Assert.Equal("present", env[name]);

        env[name] = null;

        Assert.Null(env[name]);
    }

    [Fact]
    public void Indexer_SetNullRemovesSnapshottedEntry()
    {
        string name = UniqueVar;
        Environment.SetEnvironmentVariable(name, "from-process");
        try
        {
            GitEnvironment env = new();
            Assert.Equal("from-process", env[name]);

            // Setting to null removes the snapshotted entry — subsequent reads
            // return null, simulating "variable is unset for this context".
            env[name] = null;

            Assert.Null(env[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        GitEnvironment env = new();
        string name = UniqueVar;
        env[name] = "present";

        env.Clear();

        Assert.Null(env[name]);
        // PATH is virtually always present in the process environment; after
        // Clear it must also be gone from the snapshot.
        Assert.Null(env["PATH"]);
    }

    [Fact]
    public void Mutations_DoNotLeakToProcessEnv()
    {
        string name = UniqueVar;
        GitEnvironment env = new();

        env[name] = "leaked?";

        // The process environment must be unaffected by context mutations.
        Assert.Null(Environment.GetEnvironmentVariable(name));

        env[name] = null;
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Context_SharesEnvSnapshotWithDirs()
    {
        // GitContext wires Dirs to share the same GitEnvironment instance, so
        // a mutation via ctx.Env is visible to directory re-resolution after
        // Dirs.Reset(). This locks the sharing contract documented on
        // GitSystemDirs(GitEnvironment).
        using GitContext ctx = new();
        string fakeHome = Path.Combine(Path.GetTempPath(), UniqueVar);
        try
        {
            ctx.Env["HOME"] = fakeHome;
            ctx.Dirs.Reset();

            Assert.Equal(fakeHome, ctx.Dirs.FindHomeDir());
        }
        finally
        {
            if (Directory.Exists(fakeHome))
            {
                Directory.Delete(fakeHome, recursive: true);
            }
        }
    }
}

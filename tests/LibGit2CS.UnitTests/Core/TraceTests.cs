using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class TraceTests
{
    [Fact]
    public void Level_DefaultIsNone()
    {
        using var ctx = new GitContext();
        Assert.Equal(GitTraceLevel.None, ctx.Trace.Level);
    }

    [Fact]
    public void SetLevel_Persists()
    {
        using var ctx = new GitContext();
        ctx.Trace.Level = GitTraceLevel.Info;
        Assert.Equal(GitTraceLevel.Info, ctx.Trace.Level);
    }

    [Fact]
    public void Emit_BelowLevel_DoesNotInvokeCallback()
    {
        using var ctx = new GitContext();
        bool received = false;
        ctx.Trace.OnTrace += (_, _) => received = true;

        ctx.Trace.Level = GitTraceLevel.Error;
        ctx.Trace.Emit(GitTraceLevel.Info, "should be filtered");

        Assert.False(received);
    }

    [Fact]
    public void Emit_AtLevel_InvokesCallback()
    {
        using var ctx = new GitContext();
        string? receivedMsg = null;
        ctx.Trace.OnTrace += (_, msg) => receivedMsg = msg;

        ctx.Trace.Level = GitTraceLevel.Info;
        ctx.Trace.Emit(GitTraceLevel.Info, "hello trace");

        Assert.Equal("hello trace", receivedMsg);
    }

    [Fact]
    public void Emit_AboveLevel_InvokesCallback()
    {
        using var ctx = new GitContext();
        bool received = false;
        ctx.Trace.OnTrace += (_, _) => received = true;

        ctx.Trace.Level = GitTraceLevel.Trace;
        ctx.Trace.Emit(GitTraceLevel.Fatal, "fatal at trace level");

        Assert.True(received);
    }
}

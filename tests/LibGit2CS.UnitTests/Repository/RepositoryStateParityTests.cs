using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary>
/// Regression tests for the RepositoryState parity behavior in
/// libgit2 1.9.4. The C enum
/// (repository.h:916-928) and check order (repository.c:3699-3737) were
/// verified with a probe against libgit2 1.9.4: NONE=0 MERGE=1 REVERT=2
/// REVERT_SEQUENCE=3 CHERRYPICK=4 CHERRYPICK_SEQUENCE=5 BISECT=6 REBASE=7
/// REBASE_INTERACTIVE=8 REBASE_MERGE=9 APPLY_MAILBOX=10
/// APPLY_MAILBOX_OR_REBASE=11.
/// </summary>
public sealed class RepositoryStateParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RepositoryStateParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StateParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string GitDir => _repo.Path;
    private string StatePath(string name) => Path.Combine(GitDir, name);

    private void WriteStateFile(string name)
    {
        string path = StatePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private void RemoveStateFile(string name) => File.Delete(StatePath(name));

    [Fact]
    public void State_None()
    {
        Assert.Equal(0, (int)_repo.State);
    }

    [Fact]
    public void State_MergeHead()
    {
        WriteStateFile("MERGE_HEAD");
        Assert.Equal(1, (int)_repo.State);
        Assert.Equal(RepositoryState.Merge, _repo.State);
    }

    [Fact]
    public void State_RevertHead()
    {
        WriteStateFile("REVERT_HEAD");
        Assert.Equal(2, (int)_repo.State);
    }

    [Fact]
    public void State_RevertHeadWithSequencerTodo()
    {
        // C: REVERT_SEQUENCE = 3 when sequencer/todo exists.
        WriteStateFile("REVERT_HEAD");
        WriteStateFile(Path.Combine("sequencer", "todo"));
        Assert.Equal(3, (int)_repo.State);
    }

    [Fact]
    public void State_CherryPickHead()
    {
        WriteStateFile("CHERRY_PICK_HEAD");
        Assert.Equal(4, (int)_repo.State);
    }

    [Fact]
    public void State_CherryPickHeadWithSequencerTodo()
    {
        // C: CHERRYPICK_SEQUENCE = 5 when sequencer/todo exists.
        WriteStateFile("CHERRY_PICK_HEAD");
        WriteStateFile(Path.Combine("sequencer", "todo"));
        Assert.Equal(5, (int)_repo.State);
    }

    [Fact]
    public void State_BisectLog()
    {
        WriteStateFile("BISECT_LOG");
        Assert.Equal(6, (int)_repo.State);
    }

    [Fact]
    public void State_RebaseApplyRebasing()
    {
        WriteStateFile(Path.Combine("rebase-apply", "rebasing"));
        Assert.Equal(7, (int)_repo.State);
    }

    [Fact]
    public void State_RebaseMergeInteractive()
    {
        WriteStateFile(Path.Combine("rebase-merge", "interactive"));
        Assert.Equal(8, (int)_repo.State);
    }

    [Fact]
    public void State_RebaseMergeDir()
    {
        WriteStateFile(Path.Combine("rebase-merge", "head-name"));
        Assert.Equal(9, (int)_repo.State);
    }

    [Fact]
    public void State_RebaseApplyApplying()
    {
        WriteStateFile(Path.Combine("rebase-apply", "applying"));
        Assert.Equal(10, (int)_repo.State);
    }

    [Fact]
    public void State_RebaseApplyDir()
    {
        // C: APPLY_MAILBOX_OR_REBASE = 11 for the bare rebase-apply dir.
        WriteStateFile(Path.Combine("rebase-apply", "head-name"));
        Assert.Equal(11, (int)_repo.State);
    }

    [Fact]
    public void State_RebaseMergeWinsOverRebaseApply()
    {
        // C checks rebase-merge BEFORE rebase-apply (repository.c:3699-3737).
        WriteStateFile(Path.Combine("rebase-merge", "head-name"));
        WriteStateFile(Path.Combine("rebase-apply", "head-name"));
        Assert.Equal(9, (int)_repo.State);
    }

    [Fact]
    public void State_MergeHeadWinsOverRevert()
    {
        WriteStateFile("MERGE_HEAD");
        WriteStateFile("REVERT_HEAD");
        Assert.Equal(1, (int)_repo.State);
    }

    [Fact]
    public void State_SequencerDirAlone_IsNone()
    {
        // C has no standalone "sequencer" state — the dir only refines
        // REVERT/CHERRYPICK via sequencer/todo.
        WriteStateFile(Path.Combine("sequencer", "todo"));
        Assert.Equal(0, (int)_repo.State);
    }
}

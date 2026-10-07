using BenchmarkDotNet.Attributes;

using LibGit2CS.Benchmarks.Fixtures;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;
using LibGit2CS.Status;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Benchmarks;

/// <summary>
/// End-to-end user-visible scenarios through the public API: cold repo
/// open followed by the headline operation, the full commit round-trip on
/// a dedicated mutating fixture, and warm dirty-workdir diff.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("scenario")]
public class ScenarioBenchmarks
{
    private static readonly GitStatusOptions s_withUntracked = new()
    {
        Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
    };

    private string _linearPath = null!;
    private string _dirtyPath = null!;
    private GitContext _dirtyContext = null!;
    private GitRepository _dirtyRepo = null!;
    private GitContext _labContext = null!;
    private GitRepository _labRepo = null!;
    private string _labPath = null!;
    private int _mutation;
    private long _labTime = 1_700_000_000;

    [GlobalSetup]
    public async Task Setup()
    {
        _linearPath = await FixtureFactory.GetLinearAsync().ConfigureAwait(false);
        _dirtyPath = await FixtureFactory.GetWideDirtyAsync().ConfigureAwait(false);
        _dirtyContext = new GitContext();
        _dirtyRepo = await GitRepository.OpenAsync(_dirtyPath, _dirtyContext, CancellationToken.None).ConfigureAwait(false);

        _labPath = await FixtureFactory.CreateCommitLabAsync().ConfigureAwait(false);
        _labContext = new GitContext();
        _labRepo = await GitRepository.OpenAsync(_labPath, _labContext, CancellationToken.None).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _labRepo.DisposeAsync().ConfigureAwait(false);
        _labContext.Dispose();
        try
        {
            Directory.Delete(_labPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        await _dirtyRepo.DisposeAsync().ConfigureAwait(false);
        _dirtyContext.Dispose();
    }

    [Benchmark]
    public async Task<int> OpenAndStatusDirtyWorkdir()
    {
        using var context = new GitContext();
        await using GitRepository repo = await GitRepository.OpenAsync(_dirtyPath, context, CancellationToken.None).ConfigureAwait(false);
        using GitStatusList status = await repo.StatusNewAsync(s_withUntracked, CancellationToken.None).ConfigureAwait(false);
        return status.EntryCount;
    }

    [Benchmark]
    public async Task<int> OpenAndWalkHistory()
    {
        using var context = new GitContext();
        await using GitRepository repo = await GitRepository.OpenAsync(_linearPath, context, CancellationToken.None).ConfigureAwait(false);
        using GitRevWalker walker = repo.NewRevWalker();
        await walker.PushHeadAsync(CancellationToken.None).ConfigureAwait(false);
        int count = 0;
        await foreach (GitOid id in walker.WalkAsync(CancellationToken.None).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<GitOid> CommitRoundTrip()
    {
        const string relPath = "src/File3.cs";
        _mutation++;
        await File.AppendAllTextAsync(Path.Combine(_labRepo.Workdir!, relPath), $"// mutation {_mutation}\n", CancellationToken.None).ConfigureAwait(false);
        GitIndex index = await _labRepo.GetIndexAsync(CancellationToken.None).ConfigureAwait(false);
        await index.AddByPathAsync(relPath, CancellationToken.None).ConfigureAwait(false);
        await index.WriteAsync(CancellationToken.None).ConfigureAwait(false);
        GitOid tree = await index.WriteTreeAsync(CancellationToken.None).ConfigureAwait(false);
        GitObject? head = await _labRepo.RevparseSingleAsync("HEAD", CancellationToken.None).ConfigureAwait(false);
        _labTime++;
        var sig = new GitSignature("bench", "bench@local", new GitTime(_labTime, 0));
        return await _labRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [head!.Id],
            Author = sig,
            Committer = sig,
            Message = $"bench commit {_mutation}\n",
            UpdateRef = "refs/heads/main",
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task<int> DiffHeadToWorkdir_Dirty()
    {
        using GitDiff diff = await _dirtyRepo.DiffTreeToWorkdirWithIndexAsync(null, null, CancellationToken.None).ConfigureAwait(false);
        byte[] buffer = await diff.ToBufferAsync(GitDiffPrintFormat.Patch, CancellationToken.None).ConfigureAwait(false);
        return buffer.Length;
    }

    [Benchmark]
    public async Task<GitOid> RevparseHead_Warm()
    {
        GitObject? obj = await _dirtyRepo.RevparseSingleAsync("HEAD", CancellationToken.None).ConfigureAwait(false);
        return obj!.Id;
    }
}

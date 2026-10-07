using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Benchmarks.Fixtures;

/// <summary>
/// Builds the deterministic synthetic fixture repositories and diff buffer
/// pairs used by the benchmark suites. Fixture repos are materialized once
/// under <c>%TEMP%/libgit2cs-benchmarks/&lt;name&gt;</c> and reused across
/// benchmark runs (BenchmarkDotNet re-runs <c>GlobalSetup</c> for every
/// benchmark case in a fresh process, so caching is what keeps startup
/// costs sane). Delete that directory to force a rebuild after changing
/// any fixture shape — and bump the fixture name suffixes when doing so.
/// </summary>
public static class FixtureFactory
{
    public const int LinearCommitCount = 1_000;
    public const int LinearFileCount = 100;
    public const int LinearDirCount = 10;
    public const int WideFileCount = 1_000;

    private const long BaseTime = 1_700_000_000;
    private const string LinearName = "linear-1000-v1";
    private const string WideCleanName = "wide-clean-1000-v1";
    private const string WideDirtyName = "wide-dirty-1000-v1";
    private const string DagName = "dag-v1";
    private const string DagSidecarFile = "dag-oids.txt";

    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static readonly string[] s_vocabulary =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
        "india", "juliett", "kilo", "lima", "mike", "november", "oscar", "papa",
    ];

    /// <summary>Root directory holding every cached fixture.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "libgit2cs-benchmarks");

    /// <summary>
    /// Linear history: 1,000 commits over 100 text files with realistic
    /// line-churn, HEAD at <c>refs/heads/main</c>. Loose objects only.
    /// </summary>
    public static async Task<string> GetLinearAsync(CancellationToken cancellationToken = default)
        => await GetOrCreateAsync(LinearName, BuildLinearAsync, cancellationToken).ConfigureAwait(false);

    /// <summary>Shallow repo with ~1,000 files across 10 directories and a clean workdir.</summary>
    public static async Task<string> GetWideCleanAsync(CancellationToken cancellationToken = default)
        => await GetOrCreateAsync(WideCleanName, static (path, ct) => BuildWideAsync(path, dirty: false, ct), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Same shape as the clean wide repo, plus a deterministic dirty workdir:
    /// 5 modified tracked files, 4 untracked files, 4 ignored files.
    /// </summary>
    public static async Task<string> GetWideDirtyAsync(CancellationToken cancellationToken = default)
        => await GetOrCreateAsync(WideDirtyName, static (path, ct) => BuildWideAsync(path, dirty: true, ct), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Criss-cross merge DAG (root, left, right, m1, m2, tip1, tip2) with
    /// the interesting OIDs in a <c>dag-oids.txt</c> sidecar (root, tip1,
    /// tip2 — one hex OID per line).
    /// </summary>
    public static async Task<string> GetDagAsync(CancellationToken cancellationToken = default)
        => await GetOrCreateAsync(DagName, BuildDagAsync, cancellationToken).ConfigureAwait(false);

    /// <summary>Reads the (root, tip1, tip2) OIDs from the DAG sidecar.</summary>
    public static async Task<(GitOid Root, GitOid Tip1, GitOid Tip2)> ReadDagOidsAsync(string dagPath, CancellationToken cancellationToken = default)
    {
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(dagPath, DagSidecarFile), cancellationToken).ConfigureAwait(false);
        return (
            GitOid.Parse(lines[0], GitHashAlgorithmKind.Sha1),
            GitOid.Parse(lines[1], GitHashAlgorithmKind.Sha1),
            GitOid.Parse(lines[2], GitHashAlgorithmKind.Sha1));
    }

    /// <summary>
    /// Fresh, uncached repo for the mutating commit scenario: 10 files under
    /// <c>src/</c> and a single root commit on <c>refs/heads/main</c>. The
    /// caller owns the directory and must delete it when done.
    /// </summary>
    public static async Task<string> CreateCommitLabAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Root, "commit-lab-" + Guid.NewGuid().ToString("N"));
        await BuildCommitLabAsync(path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Deterministic buffer pair with mixed churn (modify every 9th line,
    /// delete every 13th, insert after every 17th, one block move) — the
    /// input for pure XDiff benchmarks via <c>PatchFromBuffers</c>. With
    /// <paramref name="duplicateHeavy"/> the lines come from a tiny
    /// vocabulary instead (Myers stress pattern).
    /// </summary>
    public static (byte[] OldText, byte[] NewText) GenerateChurnedPair(int lineCount, int seed, bool duplicateHeavy)
    {
        var rng = new Random(seed);
        string[] oldLines = new string[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            oldLines[i] = duplicateHeavy ? PickWord(rng) : MakeLine(rng, i);
        }

        var newLines = new List<string>(oldLines);
        for (int i = 0; i < newLines.Count; i++)
        {
            if (i % 9 == 0)
            {
                newLines[i] = duplicateHeavy ? PickWord(rng) : $"L{i:D6}-edit {PickWord(rng)} {PickWord(rng)}";
            }
        }

        for (int i = newLines.Count - 1; i >= 0; i--)
        {
            if (i % 13 == 0)
            {
                newLines.RemoveAt(i);
            }
        }

        for (int i = newLines.Count - 1; i >= 1; i--)
        {
            if (i % 17 == 0)
            {
                newLines.Insert(i + 1, duplicateHeavy ? PickWord(rng) : $"inserted-{i} {PickWord(rng)}");
            }
        }

        if (lineCount >= 400)
        {
            List<string> block = newLines.GetRange(40, 20);
            newLines.RemoveRange(40, 20);
            newLines.InsertRange(newLines.Count / 2, block);
        }

        return (ToBytes(oldLines), ToBytes(newLines));
    }

    /// <summary>Deterministic pseudo-random payload for ODB write benchmarks.</summary>
    public static byte[] GeneratePayload(int length)
    {
        byte[] payload = new byte[length];
        var rng = new Random(0xC0FFEE);
        rng.NextBytes(payload);
        return payload;
    }

    // ── Cache plumbing ──────────────────────────────────────────────────

    private static async Task<string> GetOrCreateAsync(string name, Func<string, CancellationToken, Task> builder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        string dir = Path.Combine(Root, name);
        string marker = dir + ".done";
        if (File.Exists(marker))
        {
            return dir;
        }

        await s_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(marker))
            {
                return dir;
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            await builder(dir, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(marker, "ok", cancellationToken).ConfigureAwait(false);
            return dir;
        }
        finally
        {
            s_gate.Release();
        }
    }

    // ── Builders ────────────────────────────────────────────────────────

    private static async Task BuildLinearAsync(string path, CancellationToken ct)
    {
        (GitContext context, GitRepository repo) = await InitAsync(path, ct).ConfigureAwait(false);
        try
        {
            string workdir = repo.Workdir!;
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            var rng = new Random(20_260_901);
            int filesPerDir = LinearFileCount / LinearDirCount;
            var contents = new List<string>[LinearFileCount];
            for (int f = 0; f < LinearFileCount; f++)
            {
                var lines = new List<string>(48);
                for (int l = 0; l < 40; l++)
                {
                    lines.Add(MakeLine(rng, (f * 1_000) + l));
                }

                contents[f] = lines;
                string rel = LinearPath(f, filesPerDir);
                await WriteWorkdirFileAsync(workdir, rel, ToBytes(lines), ct).ConfigureAwait(false);
                await index.AddByPathAsync(rel, ct).ConfigureAwait(false);
            }

            GitOid tree = await index.WriteTreeAsync(ct).ConfigureAwait(false);
            GitOid parent = await CommitAsync(repo, tree, [], "root\n", "refs/heads/main", BaseTime, ct).ConfigureAwait(false);

            for (int i = 1; i < LinearCommitCount; i++)
            {
                int fileIdx = i % LinearFileCount;
                contents[fileIdx].Add(MakeLine(rng, 1_000_000 + i));
                await WriteWorkdirFileAsync(workdir, LinearPath(fileIdx, filesPerDir), ToBytes(contents[fileIdx]), ct).ConfigureAwait(false);
                await index.AddByPathAsync(LinearPath(fileIdx, filesPerDir), ct).ConfigureAwait(false);

                if (i % 10 == 0)
                {
                    int midIdx = (i * 3) % LinearFileCount;
                    if (midIdx != fileIdx)
                    {
                        List<string> midLines = contents[midIdx];
                        midLines[midLines.Count / 2] = MakeLine(rng, 2_000_000 + i);
                        await WriteWorkdirFileAsync(workdir, LinearPath(midIdx, filesPerDir), ToBytes(midLines), ct).ConfigureAwait(false);
                        await index.AddByPathAsync(LinearPath(midIdx, filesPerDir), ct).ConfigureAwait(false);
                    }
                }

                tree = await index.WriteTreeAsync(ct).ConfigureAwait(false);
                parent = await CommitAsync(repo, tree, [parent], $"tick {i}\n", "refs/heads/main", BaseTime + i, ct).ConfigureAwait(false);
            }

            await index.WriteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync(repo, context).ConfigureAwait(false);
        }
    }

    private static async Task BuildWideAsync(string path, bool dirty, CancellationToken ct)
    {
        (GitContext context, GitRepository repo) = await InitAsync(path, ct).ConfigureAwait(false);
        try
        {
            string workdir = repo.Workdir!;
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            var rng = new Random(4_711);
            const int DirCount = 10;
            int filesPerDir = WideFileCount / DirCount;

            for (int f = 0; f < WideFileCount; f++)
            {
                var lines = new List<string>(64);
                int lineCount = 40 + (f % 25);
                for (int l = 0; l < lineCount; l++)
                {
                    lines.Add(MakeLine(rng, (f * 10_000) + l));
                }

                await WriteWorkdirFileAsync(workdir, WidePath(f, filesPerDir), ToBytes(lines), ct).ConfigureAwait(false);
                await index.AddByPathAsync(WidePath(f, filesPerDir), ct).ConfigureAwait(false);
            }

            await WriteWorkdirFileAsync(workdir, ".gitignore", "*.log\nbuild/\ntmp/\n*.tmp\n"u8.ToArray(), ct).ConfigureAwait(false);
            await index.AddByPathAsync(".gitignore", ct).ConfigureAwait(false);

            GitOid tree = await index.WriteTreeAsync(ct).ConfigureAwait(false);
            await CommitAsync(repo, tree, [], "root\n", "refs/heads/main", BaseTime, ct).ConfigureAwait(false);

            for (int f = 0; f < WideFileCount; f += 20)
            {
                string rel = WidePath(f, filesPerDir);
                await AppendWorkdirLineAsync(workdir, rel, MakeLine(rng, 9_000_000 + f), ct).ConfigureAwait(false);
                await index.AddByPathAsync(rel, ct).ConfigureAwait(false);
            }

            tree = await index.WriteTreeAsync(ct).ConfigureAwait(false);
            await CommitAsync(repo, tree, [(await GetHeadAsync(repo, ct).ConfigureAwait(false))!.Value], "sweep\n", "refs/heads/main", BaseTime + 1, ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);

            if (dirty)
            {
                for (int f = 0; f < 5; f++)
                {
                    await AppendWorkdirLineAsync(workdir, WidePath(f * 105, filesPerDir), "late edit line\n", ct).ConfigureAwait(false);
                }

                await WriteWorkdirFileAsync(workdir, "root_untracked.txt", "untracked at root\n"u8.ToArray(), ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "notes/note_one.txt", "note one\n"u8.ToArray(), ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "notes/note_two.txt", "note two\n"u8.ToArray(), ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "deep/a/b/c/nested.txt", "deeply nested\n"u8.ToArray(), ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "build/artifact.obj", new byte[256], ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "build/sub/other.bin", new byte[128], ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "tmp/cache.log", "log line\n"u8.ToArray(), ct).ConfigureAwait(false);
                await WriteWorkdirFileAsync(workdir, "scratch.tmp", new byte[64], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await DisposeAsync(repo, context).ConfigureAwait(false);
        }
    }

    private static async Task BuildDagAsync(string path, CancellationToken ct)
    {
        (GitContext context, GitRepository repo) = await InitAsync(path, ct).ConfigureAwait(false);
        try
        {
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            GitOid root = await CommitFileAsync(repo, index, "base.txt", "base\n"u8.ToArray(), null, "refs/heads/main", BaseTime, ct).ConfigureAwait(false);
            GitOid left = await CommitFileAsync(repo, index, "left.txt", "left\n"u8.ToArray(), root, "refs/heads/left", BaseTime + 1, ct).ConfigureAwait(false);
            GitOid right = await CommitFileAsync(repo, index, "right.txt", "right\n"u8.ToArray(), root, "refs/heads/right", BaseTime + 2, ct).ConfigureAwait(false);

            GitOid mergeTree = await BuildTreeAsync(repo,
            [
                ("base.txt", await repo.Objects.WriteAsync(GitObjectType.Blob, "base\n"u8.ToArray(), ct).ConfigureAwait(false)),
                ("left.txt", await repo.Objects.WriteAsync(GitObjectType.Blob, "left\n"u8.ToArray(), ct).ConfigureAwait(false)),
                ("right.txt", await repo.Objects.WriteAsync(GitObjectType.Blob, "right\n"u8.ToArray(), ct).ConfigureAwait(false)),
            ], ct).ConfigureAwait(false);

            GitOid m1 = await MergeCommitAsync(repo, mergeTree, [left, right], "m1\n", "refs/heads/left", BaseTime + 3, ct).ConfigureAwait(false);
            GitOid m2 = await MergeCommitAsync(repo, mergeTree, [left, right], "m2\n", "refs/heads/right", BaseTime + 4, ct).ConfigureAwait(false);
            GitOid tip1 = await CommitFileAsync(repo, index, "tip1.txt", "tip1\n"u8.ToArray(), m1, "refs/heads/left", BaseTime + 5, ct).ConfigureAwait(false);
            GitOid tip2 = await CommitFileAsync(repo, index, "tip2.txt", "tip2\n"u8.ToArray(), m2, "refs/heads/right", BaseTime + 6, ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(path, DagSidecarFile), $"{root}\n{tip1}\n{tip2}\n", ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync(repo, context).ConfigureAwait(false);
        }
    }

    private static async Task BuildCommitLabAsync(string path, CancellationToken ct)
    {
        (GitContext context, GitRepository repo) = await InitAsync(path, ct).ConfigureAwait(false);
        try
        {
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            var rng = new Random(1_337);
            for (int i = 0; i < 10; i++)
            {
                var lines = new List<string>(32);
                for (int l = 0; l < 30; l++)
                {
                    lines.Add($"// file {i} line {l}: {PickWord(rng)} {PickWord(rng)}");
                }

                await WriteWorkdirFileAsync(repo.Workdir!, $"src/File{i}.cs", ToBytes(lines), ct).ConfigureAwait(false);
                await index.AddByPathAsync($"src/File{i}.cs", ct).ConfigureAwait(false);
            }

            GitOid tree = await index.WriteTreeAsync(ct).ConfigureAwait(false);
            await CommitAsync(repo, tree, [], "root\n", "refs/heads/main", BaseTime, ct).ConfigureAwait(false);
            await index.WriteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync(repo, context).ConfigureAwait(false);
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    private static async Task<(GitContext Context, GitRepository Repo)> InitAsync(string path, CancellationToken ct)
    {
        var context = new GitContext();
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, context, ct).ConfigureAwait(false);
        return (context, repo);
    }

    private static async Task DisposeAsync(GitRepository repo, GitContext context)
    {
        await repo.DisposeAsync().ConfigureAwait(false);
        context.Dispose();
    }

    private static async Task<GitOid?> GetHeadAsync(GitRepository repo, CancellationToken ct)
    {
        GitObject? head = await repo.RevparseSingleAsync("HEAD", ct).ConfigureAwait(false);
        return head?.Id;
    }

    private static async Task<GitOid> CommitAsync(GitRepository repo, GitOid tree, IReadOnlyList<GitOid> parents, string message, string updateRef, long time, CancellationToken ct)
    {
        var sig = new GitSignature("bench", "bench@local", new GitTime(time, 0));
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = updateRef,
        }, ct).ConfigureAwait(false);
        await repo.SetHeadAsync(updateRef, ct).ConfigureAwait(false);
        return commit;
    }

    private static async Task<GitOid> MergeCommitAsync(GitRepository repo, GitOid tree, IReadOnlyList<GitOid> parents, string message, string refName, long time, CancellationToken ct)
    {
        var sig = new GitSignature("bench", "bench@local", new GitTime(time, 0));
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = null,
        }, ct).ConfigureAwait(false);
        await repo.ReferenceCreateAsync(refName, commit, force: true, logMessage: "merge", cancellationToken: ct).ConfigureAwait(false);
        return commit;
    }

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, GitIndex index, string relPath, byte[] content, GitOid? parent, string updateRef, long time, CancellationToken ct)
    {
        await WriteWorkdirFileAsync(repo.Workdir!, relPath, content, ct).ConfigureAwait(false);
        await index.AddByPathAsync(relPath, ct).ConfigureAwait(false);
        GitOid tree = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        return await CommitAsync(repo, tree, parent is null ? [] : [parent.Value], $"commit {relPath}\n", updateRef, time, ct).ConfigureAwait(false);
    }

    private static async Task<GitOid> BuildTreeAsync(GitRepository repo, IReadOnlyList<(string Path, GitOid Blob)> entries, CancellationToken ct)
    {
        using GitTreeBuilder builder = repo.NewTreeBuilder();
        foreach ((string entryPath, GitOid blob) in entries)
        {
            await builder.InsertAsync(entryPath, blob, GitFileMode.Regular, ct).ConfigureAwait(false);
        }

        return await builder.WriteAsync(ct).ConfigureAwait(false);
    }

    private static string LinearPath(int fileIndex, int filesPerDir)
        => $"src/mod{fileIndex / filesPerDir}/file{fileIndex % filesPerDir}.cs";

    private static string WidePath(int fileIndex, int filesPerDir)
        => $"w{fileIndex / filesPerDir}/f{fileIndex % filesPerDir:D3}.txt";

    private static string PickWord(Random rng)
        => s_vocabulary[rng.Next(s_vocabulary.Length)];

    private static string MakeLine(Random rng, int index)
        => $"L{index:D6} {PickWord(rng)} {PickWord(rng)} {PickWord(rng)}";

    private static byte[] ToBytes(IReadOnlyList<string> lines)
        => Encoding.UTF8.GetBytes(string.Join('\n', lines) + '\n');

    private static async Task WriteWorkdirFileAsync(string workdir, string relPath, byte[] content, CancellationToken ct)
    {
        string full = Path.Combine(workdir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct).ConfigureAwait(false);
    }

    private static async Task AppendWorkdirLineAsync(string workdir, string relPath, string line, CancellationToken ct)
    {
        string full = Path.Combine(workdir, relPath);
        byte[] existing = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        byte[] appended = Encoding.UTF8.GetBytes(line + '\n');
        byte[] combined = new byte[existing.Length + appended.Length];
        existing.AsSpan().CopyTo(combined);
        appended.AsSpan().CopyTo(combined.AsSpan(existing.Length));
        await File.WriteAllBytesAsync(full, combined, ct).ConfigureAwait(false);
    }
}

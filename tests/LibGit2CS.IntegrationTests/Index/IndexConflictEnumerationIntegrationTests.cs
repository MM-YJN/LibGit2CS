using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Index;

/// <summary>
/// Integration tests for <see cref="GitIndex"/> conflict/REUC/NAME entry
/// enumeration and round-trip, exercised end-to-end against
/// locally-initialized repos with real conflict indices produced by
/// <see cref="GitRepository.MergeCommitsAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="Merge.MergeConflictIntegrationTests"/> produces a conflict
/// index via <see cref="GitRepository.MergeCommitsAsync"/> and reads
/// individual conflict entries by path, but never:
/// <list type="bullet">
/// <item>drains the full sorted entry iteration over a conflicted index
/// (the <see cref="GitIndex.EntryByIndex"/> walk over all stages);</item>
/// <item>exercises the <see cref="GitIndex.ReucAdd"/> →
/// <see cref="GitIndex.WriteAsync"/> → <see cref="GitIndex.OpenAsync"/>
/// round-trip for the REUC extension (<c>WriteReucExtension</c> +
/// <c>ReadReuc</c>);</item>
/// <item>exercises the <see cref="GitIndex.NameAdd"/> → write → read
/// round-trip for the NAME extension (<c>WriteNameExtension</c> +
/// <c>ReadConflictNames</c>);</item>
/// <item>exercises <see cref="GitIndex.ConflictRemove"/> /
/// <see cref="GitIndex.ConflictCleanup"/> over a multi-path conflict.</item>
/// </list>
/// The <c>MoveNext</c> iterators and the
/// <c>WriteReucExtension</c>/<c>ReadReuc</c>/<c>WriteNameExtension</c>/
/// <c>ReadConflictNames</c> helpers were entirely cold in the integration
/// suite.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/index/conflict.c</c>
/// (<c>test_index_conflicts__add</c>,
/// <c>test_index_conflicts__get</c>,
/// <c>test_index_conflicts__remove</c>), adapted to build a real conflict
/// via <see cref="GitRepository.MergeCommitsAsync"/> and to round-trip the
/// index file through the real on-disk format.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class IndexConflictEnumerationIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-idxconf-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Commits the given (path → content) files to <paramref name="refName"/>
    /// on top of <paramref name="parent"/> (or as a root commit if parent is
    /// null), returning the new commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir,
        IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        index.Clear();
        foreach ((string p, string c) in files)
        {
            string full = Path.Combine(workdir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct).ConfigureAwait(false);
        return commitOid;
    }

    /// <summary>
    /// Builds a 3-way content conflict on <c>shared.txt</c> and returns the
    /// conflicted <see cref="GitIndex"/> from
    /// <see cref="GitRepository.MergeCommitsAsync"/>. Caller disposes the
    /// index.
    /// </summary>
    private static async Task<GitIndex> BuildContentConflictAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid baseCommit = await CommitFilesAsync(
            repo, path,
            new Dictionary<string, string> { ["shared.txt"] = "base\n" },
            "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

        GitOid ourCommit = await CommitFilesAsync(
            repo, path,
            new Dictionary<string, string> { ["shared.txt"] = "ours\n" },
            "ours\n", "refs/heads/ours", baseCommit, ct).ConfigureAwait(false);

        GitOid theirCommit = await CommitFilesAsync(
            repo, path,
            new Dictionary<string, string> { ["shared.txt"] = "theirs\n" },
            "theirs\n", "refs/heads/theirs", baseCommit, ct).ConfigureAwait(false);

        Commit ours = (await repo.ObjectLookupAsync<Commit>(ourCommit, ct).ConfigureAwait(false))!;
        Commit theirs = (await repo.ObjectLookupAsync<Commit>(theirCommit, ct).ConfigureAwait(false))!;
        GitIndex result = await repo.MergeCommitsAsync(ours, theirs, cancellationToken: ct).ConfigureAwait(false);
        await repo.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    // ── Full entry iteration over a conflicted index ────────────────────

    /// <summary>
    /// Iterating all entries of a conflicted index via
    /// <see cref="GitIndex.EntryByIndex"/> yields the stage-1/2/3 entries
    /// for the conflict path in path-sorted order. Exercises the full
    /// sorted-entry walk (the <see cref="GitIndex.EnsureSorted"/> + index
    /// scan that <see cref="Merge.MergeConflictIntegrationTests"/> only
    /// touches via <see cref="GitIndex.EntryByPath"/>).
    /// </summary>
    [Fact]
    public async Task ConflictEntry_Iteration_AllStages_YieldedInPathOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            using GitIndex index = await BuildContentConflictAsync(path, ct);

            // Drain the full entry list.
            var entries = new List<GitIndexEntry>();
            for (int i = 0; i < index.EntryCount; i++)
            {
                entries.Add(index.EntryByIndex(i));
            }

            // All entries are for shared.txt, with stages 1, 2, 3.
            Assert.All(entries, e => Assert.Equal("shared.txt", e.Path.ToUtf8String()));
            Assert.Contains(entries, e => e.Stage == 1);
            Assert.Contains(entries, e => e.Stage == 2);
            Assert.Contains(entries, e => e.Stage == 3);
            Assert.DoesNotContain(entries, e => e.Stage == 0);
            // Stages are yielded in ascending order for a single path.
            Assert.Equal([1, 2, 3], entries.Select(e => e.Stage).ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ConflictGet ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.ConflictGet"/> on a conflicted path
    /// returns the ancestor (stage 1), ours (stage 2), and theirs (stage 3)
    /// entries. Exercises the per-stage <see cref="GitIndex.EntryByPath"/>
    /// lookup loop.
    /// </summary>
    [Fact]
    public async Task ConflictGet_AfterMerge_ReturnsAllThreeStages()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            using GitIndex index = await BuildContentConflictAsync(path, ct);

            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = index.ConflictGet("shared.txt");
            Assert.NotNull(ancestor);
            Assert.NotNull(ours);
            Assert.NotNull(theirs);
            Assert.Equal(1, ancestor!.Value.Stage);
            Assert.Equal(2, ours!.Value.Stage);
            Assert.Equal(3, theirs!.Value.Stage);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitIndex.ConflictGet"/> on a non-conflicted path
    /// throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.NotFound"/>. Exercises the not-found branch.
    /// </summary>
    [Fact]
    public async Task ConflictGet_NonConflictedPath_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            using GitIndex index = await BuildContentConflictAsync(path, ct);
            Assert.Throws<GitException>(() => index.ConflictGet("nonexistent.txt"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ConflictRemove ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.ConflictRemove"/> on one of two conflicted
    /// paths removes only that path's stage-1/2/3 entries; the other path's
    /// conflict entries remain. Exercises the per-stage removal loop.
    /// </summary>
    [Fact]
    public async Task ConflictRemove_OnePath_RemovesOnlyThatPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            await using (repo)
            {
                GitOid baseCommit = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["a.txt"] = "base\n", ["b.txt"] = "base\n" },
                    "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

                GitOid ourCommit = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["a.txt"] = "ours\n", ["b.txt"] = "ours\n" },
                    "ours\n", "refs/heads/ours", baseCommit, ct).ConfigureAwait(false);

                GitOid theirCommit = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["a.txt"] = "theirs\n", ["b.txt"] = "theirs\n" },
                    "theirs\n", "refs/heads/theirs", baseCommit, ct).ConfigureAwait(false);

                Commit ours = (await repo.ObjectLookupAsync<Commit>(ourCommit, ct).ConfigureAwait(false))!;
                Commit theirs = (await repo.ObjectLookupAsync<Commit>(theirCommit, ct).ConfigureAwait(false))!;
                using GitIndex index = await repo.MergeCommitsAsync(ours, theirs, cancellationToken: ct).ConfigureAwait(false);
                Assert.True(index.HasConflicts);

                // Remove a.txt's conflict; b.txt's must remain.
                index.ConflictRemove("a.txt");

                // a.txt no longer has conflict entries.
                Assert.Throws<GitException>(() => index.ConflictGet("a.txt"));
                // b.txt still has all three stages.
                (GitIndexEntry? anc, GitIndexEntry? o, GitIndexEntry? t) = index.ConflictGet("b.txt");
                Assert.NotNull(anc);
                Assert.NotNull(o);
                Assert.NotNull(t);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitIndex.ConflictCleanup"/> removes all stage-1/2/3
    /// entries from the index. Exercises the full-conflict cleanup path.
    /// </summary>
    [Fact]
    public async Task ConflictCleanup_RemovesAllConflictEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            using GitIndex index = await BuildContentConflictAsync(path, ct);
            Assert.True(index.HasConflicts);

            index.ConflictCleanup();

            Assert.False(index.HasConflicts);
            Assert.Throws<GitException>(() => index.ConflictGet("shared.txt"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── REUC round-trip ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.ReucAdd"/> followed by
    /// <see cref="GitIndex.WriteAsync"/> to disk, then
    /// <see cref="GitIndex.OpenAsync"/> to re-read, round-trips the REUC
    /// extension. The reopened index has the same REUC entry count and
    /// matching path/modes/OIDs. Exercises the
    /// <c>WriteReucExtension</c> serializer + <c>ReadReuc</c> parser.
    /// </summary>
    [Fact]
    public async Task ReucAdd_ThenWrite_ThenRead_RoundTripsReucExtension()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            await using (repo)
            {
                // Write a base commit so we have real OIDs.
                GitOid commitOid = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["f.txt"] = "x\n" },
                    "init\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

                GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
                GitOid zero = GitOid.Empty;
                index.ReucAdd("resolved.txt", (uint)GitFileMode.Regular, commitOid, (uint)GitFileMode.Regular, commitOid, (uint)GitFileMode.Regular, zero);
                await index.WriteAsync(ct).ConfigureAwait(false);
            }

            // Re-read the index from disk and verify the REUC entry round-tripped.
            string indexPath = System.IO.Path.Combine(repo.Path, "index");
            GitIndex reopened = await GitIndex.OpenAsync(indexPath, repo.ObjectFormat, ct).ConfigureAwait(false);
            try
            {
                Assert.Equal(1, reopened.ReucCount);
                GitIndexReucEntry? entry = reopened.ReucByPath("resolved.txt");
                Assert.NotNull(entry);
                Assert.Equal("resolved.txt", entry!.Path.ToUtf8String());
                // Modes array: [ancestor, ours, theirs].
                Assert.Equal((uint)GitFileMode.Regular, entry.Modes[0]);
                Assert.Equal((uint)GitFileMode.Regular, entry.Modes[1]);
                Assert.Equal((uint)GitFileMode.Regular, entry.Modes[2]);
            }
            finally
            {
                reopened.Dispose();
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── NAME round-trip ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.NameAdd"/> followed by
    /// <see cref="GitIndex.WriteAsync"/> to disk, then
    /// <see cref="GitIndex.OpenAsync"/> to re-read, round-trips the NAME
    /// (conflict-name) extension. The reopened index has the same NAME
    /// entry count and matching ancestor/ours/theirs paths. Exercises the
    /// <c>WriteNameExtension</c> serializer + <c>ReadConflictNames</c>
    /// parser.
    /// </summary>
    [Fact]
    public async Task NameAdd_ThenWrite_ThenRead_RoundTripsNameExtension()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            await using (repo)
            {
                await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["f.txt"] = "x\n" },
                    "init\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

                GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
                // NAME entries record the paths used for each side of a
                // rename conflict (ancestor path, our path, their path).
                index.NameAdd("old.txt", "ours.txt", "theirs.txt");
                await index.WriteAsync(ct).ConfigureAwait(false);
            }

            string indexPath = System.IO.Path.Combine(repo.Path, "index");
            GitIndex reopened = await GitIndex.OpenAsync(indexPath, repo.ObjectFormat, ct).ConfigureAwait(false);
            try
            {
                Assert.Equal(1, reopened.NameCount);
                GitIndexNameEntry entry = reopened.NameEntries[0];
                Assert.Equal("old.txt", entry.Ancestor?.ToUtf8String());
                Assert.Equal("ours.txt", entry.Ours?.ToUtf8String());
                Assert.Equal("theirs.txt", entry.Theirs?.ToUtf8String());
            }
            finally
            {
                reopened.Dispose();
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── HasConflicts ────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitIndex.HasConflicts"/> is <c>true</c> immediately
    /// after a conflicted merge and <c>false</c> on a clean (no-conflict)
    /// index. Exercises the stage-scan in the <see cref="GitIndex.HasConflicts"/>
    /// getter.
    /// </summary>
    [Fact]
    public async Task HasConflicts_ConflictedIndex_True_CleanIndex_False()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            using GitIndex conflicted = await BuildContentConflictAsync(path, ct);
            Assert.True(conflicted.HasConflicts);

            // A fresh in-memory index with no entries has no conflicts.
            var clean = GitIndex.New(conflicted.OidType);
            try
            {
                Assert.False(clean.HasConflicts);
            }
            finally
            {
                clean.Dispose();
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── EnumerateConflicts (git_index_conflict_iterator) ───────────────

    /// <summary>
    /// <see cref="GitIndex.EnumerateConflicts"/> after a real conflicted
    /// merge yields exactly one (ancestor, ours, theirs) triple for the
    /// conflicted path, with all three stages populated.
    /// </summary>
    [Fact]
    public async Task EnumerateConflicts_AfterMerge_YieldsConflictTriple()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            using GitIndex index = await BuildContentConflictAsync(path, ct);
            Assert.True(index.HasConflicts);

            var triples = index.EnumerateConflicts().ToList();

            (GitIndexEntry? Ancestor, GitIndexEntry? Ours, GitIndexEntry? Theirs) triple =
                Assert.Single(triples);
            Assert.Equal("shared.txt", triple.Ancestor!.Value.Path.ToUtf8String());
            Assert.Equal(1, triple.Ancestor.Value.Stage);
            Assert.Equal(2, triple.Ours!.Value.Stage);
            Assert.Equal(3, triple.Theirs!.Value.Stage);
            Assert.Equal(triple.Ancestor.Value.Path, triple.Ours.Value.Path);
            Assert.Equal(triple.Ancestor.Value.Path, triple.Theirs.Value.Path);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitIndex.EnumerateConflicts"/> over a two-path
    /// conflict yields two triples in path-sorted order (a.txt before b.txt),
    /// each with all three stages.
    /// </summary>
    [Fact]
    public async Task EnumerateConflicts_TwoPathConflict_YieldsTwoTriplesInOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            await using (repo)
            {
                GitOid baseCommit = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["a.txt"] = "base\n", ["b.txt"] = "base\n" },
                    "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

                GitOid ourCommit = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["a.txt"] = "ours\n", ["b.txt"] = "ours\n" },
                    "ours\n", "refs/heads/ours", baseCommit, ct).ConfigureAwait(false);

                GitOid theirCommit = await CommitFilesAsync(
                    repo, path,
                    new Dictionary<string, string> { ["a.txt"] = "theirs\n", ["b.txt"] = "theirs\n" },
                    "theirs\n", "refs/heads/theirs", baseCommit, ct).ConfigureAwait(false);

                Commit ours = (await repo.ObjectLookupAsync<Commit>(ourCommit, ct).ConfigureAwait(false))!;
                Commit theirs = (await repo.ObjectLookupAsync<Commit>(theirCommit, ct).ConfigureAwait(false))!;
                using GitIndex index = await repo.MergeCommitsAsync(ours, theirs, cancellationToken: ct).ConfigureAwait(false);

                var triples = index.EnumerateConflicts().ToList();

                Assert.Equal(2, triples.Count);
                Assert.Equal("a.txt", triples[0].Ancestor!.Value.Path.ToUtf8String());
                Assert.NotNull(triples[0].Ours);
                Assert.NotNull(triples[0].Theirs);
                Assert.Equal("b.txt", triples[1].Ancestor!.Value.Path.ToUtf8String());
                Assert.NotNull(triples[1].Ours);
                Assert.NotNull(triples[1].Theirs);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}

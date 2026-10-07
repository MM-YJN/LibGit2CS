using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// End-to-end tests for the date-parser parity fixes (libgit2 1.9.4),
/// exercised through the public revparse <c>@{date}</c> path and the
/// format-patch email pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <c>HEAD@{date}</c> parses the date with <c>GitDateParser</c> (the
/// strict parser first, approxidate as fallback) and then finds the first
/// reflog entry whose committer time is at or before the parsed epoch.
/// The expected epochs below were differentially verified against the C
/// reference (libgit2 1.9.4) — see <c>GitDateParserParityTests</c>.
/// </para>
/// <para>
/// The two reflog-append tests cover the RFC 2822
/// "day month" ordering bug (the day is dropped when <c>tm_mday</c> starts
/// at 0 instead of -1) and the inverted leap-year decrement in
/// <c>tm_to_time_t</c>: a wrong epoch would land before every entry and
/// revparse would throw <c>NotFound</c> (or resolve the wrong entry).
/// </para>
/// </remarks>
public sealed class DateRevparseIntegrationTests
{
    /// <summary>
    /// RFC 2822 "day month" ordering: "Mon, 15 Jan 2025 10:30:00 +0200" is
    /// 1736929800. The <c>tm_mday = 0</c> bug dropped the "15" and computed
    /// 2024-12-31 10:30 instead, which precedes every reflog entry.
    /// </summary>
    [Fact]
    public async Task HeadAtRfc2822Date_ResolvesToReflogEntryAtOrBeforeDate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        // Reflog entries at the RFC 2822 date and one hour later.
        var sigA = new GitSignature("t", "t@t", new GitTime(1736929800, 0));
        var sigB = new GitSignature("t", "t@t", new GitTime(1736933400, 0));
        await bld.Repo.Refs.AppendReflogAsync("HEAD", history[0], history[0], sigA, "test: entry at date", ct).ConfigureAwait(false);
        await bld.Repo.Refs.AppendReflogAsync("HEAD", history[0], history[1], sigB, "test: entry one hour later", ct).ConfigureAwait(false);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD@{Mon, 15 Jan 2025 10:30:00 +0200}", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[0], obj!.Id);
    }

    /// <summary>
    /// March dates discriminate the leap-year bug: "Sat, 15 Mar 2025 10:30:00
    /// +0200" is 1742027400 with the C leap-year decrement; the inverted
    /// condition shifted every post-February date by a day.
    /// </summary>
    [Fact]
    public async Task HeadAtRfc2822Date_MarchDate_ResolvesCorrectly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        var sigC = new GitSignature("t", "t@t", new GitTime(1742027400, 0));
        var sigD = new GitSignature("t", "t@t", new GitTime(1742031000, 0));
        await bld.Repo.Refs.AppendReflogAsync("HEAD", history[0], history[0], sigC, "test: entry at date", ct).ConfigureAwait(false);
        await bld.Repo.Refs.AppendReflogAsync("HEAD", history[0], history[1], sigD, "test: entry one hour later", ct).ConfigureAwait(false);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD@{Sat, 15 Mar 2025 10:30:00 +0200}", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[0], obj!.Id);
    }

    /// <summary>
    /// ISO-form date revspecs take the same strict-parser path
    /// (1736929800 for "2025-01-15 10:30:00 +0200").
    /// </summary>
    [Fact]
    public async Task HeadAtIsoDate_ResolvesToReflogEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        var sigA = new GitSignature("t", "t@t", new GitTime(1736929800, 0));
        await bld.Repo.Refs.AppendReflogAsync("HEAD", history[0], history[0], sigA, "test: entry at date", ct).ConfigureAwait(false);

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD@{2025-01-15 10:30:00 +0200}", ct);

        Assert.NotNull(obj);
        Assert.Equal(history[0], obj!.Id);
    }

    /// <summary>
    /// A date before every reflog entry resolves to the OLDEST entry — C
    /// (revparse.c:247-255) falls back to the last-examined (oldest) entry
    /// instead of failing.
    /// </summary>
    [Fact]
    public async Task HeadAtDate_BeforeAllReflogEntries_ReturnsOldestEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(2, ct: ct);

        // Only the tip commit has a HEAD reflog entry in this flow.
        LibGit2CS.Refs.GitRefLog? log = await bld.Repo.ReferenceReadLogAsync("HEAD", ct);
        Assert.NotNull(log);
        GitOid oldest = log![log.EntryCount - 1].NewId;

        using GitObject? obj = await bld.Repo.RevparseSingleAsync("HEAD@{Mon, 15 Jan 2024 10:30:00 +0200}", ct);

        Assert.NotNull(obj);
        Assert.Equal(oldest, obj!.Id);
    }

    /// <summary>
    /// Format-patch pipeline guard: the email <c>Date:</c> header goes
    /// through <c>GitDateParser.FormatRfc2822</c>. Whole-hour offsets
    /// must keep the exact C rendering "Wed, 15 Jan 2025 10:30:00 -0500".
    /// </summary>
    [Fact]
    public async Task EmailFormatterDateHeader_MatchesCFormat()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();

        GitOid blob = await bld.WriteBlobAsync("d6\n"u8.ToArray(), ct).ConfigureAwait(false);
        GitOid treeOid = await bld.BuildTreeAsync([("d6.txt", blob, GitFileMode.Regular)], ct).ConfigureAwait(false);
        var sig = new GitSignature("t", "t@t", new GitTime(1736955000, -300));
        GitOid commitOid = await bld.Repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = Array.Empty<GitOid>(),
            Author = sig,
            Committer = sig,
            Message = "d6 subject\n",
            UpdateRef = "refs/heads/main",
        }, ct).ConfigureAwait(false);

        using GitObject? obj = await bld.Repo.ObjectLookupAsync(commitOid, ct).ConfigureAwait(false);
        Commit commit = Assert.IsType<Commit>(obj);

        string email = await GitEmailFormatter.ToBufferTextAsync(commit, cancellationToken: ct).ConfigureAwait(false);

        Assert.Contains("Date: Wed, 15 Jan 2025 10:30:00 -0500\n", email, StringComparison.Ordinal);
    }
}

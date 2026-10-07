using System.Reflection;
using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary> Parity tests for merge/checkout/index: (rename-conflict label decoration), (notify workdir payload), (notify-abort
/// category Callback), (FileOpenFlags default 0x241), (ZDIFF3 beats DIFF3 when both set), (TREE/REUC numeric parsers accept whitespace/`+`),
/// (SetVersion error shape). Expectations are C-verified against libgit2 1.9.4 (checkout.c:2097-2111, 105-116, 142-148; merge_file.c:126-129; util.c:46-63;
/// index.c:802-815). </summary>
public sealed class CheckoutLowParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public CheckoutLowParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutLow2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateRepoAsync(string name)
    {
        string repoDir = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
    }

    private static async ValueTask<GitOid> CommitFileAsync(GitRepository repo, string path, string content, string message)
    {
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync(path, blob, GitFileMode.Regular);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/main",
        });
    }

    // ── rename-conflict label decoration ───────────────────────

    [Fact]
    public async Task MergeFileFromIndex_RenameConflictLabels_Decorated()
    {
        // C (checkout.c:2097-2111): for a 2-to-1 rename conflict (ours->path != theirs->path) the labels become "ours:path" / "theirs:path"
        // (conflict_entry_name).
        string repoDir = Path.Combine(_tempDir, "repo0");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        GitOid ancestorOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "base\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "base\nours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "base\ntheirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        var ancestor = new GitIndexEntry("path", ancestorOid, GitFileMode.Regular);
        var ours = new GitIndexEntry("pathA", oursOid, GitFileMode.Regular);
        var theirs = new GitIndexEntry("pathB", theirsOid, GitFileMode.Regular);

        (byte[] content, _, _, _) = await MergeFileFromIndex.MergeFromEntriesAsync(
            repo, ancestor, ours, theirs, new GitCheckoutOptions(), GitConflictStyle.Merge, TestContext.Current.CancellationToken);

        string text = Encoding.UTF8.GetString(content);
        Assert.Contains("<<<<<<< ours:pathA", text, StringComparison.Ordinal);
        Assert.Contains(">>>>>>> theirs:pathB", text, StringComparison.Ordinal);
    }

    // ── notify workdir payload ─────────────────────────────────

    [Fact]
    public async Task Checkout_Notify_DirtyNotificationHasWorkdir()
    {
        // C (checkout.c:105-116): checkout_notify builds a git_diff_file from the workdir entry and passes it as `workdir` whenever one is available.
        await using GitRepository repo = await CreateRepoAsync("repo1");
        _ = await CommitFileAsync(repo, "f.txt", "v1\n", "m\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        // Dirty the workdir file: HEAD and the target tree are the same, so
        // the delta is UNMODIFIED and the workdir modification is reported
        // as a DIRTY notification.
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "local edit\n", TestContext.Current.CancellationToken);

        var notifications = new List<GitCheckoutNotification>();
        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Safe,
            NotifyFlags = GitCheckoutNotifyFlags.Dirty,
            Notify = n =>
            {
                notifications.Add(n);
                return false;
            },
        };

        await repo.CheckoutHeadAsync(opts, TestContext.Current.CancellationToken);

        GitCheckoutNotification dirty = Assert.Single(notifications);
        Assert.Equal(GitCheckoutNotifyFlags.Dirty, dirty.Why);
        Assert.NotNull(dirty.Workdir);
        GitDiffFile wd = dirty.Workdir!;
        Assert.Equal("f.txt", wd.Path!.Value.ToUtf8String());
    }

    // ── notify-abort error category ────────────────────────────

    [Fact]
    public async Task Checkout_NotifyAbort_CallbackCategory()
    {
        // C (checkout.c:142-148, errors.h:35-44): a nonzero callback return sets GIT_ERROR_CALLBACK.
        await using GitRepository repo = await CreateRepoAsync("repo2");
        _ = await CommitFileAsync(repo, "f.txt", "v1\n", "m\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
            NotifyFlags = GitCheckoutNotifyFlags.Updated,
            Notify = _ => true, // abort
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.CheckoutHeadAsync(opts, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Callback, ex.Category);
    }

    // ── FileOpenFlags default ──────────────────────────────────

    [Fact]
    public void CheckoutOptions_FileOpenFlagsDefault_Is0x241()
    {
        // C (checkout.c:2463-2464): O_CREAT | O_TRUNC | O_WRONLY = 0x241 on Linux.
        Assert.Equal(0x241, new GitCheckoutOptions().FileOpenFlags);
    }

    // ── ZDIFF3 beats DIFF3 when both set ───────────────────────

    [Fact]
    public void MergeFile_BothDiff3AndZdiff3_Zdiff3Wins()
    {
        // C (merge_file.c:126-129): two INDEPENDENT ifs — ZDIFF3 is written last, so it wins when both DIFF3 and ZDIFF3 are set: with both flags the
        // shared "COMMON" line is refined out of the conflict markers.
        var ancestor = GitMergeFileInput.Create("t", 0x81A4, "1\n2\n3\n");
        var ours = GitMergeFileInput.Create("t", 0x81A4, "1\nCOMMON\nOURS\n3\n");
        var theirs = GitMergeFileInput.Create("t", 0x81A4, "1\nCOMMON\nTHEIRS\n3\n");

        GitMergeFileResult both = GitMergeFile.Merge(ancestor, ours, theirs,
            new GitMergeFileOptions { Flags = GitMergeFileFlags.StyleDiff3 | GitMergeFileFlags.StyleZdiff3 });
        GitMergeFileResult zdiff3 = GitMergeFile.Merge(ancestor, ours, theirs,
            new GitMergeFileOptions { Flags = GitMergeFileFlags.StyleZdiff3 });

        Assert.Equal(zdiff3.Content.ToArray(), both.Content.ToArray());

        // The zdiff3 refinement pulled the shared line out of the conflict.
        string text = Encoding.UTF8.GetString(both.Content.Span);
        Assert.Contains("COMMON\n<<<<<<<", text, StringComparison.Ordinal);
    }

    // ── TREE/REUC numeric parsers accept whitespace / `+` ─────

    [Fact]
    public void TreeCache_ParseDecimal_AcceptsWhitespaceAndPlus()
    {
        // C (util.c:46-63): git__strntol64 skips leading whitespace and accepts a '+' sign.
        Type treeCacheType = typeof(TreeCache);
        MethodInfo parseDecimal = treeCacheType.GetMethod("ParseDecimal", BindingFlags.NonPublic | BindingFlags.Static)!;
        ParseDecimalDelegate del = parseDecimal.CreateDelegate<ParseDecimalDelegate>()!;

        (int value, int consumed) = del("  +123x"u8);
        Assert.Equal(123, value);
        Assert.Equal(6, consumed); // two spaces + '+' + three digits

        (int value2, int consumed2) = del("\t-5x"u8);
        Assert.Equal(-5, value2);
        Assert.Equal(3, consumed2);
    }

    private delegate (int Value, int Consumed) ParseDecimalDelegate(ReadOnlySpan<byte> span);

    private delegate uint ReadOctalNulTerminatedDelegate(ReadOnlySpan<byte> buffer, ref int pos, int end);

    [Fact]
    public void ReucOctal_LeadingPlus_Accepted()
    {
        // C (index.c:2354-2359): git__strntol64 base 8 accepts a leading '+'
        // (a leading '-' parses negative and fails the tmp < 0 check). Delegate-based
        // invocation (like ParseDecimal above) — reflection Invoke cannot convert a boxed byte[] to
        // ReadOnlySpan<byte>.
        Type indexType = typeof(GitIndex);
        MethodInfo readOctal = indexType.GetMethod("ReadOctalNulTerminated", BindingFlags.NonPublic | BindingFlags.Static)!;
        ReadOctalNulTerminatedDelegate del = readOctal.CreateDelegate<ReadOctalNulTerminatedDelegate>()!;

        // "+100\0" → octal 100 = 64; pos ends past the NUL.
        int pos = 0;
        uint value = del("+100\0trailing"u8, ref pos, 7);
        Assert.Equal(64u, value);
        Assert.Equal(5, pos);

        // A leading '-' must still fail like C's tmp < 0 check.
        int negPos = 0;
        Assert.Throws<GitException>(() => del("-100\0"u8, ref negPos, 6));
    }

    // ── SetVersion error shape ─────────────────────────────────

    [Fact]
    public async Task Index_SetVersion_Invalid_ThrowsGitException()
    {
        // C (index.c:802-815): invalid version → GIT_ERROR_INDEX "invalid version number".
        await using GitRepository repo = await CreateRepoAsync("repo3");
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);

        GitException ex = Assert.Throws<GitException>(() => index.SetVersion(5));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid version number", ex.Message);
        Assert.Equal(GitErrorCategory.Index, ex.Category);
    }
}

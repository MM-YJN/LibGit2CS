using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;

namespace LibGit2CS.UnitTests.Remote;

public sealed class FetchHeadTests
{
    private const string Sha1Oid = "5e1c8e7f3a2b4c6d8e9f0a1b2c3d4e5f6a7b8c9d";
    private const string Sha1Oid2 = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
    private const string RemoteUrl = "https://github.com/user/repo.git";

    private static GitOid Oid(string hex) => GitOid.Parse(hex, GitHashAlgorithmKind.Sha1);

    [Fact]
    public void Format_BranchRef_ProducesCorrectLine()
    {
        var entry = new GitFetchHeadEntry(
            Oid: Oid(Sha1Oid),
            IsMerge: true,
            RefName: "refs/heads/main",
            RemoteUrl: RemoteUrl);

        string line = entry.Format();
        Assert.Equal($"{Sha1Oid}\t\tbranch 'main' of {RemoteUrl}", line);
    }

    [Fact]
    public void Format_TagRef_ProducesCorrectLine()
    {
        var entry = new GitFetchHeadEntry(
            Oid: Oid(Sha1Oid),
            IsMerge: false,
            RefName: "refs/tags/v1.0",
            RemoteUrl: RemoteUrl);

        string line = entry.Format();
        Assert.Equal($"{Sha1Oid}\tnot-for-merge\ttag 'v1.0' of {RemoteUrl}", line);
    }

    [Fact]
    public void Format_HeadRef_ProducesTwoTabFormat()
    {
        var entry = new GitFetchHeadEntry(
            Oid: Oid(Sha1Oid),
            IsMerge: true,
            RefName: "HEAD",
            RemoteUrl: RemoteUrl);

        string line = entry.Format();
        Assert.Equal($"{Sha1Oid}\t\t{RemoteUrl}", line);
    }

    [Fact]
    public void Format_OtherRef_ProducesQuotedName()
    {
        var entry = new GitFetchHeadEntry(
            Oid: Oid(Sha1Oid),
            IsMerge: false,
            RefName: "refs/pull/1/head",
            RemoteUrl: RemoteUrl);

        string line = entry.Format();
        Assert.Equal($"{Sha1Oid}\tnot-for-merge\t'refs/pull/1/head' of {RemoteUrl}", line);
    }

    [Fact]
    public void Format_NotMerge_BranchIncludesFlag()
    {
        var entry = new GitFetchHeadEntry(
            Oid: Oid(Sha1Oid),
            IsMerge: false,
            RefName: "refs/heads/feature",
            RemoteUrl: RemoteUrl);

        string line = entry.Format();
        Assert.Contains("not-for-merge", line);
        Assert.Contains("branch 'feature' of", line);
    }

    [Fact]
    public void CompareTo_MergeEntriesSortBeforeNonMerge()
    {
        var merge = new GitFetchHeadEntry(Oid(Sha1Oid), true, "refs/heads/a", RemoteUrl);
        var notMerge = new GitFetchHeadEntry(Oid(Sha1Oid2), false, "refs/heads/a", RemoteUrl);

        Assert.True(merge.CompareTo(notMerge) < 0);
        Assert.True(notMerge.CompareTo(merge) > 0);
    }

    [Fact]
    public void CompareTo_SameMergeFlag_SortsByRefName()
    {
        var a = new GitFetchHeadEntry(Oid(Sha1Oid), true, "refs/heads/aaa", RemoteUrl);
        var b = new GitFetchHeadEntry(Oid(Sha1Oid2), true, "refs/heads/bbb", RemoteUrl);

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
    }

    [Fact]
    public void CompareTo_SortsCorrectlyInList()
    {
        var entries = new List<GitFetchHeadEntry>
        {
            new(Oid(Sha1Oid2), false, "refs/heads/zzz", RemoteUrl),
            new(Oid(Sha1Oid), true, "refs/heads/main", RemoteUrl),
            new(Oid(Sha1Oid2), true, "refs/heads/aaa", RemoteUrl),
            new(Oid(Sha1Oid), false, "refs/tags/v1.0", RemoteUrl),
        };

        entries.Sort();

        // Expected: merge entries first (sorted by name), then non-merge (sorted by name)
        Assert.Equal("refs/heads/aaa", entries[0].RefName);
        Assert.True(entries[0].IsMerge);
        Assert.Equal("refs/heads/main", entries[1].RefName);
        Assert.True(entries[1].IsMerge);
        Assert.Equal("refs/heads/zzz", entries[2].RefName);
        Assert.False(entries[2].IsMerge);
        Assert.Equal("refs/tags/v1.0", entries[3].RefName);
        Assert.False(entries[3].IsMerge);
    }

    [Fact]
    public void SanitizeRemoteUrl_StripsCredentials()
    {
        string sanitized = GitFetchHead.SanitizeRemoteUrl("https://user:pass@github.com/repo.git");
        Assert.Equal("https://github.com/repo.git", sanitized);
    }

    [Fact]
    public void SanitizeRemoteUrl_NoCredentials_ReturnsAsIs()
    {
        string url = "https://github.com/repo.git";
        Assert.Equal(url, GitFetchHead.SanitizeRemoteUrl(url));
    }

    [Fact]
    public void SanitizeRemoteUrl_NoScheme_ReturnsAsIs()
    {
        string url = "git@github.com:user/repo.git";
        Assert.Equal(url, GitFetchHead.SanitizeRemoteUrl(url));
    }

    [Fact]
    public async Task WriteAndRead_Roundtrip_PreservesEntries()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fh_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var entries = new List<GitFetchHeadEntry>
            {
                new(Oid(Sha1Oid), true, "refs/heads/main", RemoteUrl),
                new(Oid(Sha1Oid2), false, "refs/tags/v1.0", RemoteUrl),
                new(Oid(Sha1Oid), true, "HEAD", RemoteUrl),
            };

            await GitFetchHead.WriteAsync(tempDir, entries, cancellationToken: TestContext.Current.CancellationToken);

            List<GitFetchHeadEntry> read = await GitFetchHead.ReadAsync(tempDir, GitHashAlgorithmKind.Sha1, cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, read.Count);
            Assert.True(read[0].IsMerge); // Merge entries first
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Read_NonExistentFile_ReturnsEmpty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fh_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            List<GitFetchHeadEntry> read = await GitFetchHead.ReadAsync(tempDir, GitHashAlgorithmKind.Sha1, cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(read);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Truncate_CreatesEmptyFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fh_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await GitFetchHead.TruncateAsync(tempDir, cancellationToken: TestContext.Current.CancellationToken);
            string path = Path.Combine(tempDir, GitFetchHead.FileName);
            Assert.True(File.Exists(path));
            Assert.Equal(string.Empty, await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}

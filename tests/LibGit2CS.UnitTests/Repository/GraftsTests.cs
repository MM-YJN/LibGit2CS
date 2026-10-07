using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

public sealed class GraftsTests : IDisposable
{
    private readonly string _tempDir;

    public GraftsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_GraftsTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Parse_ValidGraftsFile_ReturnsEntries()
    {
        string path = Path.Combine(_tempDir, "grafts");
        // LF line endings — the strict parser rejects CRLF like git_grafts_parse.
        await File.WriteAllTextAsync(
            path,
            "a65fedf39aefe402d3bb6e24df4d4f5fe4547750 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n" +
            "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\n",
            cancellationToken: TestContext.Current.CancellationToken);

        using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(2, grafts.Count);

        var commit1 = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        GraftEntry? entry1 = grafts.Get(commit1);
        Assert.NotNull(entry1);
        Assert.Equal(commit1, entry1!.Value.Commit);
        Assert.Single(entry1!.Value.Parents);
        Assert.Equal(
            GitOid.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904".AsSpan(), GitHashAlgorithmKind.Sha1),
            entry1!.Value.Parents[0]);

        var commit2 = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);
        GraftEntry? entry2 = grafts.Get(commit2);
        Assert.NotNull(entry2);
        Assert.Equal(commit2, entry2!.Value.Commit);
        Assert.Empty(entry2!.Value.Parents);
    }

    [Fact]
    public async Task Parse_EmptyFile_ReturnsEmpty()
    {
        string path = Path.Combine(_tempDir, "grafts");
        await File.WriteAllTextAsync(path, "", cancellationToken: TestContext.Current.CancellationToken);

        using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(0, grafts.Count);
        Assert.Empty(grafts.Oids());
    }

    [Fact]
    public async Task Parse_MissingFile_ReturnsEmpty()
    {
        string path = Path.Combine(_tempDir, "nonexistent");

        using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(0, grafts.Count);
    }

    [Fact]
    public async Task Parse_CommentsAndEmptyLines_FailWholeParse()
    {
        // C (grafts.c:142-181): there is NO comment/blank-line rule — every
        // line must start with a valid OID, so '#' and empty lines abort the
        // whole parse with GIT_ERROR_GRAFTS.
        string path = Path.Combine(_tempDir, "grafts");
        await File.WriteAllTextAsync(
            path,
            "# This is a comment\n" +
            "\n" +
            "a65fedf39aefe402d3bb6e24df4d4f5fe4547750 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n",
            cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
            });

        Assert.Equal(GitErrorCategory.Grafts, ex.Category);
    }

    [Fact]
    public async Task Get_NonexistentOid_ReturnsNull()
    {
        string path = Path.Combine(_tempDir, "grafts");
        await File.WriteAllTextAsync(
            path,
            "a65fedf39aefe402d3bb6e24df4d4f5fe4547750 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n",
            cancellationToken: TestContext.Current.CancellationToken);

        using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        var notInFile = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Null(grafts.Get(notInFile));
    }

    [Fact]
    public async Task Oids_ReturnsAllCommitOids()
    {
        string path = Path.Combine(_tempDir, "grafts");
        // LF line endings — the strict parser rejects CRLF like git_grafts_parse.
        await File.WriteAllTextAsync(
            path,
            "a65fedf39aefe402d3bb6e24df4d4f5fe4547750 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n" +
            "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\n",
            cancellationToken: TestContext.Current.CancellationToken);

        using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        IReadOnlyList<GitOid> oids = grafts.Oids();

        Assert.Equal(2, oids.Count);
        Assert.Contains(GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1), oids);
        Assert.Contains(GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1), oids);
    }

    [Fact]
    public async Task Parse_MultipleParents_AllStored()
    {
        string path = Path.Combine(_tempDir, "grafts");
        await File.WriteAllTextAsync(
            path,
            "a65fedf39aefe402d3bb6e24df4d4f5fe4547750 4b825dc642cb6eb9a060e54bf8d69288fbee4904 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\n",
            cancellationToken: TestContext.Current.CancellationToken);

        using Grafts grafts = await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        var commit = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        GraftEntry? entry = grafts.Get(commit);
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Value.Parents.Count);
    }
}

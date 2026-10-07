using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Filter;

public sealed class IdentFilterTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public IdentFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_Ident_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, TestGitContext.CreateIsolatedFromHostConfig()).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task Smudge_ExpandsId()
    {
        WriteGitAttributes("* ident\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var source = new GitFilterSource(_repo, GitPath.FromUtf8String("test.txt"), blobOid, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);
        IFilter filter = _repo.Context.Filters.Lookup(FilterRegistry.IdentName)!;
        GitApplyResult result = await filter.ApplyAsync(source, "$Id$\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Applied);
        string text = Encoding.UTF8.GetString(result.Output!);
        Assert.Contains("$Id: ", text);
        Assert.Contains(blobOid.ToString(), text);
        Assert.Contains(" $\n", text);
    }

    [Fact]
    public async Task Smudge_NoOid_Passthrough()
    {
        WriteGitAttributes("* ident\n");
        var source = new GitFilterSource(_repo, GitPath.FromUtf8String("test.txt"), GitOid.Empty, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);
        IFilter filter = _repo.Context.Filters.Lookup(FilterRegistry.IdentName)!;
        GitApplyResult result = await filter.ApplyAsync(source, "$Id$\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Clean_ContractsId()
    {
        WriteGitAttributes("* ident\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var source = new GitFilterSource(_repo, GitPath.FromUtf8String("test.txt"), blobOid, 0, GitFilterMode.ToOdb, GitFilterListFlags.None);
        IFilter filter = _repo.Context.Filters.Lookup(FilterRegistry.IdentName)!;
        string expandedStr = "$Id: " + blobOid.ToString() + " $\n";
        byte[] expanded = Encoding.UTF8.GetBytes(expandedStr);
        GitApplyResult result = await filter.ApplyAsync(source, expanded, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Applied);
        Assert.Equal("$Id$\n"u8.ToArray(), result.Output);
    }

    [Fact]
    public async Task Clean_NoId_Passthrough()
    {
        WriteGitAttributes("* ident\n");
        var source = new GitFilterSource(_repo, GitPath.FromUtf8String("test.txt"), GitOid.Empty, 0, GitFilterMode.ToOdb, GitFilterListFlags.None);
        IFilter filter = _repo.Context.Filters.Lookup(FilterRegistry.IdentName)!;
        GitApplyResult result = await filter.ApplyAsync(source, "no id here\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Apply_BinaryContent_Passthrough()
    {
        WriteGitAttributes("* ident\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, new byte[] { 0, 1, 2 }, TestContext.Current.CancellationToken);
        var source = new GitFilterSource(_repo, GitPath.FromUtf8String("test.txt"), blobOid, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);
        IFilter filter = _repo.Context.Filters.Lookup(FilterRegistry.IdentName)!;
        byte[] binary = new byte[] { 0x00, (byte)'$', (byte)'I', (byte)'d', (byte)'$', 0x01 };
        GitApplyResult result = await filter.ApplyAsync(source, binary, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Smudge_IdInMiddleOfContent()
    {
        WriteGitAttributes("* ident\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "header\n$Id$\nfooter\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var source = new GitFilterSource(_repo, GitPath.FromUtf8String("test.txt"), blobOid, 0, GitFilterMode.ToWorktree, GitFilterListFlags.None);
        IFilter filter = _repo.Context.Filters.Lookup(FilterRegistry.IdentName)!;
        GitApplyResult result = await filter.ApplyAsync(source, "header\n$Id$\nfooter\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Applied);
        string text = Encoding.UTF8.GetString(result.Output!);
        Assert.StartsWith("header\n$Id: ", text);
        Assert.Contains(" $\nfooter\n", text);
    }

    [Fact]
    public async Task FilterList_Load_WithIdentAttr_AppliesFilter()
    {
        WriteGitAttributes("* ident\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitFilterList? filters = await _repo.FilterListLoadAsync("test.txt", blobOid, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        Assert.True(filters!.Contains(FilterRegistry.IdentName));
    }

    [Fact]
    public async Task FilterList_Load_WithoutIdentAttr_ReturnsNull()
    {
        // No .gitattributes → no ident attr → filter doesn't apply.
        GitFilterList? filters = await _repo.FilterListLoadAsync("test.txt", null, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(filters);
    }

    private void WriteGitAttributes(string content)
    {
        string attrPath = Path.Combine(_repo.Workdir!, ".gitattributes");
        File.WriteAllText(attrPath, content);
    }
}

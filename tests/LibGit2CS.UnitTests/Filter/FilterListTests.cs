using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Filter;

public sealed class FilterListTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public FilterListTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_FilterList_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Load_NoAttributes_ReturnsNull()
    {
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(filters);
    }

    [Fact]
    public async Task Load_WithTextAttr_ReturnsCrlfFilter()
    {
        WriteGitAttributes("*.txt text\n");
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        Assert.True(filters!.Contains(FilterRegistry.CrlfName));
    }

    [Fact]
    public async Task Load_NoMatchForPattern_ReturnsNull()
    {
        WriteGitAttributes("*.txt text\n");
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.bin", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(filters);
    }

    [Fact]
    public async Task ApplyToBuffer_NoFilters_ReturnsInput()
    {
        WriteGitAttributes("*.txt text\n");
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        byte[] input = "hello\n"u8.ToArray();
        byte[] result = await filters!.ApplyToBufferAsync(input, cancellationToken: TestContext.Current.CancellationToken);
        // text attr with no CRLF in input → passthrough.
        Assert.Equal(input, result);
    }

    [Fact]
    public async Task ApplyToBuffer_Clean_ConvertsCrlfToLf()
    {
        WriteGitAttributes("*.txt text\n");
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        byte[] result = await filters!.ApplyToBufferAsync("a\r\nb\r\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("a\nb\n"u8.ToArray(), result);
    }

    [Fact]
    public async Task ApplyToBuffer_Smudge_ConvertsLfToCrlf()
    {
        WriteGitAttributes("*.txt text\n");
        await _repo.Config.SetBoolAsync("core.autocrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        byte[] result = await filters!.ApplyToBufferAsync("a\nb\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("a\r\nb\r\n"u8.ToArray(), result);
    }

    [Fact]
    public async Task StreamBuffer_AppliesFilters()
    {
        WriteGitAttributes("*.txt text\n");
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        var target = new MemoryStream();
        var targetStream = new MemoryStreamWriteStream(target);
        await filters!.StreamBufferAsync("a\r\nb\r\n"u8.ToArray(), targetStream, cancellationToken: TestContext.Current.CancellationToken);
        targetStream.Dispose();
        Assert.Equal("a\nb\n"u8.ToArray(), target.ToArray());
    }

    [Fact]
    public async Task Contains_NonExistentFilter_ReturnsFalse()
    {
        WriteGitAttributes("*.txt text\n");
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        Assert.False(filters!.Contains("nonexistent"));
    }

    [Fact]
    public async Task Load_IdentAttr_ReturnsIdentFilter()
    {
        WriteGitAttributes("* ident\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", blobOid, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        Assert.True(filters!.Contains(FilterRegistry.IdentName));
    }

    [Fact]
    public async Task Load_BothTextAndIdent_ReturnsBothFilters()
    {
        WriteGitAttributes("* text ident\n");
        await _repo.Config.SetBoolAsync("core.autocrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "$Id$\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", blobOid, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        Assert.True(filters!.Contains(FilterRegistry.CrlfName));
        Assert.True(filters!.Contains(FilterRegistry.IdentName));
    }

    [Fact]
    public async Task Count_WithMultipleFilters()
    {
        WriteGitAttributes("* text ident\n");
        await _repo.Config.SetBoolAsync("core.autocrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitFilterList? filters = await _repo.FilterListLoadAsync("file.txt", blobOid, GitFilterMode.ToWorktree, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(filters);
        Assert.Equal(2, filters!.Count);
    }

    private void WriteGitAttributes(string content)
    {
        string attrPath = Path.Combine(_repo.Workdir!, ".gitattributes");
        File.WriteAllText(attrPath, content);
    }

    private sealed class MemoryStreamWriteStream(MemoryStream stream) : IFilterWriteStream
    {
        public void Write(ReadOnlySpan<byte> buffer) => stream.Write(buffer);
        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void Dispose() => stream.Dispose();
    }
}

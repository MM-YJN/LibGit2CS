using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Filter;

public sealed class CrlfFilterTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public CrlfFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_Crlf_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static readonly byte[] s_crlfData = "line1\r\nline2\r\n"u8.ToArray();
    private static readonly byte[] s_lfData = "line1\nline2\n"u8.ToArray();

    [Fact]
    public async Task Clean_CrlfToLf_WithTextAttr()
    {
        WriteGitAttributes("*.txt text\n");
        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb);
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Smudge_LfToCrlf_WithTextAttr_AndAutoCrlf()
    {
        WriteGitAttributes("*.txt text\n");
        await _repo.Config.SetBoolAsync("core.autocrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        byte[] result = await ApplyFilter("test.txt", s_lfData, GitFilterMode.ToWorktree);
        Assert.Equal(s_crlfData, result);
    }

    [Fact]
    public async Task Clean_NoCrlf_Passthrough()
    {
        WriteGitAttributes("*.txt text\n");
        byte[] result = await ApplyFilter("test.txt", s_lfData, GitFilterMode.ToOdb);
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Clean_Binary_Passthrough()
    {
        WriteGitAttributes("*.txt binary\n");
        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb);
        Assert.Equal(s_crlfData, result);
    }

    [Fact]
    public async Task Clean_NoAttr_NoAutoCrlf_Passthrough()
    {
        // No .gitattributes, no core.autocrlf → no conversion.
        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb);
        Assert.Equal(s_crlfData, result);
    }

    [Fact]
    public async Task Clean_NoAttr_AutoCrlfTrue_Converts()
    {
        await _repo.Config.SetBoolAsync("core.autocrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        // With autocrlf=true and no attr, auto mode. But has_cr_in_index
        // checks the index — since nothing is staged, it passes.
        // First write a blob to ODB so the content has no CR in index.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, s_lfData, TestContext.Current.CancellationToken);
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("test.txt", blobOid, GitFileMode.Regular));
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb);
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Clean_EolCrlf_Passthrough()
    {
        WriteGitAttributes("*.txt text eol=crlf\n");
        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb);
        // eol=crlf means output EOL is CRLF — clean should not strip CRLF.
        // Actually, clean still strips CRLF→LF when the action is TextCrlf?
        // No: eol=crlf sets crlf_action=TEXT_CRLF, output_eol=CRLF.
        // In crlf_apply_to_odb: if crlf_action != BINARY and content has CRLF,
        // it still converts CRLF→LF (clean always goes to LF for the ODB).
        // Wait, re-reading the C code: crlf_apply_to_odb always converts
        // CRLF→LF regardless of output_eol. output_eol is only used by
        // smudge and safecrlf. So clean still converts.
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Smudge_EolCrlf_Converts()
    {
        WriteGitAttributes("*.txt text eol=crlf\n");
        byte[] result = await ApplyFilter("test.txt", s_lfData, GitFilterMode.ToWorktree);
        Assert.Equal(s_crlfData, result);
    }

    [Fact]
    public async Task Smudge_EolLf_NoConversion()
    {
        WriteGitAttributes("*.txt text eol=lf\n");
        byte[] result = await ApplyFilter("test.txt", s_lfData, GitFilterMode.ToWorktree);
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Clean_EmptyFile_Passthrough()
    {
        WriteGitAttributes("*.txt text\n");
        byte[] result = await ApplyFilter("test.txt", [], GitFilterMode.ToOdb);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Smudge_EmptyFile_Passthrough()
    {
        WriteGitAttributes("*.txt text\n");
        await _repo.Config.SetBoolAsync("core.autocrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        byte[] result = await ApplyFilter("test.txt", [], GitFilterMode.ToWorktree);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Clean_TextAuto_BinaryContent_Passthrough()
    {
        WriteGitAttributes("*.txt text=auto\n");
        byte[] binaryData = new byte[] { 0x00, 0x01, 0x02, 0x0D, 0x0A, 0x03 };
        byte[] result = await ApplyFilter("test.txt", binaryData, GitFilterMode.ToOdb);
        // text=auto → binary detection → passthrough for binary.
        Assert.Equal(binaryData, result);
    }

    [Fact]
    public async Task Clean_SafeCrlfFail_ThrowsOnCrlfRemoval()
    {
        WriteGitAttributes("*.txt text eol=lf\n");
        await _repo.Config.SetBoolAsync("core.safecrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GitException>(() =>
            ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb));
    }

    [Fact]
    public async Task Clean_SafeCrlfFalse_NoThrow()
    {
        WriteGitAttributes("*.txt text eol=lf\n");
        await _repo.Config.SetBoolAsync("core.safecrlf", false, cancellationToken: TestContext.Current.CancellationToken);
        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb);
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Clean_AllowUnsafeFlag_DowngradesFailToWarn()
    {
        WriteGitAttributes("*.txt text eol=lf\n");
        await _repo.Config.SetBoolAsync("core.safecrlf", true, cancellationToken: TestContext.Current.CancellationToken);
        // With AllowUnsafe, FAIL is downgraded to WARN → no throw.
        byte[] result = await ApplyFilter("test.txt", s_crlfData, GitFilterMode.ToOdb, GitFilterListFlags.AllowUnsafe);
        Assert.Equal(s_lfData, result);
    }

    [Fact]
    public async Task Smudge_AutoCrlfInput_NoConversion()
    {
        // core.autocrlf=input → smudge does not convert (input = clean only).
        WriteGitAttributes("*.txt text\n");
        await _repo.Config.SetStringAsync("core.autocrlf", "input", cancellationToken: TestContext.Current.CancellationToken);
        byte[] result = await ApplyFilter("test.txt", s_lfData, GitFilterMode.ToWorktree);
        Assert.Equal(s_lfData, result);
    }

    private async Task<byte[]> ApplyFilter(string path, byte[] input, GitFilterMode mode, GitFilterListFlags flags = GitFilterListFlags.None)
    {
        GitFilterList? filters = await _repo.FilterListLoadAsync(path, null, mode, flags);
        if (filters is null)
        {
            return input;
        }

        return await filters.ApplyToBufferAsync(input);
    }

    private void WriteGitAttributes(string content)
    {
        string attrPath = Path.Combine(_repo.Workdir!, ".gitattributes");
        File.WriteAllText(attrPath, content);
    }
}

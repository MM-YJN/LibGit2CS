using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.IO;

// WorkdirReader.
// ComputeOid stack-allocated the WHOLE file content
// (`stackalloc byte[headerBytes.Length + content.Length]`), so reading a
// workdir file of a few tens of MB crashed the process with an
// uncatchable StackOverflowException (verified: 8 MB fails on the default
// 8 MB thread stack). Upstream hashes into heap buffers
// (workdir_reader_read -> git_odb__hash, reader.c / odb.c).
public sealed class WorkdirReaderStackAllocRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public WorkdirReaderStackAllocRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_WorkdirReaderStack_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task ReadAsync_LargeWorkdirFile_ReturnsCorrectOid()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // 16 MiB — far beyond the 8 MB default thread stack. The
        // stackalloc of the full content crashes the test host here
        // (uncatchable StackOverflowException, SIGABRT); the fixed heap
        // path completes normally.
        const int size = 16 * 1024 * 1024;
        byte[] content = new byte[size];
        new Random(42).NextBytes(content);
        await File.WriteAllBytesAsync(
            Path.Combine(repoPath, "big.bin"), content,
            cancellationToken: TestContext.Current.CancellationToken);

        var reader = new WorkdirReader(repo, validateIndex: false);
        ReaderReadResult result = await reader.ReadAsync(
            GitPath.FromUtf8String("big.bin"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFound);

        // Expected OID: sha1("blob <size>\0" + content), matching
        // git_odb__hash (odb.c:916-931).
        byte[] header = Encoding.ASCII.GetBytes($"blob {size}\0");
        byte[] full = new byte[header.Length + content.Length];
        header.CopyTo(full, 0);
        content.CopyTo(full, header.Length);
        var expected = GitOid.FromRaw(SHA1.HashData(full), GitHashAlgorithmKind.Sha1);

        Assert.Equal(expected, result.Result!.Oid);
        Assert.Equal(GitFileMode.Regular, result.Result.Mode);
        Assert.Equal(content, result.Result.Content.ToArray());
    }
}

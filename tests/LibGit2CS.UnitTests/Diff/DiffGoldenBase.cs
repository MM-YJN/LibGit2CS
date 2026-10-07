using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Shared base for repository-backed diff golden tests. Handles fixture
/// extraction, repo open, temp-dir cleanup, and tree resolution. Mirrors the
/// <c>IDisposable</c>+temp-dir pattern used by <see cref="Revwalk.RevWalkerTests"/>
/// etc., consolidated for the diff golden corpus.
/// </summary>
public abstract class DiffGoldenBase : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];
    private readonly GitContext _context = new();
    protected GitContext Context => _context;

    protected DiffGoldenBase()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_DiffGolden_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _context.Dispose();
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Extracts <c>Fixtures/diff/&lt;<paramref name="fixtureName"/>&gt;.zip</c>
    /// to a temp dir and opens the repository at the working-tree root (the
    /// zip packages each fixture as <c>&lt;name&gt;/</c> + <c>.git/</c>, matching
    /// libgit2's <c>cl_git_sandbox_init</c> which renames <c>.gitted</c>→<c>.git</c>).
    /// </summary>
    protected async ValueTask<GitRepository> OpenFixtureRepoAsync(string fixtureName)
    {
        string extracted = FixtureLoader.ExtractTreeToTemp($"Fixtures/diff/{fixtureName}.zip");
        _extractedPaths.Add(extracted);
        string repoPath = Path.Combine(extracted, fixtureName);
        return await GitRepository.OpenAsync(repoPath, Context);
    }

    /// <summary>
    /// Loads the byte-exact golden output captured by <c>generate-goldens.sh</c>
    /// (the stdout of <c>git diff ...</c> on the fixture repo).
    /// </summary>
    /// <remarks>
    /// Diff output is a byte stream that may contain non-ASCII file content
    /// (e.g. UTF-8 em-dashes). The golden is compared via the byte tier
    /// (<see cref="LoadExpectedBytes"/> / <see cref="PrintToBytesAsync"/>);
    /// the string form below is the UTF-8 display decode (U+FFFD replacement)
    /// used by the string-tier goldens, which are ASCII-only.
    /// </remarks>
    protected static string LoadExpected(string caseName)
    {
        byte[] bytes = FixtureLoader.LoadBytes($"Fixtures/diff/expected/{caseName}.txt");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary> Loads the byte-exact golden output captured by <c>generate-goldens.sh</c> as raw bytes. Compare against <see cref="PrintToBytesAsync"/> output
    /// with <c>Assert.Equal</c> — no string decode anywhere, so a byte-corrupting regression on the printer path fails even when a lossy display decode would
    /// hide it. </summary>
    protected static byte[] LoadExpectedBytes(string caseName)
        => FixtureLoader.LoadBytes($"Fixtures/diff/expected/{caseName}.txt");

    /// <summary> Prints the diff via the callback tier directly into a byte writer — no string decode anywhere. This is the byte-native egress harness for the
    /// printer byte-faithfulness tests. </summary>
    protected static async Task<byte[]> PrintToBytesAsync(
        GitDiff diff, GitDiffPrintFormat format, CancellationToken cancellationToken = default)
    {
        using var buf = new PooledByteBufferWriter();
        await diff.PrintAsync(format, (delta, hunk, line) =>
        {
            XdiffBridge.RenderLine(buf, line);
        }, cancellationToken).ConfigureAwait(false);
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Formats <paramref name="stats"/> into a byte buffer and decodes the
    /// result via UTF-8 (with replacement) for string-based assertions. The
    /// byte tier (<c>stats.Format(writer, ...)</c> + <see cref="LoadExpectedBytes"/>)
    /// is the parity surface for non-ASCII paths.
    /// </summary>
    protected static string FormatStats(GitDiffStats stats, GitDiffStatsFormat format, int width = 80)
    {
        using var writer = new PooledByteBufferWriter();
        stats.Format(writer, format, width);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    /// <summary>
    /// Resolves a full 40-char commit OID hex string to its <see cref="GitTree"/>.
    /// Matches clar's <c>resolve_commit_oid_to_tree</c>.
    /// </summary>
    protected static async Task<GitTree> ResolveTreeAsync(GitRepository repo, string commitOidHex)
    {
        var oid = GitOid.Parse(commitOidHex.AsSpan(), repo.ObjectFormat);
        Commit commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");
        return await repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"tree for {commitOidHex} not found");
    }

    /// <summary>
    /// Resolves an abbreviated or full blob oid hex string to its <see cref="GitBlob"/>.
    /// Matches clar's <c>git_blob_lookup_prefix</c>. Used by the blob diff count
    /// tests (ported from <c>diff/blob.c</c>) to load the attr fixture's blobs.
    /// </summary>
    protected static async Task<GitBlob> ResolveBlobAsync(GitRepository repo, string blobOidHex)
    {
        var oid = GitOid.Parse(blobOidHex.AsSpan(), repo.ObjectFormat);
        return await repo.ObjectLookupPrefixAsync<GitBlob>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"blob {blobOidHex} not found");
    }
}

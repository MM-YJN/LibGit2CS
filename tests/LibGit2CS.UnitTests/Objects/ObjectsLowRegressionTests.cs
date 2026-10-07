using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

// Parity cases verified against libgit2 1.9.4:
//  - packlike loose-object header size accumulated into a signed long
//    and wrapped negative; the object was accepted where C's unsigned
//    size_t accumulation trips GIT_ADD_SIZET_OVERFLOW and errors
//    (odb_loose.c:258-262).
//  - the hardcoded empty-tree special case matched the SHA-256 empty
//    tree OID even in a SHA-1 repo, fabricating an object where C returns
//    GIT_ENOTFOUND (odb.c:59-66).
//  - commit/tag signature and tag-name lines without a trailing
//    newline were accepted where C requires the '\n' ender
//    (signature.c:330-332, tag.c:119-121).
//  - form-feed (0x0C) was counted in neither bucket of binary
//    detection; C counts it printable (str.c:1264-1276).
//  - pack-backend enumeration deduplicated OIDs across packs where C
//    yields duplicates (odb_pack.c pack_backend__foreach).
//  - loose object writes forced exact mode 0444, bypassing the
//    process umask that C's open(2) applies (futils.c:26-51).
//  - the quick commit parse rejected negative committer timestamps
//    that C's git__strntol64 accepts (util.c:58-63).
//  - symlink blob creation decoded the link target as UTF-8,
//    corrupting non-UTF-8 targets (C hashes the raw p_readlink bytes,
//    blob.c:163-180).
public sealed partial class ObjectsLowRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public ObjectsLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectsLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _objectsDir = Path.Combine(_tempDir, "objects");
        Directory.CreateDirectory(_objectsDir);
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

    // ---- packlike wrapped size is rejected ----

    [Fact]
    public async Task LooseObject_PacklikeWrappedSize_ThrowsGitException()
    {
        // Header: 0x9F (type 1 = commit, size 15, continuation) + eight 0xFF
        // + one 0x7F. The 9th continuation byte at shift 60 wraps the size to
        // 0xFFFFFFFFFFFFFFFF in C's unsigned size_t, which trips
        // GIT_ADD_SIZET_OVERFLOW in read_loose_packlike (odb_loose.c:258-262);
        // a signed-long accumulation would produce -1 and accept the object
        // (surfacing a negative size via ReadHeaderAsync).
        var oid = GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1);
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] header = [0x9F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F];
        await File.WriteAllBytesAsync(path, header, cancellationToken: TestContext.Current.CancellationToken);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        await Assert.ThrowsAsync<GitException>(
            () => backend.ReadHeaderAsync(oid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LooseObject_PacklikeNormalSize_StillParses()
    {
        // Control: a small packlike header (0x13 = type 1, size 3, no
        // continuation) still parses.
        var oid = GitOid.Parse("2222222222222222222222222222222222222222", GitHashAlgorithmKind.Sha1);
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [0x13], cancellationToken: TestContext.Current.CancellationToken);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitObjectHeader? header = await backend.ReadHeaderAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Commit, header.Value.Type);
        Assert.Equal(3, header.Value.Size);
    }

    // ---- SHA-1 repo does not fabricate the SHA-256 empty tree ----

    [Fact]
    public async Task ObjectDb_Sha1Repo_DoesNotFabricateSha256EmptyTree()
    {
        string repoPath = Path.Combine(_tempDir, "repo5");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // C (odb.c:59-66, odb_hardcoded_type): only the SHA-1 empty tree is
        // hardcoded in 1.9.4 — a SHA-256 OID in a SHA-1 repo is GIT_ENOTFOUND.
        // Fabricating an empty tree for the SHA-256 OID would be wrong.
        GitObject? obj = await repo.ObjectLookupAsync(
            GitOid.EmptyTreeSha256, TestContext.Current.CancellationToken);
        Assert.Null(obj);
    }

    [Fact]
    public async Task ObjectDb_Sha1Repo_StillFabricatesSha1EmptyTree()
    {
        // Control: the SHA-1 empty tree hardcode is preserved.
        string repoPath = Path.Combine(_tempDir, "repo5b");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        GitObject? obj = await repo.ObjectLookupAsync(
            GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Tree, obj.Type);
    }

    // ---- signature/name lines without a trailing newline are rejected ----

    private const string TreeOidHex = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    [Fact]
    public void Commit_CommitterLineWithoutTrailingNewline_IsRejected()
    {
        // C's git_signature__parse requires the '\n' ender ("no newline
        // given", signature.c:330-332). Parsing the
        // newline-truncated line would accept the commit.
        string raw = $"tree {TreeOidHex}\n" +
                     "author A <a@b> 1 +0000\n" +
                     "committer C <c@d> 2 +0000"; // no trailing newline
        Assert.Throws<GitException>(() => Commit.Parse(
            null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Commit_CommitterLineWithTrailingNewline_IsAccepted()
    {
        // Control: the same commit with a trailing newline parses.
        string raw = $"tree {TreeOidHex}\n" +
                     "author A <a@b> 1 +0000\n" +
                     "committer C <c@d> 2 +0000\n" +
                     "\n" +
                     "msg\n";
        var commit = Commit.Parse(null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1);
        Assert.Equal("C", commit.Committer.Name);
    }

    [Fact]
    public void Tag_TaggerLineWithoutTrailingNewline_IsRejected()
    {
        string raw = $"object {TreeOidHex}\n" +
                     "type tree\n" +
                     "tag v1\n" +
                     "tagger C <c@d> 2 +0000"; // no trailing newline
        Assert.Throws<GitException>(() => GitTag.Parse(
            null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Tag_NameWithoutTrailingNewline_IsRejected()
    {
        // C's tag_parse requires the '\n' after the tag name ("failed to
        // parse tag: object too short", tag.c:119-121).
        string raw = $"object {TreeOidHex}\n" +
                     "type tree\n" +
                     "tag v1"; // no trailing newline
        Assert.Throws<GitException>(() => GitTag.Parse(
            null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Tag_WithTrailingNewlines_IsAccepted()
    {
        // Control: a well-formed tag parses.
        string raw = $"object {TreeOidHex}\n" +
                     "type tree\n" +
                     "tag v1\n" +
                     "tagger C <c@d> 2 +0000\n" +
                     "\n" +
                     "msg\n";
        var tag = GitTag.Parse(null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1);
        Assert.Equal("v1", tag.Name);
    }

    // ---- form-feed counts as printable in binary detection ----

    [Fact]
    public void Blob_FormFeedHeavyContent_IsNotBinary()
    {
        // 5000 FF bytes + 20 non-space control bytes: C counts FF printable
        // → (5000 >> 7) = 39 >= 20 → not binary (str.c:1264-1276).
        // Counting FF in neither bucket gives 0 < 20 → binary.
        byte[] data = new byte[5020];
        Array.Fill(data, (byte)0x0C, 0, 5000);
        Array.Fill(data, (byte)0x01, 5000, 20);

        Assert.False(GitBlob.IsBinaryBytes(data));
    }

    [Fact]
    public void Blob_ControlBytesWithoutFormFeed_IsBinary()
    {
        // Control: without the FF bytes, 20 control bytes over 5000 printable
        // 'a' bytes is still not binary; 5000 control bytes is binary.
        byte[] mostlyPrintable = new byte[5020];
        Array.Fill(mostlyPrintable, (byte)'a', 0, 5000);
        Array.Fill(mostlyPrintable, (byte)0x01, 5000, 20);
        Assert.False(GitBlob.IsBinaryBytes(mostlyPrintable));

        byte[] mostlyControl = new byte[5020];
        Array.Fill(mostlyControl, (byte)0x01);
        Assert.True(GitBlob.IsBinaryBytes(mostlyControl));
    }

    // ---- pack enumeration yields duplicates across packs ----

    [Fact]
    public async Task PackBackend_Enumerate_YieldsDuplicateOidsAcrossPacks()
    {
        string repoPath = Path.Combine(_tempDir, "repo8");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        GitOid blobA = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "aaa"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobB = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "bbb"u8.ToArray(), TestContext.Current.CancellationToken);

        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);

        // Pack 1: {blobA}; pack 2: {blobA, blobB} — different contents, so
        // different pack names, with blobA present in both.
        using (GitPackWriter pb1 = repo.NewPackWriter())
        {
            await pb1.InsertAsync(blobA, TestContext.Current.CancellationToken);
            await pb1.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);
        }

        using (GitPackWriter pb2 = repo.NewPackWriter())
        {
            await pb2.InsertAsync(blobA, TestContext.Current.CancellationToken);
            await pb2.InsertAsync(blobB, TestContext.Current.CancellationToken);
            await pb2.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);
        }

        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(TestContext.Current.CancellationToken);

        var oids = new List<GitOid>();
        await foreach (GitOid oid in backend.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            oids.Add(oid);
        }

        // C (odb_pack.c pack_backend__foreach): no dedup — blobA is reported
        // once per pack. De-duplicating through a HashSet would collapse it to one.
        Assert.Equal(2, oids.Count(o => o.Equals(blobA)));
        Assert.Equal(1, oids.Count(o => o.Equals(blobB)));
    }

    // ---- loose object write applies the process umask ----

    [Fact]
    public async Task LooseObjectWrite_AppliesProcessUmask()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX umask semantics
        }

        // C creates the loose object via open(2) with mode 0444, which the
        // kernel masks with the process umask (futils.c:26-51). A same-process
        // probe created with UnixCreateMode 0444 shares the ambient umask, so
        // the assertion is umask-independent and never touches the
        // process-global umask — safe under MTP's parallel tests. A regression
        // to default 0666-based creation would produce a different mode from
        // the probe under any ambient umask (e.g. 0644 vs 0444 under 022).
        string repoPath = Path.Combine(_tempDir, "repo9");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);

        string probePath = Path.Combine(repoPath, "objects", "probe");
        using (var fs = new FileStream(probePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        }))
        {
        }

        string path = Path.Combine(repoPath, "objects", oid.ToPathString());
        const int PermMask = 0x1FF;
        int expected = (int)File.GetUnixFileMode(probePath) & PermMask;
        int actual = (int)File.GetUnixFileMode(path) & PermMask;
        Assert.Equal(expected, actual);
    }

    // ---- quick commit parse accepts negative timestamps ----

    [Fact]
    public void Commit_ParseQuick_NegativeTimestamp_IsAccepted()
    {
        // C's git__strntol64 accepts a leading '-' (util.c:58-63), so a
        // negative committer timestamp parses in the quick path. Requiring
        // an initial digit would throw 'invalid Unix timestamp'
        // where the full parse accepts the same line.
        string raw = $"tree {TreeOidHex}\n" +
                     "author A <a@b> 1 +0000\n" +
                     "committer C <c@d> -1 +0000\n" +
                     "\n" +
                     "msg\n";
        var commit = Commit.Parse(null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1);

        (long time, _) = Commit.ParseQuick(commit, GitHashAlgorithmKind.Sha1);
        Assert.Equal(-1, time);
    }

    [Fact]
    public void Commit_ParseQuick_PositiveTimestamp_StillParses()
    {
        // Control: the normal positive-timestamp path is unchanged.
        string raw = $"tree {TreeOidHex}\n" +
                     "author A <a@b> 1 +0000\n" +
                     "committer C <c@d> 1700000000 +0000\n" +
                     "\n" +
                     "msg\n";
        var commit = Commit.Parse(null, GitOid.Empty, Encoding.UTF8.GetBytes(raw), GitHashAlgorithmKind.Sha1);

        (long time, _) = Commit.ParseQuick(commit, GitHashAlgorithmKind.Sha1);
        Assert.Equal(1700000000, time);
    }

    // ---- symlink blob creation keeps raw link bytes ----

    [Fact]
    public async Task BlobCreate_SymlinkWithNonUtf8Target_HashesRawBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation needs privileges
        }

        string repoPath = Path.Combine(_tempDir, "repo12");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Raw link bytes 61 FF FE 80 62 — not valid UTF-8. .NET's
        // FileInfo.LinkTarget decodes them lossily (U+FFFD), so a string
        // path would hash a different blob than C's raw p_readlink bytes.
        // File.CreateSymbolicLink would UTF-8-encode the string, so the
        // link is created via raw symlink(2).
        string linkPath = Path.Combine(repoPath, "link");
        byte[] rawTarget = [0x61, 0xFF, 0xFE, 0x80, 0x62];
        if (!TryCreateRawSymlink(linkPath, rawTarget))
        {
            return; // filesystem without symlink support
        }

        GitOid oid = await repo.BlobCreateFromDiskAsync(linkPath, TestContext.Current.CancellationToken);

        // Expected: sha1("blob 5\0" + 61 FF FE 80 62).
        byte[] header = Encoding.ASCII.GetBytes($"blob {rawTarget.Length}\0");
        byte[] full = new byte[header.Length + rawTarget.Length];
        header.CopyTo(full, 0);
        rawTarget.CopyTo(full, header.Length);
        var expected = GitOid.FromRaw(SHA1.HashData(full), GitHashAlgorithmKind.Sha1);

        Assert.Equal(expected, oid);
    }

    /// <summary>
    /// Creates a symlink whose target is the given raw bytes (symlink(2)),
    /// bypassing .NET's UTF-8 string encoding. Returns false when the
    /// filesystem does not support symlinks.
    /// </summary>
    private static bool TryCreateRawSymlink(string linkPath, byte[] rawTarget)
    {
        byte[] target = new byte[rawTarget.Length + 1];
        rawTarget.CopyTo(target, 0);
        byte[] link = new byte[System.Text.Encoding.UTF8.GetByteCount(linkPath) + 1];
        System.Text.Encoding.UTF8.GetBytes(linkPath, link);
        return Native.SymlinkCall(target, link) == 0;
    }

    private static partial class Native
    {
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.UserDirectories)]
        [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "symlink", SetLastError = true)]
        public static partial int SymlinkCall(byte[] target, byte[] linkpath);
    }
}

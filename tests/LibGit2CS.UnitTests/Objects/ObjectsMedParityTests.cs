using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Parity tests for the objects subsystem. </summary>
public sealed class ObjectsMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public ObjectsMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectsMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> InitRepoAsync()
        => await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());

    private static string CommitBuffer(GitOid tree, GitOid parent, GitSignature author, GitSignature committer, string message)
    {
        var sb = new StringBuilder();
        sb.Append("tree ").Append(tree).Append('\n');
        if (!parent.IsZero)
        {
            sb.Append("parent ").Append(parent).Append('\n');
        }

        sb.Append("author ").Append(author.ToString()).Append('\n');
        sb.Append("committer ").Append(committer.ToString()).Append('\n');
        sb.Append('\n');
        sb.Append(message);
        return sb.ToString();
    }

    private static string TagBuffer(GitOid target, string type, string tagName, string? taggerLine, string message)
    {
        var sb = new StringBuilder();
        sb.Append("object ").Append(target).Append('\n');
        sb.Append("type ").Append(type).Append('\n');
        sb.Append("tag ").Append(tagName).Append('\n');
        if (taggerLine is not null)
        {
            sb.Append("tagger ").Append(taggerLine).Append('\n');
        }

        sb.Append('\n');
        sb.Append(message);
        return sb.ToString();
    }

    // ── RawHeader excludes the blank separator line ───────────────────

    [Fact]
    public async Task Commit_RawHeader_ExcludesBlankLine()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = (await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken));
        GitSignature sig = TestSig();

        string buffer = CommitBuffer(tree, default, sig, sig, "msg\n");
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Commit, Encoding.UTF8.GetBytes(buffer), TestContext.Current.CancellationToken);

        Commit commit = (await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken))!;
        Assert.Equal($"tree {tree}\nauthor {sig}\ncommitter {sig}\n", commit.RawHeader);
        Assert.Equal("msg\n", commit.RawMessage);
    }

    // ── Summary preserves interior whitespace runs ────────────────────

    [Fact]
    public async Task Commit_Summary_PreservesNonNewlineWhitespaceRuns()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();

        // C: "a  b\n\nrest" → "a  b" (two spaces kept — only runs containing
        // a newline collapse).
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Commit, Encoding.UTF8.GetBytes(CommitBuffer(tree, default, sig, sig, "a  b\n\nrest\n")), TestContext.Current.CancellationToken);
        Commit commit = (await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken))!;
        Assert.Equal("a  b", commit.Summary);

        // "a\tb\n\nrest" → "a\tb" (tab kept).
        GitOid oid2 = await repo.ObjectWriteAsync(GitObjectType.Commit, Encoding.UTF8.GetBytes(CommitBuffer(tree, default, sig, sig, "a\tb\n\nrest\n")), TestContext.Current.CancellationToken);
        Commit commit2 = (await repo.ObjectLookupAsync<Commit>(oid2, TestContext.Current.CancellationToken))!;
        Assert.Equal("a\tb", commit2.Summary);
    }

    [Fact]
    public async Task Commit_Summary_StopsAtWhitespaceOnlyLine()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();

        // C: "line1\n   \nline2" → "line1" (paragraph ends at the
        // whitespace-only line).
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Commit, Encoding.UTF8.GetBytes(CommitBuffer(tree, default, sig, sig, "line1\n   \nline2\n")), TestContext.Current.CancellationToken);
        Commit commit = (await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken))!;
        Assert.Equal("line1", commit.Summary);
    }

    // ── empty tag name accepted ───────────────────────────────────────

    [Fact]
    public async Task Tag_EmptyName_Accepted()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);

        string buffer = TagBuffer(tree, "tree", "", null, "msg\n");
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Tag, Encoding.UTF8.GetBytes(buffer), TestContext.Current.CancellationToken);

        GitTag tag = (await repo.ObjectLookupAsync<GitTag>(oid, TestContext.Current.CancellationToken))!;
        Assert.Equal("", tag.Name);
    }

    // ── tag message extraction ────────────────────────────────────────

    [Fact]
    public async Task Tag_NonTaggerLineAfterName_Throws()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);

        // "tag v1\nmsg\n" — C: the tagger parse runs and fails on the
        // non-"tagger " line (tag.c:135-141). NOTE: no blank line — the
        // tagger branch is what rejects this input.
        string buffer = $"object {tree}\ntype tree\ntag v1\nmsg\n";
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Tag, Encoding.UTF8.GetBytes(buffer), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ObjectLookupAsync<GitTag>(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("failed to parse signature", ex.Message);
    }

    [Fact]
    public async Task Tag_NoBlankLineBeforeMessage_Throws()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);

        // C: "…tagger …\nmsg here\n" → "tag contains no message".
        string buffer = $"object {tree}\ntype tree\ntag v1\ntagger {TestSig()}\nmsg here\n";
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Tag, Encoding.UTF8.GetBytes(buffer), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ObjectLookupAsync<GitTag>(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("tag contains no message", ex.Message);
    }

    [Fact]
    public async Task Tag_JunkBetweenTaggerAndMessage_Skipped()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid tree = await repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);

        // C: junk between the tagger and "\n\n" is skipped; the message
        // starts after the first "\n\n" → "msg\n".
        string buffer = $"object {tree}\ntype tree\ntag v1\ntagger {TestSig()}\njunk\n\nmsg\n";
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Tag, Encoding.UTF8.GetBytes(buffer), TestContext.Current.CancellationToken);

        GitTag tag = (await repo.ObjectLookupAsync<GitTag>(oid, TestContext.Current.CancellationToken))!;
        Assert.Equal("msg\n", tag.Message);
    }

    // ── raw tree mode round-trips ─────────────────────────────────────

    [Fact]
    public async Task Tree_NonCanonicalMode_RawModePreservedAndRewritten()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Craft a tree with the non-canonical mode 100664.
        byte[] raw = new byte[6 + 1 + 4 + 1 + 20];
        System.Text.Encoding.ASCII.GetBytes("100664").CopyTo(raw, 0);
        raw[6] = (byte)' ';
        "file"u8.CopyTo(raw.AsSpan(7));
        raw[11] = 0;
        blob.RawBytes.CopyTo(raw.AsSpan(12));

        GitOid treeOid = await repo.ObjectWriteAsync(GitObjectType.Tree, raw, TestContext.Current.CancellationToken);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        GitTreeEntry entry = tree.EntryByIndex(0) ?? throw new InvalidOperationException();
        // 0o100664 == 33204 — the raw octal mode bits as stored.
        Assert.Equal(unchecked((ushort)33204), entry.RawMode);
        Assert.Equal(GitFileMode.Regular, entry.Mode);

        // A builder seeded from the tree re-writes the RAW mode bytes
        // (tree.c:540-542) — the rewritten tree is byte-identical.
        using GitTreeBuilder bld = repo.NewTreeBuilder(tree);
        GitOid rewritten = await bld.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(treeOid, rewritten);
    }

    // ── treebuilder failure codes ─────────────────────────────────────

    [Fact]
    public async Task TreeBuilder_RemoveMissing_ThrowsError()
    {
        await using GitRepository repo = await InitRepoAsync();
        using GitTreeBuilder bld = repo.NewTreeBuilder();

        // C (tree.c:842): tree_error returns -1 (GIT_ERROR).
        GitException ex = Assert.Throws<GitException>(() => bld.Remove("missing.txt"));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Tree, ex.Category);
    }

    [Fact]
    public async Task TreeBuilder_InvalidFilemode_ThrowsError()
    {
        await using GitRepository repo = await InitRepoAsync();
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await bld.InsertAsync("f", blob, (GitFileMode)0x8000, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Tree, ex.Category);
    }

    [Fact]
    public async Task TreeBuilder_WrongObjectType_ThrowsError()
    {
        await using GitRepository repo = await InitRepoAsync();
        using GitTreeBuilder bld = repo.NewTreeBuilder();

        // A blob OID in a tree slot fails the read-based type check
        // (git_object__is_valid, tree.c:501-502) — "invalid object specified".
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await bld.InsertAsync("f", blob, GitFileMode.Tree, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("invalid object specified", ex.Message);
    }

    // ── commit update_ref tip == first parent ────────────────────────

    [Fact]
    public async Task CommitCreate_UpdateRefTipNotFirstParent_ThrowsModified()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("f", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();

        // First commit on master.
        GitOid first = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = sig,
            Committer = sig,
            Message = "first",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Second commit, NOT updating the ref.
        GitOid second = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [first],
            Author = sig,
            Committer = sig,
            Message = "second",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // C (commit.c:113-117): the ref tip (first) is not the first parent
        // (second) → GIT_EMODIFIED, and the commit is NOT written.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = tree,
                Parents = [second],
                Author = sig,
                Committer = sig,
                Message = "third",
                UpdateRef = "refs/heads/master",
            }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Modified, ex.Code);
        Assert.Equal("failed to create commit: current tip is not the first parent", ex.Message);
    }

    // ── tag_create_from_buffer validates ─────────────────────────────

    [Fact]
    public async Task TagCreateFromBuffer_MalformedBuffer_Throws()
    {
        await using GitRepository repo = await InitRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.TagCreateFromBufferAsync("not a tag at all\n", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task TagCreateFromBuffer_WrongTargetType_Throws()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Buffer claims "type commit" but the target is a blob.
        string buffer = TagBuffer(blob, "commit", "v1", null, "msg\n");
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.TagCreateFromBufferAsync(buffer, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("the type for the given target is invalid", ex.Message);
    }

    [Fact]
    public async Task TagCreateFromBuffer_ValidBuffer_DerivesRefFromBuffer()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // The ref name comes from the tag line INSIDE the buffer (tag.c:402).
        string buffer = TagBuffer(blob, "blob", "frombuffer", null, "msg\n");
        GitOid oid = await repo.TagCreateFromBufferAsync(buffer, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? refr = await repo.ReferenceLookupAsync("refs/tags/frombuffer", TestContext.Current.CancellationToken);
        Assert.NotNull(refr);
        Assert.Equal(oid, ((GitDirectReference)refr!).Target);
    }

    // ── annotated commit error codes ─────────────────────────────────

    [Fact]
    public async Task AnnotatedCommitLookup_WrongType_ThrowsNotFound()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // C (object.c:124-128): a wrong-type OID → GIT_ENOTFOUND "the
        // requested type does not match the type in the ODB".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.AnnotatedCommitLookupAsync(blob, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("the requested type does not match the type in the ODB", ex.Message);
    }

    // ── commit-graph reader validations ──────────────────────────────

    [Fact]
    public async Task CommitGraph_UnknownChunkId_Throws()
    {
        // "ZZZZ" chunk — a genuinely unknown ID; C rejects it
        // ("unrecognized chunk ID"). GDA2/GDO2 are NOT unknown anymore —
        // see CommitGraph_Gda2AndGdo2Chunks_AcceptedAndSkipped below.
        byte[] data = BuildGraphWithChunkId(0x5A5A5A5A);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await OpenGraphAsync(data));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("unrecognized chunk ID", ex.Message);
    }

    [Fact]
    public async Task CommitGraph_Gda2AndGdo2Chunks_AcceptedAndSkipped()
    {
        // DIVERGENCE from the v1.9.4 baseline (forward-port of upstream
        // libgit2 main 2e3ec8d / PR #7271): GDA2 (generation data v2,
        // written by default by git >= 2.38) and GDO2 (overflow) chunks are
        // recognized and skipped instead of failing the open. Legacy GDAT
        // (git 2.35-2.37) stays rejected, matching upstream main.
        CommitGraph gda2 = await OpenGraphAsync(BuildGraphWithChunkId(0x47444132));
        Assert.Equal(1, gda2.NumCommits);
        Assert.NotNull(gda2.FindEntry(MinimalGraphCommitOid()));

        CommitGraph gdo2 = await OpenGraphAsync(BuildGraphWithChunkId(0x47444F32));
        Assert.Equal(1, gdo2.NumCommits);
        Assert.NotNull(gdo2.FindEntry(MinimalGraphCommitOid()));

        GitException gdat = await Assert.ThrowsAsync<GitException>(async () =>
            await OpenGraphAsync(BuildGraphWithChunkId(0x47444154)));
        Assert.Contains("unrecognized chunk ID", gdat.Message);
    }

    [Fact]
    public async Task CommitGraph_BadChecksum_OpenSucceedsLikeC()
    {
        // C (commit_graph.c:200-250, 344-362): the open path copies the
        // trailer checksum but NEVER verifies it — verification lives only
        // in the separate git_commit_graph_validate API. A corrupted
        // trailer must not fail the open.
        byte[] data = BuildMinimalGraph();
        data[^1] ^= 0xFF; // corrupt the trailer checksum

        CommitGraph graph = await OpenGraphAsync(data);
        Assert.Equal(1, graph.NumCommits);
    }

    [Fact]
    public async Task CommitGraph_NonMonotonicOidl_Throws()
    {
        // Two OIDL entries out of order: 0x01.. then 0x00.. — not increasing.
        byte[] data = BuildTwoCommitGraph();
        // Swap the two 20-byte OIDL entries.
        byte[] tmp = new byte[20];
        Array.Copy(data, 8 + (1 + 3) * 12 + 256 * 4, tmp, 0, 20);
        Array.Copy(data, 8 + (1 + 3) * 12 + 256 * 4 + 20, data, 8 + (1 + 3) * 12 + 256 * 4, 20);
        tmp.CopyTo(data, 8 + (1 + 3) * 12 + 256 * 4 + 20);
        FixChecksum(data);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await OpenGraphAsync(data));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("OID Lookup index is non-monotonic", ex.Message);
    }

    // ── ExtractSignature ─────────────────────────────────────────────

    [Fact]
    public void ExtractSignature_Unsigned_ThrowsNotFound()
    {
        byte[] raw = "tree 0123456789abcdef0123456789abcdef01234567\nauthor A <a@b.c> 1461698037 +0200\ncommitter A <a@b.c> 1461698037 +0200\n\nmsg\n"u8.ToArray();
        var commit = Commit.Parse(null, GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1), raw, GitHashAlgorithmKind.Sha1);

        // C (commit.c:919-921): "this commit is not signed", GIT_ENOTFOUND.
        GitException ex = Assert.Throws<GitException>(() => commit.ExtractSignature());
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("this commit is not signed", ex.Message);
    }

    [Fact]
    public void ExtractSignature_Signed_PreservesSeparator()
    {
        byte[] raw = "tree 0123456789abcdef0123456789abcdef01234567\nauthor A <a@b.c> 1461698037 +0200\ncommitter A <a@b.c> 1461698037 +0200\ngpgsig -----BEGIN PGP-----\n abc\n\nmsg\n"u8.ToArray();
        var commit = Commit.Parse(null, GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1), raw, GitHashAlgorithmKind.Sha1);

        (ReadOnlyMemory<byte> signedData, string? signature) = commit.ExtractSignature();
        Assert.Equal("-----BEGIN PGP-----\nabc", signature);

        // The blank separator line between the header and the message is
        // preserved (C: git_str_puts(signed_data, eol+1), commit.c:914).
        string data = Encoding.UTF8.GetString(signedData.Span);
        Assert.Contains("committer A <a@b.c> 1461698037 +0200\n\nmsg\n", data);
    }

    // ── corrupt object read downgrades EINVALID → GIT_ERROR ──────────

    [Fact]
    public async Task CorruptCommitRead_ThrowsError()
    {
        await using GitRepository repo = await InitRepoAsync();

        // Malformed commit (bad OID line) — the parser throws GIT_EINVALID,
        // which the read downgrades to -1 (object.c:165-172).
        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Commit, "tree notanoid\n"u8.ToArray(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    // ── message bytes are written verbatim (no ASCII '?') ────────────

    [Fact]
    public async Task CommitCreate_NonAsciiMessage_RawBytesPreserved()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("f", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();

        // C's git_commit_create takes UTF-8 message bytes and writes them verbatim; the managed string is encoded as UTF-8 to match (Latin-1 would write
        // 0xE9 for é and silently replace codepoints > U+00FF with '?').
        GitOid oid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = sig,
            Committer = sig,
            Message = "caf\u00e9\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitObject? obj = await repo.ObjectLookupAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        byte[] raw = obj!.Raw.ToArray();
        Assert.Contains((byte)0xC3, raw);
        Assert.Contains((byte)0xA9, raw);
        Assert.DoesNotContain((byte)0xE9, raw);
        Assert.DoesNotContain((byte)'?', raw);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void FixChecksum(byte[] data)
    {
        int checksumSize = 20;
        byte[] hash = SHA1.HashData(data.AsSpan(0, data.Length - checksumSize));
        hash.CopyTo(data, data.Length - checksumSize);
    }

    private async ValueTask<CommitGraph> OpenGraphAsync(byte[] data)
    {
        string dir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "commit-graph"), data, cancellationToken: TestContext.Current.CancellationToken);
        return (await CommitGraph.OpenAsync(Path.Combine(_tempDir, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;
    }

    /// <summary>The deterministic single-commit OID built by the graph helpers above.</summary>
    private static GitOid MinimalGraphCommitOid()
    {
        Span<byte> raw = stackalloc byte[20];
        raw[0] = 0x01;
        for (int i = 1; i < 20; i++)
        {
            raw[i] = (byte)(i * 7);
        }

        return GitOid.FromRaw(raw, GitHashAlgorithmKind.Sha1);
    }

    /// <summary>Minimal 1-commit graph (valid checksum).</summary>
    private static byte[] BuildMinimalGraph()
    {
        int oidSize = 20;
        int numChunks = 3;
        int headerSize = 8 + (1 + numChunks) * 12;
        int oidfSize = 256 * 4;
        int oidlSize = oidSize;
        int cdatSize = oidSize + 16;
        int trailerOff = headerSize + oidfSize + oidlSize + cdatSize;
        byte[] buf = new byte[trailerOff + oidSize];
        using var ms = new MemoryStream(buf, 0, buf.Length, writable: true);
        ms.WriteByte((byte)'C');
        ms.WriteByte((byte)'G');
        ms.WriteByte((byte)'P');
        ms.WriteByte((byte)'H');
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte((byte)numChunks);
        ms.WriteByte(0);
        WriteChunkEntry(ms, 0x4F494446, headerSize);
        WriteChunkEntry(ms, 0x4F49444C, headerSize + oidfSize);
        WriteChunkEntry(ms, 0x43444154, headerSize + oidfSize + oidlSize);
        WriteChunkEntry(ms, 0, trailerOff);
        for (int i = 0; i < 256; i++)
        {
            WriteUInt32BE(ms, 1u);
        }

        byte[] oid = new byte[oidSize];
        oid[0] = 0x01;
        for (int i = 1; i < oidSize; i++)
        {
            oid[i] = (byte)(i * 7);
        }

        ms.Write(oid, 0, oidSize);
        ms.Write(new byte[cdatSize], 0, cdatSize);
        FixChecksum(buf);
        return buf;
    }

    /// <summary>Graph with a 4th chunk carrying the given ID.</summary>
    private static byte[] BuildGraphWithChunkId(uint chunkId)
    {
        // Rebuild with 4 chunks: insert the extra chunk header and a dummy chunk.
        int oidSize = 20;
        int numChunks = 4;
        int headerSize = 8 + (1 + numChunks) * 12;
        int oidfSize = 256 * 4;
        int oidlSize = oidSize;
        int cdatSize = oidSize + 16;
        int dummySize = 4;
        int trailerOff = headerSize + oidfSize + oidlSize + cdatSize + dummySize;
        byte[] buf = new byte[trailerOff + oidSize];
        using var ms = new MemoryStream(buf, 0, buf.Length, writable: true);
        ms.WriteByte((byte)'C');
        ms.WriteByte((byte)'G');
        ms.WriteByte((byte)'P');
        ms.WriteByte((byte)'H');
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte((byte)numChunks);
        ms.WriteByte(0);
        WriteChunkEntry(ms, 0x4F494446, headerSize);
        WriteChunkEntry(ms, 0x4F49444C, headerSize + oidfSize);
        WriteChunkEntry(ms, 0x43444154, headerSize + oidfSize + oidlSize);
        WriteChunkEntry(ms, chunkId, headerSize + oidfSize + oidlSize + cdatSize);
        WriteChunkEntry(ms, 0, trailerOff);
        // Correct cumulative fanout for the single commit below (first byte
        // 0x01): fanout[0]=0, fanout[1..255]=1 — required for FindEntry.
        for (int i = 0; i < 256; i++)
        {
            WriteUInt32BE(ms, i < 1 ? 0u : 1u);
        }

        byte[] oid = new byte[oidSize];
        oid[0] = 0x01;
        for (int i = 1; i < oidSize; i++)
        {
            oid[i] = (byte)(i * 7);
        }

        ms.Write(oid, 0, oidSize);
        ms.Write(new byte[cdatSize], 0, cdatSize);
        ms.Write(new byte[dummySize], 0, dummySize);
        FixChecksum(buf);
        return buf;
    }

    /// <summary>2-commit graph (valid checksum; entries in order).</summary>
    private static byte[] BuildTwoCommitGraph()
    {
        int oidSize = 20;
        int numChunks = 3;
        int headerSize = 8 + (1 + numChunks) * 12;
        int oidfSize = 256 * 4;
        int oidlSize = oidSize * 2;
        int cdatSize = (oidSize + 16) * 2;
        int trailerOff = headerSize + oidfSize + oidlSize + cdatSize;
        byte[] buf = new byte[trailerOff + oidSize];
        using var ms = new MemoryStream(buf, 0, buf.Length, writable: true);
        ms.WriteByte((byte)'C');
        ms.WriteByte((byte)'G');
        ms.WriteByte((byte)'P');
        ms.WriteByte((byte)'H');
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte((byte)numChunks);
        ms.WriteByte(0);
        WriteChunkEntry(ms, 0x4F494446, headerSize);
        WriteChunkEntry(ms, 0x4F49444C, headerSize + oidfSize);
        WriteChunkEntry(ms, 0x43444154, headerSize + oidfSize + oidlSize);
        WriteChunkEntry(ms, 0, trailerOff);
        for (int i = 0; i < 256; i++)
        {
            WriteUInt32BE(ms, 2u);
        }

        byte[] o1 = new byte[oidSize];
        o1[0] = 0x01;
        byte[] o2 = new byte[oidSize];
        o2[0] = 0x02;
        for (int i = 1; i < oidSize; i++)
        {
            o1[i] = (byte)(i * 7);
            o2[i] = (byte)(i * 11);
        }

        ms.Write(o1, 0, oidSize);
        ms.Write(o2, 0, oidSize);
        ms.Write(new byte[cdatSize], 0, cdatSize);
        FixChecksum(buf);
        return buf;
    }

    private static void WriteChunkEntry(Stream ms, uint id, long offset)
    {
        WriteUInt32BE(ms, id);
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            ms.WriteByte((byte)((offset >> shift) & 0xFF));
        }
    }

    private static void WriteUInt32BE(Stream ms, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, value);
        ms.Write(b);
    }
}

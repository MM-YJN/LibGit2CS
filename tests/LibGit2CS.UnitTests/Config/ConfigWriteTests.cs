using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Config;

public sealed class ConfigWriteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigWriteTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config");
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
    public async Task SetString_NewKey_WritesToFile()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("[core]", content);
        Assert.Contains("bare = false", content);
    }

    [Fact]
    public async Task SetAsync_NullValue_DeletesKey()
    {
        // Regression pin: SetAsync(key, null) deletes the key — the byte[]→ReadOnlyMemory<byte> implicit conversion must not coerce the null
        // branch into an empty-value write.
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", null, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("bare =", content);
        Assert.Null(await backend.GetAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetString_MultipleKeys_GroupsBySection()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.filemode", "true", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("user.name", "Alice", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("[core]", content);
        Assert.Contains("bare = false", content);
        Assert.Contains("filemode = true", content);
        Assert.Contains("[user]", content);
        Assert.Contains("name = Alice", content);
    }

    [Fact]
    public async Task SetString_ExistingKey_UpdatesValue()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "true", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("bare = true", content);
        Assert.DoesNotContain("bare = false", content);
    }

    [Fact]
    public async Task SetBool_WritesTrueOrFalse()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "true", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.logallrefupdates", "false", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("bare = true", content);
        Assert.Contains("logallrefupdates = false", content);
    }

    [Fact]
    public async Task Delete_RemovesKey()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.filemode", "true", cancellationToken: TestContext.Current.CancellationToken);

        await backend.DeleteKeyAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("bare", content);
        Assert.Contains("filemode = true", content);
    }

    [Fact]
    public async Task RoundTrip_WriteThenRead()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.filemode", "true", cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("user.name", "Alice", cancellationToken: TestContext.Current.CancellationToken);

        // Re-open and read back.
        var backend2 = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend2.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("false", (await backend2.GetAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken))!.Value.Value);
        Assert.Equal("true", (await backend2.GetAsync("core.filemode", cancellationToken: TestContext.Current.CancellationToken))!.Value.Value);
        Assert.Equal("Alice", (await backend2.GetAsync("user.name", cancellationToken: TestContext.Current.CancellationToken))!.Value.Value);
    }

    [Fact]
    public async Task Configuration_SetBool_RoundTrips()
    {
        await using var config = new GitConfiguration(new GitContext());
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await config.SetBoolAsync("core.bare", false, cancellationToken: TestContext.Current.CancellationToken);
        await config.SetBoolAsync("core.filemode", true, cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("user.name", "Bob", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await config.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await config.GetBoolAsync("core.filemode", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Bob", await config.GetStringAsync("user.name", cancellationToken: TestContext.Current.CancellationToken));

        // Verify the file was written.
        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("bare = false", content);
        Assert.Contains("filemode = true", content);
        Assert.Contains("name = Bob", content);
    }

    [Fact]
    public async Task Configuration_SetInt_RoundTrips()
    {
        await using var config = new GitConfiguration(new GitContext());
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await config.SetIntAsync("core.repositoryformatversion", 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await config.GetIntAsync("core.repositoryformatversion", cancellationToken: TestContext.Current.CancellationToken));

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("repositoryformatversion = 0", content);
    }

    // ── Formatting-preserving write, multivar, escaping, lock ───────────

    [Fact]
    public async Task Set_PreservesCommentsAndFormatting()
    {
        // Write an initial config with comments.
        await File.WriteAllTextAsync(_configPath,
            "# A comment\n" +
            "[core]\n" +
            "\tbare = false\n" +
            "\t# inline comment\n" +
            "\tfilemode = true\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.bare", "true", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        // Comment should be preserved.
        Assert.Contains("# A comment", content);
        Assert.Contains("# inline comment", content);
        // The value should be updated.
        Assert.Contains("bare = true", content);
        Assert.DoesNotContain("bare = false", content);
        // Other entries preserved.
        Assert.Contains("filemode = true", content);
    }

    [Fact]
    public async Task Set_NewSection_AppendsAtEnd()
    {
        await File.WriteAllTextAsync(_configPath,
            "[core]\n" +
            "\tbare = false\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("user.name", "Alice", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("[core]", content);
        Assert.Contains("[user]", content);
        Assert.Contains("name = Alice", content);
    }

    [Fact]
    public async Task Set_ValueWithSpecialChars_EscapesCorrectly()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("user.name", "Alice \"bob\" \\test", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        // The value should be escaped: " �?\", \ �?\\
        Assert.Contains("Alice \\\"bob\\\" \\\\test", content);
    }

    [Fact]
    public async Task Set_ValueWithTab_EscapesAsT()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.description", "tab\there", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        // The value's tab should be escaped as \t (the line's leading tab
        // for indentation is expected and unrelated).
        Assert.Contains("\\t", content);
        // The escaped form should be "tab\there" �?"tab\\there" in the file.
        Assert.Contains("tab\\there", content);
    }

    [Fact]
    public async Task Set_ValueWithNewline_EscapesAsN()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.description", "line1\nline2", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("\\n", content);
    }

    [Fact]
    public async Task SetMulti_UpdatesMatchingValues()
    {
        await File.WriteAllTextAsync(_configPath,
            "[remote \"origin\"]\n" +
            "\turl = https://old1.com\n" +
            "\turl = https://old2.com\n" +
            "\turl = https://other.com\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetMultiAsync("remote.origin.url", new Regex("https://old.*\\.com"), "https://new.com", cancellationToken: TestContext.Current.CancellationToken);

        await using var config = new GitConfiguration(new GitContext());
        var backend2 = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend2, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> urls = await config.GetMultiAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("https://new.com", urls);
        Assert.Contains("https://other.com", urls);
        // The two matching entries should have been replaced.
        int newCount = urls.Count(u => u == "https://new.com");
        Assert.Equal(2, newCount);
    }

    [Fact]
    public async Task DeleteMulti_RemovesMatchingValues()
    {
        await File.WriteAllTextAsync(_configPath,
            "[remote \"origin\"]\n" +
            "\turl = https://del.com\n" +
            "\turl = https://keep.com\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.DeleteMultiAsync("remote.origin.url", new Regex("https://del\\.com"), cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("https://del.com", content);
        Assert.Contains("https://keep.com", content);
    }

    [Fact]
    public async Task Subsection_CasePreserved()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("branch.MyFeature.merge", "refs/heads/MyFeature", cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("[branch \"MyFeature\"]", content);
        Assert.Contains("merge = refs/heads/MyFeature", content);
    }

    [Fact]
    public async Task Configuration_SetMulti_RoundTrips()
    {
        await using var config = new GitConfiguration(new GitContext());
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await config.SetStringAsync("remote.origin.url", "https://first.com", cancellationToken: TestContext.Current.CancellationToken);
        await config.SetMultiAsync("remote.origin.url", new Regex("https://.*"), "https://replaced.com", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> urls = await config.GetMultiAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("https://replaced.com", urls);
    }

    [Fact]
    public async Task Configuration_DeleteMulti_RoundTrips()
    {
        // Write a config with two user.email entries (multivar).
        await File.WriteAllTextAsync(_configPath,
            "[user]\n" +
            "\temail = keep@example.com\n" +
            "\temail = delete@example.com\n", cancellationToken: TestContext.Current.CancellationToken);

        await using var config = new GitConfiguration(new GitContext());
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await config.DeleteMultiAsync("user.email", new Regex("delete@.*"), cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> emails = await config.GetMultiAsync("user.email", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("keep@example.com", emails);
        Assert.DoesNotContain("delete@example.com", emails);
    }

    [Fact]
    public async Task Lock_Unlock_Commit_WritesChanges()
    {
        await File.WriteAllTextAsync(_configPath,
            "[core]\n" +
            "\tbare = false\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await backend.LockAsync(cancellationToken: TestContext.Current.CancellationToken);
        // While locked, set writes go to the locked content (in-memory).
        // For a simple lock/unlock with commit, the frozen content is written.
        await backend.UnlockAsync(commit: true, cancellationToken: TestContext.Current.CancellationToken);

        // The file should still exist and be readable.
        string content = await File.ReadAllTextAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("bare = false", content);
    }

    [Fact]
    public async Task Configuration_Lock_Commit_Atomic()
    {
        await using var config = new GitConfiguration(new GitContext());
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await config.SetStringAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);

        using (GitConfigTransaction tx = await config.LockAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            // Within the transaction, set operates on locked content.
            await config.SetBoolAsync("core.bare", true, cancellationToken: TestContext.Current.CancellationToken);
            await tx.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // After commit, the change should be persisted.
        await using var config2 = new GitConfiguration(new GitContext());
        var backend2 = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config2.AddBackendAsync(backend2, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await config2.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Configuration_Lock_Rollback_Discards()
    {
        await using var config = new GitConfiguration(new GitContext());
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config.AddBackendAsync(backend, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await config.SetStringAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);

        using (GitConfigTransaction tx = await config.LockAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            await config.SetBoolAsync("core.bare", true, cancellationToken: TestContext.Current.CancellationToken);
            // Dispose without Commit �?rollback.
        }

        // After rollback, the original value should be preserved.
        await using var config2 = new GitConfiguration(new GitContext());
        var backend2 = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await config2.AddBackendAsync(backend2, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(await config2.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lock_AlreadyLocked_ThrowsLocked()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await backend.LockAsync(cancellationToken: TestContext.Current.CancellationToken);
        // C (filebuf.c:44-51): a second lock attempt hits the existing .lock
        // file �?GIT_ELOCKED, not GIT_EEXISTS.
        GitException ex = await Assert.ThrowsAsync<GitException>(() => backend.LockAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Locked, ex.Code);
        await backend.UnlockAsync(commit: false, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Set_ReadOnlyBackend_Throws()
    {
        var memBackend = new MemoryConfigBackend("[core]\nbare = false\n");
        await memBackend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotSupportedException>(() => memBackend.SetAsync("core.bare", "true", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => memBackend.DeleteKeyAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => memBackend.LockAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Set_SnapshotBackend_Throws()
    {
        var sourceBackend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await sourceBackend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await sourceBackend.SetAsync("core.bare", "false", cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = (IConfigBackend)sourceBackend.Snapshot();
        await snapshot.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotSupportedException>(() => snapshot.SetAsync("core.bare", "true", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Byte-preservation: values round-trip as raw bytes ────

    [Fact]
    public async Task Set_Utf8Value_WritesUtf8Bytes()
    {
        // user-supplied string values are UTF-8-encoded on write (git convention) — 'é' appears as C3 A9, NOT the one-byte E9.
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.name", "café", cancellationToken: TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(bytes, [0xC3, 0xA9]), $"expected UTF-8 C3 A9 in {Convert.ToHexString(bytes)}");
        Assert.False(bytes.Contains((byte)0xE9), "must not encode as the old single byte 0xE9");
    }

    [Fact]
    public async Task SetMulti_Utf8Value_WritesUtf8Bytes()
    {
        await File.WriteAllBytesAsync(_configPath,
            "[remote \"origin\"]\n\turl = https://old.com\n"u8.ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetMultiAsync("remote.origin.url", new Regex("https://.*"), "https://café.com", cancellationToken: TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(bytes, [0xC3, 0xA9]), $"expected UTF-8 C3 A9 in {Convert.ToHexString(bytes)}");
        Assert.False(bytes.Contains((byte)0xE9), "must not encode as the old single byte 0xE9");
    }

    [Fact]
    public async Task SetBytes_RawE9Value_WritesRawE9()
    {
        // Byte-primary surface: raw bytes written verbatim.
        await File.WriteAllBytesAsync(_configPath,
            "[core]\n\tname = caf"u8.ToArray()
                .Concat(new byte[] { 0xE9 })
                .Concat("\n"u8.ToArray()).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetBytesAsync("core.name", new byte[] { 0xE9, 0xFF, 0xFE }, cancellationToken: TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(bytes, [0xE9, 0xFF, 0xFE]), $"expected raw bytes in {Convert.ToHexString(bytes)}");
    }

    // ── The multivar match domain is unchanged ──────── SetMultiAsync's user-compiled Regex matches the parsed value bytes via
    // the byte↔char bijection. A value containing the raw byte 0xE9 must still be matched by a \xE9 pattern and replaced, exactly as before.

    [Fact]
    public async Task SetMulti_RawE9Value_MatchedByXe9Regex_Replaced()
    {
        // Value "caf\xE9" — raw byte E9, not UTF-8 (built via byte literals;
        // a source string would UTF-8-encode é to C3 A9).
        await File.WriteAllBytesAsync(_configPath,
            "[remote \"origin\"]\n\turl = caf"u8.ToArray()
                .Concat(new byte[] { 0xE9 })
                .Concat("\n"u8.ToArray()).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetMultiAsync("remote.origin.url", new Regex("caf\\xE9"), "https://new.com", cancellationToken: TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(bytes, "url = https://new.com\n"u8.ToArray()), $"expected replacement in {Convert.ToHexString(bytes)}");
        Assert.DoesNotContain((byte)0xE9, bytes);
    }

    [Fact]
    public async Task SetMulti_NonMatchingRegex_LeavesValue()
    {
        await File.WriteAllBytesAsync(_configPath,
            "[remote \"origin\"]\n\turl = https://old.com\n"u8.ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetMultiAsync("remote.origin.url", new Regex("^$"), "https://new.com", cancellationToken: TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(bytes, "url = https://old.com\n"u8.ToArray()), "non-matching multivar regex must leave the value untouched");
    }

    [Fact]
    public async Task Subsection_Utf8_Preserved()
    {
        // a subsection containing a UTF-8 char is written as UTF-8 bytes (C3 A9) and survives a re-open round-trip (Value = display decode, ValueBytes = raw
        // bytes).
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("branch.café.merge", "refs/heads/café", cancellationToken: TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_configPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(bytes, "café"u8.ToArray()), $"expected UTF-8 café in {Convert.ToHexString(bytes)}");
        Assert.False(bytes.Contains((byte)0xE9), "must not encode as the old single byte 0xE9");

        // Re-open and verify the value reads back as café.
        var backend2 = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend2.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        GitConfigEntry? entry = await backend2.GetAsync("branch.café.merge", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("refs/heads/café", entry.Value.Value);
    }

    [Fact]
    public async Task RoundTrip_Utf8Value_Preserved()
    {
        var backend = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("user.name", "café", cancellationToken: TestContext.Current.CancellationToken);

        var backend2 = new FileConfigBackend(_configPath, null, new GitSystemDirs());
        await backend2.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        GitConfigEntry? entry = await backend2.GetAsync("user.name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("café", entry.Value.Value);
    }

    /// <summary>True if <paramref name="haystack"/> contains the byte subsequence
    /// <paramref name="needle"/> at any position.</summary>
    private static bool ContainsSubsequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty)
        {
            return true;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}

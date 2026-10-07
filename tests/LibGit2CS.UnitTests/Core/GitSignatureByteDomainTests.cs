using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary> <see cref="GitSignature"/> is byte-primary (<see cref="GitSignature.NameBytes"/>/<see
/// cref="GitSignature.EmailBytes"/>) — libgit2 keeps the <c>char *</c> name/email bytes verbatim (signature.c:70-100, 322-398, 425-441) with no charset
/// concept, so non-UTF-8 name/email bytes round-trip byte-exact through parse and write. The string members are UTF-8 display decodes. </summary>
public sealed class GitSignatureByteDomainTests
{
    // ── 1: byte parse preserves non-UTF-8 name/email bytes ─────────

    [Fact]
    public void TryParse_NonUtf8NameBytes_PreservedVerbatim()
    {
        // A name containing a raw 0xE9 byte (invalid UTF-8). C's
        // git_signature__parse (signature.c:322-398) operates on char* bytes
        // — the byte must survive verbatim, not degrade to U+FFFD.
        byte[] buffer = [.. "A"u8.ToArray(), 0xE9, .. " <a@b> 100 +0000"u8.ToArray()];

        bool ok = GitSignature.TryParse(buffer, out GitSignature? sig, out _);

        Assert.True(ok);
        Assert.NotNull(sig);
        Assert.Equal([.. "A"u8.ToArray(), 0xE9], sig.NameBytes.ToArray());
        Assert.Equal("a@b"u8.ToArray(), sig.EmailBytes.ToArray());
        // Display decode: U+FFFD for the invalid byte.
        Assert.Equal("A\uFFFD", sig.Name);
    }

    [Fact]
    public void TryParse_NonUtf8EmailBytes_PreservedVerbatim()
    {
        byte[] buffer = [.. "N <a"u8.ToArray(), 0xFF, .. "@b> 100 +0000"u8.ToArray()];

        bool ok = GitSignature.TryParse(buffer, out GitSignature? sig, out _);

        Assert.True(ok);
        Assert.NotNull(sig);
        Assert.Equal([.. "a"u8.ToArray(), 0xFF, .. "@b"u8.ToArray()], sig.EmailBytes.ToArray());
    }

    [Fact]
    public void TryParse_TrimsCrudBytes()
    {
        // C's extract_trimmed trims crud bytes (signature.c:57-68) — the
        // trimmed bytes are what is stored.
        byte[] buffer = [.. "  Alice  <alice@example.com> 100 +0000"u8.ToArray()];

        bool ok = GitSignature.TryParse(buffer, out GitSignature? sig, out _);

        Assert.True(ok);
        Assert.NotNull(sig);
        Assert.Equal("Alice"u8.ToArray(), sig.NameBytes.ToArray());
    }

    [Fact]
    public void TryParse_WithHeader_ByteDomain()
    {
        byte[] buffer = [.. "author A <a@b> 100 +0000\n"u8.ToArray()];

        bool ok = GitSignature.TryParse(buffer, out GitSignature? sig, out int consumed, "author ", (byte)'\n');

        Assert.True(ok);
        Assert.NotNull(sig);
        Assert.Equal("A"u8.ToArray(), sig.NameBytes.ToArray());
        Assert.Equal(buffer.Length, consumed);
    }

    // ── 2: byte write splices the raw bytes ────────────────────────

    [Fact]
    public void WriteTo_NonUtf8NameBytes_ByteExact()
    {
        byte[] name = [.. "A"u8.ToArray(), 0xE9];
        var sig = GitSignature.Create(name, "a@b"u8.ToArray(), new GitTime(100, 0));

        using var writer = new LibGit2CS.Utils.PooledByteBufferWriter(64);
        sig.WriteTo(writer);

        Assert.Equal([.. "A"u8.ToArray(), 0xE9, .. " <a@b> 100 +0000"u8.ToArray()], writer.WrittenMemory.ToArray());
    }

    [Fact]
    public void WriteTo_NegativeOffset_RendersSign()
    {
        var sig = GitSignature.Create("Bob"u8.ToArray(), "b@b"u8.ToArray(), new GitTime(200, -330));

        using var writer = new LibGit2CS.Utils.PooledByteBufferWriter(64);
        sig.WriteTo(writer);

        Assert.Equal("Bob <b@b> 200 -0530"u8.ToArray(), writer.WrittenMemory.ToArray());
    }

    [Fact]
    public void TryFormatBytes_MatchesCharFormat_ForAscii()
    {
        var sig = GitSignature.Create("Alice"u8.ToArray(), "alice@example.com"u8.ToArray(), new GitTime(1461698037, 120));

        Span<byte> bytes = stackalloc byte[256];
        bool ok = sig.TryFormatBytes(bytes, out int written);

        Assert.True(ok);
        Assert.Equal("Alice <alice@example.com> 1461698037 +0200"u8.ToArray(), bytes[..written].ToArray());
        Assert.Equal(sig.ToString(), Encoding.UTF8.GetString(bytes[..written]));
    }

    // ── 3: parse → write round-trip is byte-exact ────────────────

    [Fact]
    public void ParseWriteRoundTrip_NonUtf8_ByteExact()
    {
        byte[] line = [.. "A"u8.ToArray(), 0xE9, .. " <x"u8.ToArray(), 0xFF, .. "@y> 1234567890 -0530"u8.ToArray()];

        Assert.True(GitSignature.TryParse(line, out GitSignature? sig, out _));

        using var writer = new LibGit2CS.Utils.PooledByteBufferWriter(64);
        sig.WriteTo(writer);

        Assert.Equal(line, writer.WrittenMemory.ToArray());
    }

    // ── 4: config identity reads raw bytes ────────────────────────

    [Fact]
    public async Task DefaultAsync_NonUtf8ConfigValue_RawBytes()
    {
        byte[] configBytes = [.. "[user]\n\tname = "u8.ToArray(), 0xE9, .. "\n\temail = e@x\n"u8.ToArray()];
        await using var config = new GitConfiguration(new GitContext());
        await config.AddBackendAsync(
            new MemoryConfigBackend(configBytes),
            GitConfigLevel.Local,
            cancellationToken: TestContext.Current.CancellationToken);

        GitSignature sig = await GitSignature.DefaultAsync(config, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([0xE9], sig.NameBytes.ToArray());
        Assert.Equal("e@x"u8.ToArray(), sig.EmailBytes.ToArray());
    }

    // ── 5: string ctor stores UTF-8 bytes ─────────────────────────

    [Fact]
    public async Task DefaultFromEnvAsync_NonUtf8ConfigValue_RawBytes()
    {
        // the config fallback in user_from_env (signature.c:208-279) feeds git_signature_new the raw config char* bytes — a non-UTF-8 user.name must not
        // degrade to a U+FFFD needle.
        byte[] configBytes = [.. "[user]\n\tname = "u8.ToArray(), 0xE9, .. "\n\temail = e@x\n"u8.ToArray()];
        await using var config = new GitConfiguration(new GitContext());
        await config.AddBackendAsync(
            new MemoryConfigBackend(configBytes),
            GitConfigLevel.Local,
            cancellationToken: TestContext.Current.CancellationToken);

        var ctx = new GitContext();
        ctx.Env["GIT_AUTHOR_NAME"] = null;
        ctx.Env["GIT_AUTHOR_EMAIL"] = null;
        ctx.Env["GIT_COMMITTER_NAME"] = null;
        ctx.Env["GIT_COMMITTER_EMAIL"] = null;
        ctx.Env["EMAIL"] = null;

        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(
            config, ctx, TestContext.Current.CancellationToken);

        Assert.Equal([0xE9], author.NameBytes.ToArray());
        Assert.Equal([0xE9], committer.NameBytes.ToArray());
        Assert.Equal("e@x"u8.ToArray(), author.EmailBytes.ToArray());
    }

    [Fact]
    public async Task DefaultFromEnvAsync_EnvValueWinsOverConfig()
    {
        byte[] configBytes = [.. "[user]\n\tname = cfg\n\temail = e@x\n"u8.ToArray()];
        await using var config = new GitConfiguration(new GitContext());
        await config.AddBackendAsync(
            new MemoryConfigBackend(configBytes),
            GitConfigLevel.Local,
            cancellationToken: TestContext.Current.CancellationToken);

        var ctx = new GitContext();
        ctx.Env["GIT_AUTHOR_NAME"] = "T";
        ctx.Env["GIT_AUTHOR_EMAIL"] = "t@x.com";
        ctx.Env["GIT_COMMITTER_NAME"] = null;
        ctx.Env["GIT_COMMITTER_EMAIL"] = null;
        ctx.Env["EMAIL"] = null;

        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(
            config, ctx, TestContext.Current.CancellationToken);

        // Env name/email win (string tier, UTF-8-encoded); the committer
        // side falls back to config bytes.
        Assert.Equal("T"u8.ToArray(), author.NameBytes.ToArray());
        Assert.Equal("cfg"u8.ToArray(), committer.NameBytes.ToArray());
    }

    [Fact]
    public async Task DefaultFromEnvAsync_EmailEnvFallback_StillApplies()
    {
        // C (signature.c:244-252): when user.email is unset everywhere else,
        // the EMAIL env var is the last fallback — still honored after the
        // byte conversion.
        var ctx = new GitContext();
        ctx.Env["GIT_AUTHOR_NAME"] = "T";
        ctx.Env["GIT_AUTHOR_EMAIL"] = null;
        ctx.Env["GIT_COMMITTER_NAME"] = "T";
        ctx.Env["GIT_COMMITTER_EMAIL"] = null;
        ctx.Env["EMAIL"] = "fallback@x";

        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(
            null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal("fallback@x"u8.ToArray(), author.EmailBytes.ToArray());
        Assert.Equal("fallback@x"u8.ToArray(), committer.EmailBytes.ToArray());
    }

    [Fact]
    public void StringCtor_StoresUtf8Bytes()
    {
        var sig = new GitSignature("café", "café@x", new GitTime(0, 0));

        Assert.Equal("café"u8.ToArray(), sig.NameBytes.ToArray());
        Assert.Equal("café@x"u8.ToArray(), sig.EmailBytes.ToArray());
        Assert.Equal("café", sig.Name);
    }

    [Fact]
    public void Create_StringOverload_DelegatesToBytes()
    {
        var sig = GitSignature.Create("  Alice  ", "alice@example.com", new GitTime(0, 0));

        Assert.Equal("Alice"u8.ToArray(), sig.NameBytes.ToArray());
        Assert.Equal("alice@example.com"u8.ToArray(), sig.EmailBytes.ToArray());
    }

    // ── 6: record equality is byte-content, not backing-store ────

    [Fact]
    public void Equality_ContentEqualSeparateParses_AreEqual()
    {
        // Regression: each parse stores its own ToArray() copy, and the
        // synthesized record equality compared the ReadOnlyMemory<byte>
        // backing stores by reference+length — two content-identical
        // signatures parsed from separate buffers were never equal.
        byte[] buffer1 = "Alice <alice@example.com> 100 +0000"u8.ToArray();
        byte[] buffer2 = [.. "Alice <alice@example.com> 100 +0000"u8.ToArray()];

        bool ok1 = GitSignature.TryParse(buffer1, out GitSignature? a, out _);
        bool ok2 = GitSignature.TryParse(buffer2, out GitSignature? b, out _);

        Assert.True(ok1);
        Assert.True(ok2);
        // Each parse stores its own ToArray() copy — distinct backing stores
        // with identical content.
        Assert.False(a!.NameBytes.Span.Overlaps(b!.NameBytes.Span));
        Assert.True(a.NameBytes.Span.SequenceEqual(b.NameBytes.Span));
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_ByteDifference_IsUnequal()
    {
        // Same display string (U+FFFD replacement) but different raw bytes —
        // byte-parity equality distinguishes them (C compares raw bytes).
        GitSignature raw = GitSignature.TryParse(
            [.. "A"u8.ToArray(), 0xE9, .. " <a@b> 100 +0000"u8.ToArray()],
            out GitSignature? rawSig, out _)
            ? rawSig!
            : throw new InvalidOperationException();

        var literal = new GitSignature("A\uFFFD", "a@b", new GitTime(100, 0));

        Assert.Equal("A\uFFFD", raw.Name); // both display as U+FFFD
        Assert.NotEqual(raw, literal);
        Assert.False(raw == literal);
    }

    [Fact]
    public void Equality_WhenDifference_IsUnequal()
    {
        byte[] buffer = "A <a@b> 100 +0000"u8.ToArray();

        bool ok = GitSignature.TryParse(buffer, out GitSignature? sig, out _);
        Assert.True(ok);

        var different = new GitSignature("A", "a@b", new GitTime(200, 0));

        Assert.NotEqual(sig, different);
    }
}

using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Refs;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Config;

/// <summary> The shared byte-key infrastructure — the <see cref="ConfigNameKey"/>/<see cref="RefNameKey"/> dictionary key types
/// (byte-wise equality + FNV-1a hash, the managed equivalent of C's <c>strcmp</c>-keyed hashmaps), <see cref="ByteOrdinalComparer"/> (C's <c>strcmp</c>
/// ordering), <see cref="ConfigKeyName.BuildNameBytes"/> (C's <c>git_str_printf("branch.%.*s.remote", …)</c> key assembly) and <see
/// cref="ConfigKeyNameNormalizeName"/> (C's <c>git_config__normalize_name</c> over raw bytes). </summary>
public sealed class ConfigKeyNameByteKeyTests
{
    private static byte[] BranchKey(byte subsectionByte) => [.. "branch."u8.ToArray(), subsectionByte, .. ".remote"u8.ToArray()];

    // ── 1: key types hash/compare byte-wise ───────────────────────

    [Fact]
    public void ConfigNameKey_EqualBytes_EqualAndSameHash()
    {
        var a = ConfigNameKey.From(BranchKey(0xFF));
        var b = ConfigNameKey.From(BranchKey(0xFF));
        var c = ConfigNameKey.From(BranchKey(0xFE));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a.GetHashCode(), c.GetHashCode());
    }

    [Fact]
    public void ConfigNameKey_DistinctBuffers_EqualByBytes()
    {
        // Two different backing arrays with the same bytes must be equal
        // (byte-wise, not reference-wise).
        byte[] one = [.. "section.\uFFFD.key"u8.ToArray()];
        byte[] two = [.. "section.\uFFFD.key"u8.ToArray()];
        Assert.NotSame(one, two);
        Assert.Equal(ConfigNameKey.From(one), ConfigNameKey.From(two));
    }

    [Fact]
    public void RefNameKey_EqualBytes_EqualAndSameHash()
    {
        byte[] raw = [.. "refs/heads/"u8.ToArray(), 0xE9];
        var a = RefNameKey.From(raw);
        var b = RefNameKey.From(raw);
        byte[] longer = [.. raw, 0x80];
        var c = RefNameKey.From(longer);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ByteOrdinalComparer_MatchesStrCmp()
    {
        // C's strcmp: shorter prefix sorts first; byte value decides.
        Assert.True(ByteOrdinalComparer.Instance.Compare("a"u8.ToArray(), "ab"u8.ToArray()) < 0);
        Assert.True(ByteOrdinalComparer.Instance.Compare("ab"u8.ToArray(), "a"u8.ToArray()) > 0);
        Assert.Equal(0, ByteOrdinalComparer.Instance.Compare("abc"u8.ToArray(), "abc"u8.ToArray()));
        // 0xFF > 0x7F: a raw-byte name sorts after the ASCII one.
        byte[] ascii = "refs/heads/a"u8.ToArray();
        byte[] rawByte = [.. ascii, 0xFF];
        Assert.True(ByteOrdinalComparer.Instance.Compare(ascii, rawByte) < 0);
    }

    // ── 2: BuildNameBytes preserves subsection bytes verbatim ─────

    [Fact]
    public void BuildNameBytes_NonUtf8Subsection_Verbatim()
    {
        byte[] subsection = [0xFF, 0xFE];
        ReadOnlyMemory<byte> key = ConfigKeyName.BuildNameBytes("branch"u8, subsection, "Remote"u8);

        Assert.Equal([.. "branch."u8.ToArray(), 0xFF, 0xFE, .. ".remote"u8.ToArray()], key.ToArray());
    }

    [Fact]
    public void BuildNameBytes_AsciiSubsection_LowercasesVariable()
    {
        ReadOnlyMemory<byte> key = ConfigKeyName.BuildNameBytes("branch"u8, "main"u8, "REMOTE"u8);
        Assert.Equal("branch.main.remote"u8.ToArray(), key.ToArray());
    }

    // ── 3: NormalizeNameBytes is the byte port of normalize_name ──

    [Fact]
    public void NormalizeNameBytes_LowercasesSectionAndVariable_PreservesSubsection()
    {
        ReadOnlyMemory<byte> key = ConfigKeyNameNormalizeName("CORE.AutoCRLF"u8);
        Assert.Equal("core.autocrlf"u8.ToArray(), key.ToArray());

        ReadOnlyMemory<byte> dotted = ConfigKeyNameNormalizeName("Branch.My.Name.Remote"u8);
        Assert.Equal("branch.My.Name.remote"u8.ToArray(), dotted.ToArray());
    }

    [Fact]
    public void NormalizeNameBytes_NonUtf8Subsection_PassesThroughVerbatim()
    {
        // C's git_config__normalize_name operates on char* bytes: a raw 0xE9
        // subsection byte passes through untouched (only section/variable are
        // ASCII-lowercased).
        ReadOnlyMemory<byte> key = ConfigKeyNameNormalizeName([.. "Branch."u8.ToArray(), 0xE9, .. ".Remote"u8.ToArray()]);
        Assert.Equal([.. "branch."u8.ToArray(), 0xE9, .. ".remote"u8.ToArray()], key.ToArray());
    }

    [Fact]
    public void NormalizeNameBytes_RejectsNewlineInSubsection()
    {
        GitException ex = Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName("section.a\nb.key"u8));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void NormalizeNameBytes_RejectsInvalidNames()
    {
        Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName("nodot"u8));
        Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName(".leading"u8));
        Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName("trailing."u8));
        // Non-ASCII section/variable bytes are invalid (C's normalize_section
        // rejects anything that is not alnum or '-').
        Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName([.. "s"u8.ToArray(), 0xE9, .. ".key"u8.ToArray()]));
        Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName([.. "sec.k"u8.ToArray(), 0xE9]));
    }

    [Fact]
    public void NormalizeNameBytes_EmptySubsection_IsValid()
    {
        // C's git_config__normalize_name accepts an empty subsection
        // ("sec..key" — fdot/ldot are adjacent; only the section and variable
        // are validated).
        ReadOnlyMemory<byte> key = ConfigKeyNameNormalizeName("Sec..Key"u8);
        Assert.Equal("sec..key"u8.ToArray(), key.ToArray());
    }

    [Fact]
    public void NormalizeNameBytes_AgreesWithStringNormalizeName_ForAscii()
    {
        // The string convenience tier (GitConfiguration.NormalizeName) must
        // produce the same normalized key as the byte tier for ASCII input.
        string[] names = ["core.autocrlf", "Branch.My.Name.Remote", "user.NAME", "a.b.c.d.e"];
        foreach (string name in names)
        {
            ReadOnlyMemory<byte> bytes = ConfigKeyNameNormalizeName(Encoding.UTF8.GetBytes(name));
            Assert.Equal(GitConfigurationNormalizeName(name), Encoding.UTF8.GetString(bytes.Span));
        }
    }

    [Fact]
    public void NormalizeNameBytes_EmptyName_ThrowsInvalidSpec()
    {
        // C's git_config__normalize_name("") fails with "invalid config item
        // name ''" (GIT_EINVALIDSPEC). The callers' PooledByteBufferWriter
        // capacity must be clamped so the writer ctor's initialCapacity <= 0
        // ArgumentException never masks this error.
        GitException ex = Assert.Throws<GitException>(() => ConfigKeyNameNormalizeName(ReadOnlySpan<byte>.Empty));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void NormalizeNameString_EmptyName_ThrowsInvalidSpec()
    {
        // The string convenience tier must throw the same InvalidSpec for "".
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName(string.Empty));
    }

    private static byte[] ConfigKeyNameNormalizeName(ReadOnlySpan<byte> name)
    {
        using var writer = new PooledByteBufferWriter(name.Length);
        ConfigKeyName.NormalizeNameBytes(writer, name);
        return writer.WrittenSpan.ToArray();
    }

    private static string GitConfigurationNormalizeName(ReadOnlySpan<char> name)
    {
        using var writer = new PooledByteBufferWriter(Encoding.UTF8.GetByteCount(name));
        GitConfiguration.NormalizeName(writer, name);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }
}

using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary> The mailmap is byte-domain — libgit2 parses the raw buffer bytes (mailmap.c:98-149) and compares with
/// <c>git__strcmp</c> (mailmap.c:42-59), so non-UTF-8 name/email bytes round-trip byte-exact through parse and lookup. A U+FFFD-bearing needle (from a lossy
/// decode path) must NOT match a raw-byte entry. </summary>
public sealed class GitMailmapByteDomainTests
{
    // ── 1: non-UTF-8 replace-name entry matched by byte needle ────

    [Fact]
    public void LookupBytes_NonUtf8ReplaceName_ByteExactMatch()
    {
        // A replace name containing a raw 0xE9 byte (invalid UTF-8). C's
        // git_mailmap_entry_lookup compares the raw char* bytes with
        // git__strcmp — the byte needle must match the byte entry.
        byte[] replaceName = [.. "A"u8.ToArray(), 0xE9];
        byte[] buffer = [.. "Real <real@x> "u8.ToArray(), .. replaceName, .. " <jd@x>\n"u8.ToArray()];
        using var mm = GitMailmap.FromBuffer(buffer);

        MailmapEntry? entry = mm.LookupBytes(replaceName, "jd@x"u8.ToArray());

        Assert.NotNull(entry);
        Assert.Equal("Real"u8.ToArray(), entry.RealNameBytes.GetValueOrDefault().ToArray());
    }

    [Fact]
    public void LookupBytes_NonUtf8RealName_PreservedVerbatim()
    {
        byte[] realName = [.. "R"u8.ToArray(), 0xFF, .. "eal"u8.ToArray()];
        byte[] buffer = [.. realName, .. " <r@x> <jd@x>\n"u8.ToArray()];
        using var mm = GitMailmap.FromBuffer(buffer);

        MailmapEntry? entry = mm.LookupBytes(null, "jd@x"u8.ToArray());

        Assert.NotNull(entry);
        Assert.Equal(realName, entry.RealNameBytes.GetValueOrDefault().ToArray());
        // Display decode: U+FFFD for the invalid byte.
        Assert.Equal("R\uFFFD eal".Replace(" ", string.Empty), entry.RealName);
    }

    // ── 2: U+FFFD needle does NOT match a raw-byte entry ──────────

    [Fact]
    public void LookupBytes_ReencodedUfffdNeedle_DoesNotMatchRawByteEntry()
    {
        // A needle that went through a lossy
        // UTF-8 decode carries U+FFFD (bytes EF BF BD), which must NOT match
        // an entry whose replace name is the raw byte 0xE9 — C's strcmp
        // compares the original bytes.
        byte[] rawName = [0xE9];
        byte[] buffer = [.. "Real <real@x> "u8.ToArray(), .. rawName, .. " <jd@x>\n"u8.ToArray()];
        using var mm = GitMailmap.FromBuffer(buffer);

        // U+FFFD encodes to EF BF BD — different bytes from E9.
        byte[] ufffdNeedle = Encoding.UTF8.GetBytes("\uFFFD");
        MailmapEntry? entry = mm.LookupBytes(ufffdNeedle, "jd@x"u8.ToArray());

        Assert.Null(entry);
    }

    // ── 3: ResolveSignature uses raw signature bytes ─────────────

    [Fact]
    public void ResolveSignature_NonUtf8SignatureName_MatchesByteEntry()
    {
        byte[] replaceName = [.. "A"u8.ToArray(), 0xE9];
        byte[] buffer = [.. "Real <real@x> "u8.ToArray(), .. replaceName, .. " <jd@x>\n"u8.ToArray()];
        using var mm = GitMailmap.FromBuffer(buffer);

        var sig = GitSignature.Create(replaceName, "jd@x"u8.ToArray(), new GitTime(100, 0));
        GitSignature resolved = mm.ResolveSignature(sig);

        Assert.Equal("Real"u8.ToArray(), resolved.NameBytes.ToArray());
        Assert.Equal("real@x"u8.ToArray(), resolved.EmailBytes.ToArray());
    }

    [Fact]
    public void ResolveSignature_NoMatch_ReturnsOriginal()
    {
        using var mm = GitMailmap.FromBuffer("Real <real@x> <jd@x>\n"u8.ToArray());

        var sig = GitSignature.Create("Nobody"u8.ToArray(), "nobody@x"u8.ToArray(), new GitTime(100, 0));
        GitSignature resolved = mm.ResolveSignature(sig);

        Assert.Same(sig, resolved);
    }

    // ── 4: sorted order is byte-ordinal (C's strcmp) ─────────────

    [Fact]
    public void LookupBytes_ByteOrdinalSort_NonAsciiOrder()
    {
        // Entries sort by replace_email byte-ordinal (C's mailmap_entry_cmp).
        // A raw 0xFF email sorts after the ASCII one.
        byte[] buffer = [.. "A <a@x> <a@x>\nB <b@x> <"u8.ToArray(), 0xFF, .. "@x>\n"u8.ToArray()];
        using var mm = GitMailmap.FromBuffer(buffer);

        MailmapEntry? ascii = mm.LookupBytes(null, "a@x"u8.ToArray());
        byte[] rawEmail = [0xFF, .. "@x"u8.ToArray()];
        MailmapEntry? raw = mm.LookupBytes(null, rawEmail);

        Assert.NotNull(ascii);
        Assert.NotNull(raw);
        Assert.Equal("A"u8.ToArray(), ascii.RealNameBytes.GetValueOrDefault().ToArray());
        Assert.Equal("B"u8.ToArray(), raw.RealNameBytes.GetValueOrDefault().ToArray());
    }

    // ── 5: string surface still works (UTF-8 convenience) ───────

    [Fact]
    public void StringSurface_Ascii_Unchanged()
    {
        using var mm = GitMailmap.FromBuffer("Real Person <real@x> <jd@x>\n");

        (string name, string email) = mm.Resolve("jd", "jd@x");

        Assert.Equal("Real Person", name);
        Assert.Equal("real@x", email);
    }
}

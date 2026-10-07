using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

public sealed class ConfigMapTests
{
    [Fact]
    public void AutoCrlfMap_True_ReturnsTrue()
    {
        Assert.Equal(GitAutoCrlf.True, GitConfigMaps.AutoCrlfMap.Lookup("true"));
        Assert.Equal(GitAutoCrlf.True, GitConfigMaps.AutoCrlfMap.Lookup("yes"));
        Assert.Equal(GitAutoCrlf.True, GitConfigMaps.AutoCrlfMap.Lookup("on"));
        Assert.Equal(GitAutoCrlf.True, GitConfigMaps.AutoCrlfMap.Lookup("1"));
    }

    [Fact]
    public void AutoCrlfMap_False_ReturnsFalse()
    {
        Assert.Equal(GitAutoCrlf.False, GitConfigMaps.AutoCrlfMap.Lookup("false"));
        Assert.Equal(GitAutoCrlf.False, GitConfigMaps.AutoCrlfMap.Lookup("0"));
    }

    [Fact]
    public void AutoCrlfMap_Input_ReturnsInput()
    {
        Assert.Equal(GitAutoCrlf.Input, GitConfigMaps.AutoCrlfMap.Lookup("input"));
    }

    [Fact]
    public void EolMap_Lf_ReturnsLf()
    {
        Assert.Equal(GitEol.Lf, GitConfigMaps.EolMap.Lookup("lf"));
    }

    [Fact]
    public void EolMap_Crlf_ReturnsCrlf()
    {
        Assert.Equal(GitEol.Crlf, GitConfigMaps.EolMap.Lookup("crlf"));
    }

    [Fact]
    public void EolMap_Native_ReturnsNative()
    {
        Assert.Equal(GitConfigMaps.EolNative, GitConfigMaps.EolMap.Lookup("native"));
    }

    [Fact]
    public void EolMap_False_ReturnsUnset()
    {
        Assert.Equal(GitEol.Unset, GitConfigMaps.EolMap.Lookup("false"));
    }

    [Fact]
    public void SafeCrlfMap_True_ReturnsFail()
    {
        Assert.Equal(GitSafeCrlf.Fail, GitConfigMaps.SafeCrlfMap.Lookup("true"));
    }

    [Fact]
    public void SafeCrlfMap_Warn_ReturnsWarn()
    {
        Assert.Equal(GitSafeCrlf.Warn, GitConfigMaps.SafeCrlfMap.Lookup("warn"));
    }

    [Fact]
    public void SafeCrlfMap_False_ReturnsFalse()
    {
        Assert.Equal(GitSafeCrlf.False, GitConfigMaps.SafeCrlfMap.Lookup("false"));
    }

    [Fact]
    public void AutoCrlfMap_InvalidValue_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigMaps.AutoCrlfMap.Lookup("invalid"));
    }

    // ── INT32 map items return the parsed integer ──────────

    [Fact]
    public void Int32Map_ReturnsParsedInteger()
    {
        // C (config.c:1402-1405): for an INT32 map item, the parsed integer is
        // returned, not the item's construction-time value.
        var map = new GitConfigurationMap<int>([
            new(GitConfigurationMapType.Int32, null, 999),
        ]);

        Assert.Equal(5, map.Lookup("5"));
        Assert.Equal(-3, map.Lookup("-3"));
    }

    [Fact]
    public void Int32Map_InvalidValue_Throws()
    {
        var map = new GitConfigurationMap<int>([
            new(GitConfigurationMapType.Int32, null, 999),
        ]);

        Assert.Throws<GitException>(() => map.Lookup("not-an-int"));
    }
    // ── core.abbrev map (config_cache.c:68-71) ───────────────────────────

    [Fact]
    public void AbbrevMap_Integer_ReturnsParsedInteger()
    {
        // C: INT32 first — any int32-parsable value (incl. k/m/g suffixes)
        // returns the parsed integer.
        Assert.Equal(12, GitConfigMaps.AbbrevMap.Lookup("12"));
        Assert.Equal(7, GitConfigMaps.AbbrevMap.Lookup("7"));
    }

    [Fact]
    public void AbbrevMap_False_ReturnsFullOidSentinel()
    {
        // C: GIT_CONFIGMAP_FALSE → GIT_ABBREV_FALSE = GIT_OID_MAX_HEXSIZE (64).
        Assert.Equal(64, GitConfigMaps.AbbrevMap.Lookup("false"));
        Assert.Equal(64, GitConfigMaps.AbbrevMap.Lookup("no"));
    }

    [Fact]
    public void AbbrevMap_Auto_ReturnsDefault()
    {
        // C: the string entry is strcasecmp-matched.
        Assert.Equal(7, GitConfigMaps.AbbrevMap.Lookup("auto"));
        Assert.Equal(7, GitConfigMaps.AbbrevMap.Lookup("AUTO"));
    }

    [Fact]
    public void AbbrevMap_InvalidValue_Throws()
    {
        // C: "true" is not an int32, not FALSE, not "auto" → "failed to map".
        Assert.Throws<GitException>(() => GitConfigMaps.AbbrevMap.Lookup("true"));
        Assert.Throws<GitException>(() => GitConfigMaps.AbbrevMap.Lookup("banana"));
    }
}

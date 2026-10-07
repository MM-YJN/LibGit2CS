// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// Configmap tables for CRLF-related config keys. Managed port of
/// <c>src/libgit2/config_cache.c</c> tables <c>_configmap_autocrlf</c>,
/// <c>_configmap_eol</c>, <c>_configmap_safecrlf</c>.
/// </summary>
public static class GitConfigMaps
{
    /// <summary>
    /// The native EOL for this platform. On Linux/macOS, LF; on Windows, CRLF.
    /// Maps to <c>GIT_EOL_NATIVE</c> (<c>repository.h:88-90</c>).
    /// </summary>
    public static readonly GitEol EolNative = OperatingSystem.IsWindows() ? GitEol.Crlf : GitEol.Lf;

    /// <summary>
    /// The default <c>core.eol</c> value when unset. Maps to
    /// <c>GIT_EOL_DEFAULT = GIT_EOL_NATIVE</c>.
    /// </summary>
    public static readonly GitEol EolDefault = EolNative;

    /// <summary>
    /// The default <c>core.autocrlf</c> value when unset. Maps to
    /// <c>GIT_AUTO_CRLF_DEFAULT = GIT_AUTO_CRLF_FALSE</c>.
    /// </summary>
    public static readonly GitAutoCrlf AutoCrlfDefault = GitAutoCrlf.False;

    /// <summary>
    /// The default <c>core.safecrlf</c> value when unset. Maps to
    /// <c>GIT_SAFE_CRLF_DEFAULT = GIT_CONFIGMAP_FALSE</c>.
    /// </summary>
    public static readonly GitSafeCrlf SafeCrlfDefault = GitSafeCrlf.False;

    /// <summary>
    /// Configmap for <c>core.autocrlf</c>. Maps <c>false→False, true→True, "input"→Input</c>.
    /// Matches <c>_configmap_autocrlf</c> (config_cache.c:50-52).
    /// </summary>
    public static readonly GitConfigurationMap<GitAutoCrlf> AutoCrlfMap = new([
        new(GitConfigurationMapType.False, null, GitAutoCrlf.False),
        new(GitConfigurationMapType.True, null, GitAutoCrlf.True),
        new(GitConfigurationMapType.String, "input", GitAutoCrlf.Input),
    ]);

    /// <summary>
    /// Configmap for <c>core.eol</c>. Maps <c>false→Unset, "lf"→Lf, "crlf"→Crlf, "native"→Native</c>.
    /// Matches <c>_configmap_eol</c> (config_cache.c:33-36).
    /// </summary>
    public static readonly GitConfigurationMap<GitEol> EolMap = new([
        new(GitConfigurationMapType.False, null, GitEol.Unset),
        new(GitConfigurationMapType.String, "lf", GitEol.Lf),
        new(GitConfigurationMapType.String, "crlf", GitEol.Crlf),
        new(GitConfigurationMapType.String, "native", EolNative),
    ]);

    /// <summary>
    /// Configmap for <c>core.safecrlf</c>. Maps <c>false→False, true→Fail, "warn"→Warn</c>.
    /// Matches <c>_configmap_safecrlf</c> (config_cache.c:56-58).
    /// </summary>
    public static readonly GitConfigurationMap<GitSafeCrlf> SafeCrlfMap = new([
        new(GitConfigurationMapType.False, null, GitSafeCrlf.False),
        new(GitConfigurationMapType.True, null, GitSafeCrlf.Fail),
        new(GitConfigurationMapType.String, "warn", GitSafeCrlf.Warn),
    ]);

    /// <summary>
    /// The default <c>core.abbrev</c> value when unset. Maps to
    /// <c>GIT_ABBREV_DEFAULT = 7</c> (repository.h:107).
    /// </summary>
    public const int AbbrevDefault = 7;

    /// <summary>
    /// The minimum valid <c>core.abbrev</c> value. Maps to
    /// <c>GIT_ABBREV_MINIMUM = 4</c> (repository.h:106).
    /// </summary>
    public const int AbbrevMinimum = 4;

    /// <summary>
    /// The <c>core.abbrev = false</c> sentinel: print the full OID. Maps to
    /// <c>GIT_ABBREV_FALSE = GIT_OID_MAX_HEXSIZE</c> (64 with SHA-256 enabled;
    /// <c>git_repository__abbrev_length</c> clamps it down to the repository's
    /// OID hex size, so the observable result is the full OID either way).
    /// </summary>
    public const int AbbrevFalse = 64;

    /// <summary>
    /// Configmap for <c>core.abbrev</c>. Matches <c>_configmap_abbrev</c>
    /// (config_cache.c:68-71): any integer → its value (clamped by the
    /// caller), <c>false</c> → full OID, <c>"auto"</c> → 7.
    /// </summary>
    public static readonly GitConfigurationMap<int> AbbrevMap = new([
        new(GitConfigurationMapType.Int32, null, 0),
        new(GitConfigurationMapType.False, null, AbbrevFalse),
        new(GitConfigurationMapType.String, "auto", AbbrevDefault),
    ]);
}

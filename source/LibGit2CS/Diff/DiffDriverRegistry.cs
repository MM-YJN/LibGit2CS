// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Collections.Immutable;

using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Diff;

/// <summary>
/// Per-diff driver registry. Caches loaded drivers by name, looks up the
/// <c>diff</c> attribute per path, and loads drivers from config + built-in
/// table. Managed port of <c>git_diff_driver_registry</c> +
/// <c>git_diff_driver_lookup</c> + <c>git_diff_driver_load</c> +
/// <c>git_diff_driver_builtin</c> (diff_driver.c:48-384).
/// </summary>
internal sealed class DiffDriverRegistry
{
    private readonly Dictionary<string, DiffDriver> _cache = [];
    private readonly GitRepository _repo;
    private readonly AttributeCache _attrCache;
    private readonly bool _ignoreCase;

    /// <summary>
    /// The built-in driver definitions from <c>userdiff.h</c> (16 languages).
    /// POSIX character classes (<c>[:space:]</c> etc.) are converted to .NET
    /// equivalents at load time — .NET Regex does not support POSIX classes.
    /// </summary>
    internal static readonly ImmutableArray<DiffDriverDefinition> s_builtinDefs =
    [
        new("ada",
            "!^(.*[ \\t])?(is[ \\t]+new|renames|is[ \\t]+separate)([ \\t].*)?$\n"
            + "!^[ \\t]*with[ \\t].*$\n"
            + "^[ \\t]*((procedure|function)[ \\t]+.*)$\n"
            + "^[ \\t]*((package|protected|task)[ \\t]+.*)$",
            "[a-zA-Z][a-zA-Z0-9_]*"
            + "|[-+]?[0-9][0-9#_.aAbBcCdDeEfF]*([eE][+-]?[0-9_]+)?"
            + "|=>|\\.\\.|\\*\\*|:=|/=|>=|<=|<<|>>|<>",
            RegexFlags.IgnoreCase),

        new("fortran",
            "!^([C*]|[ \\t]*!)\n"
            + "!^[ \\t]*MODULE[ \\t]+PROCEDURE[ \\t]\n"
            + "^[ \\t]*((END[ \\t]+)?(PROGRAM|MODULE|BLOCK[ \\t]+DATA"
            + "|([^'\" \\t]+[ \\t]+)*(SUBROUTINE|FUNCTION))[ \\t]+[A-Z].*)$",
            "[a-zA-Z][a-zA-Z0-9_]*"
            + "|\\.([Ee][Qq]|[Nn][Ee]|[Gg][TtEe]|[Ll][TtEe]|[Tt][Rr][Uu][Ee]|[Ff][Aa][Ll][Ss][Ee]|[Aa][Nn][Dd]|[Oo][Rr]|[Nn]?[Ee][Qq][Vv]|[Nn][Oo][Tt])\\."
            + "|[-+]?[0-9.]+([AaIiDdEeFfLlTtXx][Ss]?[-+]?[0-9.]*)?(_[a-zA-Z0-9][a-zA-Z0-9_]*)?"
            + "|//|\\*\\*|::|[/<>=]=",
            RegexFlags.IgnoreCase),

        new("html", "^[ \\t]*(<[Hh][1-6][ \\t].*>.*)$", "[^<>= \\t]+", RegexFlags.None),

        new("java",
            "!^[ \\t]*(catch|do|for|if|instanceof|new|return|switch|throw|while)\n"
            + "^[ \\t]*(([A-Za-z_][A-Za-z_0-9]*[ \\t]+)+[A-Za-z_][A-Za-z_0-9]*[ \\t]*\\([^;]*)$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[fFlL]?|0[xXbB]?[0-9a-fA-F]+[lL]?"
            + "|[-+*/<>%&^|=!]="
            + "|--|\\+\\+|<<=?|>>>?=?|&&|\\|\\|",
            RegexFlags.None),

        new("matlab",
            "^[[:space:]]*((classdef|function)[[:space:]].*)$|^%%[[:space:]].*$",
            "[a-zA-Z_][a-zA-Z0-9_]*|[-+0-9.e]+|[=~<>]=|\\.[*/\\^']|\\|\\||&&",
            RegexFlags.None),

        new("objc",
            "!^[ \\t]*(do|for|if|else|return|switch|while)\n"
            + "^[ \\t]*([-+][ \\t]*\\([ \\t]*[A-Za-z_][A-Za-z_0-9* \\t]*\\)[ \\t]*[A-Za-z_].*)$\n"
            + "^[ \\t]*(([A-Za-z_][A-Za-z_0-9]*[ \\t]+)+[A-Za-z_][A-Za-z_0-9]*[ \\t]*\\([^;]*)$\n"
            + "^(@(implementation|interface|protocol)[ \\t].*)$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[fFlL]?|0[xXbB]?[0-9a-fA-F]+[lL]?"
            + "|[-+*/<>%&^|=!]=|--|\\+\\+|<<=?|>>=?|&&|\\|\\||::|->",
            RegexFlags.None),

        new("pascal",
            "^(((class[ \\t]+)?(procedure|function)|constructor|destructor|interface|"
            + "implementation|initialization|finalization)[ \\t]*.*)$\n"
            + "^(.*=[ \\t]*(class|record).*)$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+|0[xXbB]?[0-9a-fA-F]+"
            + "|<>|<=|>=|:=|\\.\\.",
            RegexFlags.None),

        new("perl",
            "^package .*\n"
            + "^sub [[:alnum:]_':]+[ \\t]*"
            + "(\\([^)]*\\)[ \\t]*)?"
            + "(:[^;#]*)?"
            + "(\\{[ \\t]*)?"
            + "(#.*)?$\n"
            + "^(BEGIN|END|INIT|CHECK|UNITCHECK|AUTOLOAD|DESTROY)[ \\t]*"
            + "(\\{[ \\t]*)?"
            + "(#.*)?$\n"
            + "^=head[0-9] .*",
            "[[:alpha:]_'][[:alnum:]_']*"
            + "|0[xb]?[0-9a-fA-F_]*"
            + "|[0-9a-fA-F_]+(\\.[0-9a-fA-F_]+)?([eE][-+]?[0-9_]+)?"
            + "|=>|-[rwxoRWXOezsfdlpSugkbctTBMAC>]|~~|::"
            + "|&&=|\\|\\|=|//=|\\*\\*="
            + "|&&|\\|\\||//|\\+\\+|--|\\*\\*|\\.\\.\\.?"
            + "|[-+*/%.^&<>=!|]="
            + "|=~|!~"
            + "|<<|<>|<=>|>>",
            RegexFlags.None),

        new("python", "^[ \\t]*((class|def)[ \\t].*)$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[jJlL]?|0[xX]?[0-9a-fA-F]+[lL]?"
            + "|[-+*/<>%&^|=!]=|//=?|<<=?|>>=?|\\*\\*=?",
            RegexFlags.None),

        new("ruby", "^[ \\t]*((class|module|def)[ \\t].*)$",
            "(@|@@|\\$)?[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+|0[xXbB]?[0-9a-fA-F]+|\\?(\\\\C-)?(\\\\M-)?."
            + "|//=?|[-+*/<>%&^|=!]=|<<=?|>>=?|===|\\.{1,3}|::|[!=]~",
            RegexFlags.None),

        new("bibtex", "(@[a-zA-Z]{1,}[ \\t]*\\{{0,1}[ \\t]*[^ \\t\"@',\\#}{~%]*).*$",
            "[={}\"]|[^={}\" \\t]+", RegexFlags.None),

        new("tex", "^(\\\\((sub)*section|chapter|part)\\*{0,1}\\{.*)$",
            "\\\\[a-zA-Z@]+|\\\\.|[a-zA-Z0-9\x80-\xff]+", RegexFlags.None),

        new("cpp",
            "!^[ \\t]*[A-Za-z_][A-Za-z_0-9]*:[[:space:]]*($|/[/*])\n"
            + "^((::[[:space:]]*)?[A-Za-z_].*)$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[fFlL]?|0[xXbB]?[0-9a-fA-F]+[lLuU]*"
            + "|[-+*/<>%&^|=!]=|--|\\+\\+|<<=?|>>=?|&&|\\|\\||::|->\\*?|\\.\\*",
            RegexFlags.None),

        new("csharp",
            "!^[ \\t]*(do|while|for|if|else|instanceof|new|return|switch|case|throw|catch|using)\n"
            + "^[ \\t]*(((static|public|internal|private|protected|new|virtual|sealed|override|unsafe)[ \\t]+)*[][<>@.~_[:alnum:]]+[ \\t]+[<>@._[:alnum:]]+[ \\t]*\\(.*\\))[ \\t]*$\n"
            + "^[ \\t]*(((static|public|internal|private|protected|new|virtual|sealed|override|unsafe)[ \\t]+)*[][<>@.~_[:alnum:]]+[ \\t]+[@._[:alnum:]]+)[ \\t]*$\n"
            + "^[ \\t]*(((static|public|internal|private|protected|new|unsafe|sealed|abstract|partial)[ \\t]+)*(class|enum|interface|struct)[ \\t]+.*)$\n"
            + "^[ \\t]*(namespace[ \\t]+.*)$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[fFlL]?|0[xXbB]?[0-9a-fA-F]+[lL]?"
            + "|[-+*/<>%&^|=!]=|--|\\+\\+|<<=?|>>=?|&&|\\|\\||::|->",
            RegexFlags.None),

        new("php",
            "^[ \\t]*(((public|private|protected|static|final)[ \\t]+)*((class|function)[ \\t].*))$",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[fFlL]?|0[xX]?[0-9a-fA-F]+[lL]?"
            + "|[-+*/<>%&^|=!]=|--|\\+\\+|<<=?|>>=?|&&|\\|\\||::|->",
            RegexFlags.None),

        new("javascript",
            "([a-zA-Z_$][a-zA-Z0-9_$]*(\\.[a-zA-Z0-9_$]+)*[ \\t]*=[ \\t]*function([ \\t][a-zA-Z_$][a-zA-Z0-9_$]*)?[^\\{]*)\n"
            + "([a-zA-Z_$][a-zA-Z0-9_$]*[ \\t]*:[ \\t]*function([ \\t][a-zA-Z_$][a-zA-Z0-9_$]*)?[^\\{]*)\n"
            + "[^a-zA-Z0-9_\\$](function([ \\t][a-zA-Z_$][a-zA-Z0-9_$]*)?[^\\{]*)",
            "[a-zA-Z_][a-zA-Z0-9_]*"
            + "|[-+0-9.e]+[fFlL]?|0[xX]?[0-9a-fA-F]+[lL]?"
            + "|[-+*/<>%&^|=!]=|--|\\+\\+|<<=?|>>=?|&&|\\|\\||::|->",
            RegexFlags.None),
    ];

    private const string WordDefault = "|[^[:space:]]|[\xc0-\xff][\x80-\xbf]+";

    public DiffDriverRegistry(GitRepository repo, bool ignoreCase, AttributeCache attrCache)
    {
        _repo = repo;
        _ignoreCase = ignoreCase;
        _attrCache = attrCache;
    }

    /// <summary>
    /// Looks up the diff driver for a path. Matches
    /// <c>git_diff_driver_lookup</c> (diff_driver.c:349-384). Looks up the
    /// <c>diff</c> attribute; based on the value: unspecified → Auto,
    /// false → Binary, true → Text, string → Load custom driver.
    /// </summary>
    public async ValueTask<DiffDriver> LookupAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return _repo.Context.DiffDrivers.Auto;
        }

        // C (attr.c:69-70): GIT_DIR_FLAG_FALSE for bare repos, otherwise
        // UNKNOWN and the workdir path is stat'ed (attr_file.c:599-613).
        string? workdir = _repo.Workdir;
        AttrPath.DirFlag dirFlag = _repo.IsBare ? AttrPath.DirFlag.False : AttrPath.DirFlag.Unknown;
        var attrPath = new AttrPath();
        attrPath.Init(path, workdir ?? string.Empty, dirFlag);

        return await LookupAsync(attrPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-faithful overload: looks up the diff driver for a path known as raw UTF-8 bytes. ; the <see cref="string"/> overload
    /// delegates here via <see cref="AttrPath.Init(string, string, AttrPath.DirFlag)"/>. </summary>
    public async ValueTask<DiffDriver> LookupAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        if (path.IsEmpty)
        {
            return _repo.Context.DiffDrivers.Auto;
        }

        // C (attr.c:69-70): GIT_DIR_FLAG_FALSE for bare repos, otherwise
        // UNKNOWN and the workdir path is stat'ed (attr_file.c:599-613).
        string? workdir = _repo.Workdir;
        AttrPath.DirFlag dirFlag = _repo.IsBare ? AttrPath.DirFlag.False : AttrPath.DirFlag.Unknown;
        var attrPath = new AttrPath();
        attrPath.Init(path, GitPath.FromUtf8String(workdir ?? string.Empty), dirFlag);

        return await LookupAsync(attrPath, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DiffDriver> LookupAsync(AttrPath attrPath, CancellationToken cancellationToken)
    {
        GitAttrValue value = await _attrCache.LookupOneAsync(attrPath, "diff", cancellationToken: cancellationToken).ConfigureAwait(false);

        if (value.IsUnspecified)
        {
            return _repo.Context.DiffDrivers.Auto;
        }

        if (value.IsFalse)
        {
            return _repo.Context.DiffDrivers.Binary;
        }

        if (value.IsTrue)
        {
            return _repo.Context.DiffDrivers.Text;
        }

        if (value.Kind == GitAttrValueKind.Value && value.Text is not null)
        {
            return await LoadAsync(value.Text, cancellationToken).ConfigureAwait(false);
        }

        return _repo.Context.DiffDrivers.Auto;
    }

    /// <summary>
    /// Loads a driver by name from config, falling back to built-in
    /// definitions. Matches <c>git_diff_driver_load</c> (diff_driver.c:223-347)
    /// + <c>git_diff_driver_builtin</c> (diff_driver.c:178-221).
    /// </summary>
    private ValueTask<DiffDriver> LoadAsync(string driverName, CancellationToken cancellationToken)
    {
        // Check cache first (majority — the config slow path is a miss).
        if (_cache.TryGetValue(driverName, out DiffDriver? cached))
        {
            return ValueTask.FromResult(cached);
        }

        return new ValueTask<DiffDriver>(LoadSlowAsync(driverName, cancellationToken));
    }

    /// <summary>
    /// Cache-miss path of <see cref="LoadAsync"/>: reads config and falls
    /// back to the builtin table (real config IO).
    /// </summary>
    private async Task<DiffDriver> LoadSlowAsync(string driverName, CancellationToken cancellationToken)
    {
        // C (git_diff_driver_load, diff_driver.c:223-347): CONFIG is read
        // FIRST — user diff.<name>.binary/.xfuncname/.funcname/.wordregex
        // override the builtin table; the builtin is only the fallback when
        // no config entry exists (diff_driver.c:337-341).
        DiffDriver? fromConfig = await LoadFromConfigAsync(driverName, cancellationToken).ConfigureAwait(false);
        if (fromConfig is not null)
        {
            _cache[driverName] = fromConfig;
            return fromConfig;
        }

        if (LoadBuiltin(driverName) is { } builtin)
        {
            _cache[driverName] = builtin;
            return builtin;
        }

        // No config, no builtin — Auto.
        _cache[driverName] = _repo.Context.DiffDrivers.Auto;
        return _repo.Context.DiffDrivers.Auto;
    }

    /// <summary>
    /// Loads a driver from the repository config: <c>diff.&lt;name&gt;.binary</c>,
    /// <c>.xfuncname</c>, <c>.funcname</c>, <c>.wordregex</c>. Returns null
    /// when no config entry exists (the caller falls back to the builtin).
    /// Matches <c>git_diff_driver_load</c>'s config phase (diff_driver.c:246-324).
    /// </summary>
    private async Task<DiffDriver?> LoadFromConfigAsync(string driverName, CancellationToken cancellationToken)
    {
        DiffDriverType type = DiffDriverType.Auto;
        GitDiffOptionsFlags binaryFlags = default;
        RegexAdapter? wordRegex = null;
        var patterns = new List<DiffDriverPattern>();
        bool foundDriver = false;
        GitConfiguration cfg = _repo.Config;

        // diff.<name>.binary (tri-state: true → Binary, false → force text,
        // unset/invalid → auto). C (diff_driver.c:257-271) reads with default
        // -1 via git_config__get_bool_force; a non-boolean value (e.g. "auto")
        // falls into the switch's default → no action. GetBoolAsync would
        // return false for a non-boolean and wrongly force text.
        string binaryKey = $"diff.{driverName}.binary";
        if (await cfg.GetEntryAsync(binaryKey, cancellationToken).ConfigureAwait(false) is { } binaryEntry
            && binaryEntry.ValueBytes is { } binaryValueBytes
            && ConfigurationValueParser.TryParseBool(binaryValueBytes.Span, out bool binaryVal))
        {
            if (binaryVal)
            {
                _cache[driverName] = _repo.Context.DiffDrivers.Binary;
                return _repo.Context.DiffDrivers.Binary;
            }

            binaryFlags = GitDiffOptionsFlags.ForceText;
            foundDriver = true;
        }

        // diff.<name>.xfuncname (multivar: newline-separated patterns).
        // C (diff_driver.c:296-303): a missing key (GIT_ENOTFOUND) just
        // continues.
        foreach (string val in await GetMultiOrEmptyAsync(cfg, $"diff.{driverName}.xfuncname", cancellationToken).ConfigureAwait(false))
        {
            AddPatterns(patterns, val, RegexFlags.None);
        }

        // diff.<name>.funcname (multivar)
        foreach (string val in await GetMultiOrEmptyAsync(cfg, $"diff.{driverName}.funcname", cancellationToken).ConfigureAwait(false))
        {
            AddPatterns(patterns, val, RegexFlags.None);
        }

        if (patterns.Count > 0)
        {
            type = DiffDriverType.PatternList;
            foundDriver = true;
        }

        // diff.<name>.wordregex
        string? wordRegexStr = await cfg.GetStringAsync($"diff.{driverName}.wordregex", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(wordRegexStr))
        {
            try
            {
                wordRegex = RegexAdapter.Compile(ConvertPosixClasses(wordRegexStr), RegexFlags.None);
                foundDriver = true;
            }
            catch (ArgumentException)
            {
                // Bad regex — ignore (TODO: warn)
            }
        }

        if (!foundDriver)
        {
            // No config found — the caller falls back to the builtin/Auto.
            return null;
        }

        return new DiffDriver(driverName)
        {
            Type = type,
            BinaryFlags = binaryFlags,
            WordRegex = wordRegex,
            FnPatterns = patterns.ToImmutableList(),
        };
    }

    private static async Task<IReadOnlyList<string>> GetMultiOrEmptyAsync(GitConfiguration cfg, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await cfg.GetMultiAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>
    /// Loads a built-in driver from the <c>userdiff.h</c> table. Matches
    /// <c>git_diff_driver_builtin</c> (diff_driver.c:178-221).
    /// </summary>
    private static DiffDriver? LoadBuiltin(string driverName)
    {
        DiffDriverDefinition? ddef = null;
        foreach (DiffDriverDefinition def in s_builtinDefs)
        {
            if (string.Equals(def.Name, driverName, StringComparison.OrdinalIgnoreCase))
            {
                ddef = def;
                break;
            }
        }

        if (ddef is null)
        {
            return null;
        }

        var patterns = new List<DiffDriverPattern>();
        RegexAdapter? wordRegex = null;

        if (!string.IsNullOrEmpty(ddef.Fns))
        {
            AddPatterns(patterns, ddef.Fns, ddef.Flags);
        }

        if (!string.IsNullOrEmpty(ddef.Words))
        {
            string wordPattern = ddef.Words + WordDefault;
            try
            {
                wordRegex = RegexAdapter.Compile(ConvertPosixClasses(wordPattern), ddef.Flags);
            }
            catch (ArgumentException)
            {
                // Bad regex — ignore
            }
        }

        return new DiffDriver(ddef.Name)
        {
            Type = DiffDriverType.PatternList,
            WordRegex = wordRegex,
            FnPatterns = patterns.ToImmutableList(),
        };
    }

    /// <summary>
    /// Parses newline-separated funcname patterns and appends them to
    /// <paramref name="patterns" />. Matches <c>diff_driver_add_patterns</c>
    /// (diff_driver.c:78-120). Each pattern may be prefixed with <c>!</c> to
    /// negate. Bad patterns are silently ignored (matches C's "ignore bad
    /// patterns" behavior).
    /// </summary>
    private static void AddPatterns(List<DiffDriverPattern> patterns, string regexStr, RegexFlags flags)
    {
        ReadOnlySpan<char> scan = regexStr.AsSpan();
        while (!scan.IsEmpty)
        {
            bool negate = false;
            if (scan[0] == '!')
            {
                negate = true;
                scan = scan[1..];
            }

            int nl = scan.IndexOf('\n');
            ReadOnlySpan<char> patternSpan;
            if (nl >= 0)
            {
                patternSpan = scan[..nl];
                scan = scan[(nl + 1)..];
            }
            else
            {
                patternSpan = scan;
                scan = [];
            }

            if (patternSpan.IsEmpty)
            {
                continue;
            }

            try
            {
                var regex = RegexAdapter.Compile(ConvertPosixClasses(patternSpan.ToString()), flags);
                patterns.Add(new DiffDriverPattern(regex, negate));
            }
            catch (ArgumentException)
            {
                // Ignore bad patterns — matches C's behavior (diff_driver.c:108-111).
            }
        }
    }

    /// <summary>
    /// Converts POSIX character classes (<c>[:space:]</c>, <c>[:alpha:]</c>,
    /// <c>[:alnum:]</c>) to .NET Regex equivalents. .NET Regex does not
    /// support POSIX classes natively.
    /// </summary>
    private static string ConvertPosixClasses(string pattern)
        => pattern
            .Replace("[:alpha:]", "a-zA-Z", StringComparison.Ordinal)
            .Replace("[:alnum:]", "a-zA-Z0-9", StringComparison.Ordinal)
            .Replace("[:space:]", "\\s", StringComparison.Ordinal);
}

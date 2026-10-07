// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.Attributes;

/// <summary>
/// Registry of <c>[attr]</c> macro definitions + the built-in <c>binary</c>
/// macro. Managed port of the macro cache in <c>src/libgit2/attrcache.c</c>.
/// </summary>
/// <remarks>
/// Macros store only the expanded assignments (no <see cref="FnMatchPattern"/>
/// — macros are looked up by name, never matched against paths). Matches
/// <c>git_attr_add_macro</c> (attr.c:464-501) + the built-in
/// <c>binary</c> = <c>-diff -merge -text -crlf</c> (attrcache.c:446).
/// </remarks>
internal sealed class AttrMacroRegistry
{
    private readonly Dictionary<string, Dictionary<string, AttrAssignment>> _macros = [];

    /// <summary>The built-in macro values. From <c>attrcache.c:446</c>.</summary>
    public const string BinaryMacroName = "binary";
    public const string BinaryMacroValues = "-diff -merge -text -crlf";

    public AttrMacroRegistry() => AddMacro(BinaryMacroName, BinaryMacroValues.AsSpan());

    /// <summary>
    /// Registers a macro by parsing its assignment text. Matches
    /// <c>git_attr_add_macro</c> (attr.c:464-501).
    /// </summary>
    public void AddMacro(string name, ReadOnlySpan<char> valuesSpan)
    {
        var assigns = new Dictionary<string, AttrAssignment>();
        AttributesFile.ParseAssignments(valuesSpan, this, assigns);
        if (assigns.Count > 0)
        {
            _macros[name] = assigns;
        }
    }

    /// <summary>Returns the macro's expanded assignments, or null if no macro with that name.</summary>
    public IReadOnlyDictionary<string, AttrAssignment>? Lookup(string name)
        => _macros.TryGetValue(name, out Dictionary<string, AttrAssignment>? a) ? a : null;
}

/// <summary> A parsed <c>.gitattributes</c> file. Managed port of the read side of <c>src/libgit2/attr_file.c</c> — <c>git_attr_file</c> +
/// <c>git_attr_file__parse_buffer</c> + <c>git_attr_file__lookup_one</c>. </summary> <remarks> Reads the repo-root, index, HEAD, and commit sources. Macros (<c>[attr]</c> syntax + built-in <c>binary</c>) included. </remarks>
internal sealed class AttributesFile
{
    /// <summary>
    /// Files larger than this are treated as nonexistent/empty — no rules
    /// are contributed. Matches <c>GIT_ATTR_MAX_FILE_SIZE</c>
    /// (<c>attr_file.h:24</c>, 100 MB).
    /// </summary>
    public const long MaxFileSize = 100L * 1024 * 1024;

    private readonly List<AttrRule> _rules = [];

    /// <summary>The parsed rules (in file order; lookups iterate in reverse).</summary>
    public IReadOnlyList<AttrRule> Rules => _rules;

    /// <summary>
    /// Filesystem stamp (length + mtime) captured at load time for FILE
    /// sources (system/global/info/workdir); null for other sources. Used by
    /// <c>AttributeCache</c> to revalidate files on every lookup, matching
    /// <c>git_attr_file__out_of_date</c> (attrcache.c:263-336).
    /// </summary>
    internal (long Length, DateTime MtimeUtc)? FileStamp { get; set; }

    /// <summary>The index blob OID for INDEX sources; null for others.</summary>
    internal GitOid? BlobStamp { get; set; }

    /// <summary>The full on-disk path for FILE sources; null for others.</summary>
    internal string? SourcePath { get; set; }

    /// <summary>The HEAD TREE oid for HEAD sources; null for others. Matches C's cache breaker (attr_file.c:243-244, git_oid_cpy(..., git_tree_id)). A missing
    /// file is negative-cached as an empty file carrying the same stamp.</summary>
    internal GitOid? HeadStamp { get; set; }

    /// <summary>
    /// Parses <c>.gitattributes</c> text into rules. Matches
    /// <c>git_attr_file__parse_buffer</c> (attr_file.c:345-406).
    /// </summary>
    /// <param name="text">The file content (may have a UTF-8 BOM).</param>
    /// <param name="macros">The macro registry (for <c>[attr]</c> expansion). May be null.</param>
    /// <param name="ignoreCase">Whether to set <c>ICASE</c> on patterns (from <c>core.ignorecase</c>).</param>
    /// <param name="context">The subdirectory context (for files in subdirs), or null for root.</param>
    public void ParseBuffer(string text, AttrMacroRegistry? macros, bool ignoreCase, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Skip UTF-8 BOM if present — matches git_str_detect_bom (attr_file.c:222-225).
        if (text.Length >= 3 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        FnMatchPattern.Flag incomingFlags = FnMatchPattern.Flag.AllowNeg | FnMatchPattern.Flag.AllowMacro;
        if (ignoreCase)
        {
            incomingFlags |= FnMatchPattern.Flag.ICase;
        }

        int pos = 0;
        while (pos < text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos, StringComparison.Ordinal);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            ReadOnlySpan<char> line = text.AsSpan(pos, lineEnd - pos);
            pos = lineEnd + 1;

            // C's
            // git_attr_fnmatch__parse skips leading whitespace BEFORE
            // scanning for the pattern/assignment split (attr_file.c:736-739).
            // A split at index 0 for an indented line yields an empty pattern
            // span and drops the whole rule; the trimmed span lets
            // FnMatchPattern.Parse (which also skips
            // leading whitespace) see the real pattern.
            ReadOnlySpan<char> trimmedLine = line;
            while (trimmedLine.Length > 0 && IsAsciiSpace(trimmedLine[0]))
            {
                trimmedLine = trimmedLine[1..];
            }

            // Split pattern from assignments at the first non-escaped whitespace.
            int splitIdx = FindPatternEnd(trimmedLine);
            ReadOnlySpan<char> patternSpan = trimmedLine[..splitIdx];
            ReadOnlySpan<char> assignSpan = splitIdx < trimmedLine.Length ? trimmedLine[splitIdx..] : [];

            var pattern = FnMatchPattern.Parse(patternSpan, context, incomingFlags);
            if (pattern is null)
            {
                continue; // blank or comment line
            }

            // Macro definition: [attr]name values — register in the macro registry. C (attr_file.c:389-392): a macro line where macros are NOT allowed
            // (any.gitattributes below the repo root, allow_macros=false) is silently DROPPED, never turned into a match rule.
            if ((pattern.Flags & FnMatchPattern.Flag.Macro) != 0)
            {
                macros?.AddMacro(pattern.Pattern.ToUtf8String(), assignSpan);
                continue;
            }

            var rule = new AttrRule(pattern);
            if (ParseAssignments(assignSpan, macros, rule.Assigns) == 0)
            {
                continue; // pattern with no assignments — GIT_ENOTFOUND
            }

            _rules.Add(rule);
        }
    }

    /// <summary> Byte-faithful overload. Parses <c>.gitattributes</c> raw bytes so non-UTF-8 patterns round-trip byte-exact. The pattern portion is parsed via
    /// <see cref="FnMatchPattern.Parse(ReadOnlySpan{byte}, GitPath, FnMatchPattern.Flag)"/>; the assignment span is decoded to chars for <see cref="ParseAssignments"/>
    /// (attribute names/values are ASCII in practice). </summary>
    public void ParseBuffer(ReadOnlyMemory<byte> content, AttrMacroRegistry? macros, bool ignoreCase, GitPath context = default)
    {
        ReadOnlySpan<byte> span = content.Span;

        // Skip UTF-8 BOM (EF BB BF) — matches git_str_detect_bom.
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        FnMatchPattern.Flag incomingFlags = FnMatchPattern.Flag.AllowNeg | FnMatchPattern.Flag.AllowMacro;
        if (ignoreCase)
        {
            incomingFlags |= FnMatchPattern.Flag.ICase;
        }

        int pos = 0;
        while (pos < span.Length)
        {
            int lineEnd = span[pos..].IndexOf((byte)'\n');
            if (lineEnd < 0)
            {
                lineEnd = span.Length;
            }
            else
            {
                lineEnd += pos;
            }

            ReadOnlySpan<byte> line = span[pos..lineEnd];
            pos = lineEnd + 1;

            // C's
            // git_attr_fnmatch__parse skips leading whitespace BEFORE
            // scanning for the pattern/assignment split (attr_file.c:736-739).
            // A split at index 0 for an indented line yields an empty pattern
            // span and drops the whole rule.
            ReadOnlySpan<byte> trimmedLine = line;
            while (trimmedLine.Length > 0 && IsAsciiSpace(trimmedLine[0]))
            {
                trimmedLine = trimmedLine[1..];
            }

            int splitIdx = FindPatternEndBytes(trimmedLine);
            ReadOnlySpan<byte> patternSpan = trimmedLine[..splitIdx];
            ReadOnlySpan<byte> assignBytes = splitIdx < trimmedLine.Length ? trimmedLine[splitIdx..] : [];

            var pattern = FnMatchPattern.Parse(patternSpan, context, incomingFlags);
            if (pattern is null)
            {
                continue;
            }

            if ((pattern.Flags & FnMatchPattern.Flag.Macro) != 0)
            {
                // C (attr_file.c:389-392): dropped when macros are not allowed.
                macros?.AddMacro(pattern.Pattern.ToUtf8String(), System.Text.Encoding.UTF8.GetString(assignBytes));
                continue;
            }

            var rule = new AttrRule(pattern);
            string assignText = System.Text.Encoding.UTF8.GetString(assignBytes);
            if (ParseAssignments(assignText.AsSpan(), macros, rule.Assigns) == 0)
            {
                continue;
            }

            _rules.Add(rule);
        }
    }

    private static int FindPatternEndBytes(ReadOnlySpan<byte> line)
    {
        bool escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            byte c = line[i];
            if (c == (byte)'\\' && !escaped)
            {
                escaped = true;
                continue;
            }

            if (IsAsciiSpace(c) && !escaped)
            {
                return i;
            }

            escaped = false;
        }

        return line.Length;
    }

    private static bool IsAsciiSpace(byte c)
        => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\v' or (byte)'\f';

    /// <summary>ASCII <c>git__isspace</c> (ctype_compat.h:43-47) — not
    /// <see cref="char.IsWhiteSpace(char)"/>, which also splits on Unicode
    /// whitespace (NBSP, U+2028, …) that C keeps inside names.</summary>
    private static bool IsAsciiSpace(char c)
        => c is ' ' or '\t' or '\n' or '\f' or '\r' or '\v';

    /// <summary>
    /// Finds the index of the first non-escaped whitespace in the line (the
    /// boundary between pattern and assignments). When <c>AllowSpace</c> is
    /// not set (the attributes case), spaces/tabs terminate the pattern.
    /// Matches the scan loop in <c>git_attr_fnmatch__parse</c> (attr_file.c:763-784).
    /// </summary>
    private static int FindPatternEnd(ReadOnlySpan<char> line)
    {
        bool escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && !escaped)
            {
                escaped = true;
                continue;
            }

            if (IsAsciiSpace(c) && !escaped)
            {
                return i;
            }

            escaped = false;
        }

        return line.Length;
    }

    /// <summary>
    /// Parses attribute assignments from a text span. Matches
    /// <c>git_attr_assignment__parse</c> (attr_file.c:886-993). Returns the
    /// number of assignments parsed. Macro expansion (when an assignment's
    /// value is True and a macro with that name exists) is performed inline
    /// — the macro's assignments are inserted first, then the assignment
    /// itself (so it overrides any duplicate from the macro).
    /// </summary>
    [SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "Faithful port of git_attr_assignment__parse — cyclomatic complexity matches C original")]
    internal static int ParseAssignments(
        ReadOnlySpan<char> span,
        AttrMacroRegistry? macros,
        Dictionary<string, AttrAssignment> assigns)
    {
        int pos = 0;
        while (pos < span.Length && span[pos] != '\n')
        {
            // Skip leading whitespace (except newline).
            while (pos < span.Length && IsAsciiSpace(span[pos]) && span[pos] != '\n')
            {
                pos++;
            }

            if (pos >= span.Length || span[pos] == '\n' || span[pos] == '#')
            {
                break; // comment or end of line
            }

            GitAttrValue value = GitAttrValue.True;

            // Magic name prefixes: - = false, ! = unset.
            if (span[pos] == '-')
            {
                value = GitAttrValue.False;
                pos++;
            }
            else if (span[pos] == '!')
            {
                value = GitAttrValue.Unset;
                pos++;
            }

            // Find the name (until whitespace or '=').
            int nameStart = pos;
            uint hash = 5381u;
            while (pos < span.Length && !IsAsciiSpace(span[pos]) && span[pos] != '=')
            {
                hash = ((hash << 5) + hash) + (byte)span[pos];
                pos++;
            }

            if (pos == nameStart)
            {
                // Lone prefix or leading '=' — skip the token.
                while (pos < span.Length && !IsAsciiSpace(span[pos]))
                {
                    pos++;
                }

                continue;
            }

            string name = span[nameStart..pos].ToString();

            // Check for '=value'.
            if (pos < span.Length && span[pos] == '=')
            {
                pos++; // skip '='
                int valueStart = pos;
                while (pos < span.Length && !IsAsciiSpace(span[pos]))
                {
                    pos++;
                }

                if (pos > valueStart)
                {
                    value = GitAttrValue.Specified(span[valueStart..pos].ToString());
                }
            }

            // Macro expansion: when value is True and a macro with this name
            // exists, insert the macro's assignments first — matches
            // git_attr_assignment__parse (attr_file.c:957-976). The macro
            // being defined is NOT yet in the registry (added after parse
            // returns), so self-referential macros don't recurse.
            if (value.IsTrue && macros is not null && macros.Lookup(name) is { } macroAssigns)
            {
                foreach (AttrAssignment massign in macroAssigns.Values)
                {
                    assigns[massign.Name] = massign;
                }
            }

            // Insert the assignment itself (after macro expansion, so it
            // overrides any duplicate from the macro).
            assigns[name] = new AttrAssignment(name, hash, value);
        }

        return assigns.Count;
    }

    /// <summary>
    /// Looks up a single attribute for a path. Matches
    /// <c>git_attr_file__lookup_one</c> (attr_file.c:420-446). Iterates rules
    /// in reverse order (bottom-to-top — later rules have higher priority).
    /// </summary>
    /// <returns>The attribute value, or <see cref="GitAttrValue.None"/> if no rule matches.</returns>
    public GitAttrValue LookupOne(AttrPath path, string attrName)
    {
        for (int i = _rules.Count - 1; i >= 0; i--)
        {
            AttrRule rule = _rules[i];
            if (!RuleMatches(rule, path))
            {
                continue;
            }

            if (rule.LookupAssignment(attrName) is { } assign)
            {
                return assign.Value;
            }
        }

        return GitAttrValue.None;
    }

    /// <summary>
    /// Tests whether a rule matches a path. Matches
    /// <c>git_attr_rule__match</c> (attr_file.c:538-548) — the fnmatch result
    /// is inverted for negative (<c>!</c>) patterns.
    /// </summary>
    private static bool RuleMatches(AttrRule rule, AttrPath path)
    {
        bool matched = rule.Match.Match(path);
        return (rule.Match.Flags & FnMatchPattern.Flag.Negative) != 0 ? !matched : matched;
    }

    /// <summary>
    /// Computes the djb2 name hash. Matches <c>git_attr_file__name_hash</c>
    /// (attr_file.c:408-418).
    /// </summary>
    public static uint NameHash(string name)
    {
        uint h = 5381u;
        foreach (char c in name)
        {
            h = ((h << 5) + h) + (byte)c;
        }

        return h;
    }

    /// <summary> Loads a <c>.gitattributes</c> file from the workdir (filesystem). Matches the <c>GIT_ATTR_FILE_SOURCE_FILE</c> path in
    /// <c>git_attr_file__load</c> (attr_file.c:151-168). Byte-faithful: the FS-boundary <c>Path.Join</c> routes through <see
    /// cref="GitPath.ToFileSystemString"/>. </summary> <returns>The parsed file, or <c>null</c> if the file does not exist.</returns>
    public static ValueTask<AttributesFile?> LoadFromWorkdirAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitPath relativePath,
        CancellationToken cancellationToken = default)
    {
        string? workdir = repo.Workdir;
        if (string.IsNullOrEmpty(workdir))
        {
            return ValueTask.FromResult<AttributesFile?>(null);
        }

        // FS boundary: single transcode point.
        string fullPath = Path.Join(workdir, relativePath.ToFileSystemString());
        if (!File.Exists(fullPath))
        {
            // Absence is the majority case when probing for attribute files.
            return ValueTask.FromResult<AttributesFile?>(null);
        }

        // C (attr_file.c:159-161): files larger than GIT_ATTR_MAX_FILE_SIZE
        // are treated as nonexistent — no rules are contributed.
        var info = new FileInfo(fullPath);
        if (info.Length > MaxFileSize)
        {
            return ValueTask.FromResult<AttributesFile?>(null);
        }

        return new ValueTask<AttributesFile?>(LoadFromWorkdirSlowAsync(fullPath, macros, ignoreCase, relativePath, info, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LoadFromWorkdirAsync(GitRepository, AttrMacroRegistry?, bool, GitPath, CancellationToken)"/>: reads the attribute file (disk IO).</summary>
    private static async Task<AttributesFile?> LoadFromWorkdirSlowAsync(
        string fullPath,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitPath relativePath,
        FileInfo info,
        CancellationToken cancellationToken)
    {
        // Read raw bytes so non-UTF-8 patterns round-trip byte-exact.
        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var file = new AttributesFile();
        GitPath context = GetContextBytes(relativePath);
        file.ParseBuffer(content, macros, ignoreCase, context);
        file.FileStamp = (info.Length, info.LastWriteTimeUtc);
        file.SourcePath = fullPath;
        return file;
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    public static ValueTask<AttributesFile?> LoadFromWorkdirAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        string relativePath = ".gitattributes",
        CancellationToken cancellationToken = default)
        => LoadFromWorkdirAsync(repo, macros, ignoreCase, GitPath.FromUtf8String(relativePath), cancellationToken);

    /// <summary>
    /// Loads a <c>.gitattributes</c> file from the index (staged blob).
    /// UTF-8 convenience — the byte-parity surface is
    /// <see cref="LoadFromIndexAsync(GitRepository, AttrMacroRegistry?, bool, GitPath, CancellationToken)"/>.
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if not staged in the index.</returns>
    public static ValueTask<AttributesFile?> LoadFromIndexAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        string relativePath = ".gitattributes",
        CancellationToken cancellationToken = default)
        => LoadFromIndexAsync(repo, macros, ignoreCase, GitPath.FromUtf8String(relativePath), cancellationToken);

    /// <summary> Loads a <c>.gitattributes</c> file from the index (staged blob) at a byte-faithful path. Matches the <c>GIT_ATTR_FILE_SOURCE_INDEX</c> path in
    /// <c>git_attr_file__load</c> (attr_file.c:136-150). The index probe is byte-keyed — non-UTF-8 directories resolve. </summary> <returns>The parsed file, or
    /// <c>null</c> if not staged in the index.</returns>
    public static async ValueTask<AttributesFile?> LoadFromIndexAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitPath relativePath,
        CancellationToken cancellationToken = default)
    {
        GitIndex? index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return null;
        }

        GitIndexEntry? entry = index.EntryByPath(relativePath);
        if (!entry.HasValue)
        {
            return null;
        }

        GitBlob? blob = await repo.Objects.LookupAsync<GitBlob>(entry.Value.Id, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        // C (attr_file.c:146-148): blobs larger than GIT_ATTR_MAX_FILE_SIZE
        // are skipped (no file is created).
        if (blob.Content.Length > MaxFileSize)
        {
            return null;
        }

        // Pass raw bytes so non-UTF-8 patterns round-trip byte-exact. The blob content is a retained buffer; slice it into a fresh byte[] for the parser to
        // own.
        byte[] content = blob.Content.Span.ToArray();
        var file = new AttributesFile();
        GitPath context = GetContextBytes(relativePath);
        file.ParseBuffer(content, macros, ignoreCase, context);
        file.BlobStamp = entry.Value.Id;
        return file;
    }

    /// <summary>
    /// Loads a <c>.gitattributes</c> file from an arbitrary path on disk.
    /// Used for <c>core.attributesfile</c> (global) and system paths. Matches
    /// the <c>GIT_ATTR_FILE_SOURCE_FILE</c> path in <c>git_attr_file__load</c>
    /// with <c>entry-&gt;fullpath</c> set to an absolute path.
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if the file does not exist.</returns>
    public static ValueTask<AttributesFile?> LoadFromPathAsync(
        AttrMacroRegistry? macros,
        bool ignoreCase,
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return ValueTask.FromResult<AttributesFile?>(null);
        }

        // C (attr_file.c:159-161): files larger than GIT_ATTR_MAX_FILE_SIZE
        // are treated as nonexistent.
        var info = new FileInfo(fullPath);
        if (info.Length > MaxFileSize)
        {
            return ValueTask.FromResult<AttributesFile?>(null);
        }

        return new ValueTask<AttributesFile?>(LoadFromPathSlowAsync(macros, ignoreCase, fullPath, info, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LoadFromPathAsync"/>: reads the attribute file (disk IO).</summary>
    private static async Task<AttributesFile?> LoadFromPathSlowAsync(
        AttrMacroRegistry? macros,
        bool ignoreCase,
        string fullPath,
        FileInfo info,
        CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var file = new AttributesFile();
        file.ParseBuffer(content, macros, ignoreCase, default);
        file.FileStamp = (info.Length, info.LastWriteTimeUtc);
        file.SourcePath = fullPath;
        return file;
    }

    /// <summary>
    /// Loads <c>.git/info/attributes</c> from the repository's git directory.
    /// Matches the <c>GIT_ATTR_FILE_INREPO</c> source in
    /// <c>collect_attr_files</c> (attr.c:668-672).
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if it does not exist.</returns>
    public static ValueTask<AttributesFile?> LoadFromInfoAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        CancellationToken cancellationToken = default)
    {
        string infoPath = Path.Join(repo.Path, "info", "attributes");
        return LoadFromPathAsync(macros, ignoreCase, infoPath, cancellationToken);
    }

    /// <summary>
    /// Loads the global <c>.gitattributes</c> from <c>core.attributesfile</c>
    /// config. Matches the <c>attr_cfg_file</c> path in
    /// <c>collect_attr_files</c> (attr.c:690-699).
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if the config key is unset or the file does not exist.</returns>
    public static async ValueTask<AttributesFile?> LoadFromGlobalAsync(
        GitRepository repo,
        AttrMacroRegistry macros,
        bool ignoreCase,
        CancellationToken cancellationToken = default)
    {
        GitPath? attrFile = await repo.Config.GetPathAsync("core.attributesfile", cancellationToken).ConfigureAwait(false);
        if (attrFile is null || attrFile.Value.IsEmpty)
        {
            // C (attrcache.c:322-353, attr_cache__lookup_path): with the
            // config key unset (or empty), fall back to the XDG attributes
            // file $XDG_CONFIG_HOME/git/attributes
            // (git_sysdir_find_xdg_file, GIT_ATTR_FILE_XDG = "attributes").
            attrFile = GitPath.FromFileSystemString(repo.Context.Dirs.FindXdgFile("attributes") ?? string.Empty);
            if (attrFile.Value.IsEmpty)
            {
                return null;
            }
        }

        return await LoadFromPathAsync(macros, ignoreCase, attrFile.Value.ToFileSystemString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the system-wide <c>gitattributes</c> file. Matches
    /// <c>system_attr_file</c> (attr.c:340-376) which uses
    /// <c>git_sysdir_find_system_file</c>.
    /// </summary>
    /// <param name="dirs">The directory resolver used to locate the system file.</param>
    /// <returns>The parsed file, or <c>null</c> if no system file is found.</returns>
    /// <param name="macros">Attribute macros available during parsing.</param>
    /// <param name="ignoreCase">Whether path matching ignores case.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static ValueTask<AttributesFile?> LoadFromSystemAsync(
        GitSystemDirs dirs,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        CancellationToken cancellationToken = default)
    {
        string? systemPath = dirs.FindSystemFile("gitattributes");
        if (string.IsNullOrEmpty(systemPath))
        {
            // No system file — the majority case.
            return ValueTask.FromResult<AttributesFile?>(null);
        }

        return LoadFromPathAsync(macros, ignoreCase, systemPath, cancellationToken);
    }

    /// <summary>
    /// Loads <c>.gitattributes</c> from the HEAD commit's tree. Matches
    /// the <c>GIT_ATTR_FILE_SOURCE_HEAD</c> path in <c>git_attr_file__load</c>
    /// (attr_file.c:169-168). The file is read from the tree root.
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if not present in HEAD.</returns>
    public static async ValueTask<AttributesFile?> LoadFromHeadAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        CancellationToken cancellationToken = default)
        => await LoadFromHeadAsync(repo, macros, ignoreCase, ".gitattributes", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Loads <c>.gitattributes</c> from the HEAD commit's tree at a relative
    /// path. UTF-8 convenience — the byte-parity surface is
    /// <see cref="LoadFromHeadAsync(GitRepository, AttrMacroRegistry?, bool, GitPath, CancellationToken)"/>.
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if not present in HEAD.</returns>
    public static ValueTask<AttributesFile?> LoadFromHeadAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        string relativePath,
        CancellationToken cancellationToken = default)
        => LoadFromHeadAsync(repo, macros, ignoreCase, GitPath.FromUtf8String(relativePath), cancellationToken);

    /// <summary> Loads <c>.gitattributes</c> from the HEAD commit's tree at a byte-faithful path. Matches the <c>GIT_ATTR_FILE_SOURCE_HEAD</c> path in
    /// <c>git_attr_file__load</c> (attr_file.c:169-168). </summary> <returns>The parsed file, or <c>null</c> if not present in
    /// HEAD.</returns>
    public static async ValueTask<AttributesFile?> LoadFromHeadAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitPath relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(macros);

        if (await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false) is not GitDirectReference head)
        {
            return null;
        }

        Commit? headCommit = await repo.Objects.LookupAsync<Commit>(head.Target, cancellationToken).ConfigureAwait(false);
        if (headCommit is null)
        {
            return null;
        }

        GitTree? tree = await repo.Objects.LookupAsync<GitTree>(headCommit.Tree, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return null;
        }

        AttributesFile? file = await LoadFromTreeAsync(repo, macros, ignoreCase, tree, relativePath, cancellationToken).ConfigureAwait(false);

        // C (attr_file.c:180-189, 243-244): a missing .gitattributes in the HEAD tree is negative-cached as an EMPTY file, and HEAD files are stamped with the
        // TREE oid (not the commit oid) — two commits that share a tree share the cache entry, and the missing case is re-resolved only when the tree changes.
        file ??= new AttributesFile();
        file.HeadStamp = headCommit.Tree;

        return file;
    }

    /// <summary>
    /// Loads <c>.gitattributes</c> from a specific commit's tree. Matches
    /// the <c>GIT_ATTR_FILE_SOURCE_COMMIT</c> path in <c>git_attr_file__load</c>
    /// (attr_file.c:169-215).
    /// </summary>
    /// <returns>The parsed file, or <c>null</c> if not present in the commit.</returns>
    public static async ValueTask<AttributesFile?> LoadFromCommitAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitOid commitId,
        CancellationToken cancellationToken = default)
        => await LoadFromCommitAsync(repo, macros, ignoreCase, commitId, ".gitattributes", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Loads <c>.gitattributes</c> from a specific commit's tree at a
    /// relative path. UTF-8 convenience — the byte-parity surface is
    /// <see cref="LoadFromCommitAsync(GitRepository, AttrMacroRegistry?, bool, GitOid, GitPath, CancellationToken)"/>.
    /// </summary>
    public static ValueTask<AttributesFile?> LoadFromCommitAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitOid commitId,
        string relativePath,
        CancellationToken cancellationToken = default)
        => LoadFromCommitAsync(repo, macros, ignoreCase, commitId, GitPath.FromUtf8String(relativePath), cancellationToken);

    /// <summary> Loads <c>.gitattributes</c> from a specific commit's tree at a byte-faithful path. Matches the <c>GIT_ATTR_FILE_SOURCE_COMMIT</c> path. </summary>
    public static async ValueTask<AttributesFile?> LoadFromCommitAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitOid commitId,
        GitPath relativePath,
        CancellationToken cancellationToken = default)
    {
        Commit? commit = await repo.Objects.LookupAsync<Commit>(commitId, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return null;
        }

        GitTree? tree = await repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return null;
        }

        return await LoadFromTreeAsync(repo, macros, ignoreCase, tree, relativePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Loads <c>.gitattributes</c> from a tree by path. Shared by <see cref="LibGit2CS.Attributes.AttributesFile.LoadFromHeadAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.Attributes.AttrMacroRegistry?, bool, System.Threading.CancellationToken)"/> and <see cref="LibGit2CS.Attributes.AttributesFile.LoadFromCommitAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.Attributes.AttrMacroRegistry?, bool, LibGit2CS.Core.GitOid, System.Threading.CancellationToken)"/>.
    /// byte-faithful path — the tree walk and the context extraction operate on raw bytes. </summary>
    private static async ValueTask<AttributesFile?> LoadFromTreeAsync(
        GitRepository repo,
        AttrMacroRegistry? macros,
        bool ignoreCase,
        GitTree tree,
        GitPath relativePath,
        CancellationToken cancellationToken)
    {
        GitTreeEntry? entry = await tree.EntryByPathAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (!entry.HasValue)
        {
            return null;
        }

        GitBlob? blob = await repo.Objects.LookupAsync<GitBlob>(entry.Value.Id, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        // C (attr_file.c:200-205): blobs larger than GIT_ATTR_MAX_FILE_SIZE
        // are skipped (no file is created).
        if (blob.Content.Length > MaxFileSize)
        {
            return null;
        }

        byte[] content = blob.Content.Span.ToArray();
        var file = new AttributesFile();
        GitPath context = GetContextBytes(relativePath);
        file.ParseBuffer(content, macros, ignoreCase, context);
        return file;
    }

    /// <summary>
    /// Extracts the subdirectory context from a relative path (e.g.
    /// <c>subdir/.gitattributes</c> → <c>subdir/</c>). Matches the context
    /// extraction in <c>git_attr_file__parse_buffer</c> (attr_file.c:353-355).
    /// Returns null for root-level files.
    /// </summary>
    private static string? GetContext(string relativePath)
    {
        // Only set context for files below the repo root that end with "/.gitattributes".
        const string Suffix = "/" + ".gitattributes";
        if (relativePath.EndsWith(Suffix, StringComparison.Ordinal) &&
            relativePath.Length > Suffix.Length)
        {
            return relativePath[..(relativePath.Length - ".gitattributes".Length)];
        }

        return null;
    }

    /// <summary>Byte-faithful overload of <see cref="GetContext"/>.</summary>
    private static GitPath GetContextBytes(string relativePath)
    {
        string? ctx = GetContext(relativePath);
        return ctx is null ? default : GitPath.FromUtf8String(ctx);
    }

    /// <summary> Byte-faithful context extraction: operates on the raw path bytes — no decode→re-encode round-trip, so non-UTF-8 directories keep their context
    /// byte-exact. </summary>
    private static GitPath GetContextBytes(GitPath relativePath)
    {
        ReadOnlySpan<byte> span = relativePath.Span;
        if (span.EndsWith("/.gitattributes"u8) && span.Length > "/.gitattributes"u8.Length)
        {
            return relativePath.Slice(0, span.Length - ".gitattributes"u8.Length);
        }

        return default;
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.Config;

/// <summary>
/// Read-only access to git configuration across multiple backends and levels.
/// Managed port of libgit2's <c>git_config</c> facade (read side).
/// </summary>
/// <remarks>
/// <para>
/// Configuration aggregates one or more <see cref="IConfigBackend"/> instances,
/// each registered at a <see cref="GitConfigLevel"/>. Lookups walk backends in
/// descending level order (LOCAL before GLOBAL before SYSTEM) and return the
/// first match — higher levels override lower ones.
/// </para>
/// <para>
/// <b>Write side</b> (<c>Set*</c>/<c>Delete*</c>/<c>Lock*</c>/<c>Unlock*</c>)
/// is implemented via <see cref="IConfigBackend"/> write methods.
/// </para>
/// <para>
/// <b><c>get_string</c> readonly restriction dropped.</b> libgit2's
/// <c>git_config_get_string</c> requires a snapshot to prevent dangling pointers
/// after file refresh. In managed code every getter returns an immutable
/// <c>string</c> copy; the restriction is unnecessary. <see cref="SnapshotAsync"/>
/// is retained for callers wanting point-in-time multi-read consistency.
/// </para>
/// <para>
/// <b>Async:</b> every IO-doing method is async-only and threads a
/// <see cref="CancellationToken"/>. <see cref="EnumerateAsync"/> returns
/// <see cref="IAsyncEnumerable{T}"/>. Disposal is <see cref="IAsyncDisposable"/>.
/// </para>
/// </remarks>
public sealed partial class GitConfiguration : IAsyncDisposable
{
    private readonly GitContext _context;
    private readonly List<BackendEntry> _readers = [];
    private bool _disposed;

    [GeneratedRegex("^$")]
    private static partial Regex EmptyValueRegex();

    /// <summary>
    /// Shared "^$" matcher (matches only an empty value — C's
    /// git_config_set_multivar rename pattern, config.c:1595).
    /// </summary>
    private static readonly Regex s_emptyValueRegex = EmptyValueRegex();

    /// <summary>
    /// Creates an empty configuration with no backends. Add backends via
    /// <see cref="AddBackendAsync"/> or <see cref="AddFileOnDiskAsync"/>. Matches
    /// libgit2's <c>git_config_new</c>.
    /// </summary>
    /// <param name="context">The library context owning the directory resolver used
    /// for <c>~</c>-expansion in path values and system/global/XDG config discovery.</param>
    public GitConfiguration(GitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Opens a single on-disk config file at <see cref="GitConfigLevel.Local"/>.
    /// Matches libgit2's <c>git_config_open_ondisk</c>.
    /// </summary>
    /// <param name="path">Path to the config file. Need not exist (results in an empty config).</param>
    /// <param name="context">The library context owning the directory resolver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// For <c>includeIf</c> conditional matching against a repository gitdir,
    /// use <c>new <see cref="GitConfiguration"/>(context)</c> followed by
    /// <see cref="AddFileOnDiskAsync"/> with an explicit
    /// <c>repoGitDirPath</c> argument.
    /// </remarks>
    public static async Task<GitConfiguration> OpenAsync(string path, GitContext context, CancellationToken cancellationToken = default)
    {
        var config = new GitConfiguration(context);
        await config.AddFileOnDiskAsync(path, GitConfigLevel.Local, repoGitDirPath: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        return config;
    }

    /// <summary>
    /// Opens the user's default configuration: global, XDG, system, and
    /// ProgramData levels. Missing files are silently skipped. Matches
    /// libgit2's <c>git_config_open_default</c>.
    /// </summary>
    /// <param name="context">The library context owning the directory resolver used to locate the global/XDG/system/ProgramData config files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<GitConfiguration> OpenDefaultAsync(GitContext context, CancellationToken cancellationToken = default)
    {
        var config = new GitConfiguration(context);
        await config.AddDefaultLevelsAsync(globalFallbackToLocation: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return config;
    }

    /// <summary>
    /// Adds the global, XDG, system, and ProgramData backends to this
    /// configuration, in that priority order. Missing files are silently
    /// skipped. Matches the upper-level portion of libgit2's
    /// <c>load_config</c> (repository.c) — the local and worktree levels are
    /// added by the caller so that the full open path reproduces libgit2's
    /// exact backend ordering (local → worktree → global → xdg → system →
    /// programdata).
    /// </summary>
    /// <param name="globalFallbackToLocation">
    /// Whether the GLOBAL backend falls back to <c>$HOME/.gitconfig</c> when
    /// no existing global file is found. <c>git_config_open_default</c> does
    /// (config.c:1305-1311: <c>!find_global || !global_location</c>, and
    /// config_file_open opens a nonexistent file as an empty backend), while
    /// the repo <c>load_config</c> path does not
    /// (<c>config_path_global</c>, repository.c:1392-1397: only an existing
    /// file yields a backend).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="useEnv">Whether to consult the context environment.</param>
    internal async Task AddDefaultLevelsAsync(bool globalFallbackToLocation, bool useEnv = false, CancellationToken cancellationToken = default)
    {
        string? global = GlobalPath(useEnv) ?? (globalFallbackToLocation ? GlobalLocation() : null);
        if (global is not null)
        {
            await AddFileOnDiskAsync(global, GitConfigLevel.Global, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string? xdg = FindXdg();
        if (xdg is not null)
        {
            await AddFileOnDiskAsync(xdg, GitConfigLevel.Xdg, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string? system = SystemPath(useEnv);
        if (system is not null)
        {
            await AddFileOnDiskAsync(system, GitConfigLevel.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string? programdata = FindProgramData();
        if (programdata is not null)
        {
            await AddFileOnDiskAsync(programdata, GitConfigLevel.ProgramData, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds an on-disk file backend at the given level. Matches libgit2's
    /// <c>git_config_add_file_ondisk</c>.
    /// </summary>
    /// <param name="path">Path to the config file.</param>
    /// <param name="level">Priority level for this file.</param>
    /// <param name="repoGitDirPath">Optional repository gitdir for conditional includes.</param>
    /// <param name="force">If <c>true</c>, replaces any existing backend at this level.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Exists"/> if a backend at <paramref name="level"/> already exists and <paramref name="force"/> is <c>false</c>.
    /// </exception>
    public async Task AddFileOnDiskAsync(string path, GitConfigLevel level, string? repoGitDirPath = null, bool force = false, CancellationToken cancellationToken = default)
    {
        var backend = new FileConfigBackend(path, repoGitDirPath, _context.Dirs);
        await AddBackendAsync(backend, level, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a backend at the given level, opening it first. Matches libgit2's
    /// <c>git_config_add_backend</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Exists"/> if a backend at <paramref name="level"/> already exists and <paramref name="force"/> is <c>false</c>.
    /// </exception>
    internal async Task AddBackendAsync(IConfigBackend backend, GitConfigLevel level, bool force = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (force)
        {
            RemoveBackend(level);
        }
        else if (HasBackend(level))
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"configuration at level {(int)level} already exists",
                GitErrorCategory.Config);
        }

        await backend.OpenAsync(level, cancellationToken).ConfigureAwait(false);

        var entry = new BackendEntry(backend, level);
        int insertAt = _readers.FindIndex(e => e.Level < level);
        if (insertAt < 0)
        {
            _readers.Add(entry);
        }
        else
        {
            _readers.Insert(insertAt, entry);
        }
    }

    /// <summary>
    /// Creates a point-in-time snapshot of this configuration. Subsequent
    /// changes (file refreshes) do not affect the returned snapshot. Matches
    /// libgit2's <c>git_config_snapshot</c>.
    /// </summary>
    public async ValueTask<GitConfiguration> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var snap = new GitConfiguration(_context);
        foreach (BackendEntry entry in _readers)
        {
            IConfigBackend snapBackend = entry.Backend.Snapshot();
            await snap.AddBackendAsync(snapBackend, entry.Level, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return snap;
    }

    /// <summary>
    /// Creates a new configuration containing only the backend at the given
    /// level. Matches libgit2's <c>git_config_open_level</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if no backend exists at <paramref name="level"/>.
    /// </exception>
    public async ValueTask<GitConfiguration> OpenLevelAsync(GitConfigLevel level, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BackendEntry entry = FindBackend(level)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"no configuration exists for the given level '{(int)level}'",
                GitErrorCategory.Config);

        // C (config.c): git_config_open_level returns a config SHARING the
        // writable backend — not a read-only snapshot.
        var result = new GitConfiguration(_context);
        await result.AddBackendAsync(entry.Backend, level, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Opens the global-level config (XDG if it exists, else GLOBAL). Matches
    /// libgit2's <c>git_config_open_global</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if neither XDG nor Global backends exist.
    /// </exception>
    public async ValueTask<GitConfiguration> OpenGlobalAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            return await OpenLevelAsync(GitConfigLevel.Xdg, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            return await OpenLevelAsync(GitConfigLevel.Global, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the entry for <paramref name="name"/>, or <c>null</c> if not found.
    /// Matches libgit2's <c>git_config_get_entry</c>. The name is normalized
    /// (section and variable lowercased; subsection case preserved).
    /// </summary>
    public async ValueTask<GitConfigEntry?> GetEntryAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        using var writer = new PooledByteBufferWriter();
        NormalizeName(writer, name);
        foreach (BackendEntry entry in _readers)
        {
            GitConfigEntry? found = await entry.Backend.GetAsync(writer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (found is { } ce)
            {
                return ce;
            }
        }

        return null;
    }

    /// <summary> Byte-key variant of <see cref="GetEntryAsync(string, CancellationToken)"/>: the name is the raw normalized key bytes — C's
    /// <c>git_config_get_entry</c> normalizes the <c>char *</c> name bytes (config.c:780-833), so non-UTF-8 subsection bytes round-trip byte-exact. The string
    /// overload is the UTF-8 convenience tier on top. </summary>
    public async ValueTask<GitConfigEntry?> GetEntryAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var writer = new PooledByteBufferWriter(name.Span.Length);
        ConfigKeyName.NormalizeNameBytes(writer, name.Span);
        foreach (BackendEntry entry in _readers)
        {
            GitConfigEntry? found = await entry.Backend.GetAsync(writer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (found is { } ce)
            {
                return ce;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the string value for <paramref name="name"/>, or <c>null</c> if not
    /// found. A lone variable (no <c>=</c>) yields the empty string, matching C's
    /// <c>git_config_get_string</c> (config.c:979-985:
    /// <c>*out = !ret ? (entry->value ? entry->value : "") : NULL;</c>).
    /// UTF-8 display convenience — the byte-parity surface is
    /// <see cref="LibGit2CS.Config.GitConfiguration.GetBytesAsync(string, System.Threading.CancellationToken)"/>.
    /// </summary>
    public async ValueTask<string?> GetStringAsync(string name, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : (entry.Value.Value ?? string.Empty);
    }

    /// <summary> Gets the raw value bytes for <paramref name="name"/>, or <c>null</c> if not found. A lone variable (no <c>=</c>) yields an empty array,
    /// matching the string tier's lone-variable mapping. Byte-parity surface: non-UTF-8 values round-trip byte-exact. </summary>
    public async ValueTask<byte[]?> GetBytesAsync(string name, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : entry.Value.ValueBytes is { } b ? b.ToArray() : [];
    }

    /// <summary> Byte-key variant of <see cref="GetBytesAsync(string, CancellationToken)"/>: the name is the raw normalized key bytes — C's
    /// <c>git_config_get_string</c> reads the <c>char *</c> name bytes (config.c:979-985), so non-UTF-8 subsection bytes round-trip byte-exact. </summary>
    public async ValueTask<byte[]?> GetBytesAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : entry.Value.ValueBytes is { } b ? b.ToArray() : [];
    }

    /// <summary>
    /// Gets the string value for <paramref name="name"/>, or
    /// <paramref name="defaultValue"/> if not found.
    /// </summary>
    public async ValueTask<string?> GetStringAsync(string name, string? defaultValue, CancellationToken cancellationToken = default)
        => (await GetStringAsync(name, cancellationToken).ConfigureAwait(false)) ?? defaultValue;

    /// <summary>
    /// Gets the boolean value for <paramref name="name"/>. Lone variables
    /// (no <c>=</c>) are treated as <c>true</c>. Returns
    /// <paramref name="defaultValue"/> if not found or unparseable.
    /// </summary>
    public async ValueTask<bool> GetBoolAsync(string name, bool defaultValue = false, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return defaultValue;
        }

        if (entry.Value.ValueBytes is null)
        {
            // Lone variable → true.
            return true;
        }

        return ConfigurationValueParser.TryParseBool(entry.Value.ValueBytes.GetValueOrDefault().Span, out bool result)
            ? result
            : defaultValue;
    }

    /// <summary>
    /// Reads a bool config value with C configmap-lookup semantics: returns
    /// the given default when the key is absent, <c>true</c> for lone
    /// variables, and <c>null</c> when the value is present but unparseable
    /// (C's <c>git_config_parse_bool</c> failure). Call sites that mirror a
    /// C configmap lookup decide per site whether the parse error is
    /// propagated (e.g. checkout_data_init), swallowed with the flag off
    /// (e.g. dotgit_flags, diff_generated__set_caps), or ignored.
    /// </summary>
    public async ValueTask<bool?> TryGetConfigmapBoolAsync(string name, bool defaultValue, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return defaultValue;
        }

        if (entry.Value.ValueBytes is null)
        {
            return true; // lone variable → true (git__parse_bool, util.c:649-651)
        }

        return ConfigurationValueParser.TryParseBool(entry.Value.ValueBytes.GetValueOrDefault().Span, out bool result)
            ? result
            : null; // parse error
    }

    /// <summary>
    /// Reads a bool config value like a C configmap lookup whose caller
    /// propagates the error (e.g. checkout_data_init, checkout.c:2468-2473):
    /// an unparseable value throws the <c>git_config_parse_bool</c> error
    /// (config.c:1449-1450).
    /// </summary>
    public async ValueTask<bool> GetConfigmapBoolOrThrowAsync(string name, bool defaultValue, CancellationToken cancellationToken = default)
    {
        bool? value = await TryGetConfigmapBoolAsync(name, defaultValue, cancellationToken).ConfigureAwait(false);
        if (value is { } parsed)
        {
            return parsed;
        }

        GitConfigEntry entry = (await GetEntryAsync(name, cancellationToken).ConfigureAwait(false))!.Value;
        string raw = entry.Value ?? "(null)";
        throw new GitException(
            GitErrorCode.Error,
            $"failed to parse '{raw}' as a boolean",
            GitErrorCategory.Config);
    }

    /// <summary>
    /// Gets the 32-bit integer value for <paramref name="name"/> (supports
    /// <c>k</c>/<c>m</c>/<c>g</c> suffixes). Returns
    /// <paramref name="defaultValue"/> if not found or unparseable.
    /// </summary>
    public async ValueTask<int> GetIntAsync(string name, int defaultValue = 0, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Value.ValueBytes is null)
        {
            return defaultValue;
        }

        return ConfigurationValueParser.TryParseInt32(entry.Value.ValueBytes.GetValueOrDefault().Span, out int result)
            ? result
            : defaultValue;
    }

    /// <summary>
    /// Gets the 64-bit integer value for <paramref name="name"/> (supports
    /// <c>k</c>/<c>m</c>/<c>g</c> suffixes). Returns
    /// <paramref name="defaultValue"/> if not found or unparseable.
    /// </summary>
    public async ValueTask<long> GetInt64Async(string name, long defaultValue = 0, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Value.ValueBytes is null)
        {
            return defaultValue;
        }

        return ConfigurationValueParser.TryParseInt64(entry.Value.ValueBytes.GetValueOrDefault().Span, out long result)
            ? result
            : defaultValue;
    }

    /// <summary> Gets the path value for <paramref name="name"/>, expanding a leading <c>~</c> to the home directory. Returns <c>null</c> if not found.
    /// Byte-parity surface: non-<c>~</c> values round-trip their raw bytes verbatim, so non-UTF-8 config paths stay byte-exact. Convert to a filesystem path
    /// via <c>GitPath.ToFileSystemString</c> at the filesystem boundary. </summary>
    public async ValueTask<GitPath?> GetPathAsync(string name, CancellationToken cancellationToken = default)
    {
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Value.ValueBytes is null)
        {
            return null;
        }

        return ConfigurationValueParser.ParsePathBytes(entry.Value.ValueBytes.GetValueOrDefault(), _context.Dirs);
    }

    // ==============================
    // Write side
    // ==============================

    /// <summary> Sets a config key to a string value. Writes through to the first writable file backend. Matches <c>git_config_set_string</c>. UTF-8
    /// convenience tier: the value is UTF-8-encoded into the byte write path — <c>é</c> writes <c>C3 A9</c>. The
    /// byte-parity surface is <see cref="LibGit2CS.Config.GitConfiguration.SetBytesAsync(string, System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>. </summary>
    public async Task SetStringAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetAsync(name, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Sets a config key to a raw byte value. Writes through to the first writable file backend. Byte-parity surface: the value bytes are written
    /// verbatim (ASCII-escaped), so non-UTF-8 values round-trip byte-exact. Matches <c>git_config_set_string</c> over a byte buffer. </summary>
    public async Task SetBytesAsync(string name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetBytesAsync(name, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-key variant of <see cref="SetBytesAsync(string, ReadOnlyMemory{byte}, CancellationToken)"/>: the key is the raw name bytes — C's
    /// <c>git_config_set_multivar</c> passes the normalized <c>char *</c> key bytes to the backend (config.c:1166-1186), so non-UTF-8 subsection bytes write
    /// byte-exact. </summary>
    public async Task SetBytesAsync(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetBytesAsync(name, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a config key to a boolean value. Matches <c>git_config_set_bool</c>.
    /// </summary>
    public async Task SetBoolAsync(string name, bool value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetAsync(name, value ? "true" : "false", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a config key to an integer value. Matches <c>git_config_set_int32</c>.
    /// </summary>
    public async Task SetIntAsync(string name, int value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetAsync(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Sets a multivar config key: updates all entries matching <paramref name="regexp"/> to <paramref name="value"/>, or appends a new entry if none
    /// match. Matches <c>git_config_set_multivar</c>. UTF-8 convenience tier — the byte-parity surface is <see cref="LibGit2CS.Config.GitConfiguration.SetMultiBytesAsync(string, System.Text.RegularExpressions.Regex, System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>. </summary>
    public async Task SetMultiAsync(string name, Regex regexp, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(regexp);
        ArgumentNullException.ThrowIfNull(value);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetMultiAsync(name, regexp, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Sets a multivar config key to a raw byte value: updates all entries matching <paramref name="regexp"/> to <paramref name="value"/>, or appends
    /// a new entry if none match. Byte-parity surface: the value bytes are written verbatim. Matches <c>git_config_set_multivar</c> over a byte buffer.
    /// </summary>
    public async Task SetMultiBytesAsync(string name, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(regexp);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetMultiBytesAsync(name, regexp, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-key variant of <see cref="SetMultiBytesAsync(string, Regex, ReadOnlyMemory{byte}, CancellationToken)"/>: the key is the raw name bytes
    /// (C's <c>git_config_set_multivar</c> passes the normalized <c>char *</c> key bytes to the backend). </summary>
    public async Task SetMultiBytesAsync(ReadOnlyMemory<byte> name, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regexp);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.SetMultiBytesAsync(name, regexp, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a config key. Matches <c>git_config_delete_entry</c>.
    /// </summary>
    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.DeleteKeyAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-key variant of <see cref="DeleteAsync(string, CancellationToken)"/>: the key is the raw name bytes (C's <c>git_config_delete_entry</c>
    /// passes the normalized <c>char *</c> key bytes to the backend). </summary>
    public async Task DeleteAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
    {
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.DeleteKeyAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all entries for <paramref name="name"/> whose value matches
    /// <paramref name="regexp"/>. Matches <c>git_config_delete_multivar</c>.
    /// </summary>
    public async Task DeleteMultiAsync(string name, Regex regexp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(regexp);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.DeleteMultiAsync(name, regexp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-key variant of <see cref="DeleteMultiAsync(string, Regex, CancellationToken)"/>: the key is the raw name bytes (C's
    /// <c>git_config_delete_multivar</c> passes the normalized <c>char *</c> key bytes to the backend). </summary>
    public async Task DeleteMultiAsync(ReadOnlyMemory<byte> name, Regex regexp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regexp);
        FileConfigBackend backend = RequireWritableBackend(name);
        await backend.DeleteMultiAsync(name, regexp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all config entries in the given section. Matches
    /// <c>git_config_rename_section</c> with <c>new_section_name = NULL</c>.
    /// </summary>
    /// <param name="section">The fully-qualified section name (e.g.
    /// <c>branch.master</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteSectionAsync(string section, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(section);
        var toDelete = new List<GitConfigEntry>();
        await foreach (GitConfigEntry ce in EnumerateAsync($"{section}.*", cancellationToken).ConfigureAwait(false))
        {
            toDelete.Add(ce);
        }

        foreach (GitConfigEntry ce in toDelete)
        {
            await DeleteAsync(ce.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary> Byte-key variant of <see cref="DeleteSectionAsync(string, CancellationToken)"/>: the section is the raw name bytes — the entries are matched
    /// by a byte prefix on <see cref="GitConfigEntry.NameBytes"/> (the anchored equivalent of C's <c>^escaped(section)\..+$</c> pattern, config.c:1629-1632)
    /// and deleted under their raw <see cref="GitConfigEntry.NameBytes"/> keys, so non-UTF-8 subsection bytes round-trip byte-exact. Documented divergence: the
    /// string overload matches the unanchored regex <c>section.*</c> (a section name appearing mid-name also matches); the byte overload anchors at the start.
    /// </summary>
    public async Task DeleteSectionAsync(ReadOnlyMemory<byte> section, CancellationToken cancellationToken = default)
    {
        byte[] prefix = [.. section.Span, (byte)'.'];
        var toDelete = new List<GitConfigEntry>();
        await foreach (GitConfigEntry ce in EnumerateAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (ce.NameBytes.Span.StartsWith(prefix))
            {
                toDelete.Add(ce);
            }
        }

        foreach (GitConfigEntry ce in toDelete)
        {
            await DeleteAsync(ce.NameBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renames a config section: moves all entries from <paramref name="oldSection"/>
    /// to <paramref name="newSection"/>, preserving variable names and values.
    /// Matches <c>git_config_rename_section</c>.
    /// </summary>
    public Task RenameSectionAsync(string oldSection, string newSection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldSection);
        ArgumentNullException.ThrowIfNull(newSection);
        return RenameSectionAsync(Encoding.UTF8.GetBytes(oldSection), Encoding.UTF8.GetBytes(newSection), cancellationToken);
    }

    /// <summary> Byte-key variant of <see cref="RenameSectionAsync(string, string, CancellationToken)"/>: the sections are the raw name bytes — C's
    /// <c>git_config_rename_section</c> (config.c:1619-1659) matches the anchored <c>^escaped(old)\..+$</c> pattern over the raw name bytes and rebuilds each
    /// new key by splicing <c>entry->name + old_len</c> (raw bytes), so non-UTF-8 subsection bytes round-trip byte-exact. </summary>
    public async Task RenameSectionAsync(ReadOnlyMemory<byte> oldSection, ReadOnlyMemory<byte> newSection, CancellationToken cancellationToken = default)
    {
        byte[] oldPrefix = [.. oldSection.Span, (byte)'.'];
        byte[] newPrefix = [.. newSection.Span, (byte)'.'];
        var toProcess = new List<GitConfigEntry>();
        await foreach (GitConfigEntry ce in EnumerateAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (ce.NameBytes.Span.StartsWith(oldPrefix))
            {
                toProcess.Add(ce);
            }
        }

        var deletedValues = new Dictionary<ConfigNameKey, List<ReadOnlyMemory<byte>>>();
        foreach (GitConfigEntry ce in toProcess)
        {
            ReadOnlyMemory<byte> varName = ce.NameBytes.Slice(oldPrefix.Length);
            byte[] newKey = [.. newPrefix, .. varName.Span];
            if (ce.ValueBytes is { } valueBytes)
            {
                // Preserve every multivar occurrence at the destination. Deleting a
                // literal value removes all identical source occurrences at once,
                // so do that only once per key/value pair.
                // SetMultiBytesAsync writes raw config syntax; escape parsed values
                // here so copying them preserves their bytes after re-parsing.
                using var escapedValue = new PooledByteBufferWriter();
                FileConfigBackend.EscapeValueBytes(valueBytes.Span, escapedValue);
                await SetMultiBytesAsync(newKey, s_emptyValueRegex, escapedValue.WrittenMemory, cancellationToken).ConfigureAwait(false);
                var key = ConfigNameKey.From(ce.NameBytes);
                if (!deletedValues.TryGetValue(key, out List<ReadOnlyMemory<byte>>? values))
                {
                    values = [];
                    deletedValues.Add(key, values);
                }

                if (!values.Any(value => value.Span.SequenceEqual(valueBytes.Span)))
                {
                    Regex matcher = RegexAdapter.CompileLiteralBytes(valueBytes.Span);
                    await DeleteMultiAsync(ce.NameBytes, matcher, cancellationToken).ConfigureAwait(false);
                    values.Add(valueBytes);
                }
            }
        }
    }

    /// <summary>
    /// Locks the configuration for an atomic read-modify-write transaction.
    /// Matches <c>git_config_lock</c>. The returned <see cref="GitConfigTransaction"/>
    /// must be disposed (committed or rolled back).
    /// </summary>
    public async Task<GitConfigTransaction> LockAsync(CancellationToken cancellationToken = default)
    {
        FileConfigBackend backend = FindWritableBackend()
            ?? throw new GitException(
                GitErrorCode.ReadOnly,
                "cannot lock: the configuration is read-only",
                GitErrorCategory.Config);

        await backend.LockAsync(cancellationToken).ConfigureAwait(false);
        return new GitConfigTransaction(backend);
    }

    /// <summary>
    /// Finds the first writable <see cref="FileConfigBackend"/> (non-snapshot,
    /// non-memory). Returns <c>null</c> if no writable backend is configured.
    /// </summary>
    /// <summary>
    /// The first writable file backend, or a GIT_EREADONLY failure — C
    /// (config.c:683-689, 715-725): a config with no writable backend rejects
    /// writes with "cannot set '%s': the configuration is read-only".
    /// </summary>
    private FileConfigBackend RequireWritableBackend(string name)
        => FindWritableBackend()
            ?? throw new GitException(
                GitErrorCode.ReadOnly,
                $"cannot set '{name}': the configuration is read-only",
                GitErrorCategory.Config);

    /// <summary> Byte-key variant of <see cref="RequireWritableBackend(string)"/>: the error message decodes the key bytes for display only. </summary>
    private FileConfigBackend RequireWritableBackend(ReadOnlyMemory<byte> name)
        => FindWritableBackend()
            ?? throw new GitException(
                GitErrorCode.ReadOnly,
                $"cannot set '{Encoding.UTF8.GetString(name.Span)}': the configuration is read-only",
                GitErrorCategory.Config);

    private FileConfigBackend? FindWritableBackend()
    {
        // C (config.c:663-675, get_writer_instance): the writers vector is
        // sorted DESCENDING by write_order (writer_cmp, config.c:107) and the
        // first non-readonly entry with write_order >= 0 wins. write_order
        // defaults to the LEVEL (config.c:308), so for a default config the
        // HIGHEST-level backend is the writer — the same result as scanning
        // the level-descending readers list — while a repo config that called
        // SetWriteOrder([Local]) has ONLY the LOCAL backend with write_order
        // >= 0 (repository.c:1342-1344).
        int bestOrder = int.MinValue;
        FileConfigBackend? best = null;
        foreach (BackendEntry entry in _readers)
        {
            if (entry.WriteOrder < 0 || entry.Backend is not FileConfigBackend fcb || fcb.ReadOnly)
            {
                continue;
            }

            if (entry.WriteOrder > bestOrder)
            {
                best = fcb;
                bestOrder = entry.WriteOrder;
            }
        }

        return best;
    }

    /// <summary>
    /// Restricts the writable backends to the given levels. Matches
    /// <c>git_config_set_writeorder</c> (config.c:394-420): a backend whose
    /// level appears in <paramref name="levels"/> gets write_order = its
    /// index in the list; every other backend gets -1 and is never chosen by
    /// <see cref="FindWritableBackend"/>. Repo configs call this with only
    /// LOCAL (repository.c:1342-1344), so writes target
    /// <c>commondir/config</c> even when a worktree config backend exists.
    /// </summary>
    public void SetWriteOrder(params GitConfigLevel[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        for (int i = 0; i < _readers.Count; i++)
        {
            BackendEntry entry = _readers[i];
            int order = -1;
            for (int j = 0; j < levels.Length; j++)
            {
                if (levels[j] == entry.Level)
                {
                    order = j;
                    break;
                }
            }

            _readers[i] = entry with { WriteOrder = order };
        }
    }

    /// <summary>
    /// Gets all values for <paramref name="name"/> across all backends
    /// (multivar support). Returns an empty list if not found. Matches
    /// libgit2's <c>git_config_get_multivar_foreach</c>. UTF-8 display
    /// convenience — the byte-parity surface is
    /// <see cref="GetMultiBytesAsync"/>.
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> GetMultiAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        using var writer = new PooledByteBufferWriter();
        NormalizeName(writer, name);
        var results = new List<string>();

        foreach (BackendEntry entry in _readers)
        {
            await foreach (GitConfigEntry ce in entry.Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ce.NameBytes.Span.SequenceEqual(writer.WrittenSpan))
                {
                    results.Add(ce.Value ?? string.Empty);
                }
            }
        }

        // C (config.c, config_get_multivar): a key matching no entry is
        // GIT_ENOTFOUND "could not find key '%s'".
        if (results.Count == 0)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"could not find key '{name}'",
                GitErrorCategory.Config);
        }

        return results;
    }

    /// <summary> Gets all raw value byte arrays for <paramref name="name"/> across all backends (multivar support). Byte-parity surface: non-UTF-8 values
    /// round-trip byte-exact. A lone variable yields an empty array, mirroring the string tier's mapping. Throws <see cref="GitErrorCode.NotFound"/> when the
    /// key matches no entry (same contract as <see cref="GetMultiAsync"/>). </summary>
    public async ValueTask<IReadOnlyList<byte[]>> GetMultiBytesAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        using var writer = new PooledByteBufferWriter();
        NormalizeName(writer, name);
        var results = new List<byte[]>();

        foreach (BackendEntry entry in _readers)
        {
            await foreach (GitConfigEntry ce in entry.Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ce.NameBytes.Span.SequenceEqual(writer.WrittenSpan))
                {
                    results.Add(ce.ValueBytes is { } b ? b.ToArray() : []);
                }
            }
        }

        // C (config.c, config_get_multivar): a key matching no entry is
        // GIT_ENOTFOUND "could not find key '%s'".
        if (results.Count == 0)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"could not find key '{name}'",
                GitErrorCategory.Config);
        }

        return results;
    }

    /// <summary>
    /// Maps the value for <paramref name="name"/> through a
    /// <see cref="GitConfigurationMap{T}"/> and returns the mapped result.
    /// Returns <paramref name="defaultValue"/> if the key is not found.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the value is found but does not match any map item.
    /// </exception>
    public async ValueTask<T> GetMappedAsync<T>(string name, GitConfigurationMap<T> map, T defaultValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        GitConfigEntry? entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return defaultValue;
        }

        return entry.Value.ValueBytes is { } b ? map.Lookup(b.Span) : map.Lookup((string?)null);
    }

    /// <summary> Iterates all entries across all backends, in descending level order. If <paramref name="pattern"/> is provided, only entries whose name
    /// matches the pattern are yielded. Matches libgit2's <c>git_config_foreach_match</c> (config.c:626-650): the pattern is an UNANCHORED regular expression
    /// compiled with <c>git_regexp_compile</c>, not a glob. </summary> <param name="pattern">Optional regex pattern (e.g. <c>core\..*</c>).</param> <remarks>
    /// the pattern is matched over the entry's raw name bytes (<see cref="GitConfigEntry.NameBytes"/>) via the byte-domain regex adapter — C's
    /// <c>all_iter_glob_next</c> (config.c:493-530) runs <c>git_regexp_match</c> over the raw name bytes. The pattern string is UTF-8-encoded into the byte
    /// domain, so ASCII patterns behave identically to a string match. </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async IAsyncEnumerable<GitConfigEntry> EnumerateAsync(string? pattern = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using RegexAdapter? regex = pattern is null ? null : RegexAdapter.Compile(pattern);
        foreach (BackendEntry entry in _readers)
        {
            await foreach (GitConfigEntry ce in entry.Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (regex is null || regex.IsMatch(ce.NameBytes.Span))
                {
                    yield return ce;
                }
            }
        }
    }

    /// <summary>
    /// Finds the global (<c>~/.gitconfig</c>) config file path, or
    /// <c>null</c> if it doesn't exist. Matches libgit2's
    /// <c>git_config_find_global</c>.
    /// </summary>
    private string? FindGlobal() => _context.Dirs.FindGlobalFile(".gitconfig");

    /// <summary>
    /// Resolves the global config location without checking existence.
    /// Matches <c>git_config__global_location</c> (config.c:1273-1297): the
    /// first entry of the GLOBAL sysdir dirlist joined with
    /// <c>.gitconfig</c>, or <c>null</c> when the dirlist is empty (HOME
    /// unresolvable).
    /// </summary>
    private string? GlobalLocation()
    {
        string dirlist = _context.Dirs.Get(GitSystemDir.Global);
        if (string.IsNullOrEmpty(dirlist))
        {
            return null;
        }

        // C (config.c, git_config__global_location): the first entry of the
        // GLOBAL sysdir dirlist, up to the first UNESCAPED path-list
        // separator (a backslash-escaped separator stays inside the entry),
        // joined with .gitconfig. The separator is ';' on Windows, so a
        // drive-letter path like C:\Users\... is not split at the colon.
        char sep = OperatingSystem.IsWindows() ? ';' : ':';
        int i = 0;
        while (i < dirlist.Length &&
               !(dirlist[i] == sep && (i == 0 || dirlist[i - 1] != '\\')))
        {
            i++;
        }

        string dir = dirlist[..i];
        return PathHelpers.Join(dir, ".gitconfig");
    }

    /// <summary>
    /// Finds the XDG (<c>$XDG_CONFIG_HOME/git/config</c>) config file path, or
    /// <c>null</c> if it doesn't exist. Matches libgit2's
    /// <c>git_config_find_xdg</c>.
    /// </summary>
    private string? FindXdg() => _context.Dirs.FindXdgFile("config");

    /// <summary>
    /// Finds the system-wide (<c>/etc/gitconfig</c>) config file path, or
    /// <c>null</c> if it doesn't exist. Matches libgit2's
    /// <c>git_config_find_system</c>.
    /// </summary>
    private string? FindSystem() => _context.Dirs.FindSystemFile("gitconfig");

    /// <summary>
    /// The GLOBAL config path. With <paramref name="useEnv"/>,
    /// <c>GIT_CONFIG_GLOBAL</c> replaces the guessed path verbatim — even a
    /// nonexistent file becomes an (empty) backend (config_path_global,
    /// repository.c:1392-1397).
    /// </summary>
    private string? GlobalPath(bool useEnv)
    {
        if (useEnv)
        {
            string? env = _context.Env["GIT_CONFIG_GLOBAL"];
            if (env is not null)
            {
                return env;
            }
        }

        return FindGlobal();
    }

    /// <summary>
    /// The SYSTEM config path. With <paramref name="useEnv"/>:
    /// <c>GIT_CONFIG_NOSYSTEM</c> (bool) omits the system level entirely;
    /// <c>GIT_CONFIG_SYSTEM</c> replaces the guessed path verbatim
    /// (config_path_system, repository.c:1362-1388).
    /// </summary>
    private string? SystemPath(bool useEnv)
    {
        if (useEnv)
        {
            string? noSystem = _context.Env["GIT_CONFIG_NOSYSTEM"];
            if (noSystem is not null)
            {
                if (ConfigurationValueParser.TryParseBool(noSystem, out bool parsed) && parsed)
                {
                    return null;
                }
            }

            string? env = _context.Env["GIT_CONFIG_SYSTEM"];
            if (env is not null)
            {
                return env;
            }
        }

        return FindSystem();
    }

    /// <summary>
    /// Finds the ProgramData config file path (Windows only), or
    /// <c>null</c> if it doesn't exist. Matches libgit2's
    /// <c>git_config_find_programdata</c>.
    /// </summary>
    private string? FindProgramData() => _context.Dirs.FindProgramDataFile("config");

    /// <summary> Normalizes a config key: lowercases the section (before first dot) and variable name (after last dot); preserves subsection case. Matches
    /// libgit2's <c>git_config__normalize_name</c>. UTF-8 convenience tier — the byte-parity surface is <see cref="ConfigKeyName.NormalizeNameBytes"/>, which
    /// normalizes the raw name bytes exactly as C does (non-UTF-8 subsection bytes pass through verbatim). </summary> <exception cref="GitException"> <see
    /// cref="GitErrorCode.InvalidSpec"/> if the name is missing a dot, has empty components, contains invalid characters, or has newlines in the subsection.
    /// </exception>
    internal static void NormalizeName(IBufferWriter<byte> writer, ReadOnlySpan<char> name)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(Encoding.UTF8.GetByteCount(name), 1));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(name, buffer);

            ConfigKeyName.NormalizeNameBytes(writer, buffer.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private BackendEntry? FindBackend(GitConfigLevel level)
    {
        foreach (BackendEntry entry in _readers)
        {
            if (entry.Level == level)
            {
                return entry;
            }
        }

        return null;
    }

    private bool HasBackend(GitConfigLevel level) => FindBackend(level) is not null;

    /// <summary>
    /// Removes the backend registered at <paramref name="level"/>, disposing
    /// it. Synchronous: the backend dispose is a no-op for all current
    /// backends (GC-reclaimed state).
    /// </summary>
    private void RemoveBackend(GitConfigLevel level)
    {
        for (int i = 0; i < _readers.Count; i++)
        {
            if (_readers[i].Level == level)
            {
                _readers[i].Backend.Dispose();
                _readers.RemoveAt(i);
                return;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (BackendEntry entry in _readers)
        {
            await entry.Backend.DisposeAsync().ConfigureAwait(false);
        }

        _readers.Clear();
    }

    private readonly record struct BackendEntry(IConfigBackend Backend, GitConfigLevel Level)
    {
        /// <summary>
        /// C (config.c:308): write_order defaults to the LEVEL; negative
        /// means the backend is never chosen as the writer
        /// (git_config_set_writeorder, config.c:394-420).
        /// </summary>
        public int WriteOrder { get; init; } = (int)Level;
    }
}

// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Config;

/// <summary>
/// Read-only config backend backed by an in-memory buffer. Managed port of
/// libgit2's <c>config_mem.c</c>.
/// </summary>
/// <remarks>
/// Supports two construction modes matching libgit2's
/// <c>git_config_backend_from_string</c> (parse a config-format string) and
/// <c>git_config_backend_from_values</c> (array of <c>"key=value"</c> strings).
/// Always read-only. No includes, no file monitoring.
/// </remarks>
internal sealed class MemoryConfigBackend : IConfigBackend
{
    private readonly string _backendType = GitConfigEntry.MemoryBackendType;
    private readonly string? _originPath;
    private readonly ReadOnlyMemory<byte> _configText;
    private readonly string[]? _values;
    private ConfigList? _configList;

    /// <summary>
    /// The parsed config list, throwing if <see cref="OpenAsync"/> has not
    /// populated it.
    /// </summary>
    private ConfigList OpenedList => _configList ?? throw new InvalidOperationException("memory config backend has not been opened");
    private bool _opened;

    /// <summary> Creates a backend that parses <paramref name="configBytes"/> (raw git config file bytes) on <see cref="OpenAsync"/>. Byte-primary surface: the
    /// buffer is parsed as-is — non-UTF-8 config bytes flow through byte-faithfully, matching libgit2's byte-domain config parser. </summary> <param
    /// name="configBytes">The raw config file bytes.</param> <param name="originPath">Optional origin path for diagnostics (does not need to exist).</param>
    /// <param name="backendType">Optional override for the backend type label.</param>
    public MemoryConfigBackend(ReadOnlyMemory<byte> configBytes, string? originPath = null, string? backendType = null)
    {
        _configText = configBytes;
        _originPath = originPath;
        if (backendType is not null)
        {
            _backendType = backendType;
        }
    }

    /// <summary> Creates a backend that parses <paramref name="configText"/> as a git config file on <see cref="OpenAsync"/>. Matches libgit2's
    /// <c>git_config_backend_from_string</c>. UTF-8 convenience tier: the string is UTF-8-encoded into the byte parse domain; use the <see
    /// cref="MemoryConfigBackend(ReadOnlyMemory{byte}, string?, string?)"/> constructor for arbitrary (non-UTF-8) config bytes. </summary> <param
    /// name="configText">The config file text (UTF-8).</param> <param name="originPath">Optional origin path for diagnostics (does not need to exist).</param>
    /// <param name="backendType">Optional override for the backend type label.</param>
    public MemoryConfigBackend(string configText, string? originPath = null, string? backendType = null)
    {
        _configText = Encoding.UTF8.GetBytes(configText);
        _originPath = originPath;
        if (backendType is not null)
        {
            _backendType = backendType;
        }
    }

    /// <summary>
    /// Creates a backend that interprets <paramref name="values"/> as an array
    /// of <c>"key=value"</c> pairs on <see cref="OpenAsync"/>. Matches libgit2's
    /// <c>git_config_backend_from_values</c>.
    /// </summary>
    /// <param name="values">Array of <c>"key=value"</c> or lone <c>"key"</c> strings.</param>
    /// <param name="originPath">Optional origin path for diagnostics.</param>
    /// <param name="backendType">Optional override for the backend type label.</param>
    public MemoryConfigBackend(string[] values, string? originPath = null, string? backendType = null)
    {
        _values = values;
        _originPath = originPath;
        if (backendType is not null)
        {
            _backendType = backendType;
        }
    }

    /// <inheritdoc/>
    public bool ReadOnly => true;

    /// <inheritdoc/>
    public async Task OpenAsync(GitConfigLevel level, CancellationToken cancellationToken = default)
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        _configList = ConfigList.New();

        if (!_configText.IsEmpty)
        {
            await ParseTextAsync(level, cancellationToken).ConfigureAwait(false);
        }

        if (_values is not null)
        {
            ParseValues(level);
        }
    }

    /// <inheritdoc/>
    public ValueTask<GitConfigEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        => GetAsync(Encoding.UTF8.GetBytes(key), cancellationToken);

    /// <inheritdoc/>
    public ValueTask<GitConfigEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        return ValueTask.FromResult(OpenedList.Get(ConfigNameKey.From(key)));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<GitConfigEntry> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        return OpenedList.Enumerate().ToAsyncEnumerableAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public IConfigBackend Snapshot() => new SnapshotConfigBackend(this);

    // ── Write side — read-only backend, all throw ───────────────────────

    /// <inheritdoc/>
    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task SetBytesAsync(string key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task SetBytesAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task SetMultiAsync(string key, Regex regexp, string value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task SetMultiBytesAsync(string key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task SetMultiBytesAsync(ReadOnlyMemory<byte> key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteKeyAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteKeyAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteMultiAsync(string key, Regex regexp, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteMultiAsync(ReadOnlyMemory<byte> key, Regex regexp, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task LockAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public Task UnlockAsync(bool commit, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("in-memory config backend is read-only");

    /// <inheritdoc/>
    public void Dispose()
    {
        // ConfigList is GC-reclaimed; no explicit cleanup needed.
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureOpened()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Memory config backend has not been opened.");
        }
    }

    private async Task ParseTextAsync(GitConfigLevel level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ConfigParser.ParseAsync(
            path: _originPath ?? "in-memory",
            content: _configText,
            onSection: null,
            onVariable: (section, varName, varValue, line, lineNum) =>
            {
                ReadOnlyMemory<byte> nameBytes = ConfigKeyName.BuildFullyQualifiedNameBytes(section, varName);
                ReadOnlyMemory<byte>? varValueBytes = varValue is { } varValueMem ? varValueMem.ToArray() : (ReadOnlyMemory<byte>?)null;
                var entry = new GitConfigEntry(
                    NameBytes: nameBytes,
                    ValueBytes: varValueBytes,
                    BackendType: _backendType,
                    Path: _originPath,
                    IncludeDepth: 0,
                    Level: level);
                OpenedList.Append(entry);
                return Task.CompletedTask;
            },
            onComment: null,
            onEof: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void ParseValues(GitConfigLevel level)
    {
        string[]? values = _values;
        Debug.Assert(values is not null, "_values is set by the values constructor.");
        foreach (string item in values)
        {
            int eq = item.IndexOf('=', StringComparison.Ordinal);
            string name;
            string? value;
            if (eq < 0)
            {
                // Lone key (no '=') — treated as a value-less entry.
                name = item;
                value = null;
            }
            else
            {
                name = item[..eq];
                value = item[(eq + 1)..];
            }

            if (name.Length == 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "empty config key",
                    GitErrorCategory.Config);
            }

            var entry = new GitConfigEntry(
                NameBytes: Encoding.UTF8.GetBytes(name),
                ValueBytes: value is null ? (ReadOnlyMemory<byte>?)null : Encoding.UTF8.GetBytes(value),
                BackendType: _backendType,
                Path: _originPath,
                IncludeDepth: 0,
                Level: level);
            OpenedList.Append(entry);
        }
    }
}

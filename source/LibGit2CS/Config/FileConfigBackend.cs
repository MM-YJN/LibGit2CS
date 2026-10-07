// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.Config;

/// <summary>
/// Config backend that reads and parses an on-disk git config file. Managed
/// port of libgit2's <c>config_file.c</c> (read side).
/// </summary>
/// <remarks>
/// <para>
/// Supports <c>[include]</c> and <c>[includeIf "gitdir:..."|gitdir/i:...|onbranch:...]</c>
/// directives with cycle detection (<c>MAX_INCLUDE_DEPTH=10</c>). Detects
/// on-disk modifications via file stamp (mtime + size) plus SHA-256 content
/// checksum and transparently re-parses on access.
/// </para>
/// <para>
/// <see cref="ReadOnly"/> is <c>false</c> (the file backend is writable in
/// libgit2). The flag matters to the facade only to decide whether
/// <c>Snapshot</c> is needed for safety.
/// </para>
/// <para>
/// The optional <c>repoGitDirPath</c> constructor argument supplies the
/// repository gitdir for <c>gitdir:</c>/<c>onbranch:</c> conditional matching.
/// When <c>null</c>, all conditional includes are silently skipped (matching
/// libgit2's behavior when <c>parse_data-&gt;repo</c> is NULL).
/// </para>
/// </remarks>
internal sealed class FileConfigBackend : IConfigBackend
{
    private const int MaxIncludeDepth = 10;

    /// <summary>
    /// Reads a config file as raw bytes decoded Latin-1 (byte-preserving) —
    /// C operates on raw bytes (config.c reads via git_futils_readbuffer), so
    /// invalid-UTF-8 content must round-trip unchanged instead of becoming
    /// U+FFFD. A leading UTF-8 BOM is stripped (the parser treats U+FEFF as a
    /// BOM).
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> ReadConfigTextAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return SkipUtf8Bom(bytes);
    }

    private static ReadOnlyMemory<byte> SkipUtf8Bom(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes.Span[0] == 0xEF && bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return bytes;
    }

    /// <summary>Writes config content as raw Latin-1 bytes (byte-preserving).</summary>
    private static async Task WriteConfigTextAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await AsyncFileIO.WriteAtomicAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private readonly string _path;
    private readonly string? _repoGitDirPath;
    private readonly GitSystemDirs _dirs;
    private readonly ConfigFile _file;
    private GitConfigLevel _level;
    private ConfigList? _configList;

    /// <summary>
    /// The parsed config list, throwing if <see cref="OpenAsync"/> has not been
    /// called. All read/write methods require an open backend.
    /// </summary>
    private ConfigList OpenedList => _configList ?? throw new InvalidOperationException("config backend has not been opened");
    private bool _opened;

    /// <summary>
    /// Creates a backend that reads from <paramref name="path"/> on
    /// <see cref="OpenAsync"/>. The file does not need to exist yet.
    /// </summary>
    /// <param name="path">Path to the on-disk config file.</param>
    /// <param name="repoGitDirPath">The repository's gitdir path for conditional include matching, or <c>null</c> to disable conditionals.</param>
    /// <param name="dirs">The directory resolver used for <c>~</c>-expansion in <c>[include]</c>/<c>[includeIf]</c> paths.</param>
    public FileConfigBackend(string path, string? repoGitDirPath, GitSystemDirs dirs)
    {
        _path = path;
        _repoGitDirPath = repoGitDirPath;
        _dirs = dirs;
        _file = new ConfigFile { _path = path };
    }

    /// <inheritdoc/>
    public bool ReadOnly => false;

    /// <summary>
    /// The on-disk path this backend reads from.
    /// </summary>
    public string Path => _path;

    /// <inheritdoc/>
    public async Task OpenAsync(GitConfigLevel level, CancellationToken cancellationToken = default)
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        _level = level;
        _configList = ConfigList.New();

        if (!PathHelpers.Exists(_path))
        {
            return;
        }

        // git silently ignores unreadable config files (e.g. sandboxed apps).
        if (!IsReadable(_path))
        {
            return;
        }

        await ConfigFileReadAsync(_configList, _file, level, depth: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<GitConfigEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            return await GetAsync(buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<GitConfigEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        if (!ReadOnly)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return OpenedList.Get(ConfigNameKey.From(key));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<GitConfigEntry> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        return EnumerateImplAsync(cancellationToken);
    }

    private async IAsyncEnumerable<GitConfigEntry> EnumerateImplAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!ReadOnly)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (GitConfigEntry e in OpenedList.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    /// <inheritdoc/>
    public IConfigBackend Snapshot() => new SnapshotConfigBackend(this);

    // ── Write side ──────────────────────────────────────────────────────

    private ReadOnlyMemory<byte> _lockedContent;
    private string? _lockTempPath;
    private bool _locked;

    /// <inheritdoc/>
    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        // UTF-8 convenience tier: the value is UTF-8-encoded into the byte write path — <c>é</c> writes <c>C3 A9</c>.
        // <c>valueBytes</c> is initialized to <c>null</c> and only assigned for a non-null value — a <c>null</c> value must stay null (a
        // deletion), not collapse to an empty memory that would turn the deletion into an empty-value write.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key) + (value is not null ? Encoding.UTF8.GetByteCount(value) : 0));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            ReadOnlyMemory<byte> keyBytes = buffer.AsMemory(0, bytesWritten);
            ReadOnlyMemory<byte>? valueBytes = null;
            if (value is not null)
            {
                int valueBytesWritten = Encoding.UTF8.GetBytes(value, buffer.AsSpan(bytesWritten));
                valueBytes = buffer.AsMemory(bytesWritten, valueBytesWritten);
            }

            await SetBytesCoreAsync(keyBytes, valueBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async Task SetBytesAsync(string key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            await SetBytesCoreAsync(buffer.AsMemory(0, bytesWritten), value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async Task SetBytesAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
        => await SetBytesCoreAsync(key, value, cancellationToken).ConfigureAwait(false);

    private async Task SetBytesCoreAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken)
    {
        EnsureOpened();

        using var normalizedKeyWriter = new PooledByteBufferWriter(key.Span.Length);
        ConfigKeyName.NormalizeNameBytes(normalizedKeyWriter, key.Span);
        ReadOnlyMemory<byte> normalizedKey = normalizedKeyWriter.WrittenMemory;

        // config_file_set (config_file.c:294-334): get_unique fails with
        // "entry is not unique due to being a multivar/included" before any
        // write; a missing key is fine (new entry).
        GitConfigEntry? existing = GetUniqueOrNull(normalizedKey);
        if (existing is { } e)
        {
            if (BytesEqual(e.ValueBytes, value))
            {
                return;
            }
        }

        if (_locked)
        {
            using var escapedValueWriter = new PooledByteBufferWriter();
            if (value is { } v)
            {
                EscapeValueBytes(v.Span, escapedValueWriter);
            }

            // During a lock, edit the frozen in-memory content (not the file).
            // C escapes the value before writing (config_file_set).
            _lockedContent = await EditInPlaceAsync(
                _lockedContent,
                key,
                normalizedKey,
                regexp: null,
                value is null ? (ReadOnlyMemory<byte>?)null : escapedValueWriter.WrittenMemory,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Refresh to pick up any external changes before editing.
        if (!ReadOnly)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        // Re-check after the refresh (the file may have changed).
        existing = GetUniqueOrNull(normalizedKey);
        if (existing is { } e2)
        {
            if (BytesEqual(e2.ValueBytes, value))
            {
                return;
            }
        }

        {
            using var escapedValueWriter = new PooledByteBufferWriter();
            if (value is { } v2)
            {
                EscapeValueBytes(v2.Span, escapedValueWriter);
            }

            await WriteConfigInPlaceAsync(key, normalizedKey, regexp: null, value is null ? (ReadOnlyMemory<byte>?)null : escapedValueWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }

        await ReloadAndRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool BytesEqual(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b)
    {
        if (a is null)
        {
            return b is null;
        }

        if (b is null)
        {
            return false;
        }

        return a.GetValueOrDefault().Span.SequenceEqual(b.GetValueOrDefault().Span);
    }

    /// <summary>
    /// <see cref="ConfigList.GetUnique"/> wrapper: returns <c>null</c> for a
    /// missing key and propagates the multivar/included errors, matching
    /// <c>git_config_list_get_unique</c> (config_list.c:204-223).
    /// </summary>
    private GitConfigEntry? GetUniqueOrNull(ReadOnlyMemory<byte> normalizedKey)
    {
        EnsureOpened();
        return OpenedList.GetUnique(ConfigNameKey.From(normalizedKey));
    }

    /// <inheritdoc/>
    public async Task SetMultiAsync(string key, Regex regexp, string value, CancellationToken cancellationToken = default)
    {
        // UTF-8 convenience tier: the multivar value is UTF-8-encoded into the byte write path.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value));
        try
        {
            int keyBytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            int valueBytesWritten = Encoding.UTF8.GetBytes(value, buffer.AsSpan(keyBytesWritten));
            await SetMultiBytesCoreAsync(buffer.AsMemory(0, keyBytesWritten), regexp, buffer.AsMemory(keyBytesWritten, valueBytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async Task SetMultiBytesAsync(string key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            await SetMultiBytesCoreAsync(buffer.AsMemory(0, bytesWritten), regexp, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async Task SetMultiBytesAsync(ReadOnlyMemory<byte> key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => await SetMultiBytesCoreAsync(key, regexp, value, cancellationToken).ConfigureAwait(false);

    private async Task SetMultiBytesCoreAsync(ReadOnlyMemory<byte> key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        EnsureOpened();

        using var normalizedKeyWriter = new PooledByteBufferWriter(key.Span.Length);
        ConfigKeyName.NormalizeNameBytes(normalizedKeyWriter, key.Span);
        ReadOnlyMemory<byte> normalizedKey = normalizedKeyWriter.WrittenMemory;

        if (_locked)
        {
            _lockedContent = await EditInPlaceAsync(
                _lockedContent,
                key,
                normalizedKey,
                regexp,
                value,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ReadOnly)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteConfigInPlaceAsync(key, normalizedKey, regexp, value, cancellationToken).ConfigureAwait(false);

        await ReloadAndRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a config key from the in-memory list and writes the file to disk.
    /// Matches <c>config_file_delete</c> (config_file.c:392-420): a missing key
    /// errors with GIT_ENOTFOUND "could not find key '%s' to delete", and a
    /// multivar/included key errors before any write.
    /// </summary>
    public async Task DeleteKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            await DeleteKeyCoreAsync(buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteKeyAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        => await DeleteKeyCoreAsync(key, cancellationToken).ConfigureAwait(false);

    private async Task DeleteKeyCoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        EnsureOpened();

        using var normalizedKeyWriter = new PooledByteBufferWriter(key.Span.Length);
        ConfigKeyName.NormalizeNameBytes(normalizedKeyWriter, key.Span);
        ReadOnlyMemory<byte> normalizedKey = normalizedKeyWriter.WrittenMemory;

        if (_locked)
        {
            if (GetUniqueOrNull(normalizedKey) is null)
            {
                throw NotFoundForKey(key);
            }

            _lockedContent = await EditInPlaceAsync(
                _lockedContent,
                key,
                normalizedKey,
                regexp: null,
                value: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ReadOnly)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (GetUniqueOrNull(normalizedKey) is null)
        {
            throw NotFoundForKey(key);
        }

        await WriteConfigInPlaceAsync(key, normalizedKey, regexp: null, value: null, cancellationToken).ConfigureAwait(false);
        await ReloadAndRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The C error for a missing key (config_file.c:407-411, 437-441).
    /// </summary>
    private static GitException NotFoundForKey(ReadOnlyMemory<byte> key)
        => new(GitErrorCode.NotFound, $"could not find key '{Encoding.UTF8.GetString(key.Span)}' to delete", GitErrorCategory.Config);

    /// <inheritdoc/>
    public async Task DeleteMultiAsync(string key, Regex regexp, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            await DeleteMultiCoreAsync(buffer.AsMemory(0, bytesWritten), regexp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteMultiAsync(ReadOnlyMemory<byte> key, Regex regexp, CancellationToken cancellationToken = default)
        => await DeleteMultiCoreAsync(key, regexp, cancellationToken).ConfigureAwait(false);

    private async Task DeleteMultiCoreAsync(ReadOnlyMemory<byte> key, Regex regexp, CancellationToken cancellationToken)
    {
        EnsureOpened();

        using var normalizedKeyWriter = new PooledByteBufferWriter(key.Span.Length);
        ConfigKeyName.NormalizeNameBytes(normalizedKeyWriter, key.Span);
        ReadOnlyMemory<byte> normalizedKey = normalizedKeyWriter.WrittenMemory;
        var configNameKey = ConfigNameKey.From(normalizedKey);

        if (_locked)
        {
            if (OpenedList.Get(configNameKey) is null)
            {
                throw NotFoundForKey(key);
            }

            _lockedContent = await EditInPlaceAsync(
                _lockedContent,
                key,
                normalizedKey,
                regexp,
                value: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ReadOnly)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        // config_file_delete_multivar (config_file.c:422-454): the key must
        // exist (last-wins lookup; multivars allowed), else GIT_ENOTFOUND.
        if (OpenedList.Get(configNameKey) is null)
        {
            throw NotFoundForKey(key);
        }

        await WriteConfigInPlaceAsync(key, normalizedKey, regexp, value: null, cancellationToken).ConfigureAwait(false);
        await ReloadAndRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Locks this backend for an atomic read-modify-write transaction. Creates
    /// a <c>&lt;path&gt;.lock</c> temp file and freezes the current content.
    /// Matches <c>config_file_lock</c> (config_file.c:456-473).
    /// </summary>
    public async Task LockAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpened();

        if (_locked)
        {
            // C (filebuf.c:44-51): a pre-existing .lock is GIT_ELOCKED, not
            // GIT_EEXISTS.
            throw new GitException(
                GitErrorCode.Locked,
                $"failed to lock file '{_path}.lock' for writing",
                GitErrorCategory.Os);
        }

        // Read the current file content (frozen snapshot).
        if (File.Exists(_path))
        {
            _lockedContent = await ReadConfigTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _lockedContent = ReadOnlyMemory<byte>.Empty;
        }

        // Create the .lock temp file (O_EXCL probe — sync metadata op).
        _lockTempPath = _path + ".lock";
        try
        {
            using var fs = new FileStream(
                _lockTempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException)
        {
            // C (filebuf.c:44-51): a pre-existing .lock is GIT_ELOCKED, not
            // GIT_EEXISTS.
            throw new GitException(
                GitErrorCode.Locked,
                $"failed to lock file '{_lockTempPath}' for writing",
                GitErrorCategory.Os);
        }

        _locked = true;
    }

    /// <summary>
    /// Unlocks the backend, either committing pending changes or discarding.
    /// Matches <c>config_file_unlock</c> (config_file.c:475-491).
    /// </summary>
    /// <param name="commit">If true, write the frozen content to the <c>.lock</c> file and commit (rename). If false, discard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UnlockAsync(bool commit, CancellationToken cancellationToken = default)
    {
        EnsureOpened();

        if (!_locked)
        {
            return;
        }

        try
        {
            if (commit && _lockTempPath is not null)
            {
                // Write the frozen content (possibly modified in-memory) and commit.
                await File.WriteAllBytesAsync(_lockTempPath, _lockedContent, cancellationToken).ConfigureAwait(false);
                // C (config_file.c:475-490): git_filebuf_commit renames the
                // lock over the target atomically — the original stays intact
                // on failure.
                File.Move(_lockTempPath, _path, overwrite: true);
                _file._stampSize = -1; // force refresh
                await ReloadAndRefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            DiscardLock();
        }
    }

    /// <summary>
    /// Discards the lock state without committing: deletes the <c>.lock</c>
    /// temp file (if any) and resets the lock fields. Synchronous — file
    /// deletion is a metadata op. Matches the rollback path
    /// of C's <c>config_file_unlock</c> (config_file.c:475-491). Shared by
    /// <see cref="UnlockAsync"/> (rollback branch) and
    /// <see cref="GitConfigTransaction.Rollback"/>.
    /// </summary>
    internal void DiscardLock()
    {
        if (!_locked)
        {
            return;
        }

        if (_lockTempPath is not null && File.Exists(_lockTempPath))
        {
            try
            {
                File.Delete(_lockTempPath);
            }
            catch (IOException)
            {
            }
        }

        _locked = false;
        _lockTempPath = null;
        _lockedContent = ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Performs the formatting-preserving in-place edit of the config file,
    /// driven by the config parser exactly like C's <c>config_file_write</c>
    /// (config_file.c:1027-1180): sections, variables and comments are
    /// re-emitted verbatim (comments buffered), and the target variable is
    /// replaced (single set) or regexp-matched (multivar).
    /// </summary>
    /// <param name="origKey">The key as passed by the caller (original case — used for the written section/variable names).</param>
    /// <param name="key">The normalized key.</param>
    /// <param name="regexp">Regex for multivar (null for single-value set/delete).</param>
    /// <param name="value">New value (null = delete the key). For a single set this must already be escaped (C: <c>escape_value</c> in <c>config_file_set</c>); multivar values are written raw.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task WriteConfigInPlaceAsync(ReadOnlyMemory<byte> origKey, ReadOnlyMemory<byte> key, Regex? regexp, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken)
    {
        // Read the current file content (empty if file doesn't exist).
        ReadOnlyMemory<byte> content = File.Exists(_path)
            ? await ReadConfigTextAsync(_path, cancellationToken).ConfigureAwait(false)
            : ReadOnlyMemory<byte>.Empty;
        using var buf = new PooledByteBufferWriter(content.Length + 64);
        await EditAsync(buf, content, origKey, key, regexp, value, cancellationToken).ConfigureAwait(false);
        await WriteConfigTextAsync(_path, buf.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Edits the config content in-place (formatting-preserving) and returns
    /// the edited string. Used by both <see cref="WriteConfigInPlaceAsync"/> (disk)
    /// and the locked-content path. Exact port of <c>config_file_write</c>:
    /// the parser drives the rewrite with buffered comments, original-case
    /// section/variable names, and <c>write_line_to</c> newline semantics.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> EditInPlaceAsync(ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> origKey, ReadOnlyMemory<byte> key, Regex? regexp, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken)
    {
        using var buf = new PooledByteBufferWriter(content.Length + 64);
        await EditAsync(buf, content, origKey, key, regexp, value, cancellationToken).ConfigureAwait(false);
        return buf.WrittenSpan.ToArray();
    }

    private async Task EditAsync(PooledByteBufferWriter buf, ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> origKey, ReadOnlyMemory<byte> key, Regex? regexp, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken)
    {
        // config_file_write (config_file.c:1145-1171): name/section come from
        // the normalized key, orig_name/orig_section from the caller's key.
        int lastDot = key.Span.LastIndexOf((byte)'.');
        ReadOnlyMemory<byte> name = key.Slice(lastDot + 1);
        ReadOnlyMemory<byte> section = key.Slice(0, lastDot);
        int origLastDot = origKey.Span.LastIndexOf((byte)'.');
        ReadOnlyMemory<byte> origName = origKey.Slice(origLastDot + 1);
        ReadOnlyMemory<byte> origSection = origKey.Slice(0, origLastDot);

        using var bufferedComment = new PooledByteBufferWriter();
        bool inSection = false;
        bool pregReplaced = false;
        ReadOnlyMemory<byte>? pendingValue = value;

        // write_line_to (config_file.c:968-976): emit the raw line; a line
        // lacking a trailing newline (final line at EOF) gets one appended.
        void WriteLine(ReadOnlyMemory<byte> line)
        {
            buf.Write(line.Span);
            if (line.IsEmpty || line.Span[^1] != (byte)'\n')
            {
                buf.Write((byte)'\n');
            }
        }

        // write_value (config_file.c:983-1000): "\t%s = %s%s%s\n" with the
        // ORIGINAL variable name; after one write for a single set, value
        // becomes NULL so it is never written again.
        void WriteValue()
        {
            ReadOnlySpan<byte> pendingValueSpan = pendingValue.GetValueOrDefault().Span;
            ReadOnlySpan<byte> q = QuotesForValue(pendingValueSpan);
            Debug.Assert(q.Length <= 1);

            // Variable names are ASCII-validated (NormalizeName); the origName bytes are copied verbatim. 7 = 1 ('\t') + 3 (" = ") + 2 (up to two quote bytes,
            // q.Length <= 1 asserted above) + 1 ('\n')
            Span<byte> span = buf.GetSpan(origName.Length + pendingValueSpan.Length + 7);
            span[0] = (byte)'\t';
            origName.Span.CopyTo(span.Slice(1));
            int bytesWritten = 1 + origName.Length;

            " = "u8.CopyTo(span.Slice(bytesWritten));
            bytesWritten += 3;

            q.CopyTo(span.Slice(bytesWritten));
            bytesWritten += q.Length;

            pendingValueSpan.CopyTo(span.Slice(bytesWritten));
            bytesWritten += pendingValueSpan.Length;

            q.CopyTo(span.Slice(bytesWritten));
            bytesWritten += q.Length;

            span[bytesWritten++] = (byte)'\n';

            buf.Advance(bytesWritten);

            if (regexp is null)
            {
                pendingValue = null;
            }
        }

        // write_section (config_file.c:907-935): "[section "subsection"]\n"
        // with the ORIGINAL section name and an escaped subsection.
        void WriteSection(ReadOnlyMemory<byte> sectionKey)
        {
            int dot = sectionKey.Span.IndexOf((byte)'.');
            buf.Write((byte)'[');
            if (dot < 0)
            {
                // Section names are ASCII-validated (NormalizeName).
                buf.Write(sectionKey.Span);
                buf.Write("]\n"u8);
            }
            else
            {
                buf.Write(sectionKey.Span[..dot]);
                buf.Write(" \""u8);

                EscapeValueBytes(sectionKey.Span[(dot + 1)..], buf);

                buf.Write("\"]\n"u8);
            }
        }

        void FlushComment()
        {
            buf.Write(bufferedComment.WrittenSpan);
            bufferedComment.ResetWrittenCount();
        }

        await ConfigParser.ParseAsync(
            path: _path,
            content: content,
            onSection: (currentSection, line, lineNum) =>
            {
                // write_on_section (config_file.c:1003-1036): if we were in the
                // matching section and haven't written the value yet (single set),
                // append it before leaving; then flush buffered comments and emit
                // the section line.
                if (inSection && regexp is null && pendingValue is not null)
                {
                    WriteValue();
                }

                inSection = currentSection is { } currentSectionValue && AsciiEquals(currentSectionValue.Span, section.Span);
                FlushComment();
                WriteLine(line);
                return Task.CompletedTask;
            },
            onVariable: (currentSection, varName, varValue, line, lineNum) =>
            {
                // write_on_variable (config_file.c:1038-1094).
                FlushComment();

                bool hasMatched = inSection && AsciiEqualsIgnoreCase(varName.Span, name.Span);
                if (hasMatched && regexp is not null)
                {
                    // git_regexp_match against the PARSED value; a lone variable (varValue null) never matches (C crashes there — UB). The user-compiled Regex
                    // matches the value bytes via the Latin1 bijection.
                    hasMatched = varValue is { } varValueMem && RegexAdapter.IsMatchBijection(regexp, varValueMem.Span);
                }

                if (!hasMatched)
                {
                    WriteLine(line);
                    return Task.CompletedTask;
                }

                pregReplaced = true;

                // value NULL → delete: write nothing.
                if (pendingValue is not null)
                {
                    WriteValue();
                }

                return Task.CompletedTask;
            },
            onComment: (line, lineNum) =>
            {
                // write_on_comment (config_file.c:1096-1101): buffer with
                // write_line_to semantics.
                ReadOnlySpan<byte> lineSpan = line.Span;
                bufferedComment.Write(lineSpan);
                if (line.Length == 0 || lineSpan[^1] != (byte)'\n')
                {
                    bufferedComment.Write((byte)'\n');
                }

                return Task.CompletedTask;
            },
            onEof: (currentSection) =>
            {
                // write_on_eof (config_file.c:1103-1125): flush comments, then
                // create the section and write the value if it was never written.
                FlushComment();

                if ((regexp is null || !pregReplaced) && pendingValue is not null)
                {
                    if (currentSection is not { } currentSectionValue || !AsciiEquals(currentSectionValue.Span, section.Span))
                    {
                        WriteSection(origSection);
                    }

                    WriteValue();
                }

                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-domain equality between a parser-emitted byte span and a normalized key byte span. Section/variable names are ASCII-validated by <see
    /// cref="ConfigKeyName.NormalizeNameBytes"/>, so a direct per-byte compare is exact — the byte-domain replacement for the char-domain compare (the
    /// old <c>AsciiEquals(bytes, chars)</c> truncated chars &gt; U+007F to their low byte, so a U+FFFD caller subsection could never match a raw non-UTF-8 file
    /// subsection). </summary>
    private static bool AsciiEquals(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> chars)
        => ConfigKeyName.AsciiEquals(bytes, chars);

    /// <summary>
    /// Case-insensitive byte-domain equality between a parser-emitted byte
    /// span and a normalized key byte span (ASCII-lowercase per byte — matches
    /// C's <c>strcasecmp</c> over ASCII names).
    /// </summary>
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> chars)
        => ConfigKeyName.AsciiEqualsIgnoreCase(bytes, chars);

    /// <summary>
    /// Whether a (already-escaped) value needs quotes when written. Matches
    /// <c>quotes_for_value</c> (config_file.c:937-953): leading/trailing space
    /// or an empty value, or an embedded <c>;</c>/<c>#</c>.
    /// </summary>
    private static ReadOnlySpan<byte> QuotesForValue(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value[0] == ' ' || value[^1] == ' ')
        {
            return "\""u8;
        }

        if (value.ContainsAny((byte)';', (byte)'#'))
        {
            return "\""u8;
        }

        return ReadOnlySpan<byte>.Empty;
    }

    private static SearchValues<byte> EscapeByteSet { get; } = SearchValues.Create([(byte)'\n', (byte)'\t', (byte)'\b', (byte)'"', (byte)'\\']);

    /// <summary> Byte-domain escape of a raw value byte span. Matches C's <c>escape_value</c> (config_file.c:800-826): <c>\n \t \b " \</c> get backslash
    /// escapes; everything else passes through verbatim. Subsection bytes are spliced raw
    /// (byte-domain keys), so no UTF-8 encode bridge remains. </summary>
    internal static void EscapeValueBytes(ReadOnlySpan<byte> value, PooledByteBufferWriter writer)
    {
        if (value.IsEmpty)
        {
            return;
        }

        while (!value.IsEmpty)
        {
            int index = value.IndexOfAny(EscapeByteSet);
            if (index < 0)
            {
                writer.Write(value);
                break;
            }

            writer.Write(value[..index]);

            switch (value[index])
            {
                case (byte)'\n':
                    writer.Write("\\n"u8);
                    break;
                case (byte)'\t':
                    writer.Write("\\t"u8);
                    break;
                case (byte)'\b':
                    writer.Write("\\b"u8);
                    break;
                case (byte)'"':
                    writer.Write("\\\""u8);
                    break;
                case (byte)'\\':
                    writer.Write("\\\\"u8);
                    break;
            }

            value = value[(index + 1)..];
        }
    }

    /// <summary>
    /// Reloads the config list from disk and resets the refresh stamp.
    /// </summary>
    private async Task ReloadAndRefreshAsync(CancellationToken cancellationToken)
    {
        ClearIncludes(_file);
        _configList = ConfigList.New();
        if (File.Exists(_path))
        {
            await ConfigFileReadAsync(_configList, _file, _level, depth: 0, cancellationToken).ConfigureAwait(false);
        }

        _file._stampSize = -1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // ConfigList and ConfigFile are GC-reclaimed.
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
            throw new InvalidOperationException("File config backend has not been opened.");
        }
    }

    private static bool IsReadable(string path)
    {
        try
        {
            FileStream stream = File.OpenRead(path);
            stream.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        bool modified = await IsModifiedAsync(_file, cancellationToken).ConfigureAwait(false);
        if (!modified)
        {
            return;
        }

        ClearIncludes(_file);
        _configList = ConfigList.New();
        await ConfigFileReadAsync(_configList, _file, _level, depth: 0, cancellationToken).ConfigureAwait(false);
    }

    private static void ClearIncludes(ConfigFile file)
    {
        foreach (ConfigFile include in file._includes)
        {
            ClearIncludes(include);
        }

        file._includes.Clear();
    }

    private static async ValueTask<bool> IsModifiedAsync(ConfigFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file._path))
        {
            // File was deleted — treat as not-modified; the next read returns empty.
            return false;
        }

        var info = new FileInfo(file._path);
        uint? ino = NativeStat.GetStat(info).Ino;
        if (info.LastWriteTimeUtc != file._stampMtime || info.Length != file._stampSize || ino != file._stampIno)
        {
            // Stamp changed; verify via content checksum to avoid false positives
            // from atime-only updates on some filesystems.
            byte[] checksumBuffer = ArrayPool<byte>.Shared.Rent(SHA256.HashSizeInBytes);
            try
            {
                FileStream fs = AsyncFileIO.OpenAsyncSequentialReadStream(file._path);
                await using ConfiguredAsyncDisposable fsDisposable = fs.ConfigureAwait(false);

                int bytesWritten = await SHA256.HashDataAsync(fs, checksumBuffer, cancellationToken).ConfigureAwait(false);
                if (!checksumBuffer.AsSpan(0, bytesWritten).SequenceEqual(file._checksum))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(checksumBuffer);
            }
        }

        foreach (ConfigFile include in file._includes)
        {
            if (await IsModifiedAsync(include, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ConfigFileReadAsync(ConfigList list, ConfigFile file, GitConfigLevel level, int depth, CancellationToken cancellationToken)
    {
        if (depth >= MaxIncludeDepth)
        {
            throw new GitException(
                GitErrorCode.Error,
                "maximum config include depth reached",
                GitErrorCategory.Config);
        }

        if (!File.Exists(file._path))
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"config file '{file._path}' not found",
                GitErrorCategory.Config);
        }

        var info = new FileInfo(file._path);
        file._stampMtime = info.LastWriteTimeUtc;
        file._stampSize = info.Length;
        // C (futils.c:1148-1176): the filestamp compares mtime+nsec+size+ino.
        file._stampIno = NativeStat.GetStat(info).Ino;

        PooledByteBufferWriter rawBytes;
        try
        {
            rawBytes = await AsyncFileIO.ReadAllBytesToBufferAsync(file._path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // C (config_file.c): any read failure surfaces as GIT_ENOTFOUND
            // "config file '%s' not found".
            throw new GitException(
                GitErrorCode.NotFound,
                $"config file '{file._path}' not found",
                GitErrorCategory.Config);
        }

        // C (config_file.c:894-896): the refresh checksum is computed over the RAW file bytes — hashing the Latin-1-decoded/BOM-stripped string would never
        // match IsModifiedAsync's raw-byte hash for BOM-bearing or non-ASCII files, causing a spurious re-parse on every stamp change.
        file._checksum = SHA256.HashData(rawBytes.WrittenSpan);

        try
        {
            await ConfigFileReadBufferAsync(list, file, level, depth, rawBytes.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rawBytes.Dispose();
        }
    }

    private async Task ConfigFileReadBufferAsync(ConfigList list, ConfigFile file, GitConfigLevel level, int depth, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        if (depth >= MaxIncludeDepth)
        {
            throw new GitException(
                GitErrorCode.Error,
                "maximum config include depth reached",
                GitErrorCategory.Config);
        }

        if (contents.IsEmpty)
        {
            return;
        }

        await ConfigParser.ParseAsync(
            path: file._path,
            content: contents,
            onSection: null,
            onVariable: (section, varName, varValue, line, lineNum) =>
                ReadOnVariableAsync(list, file, level, depth, section, varName, varValue, cancellationToken),
            onComment: null,
            onEof: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadOnVariableAsync(
        ConfigList list,
        ConfigFile file,
        GitConfigLevel level,
        int depth,
        ReadOnlyMemory<byte>? currentSection,
        ReadOnlyMemory<byte> varName,
        ReadOnlyMemory<byte>? varValue,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> nameBytes = ConfigKeyName.BuildFullyQualifiedNameBytes(currentSection, varName);

        ReadOnlyMemory<byte>? varValueBytes = varValue is { } varValueMem ? varValueMem.ToArray() : (ReadOnlyMemory<byte>?)null;

        var entry = new GitConfigEntry(
            NameBytes: nameBytes,
            ValueBytes: varValueBytes,
            BackendType: GitConfigEntry.FileBackendType,
            Path: file._path,
            IncludeDepth: depth,
            Level: level);
        list.Append(entry);

        // Include directive handling.
        if (nameBytes.Span.SequenceEqual("include.path"u8))
        {
            await ParseIncludeAsync(list, file, level, depth, varValueBytes, cancellationToken).ConfigureAwait(false);
        }
        else if (nameBytes.Span.StartsWith("includeif."u8)
            && nameBytes.Span.EndsWith(".path"u8))
        {
            string name = Encoding.UTF8.GetString(nameBytes.Span);
            await ParseConditionalIncludeAsync(list, file, level, depth, name, varValueBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ParseIncludeAsync(ConfigList list, ConfigFile parentFile, GitConfigLevel level, int depth, ReadOnlyMemory<byte>? includePath, CancellationToken cancellationToken)
    {
        if (includePath is null)
        {
            return;
        }

        // The include path is a filesystem path — the sanctioned conversion boundary is the UTF-8 display decode. A non-UTF-8 include path surfaces as U+FFFD
        // and fails to resolve as a file, matching the display-egress model.
        string includePathString = Encoding.UTF8.GetString(includePath.GetValueOrDefault().Span);

        // C (config_file.c:564-591): an EMPTY include.path is not NULL — the
        // parent directory is read and the load fails with an error.
        if (includePathString.Length == 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to read config file '{PathHelpers.Dirname(parentFile._path)}'",
                GitErrorCategory.Config);
        }

        string? resolved = ResolveIncludedPath(PathHelpers.Dirname(parentFile._path), includePathString);
        if (resolved is null)
        {
            return;
        }

        var include = new ConfigFile { _path = resolved };
        parentFile._includes.Add(include);

        try
        {
            await ConfigFileReadAsync(list, include, level, depth + 1, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            // Silently ignore missing include files (matches git behavior).
        }
    }

    private async Task ParseConditionalIncludeAsync(
        ConfigList list,
        ConfigFile parentFile,
        GitConfigLevel level,
        int depth,
        string sectionName,
        ReadOnlyMemory<byte>? file,
        CancellationToken cancellationToken)
    {
        if (_repoGitDirPath is null || file is null)
        {
            return;
        }

        // sectionName looks like "includeIf.<condition>.path"
        string prefix = "includeIf.";
        string suffix = ".path";
        if (sectionName.Length < prefix.Length + suffix.Length)
        {
            return;
        }

        string condition = sectionName.Substring(prefix.Length, sectionName.Length - prefix.Length - suffix.Length);

        if (condition.StartsWith("gitdir:", StringComparison.Ordinal))
        {
            string pattern = condition["gitdir:".Length..];
            if (MatchGitDir(_repoGitDirPath, parentFile._path, pattern, caseInsensitive: false))
            {
                await ParseIncludeAsync(list, parentFile, level, depth, file, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (condition.StartsWith("gitdir/i:", StringComparison.Ordinal))
        {
            string pattern = condition["gitdir/i:".Length..];
            if (MatchGitDir(_repoGitDirPath, parentFile._path, pattern, caseInsensitive: true))
            {
                await ParseIncludeAsync(list, parentFile, level, depth, file, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (condition.StartsWith("onbranch:", StringComparison.Ordinal))
        {
            string pattern = condition["onbranch:".Length..];
            if (await MatchOnBranchAsync(_repoGitDirPath, pattern, cancellationToken).ConfigureAwait(false))
            {
                await ParseIncludeAsync(list, parentFile, level, depth, file, cancellationToken).ConfigureAwait(false);
            }
        }

        // Unknown condition keyword: silently ignored (matches git).
    }

    private string? ResolveIncludedPath(ReadOnlySpan<char> baseDir, ReadOnlySpan<char> path)
    {
        // ~/ expansion — C (config_file.c:526-527) only expands `~/`, not
        // `~\` (a backslash is treated as a relative path on every platform).
        if (path.Length >= 2 && path[0] == '~' && path[1] == '/')
        {
            return _dirs.ExpandHomedirFile(path[1..]);
        }

        if (PathHelpers.IsAbsolute(path))
        {
            return path.ToString();
        }

        return PathHelpers.Join(baseDir, path);
    }

    private bool MatchGitDir(ReadOnlySpan<char> gitDir, ReadOnlySpan<char> cfgFile, ReadOnlySpan<char> condition, bool caseInsensitive)
    {
        ReadOnlySpan<char> pattern;
        if (condition.Length >= 2 && condition[0] == '.' && PathHelpers.IsDirSeparator(condition[1]))
        {
            // Relative to the config file's directory.
            string cfgDir = PathHelpers.Dirname(cfgFile);
            pattern = PathHelpers.Join(cfgDir, condition[2..]);
        }
        else if (condition.Length >= 2 && condition[0] == '~' && PathHelpers.IsDirSeparator(condition[1]))
        {
            pattern = _dirs.ExpandHomedirFile(condition[1..]);
        }
        else if (!PathHelpers.IsAbsolute(condition))
        {
            pattern = string.Concat("**/", condition);
        }
        else
        {
            pattern = condition;
        }

        // Trailing dir-separator → append '**'. C (config_file.c:601-640, do_match_gitdir) reads condition[-1] for an EMPTY pattern — the ':' of the prefix —
        // which is not a dir separator, so no '**' is appended and the include never matches (silent skip). Guard the index like C's effective behavior.
        if (condition.Length > 0 && PathHelpers.IsDirSeparator(condition[^1]))
        {
            pattern = string.Concat(pattern, "**");
        }

        // Trim trailing dir-separator from the gitdir for matching.
        ReadOnlySpan<char> gitDirTrimmed = gitDir;
        if (gitDirTrimmed.Length > 1 && PathHelpers.IsDirSeparator(gitDirTrimmed[^1]))
        {
            gitDirTrimmed = gitDirTrimmed[..^1];
        }

        WildMatchFlags flags = WildMatchFlags.Pathname;
        if (caseInsensitive)
        {
            flags |= WildMatchFlags.CaseInsensitive;
        }

        return WildMatch.IsMatch(pattern, gitDirTrimmed, flags);
    }

    private static async Task<bool> MatchOnBranchAsync(string gitDir, string condition, CancellationToken cancellationToken)
    {
        // NOTE: read .git/HEAD directly — do NOT use GitRepository.HeadAsync (which would
        // create the ODB and re-enter config parsing, causing infinite recursion).
        // C's
        // conditional_match_onbranch propagates the git_futils_readbuffer
        // failure (config_file.c:680-682), aborting the whole config read — a
        // missing or unreadable HEAD must fail the load, not silently skip
        // the conditional include.
        string headPath = PathHelpers.Join(gitDir, "HEAD");
        ReadOnlySpan<char> reference;
        try
        {
            // C (config_file.c:683): the HEAD content is rtrimmed with the ASCII-only set (git_str_rtrim) — a trailing non-ASCII whitespace (e.g. NBSP) must
            // NOT be trimmed.
            reference = AsciiText.Rtrim(await AsyncFileIO.ReadAllTextWithNoBomAsync(headPath, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException ex)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"failed to read '{headPath}'",
                GitErrorCategory.Os,
                ex);
        }

        // HEAD must be a symbolic ref: "ref: refs/heads/<branch>"
        const string Symref = "ref: ";
        if (!reference.StartsWith(Symref, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> refName = reference[Symref.Length..];
        const string RefsHeadsDir = "refs/heads/";
        if (!refName.StartsWith(RefsHeadsDir, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> branchName = refName[RefsHeadsDir.Length..];

        // Trailing '/' in condition → append '**'
        string pattern = condition;
        if (condition.Length > 0 && PathHelpers.IsDirSeparator(condition[^1]))
        {
            pattern += "**";
        }

        return WildMatch.IsMatch(pattern, branchName, WildMatchFlags.Pathname);
    }

    private sealed class ConfigFile
    {
        public string _path = string.Empty;
        public DateTime _stampMtime;
        public long _stampSize;
        public uint? _stampIno;
        public byte[]? _checksum;
        public List<ConfigFile> _includes = [];
    }
}

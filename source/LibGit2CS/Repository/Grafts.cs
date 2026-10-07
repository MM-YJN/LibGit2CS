// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.Repository;

/// <summary>
/// Grafts — reads <c>.git/info/grafts</c> and <c>.git/shallow</c> files.
/// Managed port of libgit2's <c>src/libgit2/grafts.c</c> (read side).
/// </summary>
/// <remarks>
/// <para>
/// Graft file format: one line per graft entry:
/// <code>
/// &lt;commit_oid&gt; &lt;parent_oid1&gt; &lt;parent_oid2&gt; ...
/// </code>
/// A commit with no parents becomes a root commit. A commit with extra parents
/// gains them. Parsing is strict like <c>git_grafts_parse</c>: any malformed
/// line (bad OID, missing/duplicated space, empty or <c>#</c> line, trailing
/// whitespace, CRLF) aborts the whole parse with a
/// <c>GitErrorCategory.Grafts</c> error.
/// </para>
/// <para>
/// The shallow file (<c>.git/shallow</c>) contains one OID per line (commits
/// that are shallow boundaries); it is parsed with the same strict rules
/// (each line is a graft entry with no parents).
/// </para>
/// </remarks>
internal sealed class Grafts(GitHashAlgorithmKind algorithm) : IDisposable
{
    private readonly Dictionary<GitOid, GraftEntry> _commits = [];
    private string? _path;
    private bool _disposed;

    /// <summary>Number of graft entries.</summary>
    public int Count => _commits.Count;

    /// <summary>
    /// Opens and parses a grafts file. Matches <c>git_grafts_open</c>.
    /// </summary>
    public static async Task<Grafts> OpenAsync(string path, GitHashAlgorithmKind algorithm, CancellationToken cancellationToken = default)
    {
        var grafts = new Grafts(algorithm);
        await grafts.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return grafts;
    }

    /// <summary>
    /// Opens and parses either a grafts or shallow file, refreshing if already loaded.
    /// Matches <c>git_grafts_open_or_refresh</c>.
    /// </summary>
    public static async Task<Grafts> OpenOrRefreshAsync(string path, GitHashAlgorithmKind algorithm, CancellationToken cancellationToken = default)
        => await OpenAsync(path, algorithm, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Re-reads the grafts file from disk. Matches <c>git_grafts_refresh</c>
    /// (grafts.c:109-133): a vanished file clears the grafts without error;
    /// otherwise the content is re-parsed. (C skips the re-read when the file
    /// is unchanged via a checksum — an optimization; the observable result
    /// is identical.)
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_path is { } path)
        {
            await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads (or reloads) the grafts file at <paramref name="path"/>.
    /// </summary>
    private ValueTask LoadAsync(string path, CancellationToken cancellationToken)
    {
        _path = path;
        _commits.Clear();

        // Absence of grafts/shallow is the majority case for a typical repo.
        if (!File.Exists(path))
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(LoadSlowAsync(path, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LoadAsync"/>: reads the grafts file from disk.</summary>
    private async Task LoadSlowAsync(string path, CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        Parse(content);
    }

    /// <summary>
    /// Parses grafts content. Matches <c>git_grafts_parse</c> (grafts.c:142-181):
    /// every line must start with a valid OID; any malformed line aborts the
    /// WHOLE parse with a <c>Grafts</c>-category error (there is no
    /// comment/blank-line rule — empty lines, <c>#</c> comments, leading
    /// whitespace, tab separators, and trailing spaces are all hard errors);
    /// parents are separated by exactly one space.
    /// </summary>
    private void Parse(ReadOnlySpan<byte> content)
    {
        // C's git_grafts_parse (grafts.c:142-181) first advances the 40-hex OID (git_parse_advance_oid requires no trailing separator), then the parent
        // loop's git_parse_advance_expected(" ") fails on a '\r' — so a CRLF shallow-style line (no space before the '\r') reports "invalid parent OID at line
        // N", and only a '\r' INSIDE the OID reports "invalid graft OID". The
        // normal parse below produces C's exact messages for both.
        var parser = new GitObjectParser(content);

        while (!parser.IsAtEnd)
        {
            GitOid? commitOid = parser.AdvanceOid(algorithm);
            if (commitOid is null)
            {
                throw GraftError($"invalid graft OID at line {parser.LineNumber}");
            }

            var parents = new List<GitOid>();
            while (parser.Line.Length > 0)
            {
                // Exactly one space precedes each parent OID.
                if (!parser.AdvanceExpected(" "u8))
                {
                    throw GraftError($"invalid parent OID at line {parser.LineNumber}");
                }

                GitOid? parentOid = parser.AdvanceOid(algorithm);
                if (parentOid is null)
                {
                    throw GraftError($"invalid parent OID at line {parser.LineNumber}");
                }

                parents.Add(parentOid.Value);
            }

            _commits[commitOid.Value] = new GraftEntry(commitOid.Value, parents);
            parser.AdvanceLine();
        }
    }

    private static GitException GraftError(string message)
        => new(GitErrorCode.Error, message, GitErrorCategory.Grafts);

    /// <summary>
    /// Gets the graft entry for <paramref name="oid"/>, or null if not grafted.
    /// Matches <c>git_grafts_get</c>.
    /// </summary>
    public GraftEntry? Get(GitOid oid)
    {
        if (_disposed)
        {
            return null;
        }

        return _commits.TryGetValue(oid, out GraftEntry entry) ? entry : null;
    }

    /// <summary>
    /// Returns all grafted commit OIDs. Matches <c>git_grafts_oids</c>.
    /// </summary>
    public IReadOnlyList<GitOid> Oids() => [.. _commits.Keys];

    /// <summary>
    /// Write shallow root OIDs to <c>.git/shallow</c>.
    /// Matches <c>git_repository__shallow_roots_write</c>.
    /// </summary>
    /// <param name="gitdir">The <c>.git</c> directory path.</param>
    /// <param name="oids">The shallow root commit OIDs to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteShallowAsync(string gitdir, IReadOnlyList<GitOid> oids, CancellationToken cancellationToken)
    {
        string path = Path.Join(gitdir, "shallow");

        if (oids.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        string content;
        using (var sb = new ValueStringBuilder((64 + 1) * oids.Count))
        {
            foreach (GitOid oid in oids)
            {
                sb.AppendSpanFormattable(oid, provider: CultureInfo.InvariantCulture);
                sb.Append('\n');
            }
            content = sb.ToString();
        }

        await AsyncFileIO.WriteAllTextWithNoBomAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _commits.Clear();
    }
}

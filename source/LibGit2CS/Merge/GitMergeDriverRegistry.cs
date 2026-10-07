// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Merge;

/// <summary>
/// Merge driver registry. Managed port of
/// <c>merge_driver_registry</c> + <c>git_merge_driver_register</c> /
/// <c>_unregister</c> / <c>_lookup</c> / <c>_for_source</c>
/// (<c>src/libgit2/merge_driver.c:20-31, 139-431</c>).
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="ReaderWriterLockSlim"/> for thread-safe access — matches
/// C's <c>git_rwlock</c>. Registry mutation (register/unregister) takes a
/// write lock; lookup takes a read lock. Built-in drivers (text/binary) are
/// fast-pathed by string comparison before acquiring the lock, matching
/// <c>merge_driver.c:335-338</c>.
/// </para>
/// <para>
/// Built-in <c>text</c>/<c>union</c>/<c>binary</c> drivers are registered in
/// the instance constructor. Custom drivers are registered via
/// <see cref="Register"/>. The driver's <c>initialize</c> callback is deferred
/// until first <see cref="Lookup"/> (lazy init), matching C
/// (<c>merge_driver.c:354-360</c>).
/// </para>
/// <para>
/// Per-context: each <see cref="GitContext"/> owns its own
/// merge driver registry instance. The <see cref="ReaderWriterLockSlim"/> is
/// disposed via <see cref="Dispose"/> from <see cref="GitContext.Dispose"/>.
/// </para>
/// </remarks>
public sealed class GitMergeDriverRegistry : IDisposable
{
    /// <summary>The <c>text</c> driver name. Matches <c>merge_driver_name__text</c>.</summary>
    public const string TextName = "text";

    /// <summary>The <c>union</c> driver name. Matches <c>merge_driver_name__union</c>.</summary>
    public const string UnionName = "union";

    /// <summary>The <c>binary</c> driver name. Matches <c>merge_driver_name__binary</c>.</summary>
    public const string BinaryName = "binary";

    /// <summary>The wildcard driver name. Matches <c>"*"</c> in C.</summary>
    public const string WildcardName = "*";

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, Entry> _drivers = new(StringComparer.Ordinal);

    /// <summary>
    /// Built-in driver instances. Fast-pathed by reference/string comparison
    /// in <see cref="Lookup"/>. Match <c>git_merge_driver__text</c> /
    /// <c>__union</c> / <c>__binary</c> (<c>merge_driver.c:155-180</c>).
    /// Stateless, so per-context instances are cheap.
    /// </summary>
    internal BuiltinTextDriver TextInstance { get; } = new();
    internal BuiltinUnionDriver UnionInstance { get; } = new();
    internal BuiltinBinaryDriver BinaryInstance { get; } = new();

    /// <summary>
    /// Registry entry: the driver + whether <see cref="IGitMergeDriver.Initialize"/>
    /// has been called. Matches <c>git_merge_driver_entry</c>
    /// (<c>merge_driver.c:25-29</c>).
    /// </summary>
    private sealed record Entry(IGitMergeDriver Driver, bool Initialized)
    {
        public Entry(IGitMergeDriver driver) : this(driver, Initialized: false) { }
    }

    /// <summary>
    /// Creates a new merge driver registry with the built-in drivers (text,
    /// union, binary) pre-registered — matches <c>merge_driver.c:209-214</c>.
    /// </summary>
    public GitMergeDriverRegistry()
    {
        _drivers[TextName] = new Entry(TextInstance);
        _drivers[UnionName] = new Entry(UnionInstance);
        _drivers[BinaryName] = new Entry(BinaryInstance);
    }

    /// <summary>
    /// Registers a custom merge driver under the given name. Matches
    /// <c>git_merge_driver_register</c> (<c>merge_driver.c:266-290</c>).
    /// </summary>
    /// <param name="name">The driver name. Must not be null or empty.
    /// Attempting to register with an in-use name throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.Exists"/>.</param>
    /// <param name="driver">The merge driver.</param>
    public void Register(string name, IGitMergeDriver driver)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(driver);

        _lock.EnterWriteLock();
        try
        {
            if (_drivers.ContainsKey(name))
            {
                throw new GitException(
                    GitErrorCode.Exists,
                    $"attempt to reregister existing driver '{name}'",
                    GitErrorCategory.Merge);
            }

            _drivers[name] = new Entry(driver);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes the merge driver with the given name. Matches
    /// <c>git_merge_driver_unregister</c> (<c>merge_driver.c:292-322</c>).
    /// </summary>
    /// <param name="name">The driver name to remove. Must not be null or
    /// empty. Throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.NotFound"/> if not registered.</param>
    public void Unregister(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _lock.EnterWriteLock();
        try
        {
            if (!_drivers.TryGetValue(name, out Entry? entry))
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"cannot find merge driver '{name}' to unregister",
                    GitErrorCategory.Merge);
            }

            _drivers.Remove(name);

            // Call shutdown if the driver was initialized (merge_driver.c:312-315).
            if (entry.Initialized)
            {
                entry.Driver.Shutdown();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Looks up a merge driver by name. Matches <c>git_merge_driver_lookup</c>
    /// (<c>merge_driver.c:324-363</c>).
    /// </summary>
    /// <remarks>
    /// Fast-paths the <c>text</c> and <c>binary</c> built-in drivers by
    /// string comparison before acquiring the read lock, matching
    /// <c>merge_driver.c:335-338</c>. Calls <see cref="IGitMergeDriver.Initialize"/>
    /// on first use (lazy init, <c>merge_driver.c:354-360</c>).
    /// </remarks>
    /// <param name="name">The driver name. If null or not found, returns
    /// null.</param>
    /// <returns>The driver, or null if not registered.</returns>
    public IGitMergeDriver? Lookup(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Fast-path the built-in text/binary drivers (merge_driver.c:335-338).
        if (name == TextName)
        {
            return TextInstance;
        }

        if (name == BinaryName)
        {
            return BinaryInstance;
        }

        _lock.EnterUpgradeableReadLock();
        try
        {
            if (!_drivers.TryGetValue(name, out Entry? entry))
            {
                return null;
            }

            // Lazy initialization on first use (merge_driver.c:354-360).
            if (!entry.Initialized)
            {
                // Upgrade to write lock for the state mutation.
                _lock.EnterWriteLock();
                try
                {
                    // Re-check after acquiring the write lock (double-checked).
                    if (!_drivers.TryGetValue(name, out entry))
                    {
                        return null;
                    }

                    if (!entry.Initialized)
                    {
                        entry.Driver.Initialize();
                        _drivers[name] = entry with { Initialized = true };
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            return entry.Driver;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>
    /// Looks up a driver by name, falling back to the <c>*</c> wildcard
    /// driver if the named driver is not found. Matches
    /// <c>merge_driver_lookup_with_wildcard</c>
    /// (<c>merge_driver.c:400-409</c>).
    /// </summary>
    internal IGitMergeDriver? LookupWithWildcard(string name)
    {
        IGitMergeDriver? driver = Lookup(name);
        driver ??= Lookup(WildcardName);

        return driver;
    }

    /// <summary>
    /// Resolves the merge driver name for a path via the <c>merge</c>
    /// attribute. Matches <c>merge_driver_name_for_path</c>
    /// (<c>merge_driver.c:365-397</c>).
    /// </summary>
    /// <param name="repo">The repository (for attribute lookup).</param>
    /// <param name="path">The file path.</param>
    /// <param name="defaultDriver">The default driver name from
    /// <c>merge.default</c> config, or null.</param>
    /// <returns>The driver name: <c>"text"</c> (set/unspecified-no-default),
    /// <c>"binary"</c> (unset), the default driver (unspecified-with-default),
    /// or the attribute's string value.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Conceptually an instance method — part of the registry API surface and called via ctx.MergeDrivers from tests.")]
    internal async ValueTask<string> NameForPathAsync(
        GitRepository repo,
        string path,
        string? defaultDriver,
        CancellationToken cancellationToken)
    {
        var attrPath = new AttrPath();
        attrPath.Init(path, string.Empty, AttrPath.DirFlag.False);

        AttributeCache attrCache = await repo.GetAttributeCacheAsync(cancellationToken).ConfigureAwait(false);
        GitAttrValue value = await attrCache.LookupOneAsync(attrPath, "merge", cancellationToken: cancellationToken).ConfigureAwait(false);

        // set: use the built-in 3-way merge driver ("text")
        if (value.IsTrue)
        {
            return TextName;
        }

        // unset: do not merge ("binary")
        if (value.IsFalse)
        {
            return BinaryName;
        }

        // unspecified + default_driver → use the default driver
        if (value.IsUnspecified && !string.IsNullOrEmpty(defaultDriver))
        {
            return defaultDriver;
        }

        // unspecified + no default → use "text"
        if (value.IsUnspecified)
        {
            return TextName;
        }

        // string value → use the named driver
        if (value.Kind == GitAttrValueKind.Value && value.Text is not null)
        {
            return value.Text;
        }

        return TextName;
    }

    /// <summary>
    /// Resolves the merge driver for a source, returning both the driver
    /// name and the driver instance. Matches <c>git_merge_driver_for_source</c>
    /// (<c>merge_driver.c:411-431</c>).
    /// </summary>
    /// <remarks>
    /// First computes the best path from the three index entries, then looks
    /// up the <c>merge</c> attribute for that path, then looks up the driver
    /// (with wildcard fallback).
    /// </remarks>
    /// <param name="src">The merge driver source.</param>
    /// <returns>The (name, driver) tuple. The driver may be null if neither
    /// the named driver nor the wildcard is registered; the caller should
    /// fall back to the text driver in that case.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async ValueTask<(string Name, IGitMergeDriver? Driver)> ForSourceAsync(
        GitMergeDriverSource src,
        CancellationToken cancellationToken)
    {
        // Compute the best path from the three entries (merge_driver.c:419-422).
        GitPath? path = GitMergeFile.BestPath(
            src.Ancestor?.Path,
            src.Ours?.Path,
            src.Theirs?.Path);

        // C always
        // calls merge_driver_name_for_path with the best-path result even
        // when NULL (git_attr_get handles a NULL pathname → unspecified,
        // attr.c:46-100), so unspecified-attr + merge.default yields the
        // default driver — including for add/add and rename/rename conflicts
        // (BestPath returns null).
        string driverName = await NameForPathAsync(
            src.Repo, path is { } p ? p.ToUtf8String() : string.Empty, src.DefaultDriver, cancellationToken).ConfigureAwait(false);

        // Look up the driver, with wildcard fallback (merge_driver.c:429).
        IGitMergeDriver? driver = LookupWithWildcard(driverName);

        return (driverName, driver);
    }

    /// <summary>
    /// Disposes the registry's <see cref="ReaderWriterLockSlim"/>. Called by
    /// <see cref="GitContext.Dispose"/> so the lock never outlives its
    /// context.
    /// </summary>
    public void Dispose()
    {
        _lock.Dispose();
    }
}

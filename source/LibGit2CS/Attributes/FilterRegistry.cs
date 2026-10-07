// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// Filter registry. Managed port of <c>git_filter_registry</c>
/// (<c>src/libgit2/filter.c:62-67</c>) + <c>git_filter_register</c>/
/// <c>git_filter_unregister</c>/<c>git_filter_lookup</c>
/// (filter.c:267-368).
/// </summary>
/// <remarks>
/// <para>
/// Filters are sorted by priority (ascending). CRLF has priority 0
/// (lowest, runs first on clean), ident has priority 100. Custom drivers
/// use priority 200 (<c>GIT_FILTER_DRIVER_PRIORITY</c>). Built-in filters
/// (CRLF, ident) are registered in the instance constructor. Matches
/// <c>git_filter_global_init</c> (filter.c:191-221).
/// </para>
/// <para>
/// Per-context: each <see cref="Core.GitContext"/> owns its
/// own filter registry instance.
/// </para>
/// </remarks>
public sealed class FilterRegistry
{
    private readonly Lock _lock = new();
    private List<FilterDef> _filters = [];

    /// <summary>Filter priority for the CRLF filter. Matches <c>GIT_FILTER_CRLF_PRIORITY</c> (0).</summary>
    public const int CrlfPriority = 0;

    /// <summary>Filter priority for the ident filter. Matches <c>GIT_FILTER_IDENT_PRIORITY</c> (100).</summary>
    public const int IdentPriority = 100;

    /// <summary>Filter priority for custom filter drivers. Matches <c>GIT_FILTER_DRIVER_PRIORITY</c> (200).</summary>
    public const int DriverPriority = 200;

    /// <summary>The CRLF filter name. Matches <c>GIT_FILTER_CRLF</c>.</summary>
    public const string CrlfName = "crlf";

    /// <summary>The ident filter name. Matches <c>GIT_FILTER_IDENT</c>.</summary>
    public const string IdentName = "ident";

    /// <summary>
    /// Creates a new filter registry with the built-in filters (CRLF, ident)
    /// pre-registered — matches <c>git_filter_global_init</c>
    /// (filter.c:191-221).
    /// </summary>
    public FilterRegistry()
    {
        Register(CrlfName, new Filters.CrlfFilter(), CrlfPriority);
        Register(IdentName, new Filters.IdentFilter(), IdentPriority);
    }

    /// <summary>
    /// Registers a filter with the given name and priority. Matches
    /// <c>git_filter_register</c> (filter.c:267-292). Throws if a filter
    /// with the same name already exists.
    /// </summary>
    public void Register(string name, IFilter filter, int priority)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(filter);

        lock (_lock)
        {
            if (_filters.Exists(f => f.Name == name))
            {
                throw new ArgumentException(
                    $"a filter named '{name}' is already registered",
                    nameof(name));
            }

            (int nattrs, int nmatches, List<AttrSpec>? specs) = ScanAttrs(filter.Attributes);
            var def = new FilterDef(name, filter, priority, nattrs, nmatches, specs);
            _filters.Add(def);

            // C (filter.c:55-60, 187): git_vector_sort → git__tsort (timsort, STABLE) — equal-priority filters keep registration order. List<T>.Sort is an
            // unstable introsort, so equal-priority custom drivers could swap; LINQ OrderBy is stable.
            _filters = [.. _filters.OrderBy(f => f.Priority)];
        }
    }

    /// <summary>
    /// Unregisters a filter by name. Matches <c>git_filter_unregister</c>
    /// (filter.c:294-333). Built-in filters (<c>crlf</c>, <c>ident</c>)
    /// cannot be unregistered.
    /// </summary>
    public void Unregister(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name is CrlfName or IdentName)
        {
            throw new ArgumentException(
                $"cannot unregister built-in filter '{name}'",
                nameof(name));
        }

        lock (_lock)
        {
            int idx = _filters.FindIndex(f => f.Name == name);
            if (idx < 0)
            {
                throw new ArgumentException(
                    $"no filter named '{name}' is registered",
                    nameof(name));
            }

            _filters.RemoveAt(idx);
        }
    }

    /// <summary>
    /// Looks up a filter by name. Matches <c>git_filter_lookup</c>
    /// (filter.c:348-368).
    /// </summary>
    public IFilter? Lookup(string name)
    {
        lock (_lock)
        {
            return _filters.Find(f => f.Name == name)?.Filter;
        }
    }

    /// <summary>
    /// Returns a snapshot of all registered filter definitions, sorted by
    /// priority. Used by <see cref="LibGit2CS.Attributes.GitFilterList.LoadAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.IO.GitPath, LibGit2CS.Core.GitOid?, LibGit2CS.Attributes.GitFilterMode, LibGit2CS.Attributes.GitFilterListFlags, LibGit2CS.Core.GitOid?, System.Threading.CancellationToken)"/> to build the
    /// filter chain.
    /// </summary>
    internal IReadOnlyList<FilterDef> GetAll()
    {
        lock (_lock)
        {
            return _filters.ToArray();
        }
    }

    /// <summary>
    /// Parses the <c>attributes</c> spec string into individual attribute
    /// names and their expected match values. Matches
    /// <c>filter_def_scan_attrs</c> (filter.c:72-104) +
    /// <c>filter_def_set_attrs</c> (filter.c:106-136).
    /// </summary>
    private static (int Nattrs, int Nmatches, List<AttrSpec> Specs) ScanAttrs(string? attrStr)
    {
        var specs = new List<AttrSpec>();
        if (string.IsNullOrEmpty(attrStr))
        {
            return (0, 0, specs);
        }

        int nmatches = 0;
        ReadOnlySpan<char> span = attrStr.AsSpan();
        int pos = 0;

        // C (filter.c:83-89): tokenization uses git__isspace — the ASCII set only; char.IsWhiteSpace would split on non-ASCII whitespace too.
        while (pos < span.Length)
        {
            // Skip whitespace.
            while (pos < span.Length && IsAsciiSpace(span[pos]))
            {
                pos++;
            }

            int start = pos;
            bool hasEq = false;
            while (pos < span.Length && !IsAsciiSpace(span[pos]))
            {
                if (span[pos] == '=')
                {
                    hasEq = true;
                }

                pos++;
            }

            if (pos > start)
            {
                ReadOnlySpan<char> token = span[start..pos];
                string? expectedValue = null;
                string attrName;
                bool requiresMatch = false;

                if (hasEq)
                {
                    // Token contains '=': split into name=value.
                    int eqIdx = token.IndexOf('=');
                    attrName = token[..eqIdx].ToString();
                    ReadOnlySpan<char> valuePart = token[(eqIdx + 1)..];
                    expectedValue = valuePart.ToString();
                    requiresMatch = true;
                }
                else if (token[0] == '-')
                {
                    attrName = token[1..].ToString();
                    expectedValue = "false";
                    requiresMatch = true;
                }
                else if (token[0] == '+')
                {
                    attrName = token[1..].ToString();
                    expectedValue = "true";
                    requiresMatch = true;
                }
                else if (token[0] == '!')
                {
                    attrName = token[1..].ToString();
                    expectedValue = "unset";
                    requiresMatch = true;
                }
                else
                {
                    attrName = token.ToString();
                }

                if (requiresMatch)
                {
                    nmatches++;
                }

                specs.Add(new AttrSpec(attrName, expectedValue, requiresMatch));
            }
        }

        return (specs.Count, nmatches, specs);
    }

    /// <summary>Matches C's <c>git__isspace</c> (util.c:729-735) — the ASCII whitespace set only.</summary>
    private static bool IsAsciiSpace(char c)
        => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
}

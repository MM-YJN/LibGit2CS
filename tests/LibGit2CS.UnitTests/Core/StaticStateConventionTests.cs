using System.Text.RegularExpressions;

namespace LibGit2CS.UnitTests.Core;

/// <summary> Source-scanning convention tests enforcing the invariants: (1) no static mutable state in <c>source/LibGit2CS/</c> outside of an
/// explicit allowlist, (2) the "GitContext second-to-last" convention, (3) the "no repo-first public static in the
/// <c>GitRepository</c> partials" convention, and (4) the "Encoding.Latin1 confined to <c>Core/RegexAdapter.cs</c>" invariant.
/// All process-global mutable state lives on per-context instance state on
/// <c>GitContext</c> (<c>GitSettings</c>, <c>GitSystemDirs</c>,
/// <c>GitTrace</c>, <c>FilterRegistry</c>, <c>GitTransportRegistry</c>, <c>GitMergeDriverRegistry</c>). The GitContext-position tripwire locks the
/// GitContext parameter convention; the repo-first-static
/// tripwire locks the instance-method shape of the repo-taking operation facades on <c>GitRepository</c>.
/// </summary> <remarks> <para> Like <see cref="IO.AsyncConventionTests"/>, these are intentionally conservative — false negatives (a violation slipping
/// through) are acceptable, false positives (a correct file flagged) are not. The scan is plain regex over file text (no Roslyn). </para> <para>
/// <b>Static-state check — what it flags.</b> Any <c>static</c> field or property whose declared type is a mutable collection/array: <c>T[]</c>,
/// <c>List&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>, <c>HashSet&lt;T&gt;</c>, <c>Queue&lt;T&gt;</c>, <c>Stack&lt;T&gt;</c>, <c>LinkedList&lt;T&gt;</c>,
/// <c>SortedSet&lt;T&gt;</c>, <c>SortedList&lt;K,V&gt;</c>, <c>SortedDictionary&lt;K,V&gt;</c>, <c>Collection&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>, etc.
/// Also flags <c>static event</c> declarations (mutable backing delegate), <c>[ThreadStatic]</c> fields, and <c>AsyncLocal&lt;T&gt;</c> static fields. </para>
/// <para> <b>Static-state check — what it exempts.</b> <c>const</c> (compile-time literal); <c>static readonly</c>/<c>static get-only</c> properties whose
/// declared type is <c>string</c>, an enum, a <c>sealed record</c>/ <c>readonly record struct</c>, <c>ImmutableArray&lt;T&gt;</c>,
/// <c>ImmutableList&lt;T&gt;</c>, <c>ImmutableDictionary&lt;K,V&gt;</c>, <c>ImmutableHashSet&lt;T&gt;</c>, <c>FrozenDictionary&lt;K,V&gt;</c>,
/// <c>FrozenSet&lt;T&gt;</c>, <c>Encoding</c>, <c>CompositeFormat</c>, <c>Regex</c>, <c>ReadOnlyMemory&lt;T&gt;</c>, <c>ReadOnlySpan&lt;T&gt;</c>
/// (expression-bodied), or any <c>readonly struct</c>. Custom types are exempt by default (false-negative risk, not false-positive) — the mutable-type list is
/// a closed allowlist of BCL collection types. </para> <para> <b>GitContext-position check — what it flags.</b> Any <c>public</c> <c>*Async</c> method in
/// <c>source/LibGit2CS/</c> that declares a <c>GitContext context</c> parameter but does NOT place it immediately before <c>CancellationToken
/// cancellationToken</c> (the second-to-last position per the AGENTS.md "GitContext" convention). The allowlist is empty.
/// </para> <para> <b>GitContext-position check — what it does NOT flag.</b> Methods that take
/// <c>GitRepository repo</c> instead of <c>GitContext</c> (facades deriving the repo from a libgit2 object), methods that derive the repo from a libgit2
/// object's <c>.Owner</c> (~13 methods), non-public methods, and non-<c>Async</c> methods (constructors, sync helpers). <c>GitRepository.DiscoverAsync</c> is
/// exempt — it takes an optional <c>GitContext? context = null</c> (nullable), and the substring filter matches <c>GitContext context</c> (non-nullable) only,
/// so the position check is not applied. Its <c>context</c> parameter is in the correct second-to-last position regardless. </para> <para> <b>Repo-first-static
/// check — what it flags.</b> Any <c>public static</c> method in <c>Repository/GitRepository*.cs</c> (the <c>GitRepository</c> partials) whose first parameter
/// is <c>GitRepository</c> (or <c>GitRepository?</c>). Every repo-taking operation facade is an instance
/// method on <c>GitRepository</c>, so no <c>public static</c> on the partials may take <c>GitRepository</c> first. The allowlist is empty.
/// </para> <para> <b>Repo-first-static check — what it does
/// NOT flag.</b> The check is scoped to the <c>Repository/GitRepository*.cs</c> partials only. The broader <c>source/LibGit2CS/</c> tree contains ~57
/// legitimate <c>public static</c> factory/lookup methods on domain types (<c>GitDiff.TreeToTreeAsync</c>, <c>GitRemote.LookupAsync</c>,
/// <c>GitTag.CreateAsync</c>, <c>Commit.CreateAsync</c>, etc.) that take <c>GitRepository repo</c> first but create/lookup a libgit2 object owned by the repo —
/// they are NOT operation facades and stay static. The <c>GitRepository</c> constructors <c>OpenAsync</c>/<c>InitAsync</c> take <c>GitContext context</c> (not
/// <c>GitRepository repo</c>) and are not flagged. Internal/private statics are out of scope. </para> <para> <b>Static-state allowlist.</b> The allowlist is
/// empty. The frozen diff-driver archetypes (<c>DiffDriver.Auto</c>/<c>Binary</c>/<c>Text</c>) live on
/// <c>GitContext.DiffDrivers</c>, so no production file holds static mutable state; no <c>static readonly
/// T[]</c> arrays remain in the product either (lookup tables use <c>ImmutableArray&lt;T&gt;</c>). </para> </remarks>
public partial class StaticStateConventionTests
{
    /// <summary> Files exempt from the static-mutable-state check. The allowlist is empty — the frozen diff-driver archetypes
    /// (<c>DiffDriver.Auto</c>/<c>Binary</c>/<c>Text</c>) live on <c>GitContext.DiffDrivers</c>, so no production file holds static
    /// mutable state. </summary>
    private static readonly HashSet<string> s_allowlist = [];

    // ── Check: no static mutable state outside GitContext ─────────────

    /// <summary> Verifies that no production source file declares a static mutable field/property/event outside <c>GitContext</c>. All
    /// process-global mutable state lives on per-context instance classes held by <c>GitContext</c>. </summary>
    [Fact]
    public void NoStaticMutableStateOutsideGitContext()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in IO.SourceScanner.EnumerateSourceFiles())
        {
            if (s_allowlist.Contains(relPath))
            {
                continue;
            }

            // Flag static fields/properties whose type is a mutable collection.
            foreach (Match m in StaticMutableFieldPattern().Matches(content))
            {
                string modifiers = m.Groups[1].Value;

                // const can't be a collection type (C# disallows const T[]),
                // but be explicit: const is always safe.
                if (modifiers.Contains("const", StringComparison.Ordinal))
                {
                    continue;
                }

                string type = m.Groups[2].Value;

                if (IsMutableCollectionType(type))
                {
                    int line = IO.SourceScanner.LineOf(content, m.Index);
                    string name = m.Groups[3].Value;
                    violations.Add($"{relPath}:{line}: static {type} {name} — mutable static state outside GitContext");
                }
            }

            // Flag static event declarations (mutable backing delegate).
            foreach (Match m in StaticEventPattern().Matches(content))
            {
                int line = IO.SourceScanner.LineOf(content, m.Index);
                string name = m.Groups[1].Value;
                violations.Add($"{relPath}:{line}: static event {name} — mutable static event outside GitContext");
            }

            // Flag [ThreadStatic] fields.
            foreach (Match m in ThreadStaticPattern().Matches(content))
            {
                int line = IO.SourceScanner.LineOf(content, m.Index);
                violations.Add($"{relPath}:{line}: [ThreadStatic] field — thread-static mutable state outside GitContext");
            }

            // Flag AsyncLocal<T> static fields.
            foreach (Match m in AsyncLocalStaticPattern().Matches(content))
            {
                int line = IO.SourceScanner.LineOf(content, m.Index);
                string name = m.Groups[1].Value;
                violations.Add($"{relPath}:{line}: AsyncLocal<{name}> static field — mutable static state outside GitContext");
            }
        }

        Assert.Empty(violations);
    }

    // ── Check: GitContext is second-to-last parameter in public *Async methods ──

    /// <summary> Verifies that every <c>public</c> <c>*Async</c> method in <c>source/LibGit2CS/</c> that declares a <c>GitContext context</c> parameter places
    /// it immediately before <c>CancellationToken cancellationToken</c> (the second-to-last position per the AGENTS.md "GitContext" convention). The allowlist
    /// is empty — the one pre-existing violation (<c>GitConfiguration.OpenAsync</c>) was fixed. </summary> <remarks> <para> This is the position-check
    /// tripwire: it locks the second-to-last placement on every public <c>*Async</c> method that takes a <c>GitContext</c>. </para> <para> The check is intentionally narrow: it only flags methods that <b>already have</b> a <c>GitContext context</c>
    /// parameter. The ~122 public <c>*Async</c> methods that take <c>GitRepository repo</c> instead (and reach context via <c>repo.Context</c>) are NOT flagged
    /// — out of scope here. A broader "no public static <c>*Async</c> takes <c>GitRepository</c> as first param" check would require those methods to be
    /// converted first. </para> </remarks>
    [Fact]
    public void GitContextIsSecondToLastInAsyncMethods()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in IO.SourceScanner.EnumerateSourceFiles())
        {
            foreach (Match m in PublicAsyncMethodPattern().Matches(content))
            {
                string methodName = m.Groups[1].Value;
                string paramList = m.Groups[2].Value;

                // Only check methods that declare a GitContext context parameter.
                if (!paramList.Contains("GitContext context", StringComparison.Ordinal))
                {
                    continue;
                }

                // Compliant: GitContext context is the second-to-last param,
                // immediately followed by CancellationToken <name> [= default].
                if (ContextSecondToLastPattern().IsMatch(paramList))
                {
                    continue;
                }

                int line = IO.SourceScanner.LineOf(content, m.Index);
                violations.Add($"{relPath}:{line}: '{methodName}' has GitContext context but it is not immediately before CancellationToken cancellationToken");
            }
        }

        Assert.Empty(violations);
    }

    // ── Check: Encoding.Latin1 confined to RegexAdapter ───────────────────

    /// <summary> <see cref="Encoding.Latin1"/> must only appear in <c>LibGit2CS/Core/RegexAdapter.cs</c> — the sanctioned byte-domain bijection. The
    /// 1-byte=1-char mapping exists in exactly one file: every other byte pipeline in the codebase is byte-native (diff/patch/email egress, config values,
    /// paths). </summary> <remarks> Latin1 uses were eliminated from <c>Config/</c>, <c>Diff/</c> and <c>Revwalk/</c>; the remaining
    /// 8 sites in <c>Core/RegexAdapter.cs</c> implement the byte↔char bijection behind the byte-domain regex matching (the byte-domain contract). The allowlist
    /// is exactly that single file. The scan covers the whole <c>source/</c> tree, so the allowlist entry
    /// carries the project prefix. </remarks>
    [Fact]
    public void Latin1ConfinedToRegexAdapter()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "LibGit2CS/Core/RegexAdapter.cs",
        };

        var violations = new List<string>();

        foreach ((string relPath, string content) in IO.SourceScanner.EnumerateAllSourceFiles())
        {
            if (allowlist.Contains(relPath))
            {
                continue;
            }

            foreach (Match m in Latin1Pattern().Matches(content))
            {
                int line = IO.SourceScanner.LineOf(content, m.Index);
                violations.Add($"{relPath}:{line}: Encoding.Latin1 outside the sanctioned RegexAdapter bijection — use byte-native pipelines (see AGENTS.md)");
            }
        }

        Assert.Empty(violations);
    }

    // ── Check: no public static takes GitRepository as first param in Repository partials ──

    /// <summary> Verifies that no <c>public static</c> method in the
    /// <c>Repository/GitRepository*.cs</c> partials takes <c>GitRepository</c> (or <c>GitRepository?</c>) as its first parameter. This is the
    /// tripwire that keeps the repo-taking operations (checkout, merge, stash, etc.)
    /// as instance methods on <c>GitRepository</c>. The allowlist is empty. </summary> <remarks> <para> The check is scoped to <c>Repository/GitRepository*.cs</c> (the <c>GitRepository</c>
    /// partials) rather than the whole <c>source/LibGit2CS/</c> tree, which contains ~57 legitimate <c>public static</c> factory/lookup methods on domain types
    /// that take <c>GitRepository repo</c> first but are NOT operation facades. The <c>GitRepository</c> constructors <c>OpenAsync</c>/<c>InitAsync</c> take
    /// <c>GitContext context</c> (not <c>GitRepository repo</c>) and are not flagged. Both <c>*Async</c> and non-<c>Async</c> <c>public static</c> methods are
    /// scanned. </para> </remarks>
    [Fact]
    public void NoPublicStaticTakesGitRepositoryFirstInRepositoryPartials()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in IO.SourceScanner.EnumerateSourceFiles())
        {
            if (!relPath.StartsWith("Repository/GitRepository", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match m in PublicStaticRepoFirstPattern().Matches(content))
            {
                string methodName = m.Groups[1].Value;
                int line = IO.SourceScanner.LineOf(content, m.Index);
                violations.Add($"{relPath}:{line}: '{methodName}' is public static taking GitRepository as first parameter — convert to an instance method on GitRepository");
            }
        }

        Assert.Empty(violations);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// True if <paramref name="type"/> is a mutable BCL collection/array type
    /// that should not be held in a static field. Immutable variants
    /// (<c>ImmutableArray&lt;T&gt;</c>, <c>Frozen*</c>, <c>ReadOnly*</c>) are
    /// exempt; so are <c>string</c>, enums, and custom types (conservative —
    /// false negatives acceptable, false positives not).
    /// </summary>
    private static bool IsMutableCollectionType(string type)
    {
        // T[] — array (any element type). ImmutableArray<T> doesn't match this
        // because there's no trailing [].
        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            return true;
        }

        // BCL mutable collection generics.
        return type.StartsWith("List<", StringComparison.Ordinal)
            || type.StartsWith("Dictionary<", StringComparison.Ordinal)
            || type.StartsWith("HashSet<", StringComparison.Ordinal)
            || type.StartsWith("Queue<", StringComparison.Ordinal)
            || type.StartsWith("Stack<", StringComparison.Ordinal)
            || type.StartsWith("LinkedList<", StringComparison.Ordinal)
            || type.StartsWith("SortedSet<", StringComparison.Ordinal)
            || type.StartsWith("SortedList<", StringComparison.Ordinal)
            || type.StartsWith("SortedDictionary<", StringComparison.Ordinal)
            || type.StartsWith("Collection<", StringComparison.Ordinal)
            || type.StartsWith("ICollection<", StringComparison.Ordinal)
            || type.StartsWith("IList<", StringComparison.Ordinal)
            || type.StartsWith("IDictionary<", StringComparison.Ordinal)
            || type.StartsWith("ISet<", StringComparison.Ordinal);
    }

    /// <summary>
    /// Matches a static field or static property declaration, capturing
    /// (modifiers-before-static) (type) (name). The <c>static</c> keyword
    /// is required (instance fields don't match). Handles both <c>static
    /// readonly</c> fields and <c>static ... { get; ... }</c> properties.
    /// Group 1 = leading modifiers (access + optional readonly/const/new/
    /// volatile), Group 2 = the declared type, Group 3 = the member name.
    /// </summary>
    [GeneratedRegex(
        @"(?m)^\s*((?:private|internal|public|protected|new|volatile|partial)\s+)*(?:readonly\s+|const\s+|new\s+|volatile\s+)*static\s+(?:readonly\s+)*([A-Za-z_][A-Za-z0-9_<>,\s\.\?\[\]]*?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|\{|;)",
        RegexOptions.Singleline)]
    private static partial Regex StaticMutableFieldPattern();

    /// <summary>
    /// Matches a static event declaration, capturing the event name.
    /// Group 1 = event name.
    /// </summary>
    [GeneratedRegex(@"static\s+event\s+[A-Za-z_][A-Za-z0-9_<>,\s\.\?]*\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex StaticEventPattern();

    /// <summary>
    /// Matches a <c>[ThreadStatic]</c> attribute on a field.
    /// </summary>
    [GeneratedRegex(@"\[ThreadStatic\]")]
    private static partial Regex ThreadStaticPattern();

    /// <summary>
    /// Matches an <c>AsyncLocal&lt;T&gt;</c> static field declaration, capturing
    /// the element type. Group 1 = element type.
    /// </summary>
    [GeneratedRegex(@"static\s+AsyncLocal\s*<\s*([A-Za-z_][A-Za-z0-9_<>,\s\.\?]*)\s*>")]
    private static partial Regex AsyncLocalStaticPattern();

    /// <summary>
    /// Matches any <c>Encoding.Latin1</c> reference (the
    /// <see cref="Latin1ConfinedToRegexAdapter"/> check; plain text scan over
    /// the raw source).
    /// </summary>
    [GeneratedRegex(@"Encoding\.Latin1")]
    private static partial Regex Latin1Pattern();

    /// <summary>
    /// Matches a <c>public</c> <c>Task</c>/<c>ValueTask</c>-returning method
    /// declaration whose name ends in <c>Async</c>, capturing the method name
    /// and raw parameter list. The <c>public</c> keyword is required (filters
    /// out <c>internal</c>/<c>private</c> methods). Uses <c>Singleline</c> so
    /// multi-line signatures match.
    /// Group 1 = method name, Group 2 = raw parameter list (excluding outer
    /// parens).
    /// </summary>
    [GeneratedRegex(
        @"public\s+(?:static\s+|async\s+|override\s+|new\s+|sealed\s+|virtual\s+)*(?:Task|ValueTask)(?:\s*<[^>]*>)?\s+(\w+Async)\s*\(([^)]*)\)",
        RegexOptions.Singleline)]
    private static partial Regex PublicAsyncMethodPattern();

    /// <summary>
    /// Matches the compliant "GitContext context is second-to-last" pattern
    /// within a parameter list: <c>GitContext context</c> immediately
    /// followed by <c>CancellationToken &lt;name&gt;</c> with an optional
    /// default value, anchored at the end of the parameter list. Uses
    /// <c>Singleline</c> so multi-line parameter lists match.
    /// </summary>
    [GeneratedRegex(
        @"GitContext\s+context\s*,\s*CancellationToken\s+\w+\s*(?:=\s*[A-Za-z_][A-Za-z0-9_\.]*)?\s*$",
        RegexOptions.Singleline)]
    private static partial Regex ContextSecondToLastPattern();

    /// <summary>
    /// Matches a <c>public static</c> method declaration whose first parameter
    /// is <c>GitRepository</c> (or <c>GitRepository?</c>), capturing the method
    /// name. The <c>[^()]</c> class prevents the match from crossing a
    /// parenthesis boundary (so it stays within one method signature), and
    /// <c>Singleline</c> allows multi-line signatures. Group 1 = method name.
    /// </summary>
    [GeneratedRegex(
        @"public\s+static\b[^()]*?(\w+)\s*\(\s*GitRepository\s*\??\s+\w+\s*[,)]",
        RegexOptions.Singleline)]
    private static partial Regex PublicStaticRepoFirstPattern();
}

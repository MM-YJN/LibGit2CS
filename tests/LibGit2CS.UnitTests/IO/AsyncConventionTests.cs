using System.Text.RegularExpressions;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Source-scanning convention tests for the async-IO conversion.
/// These tripwires run on every <c>dotnet test</c> and catch regressions. They are intentionally conservative — false
/// negatives (a violation slipping through) are acceptable, false positives
/// (a correct file flagged) are not.
/// </summary>
/// <remarks>
/// <para>
/// Scans <c>source/LibGit2CS/**/*.cs</c> (not test code — test projects have
/// CA2007 suppressed via <c>NoWarnForTestProjects</c>).
/// </para>
/// <para>
/// <b>Check (a)</b> — no sync-over-async bridges (<c>.GetAwaiter().GetResult()</c>,
/// <c>.Wait(</c>) in production code. The allowlist is empty.
/// </para>
/// <para>
/// <b>Check (b)</b> — every <c>async</c> <c>IAsyncEnumerable&lt;T&gt;</c>
/// method with a <c>CancellationToken</c> parameter must also carry
/// <c>[EnumeratorCancellation]</c>. Non-async methods (e.g. forwarders that
/// delegate to a private async iterator, or interface declarations) are exempt
/// — <c>[EnumeratorCancellation]</c> has no effect outside async-iterator
/// methods (CS8424).
/// </para>
/// <para>
/// <b>Check (c)</b> — every <c>Task</c>/<c>ValueTask</c>-returning <c>*Async</c>
/// method with at least one parameter must have <c>CancellationToken</c> in its
/// parameter list. CA1068 enforces CT-last if present; this check enforces
/// presence.
/// </para>
/// <para>
/// <b>Check (d)</b> — no buffered <c>System.IO.File</c> read/write/append calls
/// (<c>ReadAllBytes</c>, <c>ReadAllLines</c>, <c>ReadAllText</c>,
/// <c>WriteAllBytes</c>, <c>WriteAllText</c>, <c>AppendAllText</c>) in production
/// code. These all have <c>AsyncFileIO.*Async</c> equivalents; the only exempt
/// file is <c>IO/AsyncFileIO.cs</c> itself (which wraps the BCL sync calls).
/// Stat-style ops (<c>File.Exists</c>, <c>File.Delete</c>, <c>File.Move</c>) are
/// NOT matched — they are metadata-only.
/// </para>
/// </remarks>
public partial class AsyncConventionTests
{
    /// <summary>
    /// Files known to contain sync-over-async bridges. The allowlist is empty:
    /// all production code is free of sync-over-async bridging.
    /// </summary>
    private static readonly HashSet<string> s_syncOverAsyncAllowlist = [];

    /// <summary>
    /// Files exempt from the <see cref="NoBufferedFileIO"/> check. The only
    /// entry is <c>IO/AsyncFileIO.cs</c> itself — the helper that wraps these
    /// BCL sync calls behind async signatures. Every other site in
    /// <c>source/LibGit2CS/</c> must use the <c>AsyncFileIO.*Async</c> helpers.
    /// </summary>
    private static readonly HashSet<string> s_bufferedFileIoAllowlist =
    [
        "IO/AsyncFileIO.cs",
        // SshTransport reads private key files via
        // File.ReadAllBytesAsync (the BCL async overload). The convention
        // regex (File\.(ReadAllBytes|...)\s*\() doesn't match the *Async
        // suffix, so this entry is documentary, not load-bearing. Listed
        // for consistency with the AGENTS.md "all IO via AsyncFileIO"
        // convention.
        "Transports/SshTransport.cs",
    ];

    // ── Check (a): no sync-over-async bridges ─────────────────────────

    [Fact]
    public void NoSyncOverAsyncBridges()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in SourceScanner.EnumerateSourceFiles())
        {
            if (s_syncOverAsyncAllowlist.Contains(relPath))
            {
                continue;
            }

            if (content.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
            {
                violations.Add($"{relPath}: '.GetAwaiter().GetResult()'");
            }

            // Matches '.Wait(' or '.Wait (' but NOT '.WaitAsync(' because the
            // regex requires '(' (optionally preceded by whitespace) immediately
            // after 'Wait'.
            foreach (Match m in SyncOverAsyncWaitBridgePattern().Matches(content))
            {
                violations.Add($"{relPath}: '.Wait(' at offset {m.Index}");
            }
        }

        Assert.Empty(violations);
    }

    // ── Check (b): IAsyncEnumerable methods have [EnumeratorCancellation] ──

    [Fact]
    public void IAsyncEnumerableMethodsHaveEnumeratorCancellation()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in SourceScanner.EnumerateSourceFiles())
        {
            // A file that declares IAsyncEnumerable<T> and takes a CancellationToken
            // must also use [EnumeratorCancellation]. Per-file granularity: if a
            // file has two IAsyncEnumerable methods and only one is missing the
            // attribute, the per-method regex below catches it.
            if (!content.Contains("IAsyncEnumerable<", StringComparison.Ordinal))
            {
                continue;
            }

            // Per-method check: find each IAsyncEnumerable method signature and
            // verify its CancellationToken parameter has [EnumeratorCancellation].
            // Only async-iterator methods (those with the 'async' modifier) require
            // the attribute — [EnumeratorCancellation] has no effect on non-async
            // methods (CS8424).
            foreach (Match m in IAsyncEnumerableMethodPattern().Matches(content))
            {
                string signature = m.Value;
                bool isAsyncIterator = m.Groups[1].Success;
                if (isAsyncIterator &&
                    signature.Contains("CancellationToken", StringComparison.Ordinal) &&
                    !signature.Contains("[EnumeratorCancellation]", StringComparison.Ordinal))
                {
                    violations.Add($"{relPath}: async IAsyncEnumerable method has CancellationToken without [EnumeratorCancellation]");
                }
            }
        }

        Assert.Empty(violations);
    }

    // ── Check (c): *Async methods with parameters have CancellationToken ──

    [Fact]
    public void AsyncMethodsWithParametersHaveCancellation()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in SourceScanner.EnumerateSourceFiles())
        {
            foreach (Match m in AsyncMethodPattern().Matches(content))
            {
                string methodName = m.Groups[1].Value;
                string paramList = m.Groups[2].Value.Trim();

                // Skip parameterless methods (e.g. DisposeAsync, ConnectAsync()).
                if (string.IsNullOrEmpty(paramList))
                {
                    continue;
                }

                if (!paramList.Contains("CancellationToken", StringComparison.Ordinal))
                {
                    violations.Add($"{relPath}: '{methodName}' has parameters but no CancellationToken");
                }
            }
        }

        Assert.Empty(violations);
    }

    // ── Check (d): no buffered File.IO in production code ─────────────

    /// <summary>
    /// Verifies that no production source file calls the buffered
    /// <c>System.IO.File</c> read/write/append methods directly. These all
    /// have <c>AsyncFileIO.*Async</c> equivalents. The only exempt file is
    /// <c>IO/AsyncFileIO.cs</c> itself (the helper that wraps the BCL calls).
    /// </summary>
    [Fact]
    public void NoBufferedFileIO()
    {
        var violations = new List<string>();

        foreach ((string relPath, string content) in SourceScanner.EnumerateSourceFiles())
        {
            if (s_bufferedFileIoAllowlist.Contains(relPath))
            {
                continue;
            }

            foreach (Match m in BufferedFileIoPattern().Matches(content))
            {
                int line = SourceScanner.LineOf(content, m.Index);
                violations.Add($"{relPath}:{line}: '{m.Value.Trim()}'");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void PublicAsyncApisHaveOptionalCancellationLast()
    {
        var violations = new List<string>();
        foreach (Type type in typeof(LibGit2CS.Core.GitContext).Assembly.GetExportedTypes())
        {
            foreach (System.Reflection.MethodInfo method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!method.Name.EndsWith("Async", StringComparison.Ordinal))
                {
                    continue;
                }

                System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == nameof(IAsyncDisposable.DisposeAsync) && parameters.Length == 0 &&
                    typeof(IAsyncDisposable).IsAssignableFrom(type))
                {
                    continue;
                }

                if (parameters.Length == 0 || parameters[^1].ParameterType != typeof(CancellationToken) ||
                    !parameters[^1].IsOptional || !parameters[^1].HasDefaultValue || parameters[^1].DefaultValue is not null)
                {
                    violations.Add($"{type.FullName}.{method.Name}: requires a final optional CancellationToken with default value");
                }
            }
        }

        Assert.Empty(violations);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Matches an IAsyncEnumerable method declaration from the optional
    /// <c>async</c> modifier through the closing paren of the parameter list.
    /// Group 1 captures the <c>async</c> prefix (present only for async-iterator
    /// methods). Uses Singleline so multi-line signatures match.
    /// </summary>
    [GeneratedRegex(@"(async\s+)?IAsyncEnumerable\s*<[^>]*>\s*\w+\s*\([^)]*\)", RegexOptions.Singleline)]
    private static partial Regex IAsyncEnumerableMethodPattern();

    /// <summary>
    /// Matches Task/ValueTask-returning method declarations ending in 'Async'.
    /// Group 1 = method name, Group 2 = raw parameter list.
    /// </summary>
    [GeneratedRegex(@"(?:Task|ValueTask)(?:\s*<[^>]*>)?\s+(\w+Async)\s*\(([^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex AsyncMethodPattern();

    /// <summary>
    /// Matches buffered <see cref="File"/> read/write/append calls that have
    /// <c>AsyncFileIO.*Async</c> equivalents. Excludes metadata-only calls
    /// (<c>File.Delete</c>, <c>File.Move</c>, <c>File.Exists</c>) which are
    /// stat-equivalent and stay sync.
    /// </summary>
    [GeneratedRegex(@"File\.(ReadAllBytes|ReadAllLines|ReadAllText|WriteAllBytes|WriteAllText|AppendAllText)\s*\(")]
    private static partial Regex BufferedFileIoPattern();

    /// <summary>
    /// Matches sync-over-async <c>.Wait(</c> bridges. The required <c>(</c>
    /// (optionally preceded by whitespace) excludes <c>.WaitAsync(</c>.
    /// </summary>
    [GeneratedRegex(@"\.Wait\s*\(")]
    private static partial Regex SyncOverAsyncWaitBridgePattern();
}

using System.Text.RegularExpressions;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Source-scanning convention tests for byte-faithful paths. These tripwires run on every <c>dotnet test</c> and lock the conversion of every internal
/// git-path representation from decoded <c>string</c> to the byte-faithful <see cref="GitPath"/> value type. They
/// catch regressions where a future change reintroduces a <c>string</c>-based path comparison on what should be a raw-byte <see cref="GitPath"/>. </summary>
/// <remarks> <para> Like <see cref="AsyncConventionTests"/> and <see cref="Core.StaticStateConventionTests"/>, these are intentionally conservative — false
/// negatives (a violation slipping through) are acceptable, false positives (a correct file flagged) are not. The scan is plain regex over file text (no
/// Roslyn). </para> <para> <b>Why <c>GitPath</c> and not <c>string</c>.</b> libgit2 compares path bytes with <c>strcmp</c>/<c>memcmp</c> throughout
/// (<c>tree.c</c>, <c>index.c</c>, <c>diff.c</c>). The C# port's <see cref="GitPath"/> mirrors that: a <c>ReadOnlyMemory&lt;byte&gt;</c> wrapper compared
/// byte-wise. Decoding to a .NET <c>string</c> for comparison reintroduces the Latin-1/UTF-8 mismatch class of defect this design avoids (a
/// non-ASCII path decoded two ways yields two unequal strings → phantom rename on a clean repo). </para> <para> <b>Check (a) — no decode-then-compare.</b>
/// Flags any line that calls <c>.ToUtf8String</c> (the sole <see cref="GitPath"/> → <c>string</c> egress for display) AND also performs a comparison
/// (<c>==</c>, <c>!=</c>, <c>.Equals(</c>, <c>.StartsWith(</c>, <c>.EndsWith(</c>, <c>string.Equals(</c>, <c>string.Compare(</c>, <c>.CompareTo(</c>). Display
/// decodes (<c>sb.Append(path.ToUtf8String)</c>) are never flagged — they contain no comparison token. Applied to the whole <c>source/LibGit2CS/</c> tree:
/// <c>.ToUtf8String</c> is exclusively a <see cref="GitPath"/> method, so decoding-then-comparing is always a git-path anti-pattern regardless of subsystem.
/// </para> <para> <b>Check (b) — no <c>StringComparison.Ordinal*</c> on a <c>.Path</c> property.</b> Every public egress <c>.Path</c> is <see
/// cref="GitPath"/>/<c>GitPath?</c>, so <c>StringComparison</c> can only apply to a decoded path. Scoped to <c>.Path</c> member accesses (NOT <c>.Name</c> —
/// that is ambiguous: ref names, config keys, diff-driver names, submodule config names are all <c>.Name</c> and are NOT git paths). </para> <para> <b>Check
/// (c) — no <c>string</c>-keyed path collections in path subsystems.</b> Flags a <c>&lt;string&gt;</c> collection declaration or <c>StringComparer.Ordinal*</c>
/// on a path-named identifier inside the Diff/Index/Tree/Status/Checkout/Blame subsystems. These should be
/// <c>Dictionary&lt;GitPath,…&gt;</c>/<c>HashSet&lt;GitPath&gt;</c>. OS-filesystem path collections (e.g.
/// <c>PackObjectBackend._loadedPackPaths</c>) live outside these subsystems and are not scanned. </para> <para> <b>Documented out-of-scope site.</b>
/// <c>Diff/PatchParser.cs::CheckHeaderNames</c> compares parsed patch-header path <c>string?</c>s (<c>one != two</c>). The patch parser reads unified-diff
/// <i>text</i> (UTF-8 by format spec); the strings are transient parser state fed into <see cref="GitPath"/> deltas. This is a text-ingress boundary, the same
/// category as the content Latin-1 passthrough in <c>DiffPrinter</c>/ <c>XdiffBridge</c> (content = opaque bytes; paths = semantic identifiers — but patch text
/// is parsed as text before it becomes identifiers). None of the three checks below flag it (no <c>.ToUtf8String</c>, no <c>StringComparison</c>, no
/// collection), so no allowlist entry is needed; the rationale is recorded here so a future contributor does not "fix" it by byte-wising without converting the
/// whole parser to byte input. </para> <para> <b>Allowlists.</b> All three are empty — every internal path site is <see cref="GitPath"/>,
/// and the stragglers (a decode-then-<c>==</c> in <c>DiffPrinter.PrintOneRaw</c> and dead <c>NextComponent(ref ReadOnlySpan&lt;char&gt;,…)</c> in
/// <c>GitTree</c>) were removed when this test landed. Any hit the scanner finds is a latent regression to fix, not to allowlist. </para> </remarks>
public partial class PathConventionTests
{
    /// <summary>
    /// Files exempt from the path-convention checks. The allowlist is empty —
    /// every internal path site in the scanned subsystems is byte-faithful
    /// <see cref="GitPath"/>.
    /// </summary>
    private static readonly HashSet<string> s_allowlist = [];

    // ── Check (a): no decode-then-compare on GitPath egress ────────────

    /// <summary> Verifies that no production source line decodes a <see cref="GitPath"/> via <c>.ToUtf8String</c> and then compares the resulting string. The
    /// decode-then-compare anti-pattern reintroduces the Unicode-aware comparison in place of byte-wise <see cref="GitPath"/>
    /// ordering. Display decodes (no comparison on the line) are not flagged. </summary> <remarks> <b>Known limitation.</b> Same-line only. A decode on one
    /// line and a comparison of the local on a later line is not caught by plain regex. The value of this check is catching the common single-expression
    /// regression (<c>if (x.ToUtf8String == y)</c>, <c>x.ToUtf8String.StartsWith(...)</c>); multi-line variants require the allowlist to handle case-by-case.
    /// </remarks>
    [Fact]
    public void NoDecodeThenCompareOnGitPaths()
    {
        var violations = new List<string>();

        foreach ((string relPath, int lineNo, string line) in EnumerateSourceLines())
        {
            if (s_allowlist.Contains(relPath))
            {
                continue;
            }

            if (!line.Contains(".ToUtf8String()", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsComparisonLine(line))
            {
                violations.Add($"{relPath}:{lineNo}: decode-then-compare on GitPath egress");
            }
        }

        Assert.Empty(violations);
    }

    // ── Check (b): no StringComparison.Ordinal* on a .Path property ────

    /// <summary> Verifies that no production source line applies a <c>StringComparison.Ordinal*</c> comparison to a <c>.Path</c> property. Every
    /// <c>.Path</c> egress is <see cref="GitPath"/>/ <c>GitPath?</c>; a <c>StringComparison</c> on <c>.Path</c> can only mean a decoded-path comparison. Scoped
    /// to <c>.Path</c> member accesses only — <c>.Name</c> is ambiguous (ref/config/driver names are not git paths) and is not scanned. </summary>
    [Fact]
    public void NoStringComparisonOnPathProperty()
    {
        var violations = new List<string>();

        foreach ((string relPath, int lineNo, string line) in EnumerateSourceLines())
        {
            if (s_allowlist.Contains(relPath))
            {
                continue;
            }

            if (line.Contains("StringComparison.Ordina", StringComparison.Ordinal)
                && line.Contains(".Path", StringComparison.Ordinal))
            {
                violations.Add($"{relPath}:{lineNo}: StringComparison.Ordinal* on a .Path property");
            }
        }

        Assert.Empty(violations);
    }

    // ── Check (c): no string-keyed path collections in path subsystems ──

    /// <summary>
    /// Verifies that no production source file in the path-handling subsystems
    /// (Diff/Index/Tree/Status/Checkout/Blame) declares a <c>&lt;string&gt;</c>
    /// collection or a <c>StringComparer.Ordinal*</c> keyed by a path-named
    /// identifier. These are
    /// <c>Dictionary&lt;GitPath,…&gt;</c>/<c>HashSet&lt;GitPath&gt;</c>. A
    /// <c>string</c>-keyed path collection is a Unicode-aware regression. OS-
    /// filesystem path collections (pack-file disk paths, workdir roots) live
    /// outside the scanned subsystems and are not flagged.
    /// </summary>
    [Fact]
    public void NoStringKeyedPathCollectionsInPathSubsystems()
    {
        var violations = new List<string>();

        foreach ((string relPath, int lineNo, string line) in EnumerateSourceLines())
        {
            if (s_allowlist.Contains(relPath))
            {
                continue;
            }

            if (!IsPathSubsystem(relPath))
            {
                continue;
            }

            bool isStringCollection = line.Contains("<string", StringComparison.Ordinal)
                || line.Contains("StringComparer.Ordina", StringComparison.Ordinal);
            if (!isStringCollection)
            {
                continue;
            }

            // Only flag when the declared identifier is path-named. Non-path
            // string collections in these subsystems (e.g. DiffDriverRegistry's
            // driver-name cache) are not git paths.
            if (ContainsPathIdentifier(line))
            {
                violations.Add($"{relPath}:{lineNo}: string-keyed collection on a path identifier — use GitPath");
            }
        }

        Assert.Empty(violations);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates every line of every <c>.cs</c> file under
    /// <c>source/LibGit2CS/</c> as a (relativePath, 1-based lineNumber, line)
    /// triple. Used by the per-line checks above.
    /// </summary>
    private static IEnumerable<(string relPath, int lineNo, string line)> EnumerateSourceLines()
    {
        foreach ((string relPath, string content) in SourceScanner.EnumerateSourceFiles())
        {
            int lineNo = 0;
            foreach (string line in content.Split('\n'))
            {
                lineNo++;
                yield return (relPath, lineNo, line);
            }
        }
    }

    /// <summary>
    /// True if <paramref name="line"/> contains a comparison operator or a
    /// string-comparison method call. Used by check (a) to detect a
    /// <c>.ToUtf8String()</c> decode whose result is being compared rather than
    /// displayed.
    /// </summary>
    private static bool IsComparisonLine(string line)
        => line.Contains("==", StringComparison.Ordinal)
        || line.Contains("!=", StringComparison.Ordinal)
        || line.Contains(".Equals(", StringComparison.Ordinal)
        || line.Contains(".StartsWith(", StringComparison.Ordinal)
        || line.Contains(".EndsWith(", StringComparison.Ordinal)
        || line.Contains("string.Equals(", StringComparison.Ordinal)
        || line.Contains("string.Compare(", StringComparison.Ordinal)
        || line.Contains(".CompareTo(", StringComparison.Ordinal);

    /// <summary>
    /// True if <paramref name="relPath"/> is one of the path-handling subsystems
    /// whose git paths must be <see cref="GitPath"/>: Diff, Index, the tree
    /// subset of Objects (<c>GitTree*</c> + <c>TreeCache</c>), Status, Checkout,
    /// and Blame.
    /// </summary>
    private static bool IsPathSubsystem(string relPath)
        => relPath.StartsWith("Diff/", StringComparison.Ordinal)
        || relPath.StartsWith("Index/", StringComparison.Ordinal)
        || relPath.StartsWith("Status/", StringComparison.Ordinal)
        || relPath.StartsWith("Checkout/", StringComparison.Ordinal)
        || relPath.StartsWith("Blame/", StringComparison.Ordinal)
        || relPath.StartsWith("Objects/GitTree", StringComparison.Ordinal)
        || relPath == "Objects/TreeCache.cs";

    /// <summary>
    /// True if <paramref name="line"/> declares an identifier whose name
    /// contains "path" (case-insensitive). Used by check (c) to restrict
    /// string-collection flagging to path-keyed collections.
    /// </summary>
    private static bool ContainsPathIdentifier(string line)
        => PathIdentifierPattern().IsMatch(line);

    /// <summary>
    /// Matches an identifier containing "path" (case-insensitive). A C#
    /// identifier is <c>[A-Za-z_]\w*</c>; this matches when such a token has
    /// "path" anywhere inside it. Used by check (c).
    /// </summary>
    [GeneratedRegex(@"[A-Za-z_]\w*[Pp]ath\w*", RegexOptions.IgnoreCase)]
    private static partial Regex PathIdentifierPattern();
}

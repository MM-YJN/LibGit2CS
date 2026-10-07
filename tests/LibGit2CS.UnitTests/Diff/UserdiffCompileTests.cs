using LibGit2CS.Core;
using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Verifies that all 16 built-in userdiff funcname + word regexes compile
/// successfully. Ported from libgit2's <c>tests/libgit2/diff/userdiff.c</c>.
/// </summary>
public sealed class UserdiffCompileTests
{
    [Fact]
    public void AllBuiltinDriverRegexes_CompileSuccessfully()
    {
        foreach (DiffDriverDefinition ddef in DiffDriverRegistry.s_builtinDefs)
        {
            // Funcname patterns (may be multi-line, !-prefixed negations).
            if (ddef.Fns is { } fns)
            {
                foreach (string line in fns.Split('\n'))
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    string pattern = line.StartsWith('!') ? line[1..] : line;
                    var regex = RegexAdapter.Compile(pattern, ddef.Flags);
                    regex.Dispose();
                }
            }

            // Word regex.
            if (ddef.Words is { } words)
            {
                var wordRegex = RegexAdapter.Compile(words, RegexFlags.None);
                wordRegex.Dispose();
            }
        }
    }

    [Fact]
    public void AllBuiltinDriverNames_ArePresent()
    {
        var names = DiffDriverRegistry.s_builtinDefs.Select(d => d.Name).ToHashSet();
        Assert.Contains("cpp", names);
        Assert.Contains("csharp", names);
        Assert.Contains("python", names);
        Assert.Contains("java", names);
        Assert.Contains("html", names);
        Assert.Contains("ruby", names);
        Assert.Contains("perl", names);
        Assert.Contains("php", names);
        Assert.Contains("javascript", names);
        Assert.Contains("objc", names);
        Assert.Contains("fortran", names);
        Assert.Contains("ada", names);
        Assert.Contains("pascal", names);
        Assert.Contains("bibtex", names);
        Assert.Contains("tex", names);
        Assert.Contains("matlab", names);
        Assert.Equal(16, names.Count);
    }

    // ── the Compile transform must not change builtin behavior ── Compile UTF-8-encodes the pattern string into the byte domain; every builtin pattern is pure
    // ASCII, so the transform is identity and match behavior over ASCII sample lines must be unchanged.

    [Fact]
    public void BuiltinFuncnamePattern_MatchesSampleLine_ByteOverload()
    {
        // The cpp funcname pattern (diff_driver.c builtin table) on a sample
        // line, via the byte overload: ASCII in, ASCII out — offsets are
        // byte offsets and must equal the char offsets.
        var driver = new DiffDriver("cpp")
        {
            Type = DiffDriverType.PatternList,
            FnPatterns =
            [
                new DiffDriverPattern(RegexAdapter.Compile("^((::[[:space:]]*)?[A-Za-z_].*)$", RegexFlags.None), negate: false),
            ],
        };

        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> extractor = driver.GetFunctionNameExtractor();

        (bool isMatch, Range range) = extractor("int main() { return 0; }"u8.ToArray());
        Assert.True(isMatch);
        Assert.Equal(0, range.Start.Value);
        Assert.Equal(24, range.End.Value);
    }
}

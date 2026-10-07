using System.Text;

using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;

namespace LibGit2CS.UnitTests.Refs;

public sealed class RefSortingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    public async Task Enumerate_MixedNames_PreservesBytewiseDepthFirstOrder(int count)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefSorting_" + Guid.NewGuid().ToString("N"));
        string heads = Path.Combine(directory, "refs", "heads");
        Directory.CreateDirectory(heads);
        try
        {
            var expected = new List<string>();
            if (count > 1)
            {
                // Directory traversal sorts "a" before "a.txt" and visits its
                // descendants immediately, even though '/' sorts after '.'.
                expected.Add("refs/heads/a/x");
                expected.Add("refs/heads/a.txt");
            }

            for (int i = 0; i < count; i++)
            {
                expected.Add($"refs/heads/ascii-{i:D4}");
            }

            if (count > 1)
            {
                expected.Add("refs/heads/é");
                expected.Add("refs/heads/\uE000");
                expected.Add("refs/heads/\U0001F600");
            }

            foreach (string name in expected.AsEnumerable().Reverse())
            {
                string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "1111111111111111111111111111111111111111\n", TestContext.Current.CancellationToken);
            }

            await using var backend = new FileRefBackend(directory, directory, GitHashAlgorithmKind.Sha1);
            var actual = new List<string>();
            await foreach (RefNameKey name in backend.EnumerateNamesAsync(null, TestContext.Current.CancellationToken))
            {
                actual.Add(Encoding.UTF8.GetString(name.Bytes.Span));
            }

            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

public sealed class ConfigRenameBytesTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RenameSection_PreservesRawValuesAndDuplicates(bool useByteOverload, bool useTransaction)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigRename_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] oldSection = useByteOverload ? [.. "branch."u8, 0xFF] : Encoding.UTF8.GetBytes("branch.café.a+b");
        byte[] newSection = useByteOverload ? [.. "branch."u8, 0xFE] : Encoding.UTF8.GetBytes("branch.新");
        byte[] oldKey = [.. oldSection, .. ".value"u8];
        byte[] newKey = [.. newSection, .. ".value"u8];
        byte[] neighborKey = [.. oldSection, .. "extra.value"u8];
        byte[][] values =
        [
            [0xFF, 0xFE],
            [0xFE, 0xFF],
            Encoding.UTF8.GetBytes("café 日本語"),
            Encoding.UTF8.GetBytes(@"a.*[b](c)+?^$|\tail"),
            Encoding.UTF8.GetBytes("line"),
            Encoding.UTF8.GetBytes("line\n"),
            [0xFF, 0xFE],
        ];

        try
        {
            byte[] source =
            [
                .. "[branch \""u8, .. oldSection.AsSpan("branch.".Length), .. "\"]\n"u8,
                .. "\tvalue = "u8, 0xFF, 0xFE, .. "\n"u8,
                .. "\tvalue = "u8, 0xFE, 0xFF, .. "\n"u8,
                .. "\tvalue = café 日本語\n"u8,
                .. "\tvalue = a.*[b](c)+?^$|\\\\tail\n"u8,
                .. "\tvalue = line\n"u8,
                .. "\tvalue = line\\n\n"u8,
                .. "\tvalue = "u8, 0xFF, 0xFE, .. "\n"u8,
            ];
            await File.WriteAllBytesAsync(path, source, cancellationToken);
            using var context = new GitContext();
            await using (GitConfiguration config = await GitConfiguration.OpenAsync(path, context, cancellationToken))
            {
                // Similar section names must not be selected by a prefix or regex match.
                await config.SetBytesAsync(neighborKey, "untouched"u8.ToArray(), cancellationToken);
                using GitConfigTransaction? transaction = useTransaction ? await config.LockAsync(cancellationToken) : null;
                if (useByteOverload)
                {
                    await config.RenameSectionAsync(oldSection, newSection, cancellationToken);
                }
                else
                {
                    await config.RenameSectionAsync(Encoding.UTF8.GetString(oldSection), Encoding.UTF8.GetString(newSection), cancellationToken);
                }
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }

            // Reopen to verify persisted bytes, not just the in-memory view.
            await using GitConfiguration reopened = await GitConfiguration.OpenAsync(path, context, cancellationToken);
            var actual = new List<byte[]>();
            await foreach (GitConfigEntry entry in reopened.EnumerateAsync(cancellationToken: cancellationToken))
            {
                Assert.False(entry.NameBytes.Span.SequenceEqual(oldKey));
                if (entry.NameBytes.Span.SequenceEqual(newKey))
                {
                    actual.Add(entry.ValueBytes!.Value.ToArray());
                }
            }

            Assert.Equal(values.Length, actual.Count);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i], actual[i]);
            }

            Assert.Equal("untouched"u8.ToArray(), await reopened.GetBytesAsync(neighborKey, cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

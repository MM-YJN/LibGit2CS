# Status and diffs

Run this against a non-bare repository. Status combines HEAD-to-index and
index-to-working-directory changes. The example prints status and an unstaged patch.

```csharp
using System.Buffers;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Repository;

using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitRepository.OpenAsync(args[0], context, ct);
using var status = await repo.StatusNewAsync(cancellationToken: ct);
foreach (var entry in status.Entries)
    Console.WriteLine($"{entry.Status}: {entry.Path}");

using var diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
Console.WriteLine(await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct));

// Byte-array form for storage or transmission; no text decoding.
byte[] patch = await diff.ToBufferAsync(GitDiffPrintFormat.Patch, ct);
Console.WriteLine($"Patch bytes: {patch.Length}");

// Writer form when the caller already owns a buffer.
var writer = new ArrayBufferWriter<byte>();
await diff.ToBufferAsync(writer, GitDiffPrintFormat.Patch, ct);
Console.WriteLine($"Written bytes: {writer.WrittenCount}");
```

Choose the comparison explicitly:

| Method | Comparison |
| --- | --- |
| `DiffIndexToWorkdirAsync` | Unstaged changes |
| `DiffTreeToIndexAsync` | Tree versus staged index; pass HEAD's tree for staged changes |
| `DiffTreeToTreeAsync` | Two committed trees |
| `DiffTreeToWorkdirWithIndexAsync` | Tree versus working directory, accounting for the index |

Obtain HEAD's tree as shown in [reading repositories](reading.md). A null tree
represents an empty tree for tree comparisons, useful before the first commit.
Status and diff options control untracked/ignored files and rename detection;
do not assume a default patch includes every untracked file. `GitStatusEntry`
exposes `HeadToIndex` and `IndexToWorkdir` deltas for inspecting each side.

Text output is for display. Save the byte output when the patch must preserve
non-UTF-8 paths or content. Diffing does not stage files or change the repository.

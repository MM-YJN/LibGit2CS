# Reading repositories

Pass an existing repository path. This example handles unborn HEAD, inspects a
reference, lists the HEAD tree, reads a blob when present, and walks recent commits.

```csharp
using System.Text;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitRepository.OpenAsync(args[0], context, ct);
var head = await repo.HeadAsync(ct);
if (head is null)
{
    Console.WriteLine("No HEAD commit yet.");
    return;
}
Console.WriteLine(head.Name);
var namedRef = await repo.ReferenceLookupAsync(head.Name, ct);
Console.WriteLine(namedRef?.Name ?? "Reference no longer exists.");

using var obj = await repo.RevparseSingleAsync("HEAD", ct);
if (obj is not Commit commit)
    throw new InvalidOperationException("HEAD did not resolve to a commit.");
Console.WriteLine($"{commit.Id}: {commit.Summary}");
using var tree = await repo.ObjectLookupAsync<GitTree>(commit.Tree, ct)
    ?? throw new InvalidOperationException("Commit tree is missing.");
foreach (var entry in tree)
    Console.WriteLine($"{entry.Id} {entry.Name}");

var readme = await tree.EntryByPathAsync("README.md", ct);
if (readme is { Type: GitObjectType.Blob } file)
{
    using var blob = await repo.ObjectLookupAsync<GitBlob>(file.Id, ct);
    if (blob is not null)
        Console.WriteLine(Encoding.UTF8.GetString(blob.Content.Span));
}

using var walker = repo.NewRevWalker();
await walker.PushHeadAsync(ct);
int count = 0;
await foreach (var id in walker.WalkAsync(ct))
{
    using var item = await repo.ObjectLookupAsync<Commit>(id, ct);
    Console.WriteLine($"{id}: {item?.Summary}");
    if (++count == 10)
        break;
}
```

`ReferenceLookupAsync` looks up a full ref name without resolving symbolic refs;
`HeadAsync` resolves HEAD. Use byte-name overloads when names are not valid UTF-8.
`RevparseSingleAsync` accepts revision expressions; use typed OID lookups when you
already know the object ID. A typed lookup can reject an object of another type;
only request `GitBlob` for a blob entry, as the example does.

The walker yields OIDs; look up commits to obtain metadata. Configure its `Sort`
property before iteration when your application needs a particular order. This
example displays text; use the byte properties when preserving original data.

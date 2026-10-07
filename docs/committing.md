# Staging and committing

Use a **scratch, non-bare repository** initialized with the getting-started guide.
Pass its path as the first argument. This program creates or overwrites
`example.txt`, stages it, persists the index, writes a tree, and creates a commit
updating HEAD. Running it again creates a child commit.

```csharp
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitRepository.OpenAsync(args[0], context, ct);
await File.WriteAllTextAsync(Path.Combine(args[0], "example.txt"),
    $"Written at {DateTimeOffset.UtcNow:O}\n", ct);

var index = await repo.GetIndexAsync(ct); // Repository-owned; do not dispose separately.
await index.AddByPathAsync("example.txt", ct);
await index.WriteAsync(ct);
GitOid treeId = await index.WriteTreeAsync(ct);

var head = await repo.HeadAsync(ct);
var rawHead = await repo.ReferenceLookupAsync("HEAD", ct);
string updateRef = head?.Name ?? (rawHead as GitSymbolicReference)?.TargetName ?? "HEAD";
using var previous = head is null ? null : await repo.RevparseSingleAsync("HEAD", ct);
if (previous is not null && previous is not Commit)
    throw new InvalidOperationException("HEAD did not resolve to a commit.");
GitOid[] parents = previous is Commit parent ? [parent.Id] : [];
var signature = GitSignature.Now("Example Author", "author@example.com");
GitOid id = await repo.CommitCreateAsync(new CommitCreateOptions
{
    Tree = treeId,
    Parents = parents,
    Author = signature,
    Committer = signature,
    Message = "Update example.txt\n",
    UpdateRef = updateRef,
}, ct);
Console.WriteLine(id);
```

`AddByPathAsync` stages current file content. `WriteAsync` persists the index;
`WriteTreeAsync` stores its tree in the object database. Neither creates a commit.
The tree includes **all** staged entries, including changes staged earlier.
Unresolved index conflicts must be resolved before writing a tree.

An initial commit has no parents. Later commits normally include the current
HEAD commit as their first parent. The example selects the resolved branch ref (or the symbolic target for an unborn
HEAD); detached HEAD uses `"HEAD"`. The current implementation writes the named
reference directly: passing `"HEAD"` when it is symbolic would detach it rather
than advance its branch. Leaving `UpdateRef` null writes a commit without updating
a ref. This example assumes ordinary HEAD pointing directly to a branch, not a
chain of unborn symbolic references.
Use real application-provided author/committer identities in production. This
example uses a synthetic identity and does not change Git configuration.

Staging, tree writing, and committing are separate operations, not one transaction.
A failure or cancellation may leave staged changes or unreferenced objects behind.

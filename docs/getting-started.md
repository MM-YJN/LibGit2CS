# Getting started

The library targets .NET 11. Use the SDK pinned in [global.json](../global.json)
when building from source; see [compatibility](compatibility.md) for constraints.
Create a console application, then install the package:

```sh
dotnet new console --framework net11.0 -n GitExample
cd GitExample
dotnet add package LibGit2CS --prerelease
```

For an unpublished checkout, use a project reference to the library instead
of the package reference. The programs below replace `Program.cs`.

## Open an existing repository

Pass the repository path to `dotnet run -- /path/to/repository`.

```csharp
using LibGit2CS.Core;
using LibGit2CS.Repository;

using var cancellation = new CancellationTokenSource();
CancellationToken ct = cancellation.Token;
using var context = new GitContext();
await using var repo = await GitRepository.OpenAsync(args[0], context, ct);

var head = await repo.HeadAsync(ct);
Console.WriteLine(head is null ? "HEAD is unborn or missing." : head.Name);

```

`HeadAsync` returns the resolved branch reference, or a direct HEAD reference
when detached. Null means HEAD is unborn or missing; it is normal immediately
after initialization. Opening a nonexistent repository throws `GitException`.

## Initialize a scratch repository

Pass a new destination path. A non-bare repository has a working directory;
a bare repository (`isBare: true`) stores Git data without a working directory.

```csharp
using LibGit2CS.Core;
using LibGit2CS.Repository;

using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitRepository.InitAsync(args[0], isBare: false, context, ct);
Console.WriteLine(await repo.HeadAsync(ct) is null ? "Ready for a first commit." : "HEAD exists.");
```

Initialization does not create a commit. Continue with [staging and committing](committing.md).

## Clone

Pass a source URL or local repository path, followed by a new or empty
destination directory. This example uses default authentication behavior;
see [remotes](remotes.md) to supply credentials.

```csharp
using LibGit2CS.Core;
using LibGit2CS.Remote;

using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitClone.RunAsync(args[0], args[1], options: null, context, ct);
Console.WriteLine((await repo.HeadAsync(ct))?.Name ?? "No HEAD commit yet.");
```

Keep the context alive until the cloned repository is disposed. Cloning is a
static operation because there is no destination repository instance yet.

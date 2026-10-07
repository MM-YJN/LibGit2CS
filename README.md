# LibGit2CS

LibGit2CS is a managed C# port of **libgit2 1.9.4**. It provides Git repository,
object, reference, index, diff, history, and remote operations without a native
libgit2 dependency. Some filesystem operations use platform OS APIs.

## Requirements and installation

The library targets **.NET 11**. To build this checkout, install the SDK pinned
in [global.json](global.json). Resolved NuGet dependency versions are pinned in
the projects' checked-in `packages.lock.json` files; see the
[development guide](docs/development.md) for the dependency update workflow.
See [compatibility and limitations](docs/compatibility.md) before adopting it.

Add the package to a .NET 11 application:

```sh
dotnet add package LibGit2CS --prerelease
```

The command permits prerelease versions; pin the version you validate for your
application. Package availability and published versions are shown on the
[NuGet gallery](https://www.nuget.org/packages/LibGit2CS).

## Quickstart

In a console application with implicit usings enabled, replace `Program.cs`
with the following. Run it with `dotnet run -- /path/to/repository`:

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

The context owns settings and registries; keep it alive until its repositories
are disposed. A new repository can have no HEAD commit yet. See
[getting started](docs/getting-started.md) for initialization and cloning.

## Documentation

Start with the [usage guide](docs/README.md), then choose a workflow:

- [Core concepts](docs/concepts.md): ownership, cancellation, errors, and bytes.
- [Reading repositories](docs/reading.md): references, commits, trees, and history.
- [Status and diffs](docs/status-and-diffs.md): staged and working-tree changes.
- [Staging and committing](docs/committing.md): write an index, tree, and commit.
- [Remotes and authentication](docs/remotes.md): clone, fetch, push, HTTPS, and SSH.
- [Compatibility](docs/compatibility.md): platform and API limitations.

The API also includes merge, rebase, stash, blame, tags, submodules, and worktrees.
The guides cover core workflows, not every libgit2 entry point. NuGet packages
include XML documentation for IDE parameter help and API descriptions.

## Development and source rebuilds

See the [development guide](docs/development.md) for repository build/test
commands, Docker test-image management, and rebuilding the source bundled in
NuGet packages. [AGENTS.md](https://github.com/MM-YJN/LibGit2CS/blob/main/AGENTS.md) records implementation conventions.

## License and attribution


LibGit2CS adapts libgit2 1.9.4 and is distributed under **GPL version 2 only
with the libgit2 linking exception**. The exception permits linking the
compiled library into applications under other licenses, including proprietary
applications. GPL obligations still apply to the library in other respects,
including modifications and standalone distribution. See [LICENSE](LICENSE)
for the full terms.

Independently developed files explicitly marked **MIT** retain that license.
These include the utilities in `source/LibGit2CS/Utils/` and selected managed
wrappers and adapters. The runtime-derived `ValueStringBuilder` files and
`PooledByteBufferWriter` retain the .NET Foundation's MIT attribution. This is
not an offer to use the entire library under MIT. Dependencies retain their
own licenses.

Production file headers identify their licenses and upstream origins.
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and [LICENSES](LICENSES)
preserve additional notices and upstream author information.

NuGet packages include these notices and the corresponding library source
under `src/`. When adding or translating code, preserve its original notices;
do not infer a file's license merely from its directory or managed API shape.

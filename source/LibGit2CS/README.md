# LibGit2CS

A managed C# port of **libgit2 1.9.4** for reading and writing Git repositories,
inspecting history and diffs, and working with remotes. No native libgit2
installation is needed; filesystem operations can use platform OS APIs.

## Install

This package targets **.NET 11**. Review the [compatibility guide](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/compatibility.md)
for requirements and limitations.

```sh
dotnet add package LibGit2CS --prerelease
```

Pin the package version validated by your application. XML API documentation
is included for IDE IntelliSense.

## Open a repository

Use this as `Program.cs` in a .NET 11 console application with implicit usings.
Pass an existing repository path as the first command-line argument:

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

`GitContext` owns settings and registries. Dispose repositories before their
context. `HeadAsync` returns null for an unborn or missing HEAD.

## Learn more

- [Getting started](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/getting-started.md)
- [Ownership, cancellation, errors, and bytes](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/concepts.md)
- [Reading history and objects](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/reading.md)
- [Status and diffs](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/status-and-diffs.md)
- [Staging and commits](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/committing.md)
- [Remotes and authentication](https://github.com/MM-YJN/LibGit2CS/blob/main/docs/remotes.md)
- [Source repository](https://github.com/MM-YJN/LibGit2CS)

These links describe the current main branch. The package includes its
corresponding library source, build inputs, and guides under `src/`; consult
those for the version you installed. To rebuild, extract the package and follow
`src/docs/development.md` under “Building the packaged source”.

## License

LibGit2CS is distributed under **GPL version 2 only with the libgit2 linking
exception**. Independently developed files explicitly marked MIT retain that
license; the entire library is not offered under MIT. Dependencies retain their
own licenses. The package includes the complete LICENSE and third-party notices.

See the [license](https://github.com/MM-YJN/LibGit2CS/blob/main/LICENSE) and
[attribution](https://github.com/MM-YJN/LibGit2CS/blob/main/THIRD-PARTY-NOTICES.txt).

# Usage guide

LibGit2CS exposes Git operations as managed objects and asynchronous methods.
Start with [getting started](getting-started.md) and [core concepts](concepts.md).

| Task | Guide |
| --- | --- |
| Install, open, initialize, clone | [Getting started](getting-started.md) |
| Manage lifetimes, errors, cancellation, and bytes | [Core concepts](concepts.md) |
| Inspect references, commits, trees, blobs, history | [Reading repositories](reading.md) |
| Inspect staged and working-tree changes | [Status and diffs](status-and-diffs.md) |
| Stage files and create commits | [Staging and committing](committing.md) |
| Fetch, push, and authenticate | [Remotes and authentication](remotes.md) |
| Understand framework and platform constraints | [Compatibility](compatibility.md) |
| Build, test, and rebuild packaged source | [Development](development.md) |

Examples are complete console programs unless identified as a helper class.
They assume a .NET 11 project with implicit usings and nullable reference types
enabled. Pass filesystem paths as command-line arguments; use forward slashes
for paths *inside* a Git repository. Mutation examples should be run in a
scratch repository. Credentials and server URLs are supplied by your application.

Return to the [project README](../README.md).

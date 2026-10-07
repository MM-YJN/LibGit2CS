# Core concepts

## Context and ownership

Create `GitContext` with `using`, then open repositories with `await using`.
The context owns environment/configuration inputs, settings, tracing, and
registries. There is no process-default singleton. A repository uses the supplied
context and does not dispose it. Close repositories before disposing their context.

Repository-bound factories normally live on `GitRepository`, for example
`repo.StatusNewAsync`, `repo.ObjectLookupAsync<T>`, and `repo.NewRevWalker`.
Initialization, opening, and `GitClone.RunAsync` create repository instances.

| Value | Caller lifetime |
| --- | --- |
| `GitContext` | `using`; outlives repositories |
| `GitRepository`, `GitRemote` | `await using` |
| Looked-up objects, diffs, status lists, revision walkers | `using` |
| `repo.GetIndexAsync()` result | Borrowed repository-owned index; let the repository dispose it |
| Reference snapshots, OIDs, signatures, options | No disposal needed |

Keep the repository alive while using its objects and handles. Follow the
individual API's ownership documentation for standalone backends and indexes.
Do not assume that concurrent mutation through one repository/index is safe.

## Async and cancellation

Pass a `CancellationToken` as the final argument to async calls. Real applications
can use a caller's request token or a `CancellationTokenSource`; short examples
use `CancellationToken.None`. Pass the token to `WalkAsync(ct)` and other async
iterators as well. Cancellation is normally reported as `OperationCanceledException`.

Cancellation is not rollback: earlier filesystem or reference updates may already
have completed. Native file copies and durable disk flushes cannot be interrupted
once started. Await operations before disposing their context or repository.

## Errors and missing values

Git errors are represented by `GitException`, with `Code` (`GitErrorCode`) and
`Category` (`GitErrorCategory`). Argument validation and underlying IO can also raise standard
.NET exceptions. Inspect error codes rather than matching message text.

Some lookup APIs explicitly return null; check their return contracts.
`HeadAsync` returns null for an unborn/missing HEAD, while other operations may
throw for missing input. Avoid treating every error as “not found”.

## Bytes and display strings

Git can contain names and content that are not valid UTF-8. `GitPath`, reference
`NameBytes`, signature `NameBytes`/`EmailBytes`, and config `ValueBytes` preserve
raw bytes. String conveniences are for UTF-8 input or display; a replacement-decoded
name is not a reliable lookup key for arbitrary bytes.

Diff/patch output has writer, byte-array, and text tiers. Prefer
`ToBufferAsync(writer, ...)` or `ToBufferAsync(...)` for saving or forwarding data.
`ToBufferTextAsync(...)` decodes UTF-8 with replacement for display only. Blob
`Content` is bytes and may be binary; decode only when your application expects text.

Repository-relative paths use `/`. Filesystem paths passed to open/init APIs use
normal platform path conventions. See [filesystem limitations](compatibility.md).

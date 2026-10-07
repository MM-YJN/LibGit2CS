# AGENTS.md

LibGit2CS is a **managed C# port of libgit2 1.9.4**.

## Repository layout and development

- `source/LibGit2CS/`: library; `Repository/` contains the repository entry points,
  with implementations grouped by subsystem (such as `Objects/`, `Refs/`,
  `Diff/`, and `Transports/`).
- `tests/LibGit2CS.UnitTests/`: xUnit v3 unit tests, embedded `Fixtures/`, and
  `generate-goldens.sh` for reference fixture generation.
- `tests/LibGit2CS.IntegrationTests/`: xUnit v3 integration tests, including
  Docker-backed SSH and git-daemon tests and embedded fixtures.
- `LibGit2CS.slnx`: library, benchmarks, and both test projects. Read their project files for
  target frameworks and dependencies, and `Directory.Build.props` for shared
  build and analyzer settings. `global.json` pins the SDK version and selects
  Microsoft Testing Platform (MTP). Install the SDK pinned there.
  `Directory.Build.props` enables NuGet package lock files; each project's
  checked-in `packages.lock.json` pins its resolved dependency versions.
- `README.md`: project introduction and links to usage documentation.
- `docs/development.md`: build/test, package rebuild, and integration-test Docker image guidance.

Run development commands from the repository root:

```sh
dotnet restore LibGit2CS.slnx
dotnet build LibGit2CS.slnx --no-restore
dotnet build LibGit2CS.slnx -c Release --no-restore
dotnet format LibGit2CS.slnx --verify-no-changes --no-restore
dotnet test --solution LibGit2CS.slnx --no-build
```

For focused tests, discover names first and put xUnit runner options after `--`:

```sh
dotnet test --project tests/LibGit2CS.UnitTests/LibGit2CS.UnitTests.csproj --no-build --list-tests
dotnet test --project tests/LibGit2CS.UnitTests/LibGit2CS.UnitTests.csproj --no-build -- --filter-class "LibGit2CS.UnitTests.Core.GitOidTests"
```

Omit the runner filter to run the full unit suite alone. Use
`.agents/skills/dotnet-mtp-tests/SKILL.md` for filtering and coverage details.
The test projects use MTP with `coverlet.MTP`; do not substitute VSTest
`--filter` or `--collect:"XPlat Code Coverage"` options. Keep generated coverage
and reports under `artifacts/`. See `.agents/skills/reportgenerator/SKILL.md`
for coverage reports and `.agents/skills/dotnet-inspect/SKILL.md` for .NET API
inspection.

Docker-backed integration tests require Docker with Linux containers and
network access when building images; host SSH-agent tests also require
`ssh-agent` and `ssh-add`. Images are built on demand and cached as documented
in `docs/development.md`. Allow generous timeouts for image builds and container startup
plus test execution, even for a single test. Slow fixture startup alone does
not imply a hung test. Report skipped tests separately from successful
integration validation.

For code changes, run relevant tests while iterating and the full solution
tests before finishing. For comment-only or documentation-only changes,
verify paths, examples, and claims; review the diff and run `git diff --check`.
A build or test run is unnecessary unless executable behavior is affected.
Report checks performed and any checks that could not run.

## General implementation guidance

- Follow `.editorconfig` and surrounding code, including nullable annotations
  and XML documentation for public APIs. Preserve copyright and license headers.
- For whitespace or code-style warnings, try `dotnet format LibGit2CS.slnx`.
  Review the diff and keep formatting changes scoped to the task.
- Preserve the managed implementation, asynchronous IO model, and AOT
  compatibility declared by the library project.
- Add regression coverage for behavior fixes, using byte-exact fixtures and
  integration tests where appropriate.
- Update README documentation when public functionality needs usage guidance
  or existing documentation becomes inaccurate. Internal refactoring, test-only
  changes, and fixes restoring documented behavior do not require README edits.
- Keep dependency and SDK changes intentional and avoid unrelated churn.
  Restore and validate the solution after such changes. After any dependency
  update, regenerate the package lock files by running
  `dotnet restore LibGit2CS.slnx --force-evaluate` from the repository root.
  Review and include the resulting `packages.lock.json` changes with the
  dependency update.

## API and ownership conventions

- **Errors**: C `int` return codes → throw `GitException(ErrorCode, message, ErrorCategory)`
  on negative codes. No per-thread error cache — the exception carries it.
  - **Not-found exceptions to this rule**: a small set of documented public APIs return `null` for a not-found-like
    scenario where C returns `GIT_ENOTFOUND`/`GIT_EUNBORNBRANCH` — these are documented in
    their XML doc and chosen because null is the idiomatic managed shape (e.g.
    `GitRepository.HeadAsync` for an unborn/missing HEAD). New APIs opting into this shape
    must document it; the default remains throw.
- **Handles**: C `git_xxx *` + `git_xxx_free()` → `IDisposable` for in-memory cleanup, or
  `IAsyncDisposable` when `Dispose`/`free` performs IO. Caller uses `using` vs `await using`
  accordingly. Managed snapshots with no cleanup may omit disposal even when the C
  counterpart has a free function (e.g. immutable `GitReference` records).
  Implementations of shared async backend/transport contracts retain
  `IAsyncDisposable` even when their own cleanup completes synchronously.
- **OIDs and async buffers**: `const git_oid *` → `GitOid` (by value, no `in` modifier). `Span<T>` →
  `ReadOnlyMemory<byte>`/`byte[]` on async signatures (no `Span` across `await`).
- **Options structs**: drop the C `version` field; `sealed record` with `init` properties.
- **Callbacks**: named delegates and `Func<>`/`Action<>` closures are both accepted; iteration →
  `IAsyncEnumerable<T>` with `yield return` + `[EnumeratorCancellation]` on the CT;
  progress → `IProgress<T>` (async-friendly, unchanged). Notification callbacks stay sync;
  credential/push-negotiation callbacks that may do IO return `Task<…>` and take a final `CancellationToken`
  (`CertificateCheck` stays sync — BCL `HttpClientHandler` TLS callback has no async variant).

## Managed data structures and paths

- **`git_buf`/`git_str`** are never ported — use `string`/`ReadOnlyMemory<byte>`/`byte[]`
  for returns and `StringBuilder`/`MemoryStream`/`List<byte>` for internal scratch.
  For byte-faithful data, follow the byte-native rules below; strings are display
  conveniences, not intermediates for round-tripping raw bytes.
- **Internal paths use forward slashes** always; convert to OS-native via `System.IO.Path`
  only at the filesystem boundary.
- **No `git_pool`/`git_vector`** — GC + `List<T>`/`Dictionary<,>`. (Sorted vectors stay
  `List<T>` + `BinarySearch`/`Sort`; revwalk needs a real `MinHeap`, not BCL `PriorityQueue`.)

## Async IO and cancellation

- **Async IO**: filesystem reads and writes must be async and accept a `CancellationToken`,
  except for the synchronous copy and durable-flush operations below.
  Prefer standard BCL methods (`File.ReadAllBytesAsync`, `File.ReadAllTextAsync`,
  `File.WriteAllBytesAsync`, `File.WriteAllTextAsync`) directly when their encoding
  and byte behavior fit the operation. `IO/AsyncFileIO.cs` provides helpers for
  additional encoding, atomic-write, and streaming behavior: `ReadAllTextWithNoBomAsync`,
  `WriteAllTextWithNoBomAsync`, `WriteAtomicAsync`, `WriteAtomicTextAsync`,
  `WriteAtomicIfMissingAsync`, `ReadLinesAsync`, `AppendAllTextAsync`. No synchronous
  `File.Read*`/`Write*`/`AppendAllText` in `source/LibGit2CS/`. Stat/metadata ops (`File.Exists`/
  `Directory.Exists`/`FileInfo.*`/`File.Delete`/`File.Move`/`Directory.Delete`) stay sync —
  synchronous metadata calls are permitted by project policy but can block,
  particularly on network filesystems. Synchronous IO exceptions:

  - `File.Copy` is allowed to retain native copy optimizations and platform copy semantics
    (including permissions and attributes). Copy helpers may remain synchronous without
    an `Async` suffix; accept and propagate a `CancellationToken`, checking it before
    each file copy and between recursive directory steps. An individual copy blocks
    and cannot be cancelled once started. `File.Copy` does not guarantee atomicity.
  - `FileStream.Flush(flushToDisk: true)` is allowed when durable flushing is required;
    `FlushAsync` has no equivalent flush-to-disk option. Check cancellation before the
    flush; the flush itself blocks and cannot be cancelled once started.

  Outside these exceptions and metadata operations, methods performing IO end in `Async`. Prefer
  `Task<T>` for simplicity. Use `async ValueTask<T>` only when the method's first `await`
  is expected to complete synchronously most of the time (cache-hit/hot paths, e.g.
  `AttributeCache` / config-snapshot lookups). If a method has a synchronous fast path
  that returns before any `await` in the majority of cases, return `ValueTask.FromResult(...)`
  from that path and delegate the slow path to a `Task<T>`-returning method — do not make
  the whole method `async ValueTask`. Methods that perform no real IO stay synchronous,
  except implementations of a shared asynchronous interface or delegate contract whose
  other implementations may perform IO (e.g. revwalk's `NextHandler`). Such implementations
  may retain the `Async` suffix and return a completed `Task`/`ValueTask` without `async`.
  `IAsyncDisposable.DisposeAsync` keeps returning `ValueTask` (interface contract; prefer
  `default`/`ValueTask.CompletedTask` bodies when the dispose itself is synchronous).
- **Cancellation**: every externally accessible public `*Async` method must take
  `CancellationToken cancellationToken = default` as its **last parameter**, except
  methods whose interface or base-class contract has no cancellation parameter
  (e.g. `IAsyncDisposable.DisposeAsync()`). This public API requirement does not apply
  to members of internal types or to internal/private methods. Propagate cancellation
  through every call in the chain regardless of visibility; async `IAsyncEnumerable<T>`
  iterators use `[EnumeratorCancellation]` so `await foreach` propagates it.
- **Await hygiene**: `.ConfigureAwait(false)` on every `await` in `source/LibGit2CS/`
  (CA2007 at warning; test projects are exempt via `NoWarnForTestProjects` and additionally
  suppress xUnit1030).

## Context and repository entry points

- **`GitContext`**: public entry points that open a `GitRepository` or construct an object
  requiring context-owned settings, registries, or environment state must accept
  `GitContext context`. Standalone objects that need none of that state may omit it
  (e.g. `GitIndex.OpenAsync` and `GitMailmap`). Objects must use the supplied context's
  state rather than create independent defaults (e.g. `GitObjectDb` uses `context.Settings`).
  For async entry points accepting a context, place it immediately before the final
  `CancellationToken cancellationToken = default` parameter. The context owns all
  process-global state as per-context instance sub-objects: `ctx.Env`, `ctx.Settings`, `ctx.Trace`,
  `ctx.Dirs`, `ctx.Filters`, `ctx.Transports`, `ctx.MergeDrivers`, `ctx.DiffDrivers`. There is no process-default
  singleton — callers `using var ctx = new GitContext();` at the top of the call chain
  and pass it down; `GitRepository` does not own the context. Internal methods that already
  hold a `GitRepository repo` reach the context via `repo.Context` (no `GitContext` parameter
  threading needed). `public const string` values on the converted instance classes
  (`FilterRegistry.CrlfName`, `GitMergeDriverRegistry.TextName`, …) stay — compile-time
  literals are not state.
- **Entry points live on `GitRepository`.** Every public operation that creates a
  repo-bound handle/object is an instance method on `GitRepository`
  (`repo.StatusNewAsync`, `repo.DiffTreeToTreeAsync`, `repo.TagCreateAsync`,
  `repo.BlobCreateFromDiskAsync`, `repo.CommitCreateAsync`, `repo.RemoteLookupAsync`,
  `repo.SubmoduleLookupAsync`, `repo.RevparseSingleAsync`, `repo.NewRevWalker`, …).
  The underlying factory/ctor (`GitStatusList.NewAsync`, `GitDiff.TreeToTreeAsync`,
  `GitTag.CreateAsync`, `GitBlob.CreateFromDiskAsync`, `Commit.CreateAsync`,
  `GitRemote.LookupAsync`, `GitSubmodule.LookupAsync`, `GitRevParser.ParseSingleAsync`,
  the `GitRevWalker`/`GitTreeBuilder` constructors, …) is `internal`. Factory groupings
  live in `GitRepository.*.cs` partials by subsystem. Internal callers that already hold
  a `GitRepository` may call the internal factory directly. Operations that take no
  explicit repository parameter (`GitDiff.Buffers`, `GitDiff.FromBuffer`, `GitPatch.FromBuffer`,
  `GitAnnotatedCommit.FromCommit`, `Commit.AmendAsync`) remain public statics on their
  type. `Commit.AmendAsync` still requires an owning repository through its commit.
  Standalone `GitObjectDb` lookup/write APIs remain public for databases created
  without a repository; repository callers use `repo.ObjectLookupAsync`,
  `repo.ObjectLookupPrefixAsync`, and `repo.ObjectWriteAsync`. Reference factories and
  pack-writer construction use `repo.Reference*`, `repo.NewReferenceTransaction`, and
  `repo.NewPackWriter`; their underlying factories are internal.

## Byte handling and keyed stores

- **Byte-native egress**: diff/patch/email output, config values, hunk
  headers and paths are bytes end-to-end — no `string` intermediate. Egress tiers are
  `ToBufferAsync(IBufferWriter<byte>, …)` (writer first) → `ToBufferAsync(…)`
  (`Task<byte[]>`, git_buf parity) → `ToBufferTextAsync(…)` (UTF-8 decode **with
  replacement** for display only). Already-materialized diff statistics use synchronous
  `GitDiffStats.Format(IBufferWriter<byte>, …)`. Formatting performs no IO and remains
  byte-native; the asynchronous three-tier API requirement does not apply.
  Config values are byte-primary
  (`GitConfigEntry.ValueBytes`) with UTF-8 string convenience; config file IO is
  byte-faithful. Config entry **names** are byte-primary too (`NameBytes`; `Name`
  is the lazy UTF-8 display decode) — `EnumerateAsync(pattern)` and config value
  compares match raw bytes (C's `git_regexp`/`strcmp` over `char*`); caller
  strings are UTF-8-encoded once before byte compares. Regex matching operates over
  **bytes** (`RegexAdapter` byte overloads; patterns UTF-8-encoded into the byte
  domain). The 1-byte=1-char bijection is implemented with `Encoding.Latin1`
  **only inside `Core/RegexAdapter.cs`** (the sanctioned byte-domain regex
  bijection) — every other pipeline is byte-native;
  a source-scanning convention test enforces this. Tests use byte comparisons or
  UTF-8 display decodes, never `Encoding.Latin1`.
- **Byte-keyed stores**: in-memory keyed collections over raw
  name bytes use the readonly-struct key types (`ConfigNameKey` for config
  names, `RefNameKey` for refnames — hand-written `SequenceEqual` equality +
  FNV-1a hash, never `record struct` over a `ReadOnlyMemory<byte>` field) or
  `GitPath` (byte-faithful paths); `ByteOrdinalComparer`/
  `RefNameKeyComparer` provide C's `strcmp` ordering for sorted collections.
  `GitSignature` stores `NameBytes`/`EmailBytes` (store-bytes-always; `Name`/
  `Email` are lazy UTF-8 display decodes) and mailmap lookups compare raw
  bytes. The string tier cannot reach a non-UTF-8 key — the byte-key API is
  the parity surface; U+FFFD display decodes are egress-only.
  Reference snapshots expose `NameBytes` / `TargetNameBytes`; `Name` / `TargetName`
  are display conveniences. Packed-reference lookup and glob matching preserve arbitrary
  name bytes. Loose-ref and reflog filename operations currently require lossless UTF-8
  conversion at the managed filesystem boundary and throw `GitException(InvalidSpec, …)`
  for unrepresentable names (including a packed lookup miss requiring a loose-file probe).
  Raw POSIX filename support is not implemented. Existing
  text-only consumers (such as local transport advertisements) also reject unrepresentable
  names instead of using replacement-decoded identifiers.

## Convention checks

- **Source-scanning checks**: `AsyncConventionTests` (4 source-scanning checks and 1 public API check) +
  `StaticStateConventionTests` (4 source-scanning checks) run as part of the full
  unit and solution test suites; integration-only or filtered runs can exclude them.
  They check: no sync-over-async bridges (`.GetAwaiter().GetResult()`/`.Wait(`),
  `IAsyncEnumerable` CTs need `[EnumeratorCancellation]`, `*Async` methods with parameters need
  a CT, no buffered `File.*` IO, no static mutable state outside `GitContext`,
  `Encoding.Latin1` confined to `LibGit2CS/Core/RegexAdapter.cs` (the Latin1
  tripwire scans the whole `source/` tree).
  All allowlists in these checks are empty except the buffered-File-IO check,
  which exempts `IO/AsyncFileIO.cs` and `Transports/SshTransport.cs`, and the
  Latin1 check, which allows only `LibGit2CS/Core/RegexAdapter.cs`.

## Tests & fixtures

- Golden fixtures live under `tests/*/Fixtures/` and are **embedded resources**
  (`<EmbeddedResource Include="Fixtures\**\*" />`).
- **Use underscores only in fixture folder names** — MSBuild rewrites hyphens in
  *folder* names to `_` (but preserves them in file names); fixtures avoid the ambiguity.
- Treat golden fixtures as reference evidence. Investigate mismatches before
  changing expected output; do not regenerate goldens merely to make tests pass.
  Read `tests/LibGit2CS.UnitTests/generate-goldens.sh` and relevant generation
  scripts before changing reference fixtures. Generation may require an
  external libgit2 checkout; ordinary unit tests use embedded fixtures.
  Record source revisions, generation details, and attribution when adding or
  replacing upstream material.
- Respect `.gitattributes`: C# source uses CRLF, shell scripts use LF, and
  fixture text uses LF with explicit binary exceptions. Do not normalize
  fixture bytes or add/remove final newlines as cleanup.

## Attribution

Preserve `LICENSE`, `source/LibGit2CS/LICENSE`, and applicable upstream and
fixture-specific notices. When translating or migrating code, preserve upstream
copyright and permission notices, record provenance, and retain file-specific
terms in the source. Do not substitute another project's license. Keep required
notices with redistributed source and binary artifacts.

## Before committing

Before any agent-created commit, verify the effective repository setting with
`git config --get core.autocrlf`: it must be `true` on Windows and `input` on
Linux or macOS. If the setting is missing or incorrect, ask the user to run
the appropriate command from the repository root:

- Windows: `git config core.autocrlf true`
- Linux/macOS: `git config core.autocrlf input`

Verify the setting again before committing. Do not commit until it matches
the operating system.

# Compatibility and limitations

## Runtime and platforms

The target is `net11.0`. The source checkout pins its SDK in
[global.json](../global.json); .NET 10 projects cannot reference this target.
Treat the current 0.x API as evolving and validate the version you adopt.

The managed Git implementation does not require native libgit2. Filesystem
metadata and symlink operations use OS interop where needed. Linux, macOS/BSD,
and Windows have platform-specific code paths; filesystem case sensitivity,
permissions, and symlink support affect behavior. Do not infer a tested support
matrix from the presence of a code path. Docker integration tests require Linux
containers. NativeAOT compatibility is declared by the project; that declaration
alone is not a guarantee that every platform and application has been published
and exercised under NativeAOT.

## Git compatibility

The adaptation targets libgit2 1.9.4, not every feature of the Git command-line
client. Managed ownership, async IO, and exception/null contracts differ from C.
The API includes reserved blame copy/move-tracking flags; they do not implement
Git CLI `-M`/`-C` behavior. Local transport does not support shallow fetch.
Octopus merges are not implemented. Read the API documentation for each operation
rather than assuming every exposed flag corresponds to implemented behavior.

`CommitCreateOptions.UpdateRef` currently writes the named reference directly.
To keep HEAD attached, pass the branch reference rather than `"HEAD"`; see the
[commit example](committing.md). Passing `"HEAD"` replaces symbolic HEAD with a
direct reference.

## Paths and bytes

In-memory path and reference operations preserve raw bytes. Loose-reference and
reflog filenames currently require lossless UTF-8 conversion at the managed
filesystem boundary. Unrepresentable names throw `GitException` with `InvalidSpec`,
including a packed-reference lookup miss that needs a loose-file probe. Raw POSIX
filename support is not implemented. Some text-based consumers, including local
transport advertisements, also reject unrepresentable reference names.

Display strings can replace invalid UTF-8. Retain byte keys and byte output when
round-tripping names, config values, patches, or signatures. See [core concepts](concepts.md).

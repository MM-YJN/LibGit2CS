# Development

## Build and test the repository

Install the SDK pinned in [global.json](../global.json), then run from the root:

```sh
dotnet restore LibGit2CS.slnx
dotnet build LibGit2CS.slnx --no-restore
dotnet build LibGit2CS.slnx -c Release --no-restore
dotnet test --solution LibGit2CS.slnx --no-build
dotnet format LibGit2CS.slnx --verify-no-changes --no-restore
```

The SDK version is pinned in `global.json`. NuGet package locking is enabled in
[Directory.Build.props](../Directory.Build.props), and each project's checked-in
`packages.lock.json` pins its resolved dependency versions. After any dependency
update, regenerate the lock files for the solution:

```sh
dotnet restore LibGit2CS.slnx --force-evaluate
```

Review and include the updated lock files with the dependency changes, then
build and test the solution using the commands above.

Tests use xUnit v3 on Microsoft Testing Platform. For a unit-only run:

```sh
dotnet test --project tests/LibGit2CS.UnitTests/LibGit2CS.UnitTests.csproj --no-build
```

Integration tests need Git and, for container tests, Docker with Linux containers.
Host SSH-agent tests also need `ssh-agent` and `ssh-add`. Allow time for image
builds and startup; report skipped tests separately from successful validation.
The full development checkout includes tests and fixture generators; packaged
source contains the library and its build inputs, not the test suite.

## Continuous integration and coverage

[CI](../.github/workflows/ci.yml) runs on pull requests, pushes to `main`, and
manual dispatch. It builds Release and runs both test suites on Ubuntu 26.04
(x64), Windows Server 2025 (x64), and macOS 15 (ARM64). Linux also verifies
formatting and runs the Python coverage-checker regression tests.

Linux requires a working Linux Docker daemon, `/var/run/docker.sock`, Git,
`ssh-agent`, and `ssh-add`, so container and host-agent tests can execute.
Windows and macOS run the non-Docker integration tests; container-dependent
tests use their existing skip gates. Skipped tests are reported separately.

Each OS uploads its TRX test results and two Cobertura coverage reports in a
`linux-test-results`, `windows-test-results`, or `macos-test-results` artifact.
[Test Report](../.github/workflows/test-report.yml) uses trusted default-branch
tools to summarize each OS and merge coverage from all six reports. Missing,
malformed, empty, or unexpected-assembly inputs fail the coverage check.
The combined `LibGit2CS` coverage must meet **85% lines and 80% branches**,
using exact counts rather than rounded display percentages. No new coverage
exclusions are applied. Reports and artifacts are retained for 14 days.

The initial Linux baseline at commit `ce85399` was 30,843/35,090 covered lines
(87.90%) and 17,796/22,093 covered branches (80.55%), with 4,675 unit and 758
integration tests passing and no skips. Cross-platform coverage is the union
of the six inputs; percentages are not averaged across suites or OSes.

After these workflows have run on the default branch, configure branch
protection or a ruleset to require `linux-tests`, `windows-tests`, `macos-tests`,
and `Coverage`. The reporting workflow publishes `Coverage` on the tested
commit. A `workflow_run` workflow only runs once it exists on the default
branch; coverage reporting will become available after the initial merge.
The workflow files do not configure repository branch-protection settings.

To reproduce Linux coverage locally, use a fresh results directory:

```sh
dotnet restore LibGit2CS.slnx --locked-mode
dotnet build LibGit2CS.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test --solution LibGit2CS.slnx -c Release --no-build --no-artifact-post-processing \
  --results-directory ./artifacts/coverage/local -- \
  --coverlet --coverlet-output-format cobertura --coverlet-include '[LibGit2CS]*' --report-xunit-trx
dotnet tool restore
dotnet reportgenerator \
  '-reports:./artifacts/coverage/local/*.cobertura.*.xml' \
  '-targetdir:./artifacts/coverage/local/report' \
  '-assemblyfilters:+LibGit2CS' '-reporttypes:Html;MarkdownSummary;Cobertura'
python3 .github/scripts/check_coverage.py artifacts/coverage/local/report \
  --line-threshold 85 --branch-threshold 80
python3 -m unittest discover -s .github/scripts -p 'test_*.py'
```

Local coverage excludes platform-specific execution available only on the
other OSes; the required CI gate uses the combined cross-platform report.

## Build a package

```sh
dotnet pack source/LibGit2CS/LibGit2CS.csproj -c Release -o artifacts/packages
```

The package includes the gallery README, XML API documentation, license notices,
and corresponding library source and guides under `src/`. The repository README
and NuGet README serve different audiences; keep their quickstarts consistent.

## Publishing a release

[Publish NuGet](../.github/workflows/publish.yml) runs when a GitHub release is
published. It re-verifies the released commit on Ubuntu 26.04: locked restore,
formatting, Release build, the coverage-checker regression tests, integration
prerequisites, both test suites with coverage, and the **85% line and 80% branch**
gate on the merged Linux report. It then packs the library with symbols and
validates the release packages before uploading them:

- exactly one `.nupkg` and one `.snupkg`,
- release tag of the form `vX.Y.Z` with an optional prerelease suffix,
- the tag version equals the packed NuGet package version,
- the package repository metadata records the released commit, and
- the package and symbols contain the `lib/net11.0` binary and PDB payloads.

The `publish` job pushes the validated package and symbols to nuget.org using
trusted publishing.

The release tag must equal the computed package version. While `version.json`
says `0.1-dev`, every commit computes `0.1.<height>-dev`, and the matching tag is
what `dotnet nbgv tag --what-if` prints (for example `v0.1.573-dev`). Create the
release from that commit rather than inventing a tag: a tag whose version
differs from `dotnet nbgv get-version -f json` (`NuGetPackageVersion`) fails
package validation instead of publishing a mismatched package. Tag builds
resolve as public releases because `version.json` lists the tag pattern in
`publicReleaseRefSpec`; without it the package version would gain a `-g<hash>`
suffix. To ship a stable version, set `version.json` to `0.1.0` (or use
`dotnet nbgv prepare-release`) and tag the commit that builds it.

Publishing requires one-time repository setup:

- a `nuget` environment on the repository (add required reviewers if desired),
- a repository variable `NUGET_USER` naming the nuget.org account, and
- a nuget.org trusted publisher for the GitHub repository and the `nuget`
  environment.

The `build` job validates packages even before that setup is complete, but
`publish` cannot push them. Like the other workflows, it only runs from the
default branch's workflow files.

## Building the packaged source

1. Extract the .nupkg as a ZIP archive and change directory to src/.
2. Install the SDK selected by global.json.
3. Run, replacing PACKAGE_VERSION with the version in LibGit2CS.nuspec:

   ```sh
   dotnet build source/LibGit2CS/LibGit2CS.csproj -c Release -p:NBGV_Disabled=true -p:Version=PACKAGE_VERSION
   ```

Network access to NuGet is needed to restore the dependencies. NBGV_Disabled
turns off Git-history version calculation, because the extracted source does
not contain .git. The explicit Version preserves the package's version input;
this command is a source rebuild, not a promise of byte-identical binaries.

The full development repository, including tests and fixture generators, is
https://github.com/MM-YJN/LibGit2CS
The package's repository metadata identifies the commit when available.
For this package, the bundled source is authoritative, including any changes
that were not committed when packing. Release packages should be built from a
clean, tagged checkout. Do not use --no-build to create release packages after
changing source: the source payload must correspond to the binary being shipped.

## Integration-test Docker images

SSH and git-daemon images are prepared only when a test needs them and cached
locally across runs. Each test still gets a fresh container and seeded repository.
Image tags include a hash of the Dockerfile, Testcontainers version, and cache
format; changing an input selects a new image. Unused fixtures do no Docker work.

Set `LIBGIT2CS_TEST_IMAGE_REFRESH=1` when running integration tests to rebuild used
images without layer caching and pull their base images. SSH host keys are generated
at image build time and remain the same until the image is rebuilt.

List cached images with:

```sh
docker image ls --filter label=org.libgit2cs.integration-image=true
```

To remove unused cached test images manually (when no test run is active):

```sh
docker image prune -a --filter label=org.libgit2cs.integration-image=true
```

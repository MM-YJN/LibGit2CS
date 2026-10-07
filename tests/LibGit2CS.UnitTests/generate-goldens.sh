#!/usr/bin/env bash
# Regenerates the golden fixture corpus from a libgit2 checkout.
#
# Usage: ./generate-goldens.sh [path-to-libgit2]
# Default libgit2 path: ~/repo/libgit2
#
# Fixture sources:
#   config/  — tests/libgit2/config/, tests/resources/config*
#   pack/    — tests/resources/{*.pack,*big.index,empty_bare.git,...}
#   odb/     — empty_standard_repo and empty_bare.git object trees
#   repo/    — .git trees for Open/Discover tests
#   delta/   — inline deltas from tests/libgit2/delta/
#   grafts/  — graft*.git or synthesized .git/info/grafts
#   objects/ — inline object bytes from tests/libgit2/{object,commit}/
#   refs/    — ref layouts from tests/resources/*.git
#
# Idempotent: re-running overwrites in place.

set -euo pipefail

# Never capture the generator operator's identity in commits or reflogs.
export GIT_AUTHOR_NAME="Test"
export GIT_AUTHOR_EMAIL="test@example.com"
export GIT_COMMITTER_NAME="Test"
export GIT_COMMITTER_EMAIL="test@example.com"

LIBGIT2="${1:-$HOME/repo/libgit2}"

if [[ ! -d "$LIBGIT2/tests/resources" ]]; then
    echo "error: libgit2 checkout not found at $LIBGIT2" >&2
    echo "usage: $0 [path-to-libgit2]" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIXTURES="$SCRIPT_DIR/Fixtures"

# TODO (GitDateParser): extract inline date strings from
#   $LIBGIT2/tests/libgit2/date/date.c::cl_assert(...) into
#   $FIXTURES/date/*.txt as (input, expected-seconds, expected-offset) tuples.
#   Only offset_parse + object_header_date are ported; skip approxidate vectors.

# TODO (PathHelpers): extract path vectors from
#   $LIBGIT2/tests/libgit2/path/*.c into $FIXTURES/path/*.txt.
#   Only extract git_fs_path_* vectors; HFS/NTFS validation is covered by
#   PathValidatorTests rather than fixture vectors.

# TODO (Hashing/GitOid): no fixture extraction; round-trip tests use
#   compiled-in known vectors. SHA-1 empty blob/tree from libgit2 oid.c;
#   SHA-256 computed at runtime via HashAlgorithmKind dispatch.

# TODO (FileConfigBackend): copy
#   $LIBGIT2/tests/resources/config* into $FIXTURES/config/.
mkdir -p "$FIXTURES/config"
cp -t "$FIXTURES/config" \
    "$LIBGIT2/tests/resources/config/"config0 \
    "$LIBGIT2/tests/resources/config/"config1 \
    "$LIBGIT2/tests/resources/config/"config2 \
    "$LIBGIT2/tests/resources/config/"config3 \
    "$LIBGIT2/tests/resources/config/"config4 \
    "$LIBGIT2/tests/resources/config/"config5 \
    "$LIBGIT2/tests/resources/config/"config6 \
    "$LIBGIT2/tests/resources/config/"config7 \
    "$LIBGIT2/tests/resources/config/"config8 \
    "$LIBGIT2/tests/resources/config/"config9 \
    "$LIBGIT2/tests/resources/config/"config10 \
    "$LIBGIT2/tests/resources/config/"config11 \
    "$LIBGIT2/tests/resources/config/"config12 \
    "$LIBGIT2/tests/resources/config/"config13 \
    "$LIBGIT2/tests/resources/config/"config14 \
    "$LIBGIT2/tests/resources/config/"config15 \
    "$LIBGIT2/tests/resources/config/"config16 \
    "$LIBGIT2/tests/resources/config/"config17 \
    "$LIBGIT2/tests/resources/config/"config18 \
    "$LIBGIT2/tests/resources/config/"config19 \
    "$LIBGIT2/tests/resources/config/"config20 \
    "$LIBGIT2/tests/resources/config/"config21 \
    "$LIBGIT2/tests/resources/config/"config22 \
    "$LIBGIT2/tests/resources/config/"config-include \
    "$LIBGIT2/tests/resources/config/"config-included \
    "$LIBGIT2/tests/resources/config/"config-nosection \
    "$LIBGIT2/tests/resources/config/"config-oom \
    "$LIBGIT2/tests/resources/config/".gitconfig
# Rich multi-section repo config for parser coverage.
cp "$LIBGIT2/tests/resources/testrepo.git/config" "$FIXTURES/config/testrepo_gitconfig"
# Leading-dot filename breaks MSBuild EmbeddedResource globbing; rename.
mv "$FIXTURES/config/.gitconfig" "$FIXTURES/config/dotgitconfig"

# TODO (PackIndex): copy
#   $LIBGIT2/tests/resources/big.index and git-sha256.index into
#   $FIXTURES/pack/.

# TODO (PackFile): copy packfile-sha256 pack+idx pair, plus any
#   testrepo.git/*.pack into $FIXTURES/pack/.

# TODO (LooseObjectBackend): zip empty_standard_repo and
#   empty_bare.git object trees into $FIXTURES/odb/*.zip (extracted at runtime
#   via FixtureLoader.ExtractTreeToTemp).

# TODO (Repository): zip .git fixture trees for Open/Discover
#   tests into $FIXTURES/repo/*.zip.

# TODO (DeltaApplier): extract inline delta streams from
#   $LIBGIT2/tests/libgit2/delta/*.c into $FIXTURES/delta/*.bin.

# TODO (Grafts): copy $LIBGIT2/tests/resources/graft*.git or
#   synthesize .git/info/grafts fixtures into $FIXTURES/grafts/.

# TODO (objects/commits): extract inline object bytes from
#   $LIBGIT2/tests/libgit2/{object,commit}/*.c into $FIXTURES/objects/*.{bin,txt}.

# TODO (References): zip ref layouts from
#   $LIBGIT2/tests/resources/*.git into $FIXTURES/refs/*.zip.

echo "golden fixture skeleton at $FIXTURES (populate per section)"

# ============================================================
# Diff core golden fixtures
# ============================================================
# Packages the libgit2 test repos used by diff tests into zip fixtures
# and generates byte-exact golden output via the reference `git`.
#
# Reference git: $(git --version) at generation time. Golden output can
# drift across versions (rename %similarity rounding, --stat column math,
# abbrev defaults); re-run with the same version for reproducibility.
#
# Fixture packaging mirrors libgit2's cl_git_sandbox_init: each repo is
# stored under tests/resources/<name>/ with the git dir named ".gitted"
# (because git would treat a real .git as a submodule). We rename
# .gitted -> .git and gitattributes -> .gitattributes inside the zip so
# C# tests can open the extracted working-tree root directly via GitRepository.OpenAsync.
#
# Golden output is the exact stdout of `git diff ...` on the fixture
# repo. C# golden tests compare the GitDiff.PrintAsync byte tier /
# GitDiffStats.Format output byte-for-byte against these files.
# ============================================================

DIFF="$FIXTURES/diff"
DIFF_EXPECTED="$DIFF/expected"
mkdir -p "$DIFF_EXPECTED"

# package_fixture NAME — copies tests/resources/<name> to a staging dir,
# renames .gitted -> .git + gitattributes/gitignore dotfiles, zips to
# Fixtures/diff/<name>.zip. Idempotent (overwrites).
diff_package_fixture() {
    local name="$1"
    local src="$LIBGIT2/tests/resources/$name"
    if [[ ! -d "$src" ]]; then
        echo "diff: skip $name (not found at $src)" >&2
        return 0
    fi
    local stage; stage="$(mktemp -d)"
    cp -r "$src" "$stage/"
    [[ -d "$stage/$name/.gitted" ]] && mv "$stage/$name/.gitted" "$stage/$name/.git"
    [[ -f "$stage/$name/gitattributes" ]] && mv "$stage/$name/gitattributes" "$stage/$name/.gitattributes"
    [[ -f "$stage/$name/gitignore" ]] && mv "$stage/$name/gitignore" "$stage/$name/.gitignore"
    rm -f "$DIFF/$name.zip"
    ( cd "$stage" && zip -qr "$DIFF/$name.zip" "$name" )
    rm -rf "$stage"
    echo "diff: packaged $name.zip"
}

# gen_golden FIXTURE OUTFILE -- <git-diff-args...>
# Extracts the packaged zip to a temp repo, runs `git diff` with the
# given args (core.autocrlf=false to avoid host CRLF leakage), captures
# stdout to Fixtures/diff/expected/<OUTFILE>. The "--" separator delimits
# the fixture/outfile from the git args.
diff_gen_golden() {
    local fixture="$1"; shift
    local outfile="$1"; shift
    # Optional literal "--" separator (for readability only).
    [[ "${1:-}" == "--" ]] && shift
    local staged; staged="$(mktemp -d)"
    ( cd "$staged" && unzip -q "$DIFF/$fixture.zip" )
    git -C "$staged/$fixture" -c core.autocrlf=false "$@" \
        > "$DIFF_EXPECTED/$outfile"
    rm -rf "$staged"
    echo "diff: golden $outfile ($(wc -l < "$DIFF_EXPECTED/$outfile") lines)"
}

# --- Package the fixtures used by the diff golden tests ---
diff_package_fixture attr
diff_package_fixture renames
diff_package_fixture status
diff_package_fixture diff
diff_package_fixture diff_format_email
diff_package_fixture submodules

# === Tree-to-tree goldens (attr fixture) — port diff/tree.c ===
# All use --unified / --inter-hunk-context to match the clar DiffOptions.
# tree.c test_0: a=605812a b=370fe9e c=f5b0af1 (context=1 interhunk=1)
diff_gen_golden attr tree_attr_605812a_to_370fe9e_ctx1.txt -- \
    diff -U1 --inter-hunk-context=1 605812ab7fe421fdd325a935d35cb06a9234a7d7 370fe9ec224ce33e71f9e5ec2bd1142ce9937a6a
diff_gen_golden attr tree_attr_f5b0af1_to_370fe9e_ctx1.txt -- \
    diff -U1 --inter-hunk-context=1 f5b0af1fb4f5c0cd7aad880711d368a07333c307 370fe9ec224ce33e71f9e5ec2bd1142ce9937a6a
# tree.c options: a=6bab5c7 b=605812a (context=3); normal / ignore-ws / reverse
diff_gen_golden attr tree_attr_6bab5c7_to_605812a_ctx3.txt -- \
    diff -U3 --inter-hunk-context=0 6bab5c79cd5140d0f800917f550eb2a3dc32b0da 605812ab7fe421fdd325a935d35cb06a9234a7d7
diff_gen_golden attr tree_attr_6bab5c7_to_605812a_ctx3_ignorews.txt -- \
    diff -U3 --inter-hunk-context=0 --ignore-all-space 6bab5c79cd5140d0f800917f550eb2a3dc32b0da 605812ab7fe421fdd325a935d35cb06a9234a7d7
diff_gen_golden attr tree_attr_6bab5c7_to_605812a_ctx3_reverse.txt -- \
    diff -U3 --inter-hunk-context=0 -R 6bab5c79cd5140d0f800917f550eb2a3dc32b0da 605812ab7fe421fdd325a935d35cb06a9234a7d7

# === Stats goldens (diff_format_email fixture) — port diff/stats.c ===
# clar uses git_diff__commit (commit vs first parent); git equivalent is
# `git diff <parent> <commit>`. Stats formats match DiffStatsFormat flags.
# 9264b96 "Modify some content" (parent fca0c10): 1 file, 5 ins, 3 del.
diff_gen_golden diff_format_email stat_9264b96_full.txt -- \
    diff --stat fca0c10eb9f1af6494a448d5733d283f5232a514 9264b96c6d104d0e07ae33d3007b6a48246c6f92
diff_gen_golden diff_format_email stat_9264b96_short.txt -- \
    diff --shortstat fca0c10eb9f1af6494a448d5733d283f5232a514 9264b96c6d104d0e07ae33d3007b6a48246c6f92
# cd471f0 "Multi-hunk changes" (parent 77d0a3e): file2 + file3.
diff_gen_golden diff_format_email stat_cd471f0_full.txt -- \
    diff --stat 77d0a3ed37236a7941d564f08d68d3b36462d231 cd471f0d8770371e1bc78bcbb38db4c7e4106bd2
# stat_cd471f0_num.txt is HAND-CURATED, not regenerated from `git diff --numstat`.
# libgit2's git_diff_stats_to_buf(GIT_DIFF_STATS_NUMBER) emits "%-8" space-padded
# fields (diff_stats.c:147-150), which intentionally diverges from porcelain
# `git diff --numstat` (TAB-separated). The golden matches clar
# tests/libgit2/diff/stats.c::numstat (hardcoded "3       2       file2.txt\n...").
# Regenerating via `git --numstat` would reintroduce tabs and break the test.

# === Binary goldens (diff_format_email fixture) — port diff/binary.c ===
# add_normal: 873806f -> 897d3af adds binary.bin. Default (no --binary)
# emits "Binary files /dev/null and b/binary.bin differ".
diff_gen_golden diff_format_email binary_add_normal.txt -- \
    diff 873806f6f27e631eb0b23e4b56bea2bfac14a373 897d3af16ca9e420cd071b1c4541bd2b91d04c8c

# === Rename goldens (renames fixture) — port diff/rename.c ===
# autocrlf=false is set by rename.c initialize(). -M enables rename
# detection (off by default in `git diff`); matches GitDiffFindFlags.Renames.
# 31e47d8 -> 2bc7f35: serving.txt -> sixserving.txt (100% rename).
diff_gen_golden renames rename_31e47d8_to_2bc7f35_M.txt -- \
    diff -M 31e47d8c1fa36d7f8d537b96158e3f024de0a9f2 2bc7f351d20b53f1c72c16c4b036e491c478c49a
# Nominal (no rename detection): --no-renames is required because modern git
# defaults diff.renames=true, so plain `git diff` already detects renames.
diff_gen_golden renames rename_31e47d8_to_2bc7f35_nominal.txt -- \
    diff --no-renames 31e47d8c1fa36d7f8d537b96158e3f024de0a9f2 2bc7f351d20b53f1c72c16c4b036e491c478c49a

# === Gitlink (submodule reference) golden (submodules fixture) ===
# Tree-to-tree add of a gitlink (mode 0160000). This is NOT a divergence for
# the tree-to-tree path (both git and LibGit2CS report it as Added with mode
# 160000); the remaining workdir-submodule divergence only affects *workdir*
# submodule status, not tree-to-tree gitlink entries. Verified byte-exact.
diff_gen_golden submodules gitlink_add_testrepo.txt -- \
    diff 09176a980273d801a3e37cc45c84af1366501ed9 97896810b3210244a62a82458b8e0819ecfc6850

# === Workdir goldens (status fixture) — port diff/workdir.c ===# index-to-workdir (`git diff` with no revs) and tree-to-workdir (`git diff HEAD`).
# The status fixture ships with workdir modifications baked in (modified_file,
# staged_changes, etc.), so no runtime file mutation is needed.
diff_gen_golden status workdir_index_to_workdir.txt -- \
    diff --unified=3
diff_gen_golden status workdir_tree_to_workdir.txt -- \
    diff --unified=3 HEAD

echo "diff: golden corpus at $DIFF_EXPECTED ($(ls -1 "$DIFF_EXPECTED" | wc -l) files)"

# ============================================================
# Blame golden fixtures
# ============================================================
# Packages the libgit2 test repos used by blame + mailmap tests into zip
# fixtures and generates byte-exact golden output via the reference `git`.
#
# Fixture repos:
#   blametest.git — bare repo with blame test history (a/b/c/file/huge.txt)
#   testrepo      — working-tree repo with .gitted (branch_file.txt)
#   mailmap       — working-tree repo with .mailmap + file.txt
# ============================================================

BLAME="$FIXTURES/blame"
BLAME_EXPECTED="$BLAME/expected"
mkdir -p "$BLAME_EXPECTED"

# package_fixture NAME — copies tests/resources/<name> to a staging dir,
# renames .gitted -> .git + dotfiles, zips to Fixtures/blame/<name>.zip.
blame_package_fixture() {
    local name="$1"
    local src="$LIBGIT2/tests/resources/$name"
    if [[ ! -d "$src" ]]; then
        echo "blame: skip $name (not found at $src)" >&2
        return 0
    fi
    local stage; stage="$(mktemp -d)"
    cp -r "$src" "$stage/"
    [[ -d "$stage/$name/.gitted" ]] && mv "$stage/$name/.gitted" "$stage/$name/.git"
    [[ -f "$stage/$name/gitattributes" ]] && mv "$stage/$name/gitattributes" "$stage/$name/.gitattributes"
    [[ -f "$stage/$name/gitignore" ]] && mv "$stage/$name/gitignore" "$stage/$name/.gitignore"
    rm -f "$BLAME/$name.zip"
    ( cd "$stage" && zip -qr "$BLAME/$name.zip" "$name" )
    rm -rf "$stage"
    echo "blame: packaged $name.zip"
}

# package_bare_fixture NAME — for bare repos (e.g. blametest.git), the
# directory IS the git dir. We wrap it in a parent directory so extraction
# produces <name>/ (the bare git dir) which can be opened via GitRepository.OpenBareAsync.
blame_package_bare_fixture() {
    local name="$1"
    local src="$LIBGIT2/tests/resources/$name"
    if [[ ! -d "$src" ]]; then
        echo "blame: skip $name (not found at $src)" >&2
        return 0
    fi
    local stage; stage="$(mktemp -d)"
    mkdir -p "$stage/$name"
    cp -r "$src/." "$stage/$name/"
    rm -f "$BLAME/$name.zip"
    ( cd "$stage" && zip -qr "$BLAME/$name.zip" "$name" )
    rm -rf "$stage"
    echo "blame: packaged bare $name.zip"
}

# gen_golden FIXTURE OUTFILE -- <git-blame-args...>
blame_gen_golden() {
    local fixture="$1"; shift
    local outfile="$1"; shift
    [[ "${1:-}" == "--" ]] && shift
    local staged; staged="$(mktemp -d)"
    ( cd "$staged" && unzip -q "$BLAME/$fixture.zip" )
    git -C "$staged/$fixture" -c core.autocrlf=false "$@" \
        > "$BLAME_EXPECTED/$outfile" 2>/dev/null || true
    rm -rf "$staged"
    echo "blame: golden $outfile ($(wc -l < "$BLAME_EXPECTED/$outfile") lines)"
}

# --- Package the fixtures ---
blame_package_bare_fixture blametest.git
blame_package_fixture testrepo
blame_package_bare_fixture testrepo.git
blame_package_fixture mailmap

echo "blame: golden corpus at $BLAME_EXPECTED ($(ls -1 "$BLAME_EXPECTED" 2>/dev/null | wc -l) files)"

# ============================================================
# Patch parse/apply + email golden fixtures
# ============================================================
# Packages the libgit2 test repos used by apply tests into zip fixtures.
# The merge-recursive fixture is used by apply/check.c + apply/tree.c.
# We bake a `git reset --hard 539bd01` into the zip so the workdir+index
# match commit 539bd01's tree (check.c::generate_diff does this at runtime;
# index-write is unavailable at packaging time, so the reset is baked in
# once — idempotent).
# ============================================================

APPLY="$FIXTURES/apply"
mkdir -p "$APPLY"

# apply_package_fixture NAME [RESET_OID] — copies tests/resources/<name>
# to a staging dir, renames .gitted -> .git + dotfiles, optionally does a
# `git reset --hard <oid>` to align workdir+index with a commit, then zips
# to Fixtures/apply/<name>.zip. Idempotent (overwrites).
apply_package_fixture() {
    local name="$1"
    local reset_oid="${2:-}"
    local src="$LIBGIT2/tests/resources/$name"
    if [[ ! -d "$src" ]]; then
        echo "apply: skip $name (not found at $src)" >&2
        return 0
    fi
    local stage; stage="$(mktemp -d)"
    cp -r "$src" "$stage/"
    [[ -d "$stage/$name/.gitted" ]] && mv "$stage/$name/.gitted" "$stage/$name/.git"
    [[ -f "$stage/$name/gitattributes" ]] && mv "$stage/$name/gitattributes" "$stage/$name/.gitattributes"
    [[ -f "$stage/$name/gitignore" ]] && mv "$stage/$name/gitignore" "$stage/$name/.gitignore"
    # Bake in the reset so workdir+index match the target commit (check.c
    # does git reset --hard at test initialize time; index-write is
    # unavailable at packaging time, so do it once here).
    if [[ -n "$reset_oid" ]]; then
        git -C "$stage/$name" reset --hard "$reset_oid" >/dev/null 2>&1 || true
    fi
    rm -f "$APPLY/$name.zip"
    ( cd "$stage" && zip -qr "$APPLY/$name.zip" "$name" )
    rm -rf "$stage"
    echo "apply: packaged $name.zip"
}

# merge-recursive: used by apply/check.c + apply/tree.c. check.c::generate_diff
# does `git reset --hard 539bd01` to align workdir+index with that commit.
apply_package_fixture merge-recursive 539bd011c4822c560c1d17cab095006b7a10f707

echo "apply: fixtures at $APPLY ($(ls -1 "$APPLY" 2>/dev/null | wc -l) files)"

# ============================================================
# Message/Trailer golden fixtures
# ============================================================
# Generates byte-exact golden output for GitMessage.Prettify (vs
# `git stripspace`) and GitTrailers.Parse (vs `git interpret-trailers
# --parse`). No repo dependency — pure string I/O.
#
# Reference git: $(git --version) at generation time. `git stripspace`
# and `git interpret-trailers` output is stable across versions (the
# algorithms haven't changed in years).
# ============================================================

MESSAGE="$FIXTURES/message"
MESSAGE_EXPECTED="$MESSAGE/expected"
mkdir -p "$MESSAGE_EXPECTED"

# message_prettify CASE_NAME INPUT -- produces expected output via
# `git stripspace` (no comment stripping) or `git stripspace -s` (with
# comment stripping, default char '#'). The INPUT is written to a temp
# file and piped through stripspace.
message_prettify() {
    local case_name="$1"
    local input="$2"
    local strip_comments="${3:-}"
    if [[ -n "$strip_comments" ]]; then
        printf '%s' "$input" | git stripspace -s > "$MESSAGE_EXPECTED/$case_name.txt"
    else
        printf '%s' "$input" | git stripspace > "$MESSAGE_EXPECTED/$case_name.txt"
    fi
    echo "message: prettify $case_name ($(wc -l < "$MESSAGE_EXPECTED/$case_name.txt") lines)"
}

# message_trailers CASE_NAME INPUT -- produces expected trailer
# output via `git interpret-trailers --parse`. One "Key: Value" per line.
message_trailers() {
    local case_name="$1"
    local input="$2"
    printf '%s' "$input" | git interpret-trailers --parse > "$MESSAGE_EXPECTED/$case_name.txt"
    echo "message: trailers $case_name ($(wc -l < "$MESSAGE_EXPECTED/$case_name.txt") lines)"
}

# --- Prettify golden cases (mirror MessageTests scenarios) ---

# Clean message — no changes needed.
message_prettify prettify_clean \
    "Subject line

Body paragraph here.
"

# Trailing whitespace + blank-line collapse.
message_prettify prettify_trailing_ws \
    "hello   

world

"

# Multiple consecutive blank lines collapse to one.
message_prettify prettify_collapse_blanks \
    "first



second
"

# Comment stripping with '#'.
message_prettify prettify_strip_comments \
    "subject

body
# this is a comment
more body
# another comment
" \
    strip-comments

# Only blank lines → empty output.
message_prettify prettify_only_blanks ""


# --- Trailer golden cases (mirror TrailerTests scenarios) ---

# Simple signed-off-by block.
message_trailers trailers_simple \
    "Message

Signed-off-by: foo@bar.com
Signed-off-by: someone@else.com
"

# No whitespace around colon.
message_trailers trailers_no_whitespace \
    "Message

Key:value
"

# Extra whitespace around colon.
message_trailers trailers_extra_whitespace \
    "Message

Key   :   value
"

# No trailing newline.
message_trailers trailers_no_newline \
    "Message

Key: value"

# Not a trailer block (trailer not in last paragraph).
message_trailers trailers_not_last_paragraph \
    "Message

Key: value

More stuff
"

# Multiple trailers of different types.
message_trailers trailers_multiple_types \
    "Fix a bug

This fixes the issue reported in #42.

Signed-off-by: Alice <alice@example.com>
Reviewed-by: Bob <bob@example.com>
Acked-by: Carol <carol@example.com>
"

echo "message: golden corpus at $MESSAGE_EXPECTED ($(ls -1 "$MESSAGE_EXPECTED" | wc -l) files)"

# ============================================================
# Merge golden fixtures
# ============================================================
# Generates byte-exact golden output for GitRepository.MergeTreesAsync (vs
# `git merge-tree --write-tree`), GitRepository.MergeBaseFindAsync (vs `git merge-base`),
# and GitRepository.MergeCommitsAsync (vs `git merge` with pinned dates).
#
# Tree-level goldens use `git merge-tree --write-tree --name-only`:
#   - Clean merge: line 1 = merged tree OID; exit 0
#   - Conflict: line 1 = merged tree OID, then conflict messages; exit 1
# The merged tree OID alone is the golden for clean merges; for
# conflicts, the C# test compares the Index entries (stages 1/2/3) via
# `git ls-files -s` from a real `git merge` on the same branches.
#
# Commit-level goldens use `git merge --no-ff --no-commit` with pinned
# GIT_AUTHOR_DATE/GIT_COMMITTER_DATE for deterministic results, then
# `git ls-files -s` to capture the index state.
#
# Determinism env vars (commit-level only):
#   GIT_AUTHOR_DATE=2000-01-01T00:00:00
#   GIT_COMMITTER_DATE=2000-01-01T00:00:00
#   GIT_AUTHOR_NAME=Test Author
#   GIT_AUTHOR_EMAIL=test@example.com
#   GIT_COMMITTER_NAME=Test Author
#   GIT_COMMITTER_EMAIL=test@example.com
# ============================================================

MERGE="$FIXTURES/merge"
MERGE_EXPECTED="$MERGE/expected"
mkdir -p "$MERGE_EXPECTED"

# The merge-recursive fixture is already packaged at
# Fixtures/merge/merge-recursive.zip. We extract it to a temp dir and
# run reference git commands against it.
MERGE_REPO="merge-recursive"
MERGE_ZIP="$MERGE/$MERGE_REPO.zip"

if [[ ! -f "$MERGE_ZIP" ]]; then
    echo "merge: skip — $MERGE_ZIP not found (run the apply/blame sections first)" >&2
else
    # Extract the fixture once for all merge goldens.
    MERGE_STAGE="$(mktemp -d)"
    ( cd "$MERGE_STAGE" && unzip -q "$MERGE_ZIP" )
    MERGE_GITDIR="$MERGE_STAGE/$MERGE_REPO"

    # merge_tree CASE_NAME OURS THEIRS — tree-level merge golden.
    # Uses --merge-base to pin the base to the same single base that
    # GitRepository.MergeBaseFindAsync returns (the first merge-base). This makes the
    # comparison fair: both C# (GitRepository.MergeTreesAsync with one base) and git
    # (merge-tree --write-tree --merge-base=<base>) use the same single
    # base, avoiding the recursive-vs-single-base divergence on
    # criss-cross histories.
    merge_tree() {
        local case_name="$1"
        local ours="$2"
        local theirs="$3"
        local base; base=$(git -C "$MERGE_GITDIR" merge-base "$ours" "$theirs" 2>/dev/null | head -1)
        git -C "$MERGE_GITDIR" merge-tree --write-tree --name-only \
            --merge-base="$base" "$ours" "$theirs" \
            > "$MERGE_EXPECTED/$case_name.txt" 2>&1 || true
        echo "merge: tree $case_name ($(wc -l < "$MERGE_EXPECTED/$case_name.txt") lines)"
    }

    # merge_tree_stages CASE_NAME OURS THEIRS — tree-level conflict
    # stage golden. Uses `git read-tree -m <base> <ours> <theirs>` to stage
    # the three-way merge in the index (without a workdir checkout), then
    # `git ls-files -s` to capture the exact stage entries (0=merged,
    # 1=base, 2=ours, 3=theirs). This matches the C# GitRepository.MergeTreesAsync output
    # which uses the same single base.
    merge_tree_stages() {
        local case_name="$1"
        local ours="$2"
        local theirs="$3"
        local base; base=$(git -C "$MERGE_GITDIR" merge-base "$ours" "$theirs" 2>/dev/null | head -1)
        local work; work="$(mktemp -d)"
        cp -r "$MERGE_GITDIR" "$work/repo"
        git -C "$work/repo" checkout -q "$ours" 2>/dev/null
        git -C "$work/repo" read-tree -m "$base" "$ours" "$theirs" 2>/dev/null
        git -C "$work/repo" ls-files -s > "$MERGE_EXPECTED/$case_name.txt" 2>&1
        rm -rf "$work"
        echo "merge: tree-stages $case_name ($(wc -l < "$MERGE_EXPECTED/$case_name.txt") lines)"
    }

    # merge_base CASE_NAME A B — merge-base golden.
    # Single OID line from `git merge-base`.
    merge_base() {
        local case_name="$1"
        local a="$2"
        local b="$3"
        git -C "$MERGE_GITDIR" merge-base "$a" "$b" \
            > "$MERGE_EXPECTED/$case_name.txt" 2>&1 || true
        echo "merge: base $case_name ($(wc -l < "$MERGE_EXPECTED/$case_name.txt") lines)"
    }

    # merge_commit CASE_NAME OURS THEIRS — commit-level merge
    # golden. Uses pinned dates for deterministic results. Captures
    # `git ls-files -s` (index state with stages).
    merge_commit() {
        local case_name="$1"
        local ours="$2"
        local theirs="$3"
        local work; work="$(mktemp -d)"
        cp -r "$MERGE_GITDIR" "$work/repo"
        git -C "$work/repo" checkout -q "$ours" 2>/dev/null
        # Pin dates for deterministic merge results.
        GIT_AUTHOR_DATE=2000-01-01T00:00:00 \
        GIT_COMMITTER_DATE=2000-01-01T00:00:00 \
        GIT_AUTHOR_NAME="Test Author" \
        GIT_AUTHOR_EMAIL="test@example.com" \
        GIT_COMMITTER_NAME="Test Author" \
        GIT_COMMITTER_EMAIL="test@example.com" \
            git -C "$work/repo" merge --no-ff --no-commit "$theirs" 2>/dev/null || true
        # Capture the index state (ls-files -s shows stage info).
        git -C "$work/repo" ls-files -s > "$MERGE_EXPECTED/$case_name.txt" 2>&1
        rm -rf "$work"
        echo "merge: commit $case_name ($(wc -l < "$MERGE_EXPECTED/$case_name.txt") lines)"
    }

    # --- Tree-level merge goldens (single-base, matching MergeTreesAsync) ---
    # branchA-1 → branchA-2: clean merge with single base.
    merge_tree merge_tree_branchA_clean branchA-1 branchA-2
    # branchB-1 → branchB-2: conflicts with single base (recursive merge
    # would be clean, but MergeTreesAsync uses a single base).
    merge_tree merge_tree_branchB_conflict branchB-1 branchB-2
    # branchC-1 → branchC-2: conflicts with single base (criss-cross;
    # recursive merge is clean — see commit-level golden).
    merge_tree merge_tree_branchC_conflict branchC-1 branchC-2

    # --- Tree-level conflict stage goldens (git read-tree -m, single base) ---
    # These capture the exact index stages (0/1/2/3) from a three-way
    # read-tree with the same single base that MergeBaseFindAsync returns.
    # C# MergeTreesAsync conflict entries are compared against these.
    merge_tree_stages merge_tree_stages_branchB_conflict branchB-1 branchB-2
    merge_tree_stages merge_tree_stages_branchC_conflict branchC-1 branchC-2
    merge_tree_stages merge_tree_stages_branchF_conflict branchF-1 branchF-2
    merge_tree_stages merge_tree_stages_branchH_conflict branchH-1 branchH-2
    merge_tree_stages merge_tree_stages_branchJ_conflict branchJ-1 branchJ-2

    # --- Tree-level merge goldens (conflict merges, tree OID + messages) ---
    # branchF-1 → branchF-2: content conflict in veal.txt.
    merge_tree merge_tree_branchF_conflict branchF-1 branchF-2
    # branchH-1 → branchH-2: content conflict in veal.txt (criss-cross).
    merge_tree merge_tree_branchH_conflict branchH-1 branchH-2
    # branchJ-1 → branchJ-2: content conflict in version.txt.
    merge_tree merge_tree_branchJ_conflict branchJ-1 branchJ-2

    # --- Merge-base goldens ---
    merge_base merge_base_branchA branchA-1 branchA-2
    merge_base merge_base_branchF branchF-1 branchF-2
    merge_base merge_base_branchH branchH-1 branchH-2

    # --- Commit-level merge goldens (with date pinning) ---
    # Clean merge: branchA-1 + branchA-2.
    merge_commit merge_commit_branchA_clean branchA-1 branchA-2
    # Clean merge: branchC-1 + branchC-2 (requires recursive base; tree-level
    # with single base conflicts, but commit-level recursive merges clean).
    merge_commit merge_commit_branchC_clean branchC-1 branchC-2
    # Conflict merge: branchF-1 + branchF-2 (veal.txt conflict).
    merge_commit merge_commit_branchF_conflict branchF-1 branchF-2
    # Conflict merge: branchH-1 + branchH-2 (veal.txt conflict, criss-cross).
    merge_commit merge_commit_branchH_conflict branchH-1 branchH-2

    rm -rf "$MERGE_STAGE"
    echo "merge: golden corpus at $MERGE_EXPECTED ($(ls -1 "$MERGE_EXPECTED" | wc -l) files)"
fi

# ============================================================
# Submodule golden fixtures
# ============================================================
# Generates byte-exact golden output for GitSubmodule.ForEachAsync and
# GitSubmodule.StatusAsync vs `git submodule status`. Uses the libgit2
# `submodule_simple` fixture (1 submodule gitlink, not initialized).
#
# The fixture uses `.gitted` (renamed to `.git`) and a gitlink URL of
# `../testrepo.git` (relative). `git submodule status` reports `-` (not
# initialized) with the gitlink OID from the index. The C# GitSubmodule.StatusAsync
# produces WdUninitialized for the same state.
#
# Reference git: $(git --version) at generation time.
# ============================================================

SUBMODULE="$FIXTURES/submodule"
SUBMODULE_EXPECTED="$SUBMODULE/expected"
mkdir -p "$SUBMODULE_EXPECTED"

SUBMODULE_SRC="$LIBGIT2/tests/resources/submodule_simple"
SUBMODULE_ZIP="$SUBMODULE/submodule_simple.zip"

if [[ ! -d "$SUBMODULE_SRC" ]]; then
    echo "submodule: skip — $SUBMODULE_SRC not found" >&2
else
    # Package the fixture: copy, rename .gitted -> .git, zip.
    rm -f "$SUBMODULE_ZIP"
    local_stage="$(mktemp -d)"
    cp -r "$SUBMODULE_SRC" "$local_stage/submodule_simple"
    mv "$local_stage/submodule_simple/.gitted" "$local_stage/submodule_simple/.git"
    ( cd "$local_stage" && zip -qr "$SUBMODULE_ZIP" submodule_simple )
    rm -rf "$local_stage"
    echo "submodule: packaged submodule_simple.zip"

    # Extract and run `git submodule status`.
    sub_stage="$(mktemp -d)"
    ( cd "$sub_stage" && unzip -q "$SUBMODULE_ZIP" )
    sub_repo="$sub_stage/submodule_simple"

    # git submodule status: one line per submodule.
    # Format: "<status-flag><oid> <name> (<description>)"
    # The status flag is a single char: ' ' (unchanged), '-' (not initialized),
    # '+' (wd head differs), 'U' (merge conflict), '?' (untracked).
    git -C "$sub_repo" submodule status > "$SUBMODULE_EXPECTED/submodule_status_simple.txt" 2>&1 || true
    echo "submodule: status simple ($(wc -l < "$SUBMODULE_EXPECTED/submodule_status_simple.txt") lines)"

    # git ls-files -s: index state including the gitlink entry (mode 160000).
    git -C "$sub_repo" ls-files -s > "$SUBMODULE_EXPECTED/submodule_index_simple.txt" 2>&1
    echo "submodule: index simple ($(wc -l < "$SUBMODULE_EXPECTED/submodule_index_simple.txt") lines)"

    # .gitmodules content (the config mapping name -> path + url).
    git -C "$sub_repo" cat-file blob HEAD:.gitmodules > "$SUBMODULE_EXPECTED/submodule_gitmodules_simple.txt" 2>&1
    echo "submodule: gitmodules simple ($(wc -l < "$SUBMODULE_EXPECTED/submodule_gitmodules_simple.txt") lines)"

    rm -rf "$sub_stage"

    # No-submodules case: use the existing blame/testrepo fixture (a
    # non-bare repo with no gitlinks). The repo/testrepo.zip is bare and
    # cannot run `git submodule status` (no working tree).
    if [[ -f "$FIXTURES/blame/testrepo.zip" ]]; then
        no_sub_stage="$(mktemp -d)"
        ( cd "$no_sub_stage" && unzip -q "$FIXTURES/blame/testrepo.zip" )
        no_sub_repo="$no_sub_stage/testrepo"
        git -C "$no_sub_repo" submodule status > "$SUBMODULE_EXPECTED/submodule_status_empty.txt" 2>&1 || true
        echo "submodule: status empty ($(wc -l < "$SUBMODULE_EXPECTED/submodule_status_empty.txt") lines)"
        rm -rf "$no_sub_stage"
    fi

    echo "submodule: golden corpus at $SUBMODULE_EXPECTED ($(ls -1 "$SUBMODULE_EXPECTED" | wc -l) files)"
fi

# ============================================================
# Worktree golden fixtures
# ============================================================
# Generates byte-exact golden output for WorktreeListAsync vs
# `git worktree list --porcelain`. Builds a repo with 2 commits
# programmatically (matching the C# test setup), adds worktrees via
# `git worktree add`, and captures the porcelain output.
#
# The C# test creates the same repo + worktrees via the C# API, then
# compares FormatList() output against these goldens.
#
# Reference git: $(git --version) at generation time.
# ============================================================

WORKTREE="$FIXTURES/worktree"
WORKTREE_EXPECTED="$WORKTREE/expected"
mkdir -p "$WORKTREE_EXPECTED"

# Build a fresh repo matching the C# WorktreeGoldenBase setup.
# Uses explicit UTC dates (2000-01-01T00:00:00Z) to avoid timezone-dependent
# commit OIDs. The C# test uses the same UTC epoch values.
worktree_setup_repo() {
    local repo_dir="$1"
    git init -q "$repo_dir"
    git -C "$repo_dir" config user.name "Test User"
    git -C "$repo_dir" config user.email "test@example.com"
    git -C "$repo_dir" config commit.gpgsign false
    echo "hello" > "$repo_dir/file1.txt"
    git -C "$repo_dir" add file1.txt
    GIT_AUTHOR_DATE="2000-01-01T00:00:00Z" GIT_COMMITTER_DATE="2000-01-01T00:00:00Z" \
        git -C "$repo_dir" commit -q -m "first commit"
    echo "world" > "$repo_dir/file2.txt"
    git -C "$repo_dir" add file2.txt
    GIT_AUTHOR_DATE="2000-01-02T00:00:00Z" GIT_COMMITTER_DATE="2000-01-02T00:00:00Z" \
        git -C "$repo_dir" commit -q -m "second commit"
}

# worktree_list CASE_NAME WORKTREE_COUNT — builds a repo, adds
# WORKTREE_COUNT linked worktrees, captures `git worktree list --porcelain`.
worktree_list() {
    local case_name="$1"
    local wt_count="$2"
    local work; work="$(mktemp -d)"
    local repo="$work/main"
    worktree_setup_repo "$repo"
    local i
    for ((i = 1; i <= wt_count; i++)); do
        git -C "$repo" worktree add -q "$work/wt$i" -b "wt$i" HEAD 2>/dev/null
    done
    git -C "$repo" worktree list --porcelain > "$WORKTREE_EXPECTED/$case_name.txt" 2>&1
    echo "worktree: list $case_name ($(wc -l < "$WORKTREE_EXPECTED/$case_name.txt") lines)"
    rm -rf "$work"
}

# worktree_locked CASE_NAME — builds a repo, adds 1 worktree, locks it,
# captures `git worktree list --porcelain` (shows `locked` field).
worktree_locked() {
    local case_name="$1"
    local work; work="$(mktemp -d)"
    local repo="$work/main"
    worktree_setup_repo "$repo"
    git -C "$repo" worktree add -q "$work/wt1" -b "wt1" HEAD 2>/dev/null
    echo "manual lock reason" > "$repo/.git/worktrees/wt1/locked"
    git -C "$repo" worktree list --porcelain > "$WORKTREE_EXPECTED/$case_name.txt" 2>&1
    echo "worktree: locked $case_name ($(wc -l < "$WORKTREE_EXPECTED/$case_name.txt") lines)"
    rm -rf "$work"
}

worktree_list worktree_list_none 0
worktree_list worktree_list_one 1
worktree_list worktree_list_three 3
worktree_locked worktree_locked

echo "worktree: golden corpus at $WORKTREE_EXPECTED ($(ls -1 "$WORKTREE_EXPECTED" | wc -l) files)"

# ============================================================
# Checkout golden fixtures
# ============================================================
# Generates byte-exact golden output for CheckoutTreeAsync vs `git checkout`.
# Each test case builds a repo with a multi-file tree + a second commit,
# runs `git checkout` with equivalent strategy, then captures:
#   1. The index state (`git ls-files -s`)
#   2. The workdir file tree snapshot (path + SHA-1 of content + size)
#
# The C# test replicates the same setup, calls CheckoutTreeAsync, then
# compares both index (via IndexGoldenFormatter) and workdir snapshot
# (via CheckoutGoldenFormatter.FormatWorkdirSnapshot).
#
# Reference git: $(git --version) at generation time.
# ============================================================

CHECKOUT="$FIXTURES/checkout"
CHECKOUT_EXPECTED="$CHECKOUT/expected"
mkdir -p "$CHECKOUT_EXPECTED"

# Build a repo matching the C# CheckoutGoldenBase setup: 2 commits with
# a multi-file tree, then a second commit that modifies + deletes files.
# After setup, HEAD is at the second commit, and both the index and workdir
# reflect the second commit's state (a.txt modified, b.txt deleted, d.txt added).
checkout_setup_repo() {
    local repo_dir="$1"
    git init -q "$repo_dir"
    git -C "$repo_dir" config user.name "Test User"
    git -C "$repo_dir" config user.email "test@example.com"
    git -C "$repo_dir" config commit.gpgsign false
    # First commit: 4 files across 2 directories.
    mkdir -p "$repo_dir/dir1" "$repo_dir/dir2"
    echo "alpha content" > "$repo_dir/dir1/a.txt"
    echo "beta content" > "$repo_dir/dir1/b.txt"
    echo "gamma content" > "$repo_dir/dir2/c.txt"
    echo "root file" > "$repo_dir/root.txt"
    git -C "$repo_dir" add -A
    GIT_AUTHOR_DATE="2000-01-01T00:00:00Z" GIT_COMMITTER_DATE="2000-01-01T00:00:00Z" \
        git -C "$repo_dir" commit -q -m "first commit"
    # Second commit: modify a.txt, delete b.txt, add d.txt.
    echo "alpha modified" > "$repo_dir/dir1/a.txt"
    git -C "$repo_dir" rm -q dir1/b.txt
    echo "delta content" > "$repo_dir/dir1/d.txt"
    git -C "$repo_dir" add -A
    GIT_AUTHOR_DATE="2000-01-02T00:00:00Z" GIT_COMMITTER_DATE="2000-01-02T00:00:00Z" \
        git -C "$repo_dir" commit -q -m "second commit"
    # HEAD is now at the second commit. Workdir + index reflect the second
    # commit's state. No restoration needed — the checkout test will
    # transition from this state to the target tree.
}

# checkout_snapshot WORKDIR OUTFILE — captures the workdir file tree
# snapshot: for each tracked file, emit "<relative-path>\t<sha1-hex>\t<size>\n"
# sorted by path. Uses git ls-files to enumerate tracked files (avoiding
# untracked/ignored noise) and computes SHA-1 of raw file bytes.
checkout_snapshot() {
    local workdir="$1"
    local outfile="$2"
    local tmpfile; tmpfile="$(mktemp)"
    # Enumerate tracked files (after checkout), compute path + sha1 + size.
    # Use git ls-files to get tracked paths, then hash each file.
    while IFS= read -r fpath; do
        local full="$workdir/$fpath"
        if [[ -f "$full" ]]; then
            local sha1; sha1=$(git hash-object "$full")
            local size; size=$(wc -c < "$full" | tr -d ' ')
            printf '%s\t%s\t%s\n' "$fpath" "$sha1" "$size" >> "$tmpfile"
        fi
    done < <(git -C "$workdir" ls-files)
    sort "$tmpfile" > "$outfile"
    rm -f "$tmpfile"
}

# checkout_case CASE_NAME TREE_REF EXTRA_GIT_ARGS — builds a repo,
# checks out TREE_REF with the given extra git args, captures index +
# workdir snapshot.
checkout_case() {
    local case_name="$1"
    local tree_ref="$2"
    shift 2
    local extra_args=("$@")
    local work; work="$(mktemp -d)"
    local repo="$work/repo"
    checkout_setup_repo "$repo"
    # The workdir is at the first commit's state; checkout the target tree
    # to transition the workdir + index to the target state.
    if [[ ${#extra_args[@]} -gt 0 ]]; then
        git -C "$repo" checkout -q "${extra_args[@]}" "$tree_ref" -- . 2>/dev/null || \
            git -C "$repo" checkout -q "${extra_args[@]}" "$tree_ref" 2>/dev/null || true
    else
        git -C "$repo" checkout -q "$tree_ref" -- . 2>/dev/null || true
    fi
    git -C "$repo" ls-files -s > "$CHECKOUT_EXPECTED/checkout_index_${case_name}.txt" 2>&1
    checkout_snapshot "$repo" "$CHECKOUT_EXPECTED/checkout_workdir_${case_name}.txt"
    echo "checkout: $case_name (index $(wc -l < "$CHECKOUT_EXPECTED/checkout_index_${case_name}.txt") lines, workdir $(wc -l < "$CHECKOUT_EXPECTED/checkout_workdir_${case_name}.txt") lines)"
    rm -rf "$work"
}

# checkout_case_full CASE_NAME TREE_REF — builds a repo, does a FULL
# checkout (git checkout <ref> without -- . pathspec), which removes
# files not in the target tree. Captures index + workdir snapshot.
checkout_case_full() {
    local case_name="$1"
    local tree_ref="$2"
    local work; work="$(mktemp -d)"
    local repo="$work/repo"
    checkout_setup_repo "$repo"
    # Full checkout (no pathspec): removes files not in target tree.
    git -C "$repo" checkout -q "$tree_ref" 2>/dev/null || true
    git -C "$repo" ls-files -s > "$CHECKOUT_EXPECTED/checkout_index_${case_name}.txt" 2>&1
    checkout_snapshot "$repo" "$CHECKOUT_EXPECTED/checkout_workdir_${case_name}.txt"
    echo "checkout: $case_name (index $(wc -l < "$CHECKOUT_EXPECTED/checkout_index_${case_name}.txt") lines, workdir $(wc -l < "$CHECKOUT_EXPECTED/checkout_workdir_${case_name}.txt") lines)"
    rm -rf "$work"
}

# Clean checkout: checkout HEAD (no-op, already at HEAD). Verifies the
# index + workdir match the second commit's state.
checkout_case clean HEAD

# Force checkout with untracked file present.
checkout_case force_untracked HEAD --force

# Full checkout to HEAD~1 (first commit): transitions from second commit
# to first commit. a.txt restored, b.txt added, d.txt removed.
checkout_case_full tree_head1 HEAD~1

echo "checkout: golden corpus at $CHECKOUT_EXPECTED ($(ls -1 "$CHECKOUT_EXPECTED" | wc -l) files)"

# ============================================================
# Merge workdir-state golden fixtures
# ============================================================
# Generates byte-exact golden output for the .git/MERGE_HEAD,
# .git/MERGE_MODE, .git/MERGE_MSG, .git/ORIG_HEAD files written by
# GitRepository.MergeAsync vs `git merge --no-ff --no-commit`.
#
# Uses the existing merge-recursive.zip fixture (same as the merge section).
# The golden file concatenates all 4 state files with section headers:
#   === ORIG_HEAD ===
#   <oid>
#   === MERGE_HEAD ===
#   <oid>
#   === MERGE_MODE ===
#   no-ff
#   === MERGE_MSG ===
#   <message>
#
# Reference git: $(git --version) at generation time. Dates pinned for
# deterministic MERGE_MSG content.
# ============================================================

MERGE_STATE="$FIXTURES/merge"
MERGE_STATE_EXPECTED="$MERGE_STATE/expected"
# Reuse merge-recursive.zip (already extracted by the merge section if run).
MERGE_STATE_REPO="merge-recursive"
MERGE_STATE_ZIP="$MERGE_STATE/$MERGE_STATE_REPO.zip"

if [[ ! -f "$MERGE_STATE_ZIP" ]]; then
    echo "merge-state: skip — $MERGE_STATE_ZIP not found" >&2
else
    # merge_state CASE_NAME OURS THEIRS — builds a work copy,
    # runs `git merge --no-ff --no-commit`, concatenates the 4 state files.
    # NOTE: core git's MERGE_MSG includes an " into <branch>" suffix that
    # libgit2 does NOT produce (libgit2's write_merge_msg has no "into"
    # logic). The golden strips this suffix to match the C# port (which
    # is a 1:1 port of libgit2, not core git).
    merge_state() {
        local case_name="$1"
        local ours="$2"
        local theirs="$3"
        local work; work="$(mktemp -d)"
        cp -r "$MERGE_STATE_ZIP" "$work/" 2>/dev/null || true
        # Extract fresh copy (read-only zip, so copy then extract).
        ( cd "$work" && unzip -q "$MERGE_STATE_ZIP" )
        local repo="$work/$MERGE_STATE_REPO"
        git -C "$repo" checkout -q "$ours" 2>/dev/null
        GIT_AUTHOR_DATE=2000-01-01T00:00:00 \
        GIT_COMMITTER_DATE=2000-01-01T00:00:00 \
        GIT_AUTHOR_NAME="Test Author" \
        GIT_AUTHOR_EMAIL="test@example.com" \
        GIT_COMMITTER_NAME="Test Author" \
        GIT_COMMITTER_EMAIL="test@example.com" \
            git -C "$repo" merge --no-ff --no-commit "$theirs" 2>/dev/null || true
        {
            echo "=== ORIG_HEAD ==="
            cat "$repo/.git/ORIG_HEAD" 2>/dev/null || echo "(missing)"
            echo "=== MERGE_HEAD ==="
            cat "$repo/.git/MERGE_HEAD" 2>/dev/null || echo "(missing)"
            echo "=== MERGE_MODE ==="
            cat "$repo/.git/MERGE_MODE" 2>/dev/null || echo "(missing)"
            echo "=== MERGE_MSG ==="
            # Strip the " into <branch>" suffix that core git adds but
            # libgit2 does not. Also normalize "# Conflicts:" to "#Conflicts:"
            # (libgit2 has no space after #). This makes the golden match
            # the C# port (1:1 port of libgit2, not core git).
            sed 's/ into [^ ]*$//; s/^# Conflicts:/#Conflicts:/' "$repo/.git/MERGE_MSG" 2>/dev/null || echo "(missing)"
        } > "$MERGE_STATE_EXPECTED/merge_state_${case_name}.txt"
        echo "merge-state: $case_name ($(wc -l < "$MERGE_STATE_EXPECTED/merge_state_${case_name}.txt") lines)"
        rm -rf "$work"
    }

    # Clean merge: branchA-1 + branchA-2.
    merge_state branchA_clean branchA-1 branchA-2
    # Conflict merge: branchF-1 + branchF-2 (veal.txt content conflict).
    merge_state branchF_conflict branchF-1 branchF-2
    # Conflict merge: branchH-1 + branchH-2 (veal.txt conflict, criss-cross).
    merge_state branchH_conflict branchH-1 branchH-2

    echo "merge-state: golden corpus at $MERGE_STATE_EXPECTED ($(ls -1 "$MERGE_STATE_EXPECTED" | grep -c merge_state) state files)"
fi

# ============================================================
# Status-engine fixture packaging
# ============================================================
# Packages 6 libgit2 test resource repositories into
# $FIXTURES/status/<name>.zip for use by StatusGoldenTests.cs.
# Each repo is stored under tests/resources/<name>/ with the git dir
# named ".gitted". We rename .gitted -> .git inside the zip so C# tests
# can open the extracted working-tree root directly via GitRepository.OpenAsync.
#
# The fixtures exercise: filemode changes (filemodes), case-insensitive
# filesystems (icase), ignore/pathspec interaction (issue_592, issue_592b),
# CRLF handling (issue_1397), and a multi-commit test repo (testrepo2).
# ============================================================

STATUS="$FIXTURES/status"
STATUS_RESOURCES="$LIBGIT2/tests/resources"

if [[ ! -d "$STATUS_RESOURCES" ]]; then
    echo "status: skipped (libgit2 resources not found at $STATUS_RESOURCES)"
else
    mkdir -p "$STATUS"

    # Package each fixture. The zip extracts to <name>/ with .git inside.
    for fixture in filemodes icase issue_592 issue_592b issue_1397 testrepo2; do
        fixture_src="$STATUS_RESOURCES/$fixture"
        if [[ -d "$fixture_src" ]]; then
            workdir="$(mktemp -d)"
            mkdir -p "$workdir/$fixture"
            cp -r "$fixture_src/." "$workdir/$fixture/"
            if [[ -d "$workdir/$fixture/.gitted" ]]; then
                mv "$workdir/$fixture/.gitted" "$workdir/$fixture/.git"
            fi
            zip_dest="$STATUS/$fixture.zip"
            rm -f "$zip_dest"
            ( cd "$workdir" && zip -qr "$zip_dest" "$fixture" )
            rm -rf "$workdir"
            echo "status: packaged $fixture -> $zip_dest"
        else
            echo "status: $fixture not found — skipping"
        fi
    done

    echo "status: fixture corpus at $STATUS ($(ls -1 "$STATUS" | grep -c '\.zip$') zips)"
fi

#!/usr/bin/env bash
# test_classify_docs_only.sh — unit tests for scripts/ci/classify-docs-only.sh
# (verbara-meta/ADR-0016 §3.4). Each case builds a throwaway git repo and asserts the verdict over
# the WHOLE $SEED..HEAD range — which is what CI diffs (pull_request.base.sha .. github.sha).
# Wave-1's harness diffed HEAD~1..HEAD, so its multi-file cases only ever tested the LAST commit
# and passed for the wrong reason; commit_many() + the seed range fix that. Pure bash + git, ~2s.
# Runs in the ALWAYS-RUN, REQUIRED `Coverage Script Tests` job.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLASSIFY="$SCRIPT_DIR/../ci/classify-docs-only.sh"
CS="$SCRIPT_DIR/../../Tests/Verbara.Sdk.DocSnippets.Tests/DocSnippetCompilationTests.cs"
fails=0; pass=0
ok()   { pass=$((pass + 1)); }
bad()  { echo "FAIL: $1"; fails=$((fails + 1)); }

# Every throwaway repo lives under one root that is removed on exit, pass or fail, so a run
# leaves nothing behind in the temp directory.
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

new_repo() { # new_repo [seed-file ...]
  WORK="$(mktemp -d -p "$TMP_ROOT")"
  git -C "$WORK" init -q
  git -C "$WORK" config user.email t@t.t
  git -C "$WORK" config user.name t
  local f
  for f in "$@"; do mkdir -p "$WORK/$(dirname "$f")"; printf 'seed %s\n' "$f" > "$WORK/$f"; done
  git -C "$WORK" add -A
  git -C "$WORK" commit -q --allow-empty -m seed
  SEED="$(git -C "$WORK" rev-parse HEAD)"
}
commit_many() { # commit_many <file> [file...] — ONE commit, N paths (the real PR shape)
  local p
  for p in "$@"; do mkdir -p "$WORK/$(dirname "$p")"; printf 'x\n' > "$WORK/$p"; done
  git -C "$WORK" add -A; git -C "$WORK" commit -q -m "change $*"
}
run_case() { # run_case <expected> <description>
  local expected="$1" desc="$2" out
  out="$(cd "$WORK" && bash "$CLASSIFY" "$SEED" "$(git -C "$WORK" rev-parse HEAD)")"
  [ "$out" = "docs_only=$expected" ] && ok || bad "$desc — expected docs_only=$expected, got '$out'"
}

# --- contract ---
[ -x "$CLASSIFY" ] && ok || bad "classifier must be committed executable (mode 100755)"

# --- true cases (allowlisted) ---
new_repo; commit_many docs/guides/asterisk-version-matrix.md; run_case true "docs/ nested"
new_repo; commit_many docs/operations/x.md;                  run_case true  "docs/operations"
new_repo; commit_many CHANGELOG.md;                          run_case true  "top-level CHANGELOG.md (PRs #113/#114)"
new_repo; commit_many src/Verbara.Sdk.Ami/README.md;         run_case true  "nested README (packed, NOT DocSnippets-compiled)"
new_repo; commit_many Examples/SessionExample/README.md;     run_case true  "Examples README (PR #93 shape)"
new_repo; commit_many SECURITY.md;                           run_case true  "top-level *.md"
new_repo; commit_many docs/ci-docs-fast-path.md;             run_case true  "the verbara-meta/ADR-0016 §6 canary payload itself"
new_repo; commit_many docs/guides/README.md;                 run_case true  "docs/guides README is NOT a snippet source"
new_repo; commit_many docs/guides/log-analysis-reference.md; run_case true  "non-snippet guide (carve-out is a FILE LIST, not docs/guides/*)"
new_repo; commit_many docs/claim-registry.md CHANGELOG.md src/Verbara.Sdk.Ami/README.md
run_case true "MULTI-PATH docs + changelog + packed README in one commit"
new_repo; commit_many "docs/café.md";                        run_case true  "non-ASCII path (core.quotePath=false)"

# --- Sdk DocSnippets carve-out (the gated Unit Tests job is their only guard) ---
new_repo; commit_many README.md;                             run_case false "top-level README.md is DocSnippets-compiled"
new_repo; commit_many docs/README-technical.md;              run_case false "docs/README-technical.md is DocSnippets-compiled"
new_repo; commit_many docs/guides/troubleshooting.md;        run_case false "docs/guides/troubleshooting.md is DocSnippets-compiled"
new_repo; commit_many docs/guides/asterisk-version-matrix.md docs/guides/high-load-tuning.md
run_case false "MULTI-PATH: carve-out wins inside an otherwise-allowlisted diff"

# --- false cases (fail-closed) ---
new_repo; commit_many src/Verbara.Sdk/Foo.cs;                run_case false "nested code file"
new_repo; commit_many Directory.Build.props;                 run_case false "top-level non-md"
new_repo; commit_many gates.yaml;                            run_case false "top-level yaml is deliberately fail-closed (PR #123 shape)"
new_repo; commit_many docker/Dockerfile.asterisk;            run_case false "top-level dir merely STARTING with 'docs' (bash case globs cross /)"
new_repo; commit_many src/Verbara.Sdk/NOTES.md;              run_case false "nested non-README .md (blanket **/*.md is BANNED)"
new_repo; commit_many .github/workflows/ci.yml;              run_case false "workflow change (self-validation: the rollout PR is NOT docs-only)"
new_repo; commit_many scripts/ci/classify-docs-only.sh;      run_case false "the classifier itself"
new_repo; commit_many docs/guides/asterisk-version-matrix.md src/Verbara.Sdk/Foo.cs
run_case false "MULTI-PATH docs + code (docs first)"
new_repo; commit_many src/Verbara.Sdk/Foo.cs docs/guides/asterisk-version-matrix.md
run_case false "MULTI-PATH code + docs (docs last — order must not matter)"

# rename CODE -> DOCS: without --no-renames git prints ONLY the destination (docs/...) and this
# would read TRUE. This is the real --no-renames pin; the docs->code direction is false either way.
new_repo tools/verify-aot.sh
mkdir -p "$WORK/docs/tools"
git -C "$WORK" mv tools/verify-aot.sh docs/tools/verify-aot.sh
git -C "$WORK" commit -q -m "rename tools/verify-aot.sh -> docs/tools/verify-aot.sh"
run_case false "rename CODE->DOCS classifies both paths (--no-renames pin)"
new_repo docs/old.md
mkdir -p "$WORK/src/Verbara.Sdk"
git -C "$WORK" mv docs/old.md src/Verbara.Sdk/New.cs
git -C "$WORK" commit -q -m "rename docs/old.md -> src/Verbara.Sdk/New.cs"
run_case false "rename DOCS->CODE classifies both paths"

# --- exit-code contract (fail-closed at the gate) ---
new_repo; commit_many docs/a.md
(cd "$WORK" && bash "$CLASSIFY" "$SEED" "$SEED") | grep -qx 'docs_only=false' && ok || bad "empty diff => docs_only=false"
if (cd "$WORK" && bash "$CLASSIFY" "" >/dev/null 2>&1); then bad "empty BASE must exit non-zero"; else ok; fi
if (cd "$WORK" && bash "$CLASSIFY" deadbeefdeadbeef HEAD >/dev/null 2>&1); then bad "unreachable base must exit non-zero"; else ok; fi

# --- drift guard: the carve-out MUST remain a superset of DocSnippetCompilationTests.cs sources ---
# The guard reads the list the snippet test really iterates: the collection named by a
# `foreach (var x in <name>)`, from its declaration `<name> =` up to the first `;`. That holds for
# a field (`string[] _snippetSources = [...]`), a local (`var sources = new[] {...}`) or any other
# collection type, and survives a rename. Finding no declaration that lists a .md file is a loud
# failure, never a silent pass: a guard that reads nothing passes whatever the carve-out holds.
snippet_sources() { # snippet_sources <cs-file> — one path per line; exit 1 when no list is found
  local cs="$1" names n out=""
  names="$(sed -nE 's/.*foreach[[:space:]]*\([^)]*[[:space:]]in[[:space:]]+([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*\).*/\1/p' "$cs" | sort -u)"
  for n in $names; do
    out+="$(awk -v n="$n" '
      !inside && $0 !~ /foreach/ && $0 ~ ("(^|[^A-Za-z0-9_.])" n "[[:space:]]*=") \
        && $0 !~ (n "[[:space:]]*=[=>]") { inside = 1 }
      inside { print; if ($0 ~ /;/) exit }' "$cs" | grep -oE '"[^"]+\.md"' | tr -d '"' || true)"$'\n'
  done
  out="$(printf '%s' "$out" | sed '/^$/d' | sort -u)"
  [ -n "$out" ] || return 1
  printf '%s\n' "$out"
}
carveout() { # carveout <classifier> — the pipe-separated carve-out line; exit 1 when absent
  local c
  c="$(grep -A1 'BEGIN docsnippets-carveout' "$1" | tail -1 | tr -d ' \t' | sed 's/).*$//')"
  case "$c" in *.md*) printf '%s\n' "$c" ;; *) return 1 ;; esac
}
drift_missing() { # drift_missing <cs-file> <classifier> — prints each source the carve-out lacks
  local srcs carve s
  srcs="$(snippet_sources "$1")" || { echo "NO-SNIPPET-SOURCE-DECLARATION"; return 2; }
  carve="$(carveout "$2")" || { echo "NO-CARVEOUT"; return 2; }
  while IFS= read -r s; do
    case "|$carve|" in *"|$s|"*) ;; *) echo "$s" ;; esac
  done <<< "$srcs"
}

if [ -f "$CS" ]; then
  if missing="$(drift_missing "$CS" "$CLASSIFY")"; then
    if [ -z "$missing" ]; then ok; else
      while IFS= read -r s; do
        bad "DocSnippets source '$s' is NOT in the classifier carve-out (a docs PR editing it would skip Unit Tests)"
      done <<< "$missing"
    fi
  else
    bad "drift guard read nothing ($missing) from $CS / $CLASSIFY — it cannot vouch for the carve-out"
  fi
  # The guard must read a list, not one stray string: the real list holds README.md and more.
  n_read="$(snippet_sources "$CS" | wc -l)" || n_read=0
  [ "$n_read" -ge 2 ] && ok || bad "drift guard read only $n_read snippet source(s) from $CS"
else
  bad "DocSnippetCompilationTests.cs not found — the carve-out drift guard cannot run"
fi

# --- drift guard negative controls: the guard must be able to turn red ---
CTRL="$(mktemp -d -p "$TMP_ROOT")"
printf '%s\n' 'class T {' '    private static readonly string[] _snippetSources =' '    [' \
  '        "README.md",' '        "docs/guides/not-in-the-carveout.md",' '    ];' \
  '    void M() { foreach (var relativePath in _snippetSources) { } }' '}' > "$CTRL/outside.cs"
out="$(drift_missing "$CTRL/outside.cs" "$CLASSIFY" || true)"
[ "$out" = "docs/guides/not-in-the-carveout.md" ] && ok \
  || bad "negative control: a source outside the carve-out must be reported, got '$out'"
printf '%s\n' 'class T {' '    void M() {' '        var sources = new[] { "README.md", "docs/README-technical.md" };' \
  '        foreach (var s in sources) { }' '    }' '}' > "$CTRL/local.cs"
[ "$(snippet_sources "$CTRL/local.cs")" = "$(printf '%s\n' README.md docs/README-technical.md | sort -u)" ] && ok \
  || bad "shape control: a local 'var sources = new[] {...}' list must be read"
printf '%s\n' 'class T {' '    private static readonly string[] _paths = [ "README.md" ];' '}' > "$CTRL/none.cs"
if drift_missing "$CTRL/none.cs" "$CLASSIFY" >/dev/null; then
  bad "negative control: a file with no iterated source list must fail the guard, not pass it"
else ok; fi

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"

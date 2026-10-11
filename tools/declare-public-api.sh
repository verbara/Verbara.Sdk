#!/usr/bin/env bash
set -euo pipefail

# Declares undeclared public API in each package's PublicAPI.Shipped.txt from the compiler's own
# RS0016 output, then lets the compiler verify what was written.
#
# Usage: tools/declare-public-api.sh [--list] [--no-verify] [--inventory <file>] [<project.csproj>...]
#
#   <project.csproj>    the projects to declare (repo-relative or absolute). With none, the whole
#                       solution is inventoried and every project with a finding is written.
#   --inventory <file>  take the symbols from a file of '<project.csproj>|<symbol>' rows (project
#                       repo-relative; blank lines and lines starting with '#' ignored) instead of
#                       running the inventory build.
#   --list              print the '<project.csproj>|<symbol>' rows the parse found and stop; nothing
#                       is written.
#   --no-verify         write the records but skip the verification rebuild (step 4).
#
# The four steps:
#   1. Inventory build with RS0016 lifted as a GLOBAL property, so nothing is edited and nothing has
#      to be restored: NoWarn is the project's own evaluated value minus RS0016, and
#      TreatWarningsAsErrors=false, or the first failing project stops its dependents and the
#      inventory is partial.
#   2. The message is the record line: the text between "Symbol '" and the fixed
#      "' is not part of the declared public API" is exactly what the record file takes. Anchor on
#      that fixed text, never on a closing quote (symbols contain quotes, '<Clone>$', 'operator ==').
#      MSBuild prints every diagnostic at least twice, so rows are deduplicated per project, and the
#      owning project is the trailing '[.../X.csproj]', not the source path (generated files).
#   3. Append to PublicAPI.Shipped.txt: the file must begin with '#nullable enable' and end with LF;
#      a symbol already present in either record file is skipped; only the appended block is sorted
#      (ordinal); existing lines are left alone. Unshipped is never written.
#   4. The oracle is the compiler, not a diff: each written project is rebuilt alone with RS0016
#      live (its own NoWarn minus RS0016) and TreatWarningsAsErrors at its default, and must report
#      0 warnings and 0 errors. RS0016 there is a missing entry, RS0017 a mistyped or stale one.
#      Never verify a whole solution with a global NoWarn: it replaces the value every project
#      inherits, so it also discards per-project appends (test projects' CA1707, for one) and fails
#      for reasons that have nothing to do with the records.
#
# Exit codes:
#   0  every selected project declared (and verified, unless --no-verify)
#   2  usage error, or a project/inventory file that does not exist
#   3  the inventory build failed (non-zero exit)
#   4  a record file is missing or malformed (no '#nullable enable' header, or no trailing LF)
#   5  the inventory build already reports RS0017: a record holds a stale or mistyped entry
#   6  a verification rebuild failed or did not report 0 warnings and 0 errors

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"
SOLUTION="Verbara.Sdk.slnx"
ANCHOR="' is not part of the declared public API"

list_only=0
verify=1
inventory_file=""
projects=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --list) list_only=1 ;;
    --no-verify) verify=0 ;;
    --inventory)
      [[ $# -ge 2 ]] || { echo "ERROR: --inventory needs a file" >&2; exit 2; }
      inventory_file="$2"; shift ;;
    -h|--help) sed -n '4,17p' "$0"; exit 0 ;;
    -*) echo "ERROR: unknown option $1" >&2; exit 2 ;;
    *) projects+=("$1") ;;
  esac
  shift
done

# Normalise each project to a repo-relative path.
rel_projects=()
for p in "${projects[@]+"${projects[@]}"}"; do
  [[ -f "$p" ]] || { echo "ERROR: no such project: $p" >&2; exit 2; }
  abs=$(cd "$(dirname "$p")" && pwd)/$(basename "$p")
  rel_projects+=("${abs#"$ROOT"/}")
done

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
ROWS="$WORK/rows"
: > "$ROWS"

# NoWarn as the project evaluates it, minus RS0016, ';' escaped for the command line.
nowarn_without_rs0016() {
  local value
  value=$(dotnet msbuild "$1" -getProperty:NoWarn -p:Configuration=Release)
  printf '%s' "$value" | tr ';' '\n' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//' \
    | awk 'NF && $0 != "RS0016"' | paste -sd ';' | sed 's/;/%3B/g'
}

# Step 2: '<repo-relative csproj>|<symbol>' from a build log, deduplicated.
parse_log() {
  sed -n "s/^.*: \(warning\|error\) RS0016: Symbol '\(.*\)${ANCHOR}.*\[\([^][]*\.csproj\)\][[:space:]]*\$/\3|\2/p" "$1" \
    | sed "s#^${ROOT}/##" | LC_ALL=C sort -u
}

if [[ -n "$inventory_file" ]]; then
  [[ -f "$inventory_file" ]] || { echo "ERROR: no such inventory: $inventory_file" >&2; exit 2; }
  awk 'NF && $0 !~ /^#/' "$inventory_file" | LC_ALL=C sort -u > "$WORK/all"
else
  # Step 1: the inventory build.
  targets=("${rel_projects[@]+"${rel_projects[@]}"}")
  [[ ${#targets[@]} -gt 0 ]] || targets=("$SOLUTION")
  : > "$WORK/all"
  for t in "${targets[@]}"; do
    probe="$t"
    if [[ "$t" == "$SOLUTION" ]]; then
      probe=$(find src -mindepth 2 -maxdepth 2 -name '*.csproj' | LC_ALL=C sort | head -n 1)
    fi
    nw=$(nowarn_without_rs0016 "$probe")
    log="$WORK/inventory.log"
    echo "Inventory build: $t (NoWarn=${nw//%3B/;}, TreatWarningsAsErrors=false)" >&2
    set +e
    dotnet build "$t" -c Release --no-incremental -p:NoWarn="$nw" -p:TreatWarningsAsErrors=false > "$log" 2>&1
    rc=$?
    set -e
    if [[ $rc -ne 0 ]]; then
      tail -n 30 "$log" >&2
      echo "ERROR: inventory build of $t exited $rc" >&2
      exit 3
    fi
    if grep -q ' RS0017: ' "$log"; then
      grep ' RS0017: ' "$log" | LC_ALL=C sort -u >&2
      echo "ERROR: the inventory build reports RS0017; fix the record before declaring more" >&2
      exit 5
    fi
    parse_log "$log" >> "$WORK/all"
  done
  LC_ALL=C sort -u -o "$WORK/all" "$WORK/all"
fi

# Keep only the selected projects (all of them when none was named).
if [[ ${#rel_projects[@]} -gt 0 ]]; then
  printf '%s\n' "${rel_projects[@]}" > "$WORK/selected"
  awk -F'|' 'NR == FNR { keep[$0] = 1; next } ($1 in keep)' "$WORK/selected" "$WORK/all" > "$ROWS"
else
  cp "$WORK/all" "$ROWS"
fi

if [[ $list_only -eq 1 ]]; then
  cat "$ROWS"
  exit 0
fi

# Step 3: append per project.
written=()
total=0
while IFS= read -r proj; do
  dir=$(dirname "$proj")
  shipped="$dir/PublicAPI.Shipped.txt"
  unshipped="$dir/PublicAPI.Unshipped.txt"
  for f in "$shipped" "$unshipped"; do
    [[ -f "$f" ]] || { echo "ERROR: missing record file: $f" >&2; exit 4; }
    [[ "$(head -c 16 "$f")" == "#nullable enable" ]] || { echo "ERROR: $f does not begin with '#nullable enable'" >&2; exit 4; }
    [[ "$(tail -c 1 "$f" | od -An -c | tr -d ' ')" == '\n' ]] || { echo "ERROR: $f does not end with LF" >&2; exit 4; }
  done
  block="$WORK/block"
  awk -F'|' -v p="$proj" '$1 == p { sub(/^[^|]*\|/, ""); print }' "$ROWS" \
    | awk 'NR == FNR { have[$0] = 1; next } !($0 in have)' <(cat "$shipped" "$unshipped") - \
    | LC_ALL=C sort -u > "$block"
  n=$(wc -l < "$block" | tr -d ' ')
  skipped=$(( $(awk -F'|' -v p="$proj" '$1 == p' "$ROWS" | wc -l) - n ))
  cat "$block" >> "$shipped"
  echo "Declared $n in $shipped (already present, skipped: $skipped)" >&2
  total=$(( total + n ))
  written+=("$proj")
done < <(cut -d'|' -f1 "$ROWS" | LC_ALL=C sort -u)

echo "Declared $total symbol(s) in ${#written[@]} project(s)" >&2
[[ $verify -eq 1 && ${#written[@]} -gt 0 ]] || exit 0

# Step 4: the compiler verifies, one project at a time.
failed=0
for proj in "${written[@]}"; do
  nw=$(nowarn_without_rs0016 "$proj")
  log="$WORK/verify.log"
  set +e
  dotnet build "$proj" -c Release --no-incremental -p:NoWarn="$nw" > "$log" 2>&1
  rc=$?
  set -e
  if [[ $rc -eq 0 ]] && grep -Eq '^[[:space:]]*0 Warning\(s\)' "$log" && grep -Eq '^[[:space:]]*0 Error\(s\)' "$log"; then
    echo "Verified: $proj (NoWarn=${nw//%3B/;}): 0 warnings, 0 errors" >&2
  else
    grep -E ' (RS00[0-9]{2}|[A-Z]+[0-9]{4}): ' "$log" | LC_ALL=C sort -u | head -n 40 >&2 || true
    echo "ERROR: verification rebuild of $proj exited $rc or did not report 0 warnings, 0 errors" >&2
    failed=1
  fi
done
[[ $failed -eq 0 ]] || exit 6

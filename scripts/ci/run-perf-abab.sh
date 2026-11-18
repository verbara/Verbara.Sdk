#!/usr/bin/env bash
# run-perf-abab.sh — measure the reference tag and HEAD alternated, in one job, on one machine.
#
# Usage:
#   run-perf-abab.sh --baseline <baseline.json> --head <dir> --reference <dir>
#                    --reference-tag <tag> --out <dir>
#
# Environment:
#   PERF_ABAB_INVOCATION_TIMEOUT_S  seconds per invocation (default: baseline abab.invocation_timeout_s)
#   PERF_ABAB_DRY_RUN=1             print the plan, run nothing, write nothing
#
# What it does, for scripts/ci/check-perf-abab.py to judge:
#   1. Reads the `sources` types and `abab.order` from HEAD's baseline.json. The filter set is one
#      exact glob per type (`<Type>.*`), the same for both arms, so a loose glob on one side cannot
#      change what the ratio compares.
#   2. Writes `--list flat` for each arm: <out>/list-R.txt and <out>/list-H.txt. They are the
#      methods each tree declares, so a benchmark present on one side only is reported, not dropped.
#   3. Runs one BenchmarkDotNet invocation per position of the order (all the classes in one
#      invocation: one generated-project build each), into <out>/<position>-<arm>, each under
#      `timeout` and with `--launchCount <abab.launch_count>`: BenchmarkDotNet rewrites the source's
#      `[ShortRunJob]` in place, so each case runs in that many processes per invocation. A failing or timed-out position never stops the later ones: missing output is the
#      comparator's to find, as a structural failure.
#   4. Writes <out>/manifest.json: the order, the launch count, each invocation's times and exit
#      code, the two commits, the files that differ between them, and the newest release tag.
#
# Both arms run with the job's environment (the ISA pin, the pinned SDK) and the same command line.
# The job is the benchmark source's ([ShortRunJob], [MemoryDiagnoser]) with one override, the launch
# count, which only this in-job run passes: the absolute gate's steps run the sources' job as written.
# The `--list flat` calls take no job option. check-perf-abab.py confirms from each report that the
# override applied.
#
# Exit: 0 after the arguments parse, whatever the invocations did; 2 on bad usage or a baseline
# that declares no `abab` block, or one whose `abab.launch_count` is missing or not a positive
# integer (no default: a silent 1 would measure the old design). Unit-tested by scripts/tests/test_run_perf_abab.sh.
set -uo pipefail

die() { echo "::error::run-perf-abab: $1"; exit 2; }
USAGE="usage: run-perf-abab.sh --baseline <baseline.json> --head <dir> --reference <dir> --reference-tag <tag> --out <dir>"

BASELINE="" HEAD_DIR="" REF_DIR="" REF_TAG="" OUT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --baseline|--head|--reference|--reference-tag|--out)
      [ $# -ge 2 ] || die "'$1' needs a value. $USAGE"
      case "$1" in
        --baseline) BASELINE="$2" ;;
        --head) HEAD_DIR="$2" ;;
        --reference) REF_DIR="$2" ;;
        --reference-tag) REF_TAG="$2" ;;
        --out) OUT="$2" ;;
      esac
      shift 2 ;;
    *) die "unrecognized argument '$1'. $USAGE" ;;
  esac
done
[ -n "$BASELINE" ] && [ -n "$HEAD_DIR" ] && [ -n "$REF_DIR" ] && [ -n "$REF_TAG" ] && [ -n "$OUT" ] \
  || die "$USAGE"
[ -f "$BASELINE" ] || die "cannot read baseline '$BASELINE'"
[ -d "$HEAD_DIR" ] || die "HEAD directory '$HEAD_DIR' does not exist"

# One line per fact: `type <T>`, `order <arm>`, `tag <tag>`, `timeout <s>`, `launch <n>`.
plan="$(python3 - "$BASELINE" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
abab = doc["abab"]
for key, value in sorted(doc["sources"].items()):
    if not key.startswith("_"):
        print(f"type {value}")
for arm in abab["order"]:
    print(f"order {arm}")
print(f"tag {abab['reference_tag']}")
print(f"timeout {abab['invocation_timeout_s']}")
launch = abab.get("launch_count")
# JSON true is a Python int and "2" would print as 2: only a real positive integer passes.
ok = isinstance(launch, int) and not isinstance(launch, bool) and launch > 0
print(f"launch {launch if ok else 'INVALID'}")
PY
)" || die "baseline '$BASELINE' declares no readable \"sources\" and \"abab\" (reference_tag, order, invocation_timeout_s, launch_count)"

globs=() order=() declared_tag="" timeout_s="" launch_count=""
while IFS=' ' read -r kind value; do
  case "$kind" in
    type) globs+=("${value}.*") ;;
    order) order+=("$value") ;;
    tag) declared_tag="$value" ;;
    timeout) timeout_s="$value" ;;
    launch) launch_count="$value" ;;
  esac
done <<< "$plan"
[ "${#globs[@]}" -gt 0 ] || die "baseline \"sources\" declares no type"
[ "${#order[@]}" -gt 0 ] || die "baseline \"abab.order\" is empty"
[ "$REF_TAG" = "$declared_tag" ] || die "--reference-tag '$REF_TAG' is not the declared abab.reference_tag '$declared_tag'"
timeout_s="${PERF_ABAB_INVOCATION_TIMEOUT_S:-$timeout_s}"
case "$timeout_s" in ''|*[!0-9]*|0) die "invocation timeout '$timeout_s' is not a positive integer of seconds" ;; esac
case "$launch_count" in
  ''|*[!0-9]*|0) die "baseline \"abab.launch_count\" must be a positive integer, not '${launch_count:-missing}'" ;;
esac
LIST_TIMEOUT_S=120
BENCH="Tests/Verbara.Sdk.Benchmarks"

arm_dir() { if [ "$1" = R ]; then printf '%s' "$REF_DIR"; else printf '%s' "$HEAD_DIR"; fi; }

if [ "${PERF_ABAB_DRY_RUN:-0}" = "1" ]; then
  for arm in R H; do echo "list $arm $(arm_dir "$arm") ${globs[*]}"; done
  pos=0
  for arm in "${order[@]}"; do
    pos=$((pos + 1))
    echo "run $pos $arm $(arm_dir "$arm") timeout=$timeout_s launchCount=$launch_count ${globs[*]}"
  done
  exit 0
fi

mkdir -p "$OUT" || die "cannot create '$OUT'"
OUT="$(cd "$OUT" && pwd)"
records="$OUT/.invocations.tsv"; : > "$records"
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

declare -A list_rc
for arm in R H; do
  dir="$(arm_dir "$arm")/$BENCH"
  (cd "$dir" && timeout --kill-after=30 "$LIST_TIMEOUT_S" \
     dotnet run -c Release --no-build -- --list flat --filter "${globs[@]}") \
     > "$OUT/list-$arm.txt" 2> "$OUT/list-$arm.err"
  list_rc[$arm]=$?
  echo "run-perf-abab: list-$arm.txt: $(wc -l < "$OUT/list-$arm.txt") line(s), exit ${list_rc[$arm]}"
done

pos=0
for arm in "${order[@]}"; do
  pos=$((pos + 1))
  label="$pos-$arm"
  dir="$(arm_dir "$arm")/$BENCH"
  echo "::group::ABAB position $pos ($arm) in $dir"
  started="$(now)"
  (cd "$dir" && timeout --kill-after=30 "$timeout_s" \
     dotnet run -c Release --no-build -- --filter "${globs[@]}" --exporters json \
       --launchCount "$launch_count" --artifacts "$OUT/$label")
  rc=$?
  ended="$(now)"
  echo "::endgroup::"
  timed_out=false
  if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ]; then
    timed_out=true
    echo "::warning::run-perf-abab: position $pos ($arm) timed out after ${timeout_s}s (exit $rc)"
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$pos" "$arm" "$started" "$ended" "$rc" "$timed_out" >> "$records"
done

head_sha="$(git -C "$HEAD_DIR" rev-parse --verify -q HEAD 2>/dev/null || true)"
ref_sha="$(git -C "$HEAD_DIR" rev-parse --verify -q "${REF_TAG}^{commit}" 2>/dev/null || true)"
ref_dir_sha="$(git -C "$REF_DIR" rev-parse --verify -q HEAD 2>/dev/null || true)"
changed="$OUT/.changed.txt"; changed_ok=0
if [ -n "$ref_sha" ] && [ -n "$head_sha" ] \
   && git -C "$HEAD_DIR" diff --name-only "$ref_sha" "$head_sha" > "$changed" 2>/dev/null; then
  changed_ok=1
fi
tags="$OUT/.tags.txt"; tags_ok=0
if git -C "$HEAD_DIR" ls-remote --tags --refs origin 'v*' > "$tags" 2>/dev/null; then tags_ok=1; fi

python3 - "$OUT/manifest.json" "$records" "$changed" "$changed_ok" "$tags" "$tags_ok" \
  "$REF_TAG" "$ref_sha" "$ref_dir_sha" "$head_sha" "${list_rc[R]}" "${list_rc[H]}" \
  "$launch_count" "${order[@]}" <<'PY'
import json, re, sys
(out, records, changed, changed_ok, tags, tags_ok, tag, ref_sha, ref_dir_sha, head_sha,
 list_r, list_h, launch_count, *order) = sys.argv[1:]
invocations = []
for line in open(records, encoding="utf-8").read().splitlines():
    pos, arm, started, ended, rc, timed_out = line.split("\t")
    invocations.append({"position": int(pos), "arm": arm, "dir": f"{pos}-{arm}",
                        "started": started, "ended": ended, "exit_code": int(rc),
                        "timed_out": timed_out == "true"})
bench = "Tests/Verbara.Sdk.Benchmarks/"
benchmark_paths = compiled_paths = None
if changed_ok == "1":
    paths = sorted(p for p in open(changed, encoding="utf-8").read().splitlines() if p)
    benchmark_paths = [p for p in paths if p.startswith(bench)]
    compiled_paths = [p for p in paths
                      if (p.startswith("src/") or p.startswith(bench) or "/" not in p)
                      and (p.endswith((".cs", ".csproj", ".props", ".targets"))
                           or p.rsplit("/", 1)[-1] == "global.json")]
newest = None
if tags_ok == "1":
    release = re.compile(r"refs/tags/(v([0-9]+)\.([0-9]+)\.([0-9]+))$")
    found = [m for m in (release.search(l.strip()) for l in open(tags, encoding="utf-8")) if m]
    if found:
        newest = max(found, key=lambda m: tuple(int(g) for g in m.groups()[1:])).group(1)
manifest = {"schema": 1, "reference_tag": tag, "reference_sha": ref_sha or None,
            "reference_dir_sha": ref_dir_sha or None, "head_sha": head_sha or None,
            "newest_tag": newest, "order": order, "launch_count": int(launch_count),
            "invocations": invocations,
            "lists": {"R": {"exit_code": int(list_r)}, "H": {"exit_code": int(list_h)}},
            "changed_benchmark_paths": benchmark_paths, "changed_compiled_paths": compiled_paths}
with open(out, "w", encoding="utf-8") as handle:
    json.dump(manifest, handle, indent=1)
print(f"run-perf-abab: manifest written, {len(invocations)} invocation(s), newest tag {newest}")
PY
rm -f "$records" "$changed" "$tags"
exit 0

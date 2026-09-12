#!/usr/bin/env bash
# test_package_validation_coverage.sh — unit tests for scripts/ci/check-package-validation-coverage.sh (ADR-0055
# addendum "all 29 packed, but only 17 were validated").
#
# The guard fires on a state that is invisible by construction: a project with package validation switched off, or
# silenced, packs exactly as green as one that validated and passed. Twelve projects sat in that state for months, so
# every verdict gets a case here, in both directions, and so does every way the evaluation or the feed can come back
# wrong.
#
# Two halves:
#   * THE LOGIC, always. MSBuild is replaced by a stand-in `dotnet` that answers from an `evaluated` file next to each
#     fixture project, and the feed by a stand-in `curl` that answers with a chosen HTTP status and body — the answers
#     nuget.org really gives, the 404 of a package that does not exist yet included. Each case states what MSBuild
#     evaluated and what the feed said; the guard has to reach the right verdict from them. Pure bash + jq, no .NET,
#     no network, a few seconds. Runs in the ALWAYS-RUN `Coverage Script Tests` job, next to the release-hygiene
#     harness and for the same reason.
#   * THE READ, with PKV_MSBUILD_CASES=1. The same guard against real `dotnet msbuild`, on fixtures that switch
#     validation off or silence it without the project ever saying <EnablePackageValidation>false — the cases a grep
#     cannot see, which is why the guard evaluates rather than greps — plus the two documented blind spots, pinned as
#     passing. Each fixture carries the repository's global.json, so it evaluates with the SDK the guard does. No
#     restore, no network. Runs in `Pack Warnings Gate`, where the guard itself runs. Without the variable these cases
#     are reported skipped.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUARD="$SCRIPT_DIR/../ci/check-package-validation-coverage.sh"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

CLEANUP=()
trap 'rm -rf "${CLEANUP[@]}"' EXIT
new_dir() { REPLY="$(mktemp -d)"; CLEANUP+=("$REPLY"); } # sets $REPLY; no subshell, so the cleanup list survives

BASELINE=1.0.0

# A stand-in for `dotnet msbuild <project> -getProperty:<name>... -getItem:PackageDownload`, answering from the file
# `evaluated` next to the project: `Name=Value` lines (first match wins), and `PackageDownload=<id>@<version>` lines as
# items. FAKE_MSBUILD=fail makes the evaluation fail, =garbage prints something that is not JSON, and =shape prints
# JSON without the Items section.
new_dir; FAKE="$REPLY/dotnet"
cat > "$FAKE" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[ "${1:-}" = msbuild ] || { echo "stand-in dotnet: expected 'msbuild', got '$*'" >&2; exit 64; }
project="$2"; shift 2
case "${FAKE_MSBUILD:-}" in
  fail)    echo "$project : error MSB4025: The project file could not be loaded." >&2; exit 1 ;;
  garbage) echo "Welcome to .NET! This is not JSON."; exit 0 ;;
  shape)   echo '{"Properties": {"IsPackable": "true", "PackageId": "Alpha"}}'; exit 0 ;;
esac
evaluated="$(dirname "$project")/evaluated"
props='{}'
for arg in "$@"; do
  case "$arg" in
    -getProperty:*)
      name="${arg#-getProperty:}"
      value="$(grep -m1 "^$name=" "$evaluated" | cut -d= -f2- || true)"
      props="$(jq -c --arg n "$name" --arg v "$value" '. + {($n): $v}' <<<"$props")" ;;
  esac
done
items='[]'
while IFS= read -r spec; do
  items="$(jq -c --arg i "${spec%@*}" --arg v "${spec#*@}" '. + [{Identity: $i, Version: $v}]' <<<"$items")"
done < <(sed -n 's/^PackageDownload=\(..*\)$/\1/p' "$evaluated")
jq -n --argjson p "$props" --argjson i "$items" '{Properties: $p, Items: {PackageDownload: $i}}'
EOF
chmod +x "$FAKE"

# A stand-in for `curl [-f] -o <file> -w '%{http_code}' <url>` that answers like the NuGet flat container, from
# $FAKE_FEED_DIR/<id>/ for a URL ending in /<id>/index.json: the status is `status` if present, else 200 when
# `index.json` exists and 404 when it does not; the body is `body`, else `index.json`, else nuget.org's 404 blob error.
# -f behaves as measured against nuget.org: on a status of 400 or more, no body file, the status still written by -w,
# exit 22. FAKE_FEED=down fails every request to connect (exit 7). Each URL asked is appended to
# $FAKE_FEED_DIR/requests.
new_dir; FAKE_CURL="$REPLY/curl"
cat > "$FAKE_CURL" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
out=''; format=''; fail=0; url=''
while [ $# -gt 0 ]; do
  case "$1" in
    -o|--output)              out="$2"; shift 2 ;;
    -w|--write-out)           format="$2"; shift 2 ;;
    -m|--max-time)            shift 2 ;;
    --fail|--fail-with-body)  fail=1; shift ;;
    --*)                      shift ;;
    -*)                       case "$1" in *f*) fail=1 ;; esac; shift ;;
    *)                        url="$1"; shift ;;
  esac
done
[ -n "$url" ] || { echo "stand-in curl: no URL" >&2; exit 2; }
echo "$url" >> "$FAKE_FEED_DIR/requests"
code() { [ -z "$format" ] || printf '%s' "${format//'%{http_code}'/$1}"; }
if [ "${FAKE_FEED:-}" = down ]; then
  code 000; echo "curl: (7) Failed to connect to the feed" >&2; exit 7
fi
dir="$FAKE_FEED_DIR/$(basename "$(dirname "$url")")"
if [ -f "$dir/status" ]; then status="$(cat "$dir/status")"
elif [ -f "$dir/index.json" ]; then status=200
else status=404; fi
if [ "$fail" -eq 1 ] && [ "$status" -ge 400 ]; then
  code "$status"; echo "curl: (22) The requested URL returned error: $status" >&2; exit 22
fi
if [ -f "$dir/body" ]; then cat "$dir/body"
elif [ -f "$dir/index.json" ]; then cat "$dir/index.json"
else printf '%s' '<?xml version="1.0" encoding="utf-8"?><Error><Code>BlobNotFound</Code></Error>'
fi > "${out:-/dev/stdout}"
code "$status"
EOF
chmod +x "$FAKE_CURL"

new_repo() { # new_repo [baseline] — a repo root: Directory.Build.props carrying the baseline, an empty src/, a feed
  new_dir; WORK="$REPLY"
  new_dir; FEED="$REPLY"
  mkdir -p "$WORK/src"
  cat > "$WORK/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup Condition="\$(MSBuildProjectDirectory.Contains('src'))">
    <EnablePackageValidation>true</EnablePackageValidation>
    <PackageValidationBaselineVersion>${1-$BASELINE}</PackageValidationBaselineVersion>
  </PropertyGroup>
</Project>
EOF
}

# project <name> [Name=Value ...] — a shipped project the evaluation reports as validated against $BASELINE, except
# where an override says otherwise. Overrides are written first and the stand-in takes the first match; any
# PackageDownload= override (an empty one means none) replaces the default download item.
project() {
  local name="$1" id="$1" download=1 kv; shift
  for kv in "$@"; do
    case "$kv" in PackageId=*) id="${kv#PackageId=}" ;; PackageDownload=*) download=0 ;; esac
  done
  mkdir -p "$WORK/src/$name"
  echo '<Project Sdk="Microsoft.NET.Sdk" />' > "$WORK/src/$name/$name.csproj"
  {
    for kv in "$@"; do printf '%s\n' "$kv"; done
    printf '%s\n' IsPackable=true "PackageId=$id" EnablePackageValidation=true \
                  "PackageValidationBaselineVersion=$BASELINE"
    if [ "$download" -eq 1 ]; then echo "PackageDownload=$id@[$BASELINE]"; fi
  } > "$WORK/src/$name/evaluated"
}
# optout <name> [Name=Value ...] — the shape the twelve had: EnablePackageValidation=false, so no baseline download.
optout() { local name="$1"; shift; project "$name" "$@" EnablePackageValidation=false PackageDownload=; }

feed_dir() { REPLY="$FEED/$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"; mkdir -p "$REPLY"; }
feed() { # feed <package id> <version>... — the index for that id answers 200, listing exactly these versions
  feed_dir "$1"; shift
  printf '%s\n' "$@" | jq -Rn '{versions: [inputs]}' > "$REPLY/index.json"
}
feed_raw() { # feed_raw <package id> <body> — the index answers 200 with exactly this body
  feed_dir "$1"; printf '%s' "$2" > "$REPLY/index.json"
}
feed_status() { # feed_status <package id> <status> [<body>] — the index answers with this status and body
  feed_dir "$1"; printf '%s' "$2" > "$REPLY/status"; printf '%s' "${3:-}" > "$REPLY/body"
}

# run <expected-exit> <description> [env assignments...] — the stand-in MSBuild and the stand-in feed unless an
# assignment overrides them (later assignments win). Leaves the output in $OUT for the caller to grep.
run() {
  local expected="$1" desc="$2"; shift 2
  local actual=0
  OUT="$(cd "$WORK" && env DOTNET_BIN="$FAKE" CURL_BIN="$FAKE_CURL" FAKE_FEED_DIR="$FEED" \
           FEED_BASE_URL=https://feed.invalid/v3-flatcontainer "$@" bash "$GUARD" 2>&1)" || actual=$?
  if [ "$actual" -eq "$expected" ]; then ok; else
    bad "$desc — expected exit $expected, got $actual"
    printf '%s\n' "$OUT" | sed 's/^/      | /'
  fi
}
says()   { case "$OUT" in *"$1"*) ok ;; *) bad "$2 — output did not mention '$1'" ;; esac; }
silent() { case "$OUT" in *"$1"*) bad "$2 — output mentioned '$1'" ;; *) ok ;; esac; }
lines()  { # lines <needle> <count> <description> — exactly <count> output lines contain <needle>
  local n; n="$(printf '%s\n' "$OUT" | grep -cF -- "$1" || true)"
  [ "$n" -eq "$2" ] && ok || bad "$3 — expected $2 line(s) containing '$1', got $n"
}
asked() { # asked <count> <description> — the stand-in feed received exactly <count> request(s)
  local n=0
  if [ -f "$FEED/requests" ]; then n="$(wc -l < "$FEED/requests" | tr -d ' ')"; fi
  [ "$n" -eq "$1" ] && ok || bad "$2 — expected $1 feed request(s), got $n"
}

# =============================================================================================
# contract
# =============================================================================================
[ -x "$GUARD" ] && ok || bad "check-package-validation-coverage.sh must be committed executable (mode 100755)"

# =============================================================================================
# the verdict, in both directions
# =============================================================================================
new_repo; project Alpha; project Beta
run 0 "no opt-outs passes"
says "all 2 shipped project(s) are validated against 1.0.0" "a clean tree says what it checked"
asked 0 "a clean tree asks the feed nothing"
run 0 "a clean tree passes with the feed down" FAKE_FEED=down
silent "::warning::" "a clean tree does not warn about a feed it never asked"

new_repo; project Alpha; optout Beta; feed Beta 0.9.0 1.0.0
run 1 "an opt-out whose package is on the feed at the baseline fails"
says "FAILED" "the violation is named"
says "src/Beta/Beta.csproj" "the failure names the project"
says "package:  Beta, listed at 1.0.0" "the failure names the package id and the version it found"
says "EnablePackageValidation is 'false'" "the failure says what switched validation off"
says "Fix: remove the opt-out" "the failure says how to fix it"
silent "src/Alpha/Alpha.csproj" "a validated project is not reported"

# A new package keeps working: the flat container answers 404 for an id with no version at all.
new_repo; project Alpha; optout Beta
run 0 "an opt-out whose package the feed answers 404 for passes (a new package)"
says "Allowed to skip it" "an allowed opt-out is still reported, not hidden"
says "src/Beta/Beta.csproj (Beta)" "the allowed opt-out names its project and package"
silent "::warning::" "a 404 is an answer about the package, not a sign the feed is down"

# On the feed, but not at the baseline — so there is nothing to download and compare against yet.
new_repo; project Alpha; optout Beta; feed Beta 0.9.0 1.1.0
run 0 "an opt-out whose package lists other versions but not the baseline passes"
says "Allowed to skip it" "a package missing at the baseline is allowed"

# Every offender is named, not only the first, and an allowed one is not among them.
new_repo; project Alpha; optout Beta; optout Gamma; optout Delta; feed Beta 1.0.0; feed Gamma 1.0.0
run 1 "every offender is reported, not only the first"
says "FAILED — 2 shipped project(s)" "the failure counts the offenders"
lines "    package:  " 2 "exactly the two offenders get a failure block"
says "src/Delta/Delta.csproj (Delta)" "the allowed opt-out is listed alongside"

# The feed is asked about the PackageId, lower-cased — not the project file's name.
new_repo; optout Beta PackageId=Vendor.Beta.Core; feed Vendor.Beta.Core 1.0.0
run 1 "the feed is asked about PackageId (lower-cased), not the project name"
says "package:  Vendor.Beta.Core" "the failure names the package id"
grep -qxF 'https://feed.invalid/v3-flatcontainer/vendor.beta.core/index.json' "$FEED/requests" \
  && ok || bad "the lookup is <base>/<lowercase id>/index.json"

# =============================================================================================
# the feed: the answers nuget.org gives, through the stand-in curl
# =============================================================================================
# A 404 settles one package and nothing else. Alpha sorts first and is new; Beta after it is stale. A guard that let
# the 404 end its lookups (curl -f turns it into exit 22, which reads like a dead feed) would pass Beta with a warning.
new_repo; optout Alpha; optout Beta; feed Beta 1.0.0
run 1 "a 404 does not stop the lookups after it: a stale opt-out sorting later is still caught"
says "package:  Beta, listed at 1.0.0" "the later offender is caught"
says "src/Alpha/Alpha.csproj (Alpha)" "the new package before it is still allowed"
silent "::warning::" "a 404 does not read as an unreachable feed"
asked 2 "both packages are looked up"

new_repo; project Alpha; optout Beta; feed Beta 1.0.0; feed_status Beta 503 'Service Unavailable'
run 0 "a 5xx is a warning, never a failure"
says "::warning::Could not reach https://feed.invalid/v3-flatcontainer" "a 5xx warns"
says "not checked against the feed: Beta" "the warning names what went unchecked"
silent "Allowed to skip it" "a 5xx is not a 404: nothing is allowed on it"

new_repo; optout Alpha; optout Beta; feed_status Alpha 503 'Service Unavailable'; feed Beta 1.0.0
run 1 "a 5xx for one package does not stop the lookups after it"
says "package:  Beta" "the later offender is caught"
says "not checked against the feed: Alpha" "the package that got the 5xx is still reported unchecked"

new_repo; optout Alpha; optout Beta; feed Alpha 1.0.0; feed Beta 1.0.0
run 0 "a feed that cannot be connected to is a warning, never a failure" FAKE_FEED=down
says "not checked against the feed: Alpha Beta" "the warning names every package it could not check"
asked 1 "after one failed connection the remaining lookups are not attempted"

# The same path through the real curl binary: a closed port.
new_repo; project Alpha; optout Beta
run 0 "real curl against a closed port is a warning, never a failure" CURL_BIN=curl FEED_BASE_URL=http://127.0.0.1:1
says "::warning::Could not reach http://127.0.0.1:1" "an unreachable feed warns"

new_repo; project Alpha; optout Beta; feed_raw Beta '<html>Too Many Requests</html>'
run 2 "a 200 whose body is not JSON is 'cannot tell', not a pass"
says "not a list of versions" "an unreadable answer is named"
new_repo; project Alpha; optout Beta; feed_raw Beta '{"items": []}'
run 2 "JSON without a versions list is 'cannot tell', not a pass"
new_repo; project Alpha; optout Beta; feed_raw Beta '{"versions": "1.0.0"}'
run 2 "a versions field that is not a list is 'cannot tell', not a pass"
# The baseline comes first, so a guard that stopped checking the types would find it and answer "on the feed".
new_repo; project Alpha; optout Beta; feed_raw Beta '{"versions": ["1.0.0", 2]}'
run 2 "a versions list holding anything but strings is 'cannot tell', not a verdict"
new_repo; project Alpha; optout Beta; feed_raw Beta ''
run 2 "an empty 200 is 'cannot tell', not a pass"

# A definite failure is not traded for "cannot tell" because another package's answer was unreadable.
new_repo; optout Beta; optout Gamma; feed Beta 1.0.0; feed_raw Gamma 'nope'
run 1 "a violation still fails when another package's answer is unreadable"
says "src/Gamma/Gamma.csproj (Gamma): https://feed.invalid" "the unreadable answer is reported alongside the failure"

# =============================================================================================
# the baseline
# =============================================================================================
new_repo ''; project Alpha
run 2 "an unreadable baseline is 'cannot tell', not 'fine'"
# A baseline that is not a version would miss on every feed lookup and wave every opt-out through as new.
new_repo '$(ReleasedVersion)'; optout Beta; feed Beta 1.0.0
run 2 "a baseline that is not a version is 'cannot tell', not a pass"

# =============================================================================================
# reading the evaluation: the value the SDK compares, compared the way the SDK compares it
# =============================================================================================
# The SDK's condition is '$(EnablePackageValidation)' == 'true' and MSBuild compares strings case-insensitively, so
# `True` validates and must not be flagged, while `FALSE` must.
new_repo; project Alpha EnablePackageValidation=True; feed Alpha 1.0.0
run 0 "EnablePackageValidation 'True' is on (case-insensitive, as MSBuild compares)"
new_repo; optout Beta EnablePackageValidation=FALSE; feed Beta 1.0.0
run 1 "EnablePackageValidation 'FALSE' is off"
# Untrimmed: ' true ' fails that comparison, so validation is off although the word is 'true'. A grep for 'false'
# passes this; the evaluated value cannot.
new_repo; project Beta 'EnablePackageValidation= true ' PackageDownload=; feed Beta 1.0.0
run 1 "EnablePackageValidation ' true ' is off (the comparison is not trimmed)"
says "EnablePackageValidation is ' true '" "the offending value is shown with its whitespace"
# A download item on its own is not validation: a switched-off project that lists one is still switched off.
new_repo; project Beta EnablePackageValidation=false; feed Beta 1.0.0
run 1 "a baseline download item does not make a switched-off project validated"

# Every other way the SDK stops comparing against the baseline, as the evaluation reports it.
new_repo; project Beta DisablePackageBaselineValidation=true PackageDownload=; feed Beta 1.0.0
run 1 "DisablePackageBaselineValidation=true skips the baseline"
says "DisablePackageBaselineValidation is 'true'" "the failure names the property"
new_repo; project Beta PackageValidationBaselineVersion=0.9.0 'PackageDownload=Beta@[0.9.0]'; feed Beta 0.9.0 1.0.0
run 1 "a per-project baseline override compares against the wrong release"
says "evaluates to '0.9.0', not the repository baseline 1.0.0" "the failure names the override"
new_repo; project Beta PackageValidationBaselineName=Other.Pkg 'PackageDownload=Other.Pkg@[1.0.0]'; feed Beta 1.0.0
run 1 "a baseline name other than the package id compares against another package"
new_repo; project Beta PackageValidationBaselinePath=baseline/Beta.1.0.0.nupkg PackageDownload=; feed Beta 1.0.0
run 1 "a baseline path compares against a file, not the published release"
# NuGet ids are case-insensitive, and so is the match on the download item.
new_repo; project Beta 'PackageDownload=beta@[1.0.0]'; feed Beta 1.0.0
run 0 "the download item's id matches case-insensitively"

# RunApiCompat and the suppression-file switch reach the validation task as bool parameters, so they read the way
# MSBuild converts a boolean (measured): true/on/yes/!false/!off/!no and false/off/no/!true/!on/!yes, any case.
for v in false False off no NO '!true' '!on' '!yes'; do
  new_repo; project Beta "RunApiCompat=$v"; feed Beta 1.0.0
  run 1 "RunApiCompat '$v' reads as false: validation runs and compares nothing"
done
says "RunApiCompat is '!yes', which MSBuild reads as false" "the failure names the property and how it reads"
for v in '' true on yes '!false' '!off' '!no'; do
  new_repo; project Alpha "RunApiCompat=$v"; feed Alpha 1.0.0
  run 0 "RunApiCompat '$v' leaves the comparison running"
done
new_repo; project Alpha 'RunApiCompat= no '; feed Alpha 1.0.0
run 0 "a RunApiCompat MSBuild cannot convert (' no ') fails the pack itself with MSB4030, so it is not reported here"

for v in true TRUE on yes '!false' '!off' '!no'; do
  new_repo; project Beta "ApiCompatGenerateSuppressionFile=$v"; feed Beta 1.0.0
  run 1 "ApiCompatGenerateSuppressionFile '$v' reads as true: every difference is suppressed on the build machine"
done
says "ApiCompatGenerateSuppressionFile is '!no', which MSBuild reads as true" "the failure names the property and how it reads"
for v in false off no '!true'; do
  new_repo; project Alpha "ApiCompatGenerateSuppressionFile=$v"; feed Alpha 1.0.0
  run 0 "ApiCompatGenerateSuppressionFile '$v' generates nothing"
done
# The older name, which the SDK copies onto the newer one inside a target, and only while the newer one is empty.
for v in true yes; do
  new_repo; project Beta "GenerateCompatibilitySuppressionFile=$v"; feed Beta 1.0.0
  run 1 "GenerateCompatibilitySuppressionFile '$v' (the older name) reads as true"
done
says "GenerateCompatibilitySuppressionFile (the older name of ApiCompatGenerateSuppressionFile) is 'yes'" \
  "the failure names the older property"
new_repo; project Alpha GenerateCompatibilitySuppressionFile=false; feed Alpha 1.0.0
run 0 "GenerateCompatibilitySuppressionFile 'false' generates nothing"
new_repo; project Alpha ApiCompatGenerateSuppressionFile=false GenerateCompatibilitySuppressionFile=true; feed Alpha 1.0.0
run 0 "the older name is ignored once the newer one is set, as the SDK's copy is"

# =============================================================================================
# what counts as shipped, and an evaluation that cannot be trusted
# =============================================================================================
new_repo; project Alpha; optout Tool IsPackable=false; feed Tool 1.0.0
run 0 "a project that does not pack is not a shipped project"
new_repo
run 2 "no project under src/ is 'cannot tell', not 'fine'"
new_repo; optout Tool IsPackable=false
run 2 "nothing packable under src/ is 'cannot tell': the evaluation is not seeing what pack sees"
new_repo; project Alpha
run 2 "an evaluation MSBuild cannot complete is 'cannot tell'" FAKE_MSBUILD=fail
says "MSB4025" "the MSBuild error is shown"
run 2 "evaluation output that is not JSON is 'cannot tell', not a pass" FAKE_MSBUILD=garbage
run 2 "evaluation JSON of the wrong shape is 'cannot tell', not a pass" FAKE_MSBUILD=shape

# =============================================================================================
# the read itself: real MSBuild (PKV_MSBUILD_CASES=1)
# =============================================================================================
# Each fixture has the repository's own shape — validation on and the baseline set for src/ in Directory.Build.props —
# plus its global.json, one project, src/Foo, and Foo on the feed at the baseline, so any switch-off is a violation.
# Only the literal control writes <EnablePackageValidation>false</EnablePackageValidation> in the project; that is the
# point.
if [ "${PKV_MSBUILD_CASES:-0}" = 1 ]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    bad "PKV_MSBUILD_CASES=1 but there is no dotnet on PATH"
  elif [ ! -f "$REPO_ROOT/global.json" ]; then
    bad "PKV_MSBUILD_CASES=1 but $REPO_ROOT/global.json is missing, so the fixtures cannot pin the guard's SDK"
  else
    real_repo() { # real_repo <project PropertyGroup body> [<file relative to the root> <content>]
      new_repo
      cp "$REPO_ROOT/global.json" "$WORK/global.json"
      mkdir -p "$WORK/src/Foo"
      printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n%s\n  </PropertyGroup>\n</Project>\n' \
        "$1" > "$WORK/src/Foo/Foo.csproj"
      if [ $# -ge 3 ]; then mkdir -p "$(dirname "$WORK/$2")"; printf '%s\n' "$3" > "$WORK/$2"; fi
      feed Foo 1.0.0
    }
    real() { run "$1" "real MSBuild: $2" DOTNET_BIN=dotnet; }

    real_repo ''
    real 0 "validation switched on for src/ in Directory.Build.props is validated (the positive control)"
    real_repo '    <EnablePackageValidation>false</EnablePackageValidation>'
    real 1 "the literal opt-out the twelve projects carried"
    real_repo '    <EnablePackageValidation>True</EnablePackageValidation>'
    real 0 "'True' is on: the SDK compares case-insensitively"
    real_repo '    <EnablePackageValidation> true </EnablePackageValidation>'
    real 1 "' true ' is off: the SDK does not trim, and a grep for 'false' passes it"
    real_repo '' src/Foo/Directory.Build.props '<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Build.props, $(MSBuildThisFileDirectory)..))" />
  <PropertyGroup><EnablePackageValidation>False</EnablePackageValidation></PropertyGroup>
</Project>'
    real 1 "an opt-out in a nested Directory.Build.props, nowhere in the project"
    real_repo '    <Released>false</Released>
    <EnablePackageValidation>$(Released)</EnablePackageValidation>'
    real 1 "an opt-out through another property"
    real_repo '    <PackAsTool>true</PackAsTool>'
    real 1 "PackAsTool: the SDK switches validation off by itself"
    real_repo '' Directory.Build.targets '<Project>
  <PropertyGroup><DisablePackageBaselineValidation>true</DisablePackageBaselineValidation></PropertyGroup>
</Project>'
    real 1 "DisablePackageBaselineValidation from Directory.Build.targets"
    real_repo '    <PackageValidationBaselineVersion>0.9.0</PackageValidationBaselineVersion>'
    real 1 "a per-project baseline override"
    real_repo '    <RunApiCompat>off</RunApiCompat>'
    real 1 "RunApiCompat 'off': validation runs and compares nothing"
    real_repo '' Directory.Build.targets '<Project>
  <PropertyGroup><ApiCompatGenerateSuppressionFile>yes</ApiCompatGenerateSuppressionFile></PropertyGroup>
</Project>'
    real 1 "ApiCompatGenerateSuppressionFile 'yes' from Directory.Build.targets"
    real_repo '' src/Foo/Directory.Build.props '<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Build.props, $(MSBuildThisFileDirectory)..))" />
  <PropertyGroup><GenerateCompatibilitySuppressionFile>true</GenerateCompatibilitySuppressionFile></PropertyGroup>
</Project>'
    real 1 "GenerateCompatibilitySuppressionFile 'true' (the older name) in a nested Directory.Build.props"

    # The documented blind spots, pinned as passing so the documentation cannot drift from the behaviour. Both are
    # decided after evaluation: pack compares nothing for either, and evaluating the project cannot show it.
    real_repo '' Directory.Build.targets '<Project>
  <Target Name="SwitchValidationOff" BeforeTargets="Pack">
    <PropertyGroup><EnablePackageValidation>false</EnablePackageValidation></PropertyGroup>
  </Target>
</Project>'
    real 0 "blind spot (documented): a target that switches validation off before Pack passes"
    real_repo "    <EnablePackageValidation Condition=\"'\$(_IsPacking)' == 'true'\">false</EnablePackageValidation>"
    real 0 "blind spot (documented): a condition on _IsPacking, the global property dotnet pack sets, passes"
  fi
else
  echo "SKIP: the real-MSBuild cases (PKV_MSBUILD_CASES=1 runs them; Pack Warnings Gate does)"
fi

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"

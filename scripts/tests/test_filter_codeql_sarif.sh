#!/usr/bin/env bash
# test_filter_codeql_sarif.sh — unit tests for scripts/ci/filter-codeql-sarif.sh (ADR-0051 addendum, 2026-09-12).
#
# The filter runs only in codeql.yml's `Analyze (C#)` job, between analysis and upload, and a wrong rule turns
# nothing red: remove too much and alerts about this repository's own code, or security alerts anywhere, close as
# "fixed" with nobody having fixed them; remove too little and the .NET generator alerts come back. So every rule
# gets a case in both directions, each beside a control result in our own code that must survive, and this runs in
# the ALWAYS-RUN `Coverage Script Tests` job beside the other guard harnesses — on the very PR that edits the filter.
#
# Fixtures are SARIF 2.1.0 logs in the shape the CodeQL CLI writes (a uri relative to %SRCROOT% plus an index into
# run.artifacts; the rules in tool.extensions[0] beside an empty driver, each result naming its rule by id and by
# toolComponent + index), built with jq. The .NET generator paths, and the metadata of the cs/* rules, are real
# ones from main's analysis of 99162772. Pure bash + jq (+ git for the project-name guard), no network, no .NET.
# FILTER_CODEQL_SARIF runs every case against another copy of the filter: that is how a negative control shows the
# cases can fail.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FILTER="${FILTER_CODEQL_SARIF:-$SCRIPT_DIR/../ci/filter-codeql-sarif.sh}"
CODEQL_WORKFLOW="$REPO_ROOT/.github/workflows/codeql.yml"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

WORK="$(mktemp -d)"
trap 'chmod -R u+rwx "$WORK" 2>/dev/null || true; rm -rf "$WORK"' EXIT
IN="$WORK/in.sarif"
OUT="$WORK/out.sarif"
SCHEMA=https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json

# Code this repository owns: the control beside every case, which no case may remove.
OURS=src/Verbara.Sdk.Ami.SourceGenerators/EventRegistryGenerator.cs
# .NET SDK generator output, located exactly as main's analysis of 99162772 located it.
STJ=src/Verbara.Sdk.Ari/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/AriJsonContext.ApplicationMoveFailedEvent.g.cs
OPTIONS=src/Verbara.Sdk.Ami/obj/Release/net10.0/generated/Microsoft.Extensions.Options.SourceGeneration/Microsoft.Extensions.Options.Generators.OptionsValidatorGenerator/Validators.g.cs
REGEX=src/Verbara.Sdk.Sessions.Postgres/obj/Release/net10.0/generated/System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs
EXAMPLE=Examples/SessionExtensionsExample/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/FileStoreJsonContext.CallSessionDto.g.cs
# The [LoggerMessage] generator, where a data-flow security alert could end with its sink in generated code.
LOGGING=src/Verbara.Sdk.Ari/obj/Release/net10.0/generated/Microsoft.Extensions.Logging.Generators/Microsoft.Extensions.Logging.Generators.LoggerMessageGenerator/LoggerMessage.g.cs
# This repository's own generator writes into the same tree, and that output must stay analysed.
AMI_GEN_DIR=src/Verbara.Sdk.Ami/obj/Release/net10.0/generated/Verbara.Sdk.Ami.SourceGenerators/Verbara.Sdk.Ami.SourceGenerators.EventRegistryGenerator
AMI_GEN=$AMI_GEN_DIR/GeneratedEventRegistry.g.cs
# The segments every "kept" spelling below is a near miss of.
TAIL=System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/AriJsonContext.g.cs
ARI=src/Verbara.Sdk.Ari

# The rules of every log that `log` writes, in tool.extensions[0] ("codeql/csharp-queries") as the CLI writes them.
# The first six carry the metadata main's analysis gave them: five maintainability or correctness rules, and
# cs/log-forging, a security rule with both markers. Each test/* rule isolates one marker, or none.
RULES="$(jq -cn '[
  { id: "cs/useless-cast-to-self", properties: { tags: ["external/cwe/cwe-561", "maintainability", "quality", "useless-code"] } },
  { id: "cs/useless-upcast", properties: { tags: ["external/cwe/cwe-561", "maintainability", "quality", "useless-code"] } },
  { id: "cs/missed-ternary-operator", properties: { tags: ["language-features", "maintainability", "quality", "readability"] } },
  { id: "cs/path-combine", properties: { tags: ["correctness", "quality", "reliability"] } },
  { id: "cs/nested-if-statements", properties: { tags: ["language-features", "maintainability", "quality", "readability"] } },
  { id: "cs/log-forging", properties: { tags: ["external/cwe/cwe-117", "security"], "security-severity": "6.1" } },
  { id: "test/security-tag-only", properties: { tags: ["external/cwe/cwe-079", "security"] } },
  { id: "test/security-severity-only", properties: { tags: ["correctness"], "security-severity": "7.5" } },
  { id: "test/no-properties" }
]')"

# result <rule-id> <uri> — one result whose primary location is <uri>, in the CLI's shape: its rule named by id and
# by toolComponent 0 + its index in $RULES. An id that is not in $RULES gets no index, so only its id can match.
result() {
  jq -cn --arg rule "$1" --arg uri "$2" --argjson rules "$RULES" '
    (first(range(0; $rules | length) | select($rules[.].id == $rule)) // null) as $index
    | { ruleId: $rule,
        rule: ({ id: $rule, toolComponent: { index: 0 } } + (if $index == null then {} else { index: $index } end)),
        level: "note", message: { text: "a finding" },
        locations: [ { physicalLocation: { artifactLocation: { uri: $uri, uriBaseId: "%SRCROOT%", index: 0 },
                                           region: { startLine: 3, startColumn: 5, endColumn: 9 } } } ],
        partialFingerprints: { primaryLocationLineHash: "a771138decb78ce4:1" } }'
}

# located <reference-json> [<uri>] — a result at <uri> (default: System.Text.Json output) whose rule reference is
# exactly <reference-json>, merged in: `{}` names no rule at all.
located() {
  jq -cn --arg uri "${2:-$STJ}" --argjson reference "$1" \
    '{ message: { text: "m" }, locations: [ { physicalLocation: { artifactLocation: { uri: $uri } } } ] } + $reference'
}

# log <result>... — writes $IN: one SARIF 2.1.0 log whose one run holds the given results, in order, beside the
# other things a CLI run carries — so "every other field unchanged" is checked against something.
log() {
  printf '%s\n' "$@" | jq -cs --arg schema "$SCHEMA" --arg ours "$OURS" --argjson rules "$RULES" '
    { "$schema": $schema, version: "2.1.0",
      runs: [ { tool: { driver: { name: "CodeQL", semanticVersion: "2.27.0", rules: [] },
                        extensions: [ { name: "codeql/csharp-queries", semanticVersion: "1.9.3", rules: $rules },
                                      { name: "codeql/csharp-all", semanticVersion: "7.3.0", rules: [] } ] },
                invocations: [ { executionSuccessful: true, toolExecutionNotifications: [] } ],
                artifacts: [ { location: { uri: $ours, uriBaseId: "%SRCROOT%", index: 0 } } ],
                automationDetails: { id: "/language:csharp/" },
                versionControlProvenance: [ { repositoryUri: "https://github.invalid/repo", revisionId: "99162772" } ],
                properties: { "semmle.formatSpecifier": "sarif-latest" },
                results: . } ] }' > "$IN"
}

# patch <jq> — rewrites $IN with <jq> applied.
patch() { jq -c "$1" "$IN" > "$WORK/patched.sarif"; cp "$WORK/patched.sarif" "$IN"; }

# run <expected-exit> <description> <args>... — runs the filter, asserts its exit status, leaves its output in $LOG.
run() {
  local expected="$1" desc="$2" actual=0
  shift 2
  LOG="$(bash "$FILTER" "$@" 2>&1)" || actual=$?
  if [ "$actual" -eq "$expected" ]; then ok; else
    bad "$desc — expected exit $expected, got $actual"
    printf '%s\n' "$LOG" | sed 's/^/      | /'
  fi
}

says()  { case "$LOG" in *"$1"*) ok ;; *) bad "$2 — output did not mention '$1'"; printf '%s\n' "$LOG" | sed 's/^/      | /' ;; esac; }
lacks() { case "$LOG" in *"$1"*) bad "$2 — output mentioned '$1'"; printf '%s\n' "$LOG" | sed 's/^/      | /' ;; *) ok ;; esac; }

# warned <text> <description> — the output has exactly one ::warning:: line, and that line contains <text>.
warned() {
  local lines count
  lines="$(grep '^::warning::' <<<"$LOG" || true)"
  count="$(grep -c '^::warning::' <<<"$LOG" || true)"
  if [ "$count" = 1 ] && case "$lines" in *"$1"*) true ;; *) false ;; esac; then ok; else
    bad "$2 — expected exactly one ::warning:: containing '$1', got $count"
    printf '%s\n' "$LOG" | sed 's/^/      | /'
  fi
}

# expect_output <jq-transform> <description> — $OUT is exactly $IN with <transform> applied: same values, same order.
expect_output() {
  if [ -f "$OUT" ] && jq -e --slurpfile src "$IN" "(\$src[0] | $1) == ." "$OUT" >/dev/null 2>&1; then ok; else
    bad "$2 — the output is not the input with '$1' applied"
    if [ -f "$OUT" ]; then
      jq -c '[.runs[] | [(.results // [])[] | .locations[0].physicalLocation.artifactLocation.uri]]' "$OUT" 2>/dev/null \
        | sed 's/^/      | kept: /' || true
    else
      echo "      | (no output file)"
    fi
  fi
}

# fails_closed <description> <args>... — exit 2, an ::error:: line, and no file at $OUT afterwards, even though
# one was there before: a stale log from an earlier run must never be left to be uploaded.
fails_closed() {
  local desc="$1"
  shift
  echo 'stale' > "$OUT"
  run 2 "$desc" "$@"
  if grep -q '^::error::filter-codeql-sarif: cannot tell' <<<"$LOG"; then ok; else bad "$desc — no ::error:: line"; fi
  if [ -e "$OUT" ]; then bad "$desc — a file was left at the output path"; else ok; fi
}

# bad_input <content> <description> — $IN holds exactly <content>.
bad_input() {
  printf '%s' "$1" > "$IN"
  fails_closed "$2" "$IN" "$OUT"
}

no_temp_left() {
  if compgen -G "$OUT.*" >/dev/null; then bad "$1 — a temporary file was left beside the output"; else ok; fi
}

# =============================================================================================
# contract
# =============================================================================================
[ -x "$FILTER" ] && ok || bad "filter-codeql-sarif.sh must be committed executable (mode 100755)"
[ -x "${BASH_SOURCE[0]}" ] && ok || bad "test_filter_codeql_sarif.sh must be committed executable (mode 100755)"

# codeql.yml's wiring. Each of these breaks silently if edited away: an analyze that uploads puts the unfiltered
# log in first, an upload of the raw directory skips the filter, and a category that differs between the two
# steps starts a second analysis and strands every existing alert open under the first. Lines are compared with
# their indentation (and a leading "- ") trimmed, so a comment that quotes one never counts as the step itself.
# lines <text> — trimmed lines equal to <text>; starts <text> — trimmed lines beginning with <text>.
lines()  { awk -v want="$1" '{ l = $0; sub(/^[ \t]*(- )?/, "", l); sub(/[ \t]+$/, "", l); if (l == want) n++ } END { print n + 0 }' "$CODEQL_WORKFLOW"; }
starts() { awk -v want="$1" '{ l = $0; sub(/^[ \t]*(- )?/, "", l); if (index(l, want) == 1) n++ } END { print n + 0 }' "$CODEQL_WORKFLOW"; }
[ "$(starts 'uses: github/codeql-action/analyze@')" = 1 ] && ok || bad "codeql.yml must call analyze exactly once"
[ "$(starts 'uses: github/codeql-action/upload-sarif@')" = 1 ] && ok || bad "codeql.yml must call upload-sarif exactly once"
[ "$(starts 'upload:')" = 1 ] && [ "$(lines 'upload: failure-only')" = 1 ] && ok \
  || bad "analyze must write its SARIF without uploading it on success (exactly one 'upload: failure-only')"
[ "$(starts 'category:')" = 2 ] && [ "$(lines 'category: "/language:csharp"')" = 2 ] && ok \
  || bad "analyze and upload-sarif must both carry category \"/language:csharp\", and no other category may appear"
[ "$(lines 'output: ${{ runner.temp }}/codeql-sarif')" = 1 ] && ok || bad "analyze must write to \${{ runner.temp }}/codeql-sarif"
[ "$(lines 'RAW_SARIF_DIR: ${{ runner.temp }}/codeql-sarif')" = 1 ] && ok || bad "the filter step must read \${{ runner.temp }}/codeql-sarif"
[ "$(lines 'FILTERED_SARIF_DIR: ${{ runner.temp }}/codeql-sarif-filtered')" = 1 ] && ok || bad "the filter step must write \${{ runner.temp }}/codeql-sarif-filtered"
[ "$(starts 'sarif_file:')" = 1 ] && [ "$(lines 'sarif_file: ${{ runner.temp }}/codeql-sarif-filtered')" = 1 ] && ok \
  || bad "upload-sarif must upload the filtered directory, never the raw one"
[ "$(starts 'bash ./scripts/ci/filter-codeql-sarif.sh ')" = 1 ] && ok || bad "codeql.yml must run the filter"

# The rule removes output by generator ASSEMBLY name, so a project here named System.* or Microsoft.* would have its
# own generator output hidden from analysis. None is; this fails the PR that adds one.
if projects="$(git -C "$REPO_ROOT" ls-files -- '*.csproj' 2>/dev/null)" && [ -n "$projects" ]; then
  offenders="$(printf '%s\n' "$projects" | awk -F/ '{ print $NF }' | grep -E '^(System|Microsoft)\.' || true)"
  [ -z "$offenders" ] && ok || bad "a project named System.* or Microsoft.* would have its generator output filtered: $offenders"
  offenders="$(git -C "$REPO_ROOT" grep -hE '<AssemblyName>[[:space:]]*(System|Microsoft)\.' -- '*.csproj' '*.props' '*.targets' || true)"
  [ -z "$offenders" ] && ok || bad "an <AssemblyName> starting with System. or Microsoft. would have its generator output filtered: $offenders"
else
  bad "git ls-files found no project in $REPO_ROOT, so the System.*/Microsoft.* project-name guard could not run"
fi

# =============================================================================================
# location, removed — .NET SDK generator output, wherever the build put it
# =============================================================================================
# removed <uri> <why> / kept <uri> <why> — beside the control, a non-security result at <uri> is removed (or kept),
# the rest of the log is exactly as it was, and nothing is warned about.
removed() {
  log "$(result cs/useless-cast-to-self "$OURS")" "$(result cs/useless-upcast "$1")"
  run 0 "removed: $2" "$IN" "$OUT"
  expect_output 'del(.runs[0].results[1])' "removed: $2 ($1)"
  lacks "::warning::" "removed: $2 ($1)"
}
kept() {
  log "$(result cs/useless-cast-to-self "$OURS")" "$(result cs/useless-upcast "$1")"
  run 0 "kept: $2" "$IN" "$OUT"
  expect_output '.' "kept: $2 ($1)"
  lacks "::warning::" "kept: $2 ($1)"
}

removed "$STJ" "System.Text.Json generator output under src/<project>/obj/"
removed "$OPTIONS" "Microsoft.Extensions.Options validator output"
removed "$REGEX" "System.Text.RegularExpressions generator output"
removed "$EXAMPLE" "generator output of a sample under Examples/"
removed "obj/Release/net10.0/generated/$TAIL" "generator output under a root obj/"
removed "$ARI/obj/Debug/net10.0/generated/$TAIL" "a Debug build"
removed "$ARI/obj/generated/$TAIL" "generated/ directly under obj/"
removed "$ARI/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/nested/AriJsonContext.g.cs" \
  "a hint name inside a subfolder"
removed "$ARI/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/generated/Verbara.Sdk.Ari/AriJsonContext.g.cs" \
  "a .NET generator hint name with a generated/ subfolder of its own — the first generated/ after obj/ decides"
removed "$LOGGING" "the [LoggerMessage] generator — any Microsoft.* generator, not only those seen on main"
removed "file:///w/repo/$ARI/obj/Release/net10.0/generated/$TAIL" "an absolute file: uri"

# The shape GitHub stores after upload: no uriBaseId, no index, and the rule named by ruleId alone.
log "$(result cs/useless-cast-to-self "$OURS")" "$(located '{"ruleId": "cs/useless-upcast"}')"
run 0 "removed: a location with a uri and nothing else" "$IN" "$OUT"
expect_output 'del(.runs[0].results[1])' "removed: a location with a uri and nothing else"

# Only the primary location decides — here it is generator output, and a related location in our code saves nothing.
log "$(result cs/useless-cast-to-self "$OURS")" \
    "$(jq -cn --arg stj "$STJ" --arg ours "$OURS" '{ ruleId: "cs/useless-upcast", message: { text: "m" },
        locations: [ { physicalLocation: { artifactLocation: { uri: $stj } } } ],
        relatedLocations: [ { id: 1, physicalLocation: { artifactLocation: { uri: $ours } } } ] }')"
run 0 "removed: primary location in generator output, related location in our code" "$IN" "$OUT"
expect_output 'del(.runs[0].results[1])' "removed: primary location in generator output, related location in our code"

# =============================================================================================
# location, kept — our own generator, and every near miss of the rule
# =============================================================================================
kept "$AMI_GEN" "this repository's own generator output (Verbara.Sdk.Ami.SourceGenerators)"
kept "$AMI_GEN_DIR/generated/System.Events/Registry.g.cs" \
  "our generator's output under a hint-name subfolder generated/System.*/ — only the first generated/ after obj/ counts"
kept "$AMI_GEN_DIR/obj/generated/System.Events/Registry.g.cs" \
  "our generator's output under a hint-name subfolder obj/generated/System.*/ — only the first obj/ counts"
kept "$OURS" "ordinary source"
kept "objects/Release/net10.0/generated/$TAIL" "a root objects/ directory"
kept "$ARI/objects/Release/net10.0/generated/$TAIL" "objects/ inside a project"
kept "$ARI/myobj/Release/net10.0/generated/$TAIL" "a directory whose name ends in obj"
kept "$ARI/obj.bak/Release/net10.0/generated/$TAIL" "a directory whose name starts with obj"
kept "src/obj.cs" "a file named obj.cs"
kept "$ARI/obj" "a file named obj"
kept "$ARI/obj.generated.System.Text.Json.g.cs" "obj, generated and System. inside one file name"
kept "$ARI/generated/$TAIL" "generated/ outside obj/"
kept "generated/$TAIL" "a root generated/ with no obj/ at all"
kept "$ARI/generated/System.Text.Json.SourceGeneration/obj/AriJsonContext.g.cs" "generated/ above obj/ rather than below it"
kept "$ARI/obj/Release/net10.0/$TAIL" "a System.* folder under obj/ with no generated/"
kept "$ARI/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration.g.cs" "a System.* FILE directly in generated/"
kept "$ARI/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration" "a uri that ends at the generator folder"
kept "$ARI/obj/Release/net10.0/generated/System/Gen/AriJsonContext.g.cs" "a folder named System, with no dot"
kept "$ARI/obj/Release/net10.0/generated/SystemTextJson/Gen/AriJsonContext.g.cs" "System without its dot"
kept "$ARI/obj/Release/net10.0/generated/MicrosoftExtensionsOptions/Gen/Validators.g.cs" "Microsoft without its dot"
kept "$ARI/obj/Release/net10.0/generated/Vendor.System.Text.Json/Gen/AriJsonContext.g.cs" "System. in the middle of a folder name"
kept "$ARI/obj/Release/net10.0/generated/system.text.json.sourcegeneration/Gen/AriJsonContext.g.cs" "a lower-case system.* folder"
kept "$ARI/Obj/Release/net10.0/generated/$TAIL" "Obj/ in another case"
kept "$ARI/obj/Release/net10.0/Generated/$TAIL" "Generated/ in another case"
kept "$ARI/obj/../generated/$TAIL" "a .. segment between obj/ and generated/"
kept "$ARI/obj/Release/net10.0/generated/System.Text.Json.SourceGeneration/../../../../../AriClient.cs" \
  "a .. segment climbing back out of generator output"
kept 'src\Verbara.Sdk.Ari\obj\Release\net10.0\generated\System.Text.Json.SourceGeneration\Gen\AriJsonContext.g.cs' \
  "backslash separators, which are not uri segments"
kept "" "an empty uri"

# shape_kept <result> <why> — a valid result with an unusual location, beside the control: kept, log untouched.
shape_kept() {
  log "$(result cs/useless-cast-to-self "$OURS")" "$1"
  run 0 "kept: $2" "$IN" "$OUT"
  expect_output '.' "kept: $2"
}
shape_kept '{"ruleId": "cs/useless-upcast", "message": {"text": "m"}}' "a result with no locations"
shape_kept '{"ruleId": "cs/useless-upcast", "message": {"text": "m"}, "locations": []}' "a result with an empty locations array"
shape_kept '{"ruleId": "cs/useless-upcast", "message": {"text": "m"}, "locations": null}' "a result whose locations are null"
shape_kept '{"ruleId": "cs/useless-upcast", "message": {"text": "m"}, "locations": [{"logicalLocations": [{"fullyQualifiedName": "Verbara.Sdk.Ari.AriJsonContext"}]}]}' \
  "a primary location with no physicalLocation"
shape_kept '{"ruleId": "cs/useless-upcast", "message": {"text": "m"}, "locations": [{"physicalLocation": {"region": {"startLine": 1}}}]}' \
  "a physicalLocation with no artifactLocation"
shape_kept "$(jq -cn --arg stj "$STJ" --arg ours "$OURS" '{ ruleId: "cs/useless-upcast", message: { text: "m" },
    locations: [ { physicalLocation: { artifactLocation: { uri: $ours } } }, { physicalLocation: { artifactLocation: { uri: $stj } } } ] }')" \
  "a result in our code whose SECOND location is generator output"
shape_kept "$(jq -cn --arg stj "$STJ" --arg ours "$OURS" '{ ruleId: "cs/useless-upcast", message: { text: "m" },
    locations: [ { physicalLocation: { artifactLocation: { uri: $ours } } } ],
    relatedLocations: [ { id: 1, physicalLocation: { artifactLocation: { uri: $stj } } } ],
    codeFlows: [ { threadFlows: [ { locations: [ { location: { physicalLocation: { artifactLocation: { uri: $stj } } } } ] } ] } ] }')" \
  "a result in our code whose related location and code flow pass through generator output"

# An artifactLocation that carries only an index is not resolved through run.artifacts, even when it would land in
# generator output: the rule never removes more than a uri shows.
log "$(result cs/useless-cast-to-self "$OURS")" \
    '{"ruleId": "cs/useless-upcast", "message": {"text": "m"}, "locations": [{"physicalLocation": {"artifactLocation": {"index": 1}}}]}'
patch ".runs[0].artifacts += [ { location: { uri: \"$STJ\", uriBaseId: \"%SRCROOT%\" } } ]"
run 0 "kept: a location that names its artifact only by index" "$IN" "$OUT"
expect_output '.' "kept: a location that names its artifact only by index"

# =============================================================================================
# rule, kept — a result in generator output whose rule is security-relevant, or cannot be shown not to be
# =============================================================================================
SECURITY='its rule is security-relevant (tag "security" or a security-severity), and a security result is never removed.'
UNRESOLVED='its rule does not resolve in tool.driver or tool.extensions, so it cannot be shown not to be security-relevant.'
UNREADABLE='the properties or properties.tags of its rule have the wrong type, so the rule cannot be shown not to be security-relevant.'

# guarded <why> <rule-label> <reason> <result> [<patch>] — beside the control, a result that the location rule alone
# would remove is kept, the log is exactly as it was, and one ::warning:: names its rule, its uri and the reason.
guarded() {
  local uri
  uri="$(jq -r '.locations[0].physicalLocation.artifactLocation.uri' <<<"$4")"
  log "$(result cs/useless-cast-to-self "$OURS")" "$4"
  [ -z "${5:-}" ] || patch "$5"
  run 0 "kept: $1" "$IN" "$OUT"
  expect_output '.' "kept: $1"
  warned "::warning::filter-codeql-sarif: kept $2 at $uri although it lies in .NET source-generator output — $3" "kept: $1"
  says "::notice::filter-codeql-sarif: removed nothing — 1 of the 2 result(s) lie in .NET source-generator output, and each was kept because its rule is security-relevant or cannot be shown not to be (see the warnings). Wrote $OUT." \
    "kept: $1 — the notice counts it"
}

# dropped <why> <result> [<patch>] — beside the control, a result in generator output whose rule resolves and is not
# security-relevant is removed, silently.
dropped() {
  log "$(result cs/useless-cast-to-self "$OURS")" "$2"
  [ -z "${3:-}" ] || patch "$3"
  run 0 "removed: $1" "$IN" "$OUT"
  expect_output 'del(.runs[0].results[1])' "removed: $1"
  lacks "::warning::" "removed: $1"
}

# Security-relevant, in the CLI's shape: tool.extensions, named by toolComponent + index.
guarded "cs/log-forging (tag and security-severity) in the [LoggerMessage] generator's output" \
  cs/log-forging "$SECURITY" "$(result cs/log-forging "$LOGGING")"
guarded "a rule in tool.extensions tagged security, with no security-severity" \
  test/security-tag-only "$SECURITY" "$(result test/security-tag-only "$STJ")"
guarded "a rule in tool.extensions with a security-severity and no security tag" \
  test/security-severity-only "$SECURITY" "$(result test/security-severity-only "$OPTIONS")"
guarded "a security-severity whose value is null — the key is what counts" \
  test/security-severity-only "$SECURITY" "$(result test/security-severity-only "$REGEX")" \
  '.runs[0].tool.extensions[0].rules[7].properties["security-severity"] = null'
guarded "a rule named only by id, security-relevant in tool.extensions" \
  cs/log-forging "$SECURITY" "$(located '{"ruleId": "cs/log-forging"}')"

# Security-relevant, in tool.driver with no tool.extensions: named by ruleIndex, by rule.index, or by the id alone.
IN_DRIVER='.runs[0].tool |= (del(.extensions) | .driver.rules = [
  { id: "cs/useless-upcast", properties: { tags: ["maintainability"] } },
  { id: "test/driver-security", properties: { tags: ["security"] } } ])'
guarded "a rule in tool.driver tagged security, named by ruleIndex" \
  test/driver-security "$SECURITY" "$(located '{"ruleId": "test/driver-security", "ruleIndex": 1}')" "$IN_DRIVER"
guarded "a rule in tool.driver tagged security, named by rule.index" \
  test/driver-security "$SECURITY" "$(located '{"rule": {"id": "test/driver-security", "index": 1}}')" "$IN_DRIVER"
guarded "a rule in tool.driver tagged security, named only by id" \
  test/driver-security "$SECURITY" "$(located '{"ruleId": "test/driver-security"}')" "$IN_DRIVER"

# An id matches in EVERY component, and one security-relevant match is enough.
guarded "an id that matches a plain rule in tool.driver and in extensions[0], and a security rule in extensions[1]" \
  cs/useless-upcast "$SECURITY" "$(located '{"ruleId": "cs/useless-upcast"}')" \
  '.runs[0].tool.driver.rules = [ { id: "cs/useless-upcast" } ] | .runs[0].tool.extensions[1].rules = [ { id: "cs/useless-upcast", properties: { tags: ["security"] } } ]'

# Unresolvable: nothing found, references that contradict each other, and references of the wrong type.
guarded "a rule id that no rule has" test/unknown "$UNRESOLVED" "$(result test/unknown "$STJ")"
guarded "a result that names no rule" "(no rule id)" "$UNRESOLVED" "$(located '{}')"
guarded "an index past the end of the rules" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": 0}, "index": 99}}')"
guarded "an index that lands on a plain rule while the id names a security rule" cs/log-forging "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/log-forging", "rule": {"id": "cs/log-forging", "toolComponent": {"index": 0}, "index": 1}}')"
guarded "ruleId and rule.id that differ" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/log-forging", "toolComponent": {"index": 0}}}')"
guarded "ruleIndex and rule.index that differ" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "ruleIndex": 5, "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": 0}, "index": 1}}')"
guarded "a toolComponent index with no such extension" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": 7}, "index": 1}}')"
guarded "a ruleIndex with no toolComponent, so into tool.driver, whose rules are empty" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "ruleIndex": 1}')"
guarded "an index that is not an integer" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": 0}, "index": 1.5}}')"
guarded "an index that is a string" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": 0}, "index": "1"}}')"
guarded "a negative index other than -1" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "ruleIndex": -2}')"
guarded "a rule id that is not a string" 7 "$UNRESOLVED" "$(located '{"ruleId": 7}')"
guarded "a rule reference that is not an object" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": "cs/useless-upcast"}')"
guarded "a toolComponent that is not an object" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/useless-upcast", "toolComponent": 0, "index": 1}}')"
guarded "a toolComponent index that is a string" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast", "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": "0"}, "index": 1}}')"
guarded "a run with no tool" cs/useless-upcast "$UNRESOLVED" "$(result cs/useless-upcast "$STJ")" 'del(.runs[0].tool)'
guarded "an id, with tool.extensions an object" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast"}')" '.runs[0].tool.extensions = { rules: [ { id: "cs/useless-upcast" } ] }'
guarded "an id, with one searched extension whose rules are an object" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast"}')" '.runs[0].tool.extensions[1].rules = { id: "cs/log-forging" }'
guarded "an id, with a tool.driver that is not an object" cs/useless-upcast "$UNRESOLVED" \
  "$(located '{"ruleId": "cs/useless-upcast"}')" '.runs[0].tool.driver = "CodeQL"'

# Unreadable: the rule is found, but its security markers cannot be read.
guarded "a rule whose properties are a string" cs/useless-upcast "$UNREADABLE" "$(result cs/useless-upcast "$STJ")" \
  '.runs[0].tool.extensions[0].rules[1].properties = "security"'
guarded "a rule whose tags are a string" cs/useless-upcast "$UNREADABLE" "$(result cs/useless-upcast "$STJ")" \
  '.runs[0].tool.extensions[0].rules[1].properties.tags = "security"'

# =============================================================================================
# rule, removed — every other way a non-security rule resolves
# =============================================================================================
dropped "a rule with no properties at all" "$(result test/no-properties "$STJ")"
dropped "a rule named only by id, found in tool.extensions" "$(located '{"ruleId": "cs/nested-if-statements"}' "$REGEX")"
dropped "a rule named only by rule.id" "$(located '{"rule": {"id": "cs/nested-if-statements"}}' "$REGEX")"
dropped "a ruleIndex of -1, SARIF's 'not given', so the id decides" "$(located '{"ruleId": "cs/useless-upcast", "ruleIndex": -1}')"
dropped "a toolComponent named without an index, so the id decides" \
  "$(located '{"ruleId": "cs/missed-ternary-operator", "rule": {"id": "cs/missed-ternary-operator", "toolComponent": {"name": "codeql/csharp-queries"}, "index": 2}}')"
dropped "ruleIndex and rule.index given and equal" \
  "$(located '{"ruleId": "cs/useless-upcast", "ruleIndex": 1, "rule": {"id": "cs/useless-upcast", "toolComponent": {"index": 0}, "index": 1}}')"
dropped "a plain rule in tool.driver, named by ruleIndex" "$(located '{"ruleId": "cs/useless-upcast", "ruleIndex": 0}')" "$IN_DRIVER"
dropped "an index into tool.extensions does not read tool.driver, however malformed" \
  "$(result cs/useless-upcast "$STJ")" '.runs[0].tool.driver = "CodeQL"'

# A security result, or a result whose rule does not resolve, OUTSIDE generator output is never looked at: kept and
# not warned about.
log "$(result cs/log-forging "$OURS")" "$(result test/security-tag-only "$ARI/generated/$TAIL")" \
    "$(result test/unknown "$AMI_GEN")" "$(located '{}' "$ARI/obj/Release/net10.0/$TAIL")"
run 0 "security and unresolvable results outside generator output" "$IN" "$OUT"
expect_output '.' "security and unresolvable results outside generator output are left as they are"
lacks "::warning::" "a result outside generator output is never warned about"
says "::notice::filter-codeql-sarif: removed nothing — none of the 4 result(s) lies in .NET source-generator output" \
  "results outside generator output are not counted as held"

# =============================================================================================
# several runs, and runs with nothing to filter
# =============================================================================================
# Each run resolves rules in its own tool: run 0 by id in tool.driver, run 1 by id in tool.extensions — which lacks
# cs/useless-upcast, so that result is held even though run 0 knows the rule.
jq -cn --arg schema "$SCHEMA" --arg ours "$OURS" --arg stj "$STJ" --arg options "$OPTIONS" --arg regex "$REGEX" --arg amigen "$AMI_GEN" \
    --argjson rules "$RULES" '
  def r($rule; $uri): { ruleId: $rule, message: { text: "m" }, locations: [ { physicalLocation: { artifactLocation: { uri: $uri } } } ] };
  { "$schema": $schema, version: "2.1.0",
    runs: [ { tool: { driver: { name: "CodeQL", rules: $rules } }, automationDetails: { id: "/language:csharp/" },
              results: [ r("cs/useless-cast-to-self"; $stj), r("cs/path-combine"; $ours), r("cs/useless-upcast"; $options) ] },
            { tool: { driver: { name: "CodeQL" }, extensions: [ { name: "q", rules: [ $rules[0], $rules[2] ] } ] },
              automationDetails: { id: "/language:csharp/second/" },
              results: [ r("cs/useless-cast-to-self"; $amigen), r("cs/missed-ternary-operator"; $regex), r("cs/useless-upcast"; $options) ] },
            { tool: { driver: { name: "a run with no results property" } } },
            { tool: { driver: { name: "a run whose results are null" } }, results: null },
            { tool: { driver: { name: "a run with no results" } }, results: [] } ] }' > "$IN"
run 0 "several runs" "$IN" "$OUT"
expect_output 'del(.runs[0].results[0], .runs[0].results[2], .runs[1].results[1])' "each run is filtered on its own, and the others are untouched"
jq -e '(.runs[2] | has("results") | not) and (.runs[3] | has("results")) and .runs[3].results == null' "$OUT" >/dev/null 2>&1 && ok \
  || bad "a run with no results property gets none, and null results stay null"
says "removed 3 of 6 result(s)" "the count spans every run"
warned "kept cs/useless-upcast at $OPTIONS although it lies in .NET source-generator output — $UNRESOLVED" \
  "a rule is resolved in its own run's tool, never another run's"

printf '{"$schema": "%s", "version": "2.1.0", "runs": []}' "$SCHEMA" > "$IN"
run 0 "a log with no runs" "$IN" "$OUT"
expect_output '.' "a log with no runs is written unchanged"
says "removed nothing — none of the 0 result(s)" "an empty log is reported as such"

log "$(result cs/useless-cast-to-self "$OURS")"
run 0 "nothing to remove" "$IN" "$OUT"
expect_output '.' "a log with nothing to remove is written unchanged"
says "::notice::filter-codeql-sarif: removed nothing — none of the 1 result(s)" "a pass that removes nothing still says so"
lacks "::error::" "a pass reports no error"

# =============================================================================================
# the notice and the warnings — counts by generator and by rule, and nothing in them can start a workflow command
# =============================================================================================
log "$(result cs/useless-cast-to-self "$STJ")" "$(result cs/path-combine "$OURS")" "$(result cs/useless-upcast "$EXAMPLE")" \
    "$(result cs/missed-ternary-operator "$OPTIONS")" "$(result cs/useless-cast-to-self "$ARI/obj/Debug/net10.0/generated/$TAIL")" \
    "$(result cs/path-combine "$AMI_GEN")"
run 0 "a mixed log" "$IN" "$OUT"
says "::notice::filter-codeql-sarif: removed 4 of 6 result(s) in .NET source-generator output, kept 2 — by generator: System.Text.Json.SourceGeneration 3, Microsoft.Extensions.Options.SourceGeneration 1; by rule: cs/useless-cast-to-self 2, cs/missed-ternary-operator 1, cs/useless-upcast 1. Wrote $OUT." \
  "the notice counts by generator and by rule, largest first"
lacks "::error::" "a pass reports no error"
lacks "::warning::" "a pass that holds nothing back warns about nothing"
no_temp_left "a successful run"
[ "$(jq -n '[inputs] | length' "$OUT")" = 1 ] && ok || bad "the output is exactly one JSON value"
jq -e --arg schema "$SCHEMA" 'type == "object" and .version == "2.1.0" and .["$schema"] == $schema and (.runs | length) == 1' "$OUT" >/dev/null 2>&1 \
  && ok || bad "the output is still a SARIF 2.1.0 log: version, \$schema and runs kept"

log "$(result cs/useless-cast-to-self "$STJ")" "$(result cs/log-forging "$LOGGING")" "$(result test/unknown "$OPTIONS")" \
    "$(result cs/path-combine "$OURS")"
run 0 "a log with results removed and results held" "$IN" "$OUT"
expect_output 'del(.runs[0].results[0])' "only the plain result in generator output is removed"
says "::notice::filter-codeql-sarif: removed 1 of 4 result(s) in .NET source-generator output, kept 3 — by generator: System.Text.Json.SourceGeneration 1; by rule: cs/useless-cast-to-self 1. 2 of the kept result(s) lie in that output too, and were kept because their rule is security-relevant or cannot be shown not to be (see the warnings). Wrote $OUT." \
  "the notice counts the held results apart from the removed ones"
[ "$(grep -c '^::warning::' <<<"$LOG" || true)" = 2 ] && ok || bad "one ::warning:: per held result"
grep '^::warning::' <<<"$LOG" | sed -n 1p | grep -qF "kept cs/log-forging at $LOGGING" \
  && grep '^::warning::' <<<"$LOG" | sed -n 2p | grep -qF "kept test/unknown at $OPTIONS" && ok \
  || bad "the warnings follow the order of the results"
tail -n 1 <<<"$LOG" | grep -q '^::notice::' && ok || bad "the notice comes after the warnings"

log "$(result cs/useless-upcast "$OURS")" \
    '{"rule": {"id": "cs/missed-ternary-operator"}, "message": {"text": "m"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "obj/generated/System.Text.Json.SourceGeneration/Gen/A.g.cs"}}}]}' \
    '{"rule": {"toolComponent": {"index": 0}, "index": 4}, "message": {"text": "m"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "obj/generated/System.Text.Json.SourceGeneration/Gen/B.g.cs"}}}]}' \
    '{"rule": {"toolComponent": {"index": 0}, "index": 8}, "message": {"text": "m"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "obj/generated/System.Text.Json.SourceGeneration/Gen/C.g.cs"}}}]}'
patch '.runs[0].tool.extensions[0].rules[8] |= del(.id)'
run 0 "results that name their rule only by reference, or only by index" "$IN" "$OUT"
expect_output 'del(.runs[0].results[1], .runs[0].results[2], .runs[0].results[3])' "rules named by reference or by index alone resolve"
says "by rule: (no rule id) 1, cs/missed-ternary-operator 1, cs/nested-if-statements 1" \
  "rule.id stands in for ruleId, an index alone reports the id of its rule, and a rule with no id is still counted"

log "$(result "$(printf 'cs/forged\n::error::all red')" "$STJ")"
run 0 "a rule id with a newline in it, whose rule does not resolve" "$IN" "$OUT"
if grep -q '^::error::' <<<"$LOG"; then bad "a rule id started a workflow command of its own, from a ::warning::"; else ok; fi
says 'kept cs/forged%0A::error::all red at' "the newline is escaped in the ::warning::"
patch '.runs[0].tool.extensions[0].rules += [ { id: "cs/forged\n::error::all red" } ]'
run 0 "a rule id with a newline in it, whose rule resolves" "$IN" "$OUT"
if grep -q '^::error::' <<<"$LOG"; then bad "a rule id started a workflow command of its own, from the ::notice::"; else ok; fi
says 'by rule: cs/forged%0A::error::all red 1' "the newline is escaped in the ::notice::"

log "$(result cs/log-forging "$(printf 'obj/generated/System.Text.Json.SourceGeneration/Gen/A\n::error::uri.g.cs')")"
run 0 "a uri with a newline in it, held back" "$IN" "$OUT"
if grep -q '^::error::' <<<"$LOG"; then bad "a uri started a workflow command of its own, from a ::warning::"; else ok; fi
says 'at obj/generated/System.Text.Json.SourceGeneration/Gen/A%0A::error::uri.g.cs although' "the newline in a uri is escaped in the ::warning::"

# =============================================================================================
# input the filter cannot read as one SARIF 2.1.0 log — exit 2, and nothing left to upload
# =============================================================================================
bad_input '' "an empty file"
bad_input $'  \n\t\n' "a file of whitespace"
bad_input '{"version": "2.1.0", "runs": [' "truncated JSON"
bad_input 'not json' "text that is not JSON"
bad_input 'null' "a bare null"
bad_input '[]' "an array"
bad_input '"2.1.0"' "a string"
bad_input '42' "a number"
bad_input '{}' "an object with neither version nor runs"
bad_input '{"version": "2.1.0"}' "a log with no runs"
bad_input '{"runs": []}' "a log with no version"
bad_input '{"version": "2.0.0", "runs": []}' "a SARIF 2.0.0 log"
bad_input '{"version": 2.1, "runs": []}' "a version that is a number"
bad_input '{"version": "2.1.0", "runs": {}}' "runs that are an object"
bad_input '{"version": "2.1.0", "runs": ["a run"]}' "a run that is a string"
bad_input '{"version": "2.1.0", "runs": [{"results": {}}]}' "results that are an object"
bad_input '{"version": "2.1.0", "runs": [{"results": ["a result"]}]}' "a result that is a string"
bad_input '{"version": "2.1.0", "runs": [{"results": [{"locations": {}}]}]}' "locations that are an object"
bad_input '{"version": "2.1.0", "runs": [{"results": [{"locations": ["here"]}]}]}' "a primary location that is a string"
bad_input '{"version": "2.1.0", "runs": [{"results": [{"locations": [{"physicalLocation": "here"}]}]}]}' "a physicalLocation that is a string"
bad_input '{"version": "2.1.0", "runs": [{"results": [{"locations": [{"physicalLocation": {"artifactLocation": []}}]}]}]}' "an artifactLocation that is an array"
bad_input '{"version": "2.1.0", "runs": [{"results": [{"locations": [{"physicalLocation": {"artifactLocation": {"uri": 7}}}]}]}]}' "a uri that is a number"

# One malformed result among valid ones still refuses the whole log — never a partial filter.
log "$(result cs/useless-cast-to-self "$OURS")" "$(result cs/useless-upcast "$STJ")"
patch '.runs[0].results += [ { locations: [ { physicalLocation: { artifactLocation: { uri: 7 } } } ] } ]'
fails_closed "one malformed result after two valid ones" "$IN" "$OUT"

log "$(result cs/useless-upcast "$STJ")"
cat "$IN" "$IN" > "$WORK/two.sarif"
cp "$WORK/two.sarif" "$IN"
fails_closed "two logs in one file" "$IN" "$OUT"
says "holds 2 JSON values" "the error says how many values it found"

log "$(result cs/useless-upcast "$STJ")"
printf '\n}' >> "$IN"
fails_closed "a valid log followed by garbage" "$IN" "$OUT"

fails_closed "a missing input file" "$WORK/nope.sarif" "$OUT"
mkdir -p "$WORK/a-directory"
fails_closed "an input that is a directory" "$WORK/a-directory" "$OUT"
if [ "$(id -u)" -ne 0 ]; then
  log "$(result cs/useless-upcast "$STJ")"
  chmod 000 "$IN"
  fails_closed "an input that cannot be read" "$IN" "$OUT"
  chmod 644 "$IN"
fi

# Without jq nothing can be read — and the stale output still goes, because it is removed before jq is looked for.
mkdir -p "$WORK/bin-without-jq"
ln -s "$(command -v rm)" "$WORK/bin-without-jq/rm"
log "$(result cs/useless-upcast "$STJ")"
echo 'stale' > "$OUT"
actual=0
LOG="$(PATH="$WORK/bin-without-jq" "$BASH" "$FILTER" "$IN" "$OUT" 2>&1)" || actual=$?
[ "$actual" -eq 2 ] && ok || bad "no jq on PATH — expected exit 2, got $actual"
says "jq is not on PATH" "the missing tool is named"
[ ! -e "$OUT" ] && ok || bad "no jq on PATH — a stale output was left behind"

# =============================================================================================
# arguments and the output path
# =============================================================================================
log "$(result cs/useless-cast-to-self "$OURS")" "$(result cs/useless-upcast "$STJ")"
cp "$IN" "$WORK/original.sarif"

run 2 "no arguments"
says "usage: filter-codeql-sarif.sh <in.sarif> <out.sarif>" "the usage is printed"
run 2 "one argument" "$IN"
run 2 "three arguments" "$IN" "$OUT" "$WORK/extra.sarif"
[ ! -e "$WORK/extra.sarif" ] && ok || bad "three arguments — nothing may be written"
run 2 "an empty input path" "" "$OUT"
run 2 "an empty output path" "$IN" ""

run 2 "the input is also the output" "$IN" "$IN"
cmp -s "$IN" "$WORK/original.sarif" && ok || bad "the input must survive being named as the output"
ln -s "$IN" "$WORK/link.sarif"
run 2 "the output is a symlink to the input" "$IN" "$WORK/link.sarif"
cmp -s "$IN" "$WORK/original.sarif" && ok || bad "the input must survive a symlinked output"

mkdir -p "$WORK/out-dir"
run 2 "the output is a directory" "$IN" "$WORK/out-dir"
[ -d "$WORK/out-dir" ] && ok || bad "an output directory must not be removed"
run 2 "the output's directory does not exist" "$IN" "$WORK/missing/out.sarif"
[ ! -e "$WORK/missing" ] && ok || bad "a missing output directory must not be created"

echo 'stale' > "$OUT"
run 0 "a stale output is replaced" "$IN" "$OUT"
expect_output 'del(.runs[0].results[1])' "a stale output is replaced by this run's log"
no_temp_left "a run that replaced a stale output"

mkdir -p "$WORK/with space"
cp "$IN" "$WORK/with space/in put.sarif"
run 0 "paths with spaces" "$WORK/with space/in put.sarif" "$WORK/with space/out put.sarif"
[ -f "$WORK/with space/out put.sarif" ] && ok || bad "paths with spaces — the output was not written"

jq '.' "$IN" > "$WORK/pretty.sarif"
run 0 "a pretty-printed log" "$WORK/pretty.sarif" "$OUT"
expect_output 'del(.runs[0].results[1])' "a pretty-printed log is filtered the same way"

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"

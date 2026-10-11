#!/usr/bin/env bash
# test_public_api_guard_coverage.sh — unit tests for scripts/ci/check-public-api-guard-coverage.sh.
#
# The guard fires on a state that is invisible by construction: a package that silences the undeclared-public-API
# diagnostic (RS0016) for itself builds exactly as green as one that declares its whole surface. So every rule gets
# a case here, in both directions, and so does every way its input can come back missing or wrong.
#
# Two halves:
#   * THE LOGIC, always. Each case is a stand-in checkout: a project under src/ with its own record files, the
#     argument record and SARIF log a real compile leaves in obj/Release/<tfm>/, the analyzer-config files the record
#     names, and a stand-in `dotnet` that answers the selection from an `evaluated` file next to the project. The
#     record lines are the shape real builds write and the SARIF logs are the JSON real builds wrote, trimmed to
#     `version`, `runs[0].tool.driver.rules` and `runs[0].results`. Pure bash + jq + git, no .NET, a few seconds.
#     Runs in the always-run `Coverage Script Tests` job.
#   * THE READ, with PUBLIC_API_MSBUILD_CASES=1. Scratch projects built by real MSBuild with the two recording
#     properties: once clean (proven), once with an undeclared public type and nothing else (the build fails, so the
#     probe is live), and once each with a silencer that was measured to build green with that undeclared type in the
#     DLL — each must read "not proven". Needs the analyzer packages in the NuGet cache (restored by Build Release) and
#     no network. Runs in `Pack Warnings Gate`, where the guard runs. Without the variable these cases are skipped.
#
# Not pinned, on purpose: "both PublicAPI.*.txt deleted". With neither file the analyzer reports every public symbol,
# with exactly one it reports RS0048, and both fail the build on their own.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUARD="$SCRIPT_DIR/../ci/check-public-api-guard-coverage.sh"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

CLEANUP=()
trap 'rm -rf "${CLEANUP[@]}"' EXIT
new_dir() { REPLY="$(mktemp -d)"; CLEANUP+=("$REPLY"); } # sets $REPLY; no subshell, so the cleanup list survives

GIT=(git -c user.name=harness -c user.email=harness@invalid -c init.defaultBranch=main -c commit.gpgsign=false)

# A stand-in for `dotnet --version` and `dotnet msbuild <project> -p:... -getProperty:<name>...`, answering from the
# file `evaluated` next to the project (`Name=Value` lines, first match wins). FAKE_MSBUILD=fail makes the evaluation
# fail, =garbage prints something that is not JSON.
new_dir; FAKE="$REPLY/dotnet"
cat > "$FAKE" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [ "${1:-}" = --version ]; then echo "10.0.999-standin"; exit 0; fi
[ "${1:-}" = msbuild ] || { echo "stand-in dotnet: expected 'msbuild' or '--version', got '$*'" >&2; exit 64; }
project="$2"; shift 2
case "${FAKE_MSBUILD:-}" in
  fail)    echo "$project : error MSB4025: The project file could not be loaded." >&2; exit 1 ;;
  garbage) echo "Welcome to .NET! This is not JSON."; exit 0 ;;
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
jq -n --argjson p "$props" '{Properties: $p}'
EOF
chmod +x "$FAKE"

# The SARIF logs real builds wrote (SDK 10.0.401, analyzer 5.6.0), trimmed; paths rewritten to a neutral root.
RULES='[{"id":"RS0016","shortDescription":{"text":"Add public types and members to the declared API"},"properties":{"category":"ApiDesign"}},{"id":"RS0017","shortDescription":{"text":"Remove deleted types and members from the declared API"},"properties":{"category":"ApiDesign"}}]'
OTHER_RESULT='{"ruleId":"MA0154","ruleIndex":692,"level":"note","message":{"text":"Use langword in XML comment"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"file:///work/src/Alpha/CircuitBreakerState.cs"},"region":{"startLine":5,"startColumn":50,"endLine":5,"endColumn":61}}}]}'
PRAGMA_RESULT='{"ruleId":"RS0016","ruleIndex":317,"level":"error","message":{"text":"Symbol '"'"'Alpha.Hidden.Probe'"'"' is not part of the declared public API"},"suppressions":[{"kind":"inSource","properties":{"suppressionType":"Pragma Directive"}}],"locations":[{"physicalLocation":{"artifactLocation":{"uri":"file:///work/src/Alpha/Probe.cs"},"region":{"startLine":4,"startColumn":14,"endLine":4,"endColumn":19}}}]}'
sarif_json() { # sarif_json <version> <rules json> <results json>
  jq -cn --arg v "$1" --argjson r "$2" --argjson x "$3" '{version: $v, runs: [{tool: {driver: {rules: $r}}, results: $x}]}'
}

new_repo() { # new_repo — a checkout root carrying the repository's own root .editorconfig and an empty src/
  new_dir; WORK="$REPLY"
  mkdir -p "$WORK/src"
  cp "$REPO_ROOT/.editorconfig" "$WORK/.editorconfig"
  printf 'obj/\nartifacts/\n' > "$WORK/.gitignore"
}

# project <name> [packable] — a shipped project whose last compile left a clean record, a clean log and a generated
# analyzer config, exactly as Build Release with the two recording properties leaves them.
project() {
  local name="$1" packable="${2:-true}" d="$WORK/src/$1" o
  o="$d/obj/Release/net10.0"
  mkdir -p "$o"
  echo '<Project Sdk="Microsoft.NET.Sdk" />' > "$d/$name.csproj"
  printf 'IsPackable=%s\nPackageId=%s\n' "$packable" "$name" > "$d/evaluated"
  printf '#nullable enable\n%s.Declared\n%s.Declared.Declared() -> void\n' "$name" "$name" > "$d/PublicAPI.Shipped.txt"
  printf '#nullable enable\n' > "$d/PublicAPI.Unshipped.txt"
  printf 'is_global = true\nbuild_property.TargetFramework = net10.0\nbuild_property.RootNamespace = %s\nbuild_property.ProjectDir = %s/\n' \
    "$name" "$d" > "$o/$name.GeneratedMSBuildEditorConfig.editorconfig"
  cat > "$o/public-api.csc-args.txt" <<EOF
/noconfig
/unsafe-
/checked-
/nowarn:CS1591,RS0037,RS0041,1701,1702,8002
/fullpaths
/nostdlib+
/errorreport:prompt
/warn:9999
/define:TRACE;RELEASE;NET;NET10_0
/reference:/usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/System.Runtime.dll
/debug+
/debug:portable
/filealign:512
/optimize+
/out:obj/Release/net10.0/$name.dll
/refout:obj/Release/net10.0/refint/$name.dll
/errorlog:"obj/Release/net10.0/public-api.sarif,version=2"
/target:library
/warnaserror+
/utf8output
/deterministic+
/langversion:latest
/analyzerconfig:$WORK/.editorconfig
/analyzerconfig:obj/Release/net10.0/$name.GeneratedMSBuildEditorConfig.editorconfig
/analyzerconfig:$WORK/sdk/analysislevel_10_recommended.globalconfig
/analyzer:/nuget/microsoft.codeanalysis.publicapianalyzers/5.6.0/analyzers/dotnet/Microsoft.CodeAnalysis.PublicApiAnalyzers.CodeFixes.dll
/analyzer:/nuget/microsoft.codeanalysis.publicapianalyzers/5.6.0/analyzers/dotnet/Microsoft.CodeAnalysis.PublicApiAnalyzers.dll
Declared.cs
obj/Release/net10.0/$name.AssemblyInfo.cs
/additionalfile:$d/PublicAPI.Shipped.txt
/additionalfile:$d/PublicAPI.Unshipped.txt
/additionalfile:PublicAPI.Shipped.txt
/additionalfile:PublicAPI.Unshipped.txt
/warnaserror+:NU1605,SYSLIB0011
EOF
  mkdir -p "$WORK/sdk"
  # The opening lines of the SDK preset the record names, which carries no RS0016 key.
  printf '# NOTE: Requires **VS2019 16.7** or later\n\n# Rules from %s\n\nis_global = true\n\nglobal_level = -99\n\n\n# CA1000: Do not declare static members on generic types\ndotnet_diagnostic.CA1000.severity = none\n' \
    "'10.0.0'" > "$WORK/sdk/analysislevel_10_recommended.globalconfig"
  sarif_json 2.1.0 "$RULES" "[$OTHER_RESULT]" > "$o/public-api.sarif"
}

rec()     { REPLY="$WORK/src/$1/obj/Release/net10.0/public-api.csc-args.txt"; }
rec_add() { rec "$1"; printf '%s\n' "$2" >> "$REPLY"; }                      # rec_add <project> <line>
rec_del() { rec "$1"; local t; t="$(grep -vxF -- "$2" "$REPLY" || true)"; printf '%s\n' "$t" > "$REPLY"; } # exact line
rec_sub() { rec "$1"; local t; t="$(awk -v a="$2" -v b="$3" '$0 == a { print b; next } { print }' "$REPLY")"; printf '%s\n' "$t" > "$REPLY"; }
log_of()  { REPLY="$WORK/src/$1/obj/Release/net10.0/public-api.sarif"; }
root_cfg() { printf '%s\n' "$1" > "$WORK/.editorconfig"; }                    # root_cfg <whole content>

# run <expected-exit> <description> [env assignments...] — commits the stand-in checkout the first time, then runs
# the guard from its root with the stand-in dotnet and no artifacts check unless an assignment says otherwise.
run() {
  local expected="$1" desc="$2"; shift 2
  local actual=0
  if [ ! -d "$WORK/.git" ]; then
    (cd "$WORK" && "${GIT[@]}" init -q && "${GIT[@]}" add -A && "${GIT[@]}" commit -qm fixture) >/dev/null
  fi
  OUT="$(cd "$WORK" && env CI= PUBLIC_API_GUARD_ARTIFACTS= DOTNET_BIN="$FAKE" "$@" bash "$GUARD" 2>&1)" || actual=$?
  if [ "$actual" -eq "$expected" ]; then ok; else
    bad "$desc — expected exit $expected, got $actual"
    printf '%s\n' "$OUT" | sed 's/^/      | /'
  fi
}
says()   { case "$OUT" in *"$1"*) ok ;; *) bad "$2 — output did not mention '$1'" ;; esac; }
silent() { case "$OUT" in *"$1"*) bad "$2 — output mentioned '$1'" ;; *) ok ;; esac; }

SENTENCE="read as they existed when this check ran"

# =============================================================================================
# contract
# =============================================================================================
[ -x "$GUARD" ] && ok || bad "check-public-api-guard-coverage.sh must be committed executable (mode 100755)"

# =============================================================================================
# the verdict on a clean compile, and what a proven package prints
# =============================================================================================
new_repo; project Alpha
run 0 "a clean record, log and analyzer-config set is proven"
says "all 1 shipped project(s) proven" "a clean tree says how many it proved"
says "read: src/Alpha/obj/Release/net10.0/public-api.csc-args.txt" "a proven package names the argument record it read"
says "read: src/Alpha/obj/Release/net10.0/public-api.sarif" "a proven package names the log it read"
says "read: .editorconfig" "a proven package names the analyzer-config files it read"
says "read: sdk/analysislevel_10_recommended.globalconfig" "the SDK preset is scanned, not exempted by path"
says ".NET SDK 10.0.999-standin" "the SDK version it read is printed"
says "$SENTENCE" "the output says the files were judged as they existed when it ran"

new_repo; project Alpha; project Beta; project Tool false
rm -rf "$WORK/src/Tool/obj"
run 0 "two clean shipped projects pass, and a project that does not pack is not judged"
says "all 2 shipped project(s) proven" "only the packable projects are counted"
silent "src/Tool" "a project that does not pack is not reported"

# The repository's own root .editorconfig as it stands is the default of every fixture; this pins it explicitly.
new_repo; project Alpha
cmp -s "$REPO_ROOT/.editorconfig" "$WORK/.editorconfig" && ok || bad "the fixtures carry the repository's own root .editorconfig"
run 0 "the repository's own root .editorconfig passes the rule"

# =============================================================================================
# the record: missing, empty, and every forbidden or required line
# =============================================================================================
new_repo; project Alpha; rm -f "$WORK/src/Alpha/obj/Release/net10.0/public-api.csc-args.txt"
run 1 "a missing record is 'not proven', never a pass"
says "no argument record at src/Alpha/obj/Release/*/public-api.csc-args.txt — not proven" "the missing record is named"
says "src/Alpha/Alpha.csproj (Alpha)" "the failure names the project and its package"

new_repo; project Alpha; rec Alpha; : > "$REPLY"
run 1 "an empty record is 'not proven'"

new_repo; project Alpha; rec_del Alpha '/warnaserror+'
run 1 "no /warnaserror+ line (TreatWarningsAsErrors off) fails, although /warnaserror+:NU1605 is still there"
says "no '/warnaserror+' line" "the failure names the missing line"

new_repo; project Alpha; rec_add Alpha '/skipanalyzers+'
run 1 "/skipanalyzers+ fails"
says "line '/skipanalyzers+'" "the failure quotes the line"

new_repo; project Alpha; rec_sub Alpha '/warn:9999' '/warn:0'
run 1 "/warn:0 fails"
says "line '/warn:0'" "the failure quotes the line"

new_repo; project Alpha; rec_sub Alpha '/nowarn:CS1591,RS0037,RS0041,1701,1702,8002' '/nowarn:CS1591,rs0016,RS0037,1701'
run 1 "RS0016 in /nowarn: fails, whatever its case"
says "RS0016 is in /nowarn:" "the failure says where RS0016 is"

new_repo; project Alpha; rec_add Alpha '/warnaserror-:RS0016'
run 1 "RS0016 in /warnaserror-: fails"
says "RS0016 is in /warnaserror-:" "the failure says where RS0016 is"

new_repo; project Alpha
rec_del Alpha '/analyzer:/nuget/microsoft.codeanalysis.publicapianalyzers/5.6.0/analyzers/dotnet/Microsoft.CodeAnalysis.PublicApiAnalyzers.dll'
run 1 "no /analyzer: line for Microsoft.CodeAnalysis.PublicApiAnalyzers.dll fails (the CodeFixes DLL is not the analyzer)"
says "the analyzer never ran" "the failure says the analyzer was not on the compile"

new_repo; project Alpha; rec_add Alpha '/ruleset:mute.ruleset'
run 1 "a /ruleset: line fails"
says "line '/ruleset:mute.ruleset'" "the failure quotes the line"

# A second pair from another directory, listing the undeclared symbols: measured green with 0 RS0016 anywhere.
new_repo; project Alpha; mkdir -p "$WORK/src/extra"; printf '#nullable enable\nAlpha.Hidden.Probe\n' > "$WORK/src/extra/PublicAPI.Shipped.txt"
rec_add Alpha '/additionalfile:../extra/PublicAPI.Shipped.txt'
run 1 "an /additionalfile: PublicAPI.Shipped.txt from another directory fails"
says "line '/additionalfile:../extra/PublicAPI.Shipped.txt': a declared record other than the project's own PublicAPI.Shipped.txt" \
  "the failure names the foreign record"

new_repo; project Alpha; mkdir -p "$WORK/src/extra"; printf '#nullable enable\n' > "$WORK/src/extra/PublicAPI.Unshipped.txt"
rec_add Alpha '/additionalfile:notes.txt,../extra/PublicAPI.Unshipped.txt'
run 1 "a foreign record hidden behind a comma in one /additionalfile: line fails"

new_repo; project Alpha; ln -s . "$WORK/src/Alpha/link"
rec_sub Alpha '/additionalfile:PublicAPI.Shipped.txt' '/additionalfile:link/PublicAPI.Shipped.txt'
run 0 "an /additionalfile: that reaches the project's own record through a symbolic link is the project's own"

new_repo; project Alpha
rec_del Alpha "/additionalfile:$WORK/src/Alpha/PublicAPI.Unshipped.txt"; rec_del Alpha '/additionalfile:PublicAPI.Unshipped.txt'
run 1 "a record that never hands the analyzer PublicAPI.Unshipped.txt fails"
says "no '/additionalfile:' line names PublicAPI.Unshipped.txt" "the failure names the missing file"

new_repo; project Alpha; rm -f "$WORK/src/Alpha/PublicAPI.Shipped.txt"
run 1 "a project with no PublicAPI.Shipped.txt of its own is 'not proven'"

# =============================================================================================
# the compiler's log, as the record names it
# =============================================================================================
new_repo; project Alpha; rec_del Alpha '/errorlog:"obj/Release/net10.0/public-api.sarif,version=2"'
run 1 "a record with no /errorlog: line fails"
says "0 '/errorlog:' line(s), not exactly one" "the failure counts the lines"

new_repo; project Alpha; rec_add Alpha '/errorlog:"obj/Release/net10.0/other.sarif,version=2"'
run 1 "a record with two /errorlog: lines fails"

new_repo; project Alpha
rec_sub Alpha '/errorlog:"obj/Release/net10.0/public-api.sarif,version=2"' '/errorlog:"obj/elsewhere/public-api.sarif,version=2"'
run 1 "an /errorlog: whose directory differs from /out: fails"
says "is not 'obj/Release/net10.0/public-api.sarif,version=2'" "the failure says what was expected"

new_repo; project Alpha; log_of Alpha; rm -f "$REPLY"
run 1 "a log the record names that does not exist is 'not proven'"
says "the log the record names does not exist — not proven" "the missing log is named"

new_repo; project Alpha; log_of Alpha; printf '{"version": "2.1.0", "runs": [' > "$REPLY"
run 1 "a log that is not JSON is 'not proven'"

new_repo; project Alpha; log_of Alpha; sarif_json 1.0.0 "$RULES" '[]' > "$REPLY"
run 1 "a SARIF 1.0.0 log (an unescaped comma on a -p:ErrorLog) is 'not proven'"
says "SARIF version '1.0.0', not 2.1.0" "the failure names the version"

new_repo; project Alpha; log_of Alpha; sarif_json 2.1.0 "$(jq -c 'map(select(.id != "RS0016"))' <<<"$RULES")" '[]' > "$REPLY"
run 1 "a log whose rules omit RS0016 (the analyzer did not run) is 'not proven'"
says "RS0016 is not among the rules of this compile" "the failure says the analyzer did not run"

new_repo; project Alpha; log_of Alpha; sarif_json 2.1.0 "$RULES" "[$OTHER_RESULT,$PRAGMA_RESULT]" > "$REPLY"
run 1 "a log with one suppressed RS0016 result fails"
says "file:///work/src/Alpha/Probe.cs:4" "the failure gives the file and line"
says "Symbol 'Alpha.Hidden.Probe' is not part of the declared public API" "the failure names the symbol"
says "suppressed by Pragma Directive" "the failure says what suppressed it"
silent "EmitCompilerGeneratedFiles" "a hand-written file gets no generator hint"

new_repo; project Alpha; log_of Alpha
sarif_json 2.1.0 "$RULES" "[$(jq -c 'del(.suppressions) | .level = "note"' <<<"$PRAGMA_RESULT")]" > "$REPLY"
run 1 "a log with one demoted RS0016 result (no suppression, level note) fails"
says "demoted to level note" "the failure says it was demoted"

new_repo; project Alpha; log_of Alpha
gen='file:///work/src/Alpha/obj/Release/net10.0/Gen/Gen.ProbeGenerator/Probe.g.cs'
sarif_json 2.1.0 "$RULES" "[$(jq -c --arg u "$gen" '.locations[0].physicalLocation.artifactLocation.uri = $u
  | .suppressions[0].properties.suppressionType = "DiagnosticSuppressor { Suppression Id: SPR0001, Suppression Justification: probe }"' <<<"$PRAGMA_RESULT")]" > "$REPLY"
run 1 "an RS0016 in a generated file, suppressed by a DiagnosticSuppressor, fails"
says "suppressed by DiagnosticSuppressor { Suppression Id: SPR0001" "the failure names the suppressor"
says "EmitCompilerGeneratedFiles=true" "the failure tells the reader how to see the generated file"

new_repo; project Alpha; log_of Alpha; sarif_json 2.1.0 "$RULES" "[$OTHER_RESULT]" > "$REPLY"
run 0 "results for other rules do not fail the package"

# =============================================================================================
# the analyzer-config files the record names
# =============================================================================================
new_repo; project Alpha; printf '[*.cs]\ndotnet_public_api_analyzer.skip_namespaces = Alpha.Hidden\n' > "$WORK/src/Alpha/.editorconfig"
rec_add Alpha "/analyzerconfig:$WORK/src/Alpha/.editorconfig"
run 1 "skip_namespaces in a nested .editorconfig fails"
says "src/Alpha/.editorconfig: line 2: an option addressed to the public-API analyzer" "the failure names the file, line and cause"

new_repo; project Alpha; printf 'is_global = true\ndotnet_public_api_analyzer.skip_namespaces = Alpha.Hidden\n' > "$WORK/src/Alpha/.globalconfig"
rec_add Alpha '/analyzerconfig:.globalconfig'
run 1 "skip_namespaces in a .globalconfig beside the project (relative path on the record) fails"
says "src/Alpha/.globalconfig: line 2" "the failure names the .globalconfig"

new_repo; project Alpha; printf '\n[*.cs]\nDOTNET_PUBLIC_API_ANALYZER.SKIP_NAMESPACES = Alpha.Hidden\n' >> "$WORK/.editorconfig"
run 1 "the key upper-cased in the root file fails"

new_repo; project Alpha; printf 'dotnet_public_api_analyzer.require_api_files = false\n' >> "$WORK/.editorconfig"
run 1 "any key under the dotnet_public_api_analyzer prefix fails, not only skip_namespaces"

new_repo; project Alpha
root_cfg 'root = true

[*.cs]
dotnet_diagnostic.RS0016.severity = warning  # Undeclared public API
dotnet_diagnostic.CA1000.severity = none
dotnet_diagnostic.RS0016.severity = none'
run 1 "a later line in the same section setting RS0016 to none fails, with the warning line intact"
says ".editorconfig: line 6: RS0016 severity \"none\"" "the failure names the line and the value"

new_repo; project Alpha
root_cfg 'root = true
[*.cs]
dotnet_diagnostic.RS0016.severity = warning
dotnet_diagnostic.RS0016.severity = silent'
run 1 "a later line setting RS0016 to silent fails"

new_repo; project Alpha
root_cfg 'root = true
[*.cs]
dotnet_diagnostic.RS0016.severity = warning

[src/**/*.cs]
dotnet_diagnostic.RS0016.severity = suggestion'
run 1 "a later section setting RS0016 to suggestion fails"

new_repo; project Alpha; root_cfg $'root = true\r\n[*.cs]\r\ndotnet_diagnostic.RS0016.severity = warning\r\ndotnet_diagnostic.RS0016.severity: none\r'
run 1 "a ':' separator and CRLF line endings are read as the compiler reads them"

new_repo; project Alpha; root_cfg $'root = true\r\n[*.cs]\r\ndotnet_diagnostic.RS0016.severity = warning\r'
run 0 "CRLF line endings on a clean root file pass"

new_repo; project Alpha; root_cfg 'root = true
[*.cs]
dotnet_diagnostic.CA1000.severity = none'
run 1 "a root file with no RS0016 severity line fails"
says "no dotnet_diagnostic.RS0016.severity line at the repository root" "the failure says what the root lacks"

new_repo; project Alpha; printf 'dotnet_analyzer_diagnostic.severity = none\n' >> "$WORK/.editorconfig"
run 1 "a bulk dotnet_analyzer_diagnostic severity fails"
new_repo; project Alpha; printf 'dotnet_analyzer_diagnostic.category-ApiDesign.severity = none\n' >> "$WORK/.editorconfig"
run 1 "a category demotion fails"
says "a bulk severity setting" "the failure names the cause"

new_repo; project Alpha; printf 'dotnet_code_quality.RS0016.excluded_symbol_names = Probe\n' >> "$WORK/.editorconfig"
run 1 "RS0016 named in any other key fails (dotnet_code_quality.RS0016.excluded_symbol_names: inert, failed on purpose)"
says "RS0016 named in a key other than its severity key" "the failure names the cause"

new_repo; project Alpha; rec_add Alpha "/analyzerconfig:$WORK/gone/.editorconfig"
run 1 "an analyzer-config path that does not exist at scan time is 'not proven'"
says "gone/.editorconfig: does not exist at scan time — not proven" "the missing file is named"

new_repo; project Alpha; root_cfg 'root = true
[*.cs]
dotnet_diagnostic.RS0016.severity = error'
run 0 "the severity line at error passes"

new_repo; project Alpha; root_cfg '# RS0016 is the undeclared-public-API diagnostic; see PublicAPI.Shipped.txt
root = true
[*.cs]
; dotnet_diagnostic.RS0016.severity = none   (a comment, not a setting)
dotnet_diagnostic.RS0016.severity = warning # RS0016 stays a build failure under TreatWarningsAsErrors
dotnet_code_quality.excluded_symbol_names = Probe'
run 0 "comments mentioning RS0016 and a dotnet_code_quality.excluded_symbol_names line pass"

# =============================================================================================
# the record files after the build
# =============================================================================================
new_repo; project Alpha; run 0 "precondition: the stand-in checkout is clean"
printf 'Alpha.Hidden.Probe\n' >> "$WORK/src/Alpha/PublicAPI.Shipped.txt"
run 1 "a record file the build left changed fails"
says "src/Alpha/PublicAPI.Shipped.txt: differs from the checkout after the build" "the failure names the file"

new_repo; project Alpha; run 0 "precondition: the stand-in checkout is clean"
mkdir -p "$WORK/src/Alpha/sub"; printf '#nullable enable\n' > "$WORK/src/Alpha/sub/PublicAPI.Unshipped.txt"
run 1 "an untracked record file fails"
says "src/Alpha/sub/PublicAPI.Unshipped.txt: ?? in git status" "the failure names the untracked file"

# =============================================================================================
# the packed set (CI)
# =============================================================================================
new_repo; project Alpha; project Alpha.Gen; mkdir -p "$WORK/artifacts"
touch "$WORK/artifacts/Alpha.2.8.0.nupkg" "$WORK/artifacts/Alpha.Gen.2.8.0.nupkg"
run 0 "one package per judged project passes (Alpha.Gen.* is not counted as Alpha's)" CI=true
new_repo; project Alpha; project Beta; mkdir -p "$WORK/artifacts"; touch "$WORK/artifacts/Alpha.2.8.0.nupkg"
run 1 "a judged project with no package fails" CI=true
says "0 package(s) named Beta.<version>.nupkg" "the failure names the missing package"
new_repo; project Alpha; mkdir -p "$WORK/artifacts"; touch "$WORK/artifacts/Alpha.2.8.0.nupkg" "$WORK/artifacts/Stray.1.0.0.nupkg"
run 1 "a package nobody judged fails" CI=true
says "2 .nupkg file(s) for 1 judged project(s)" "the failure counts both sides"
new_repo; project Alpha
run 1 "CI with no artifacts directory fails" CI=true
new_repo; project Alpha; new_dir; touch "$REPLY/Alpha.2.8.0.nupkg"
run 0 "PUBLIC_API_GUARD_ARTIFACTS points the check at another directory" PUBLIC_API_GUARD_ARTIFACTS="$REPLY"
new_repo; project Alpha
run 0 "outside CI, with no artifacts directory named, the packed set is not checked"

# =============================================================================================
# input that cannot be trusted
# =============================================================================================
new_repo; project Alpha
run 2 "an evaluation MSBuild cannot complete is 'cannot tell'" FAKE_MSBUILD=fail
says "MSB4025" "the MSBuild error is shown"
run 2 "evaluation output that is not JSON is 'cannot tell', not a pass" FAKE_MSBUILD=garbage
new_repo; project Tool false
run 2 "nothing packable under src/ is 'cannot tell'"
new_repo
run 2 "no project under src/ is 'cannot tell', not 'fine'"

# =============================================================================================
# the read itself: real MSBuild (PUBLIC_API_MSBUILD_CASES=1)
# =============================================================================================
# Each scratch checkout: global.json and Directory.Build.targets from this repository, a root .editorconfig with the
# RS0016 severity line, the analyzer at this repository's pinned version on src/Foo (TreatWarningsAsErrors on), and
# a record declaring Foo.Declared. The probe is `public class Probe` in namespace Foo.Hidden, which has no record
# lines, in src/Foo/Probe.cs. Every silencer below was measured to build green with the probe in the DLL.
if [ "${PUBLIC_API_MSBUILD_CASES:-0}" = 1 ]; then
  pinned() { sed -n "s:.*<PackageVersion Include=\"$1\" Version=\"\([^\"]*\)\".*:\1:p" "$REPO_ROOT/Directory.Packages.props" | head -1; }
  V_API="$(pinned Microsoft.CodeAnalysis.PublicApiAnalyzers)"
  V_CS="$(pinned Microsoft.CodeAnalysis.CSharp)"
  V_AN="$(pinned Microsoft.CodeAnalysis.Analyzers)"
  if ! command -v dotnet >/dev/null 2>&1; then
    bad "PUBLIC_API_MSBUILD_CASES=1 but there is no dotnet on PATH"
  elif [ -z "$V_API" ] || [ -z "$V_CS" ] || [ -z "$V_AN" ]; then
    bad "PUBLIC_API_MSBUILD_CASES=1 but the analyzer versions could not be read from Directory.Packages.props"
  else
    real_repo() { # real_repo <Foo.csproj PropertyGroup/ItemGroup body> — a scratch checkout with the probe in place
      new_dir; WORK="$REPLY"
      mkdir -p "$WORK/src/Foo"
      cp "$REPO_ROOT/global.json" "$REPO_ROOT/Directory.Build.targets" "$WORK/"
      printf 'obj/\nbin/\n' > "$WORK/.gitignore"
      printf 'root = true\n\n[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\n' > "$WORK/.editorconfig"
      cat > "$WORK/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'Foo'">
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup Condition="'\$(MSBuildProjectName)' == 'Foo'">
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="$V_API" PrivateAssets="all" />
    <AdditionalFiles Include="\$(MSBuildProjectDirectory)/PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="\$(MSBuildProjectDirectory)/PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
EOF
      printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n%s\n</Project>\n' \
        "$1" > "$WORK/src/Foo/Foo.csproj"
      printf '#nullable enable\nFoo.Declared\nFoo.Declared.Declared() -> void\n' > "$WORK/src/Foo/PublicAPI.Shipped.txt"
      printf '#nullable enable\n' > "$WORK/src/Foo/PublicAPI.Unshipped.txt"
      printf 'namespace Foo;\n\npublic class Declared\n{\n}\n' > "$WORK/src/Foo/Declared.cs"
      printf 'namespace Foo.Hidden;\n\npublic class Probe\n{\n}\n' > "$WORK/src/Foo/Probe.cs"
    }
    real_gen() { # real_gen <C# source> — a netstandard2.0 analyzer project referenced the way Verbara.Sdk.Ami references its generator
      mkdir -p "$WORK/gen/Gen"
      cat > "$WORK/gen/Gen/Gen.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="$V_CS" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="$V_AN" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF
      printf '%s\n' "$1" > "$WORK/gen/Gen/Gen.cs"
    }
    GEN_REF='  <ItemGroup>
    <ProjectReference Include="../../gen/Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>'
    # real_build <expected: green|red> <description> — builds src/Foo as Build Release does, with the two properties
    real_build() {
      local rc=0
      (cd "$WORK" && "${GIT[@]}" init -q && "${GIT[@]}" add -A && "${GIT[@]}" commit -qm fixture) >/dev/null
      BUILD_OUT="$(cd "$WORK" && MSBUILDDISABLENODEREUSE=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
                   dotnet build src/Foo/Foo.csproj -c Release --no-incremental -nodeReuse:false \
                   -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true 2>&1)" || rc=$?
      if { [ "$1" = green ] && [ "$rc" -eq 0 ]; } || { [ "$1" = red ] && [ "$rc" -ne 0 ]; }; then ok; else
        bad "real MSBuild: $2 — expected a $1 build, got exit $rc"
        printf '%s\n' "$BUILD_OUT" | grep -E 'error|warning' | head -10 | sed 's/^/      | /'
        return 1
      fi
    }
    real() { # real <expected guard exit> <description> <cause the output must name> [<build file body>...]
      local expected="$1" desc="$2" cause="$3"
      real_build green "$desc" || return 0
      run "$expected" "real MSBuild: $desc" DOTNET_BIN=dotnet
      [ -z "$cause" ] || says "$cause" "real MSBuild: $desc — names its cause"
    }

    # Positive control: nothing undeclared, nothing silenced.
    real_repo ''; rm -f "$WORK/src/Foo/Probe.cs"
    real 0 "a clean compile is proven" "$SENTENCE"
    # The probe is live: undeclared and unsilenced, it fails the build, and a failed compile leaves no record.
    real_repo ''
    if real_build red "the probe alone fails the build"; then
      case "$BUILD_OUT" in *"error RS0016"*"Foo.Hidden.Probe"*) ok ;; *) bad "real MSBuild: the probe fails the build with RS0016 naming it" ;; esac
      [ ! -e "$WORK/src/Foo/obj/Release/net10.0/public-api.csc-args.txt" ] && ok \
        || bad "real MSBuild: a failed CoreCompile writes no record"
    fi

    real_repo '  <PropertyGroup><WarningLevel>0</WarningLevel></PropertyGroup>'
    real 1 "WarningLevel=0" "line '/warn:0'"
    real_repo '  <PropertyGroup><RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild></PropertyGroup>'
    real 1 "RunAnalyzersDuringBuild=false with RunAnalyzers unset" "line '/skipanalyzers+'"
    real_repo '  <Target Name="Mute" BeforeTargets="CoreCompile">
    <PropertyGroup><NoWarn>$(NoWarn);RS0016</NoWarn></PropertyGroup>
  </Target>'
    real 1 "RS0016 appended to NoWarn inside a BeforeTargets=CoreCompile target" "RS0016 is in /nowarn:"
    real_repo '  <Target Name="DropApiAnalyzer" BeforeTargets="CoreCompile">
    <ItemGroup>
      <_DropApi Include="@(Analyzer)" Condition="'"'"'%(Filename)'"'"' == '"'"'Microsoft.CodeAnalysis.PublicApiAnalyzers'"'"'" />
      <Analyzer Remove="@(_DropApi)" />
    </ItemGroup>
  </Target>'
    real 1 "an <Analyzer Remove> inside a BeforeTargets=CoreCompile target" "the analyzer never ran"
    real_repo '  <PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>'
    real 1 "TreatWarningsAsErrors=false" "no '/warnaserror+' line"
    real_repo '  <PropertyGroup><CodeAnalysisRuleSet>$(MSBuildProjectDirectory)/mute.ruleset</CodeAnalysisRuleSet></PropertyGroup>'
    cat > "$WORK/src/Foo/mute.ruleset" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<RuleSet Name="mute" ToolsVersion="17.0">
  <Rules AnalyzerId="Microsoft.CodeAnalysis.PublicApiAnalyzers" RuleNamespace="Microsoft.CodeAnalysis.PublicApiAnalyzers">
    <Rule Id="RS0016" Action="None" />
  </Rules>
</RuleSet>
EOF
    real 1 "CodeAnalysisRuleSet at a ruleset with RS0016 at None" "/ruleset:"
    real_repo ''; printf 'dotnet_public_api_analyzer.skip_namespaces = Foo.Hidden\n' >> "$WORK/.editorconfig"
    real 1 "skip_namespaces in the root .editorconfig, the severity line left in place" "an option addressed to the public-API analyzer"
    real_repo '  <ItemGroup><AdditionalFiles Include="../extra/PublicAPI.Shipped.txt;../extra/PublicAPI.Unshipped.txt" /></ItemGroup>'
    mkdir -p "$WORK/src/extra"
    printf '#nullable enable\nFoo.Hidden.Probe\nFoo.Hidden.Probe.Probe() -> void\n' > "$WORK/src/extra/PublicAPI.Shipped.txt"
    printf '#nullable enable\n' > "$WORK/src/extra/PublicAPI.Unshipped.txt"
    real 1 "a second PublicAPI.*.txt pair from another directory listing the probe" "a declared record other than the project's own"
    real_repo ''; printf '#pragma warning disable RS0016\n%s\n' "$(cat "$WORK/src/Foo/Probe.cs")" > "$WORK/src/Foo/Probe.cs"
    real 1 "#pragma warning disable RS0016 in a hand-written file" "suppressed by Pragma Directive"
    real_repo ''
    printf 'namespace Foo.Hidden;\n\ninternal static class Ids\n{\n    public const string Api = "RS" + "0016";\n}\n' > "$WORK/src/Foo/Ids.cs"
    printf 'namespace Foo.Hidden;\n\n[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", Ids.Api)]\npublic class Probe\n{\n}\n' \
      > "$WORK/src/Foo/Probe.cs"
    real 1 "[SuppressMessage(\"ApiDesign\", Ids.Api)] with the id a const in another file" "suppressed by SuppressMessageAttribute"
    real_repo "$GEN_REF"; rm -f "$WORK/src/Foo/Probe.cs"
    real_gen 'using Microsoft.CodeAnalysis;

[Generator]
public sealed class ProbeGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterPostInitializationOutput(static c => c.AddSource("Probe.g.cs",
            "#pragma warning disable RS0016\nnamespace Foo.Hidden { public class Generated { } }\n"));
}'
    real 1 "a source generator that emits an undeclared public class under #pragma warning disable RS0016" "EmitCompilerGeneratedFiles=true"
    real_repo "$GEN_REF"
    real_gen 'using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Rs0016Suppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor Descriptor = new("SPR0001", "RS0016", "probe");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => ImmutableArray.Create(Descriptor);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
            context.ReportSuppression(Suppression.Create(Descriptor, diagnostic));
    }
}'
    real 1 "a DiagnosticSuppressor for RS0016" "suppressed by DiagnosticSuppressor"
  fi
else
  echo "SKIP: the real-MSBuild cases (PUBLIC_API_MSBUILD_CASES=1 runs them; Pack Warnings Gate does)"
fi

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"

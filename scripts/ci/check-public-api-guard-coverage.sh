#!/usr/bin/env bash
# check-public-api-guard-coverage.sh — every shipped package was compiled with the undeclared-public-API
# diagnostic (RS0016) live, and nothing suppressed, demoted or starved it.
#
# WHY. RS0016 is the guard: a public member added to a shipped package fails the build unless it is declared in
# that package's PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt. The negative control proves it fires on one
# package. It cannot show the other packages are guarded, because a package that silences the diagnostic for itself
# builds exactly as green as one that declares everything — and that is not hypothetical: for months RS0016 sat in
# the repository-wide NoWarn under a comment saying it was active, and the source-generator package never received
# the analyzer at all.
#
# WHAT IS READ — what the compile that produced each package left behind, never the project's evaluation and never a
# second compile. Build Release runs with -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true; Directory.Build.targets
# then writes, per project, obj/Release/<tfm>/public-api.csc-args.txt (the compiler's arguments, written after
# CoreCompile, so a failed compile leaves none) and points that compile's ErrorLog at public-api.sarif beside it.
# Evaluation only chooses the projects (IsPackable=true under src/); it judges nothing, because a target can change
# NoWarn, RunAnalyzers or the analyzer items after evaluation and before CoreCompile.
#
#   1. THE ARGUMENTS (the record) judge the compiler's options and the declared record it was given. A package fails
#      unless: the record exists; `/warnaserror+` is a line; no line is `/skipanalyzers+` or `/warn:0`; no `/nowarn:`
#      or `/warnaserror-:` line names RS0016 (any case); an `/analyzer:` line ends in
#      Microsoft.CodeAnalysis.PublicApiAnalyzers.dll; no line starts with `/ruleset:` (a ruleset at None silences
#      RS0016 whatever .editorconfig says); every `/additionalfile:` named PublicAPI.Shipped.txt or
#      PublicAPI.Unshipped.txt resolves (relative to the project directory, symbolic links followed) to the project's
#      own file of that name, and each name is present — the analyzer unions every additional file of those two exact
#      names, so a second pair from another directory listing the undeclared symbols builds green.
#   2. THE COMPILER'S LOG judges what became of what the analyzer raised. The record carries exactly one `/errorlog:`
#      line, equal to its own `/out:` directory followed by public-api.sarif,version=2, so the log read is the one the
#      record names. That file must parse as SARIF 2.1.0, list RS0016 among the rules (the analyzer ran in this
#      compile) and hold no RS0016 result at all: the compile succeeded, so any RS0016 in its log is one something
#      suppressed (a pragma, a SuppressMessage, a DiagnosticSuppressor — generated code included) or demoted.
#      invocations[0].executionSuccessful is not read: it is true on a failed compile too. Source text is not read
#      either: it does not include what a generator emitted and cannot show a suppressor.
#   3. THE ANALYZER-CONFIG FILES the record names judge what the analyzer was told — the one thing neither the
#      arguments nor the log can show, because the analyzer then raises nothing. Every `/analyzerconfig:` path is
#      scanned, wherever it lives (the repository's root .editorconfig and the SDK's own presets included; no exempt
#      file, no exempt directory). On every non-comment, non-section `key = value` line (key lowercased, value cut at
#      an inline # or ;, `:` accepted as the separator, as the compiler accepts it): no key begins with
#      dotnet_public_api_analyzer (a prefix ban: skip_namespaces and require_api_files today, whatever a later
#      version adds) or dotnet_analyzer_diagnostic; dotnet_diagnostic.rs0016.severity is warning or error, every
#      occurrence, in every section (a later line or section wins); and rs0016 appears in no other key. Values are
#      not searched for it: the compiler's generated config carries the checkout path, which may contain it. The root
#      .editorconfig must carry at least one such severity line: it is what keeps the bulk settings inert.
#   4. THE RECORD FILES: after the build, no PublicAPI.*.txt differs from the checkout or is untracked — a build step
#      that writes the symbols into the record leaves the analyzer nothing to report, and this is where it shows.
#   5. On CI (CI=true), or when PUBLIC_API_GUARD_ARTIFACTS names a directory: exactly one <PackageId>.<version>.nupkg
#      per selected project there, and no other .nupkg, so the set judged is the set packed.
#
# NOT SEEN, by construction: these files are judged as they exist when this script runs. A step of this
# repository's own build that changes one after the compiler read it, or changes what the compiler read and restores
# it before this runs, is outside what any after-the-fact reading can see; such a step is a change to a project
# file, Directory.Build.targets or a package reference, and review reads those. An option the analyzer reads under a
# prefix this rule does not name is outside it too. Missing or unreadable input is "not proven", never a pass.
#
# Usage: scripts/ci/check-public-api-guard-coverage.sh   (from the repo root, after
#        `dotnet build Verbara.Sdk.slnx -c Release -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true`;
#        locally add --no-incremental, because an up-to-date compile leaves the previous record in obj/)
# Exit 0 = every shipped package proven. Exit 1 = a package failed or is not proven. Exit 2 = cannot tell.
# DOTNET_BIN is overridable so the unit tests can stand in for MSBuild.
set -euo pipefail

DOTNET_BIN="${DOTNET_BIN:-dotnet}"
ARTIFACTS="${PUBLIC_API_GUARD_ARTIFACTS:-}"
if [ -z "$ARTIFACTS" ] && [ "${CI:-}" = true ]; then ARTIFACTS=./artifacts; fi

say()         { echo "public-api-guard: $*"; }
cannot_tell() { echo "public-api-guard: $* — cannot tell." >&2; exit 2; }
lc()          { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
unquote()     { local v="$1"; v="${v#\"}"; printf '%s\n' "${v%\"}"; } # trailing LF so 'while read' sees the last item

command -v jq  >/dev/null 2>&1 || cannot_tell "jq is required to read MSBuild's JSON and the compiler's SARIF log"
command -v git >/dev/null 2>&1 || cannot_tell "git is required to compare the record files with the checkout"

REPO="$(pwd -P)"
[ -f "$REPO/.editorconfig" ] || cannot_tell "no .editorconfig at the repository root (run this from the repository root)"

sdk_version="$(DOTNET_NOLOGO=1 "$DOTNET_BIN" --version 2>/dev/null | head -1 || true)"
[ -n "$sdk_version" ] || cannot_tell "'$DOTNET_BIN --version' printed nothing"

projects=()
while IFS= read -r p; do projects+=("$p"); done < <(find src -type f -name '*.csproj' -not -path '*/obj/*' \
                                                       -not -path '*/bin/*' 2>/dev/null | LC_ALL=C sort)
[ "${#projects[@]}" -gt 0 ] || cannot_tell "no project found under src/ (run this from the repository root)"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# ---------------------------------------------------------------------------------------------------------------------
# The analyzer-config rule. Prints one `<line>\t<cause>\t<text>` per violation, then `SEVERITY\t<count>`.
# ---------------------------------------------------------------------------------------------------------------------
# shellcheck disable=SC2016 # awk program, not shell
CONFIG_AWK='
{
  sub(/\r$/, ""); text = $0; line = $0
  sub(/^[ \t]+/, "", line)
  if (line == "" || line ~ /^[#;]/ || line ~ /^\[/) next
  p = match(line, /[=:]/); if (p == 0) next
  key = tolower(substr(line, 1, p - 1)); sub(/[ \t]+$/, "", key)
  val = substr(line, p + 1); q = match(val, /[#;]/); if (q > 0) val = substr(val, 1, q - 1)
  gsub(/^[ \t]+|[ \t]+$/, "", val); val = tolower(val)
  if (index(key, "dotnet_public_api_analyzer") == 1)
    printf "%d\tan option addressed to the public-API analyzer (any key under dotnet_public_api_analyzer)\t%s\n", NR, text
  else if (index(key, "dotnet_analyzer_diagnostic") == 1)
    printf "%d\ta bulk severity setting (dotnet_analyzer_diagnostic), which can demote RS0016 with every other rule\t%s\n", NR, text
  else if (key == "dotnet_diagnostic.rs0016.severity") {
    sev++
    if (val != "warning" && val != "error")
      printf "%d\tRS0016 severity \"%s\": anything but warning or error stops it failing the build (a later line or section wins)\t%s\n", NR, val, text
  }
  else if (index(key, "rs0016") > 0)
    printf "%d\tRS0016 named in a key other than its severity key\t%s\n", NR, text
}
END { printf "SEVERITY\t%d\n", sev + 0 }'

declare -A config_verdict=()   # absolute path -> "" (clean) | violation text
scan_config() { # scan_config <absolute path> — fills config_verdict[<path>]
  local f="$1" out
  [ -n "${config_verdict[$f]+x}" ] && return 0
  if [ ! -f "$f" ]; then config_verdict[$f]="does not exist at scan time — not proven"; return 0; fi
  out="$(awk "$CONFIG_AWK" "$f")" || { config_verdict[$f]="could not be read — not proven"; return 0; }
  config_verdict[$f]="$(printf '%s\n' "$out" | grep -v '^SEVERITY	' | awk -F'\t' '{ printf "line %s: %s: %s\n", $1, $2, $3 }' || true)"
}

root_cfg="$REPO/.editorconfig"
root_sev="$(awk "$CONFIG_AWK" "$root_cfg" | sed -n 's/^SEVERITY	//p')"

# ---------------------------------------------------------------------------------------------------------------------
# Select the shipped projects (evaluation chooses; it never judges).
# ---------------------------------------------------------------------------------------------------------------------
sel_proj=(); sel_id=()
for proj in "${projects[@]}"; do
  if ! DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 "$DOTNET_BIN" msbuild "$proj" -p:Configuration=Release \
       -getProperty:IsPackable -getProperty:PackageId >"$tmp/eval.json" 2>"$tmp/eval.err"; then
    echo "public-api-guard: MSBuild could not evaluate $proj — cannot tell." >&2
    cat "$tmp/eval.err" "$tmp/eval.json" | tail -20 | sed 's/^/    /' >&2
    exit 2
  fi
  if ! fields="$(jq -er 'if (.Properties | type) != "object" then error("shape") else
                          [(.Properties.IsPackable // "" | tostring), (.Properties.PackageId // "" | tostring)] | @tsv end' \
                   "$tmp/eval.json" 2>/dev/null)"; then
    echo "public-api-guard: evaluating $proj did not return what dotnet msbuild -getProperty prints — cannot tell." >&2
    head -c 600 "$tmp/eval.json" | sed 's/^/    /' >&2
    exit 2
  fi
  [ "$(lc "${fields%%$'\t'*}")" = true ] || continue
  id="${fields#*$'\t'}"
  [ -n "$id" ] || cannot_tell "$proj is packable but evaluates to no PackageId"
  sel_proj+=("$proj"); sel_id+=("$id")
done
[ "${#sel_proj[@]}" -gt 0 ] || cannot_tell "none of the ${#projects[@]} project(s) under src/ evaluates as packable"

# ---------------------------------------------------------------------------------------------------------------------
# Judge each selected project from its record, its log and its analyzer-config files.
# ---------------------------------------------------------------------------------------------------------------------
failed=(); proven_lines=()
for i in "${!sel_proj[@]}"; do
  proj="${sel_proj[i]}"; dir="$(dirname "$proj")"; adir="$REPO/$dir"
  why=(); read_cfg=()
  records=()
  for r in "$adir"/obj/Release/*/public-api.csc-args.txt; do [ -f "$r" ] && records+=("$r"); done
  if [ "${#records[@]}" -eq 0 ]; then
    why+=("no argument record at $dir/obj/Release/*/public-api.csc-args.txt — not proven (build Release with -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true; a failed compile writes none)")
  fi
  for rec in "${records[@]}"; do
    rrel="${rec#"$REPO"/}"
    mapfile -t args < <(sed 's/\r$//' "$rec")
    if [ "${#args[@]}" -eq 0 ]; then why+=("$rrel is empty — not proven"); continue; fi

    has_wae=0 has_analyzer=0 n_shipped=0 n_unshipped=0
    errorlogs=(); outs=()
    for a in "${args[@]}"; do
      la="$(lc "$a")"
      case "$a" in
        /warnaserror+) has_wae=1 ;;
        /skipanalyzers+) why+=("$rrel: line '$a': analyzers are skipped for this compile") ;;
        /warn:0) why+=("$rrel: line '$a': warning level 0 hides every warning, RS0016 included") ;;
        /ruleset:*) why+=("$rrel: line '$a': a ruleset can set RS0016 to None whatever .editorconfig says") ;;
        /errorlog:*) errorlogs+=("$(unquote "${a#/errorlog:}")") ;;
        /out:*) outs+=("$(unquote "${a#/out:}")") ;;
        /analyzer:*)
          while IFS= read -r v; do
            case "$(unquote "$v")" in */Microsoft.CodeAnalysis.PublicApiAnalyzers.dll|Microsoft.CodeAnalysis.PublicApiAnalyzers.dll) has_analyzer=1 ;; esac
          done < <(unquote "${a#/analyzer:}" | tr ',;' '\n\n') ;;
        /additionalfile:*)
          while IFS= read -r v; do
            v="$(unquote "$v")"; [ -n "$v" ] || continue
            base="${v##*/}"
            case "$base" in
              PublicAPI.Shipped.txt|PublicAPI.Unshipped.txt)
                [ "$base" = PublicAPI.Shipped.txt ] && n_shipped=$((n_shipped + 1)) || n_unshipped=$((n_unshipped + 1))
                case "$v" in /*) cand="$v" ;; *) cand="$adir/$v" ;; esac
                got="$(realpath -e -- "$cand" 2>/dev/null || true)"
                own="$(realpath -e -- "$adir/$base" 2>/dev/null || true)"
                if [ -z "$own" ]; then
                  why+=("$rrel: line '$a': the project has no $base of its own — not proven")
                elif [ "$got" != "$own" ]; then
                  why+=("$rrel: line '$a': a declared record other than the project's own $base (resolves to '${got:-nothing}'); the analyzer unions every file of that name, so it can declare what the package's record does not")
                fi ;;
            esac
          done < <(unquote "${a#/additionalfile:}" | tr ',;' '\n\n')
          ;;
        /analyzerconfig:*)
          while IFS= read -r v; do
            v="$(unquote "$v")"; [ -n "$v" ] || continue
            case "$v" in /*) cand="$v" ;; *) cand="$adir/$v" ;; esac
            read_cfg+=("$cand")
          done < <(unquote "${a#/analyzerconfig:}" | tr ',;' '\n\n')
          ;;
      esac
      case "$la" in
        /nowarn:*rs0016*) why+=("$rrel: line '$a': RS0016 is in /nowarn:, so the compiler drops it") ;;
        /warnaserror-:*rs0016*) why+=("$rrel: line '$a': RS0016 is in /warnaserror-:, so it no longer fails the build") ;;
      esac
    done
    [ "$has_wae" -eq 1 ] || why+=("$rrel: no '/warnaserror+' line: warnings, RS0016 included, do not fail the build (TreatWarningsAsErrors is off)")
    [ "$has_analyzer" -eq 1 ] || why+=("$rrel: no '/analyzer:' line ends in Microsoft.CodeAnalysis.PublicApiAnalyzers.dll: the analyzer never ran")
    [ "$n_shipped" -gt 0 ] || why+=("$rrel: no '/additionalfile:' line names PublicAPI.Shipped.txt — the analyzer was not given the package's record")
    [ "$n_unshipped" -gt 0 ] || why+=("$rrel: no '/additionalfile:' line names PublicAPI.Unshipped.txt — the analyzer was not given the package's record")

    # The compiler's log, as the record names it.
    log_rel=""
    if [ "${#errorlogs[@]}" -ne 1 ]; then
      why+=("$rrel: ${#errorlogs[@]} '/errorlog:' line(s), not exactly one — the compiler's log is not proven")
    elif [ "${#outs[@]}" -ne 1 ]; then
      why+=("$rrel: ${#outs[@]} '/out:' line(s), not exactly one — the log's location cannot be checked")
    else
      out_dir="$(dirname "${outs[0]}")"
      expect="$out_dir/public-api.sarif,version=2"
      if [ "${errorlogs[0]}" != "$expect" ]; then
        why+=("$rrel: line '/errorlog:${errorlogs[0]}' is not '$expect' (the /out: directory followed by public-api.sarif,version=2) — the log read would not be this compile's")
      else
        log_rel="$out_dir/public-api.sarif"
      fi
    fi
    if [ -n "$log_rel" ]; then
      case "$log_rel" in /*) log="$log_rel" ;; *) log="$adir/$log_rel" ;; esac
      lrel="${log#"$REPO"/}"
      if [ ! -f "$log" ]; then
        why+=("$lrel: the log the record names does not exist — not proven")
      elif ! jq -e . "$log" >/dev/null 2>&1; then
        why+=("$lrel: not parseable JSON — not proven")
      else
        ver="$(jq -r '.version // "" | tostring' "$log")"
        if [ "$ver" != 2.1.0 ]; then
          why+=("$lrel: SARIF version '$ver', not 2.1.0 — not proven (a -p:ErrorLog with an unescaped comma writes 1.0.0)")
        elif ! jq -e '[.runs[0].tool.driver.rules[]? | select(.id == "RS0016")] | length > 0' "$log" >/dev/null 2>&1; then
          why+=("$lrel: RS0016 is not among the rules of this compile — the analyzer did not run; not proven")
        elif ! jq -e '.runs[0].results | type == "array"' "$log" >/dev/null 2>&1; then
          why+=("$lrel: runs[0].results is not a list — not proven")
        else
          while IFS=$'\t' read -r uri sline msg how; do
            hint=""
            case "$uri" in
              file://*)
                fpath="${uri#file://}"
                if [ ! -e "$fpath" ] && case "$fpath" in */obj/*) true ;; *) false ;; esac; then
                  hint=" — emitted by a source generator (no such file on disk); rebuild $dir with -p:EmitCompilerGeneratedFiles=true and read obj/Release/<tfm>/generated/…"
                fi ;;
            esac
            why+=("$lrel: an RS0016 result the build did not fail on, at $uri:$sline — $msg — $how$hint")
          done < <(jq -r '.runs[0].results[] | select(.ruleId == "RS0016")
                          | [ (.locations[0].physicalLocation.artifactLocation.uri // "(no location)"),
                              (.locations[0].physicalLocation.region.startLine // "?" | tostring),
                              (.message.text // "" | gsub("[\t\n]"; " ")),
                              (if (.suppressions // []) | length > 0
                               then "suppressed by " + (.suppressions[0].properties.suppressionType // .suppressions[0].kind // "?" | tostring)
                               else "demoted to level " + (.level // "warning" | tostring) end) ] | @tsv' "$log")
        fi
      fi
    fi
  done

  # The analyzer-config files the record names.
  cfg_list=()
  for c in "${read_cfg[@]}"; do
    scan_config "$c"
    crel="${c#"$REPO"/}"; cfg_list+=("$crel")
    if [ -n "${config_verdict[$c]}" ]; then
      while IFS= read -r v; do why+=("$crel: $v"); done <<<"${config_verdict[$c]}"
    fi
  done
  [ "${#records[@]}" -eq 0 ] || [ "${#read_cfg[@]}" -gt 0 ] \
    || why+=("${records[0]#"$REPO"/}: no '/analyzerconfig:' line — not proven (the root .editorconfig carries RS0016's severity)")

  if [ "${#why[@]}" -gt 0 ]; then
    failed+=("$proj (${sel_id[i]})"$'\n'"$(printf '      - %s\n' "${why[@]}")")
  else
    proven_lines+=("  proven: $proj (${sel_id[i]})"$'\n'"      read: ${records[0]#"$REPO"/}"$'\n'"      read: $lrel"$'\n'"$(printf '      read: %s\n' "${cfg_list[@]}")")
  fi
done

# ---------------------------------------------------------------------------------------------------------------------
# Repository-wide: the root severity line, the record files against the checkout, the packed set.
# ---------------------------------------------------------------------------------------------------------------------
repo_why=()
[ "${root_sev:-0}" -gt 0 ] \
  || repo_why+=(".editorconfig: no dotnet_diagnostic.RS0016.severity line at the repository root; it is what keeps dotnet_analyzer_diagnostic.severity and category demotions inert for RS0016")

if ! git -c core.quotepath=off diff --quiet -- '*PublicAPI.Shipped.txt' '*PublicAPI.Unshipped.txt' 2>"$tmp/git.err"; then
  if [ -s "$tmp/git.err" ]; then cannot_tell "git diff failed: $(head -1 "$tmp/git.err")"; fi
  while IFS= read -r f; do
    repo_why+=("$f: differs from the checkout after the build — a declared record the build changed is not the record the pull request shows")
  done < <(git -c core.quotepath=off diff --name-only -- '*PublicAPI.Shipped.txt' '*PublicAPI.Unshipped.txt')
fi
while IFS= read -r l; do
  [ -n "$l" ] && repo_why+=("${l:3}: ${l:0:2} in git status after the build — a declared record the checkout does not carry")
done < <(git -c core.quotepath=off status --porcelain=v1 --untracked-files=all -- '*PublicAPI.*.txt' 2>/dev/null | grep -v '^ M' || true)

if [ -n "$ARTIFACTS" ]; then
  if [ ! -d "$ARTIFACTS" ]; then
    repo_why+=("$ARTIFACTS: no such directory — the packed set cannot be compared with the judged set")
  else
    total="$(find "$ARTIFACTS" -maxdepth 1 -type f -name '*.nupkg' | wc -l | tr -d ' ')"
    for i in "${!sel_id[@]}"; do
      id="${sel_id[i]}"
      n="$(find "$ARTIFACTS" -maxdepth 1 -type f -name "$id.*.nupkg" | awk -v id="$id" '{ b = $0; sub(/.*\//, "", b); if (substr(b, length(id) + 2, 1) ~ /[0-9]/) c++ } END { print c + 0 }')"
      [ "$n" -eq 1 ] || repo_why+=("$ARTIFACTS: $n package(s) named $id.<version>.nupkg, not exactly one, for ${sel_proj[i]}")
    done
    [ "$total" -eq "${#sel_id[@]}" ] \
      || repo_why+=("$ARTIFACTS: $total .nupkg file(s) for ${#sel_id[@]} judged project(s) — the set judged must be the set packed")
  fi
fi

SENTENCE="These files were read as they existed when this check ran; a build step that changed one after the compiler read it, or changed and restored it before this check, is outside what it can see."

if [ "${#failed[@]}" -eq 0 ] && [ "${#repo_why[@]}" -eq 0 ]; then
  say "all ${#sel_proj[@]} shipped project(s) proven: RS0016 live, nothing suppressed or demoted, each analyzer-config file clean. .NET SDK $sdk_version."
  printf '%s\n' "${proven_lines[@]}"
  say "$SENTENCE"
  exit 0
fi

say "FAILED — ${#failed[@]} of ${#sel_proj[@]} shipped project(s) not proven, ${#repo_why[@]} repository-wide finding(s). .NET SDK $sdk_version."
for f in "${failed[@]}"; do echo; echo "  ${f%%$'\n'*}"; printf '%s\n' "${f#*$'\n'}"; done
if [ "${#repo_why[@]}" -gt 0 ]; then
  echo; echo "  Repository-wide:"; printf '      - %s\n' "${repo_why[@]}"
fi
echo
echo "  Each of these builds as green as a package that declares its whole surface: the diagnostic was silenced,"
echo "  demoted, starved of its analyzer, or given another record. Fix: remove the setting named above, so that a"
echo "  public member that is not declared fails the build — then declare what that build reports in the package's"
echo "  PublicAPI.Unshipped.txt."
if [ "${#proven_lines[@]}" -gt 0 ]; then echo; echo "  Proven:"; printf '%s\n' "${proven_lines[@]}"; fi
say "$SENTENCE"
exit 1

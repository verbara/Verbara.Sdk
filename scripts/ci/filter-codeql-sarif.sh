#!/usr/bin/env bash
# filter-codeql-sarif.sh — answers ONE question for codeql.yml, between analysis and upload: does this CodeQL
# result sit in code that the .NET SDK generated during the build, which this repository cannot change?
#
# THE RULE. A result is removed when the uri of its PRIMARY location
# (`locations[0].physicalLocation.artifactLocation.uri`), read as `/`-separated segments, has a segment that is
# exactly `obj`, then further down a segment that is exactly `generated`, then a folder whose name starts with
# `System.` or `Microsoft.`, then at least one more segment. That is where the compiler puts source-generator
# output: obj/<configuration>/<tfm>/generated/<generator assembly>/<generator type>/<hint name>. Every other
# result is kept, in its original order, and so is every other part of the log — tool, rules, artifacts,
# invocations, automationDetails, properties — so indices into `artifacts` and `rules` still resolve.
#
# WHY. On main at 99162772, 633 of the 1,082 open CodeQL alerts were in that output: 613 from
# System.Text.Json.SourceGeneration, 19 from Microsoft.Extensions.Options.SourceGeneration and 1 from
# System.Text.RegularExpressions.Generator, all maintainability notes (useless casts, a missed ternary) about
# code no change here can reach. A CodeQL config cannot drop them: GitHub does not honour `paths` or
# `paths-ignore` for a compiled language analysed from a traced build, which is what codeql.yml runs. So the
# analyze step writes its SARIF without uploading it, this filter runs, and upload-sarif sends what is left
# under the same category — every other alert keeps its history, and the generator ones close as fixed.
#
# WHY ONLY System.* AND Microsoft.*. Those are the generators the .NET SDK and runtime ship. A generator of our
# own writes into the same generated/ tree — today Verbara.Sdk.Ami.SourceGenerators — and that output IS ours
# to fix, so it stays analysed. The folder is the generator's assembly name, so a project here with a System.*
# or Microsoft.* name would have its output hidden; scripts/tests/test_filter_codeql_sarif.sh fails the PR that
# adds one.
#
# BY PATH SEGMENT, NEVER BY SUBSTRING, AND CASE-SENSITIVE. `objects/`, `myobj/`, `src/obj.cs`, `Obj/`, a
# `generated/` outside obj/, and a file lying directly in generated/ are all kept. A uri with a `..` segment is
# kept too: where it points cannot be read from its spelling. Only the primary location decides, so a result in
# our code whose other or related locations pass through generator output is kept.
#
# FAIL CLOSED. Unreadable input, and input that is not exactly one SARIF 2.1.0 log (`version` "2.1.0", `runs` an
# array of objects, each `results` null or an array of objects, each primary location readable, each uri a
# string), exits 2 with an ::error:: line — and <out.sarif> does not exist afterwards. A file already at that
# path is removed first, so nothing downstream can upload a stale one as this run's output; the new log is
# written to a temporary file beside it and moved into place only once it is complete.
#
# NOT CHANGED: a result with no location, or whose primary location has no uri. An artifactLocation that
# carries only an `index` is kept rather than resolved through `artifacts` — the rule never removes more than
# a uri shows. The output is jq's compact serialisation of the same JSON values: whitespace is not preserved.
#
# Usage: filter-codeql-sarif.sh <in.sarif> <out.sarif>
# Exit 0 = <out.sarif> written, with a ::notice:: counting what was removed by rule and by generator.
# Exit 2 = nothing written: unreadable or non-SARIF input, or <out.sarif> cannot be written.
set -euo pipefail

tmp=''
trap 'if [ -n "$tmp" ]; then rm -f -- "$tmp"; fi' EXIT

cannot_tell() { # cannot_tell <reason>
  echo "::error::filter-codeql-sarif: cannot tell — $1. Nothing was written, so nothing unfiltered can be uploaded."
  exit 2
}

# Shared jq definitions. `cmd` escapes a value for a workflow-command line (`%`, CR, LF): rule ids and paths come
# from the SARIF, and must neither break the ::notice:: nor start a workflow command of their own.
JQ_DEFS='
  def obj_or_null: . == null or type == "object";
  def primary_ok:
    if .locations == null then true
    elif (.locations | type) != "array" then false
    elif (.locations | length) == 0 then true
    else .locations[0]
      | if type != "object" then false
        elif (.physicalLocation | obj_or_null) | not then false
        elif (.physicalLocation.artifactLocation | obj_or_null) | not then false
        else (.physicalLocation.artifactLocation.uri | . == null or type == "string")
        end
    end;
  def is_result: type == "object" and primary_ok;
  def is_run:
    type == "object"
    and (.results == null or ((.results | type) == "array" and all(.results[]; is_result)));
  def is_sarif:
    type == "object" and .version == "2.1.0" and (.runs | type) == "array" and all(.runs[]; is_run);
  def primary_uri:
    if (.locations | type) == "array" and (.locations | length) > 0
    then .locations[0].physicalLocation.artifactLocation.uri
    else null end;
  # The System.* or Microsoft.* generator folder the uri lies in, below obj/ and generated/, or null.
  def dotnet_generator:
    if type != "string" then null
    else split("/") as $s
      | if any($s[]; . == "..") then null
        else first(
               range(0; $s | length) as $i | select($s[$i] == "obj")
               | range($i + 1; ($s | length) - 2) as $j | select($s[$j] == "generated")
               | $s[$j + 1] | select(startswith("System.") or startswith("Microsoft."))
             ) // null
        end
    end;
  def removed: (primary_uri | dotnet_generator) != null;
  def cmd: tostring | gsub("%"; "%25") | gsub("\r"; "%0D") | gsub("\n"; "%0A");
'

[ "$#" -eq 2 ] || cannot_tell "usage: filter-codeql-sarif.sh <in.sarif> <out.sarif>"
in="$1"; out="$2"
[ -n "$in" ] || cannot_tell "no input SARIF path was given"
[ -n "$out" ] || cannot_tell "no output SARIF path was given"
if [ -e "$out" ] && [ "$in" -ef "$out" ]; then
  cannot_tell "'$in' and '$out' are the same file, and the input must survive a failed run"
fi
[ ! -d "$out" ] || cannot_tell "the output path '$out' is a directory"
# Before anything else can fail: from here on, every exit leaves no file at <out.sarif>.
rm -f -- "$out" || cannot_tell "the stale output '$out' could not be removed"
command -v jq >/dev/null 2>&1 || cannot_tell "jq is not on PATH"

if [ ! -f "$in" ] || [ ! -r "$in" ]; then
  cannot_tell "the input '$in' cannot be read"
fi
if ! err="$(jq empty "$in" 2>&1)"; then
  cannot_tell "the input '$in' is not JSON (${err%%$'\n'*})"
fi
shape="$(jq -rn "$JQ_DEFS"'
  [inputs] | if length != 1 then "values:\(length)"
             elif (.[0] | is_sarif) then "sarif"
             else "shape" end' "$in")" || cannot_tell "the input '$in' could not be checked"
case "$shape" in
  sarif) ;;
  values:0) cannot_tell "the input '$in' is empty" ;;
  values:*) cannot_tell "the input '$in' holds ${shape#values:} JSON values, not one SARIF log" ;;
  *) cannot_tell "the input '$in' is not a SARIF 2.1.0 log (expected \"version\": \"2.1.0\", \"runs\" an array of objects, each \"results\" an array of result objects whose primary location uri is a string)" ;;
esac

report="$(jq -c "$JQ_DEFS"'
  [ .runs[] | (.results // [])[] ] as $all
  | [ $all[] | select(removed)
      | { rule: ((.ruleId // .rule.id // "(no rule id)") | tostring), generator: (primary_uri | dotnet_generator) } ] as $gone
  | def tally($key): $gone | group_by(.[$key]) | map({ name: .[0][$key], count: length }) | sort_by(-.count, .name);
    { total: ($all | length), removed: ($gone | length), by_rule: tally("rule"), by_generator: tally("generator") }
' "$in")" || cannot_tell "the results in '$in' could not be classified"

out_dir="$(dirname -- "$out")"
[ -d "$out_dir" ] || cannot_tell "the output directory '$out_dir' does not exist"
tmp="$(mktemp "$out.XXXXXX" 2>/dev/null)" || { tmp=''; cannot_tell "a temporary file could not be created beside '$out'"; }
jq -c "$JQ_DEFS"'.runs |= map(if (.results | type) == "array" then .results |= map(select(removed | not)) else . end)' \
  "$in" > "$tmp" || cannot_tell "the filtered log could not be written to '$tmp'"
chmod 0644 -- "$tmp" || cannot_tell "the filtered log '$tmp' could not be made readable"
mv -f -- "$tmp" "$out" || cannot_tell "the filtered log could not be moved to '$out'"
tmp=''

printf '%s' "$report" | jq -r --arg out "$out" "$JQ_DEFS"'
  def list: map("\(.name) \(.count)") | join(", ");
  if .removed == 0 then
    "::notice::filter-codeql-sarif: removed nothing — none of the \(.total) result(s) lies in .NET source-generator output (obj/…/generated/System.*|Microsoft.*/). Wrote \($out | cmd)."
  else
    "::notice::filter-codeql-sarif: removed \(.removed) of \(.total) result(s) in .NET source-generator output, kept \(.total - .removed) — by generator: \(.by_generator | list | cmd); by rule: \(.by_rule | list | cmd). Wrote \($out | cmd)."
  end
' || cannot_tell "the summary could not be rendered"

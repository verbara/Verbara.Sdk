#!/usr/bin/env bash
# filter-codeql-sarif.sh — answers ONE question for codeql.yml, between analysis and upload: does this CodeQL
# result sit in code that the .NET SDK generated during the build, which this repository cannot change, and is
# it a result that may be dropped from there?
#
# THE RULE. A result is removed only when BOTH hold; every other result is kept.
#
#  1. Location. Read the uri of its PRIMARY location (`locations[0].physicalLocation.artifactLocation.uri`) as
#     `/`-separated segments and take the first segment that is exactly `obj`, then the first segment after it
#     that is exactly `generated`. The segment after that `generated` must be a folder whose name starts with
#     `System.` or `Microsoft.`, followed by at least one more segment. That is where the compiler puts
#     source-generator output: obj/<configuration>/<tfm>/generated/<generator assembly>/<generator type>/<hint
#     name>. Only that first `obj` and that first `generated` count, so an `obj` or `generated` segment further
#     down, inside a hint name, changes nothing.
#  2. Rule. Its rule resolves, in its run's `tool`, and every rule it resolves to shows it is NOT
#     security-relevant: `properties` absent or an object, `properties.tags` absent or an array with no
#     "security" in it, and no "security-severity" key in `properties`. The rule is resolved as follows.
#     - By index. The index is `ruleIndex` or `rule.index` (absent, null or -1 means not given). Without a
#       `rule.toolComponent` it indexes `tool.driver.rules`; with a `rule.toolComponent.index` (absent, null or
#       -1 means not given) it indexes `tool.extensions[rule.toolComponent.index].rules`. It resolves to the rule
#       object found there, provided that rule's `id` equals the result's rule id whenever the result gives one.
#     - By id, when no index is given or `rule.toolComponent` gives no index. The id is `ruleId` or `rule.id`.
#       It resolves to every rule object in `tool.driver.rules` and in every `tool.extensions[].rules` whose
#       `id` is exactly that string.
#     It does NOT resolve — so the result is kept — when nothing is found; when `ruleIndex` and `rule.index`,
#     or `ruleId` and `rule.id`, are both given and differ; when a given index (`rule.toolComponent.index`
#     included) is not a non-negative integer, an id is not a string, or `rule` or `rule.toolComponent` is
#     present but not an object; or when `tool`, or a component the lookup reads (the indexed one; for an id
#     `tool.driver` and every `tool.extensions` entry), is not an object or has a `rules` property that is not
#     an array, or, for an id, `tool.extensions` is neither absent nor an array.
#     Each result that matches the location rule but is kept by the rule check gets one ::warning:: with its
#     rule id, its uri and the reason. A result outside generator output is never looked at, security or not.
#
# Every kept result stays in its original order, and so does every other part of the log — tool, rules,
# artifacts, invocations, automationDetails, properties — so indices into `artifacts` and `rules` still resolve.
#
# WHY. On main at 99162772, 633 of the 1,082 open CodeQL alerts were in that output: 613 from
# System.Text.Json.SourceGeneration, 19 from Microsoft.Extensions.Options.SourceGeneration and 1 from
# System.Text.RegularExpressions.Generator, all maintainability notes (useless casts, a missed ternary) about
# code no change here can reach. A CodeQL config cannot drop them: GitHub does not honour `paths` or
# `paths-ignore` for a compiled language analysed from a traced build, which is what codeql.yml runs. So the
# analyze step writes its SARIF without uploading it, this filter runs, and upload-sarif sends what is left
# under the same category — every other alert keeps its history, and the generator ones close as fixed.
#
# WHY NEVER A SECURITY RESULT. "No change here can reach that code" holds for a note about the generated code
# itself. It does not hold for a data-flow alert whose sink lies in generated code while its source, and its
# fix, lie in ours — for example a [LoggerMessage] method, which the Microsoft.* logging generator expands into
# an ILogger call. Such an alert must stay open, and one whose rule cannot be read is treated the same way.
#
# WHY ONLY System.* AND Microsoft.*. Those are the generators the .NET SDK and runtime ship. A generator of our
# own writes into the same generated/ tree — today Verbara.Sdk.Ami.SourceGenerators — and that output IS ours
# to fix, so it stays analysed, whatever subfolders its hint names use. The folder is the generator's assembly
# name, so a project here with a System.* or Microsoft.* name would have its output hidden;
# scripts/tests/test_filter_codeql_sarif.sh fails the PR that adds one.
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
# Exit 0 = <out.sarif> written, with a ::notice:: counting what was removed by rule and by generator, after one
#          ::warning:: per result in generator output that the rule check kept.
# Exit 2 = nothing written: unreadable or non-SARIF input, or <out.sarif> cannot be written.
set -euo pipefail

tmp=''
trap 'if [ -n "$tmp" ]; then rm -f -- "$tmp"; fi' EXIT

cannot_tell() { # cannot_tell <reason>
  echo "::error::filter-codeql-sarif: cannot tell — $1. Nothing was written, so nothing unfiltered can be uploaded."
  exit 2
}

# Shared jq definitions. `cmd` escapes a value for a workflow-command line (`%`, CR, LF): rule ids and paths come
# from the SARIF, and must neither break the ::notice:: or a ::warning:: nor start a workflow command of their own.
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

  # 1. Location. The System.* or Microsoft.* generator folder the uri lies in, or null. Only the first `obj`
  # segment, and the first `generated` segment after it, count.
  def dotnet_generator:
    if type != "string" then null
    else split("/") as $s
      | if any($s[]; . == "..") then null
        else (first(range(0; $s | length) | select($s[.] == "obj")) // null) as $i
          | if $i == null then null
            else (first(range($i + 1; $s | length) | select($s[.] == "generated")) // null) as $j
              | if $j == null or $j + 2 >= ($s | length) then null
                else $s[$j + 1] | if startswith("System.") or startswith("Microsoft.") then . else null end
                end
            end
        end
    end;

  # 2. Rule. `ref`: the rule reference a result carries, as { id, index, component } — component is "driver",
  # an index into tool.extensions, or "any" when rule.toolComponent names its component some other way — or
  # null when the reference contradicts itself or has the wrong types.
  def nonneg_int: type == "number" and . >= 0 and . == floor;
  def ref:
    (if .rule == null then {} elif (.rule | type) == "object" then .rule else null end) as $r
    | if $r == null then null
      else ([.ruleId, $r.id] | map(select(. != null)) | unique) as $ids
        | ([.ruleIndex, $r.index] | map(select(. != null and . != -1)) | unique) as $indices
        | if ($ids | length) > 1 or ($indices | length) > 1 then null
          elif any($ids[]; type != "string") or any($indices[]; nonneg_int | not) then null
          elif $r.toolComponent == null then { id: $ids[0], index: $indices[0], component: "driver" }
          elif ($r.toolComponent | type) != "object" then null
          else $r.toolComponent.index as $c
            | if $c == null or $c == -1 then { id: $ids[0], index: $indices[0], component: "any" }
              elif ($c | nonneg_int) then { id: $ids[0], index: $indices[0], component: $c }
              else null end
          end
      end;
  # A component that can be searched: an object whose rules are absent or an array.
  def readable_component: type == "object" and (.rules == null or (.rules | type) == "array");
  # The rules the reference denotes in $tool — a non-empty array — or null when it does not resolve.
  def denoted($tool):
    . as $ref
    | if $ref == null or ($tool | type) != "object" then null
      elif $ref.index != null and $ref.component != "any" then
        (if $ref.component == "driver" then $tool.driver
         elif ($tool.extensions | type) == "array" then $tool.extensions[$ref.component]
         else null end) as $component
        | if ($component | readable_component) and ($component.rules | type) == "array"
             and $ref.index < ($component.rules | length)
          then $component.rules[$ref.index]
            | if type == "object" and ($ref.id == null or .id == $ref.id) then [.] else null end
          else null end
      elif $ref.id != null then
        if ($tool.driver | readable_component) | not then null
        elif $tool.extensions != null and ($tool.extensions | type) != "array" then null
        elif any(($tool.extensions // [])[]; readable_component | not) then null
        else [ ($tool.driver.rules // [])[], (($tool.extensions // [])[] | (.rules // [])[])
               | select(type == "object" and .id == $ref.id) ]
          | if length == 0 then null else . end
        end
      else null end;
  def metadata_readable:
    .properties == null
    or ((.properties | type) == "object" and (.properties.tags == null or (.properties.tags | type) == "array"));
  def security_relevant:
    (.properties | type) == "object"
    and (((.properties.tags | type) == "array" and any(.properties.tags[]; . == "security"))
         or (.properties | has("security-severity")));
  # "removable", or why the result must stay: "security", "unreadable" (a rule matched, but its properties or
  # tags have the wrong type) or "unresolved".
  def verdict($tool):
    (ref | denoted($tool)) as $rules
    | if $rules == null then "unresolved"
      elif any($rules[]; security_relevant) then "security"
      elif any($rules[]; metadata_readable | not) then "unreadable"
      else "removable" end;
  def removed($tool): (primary_uri | dotnet_generator) != null and verdict($tool) == "removable";
  # The rule id to report: ruleId, else rule.id, else the id of the one rule an index lands on.
  def rule_label($tool):
    (.ruleId // (.rule | if type == "object" then .id else null end)
     // ((ref | denoted($tool)) as $rules | if $rules != null and ($rules | length) == 1 then $rules[0].id else null end)
     // "(no rule id)") | tostring;
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
  [ .runs[] | .tool as $tool | (.results // [])[]
    | (primary_uri | dotnet_generator) as $generator
    | select($generator != null)
    | { rule: rule_label($tool), uri: primary_uri, generator: $generator, verdict: verdict($tool) } ] as $inside
  | ([ .runs[] | (.results // [])[] ] | length) as $total
  | [ $inside[] | select(.verdict == "removable") ] as $gone
  | def tally($key): $gone | group_by(.[$key]) | map({ name: .[0][$key], count: length }) | sort_by(-.count, .name);
    { total: $total, removed: ($gone | length), by_rule: tally("rule"), by_generator: tally("generator"),
      held: [ $inside[] | select(.verdict != "removable") | del(.generator) ] }
' "$in")" || cannot_tell "the results in '$in' could not be classified"

out_dir="$(dirname -- "$out")"
[ -d "$out_dir" ] || cannot_tell "the output directory '$out_dir' does not exist"
tmp="$(mktemp "$out.XXXXXX" 2>/dev/null)" || { tmp=''; cannot_tell "a temporary file could not be created beside '$out'"; }
jq -c "$JQ_DEFS"'.runs |= map(.tool as $tool
    | if (.results | type) == "array" then .results |= map(select(removed($tool) | not)) else . end)' \
  "$in" > "$tmp" || cannot_tell "the filtered log could not be written to '$tmp'"
chmod 0644 -- "$tmp" || cannot_tell "the filtered log '$tmp' could not be made readable"
mv -f -- "$tmp" "$out" || cannot_tell "the filtered log could not be moved to '$out'"
tmp=''

printf '%s' "$report" | jq -r --arg out "$out" "$JQ_DEFS"'
  def list: map("\(.name) \(.count)") | join(", ");
  def why:
    if . == "security" then "its rule is security-relevant (tag \"security\" or a security-severity), and a security result is never removed"
    elif . == "unreadable" then "the properties or properties.tags of its rule have the wrong type, so the rule cannot be shown not to be security-relevant"
    else "its rule does not resolve in tool.driver or tool.extensions, so it cannot be shown not to be security-relevant" end;
  (.held[] | "::warning::filter-codeql-sarif: kept \(.rule | cmd) at \(.uri | cmd) although it lies in .NET source-generator output — \(.verdict | why)."),
  ((.held | length) as $held
   | if .removed == 0 and $held == 0 then
       "::notice::filter-codeql-sarif: removed nothing — none of the \(.total) result(s) lies in .NET source-generator output (obj/…/generated/System.*|Microsoft.*/). Wrote \($out | cmd)."
     elif .removed == 0 then
       "::notice::filter-codeql-sarif: removed nothing — \($held) of the \(.total) result(s) lie in .NET source-generator output, and each was kept because its rule is security-relevant or cannot be shown not to be (see the warnings). Wrote \($out | cmd)."
     else
       "::notice::filter-codeql-sarif: removed \(.removed) of \(.total) result(s) in .NET source-generator output, kept \(.total - .removed) — by generator: \(.by_generator | list | cmd); by rule: \(.by_rule | list | cmd)."
       + (if $held == 0 then ""
          else " \($held) of the kept result(s) lie in that output too, and were kept because their rule is security-relevant or cannot be shown not to be (see the warnings)." end)
       + " Wrote \($out | cmd)."
     end)
' || cannot_tell "the summary could not be rendered"

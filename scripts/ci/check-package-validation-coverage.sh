#!/usr/bin/env bash
# check-package-validation-coverage.sh — no shipped project skips package validation once its package is on the
# feed at the baseline version.
#
# WHY. Package validation is the only thing in this repo that catches an unintended binary break, and
# check-apicompat-baseline.sh keeps the release it compares against current. Neither asks which projects actually
# compare. A project can switch validation off for itself, and a brand-new one has to: until its package is on the
# feed at <PackageValidationBaselineVersion> there is nothing to download. Nothing ever switches it back on, and
# nothing notices, because a project that validates nothing packs exactly as green as one that validated and passed.
#
# That is what happened. Twelve of the 29 shipped projects set <EnablePackageValidation>false in their own project
# file, each while its package was new (2026-04-13 … 2026-05-23, "no baseline published yet"), and kept it for
# months after the package shipped. "All 29 packages pack clean" (ADR-0055 D6) was true of packing; validation
# covered 17. See the ADR-0055 addendum of 2026-09-12.
#
# THE RULE: a shipped project may skip validation only while its package is not on the feed at the baseline version.
# That is exactly when validation becomes possible, so a new package keeps working, and the PR that moves the
# baseline past a package's first release is the PR this check makes remove that package's opt-out.
#
# "SKIPS VALIDATION" IS READ FROM MSBUILD'S EVALUATION, NOT GREPPED. Each project under src/ is evaluated with
# `dotnet msbuild -getProperty/-getItem` (about 0.3 s a project; no restore, no build) and asked what the SDK's own
# targets ask. A packable project counts as validated against the baseline only when all three hold:
#   * EnablePackageValidation is `true` as the SDK's condition compares it — case-insensitively and untrimmed, so
#     `True` is on and ` true ` is off;
#   * MSBuild plans a PackageDownload of <PackageId> at [<baseline>]. Microsoft.NET.ApiCompat.targets adds that item
#     only when validation will fetch the baseline, so it already accounts for DisablePackageBaselineValidation,
#     PackageValidationBaselinePath, and a per-project PackageValidationBaselineVersion or -Name;
#   * RunApiCompat is not `false`.
# A grep for `<EnablePackageValidation>false` was rejected because it sees one spelling in one file, and each of
# these was measured to stop the baseline comparison without that spelling anywhere in the project: the property set
# in a nested Directory.Build.props, set through another property, ` true ` written with spaces, PackAsTool (the SDK
# sets the property false itself), DisablePackageBaselineValidation in Directory.Build.targets, and a per-project
# baseline override. The harness runs each one against real MSBuild
# (scripts/tests/test_package_validation_coverage.sh with PKV_MSBUILD_CASES=1).
#
# NOT SEEN, by construction: a global property on the pack command line (`-p:EnablePackageValidation=false`)
# belongs to no project, so evaluating projects cannot see it; and CP findings silenced one at a time through NoWarn
# or CompatibilitySuppressions.xml, which leave the check itself running.
#
# THE FEED is asked only about projects that are not validated, so a clean tree makes no network call at all.
# FEED_BASE_URL is a NuGet flat container, read as <base>/<lowercase id>/index.json. A 404 means no version of that
# id exists: a new package, allowed. Unreachable is a ::warning::, never a failure. An answer that is not a list of
# versions is not a pass. FEED_BASE_URL and DOTNET_BIN are overridable so the unit tests can run on file:// fixtures
# with a stand-in for MSBuild; under file://, a missing index file plays the 404.
#
# Usage: scripts/ci/check-package-validation-coverage.sh   (from the repo root; CI runs it in Pack Warnings Gate)
# Exit 0 = every shipped package that can be validated is. Exit 1 = a shipped project skips validation although its
# package is on the feed at the baseline. Exit 2 = cannot tell.
set -euo pipefail

DOTNET_BIN="${DOTNET_BIN:-dotnet}"
FEED_BASE_URL="${FEED_BASE_URL:-https://api.nuget.org/v3-flatcontainer}"
FEED_BASE_URL="${FEED_BASE_URL%/}"

say()         { echo "package-validation-coverage: $*"; }
cannot_tell() { echo "package-validation-coverage: $* — cannot tell." >&2; exit 2; }
lc()          { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
trim()        { local s="$1"; s="${s#"${s%%[![:space:]]*}"}"; printf '%s' "${s%"${s##*[![:space:]]}"}"; }
shown()       { local s="${1//$'\n'/\\n}"; printf "'%s'" "${s//$'\t'/\\t}"; } # whitespace stays visible

command -v jq >/dev/null 2>&1 || cannot_tell "jq is required to read MSBuild's and the feed's JSON"

baseline="$(sed -n 's:.*<PackageValidationBaselineVersion>\(.*\)</PackageValidationBaselineVersion>.*:\1:p' \
              Directory.Build.props 2>/dev/null | head -1 || true)"
[ -n "$baseline" ] || cannot_tell "no <PackageValidationBaselineVersion> in Directory.Build.props"
# Anything that is not a version (a $(Property) reference, say) would miss on every feed lookup and pass every
# opt-out as a new package, so it is refused rather than used.
printf '%s' "$baseline" | grep -qE '^[0-9]+(\.[0-9]+){1,3}(-[0-9A-Za-z.-]+)?$' \
  || cannot_tell "<PackageValidationBaselineVersion> is $(shown "$baseline"), which is not a version"

mapfile -t projects < <(find src -type f -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null \
                          | LC_ALL=C sort)
[ "${#projects[@]}" -gt 0 ] || cannot_tell "no project found under src/ (run this from the repository root)"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

EVAL_ARGS=(-p:Configuration=Release
           -getProperty:IsPackable -getProperty:PackageId -getProperty:EnablePackageValidation
           -getProperty:RunApiCompat -getProperty:DisablePackageBaselineValidation
           -getProperty:PackageValidationBaselineVersion -getProperty:PackageValidationBaselineName
           -getProperty:PackageValidationBaselinePath -getItem:PackageDownload)

# Nine NUL-terminated fields per project. Output that is not the shape `-getProperty -getItem` prints is an error,
# never a default: a stand-in that printed nothing must not read as "validated".
# shellcheck disable=SC2016 # $p, $id and $baseline are jq variables, not shell ones
EVAL_JQ='
  if (.Properties | type) != "object" or (.Items.PackageDownload | type) != "array"
  then error("not the output of dotnet msbuild -getProperty/-getItem") else . end
  | .Properties as $p
  | ($p.PackageId // "" | tostring) as $id
  | [ $p.IsPackable, $id, $p.EnablePackageValidation, $p.RunApiCompat, $p.DisablePackageBaselineValidation,
      $p.PackageValidationBaselineVersion, $p.PackageValidationBaselineName, $p.PackageValidationBaselinePath,
      (if any(.Items.PackageDownload[];
              ((.Identity // "") | ascii_downcase) == ($id | ascii_downcase)
              and ((.Version // "") | ascii_downcase) == ("[" + ($baseline | ascii_downcase) + "]"))
       then "yes" else "no" end) ]
  | map((. // "" | tostring) + ([0] | implode)) | add'

packable=0; validated=0
skip_proj=(); skip_id=(); skip_why=(); skip_url=()
for proj in "${projects[@]}"; do
  if ! DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
       "$DOTNET_BIN" msbuild "$proj" "${EVAL_ARGS[@]}" >"$tmp/eval.json" 2>"$tmp/eval.err"; then
    echo "package-validation-coverage: MSBuild could not evaluate $proj — cannot tell." >&2
    cat "$tmp/eval.err" "$tmp/eval.json" | tail -20 | sed 's/^/    /' >&2
    exit 2
  fi
  f=()
  if ! jq -j --arg baseline "$baseline" "$EVAL_JQ" "$tmp/eval.json" >"$tmp/fields" 2>/dev/null \
     || ! mapfile -d '' -t f <"$tmp/fields" || [ "${#f[@]}" -ne 9 ]; then
    echo "package-validation-coverage: evaluating $proj did not return what dotnet msbuild -getProperty/-getItem prints — cannot tell." >&2
    head -c 600 "$tmp/eval.json" | sed 's/^/    /' >&2
    exit 2
  fi
  is_packable="${f[0]}" id="${f[1]}" enabled="${f[2]}" run_apicompat="${f[3]}" no_baseline="${f[4]}"
  b_version="${f[5]}" b_name="${f[6]}" b_path="${f[7]}" downloads="${f[8]}"

  # Not packed means not published: nothing to validate, and nothing for this check to say.
  [ "$(lc "$is_packable")" = true ] || continue
  packable=$((packable + 1))
  [ -n "$id" ] || cannot_tell "$proj is packable but evaluates to no PackageId"

  if [ "$(lc "$enabled")" = true ] && [ "$downloads" = yes ] \
     && [ "$(lc "$(trim "$run_apicompat")")" != false ]; then
    validated=$((validated + 1))
    continue
  fi

  why=()
  [ "$(lc "$enabled")" = true ] \
    || why+=("EnablePackageValidation is $(shown "$enabled"); validation runs only when it is exactly 'true' (any case, no spaces)")
  [ "$(lc "$no_baseline")" != true ] || why+=("DisablePackageBaselineValidation is $(shown "$no_baseline")")
  [ "$(lc "$(trim "$run_apicompat")")" != false ] || why+=("RunApiCompat is $(shown "$run_apicompat")")
  [ -z "$b_path" ] \
    || why+=("PackageValidationBaselinePath is $(shown "$b_path"), a file rather than the published release")
  [ "$(lc "$b_version")" = "$(lc "$baseline")" ] \
    || why+=("PackageValidationBaselineVersion evaluates to $(shown "$b_version"), not the repository baseline $baseline")
  [ -z "$b_name" ] || [ "$(lc "$b_name")" = "$(lc "$id")" ] \
    || why+=("PackageValidationBaselineName is $(shown "$b_name"), not $id")
  [ "${#why[@]}" -gt 0 ] \
    || why+=("MSBuild plans no download of $id at [$baseline], so there is nothing to compare against")

  skip_proj+=("$proj"); skip_id+=("$id"); skip_why+=("$(printf '%s\n' "${why[@]}")")
done

[ "$packable" -gt 0 ] || cannot_tell "none of the ${#projects[@]} project(s) under src/ evaluates as packable"

if [ "${#skip_proj[@]}" -eq 0 ]; then
  say "all $validated shipped project(s) are validated against $baseline."
  exit 0
fi

# feed_state <package id> — sets $state (on | absent | unreachable | unreadable) and $url (what was asked).
feed_down=0
feed_state() {
  local code=0 http verdict
  url="$FEED_BASE_URL/$(lc "$1")/index.json"
  if [ "$feed_down" -eq 1 ]; then state=unreachable; return; fi # one connection failure is enough to know
  rm -f "$tmp/index.json"
  http="$(curl -sSL --max-time 20 -o "$tmp/index.json" -w '%{http_code}' "$url" 2>/dev/null)" || code=$?
  case "$code:$http" in
    0:200|0:000) ;;                             # an answer (curl reports 000 for file://)
    0:404) state=absent; return ;;              # the flat container has no version of this id at all
    0:*) state=unreachable; return ;;           # the feed is up but did not answer (5xx, 429, ...)
    37:*) if [ "${url#file://}" != "$url" ]; then state=absent; else state=unreachable; feed_down=1; fi
          return ;;
    *) state=unreachable; feed_down=1; return ;;
  esac
  # shellcheck disable=SC2016 # $v is a jq variable
  if verdict="$(jq -r --arg v "$(lc "$baseline")" '
       if (.versions | type) == "array" and all(.versions[]; type == "string")
       then (if any(.versions[]; ascii_downcase == $v) then "on" else "absent" end)
       else error("not a list of versions") end' "$tmp/index.json" 2>/dev/null)" \
     && { [ "$verdict" = on ] || [ "$verdict" = absent ]; }; then
    state="$verdict"
  else
    state=unreadable
  fi
}

off=(); new=(); unverified=(); unreadable=()
for i in "${!skip_proj[@]}"; do
  feed_state "${skip_id[i]}"
  skip_url[i]="$url"
  case "$state" in
    on)          off+=("$i") ;;
    absent)      new+=("$i") ;;
    unreachable) unverified+=("$i") ;;
    *)           unreadable+=("$i") ;;
  esac
done

list() { # list <index>... — one line per project: path (package id): each reason
  local i line
  for i in "$@"; do
    echo "    ${skip_proj[i]} (${skip_id[i]})"
    while IFS= read -r line; do echo "      - $line"; done <<<"${skip_why[i]}"
  done
}

report_others() {
  if [ "${#new[@]}" -gt 0 ]; then
    echo
    echo "  Allowed to skip it, because the package is not on the feed at $baseline yet (a new package):"
    list "${new[@]}"
    echo "  Each must drop its opt-out in the PR that moves the baseline past its first release; this check"
    echo "  fails that PR until it does."
  fi
  if [ "${#unreadable[@]}" -gt 0 ]; then
    echo
    echo "  The feed's answer is not a list of versions, so these could not be checked:"
    for i in "${unreadable[@]}"; do echo "    ${skip_proj[i]} (${skip_id[i]}): ${skip_url[i]}"; done
  fi
  if [ "${#unverified[@]}" -gt 0 ]; then
    echo
    echo "  The feed could not be reached, so whether these are on it at $baseline was not checked:"
    list "${unverified[@]}"
    local ids=() i
    for i in "${unverified[@]}"; do ids+=("${skip_id[i]}"); done
    echo "::warning::Could not reach $FEED_BASE_URL — ${#unverified[@]} shipped project(s) skip package validation and were not checked against the feed: ${ids[*]}"
  fi
}

if [ "${#off[@]}" -gt 0 ]; then
  say "FAILED — ${#off[@]} shipped project(s) skip package validation, and their package is on the feed at $baseline."
  for i in "${off[@]}"; do
    echo
    echo "  ${skip_proj[i]}"
    echo "    package:  ${skip_id[i]}, listed at $baseline by ${skip_url[i]}"
    first=1
    while IFS= read -r line; do
      if [ "$first" -eq 1 ]; then echo "    skips it: $line"; first=0; else echo "              $line"; fi
    done <<<"${skip_why[i]}"
  done
  echo
  echo "  Each of these packs green while comparing its public API against nothing, so a binary break in it would"
  echo "  ship without a single CP diagnostic. A new package may skip validation only until it is on the feed at the"
  echo "  baseline, and these are."
  echo
  echo "  Fix: remove the opt-out, so that pack downloads each package's $baseline release and compares against it."
  echo "  Delete the property named above from the project, or from the file that sets it —"
  echo "  'dotnet msbuild <project> -pp:pp.xml' writes the fully imported project, where it can be searched for."
  echo "  Expect CP errors if something already broke: that is the check doing its job, and a CHANGELOG entry per"
  echo "  break is where the justification belongs. See ADR-0055, addendum 2026-09-12."
  report_others
  exit 1
fi

if [ "${#unreadable[@]}" -gt 0 ]; then
  echo "package-validation-coverage: the feed answered with something that is not a list of versions — cannot tell." >&2
  report_others >&2
  exit 2
fi

say "$validated of $packable shipped project(s) are validated against $baseline; none of the rest is on the feed at it."
report_others
exit 0

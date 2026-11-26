#!/usr/bin/env bash
# test_build_release_notes.sh — unit tests for scripts/ci/build-release-notes.sh.
#
# The script runs in publish.yml, which runs only on a tag push or a manual dispatch, so no PR ever
# exercises it, and a broken one is found while a release waits on it. Three things are pinned here:
#
#   1. A body that fits is BYTE-IDENTICAL to what the inline step in publish.yml produced before the
#      logic moved into a script. `old_build` below is that step's text, kept verbatim, and every real
#      section of CHANGELOG.md is built both ways and compared with `cmp`.
#   2. A body over GitHub's release-body cap no longer fails the release. It falls back to a shorter
#      body: a link to CHANGELOG.md at the tag, every BREAKING entry in full, then the heading of
#      every other entry; and if that is still too long, the headings alone.
#   3. Only a body that cannot fit even as a list of headings fails, before anything is pushed.
#
# Pure bash + awk, no network, ~2s.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BUILD="$SCRIPT_DIR/../ci/build-release-notes.sh"
REAL_CHANGELOG="$REPO_ROOT/CHANGELOG.md"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

export LC_ALL=C
unset GITHUB_REPOSITORY

ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

# The package names publish.yml would list: one per project under src/.
PACKAGES=()
while IFS= read -r d; do PACKAGES+=("$d"); done < <(cd "$REPO_ROOT/src" && find . -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)

# workdir <name> <version> — a fresh directory laid out like the runner's checkout: CHANGELOG.md and
# ./artifacts/ holding one .nupkg per package at <version>. Prints the directory.
workdir() {
  local dir="$ROOT/$1" p
  mkdir -p "$dir/artifacts"
  for p in "${PACKAGES[@]}"; do : > "$dir/artifacts/$p.$2.nupkg"; done
  printf '%s\n' "$dir"
}

# old_build — the inline `run:` of publish.yml's "Build release notes from CHANGELOG.md" step as it was
# before the move, verbatim apart from the indentation. Runs in the current directory with $VERSION set.
old_build() (
          set -euo pipefail

          # This version's own section: everything between its heading
          # (`## [2.4.0] - 2026-07-26`) and the next `## [` heading.
          awk -v v="$VERSION" '
            index($0, "## [" v "]") == 1 { grab = 1; next }
            grab && index($0, "## [") == 1 { exit }
            grab { print }
          ' CHANGELOG.md > release-notes.md

          if [ ! -s release-notes.md ]; then
            echo "::error::CHANGELOG.md has no content under a '## [$VERSION]' heading. Write the section, then re-tag."
            exit 1
          fi

          # Drop trailing blank lines and any horizontal rule the section ended on,
          # so the footer below contributes the only `---` in the body.
          awk '{ line[NR] = $0 }
               END { last = NR
                     while (last > 0 && (line[last] ~ /^[[:space:]]*$/ || line[last] == "---")) last--
                     for (i = 1; i <= last; i++) print line[i] }' release-notes.md > release-notes.trimmed
          mv release-notes.trimmed release-notes.md

          {
            printf '\n---\n\n'
            printf 'Packages published to [nuget.org](https://www.nuget.org/profiles/verbara) at version `%s`:\n\n' "$VERSION"
            for f in ./artifacts/*.nupkg; do
              [ -e "$f" ] || continue
              base="$(basename "$f" .nupkg)"
              printf -- '- `%s`\n' "${base%".$VERSION"}"
            done
          } >> release-notes.md

          # GitHub caps a release body at 125,000 characters and `gh release create`
          # runs AFTER the push, so a body that does not fit would publish 29 packages
          # and then fail to announce them. Measured here, pre-push, where failing is free.
          # Bytes, not characters: `wc -m` depends on a UTF-8 locale the runner does not
          # guarantee, and for this file bytes over-count, which errs toward blocking.
          BODY_LIMIT=125000
          BODY_BYTES="$(wc -c < release-notes.md)"

          if [ "$BODY_BYTES" -gt "$BODY_LIMIT" ]; then
            echo "::error::Release body is $BODY_BYTES bytes, over GitHub's $BODY_LIMIT-character cap. Split the CHANGELOG section before tagging."
            exit 1
          fi

          if [ "$BODY_BYTES" -gt $(( BODY_LIMIT * 9 / 10 )) ]; then
            echo "::warning::Release body is $BODY_BYTES bytes — over 90% of GitHub's $BODY_LIMIT-character cap. The next release of this size will not fit."
          fi

          echo "Release notes: $(wc -l < release-notes.md) lines, $BODY_BYTES bytes."
)

# run_new <dir> <version> [extra args...] — runs the script in <dir>; sets $rc and $out (stdout+stderr).
run_new() {
  local dir="$1" version="$2"; shift 2
  set +e
  out="$(cd "$dir" && bash "$BUILD" "$version" "$@" 2>&1)"
  rc=$?
  set -e
}

has()     { [[ "$out" == *"$1"* ]]; }
bytes()   { if [ -e "$1" ]; then wc -c < "$1" | tr -d ' '; else echo 0; fi; }
in_file() { awk -v s="$2" 'index($0, s) { found = 1 } END { exit !found }' "$1"; }
# notes <dir> — the body the script wrote, or /dev/null when it wrote none, so a red run reports
# every failed assertion instead of stopping at the first read of a missing file.
notes()   { if [ -e "$1/release-notes.md" ]; then printf '%s\n' "$1/release-notes.md"; else printf '/dev/null\n'; fi; }
line_of() { awk -v s="$2" 'index($0, s) { print NR; exit }' "$1"; }

# ---------------------------------------------------------------------------------------------------
# 1. Byte identity with the old inline step, for every real section of CHANGELOG.md.
# ---------------------------------------------------------------------------------------------------
VERSIONS=()
while IFS= read -r v; do VERSIONS+=("$v"); done < <(
  awk 'index($0, "## [") == 1 { v = substr($0, 5); sub(/\].*/, "", v); print v }' "$REAL_CHANGELOG")

for must in 2.10.1 2.9.0 2.5.0; do
  found=0
  for v in "${VERSIONS[@]}"; do [ "$v" = "$must" ] && found=1; done
  if [ "$found" -eq 1 ]; then ok; else bad "CHANGELOG.md has no [$must] section to compare"; fi
done

identical=0
for v in "${VERSIONS[@]}"; do
  old="$(workdir "old-$v" "$v")"; new="$(workdir "new-$v" "$v")"
  cp "$REAL_CHANGELOG" "$old/CHANGELOG.md"; cp "$REAL_CHANGELOG" "$new/CHANGELOG.md"
  set +e
  old_out="$(cd "$old" && VERSION="$v" old_build 2>&1)"; old_rc=$?
  set -e
  run_new "$new" "$v"
  if [ "$old_rc" -ne 0 ]; then
    bad "[$v]: the old step failed (rc $old_rc), so this section proves nothing: $old_out"
    continue
  fi
  if [ "$rc" -ne 0 ]; then bad "[$v]: old step exit 0, new script exit $rc: $out"; continue; fi
  if cmp -s "$old/release-notes.md" "$new/release-notes.md"; then
    identical=$((identical + 1)); ok
    case "$v" in 2.10.1|2.9.0|2.5.0)
      echo "  identity [$v]: old $(bytes "$old/release-notes.md") bytes, new $(bytes "$new/release-notes.md") bytes, cmp equal";;
    esac
  else
    bad "[$v]: body differs from the old step's ($(bytes "$old/release-notes.md") vs $(bytes "$new/release-notes.md") bytes)"
  fi
  # The 90% warning fires on the same sections it fired on before.
  ow=0; nw=0
  [[ "$old_out" == *"::warning::"* ]] && ow=1
  [[ "$out" == *"::warning::"* ]] && nw=1
  if [ "$ow" -eq "$nw" ]; then ok; else bad "[$v]: old warned=$ow, new warned=$nw"; fi
done
echo "  byte identity: $identical of ${#VERSIONS[@]} real sections"

# 2.5.0 is the one real section past 90% of the cap: it must keep its warning, and stay a full body.
d="$(workdir w250 2.5.0)"; cp "$REAL_CHANGELOG" "$d/CHANGELOG.md"
run_new "$d" 2.5.0
if has "::warning::Release body is" && has "over 90% of GitHub's 125000-character cap"; then ok
else bad "2.5.0: the 90% warning is missing: $out"; fi
if has "fallback"; then bad "2.5.0 fits, yet the output mentions a fallback: $out"; else ok; fi

# ---------------------------------------------------------------------------------------------------
# 2. The errors that stay errors.
# ---------------------------------------------------------------------------------------------------
d="$(workdir empty 9.9.9)"
printf '# Changelog\n\n## [9.9.9] - 2099-01-01\n## [9.9.8] - 2099-01-01\n\n### Added: x\n' > "$d/CHANGELOG.md"
run_new "$d" 9.9.9
if [ "$rc" -eq 1 ] && has "::error::CHANGELOG.md has no content under a '## [9.9.9]' heading."; then ok
else bad "empty section: want rc 1 and the empty-section error, got rc $rc: $out"; fi

d="$(workdir missing 9.9.9)"
printf '# Changelog\n\n## [9.9.8] - 2099-01-01\n\n### Added: x\n' > "$d/CHANGELOG.md"
run_new "$d" 9.9.9
if [ "$rc" -eq 1 ] && has "::error::CHANGELOG.md has no content under a '## [9.9.9]' heading."; then ok
else bad "missing section: want rc 1 and the empty-section error, got rc $rc: $out"; fi

set +e
out="$(cd "$d" && bash "$BUILD" 2>&1)"; rc=$?
set -e
if [ "$rc" -ne 0 ] && [ ! -e "$d/release-notes.md" ]; then ok; else bad "no version argument: want a non-zero exit and no file, got $rc"; fi

# ---------------------------------------------------------------------------------------------------
# Synthetic sections. `filler <n> <tag>` prints n lines of 100 bytes each (99 + newline), every line
# carrying <tag> so a test can tell whose body a line came from.
# ---------------------------------------------------------------------------------------------------
filler() { awk -v n="$1" -v t="$2" 'BEGIN { s = "- " t " "; while (length(s) < 99) s = s "x"; for (i = 0; i < n; i++) print s }'; }

# big_section <file> <version> — a section well over the cap: two BREAKING entries (one with a
# `####` sub-heading, which belongs to it) and forty ordinary ones, plus a `### ` line inside a code
# fence, which is not an entry.
big_section() {
  {
    printf '# Changelog\n\n## [Unreleased]\n\n### Added: unreleased work\n\n'
    printf '## [%s] - 2099-02-03\n\n' "$2"
    printf 'Preamble line before the first entry.\n\n'
    printf '### Changed — BREAKING: the first breaking change (#1)\n\n'
    filler 5 BREAK1
    printf '\n#### Migration\n\nBREAK1-migration-line\n\n'
    for i in $(seq 1 20); do
      printf '### Fixed: ordinary fix %d (#%d)\n\n' "$i" "$((100 + i))"
      filler 35 "BODY$i"
      printf '\n```markdown\n### not an entry %d\n```\n\n' "$i"
    done
    printf '### Changed — BREAKING: the second breaking change (#2)\n\n'
    filler 4 BREAK2
    printf '\n'
    for i in $(seq 21 40); do
      printf '### Added: ordinary addition %d (#%d)\n\n' "$i" "$((100 + i))"
      filler 35 "BODY$i"
      printf '\n'
    done
    printf '\n---\n\n## [0.0.1] - 2000-01-01\n\n### Added: the first release\n'
  } > "$1"
}

# ---------------------------------------------------------------------------------------------------
# 3. Fallback level 1: link, BREAKING entries in full, headings of the rest, footer.
# ---------------------------------------------------------------------------------------------------
d="$(workdir level1 3.0.0)"
big_section "$d/CHANGELOG.md" 3.0.0
full_bytes="$(cd "$d" && awk -v v=3.0.0 'index($0, "## [" v "]") == 1 { g = 1; next } g && index($0, "## [") == 1 { exit } g' CHANGELOG.md | wc -c | tr -d ' ')"
if [ "$full_bytes" -gt 125000 ]; then ok; else bad "fixture: the synthetic section is only $full_bytes bytes"; fi

run_new "$d" 3.0.0
f="$(notes "$d")"
if [ "$rc" -eq 0 ]; then ok; else bad "level 1: want rc 0, got $rc: $out"; fi
if [ -s "$f" ] && [ "$(bytes "$f")" -le 125000 ]; then ok; else bad "level 1: body missing or over the cap"; fi
if has "::warning::" && has "fallback level 1"; then ok; else bad "level 1: no ::warning naming the level: $out"; fi
if has "::error::"; then bad "level 1: unexpected ::error: $out"; else ok; fi
# The warning names the full body's size: section plus footer, which is more than the section alone.
size_in_warning="$(printf '%s\n' "$out" | awk '/::warning::/ { if (match($0, /full release body is [0-9]+ bytes/)) { s = substr($0, RSTART, RLENGTH); gsub(/[^0-9]/, "", s); print s; exit } }')"
if [ -n "$size_in_warning" ] && [ "$size_in_warning" -gt "$full_bytes" ]; then ok
else bad "level 1: warning should name the full body's size (> $full_bytes), got '$size_in_warning': $out"; fi

link='https://github.com/verbara/Verbara.Sdk/blob/v3.0.0/CHANGELOG.md#300---2099-02-03'
if in_file "$f" "$link"; then ok; else bad "level 1: no link '$link'"; fi
if in_file "$f" "BREAKING: the first breaking change (#1)" && in_file "$f" "BREAK1-migration-line" \
   && [ "$(awk '/ BREAK1 /' "$f" | wc -l)" -eq 5 ] && [ "$(awk '/ BREAK2 /' "$f" | wc -l)" -eq 4 ]; then ok
else bad "level 1: the BREAKING entries are not there in full"; fi
if [ "$(awk '/ BODY[0-9]+ /' "$f" | wc -l)" -eq 0 ]; then ok; else bad "level 1: a non-breaking body line leaked in"; fi
if in_file "$f" "Preamble line"; then bad "level 1: the preamble leaked in"; else ok; fi
missing=0
for i in $(seq 1 20); do in_file "$f" "- Fixed: ordinary fix $i (#$((100 + i)))" || missing=$((missing + 1)); done
for i in $(seq 21 40); do in_file "$f" "- Added: ordinary addition $i (#$((100 + i)))" || missing=$((missing + 1)); done
if [ "$missing" -eq 0 ]; then ok; else bad "level 1: $missing of 40 ordinary headings are not listed as bullets"; fi
if in_file "$f" "not an entry"; then bad "level 1: a '### ' line inside a code fence was taken for an entry"; else ok; fi
if [ "$(awk 'index($0, "### Changed — BREAKING") == 1' "$f" | wc -l)" -eq 2 ]; then ok
else bad "level 1: want exactly two BREAKING headings kept as headings"; fi
if [ "$(awk 'index($0, "- Changed — BREAKING") == 1' "$f" | wc -l)" -eq 0 ]; then ok
else bad "level 1: a BREAKING entry is also repeated in the heading list"; fi

# Order: link paragraph, BREAKING entries, heading list, footer.
l_link="$(line_of "$f" "$link")"; l_b1="$(line_of "$f" "first breaking change")"
l_b2="$(line_of "$f" "second breaking change")"; l_list="$(line_of "$f" "- Fixed: ordinary fix 1 (#101)")"
l_foot="$(line_of "$f" "Packages published to [nuget.org]")"
if [ -n "$l_link" ] && [ -n "$l_b1" ] && [ -n "$l_b2" ] && [ -n "$l_list" ] && [ -n "$l_foot" ] \
   && [ "$l_link" -lt "$l_b1" ] && [ "$l_b1" -lt "$l_b2" ] && [ "$l_b2" -lt "$l_list" ] && [ "$l_list" -lt "$l_foot" ]; then ok
else bad "level 1: order is wrong (link $l_link, breaking $l_b1/$l_b2, list $l_list, footer $l_foot)"; fi

# The footer is the same bytes the full body would have ended with: one `---`, then the package list.
footer_expected="$(cd "$d" && VERSION=3.0.0 && {
  printf '\n---\n\n'
  printf 'Packages published to [nuget.org](https://www.nuget.org/profiles/verbara) at version `%s`:\n\n' "$VERSION"
  for p in ./artifacts/*.nupkg; do b="$(basename "$p" .nupkg)"; printf -- '- `%s`\n' "${b%".$VERSION"}"; done
} | od -An -tx1 | tr -d ' \n')"
footer_lines=$(( ${#PACKAGES[@]} + 5 ))
footer_actual="$(awk -v n="$footer_lines" '{ l[NR] = $0 } END { for (i = NR - n + 1; i <= NR; i++) print l[i] }' "$f" | od -An -tx1 | tr -d ' \n')"
if [ "$footer_expected" = "$footer_actual" ]; then ok; else bad "level 1: the footer differs from the full body's"; fi
if [ "$(awk '$0 == "---"' "$f" | wc -l)" -eq 1 ]; then ok; else bad "level 1: want exactly one '---' (the footer's)"; fi

# GITHUB_REPOSITORY names the repository in the link.
set +e
out="$(cd "$d" && GITHUB_REPOSITORY=someone/Fork bash "$BUILD" 3.0.0 2>&1)"; rc=$?
set -e
f="$(notes "$d")"
if [ "$rc" -eq 0 ] && in_file "$f" "https://github.com/someone/Fork/blob/v3.0.0/CHANGELOG.md"; then ok
else bad "GITHUB_REPOSITORY is not used for the link (rc $rc)"; fi

# ---------------------------------------------------------------------------------------------------
# 4. Fallback level 2: the BREAKING bodies alone are over the cap, so only the headings are kept.
# ---------------------------------------------------------------------------------------------------
d="$(workdir level2 3.1.0)"
{
  printf '# Changelog\n\n## [3.1.0] - 2099-03-04\n\n'
  printf '### Changed — BREAKING: an enormous breaking change (#7)\n\n'
  filler 1300 HUGEBREAK
  printf '\n### Fixed: a small fix (#8)\n\nsmall-fix-body\n\n'
  printf '### Changed — BREAKING: a second breaking change (#9)\n\nsecond-break-body\n'
} > "$d/CHANGELOG.md"
run_new "$d" 3.1.0
f="$(notes "$d")"
if [ "$rc" -eq 0 ]; then ok; else bad "level 2: want rc 0, got $rc: $out"; fi
if has "::warning::" && has "fallback level 2"; then ok; else bad "level 2: no ::warning naming the level: $out"; fi
if [ -s "$f" ] && [ "$(bytes "$f")" -le 125000 ]; then ok; else bad "level 2: body missing or over the cap"; fi
if [ "$(awk '/HUGEBREAK/' "$f" | wc -l)" -eq 0 ] && ! in_file "$f" "second-break-body" && ! in_file "$f" "small-fix-body"; then ok
else bad "level 2: an entry body is still there"; fi
if in_file "$f" "- Changed — BREAKING: an enormous breaking change (#7)" && in_file "$f" "- Fixed: a small fix (#8)" \
   && in_file "$f" "- Changed — BREAKING: a second breaking change (#9)"; then ok
else bad "level 2: not every heading is listed"; fi
l1="$(line_of "$f" "enormous breaking")"; l2="$(line_of "$f" "a small fix")"; l3="$(line_of "$f" "a second breaking")"
if [ -n "$l1" ] && [ -n "$l2" ] && [ -n "$l3" ] && [ "$l1" -lt "$l2" ] && [ "$l2" -lt "$l3" ]; then ok
else bad "level 2: headings are not in CHANGELOG order"; fi
if in_file "$f" "https://github.com/verbara/Verbara.Sdk/blob/v3.1.0/CHANGELOG.md" && in_file "$f" "Packages published to [nuget.org]"; then ok
else bad "level 2: link or footer missing"; fi

# ---------------------------------------------------------------------------------------------------
# 4b. Fences of other kinds, and a section with no BREAKING entry. A fence closes only on the same
#     character, at least as long as the one that opened it (CommonMark); a `~~~` fence holding a
#     ``` line, or a ```` fence holding ``` lines, must not flip the state and hide or invent entries.
# ---------------------------------------------------------------------------------------------------
d="$(workdir fences 3.3.0)"
{
  printf '# Changelog\n\n## [3.3.0] - 2099-03-04\n\n'
  printf '### Fixed: before the fences (#1)\n\n'; filler 30 FENCEBODY1
  printf '\n~~~\n```\n### inside a tilde fence\n~~~\n\n'
  printf '### Fixed: after-mixed-fence (#4)\n\n'; filler 30 FENCEBODY4
  printf '\n````\n```\n### four-backtick-inner\n```\n````\n\n'
  printf '### Fixed: last entry (#5)\n\n'; filler 30 FENCEBODY5
} > "$d/CHANGELOG.md"
set +e
out="$(cd "$d" && BODY_LIMIT=5000 bash "$BUILD" 3.3.0 2>&1)"; rc=$?
set -e
f="$(notes "$d")"
if [ "$rc" -eq 0 ] && has "fallback level 1"; then ok; else bad "fences: want rc 0 at level 1, got rc $rc: $out"; fi
if in_file "$f" "- Fixed: before the fences (#1)" && in_file "$f" "- Fixed: after-mixed-fence (#4)" \
   && in_file "$f" "- Fixed: last entry (#5)"; then ok
else bad "fences: an entry after a mixed fence is missing from the list"; fi
if in_file "$f" "inside a tilde fence" || in_file "$f" "four-backtick-inner"; then
  bad "fences: a '### ' line inside a fence was listed as an entry"; else ok; fi
# No BREAKING entry: the lead must not promise breaking changes that are not there.
if in_file "$f" "every breaking change"; then bad "no-BREAKING lead still promises breaking changes"; else ok; fi
if in_file "$f" "Below, the heading of every change."; then ok; else bad "no-BREAKING lead: want 'Below, the heading of every change.'"; fi

# ---------------------------------------------------------------------------------------------------
# 5. Level 3: even the list of headings is over the cap — the release fails before the push.
# ---------------------------------------------------------------------------------------------------
d="$(workdir level3 3.2.0)"
{
  printf '# Changelog\n\n## [3.2.0] - 2099-04-05\n\n'
  awk 'BEGIN { pad = ""; while (length(pad) < 80) pad = pad "y"; for (i = 0; i < 2000; i++) printf "### Fixed: heading %d %s\n\nbody\n\n", i, pad }'
} > "$d/CHANGELOG.md"
run_new "$d" 3.2.0
if [ "$rc" -eq 1 ] && has "::error::"; then ok; else bad "level 3: want rc 1 and an ::error, got rc $rc: $out"; fi

# ---------------------------------------------------------------------------------------------------
# 6. The cap's boundary, and the 90% warning on a synthetic body. `sized <dir> <version> <bytes>`
#    writes a section whose FULL body (section + footer) is exactly <bytes>.
# ---------------------------------------------------------------------------------------------------
sized() {
  local dir="$1" v="$2" want="$3" probe pad
  printf '# Changelog\n\n## [%s] - 2099-05-06\n\n### Added: padding (#1)\n\nX\n' "$v" > "$dir/CHANGELOG.md"
  (cd "$dir" && BODY_LIMIT=999999999 bash "$BUILD" "$v" > /dev/null 2>&1) || true
  probe="$(bytes "$dir/release-notes.md")"
  rm -f "$dir/release-notes.md"
  pad=$(( want - probe ))
  {
    printf '# Changelog\n\n## [%s] - 2099-05-06\n\n### Added: padding (#1)\n\n' "$v"
    awk -v n="$(( pad + 2 ))" 'BEGIN { while (n > 101) { printf "%099d\n", 0; n -= 100 } s = "X"; while (length(s) < n - 1) s = s "X"; print s }'
  } > "$dir/CHANGELOG.md"
}

d="$(workdir at-cap 4.0.0)"; sized "$d" 4.0.0 125000
run_new "$d" 4.0.0
if [ "$rc" -eq 0 ] && [ "$(bytes "$d/release-notes.md")" -eq 125000 ] && ! has "fallback" && has "over 90%"; then ok
else bad "exactly 125000 bytes: want the full body with the 90% warning, got rc $rc, $(bytes "$d/release-notes.md") bytes: $out"; fi

d="$(workdir over-cap 4.0.1)"; sized "$d" 4.0.1 125001
run_new "$d" 4.0.1
if [ "$rc" -eq 0 ] && has "fallback level 1" && has "full release body is 125001 bytes" && [ "$(bytes "$d/release-notes.md")" -lt 125000 ]; then ok
else bad "125001 bytes: want fallback level 1 naming 125001, got rc $rc: $out"; fi

d="$(workdir at-90 4.0.2)"; sized "$d" 4.0.2 112500
run_new "$d" 4.0.2
if [ "$rc" -eq 0 ] && ! has "::warning::"; then ok; else bad "112500 bytes (exactly 90%): want no warning, got rc $rc: $out"; fi

d="$(workdir over-90 4.0.3)"; sized "$d" 4.0.3 112501
run_new "$d" 4.0.3
if [ "$rc" -eq 0 ] && has "::warning::Release body is 112501 bytes"; then ok; else bad "112501 bytes: want the 90% warning, got rc $rc: $out"; fi

# ---------------------------------------------------------------------------------------------------
# 7. The optional arguments: another CHANGELOG path and another artifacts directory.
# ---------------------------------------------------------------------------------------------------
d="$ROOT/args"; mkdir -p "$d/notes" "$d/pkgs"
printf '# Changelog\n\n## [5.0.0] - 2099-06-07\n\n### Added: one (#1)\n\nbody\n' > "$d/notes/CL.md"
: > "$d/pkgs/Verbara.Sdk.Only.5.0.0.nupkg"
run_new "$d" 5.0.0 notes/CL.md pkgs
if [ "$rc" -eq 0 ] && in_file "$d/release-notes.md" '- `Verbara.Sdk.Only`' && in_file "$d/release-notes.md" "### Added: one (#1)"; then ok
else bad "changelog and artifacts-dir arguments are not honoured (rc $rc): $out"; fi

echo
echo "build-release-notes: $pass passed, $fails failed"
[ "$fails" -eq 0 ]

#!/usr/bin/env bash
# build-release-notes.sh — builds the GitHub release body for one version from CHANGELOG.md.
#
# Usage: build-release-notes.sh <version> [changelog] [artifacts-dir]
#   changelog      default CHANGELOG.md
#   artifacts-dir  default ./artifacts (the packed .nupkg files the footer lists)
# Writes release-notes.md in the current directory. Run by publish.yml's "Build release notes from
# CHANGELOG.md" step, BEFORE the push; tested by scripts/tests/test_build_release_notes.sh.
#
# The body is the version's CHANGELOG section followed by a footer listing the packages. GitHub caps a
# release body at 125,000 characters, and `gh release create` runs AFTER the push, so a body that does
# not fit would publish every package and then fail to announce them. Measured here, pre-push.
#
#   fits          the full section + footer, unchanged. A ::warning past 90% of the cap.
#   fallback 1    too long: a lead paragraph linking the full notes in CHANGELOG.md at the tag, every
#                 BREAKING entry in full, the heading of every other entry as a bullet, the footer.
#   fallback 2    still too long: the lead paragraph, the heading of EVERY entry, the footer.
#   exit 1        even that does not fit (thousands of entries), or the section is empty or missing.
#
# An "entry" is a `### ` heading and every line up to the next `### ` heading or the section's end
# (a `####` sub-heading belongs to its entry; a `### ` line inside a ``` fence is not a heading). An
# entry is BREAKING when its heading text contains "BREAKING".
#
# Bytes, not characters: `wc -m` depends on a UTF-8 locale the runner does not guarantee, and for this
# file bytes over-count, which errs toward the shorter body.
set -euo pipefail
export LC_ALL=C

if [ $# -lt 1 ] || [ -z "$1" ]; then
  echo "usage: $0 <version> [changelog] [artifacts-dir]" >&2
  exit 2
fi
VERSION="$1"
CHANGELOG="${2:-CHANGELOG.md}"
ARTIFACTS="${3:-./artifacts}"
OUT=release-notes.md
BODY_LIMIT="${BODY_LIMIT:-125000}" # GitHub's release-body cap; overridable only for tests
REPO="${GITHUB_REPOSITORY:-verbara/Verbara.Sdk}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# This version's own section: everything between its heading (`## [2.4.0] - 2026-07-26`) and the next
# `## [` heading.
awk -v v="$VERSION" '
  index($0, "## [" v "]") == 1 { grab = 1; next }
  grab && index($0, "## [") == 1 { exit }
  grab { print }
' "$CHANGELOG" > "$WORK/section"

if [ ! -s "$WORK/section" ]; then
  echo "::error::CHANGELOG.md has no content under a '## [$VERSION]' heading. Write the section, then re-tag."
  exit 1
fi

# Drop trailing blank lines and any horizontal rule the section ended on, so the footer below
# contributes the only `---` in the body.
awk '{ line[NR] = $0 }
     END { last = NR
           while (last > 0 && (line[last] ~ /^[[:space:]]*$/ || line[last] == "---")) last--
           for (i = 1; i <= last; i++) print line[i] }' "$WORK/section" > "$WORK/trimmed"

{
  printf '\n---\n\n'
  printf 'Packages published to [nuget.org](https://www.nuget.org/profiles/verbara) at version `%s`:\n\n' "$VERSION"
  for f in "$ARTIFACTS"/*.nupkg; do
    [ -e "$f" ] || continue
    base="$(basename "$f" .nupkg)"
    printf -- '- `%s`\n' "${base%".$VERSION"}"
  done
} > "$WORK/footer"

cat "$WORK/trimmed" "$WORK/footer" > "$WORK/full"
FULL_BYTES="$(wc -c < "$WORK/full" | tr -d ' ')"

if [ "$FULL_BYTES" -le "$BODY_LIMIT" ]; then
  cp "$WORK/full" "$OUT"
  if [ "$FULL_BYTES" -gt $(( BODY_LIMIT * 9 / 10 )) ]; then
    echo "::warning::Release body is $FULL_BYTES bytes — over 90% of GitHub's $BODY_LIMIT-character cap. The next release of this size will not fit in full."
  fi
  echo "Release notes: $(wc -l < "$OUT" | tr -d ' ') lines, $FULL_BYTES bytes."
  exit 0
fi

# --- The full body does not fit: build the shorter ones. ----------------------------------------

# The link to the full notes: CHANGELOG.md at the tag, anchored on the version's heading. The anchor is
# GitHub's heading slug of the heading line as written (`## [2.10.1] - 2026-11-16` → `2101---2026-11-16`):
# lower-cased, every character but letters, digits, spaces, `-` and `_` dropped, each space a `-`. A
# heading with non-ASCII text would get a wrong anchor, which only lands the reader at the file's top.
heading="$(awk -v v="$VERSION" 'index($0, "## [" v "]") == 1 { print substr($0, 4); exit }' "$CHANGELOG")"
anchor="$(printf '%s' "$heading" | awk '{ s = tolower($0); gsub(/[^a-z0-9 _-]/, "", s); gsub(/ /, "-", s); print s }')"
LINK="https://github.com/$REPO/blob/v$VERSION/CHANGELOG.md#$anchor"

# The entries, split once: breaking.md holds every BREAKING entry in full (its heading and body, blank
# lines between entries as written); others.md and all.md hold one bullet per heading.
awk -v breaking="$WORK/breaking.md" -v others="$WORK/others.md" -v all="$WORK/all.md" '
  BEGIN { printf "" > breaking; printf "" > others; printf "" > all }
  # A fence opens on 3+ backticks or tildes and closes only on a line of the same character, at least
  # as long, and nothing else (CommonMark) — so a ``` line inside a ~~~ fence does not close it.
  {
    l = $0; sub(/^[ \t]*/, "", l); c = substr(l, 1, 1); n = 0
    if (c == "`" || c == "~") { while (substr(l, n + 1, 1) == c) n++ }
    if (n >= 3) {
      if (!fence) { fence = 1; fch = c; flen = n; }
      else if (c == fch && n >= flen && substr(l, n + 1) ~ /^[ \t]*$/) { fence = 0; }
    }
  }
  !fence && index($0, "### ") == 1 {
    text = substr($0, 5)
    in_break = index(text, "BREAKING") > 0
    print "- " text >> all
    if (!in_break) print "- " text >> others
  }
  !fence && index($0, "### ") == 1 && in_break { print >> breaking; next }
  in_break { print >> breaking }
' "$WORK/trimmed"
# Each kept BREAKING entry ends where the next heading began; drop the blank lines it ended on.
awk '{ line[NR] = $0 }
     END { last = NR
           while (last > 0 && line[last] ~ /^[[:space:]]*$/) last--
           for (i = 1; i <= last; i++) print line[i] }' "$WORK/breaking.md" > "$WORK/breaking.trimmed"

lead() {
  printf 'The full release notes for `%s` are %s bytes, over the %s-character limit GitHub sets on a release description, so they are published in CHANGELOG.md at the tag:\n\n' "$VERSION" "$FULL_BYTES" "$BODY_LIMIT"
  printf '**[CHANGELOG.md at v%s](%s)**\n\n' "$VERSION" "$LINK"
  printf '%s\n' "$1"
}

{
  if [ -s "$WORK/breaking.trimmed" ]; then
    lead 'Below, every breaking change in full, then the heading of every other change.'
  else
    lead 'Below, the heading of every change.'
  fi
  if [ -s "$WORK/breaking.trimmed" ]; then
    printf '\n'
    cat "$WORK/breaking.trimmed"
  fi
  if [ -s "$WORK/others.md" ]; then
    printf '\n### Every other change\n\n'
    cat "$WORK/others.md"
  fi
  cat "$WORK/footer"
} > "$WORK/level1"

{
  lead 'Below, the heading of every change; the breaking ones say BREAKING.'
  if [ -s "$WORK/all.md" ]; then
    printf '\n### Every change\n\n'
    cat "$WORK/all.md"
  fi
  cat "$WORK/footer"
} > "$WORK/level2"

L1_BYTES="$(wc -c < "$WORK/level1" | tr -d ' ')"
L2_BYTES="$(wc -c < "$WORK/level2" | tr -d ' ')"

if [ "$L1_BYTES" -le "$BODY_LIMIT" ]; then
  cp "$WORK/level1" "$OUT"
  echo "::warning::The full release body is $FULL_BYTES bytes, over GitHub's $BODY_LIMIT-character cap. Published fallback level 1 ($L1_BYTES bytes): a link to CHANGELOG.md at v$VERSION, every BREAKING entry in full, and the heading of every other entry."
elif [ "$L2_BYTES" -le "$BODY_LIMIT" ]; then
  cp "$WORK/level2" "$OUT"
  echo "::warning::The full release body is $FULL_BYTES bytes, over GitHub's $BODY_LIMIT-character cap, and the BREAKING entries alone do not fit ($L1_BYTES bytes with them). Published fallback level 2 ($L2_BYTES bytes): a link to CHANGELOG.md at v$VERSION and the heading of every entry."
else
  echo "::error::Release body does not fit GitHub's $BODY_LIMIT-character cap even as a list of headings: full $FULL_BYTES bytes, with BREAKING entries $L1_BYTES, headings only $L2_BYTES. Split the CHANGELOG section before tagging."
  exit 1
fi

echo "Release notes: $(wc -l < "$OUT" | tr -d ' ') lines, $(wc -c < "$OUT" | tr -d ' ') bytes (full notes: $FULL_BYTES bytes, in CHANGELOG.md)."

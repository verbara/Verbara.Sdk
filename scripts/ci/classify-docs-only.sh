#!/usr/bin/env bash
# classify-docs-only.sh — prints "docs_only=true" | "docs_only=false" (verbara-meta/ADR-0016 §3.4).
# Fail-closed: empty diff or ANY non-allowlisted path => false. Rename/copy => BOTH paths
# classified (--no-renames surfaces a rename as delete(old)+add(new)); a git-diff failure
# EXITS NON-ZERO so the gate job goes red and every heavy job runs (§3.2 fail-closed).
#
# Sdk carve-out (first case block): Tests/Verbara.Sdk.DocSnippets.Tests Roslyn-compiles every
# ```csharp block out of six Markdown files, INSIDE the "Unit Tests" job — the very job this gate
# can skip. Those six are therefore NOT docs-only: editing them must keep their only guard running.
# The list is pinned as a superset of DocSnippetCompilationTests.cs `sources` by
# scripts/tests/test_classify_docs_only.sh — do not edit one without the other.
# Nested src/*/README.md are NOT compiled by that test and stay allowlisted.
set -euo pipefail
BASE="${1:?usage: classify-docs-only.sh <base-sha> [head]}"
HEAD="${2:-HEAD}"
if ! raw="$(git -c core.quotePath=false diff --name-only --no-renames "$BASE" "$HEAD")"; then
  echo "classify-docs-only: git diff failed against base '$BASE'" >&2
  exit 1
fi
[ -n "$raw" ] || { echo "docs_only=false"; exit 0; }   # empty diff => fail-closed
mapfile -t files <<< "$raw"
for f in "${files[@]}"; do
  case "$f" in
    # BEGIN docsnippets-carveout (must stay a superset of DocSnippetCompilationTests.cs `sources`)
    README.md|docs/README-technical.md|docs/guides/asterisk-version-compatibility.md|docs/guides/high-load-tuning.md|docs/guides/session-store-backends.md|docs/guides/troubleshooting.md)
    # END docsnippets-carveout
      echo "docs_only=false"; exit 0 ;;
  esac
  case "$f" in
    docs/*|openspec/*|CHANGELOG.md) continue ;;   # docs + specs + changelog
    */README.md) continue ;;                      # README at any depth. Nested src/*/README.md ARE
                                                  # packed: deleting one is NU5019 and emptying one
                                                  # is NU5040 — both skipped by this fast path and
                                                  # surfacing only in publish.yml. Accepted residual
                                                  # (verbara-meta/ADR-0016 §6.1 risk 3).
  esac
  case "$f" in
    */*)  echo "docs_only=false"; exit 0 ;;       # nested non-doc path
    *.md) continue ;;                             # top-level *.md only (NOT **/*.md)
    *)    echo "docs_only=false"; exit 0 ;;       # top-level non-md (gates.yaml, *.props, ... )
  esac
done
echo "docs_only=true"

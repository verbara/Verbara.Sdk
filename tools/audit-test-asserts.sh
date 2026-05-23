#!/usr/bin/env bash
# Walks Tests/**/*.cs for [Fact] and [Theory] declarations and verifies that
# the body of each test method contains at least one assertion expression.
#
# Recognized assertion forms:
#   - xUnit:             `Assert.…`
#   - FluentAssertions:  `.Should()`, `.ShouldBe(`, `.ShouldNotBe(`
#   - NSubstitute mocks: `.Received(`, `.DidNotReceive(`
#
# Designed as a CI gate against the "smoke test that doesn't actually assert"
# anti-pattern — a test method that exercises code but never checks behavior
# passes vacuously and inflates the coverage signal without protecting against
# regressions.
#
# Allowlist (legitimate test bodies that intentionally lack asserts):
#   - `*Benchmark*.cs` — performance-recording test methods (output to console
#     for manual measurement, gated by category trait).
#
# Exits 0 if every test method (except allowlisted) has at least one assertion.
# Exits 1 otherwise.
#
# Usage:
#   bash tools/audit-test-asserts.sh           # exit 1 on violations
#   bash tools/audit-test-asserts.sh --report  # print full report, exit 0

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

REPORT_ONLY=0
if [[ "${1:-}" == "--report" ]]; then
  REPORT_ONLY=1
fi

VIOLATIONS=()
SCANNED_FILES=0
SCANNED_TESTS=0

# Match: [Fact] / [Fact(DisplayName="...")] / [Theory] / [Theory(...)] but not
# entries that contain `Skip = "..."` because Skip-flagged tests legitimately
# have no body to audit.
ATTR_REGEX='^\s*\[(Fact|Theory)(\([^)]*\))?\]\s*$'
SKIP_REGEX='Skip\s*='

while IFS= read -r -d '' file; do
  SCANNED_FILES=$((SCANNED_FILES + 1))

  # Skip benchmark files — their [Fact] methods record performance data and
  # legitimately do not have asserts (output is for manual inspection).
  if [[ "$file" == *Benchmark*.cs ]]; then
    continue
  fi

  # Read the file line-by-line, looking for [Fact]/[Theory] attribute lines.
  # When found, capture the method signature on a following line and then read
  # forward until we hit a closing brace at the method-body indentation level.
  awk -v file="$file" '
    BEGIN {
      in_method = 0
      brace_depth = 0
      has_assert = 0
      method_name = ""
      method_line = 0
    }

    # Detect [Fact] / [Theory] attribute (possibly with non-Skip parameters).
    /^[[:space:]]*\[(Fact|Theory)(\([^)]*\))?\][[:space:]]*$/ {
      if (index($0, "Skip = ") > 0 || index($0, "Skip=") > 0) next
      pending_attr = 1
      next
    }

    # When an attribute is pending, the next non-blank, non-attribute line is
    # the method signature. Capture method name + start tracking braces.
    pending_attr && /^[[:space:]]*public[[:space:]]+(async[[:space:]]+)?(Task|ValueTask|void)/ {
      # Extract method name: between "public ... void/Task/ValueTask " and the "("
      line = $0
      sub(/\(.*/, "", line)
      n = split(line, parts, /[[:space:]]+/)
      method_name = parts[n]
      method_line = NR
      pending_attr = 0
      in_method = 1
      brace_depth = 0
      has_assert = 0
      # Fall through so the same line is scanned for { and assertions.
    }

    in_method {
      # Count { and } on the line to track method body scope.
      open_count = gsub(/\{/, "&")
      close_count = gsub(/\}/, "&")
      brace_depth += open_count - close_count

      # Detect assertions inside the body. Recognized forms:
      #   - xUnit:             Assert.X(...)
      #   - FluentAssertions:  .Should() / .ShouldBe( / .ShouldNotBe(
      #   - NSubstitute mocks: .Received( / .DidNotReceive(
      # The signature line is included in this scan, but assertion tokens
      # never appear in C# method signatures so it does not cause false negatives.
      if (index($0, "Assert.") > 0 \
          || index($0, ".Should()") > 0 \
          || index($0, ".ShouldBe(") > 0 \
          || index($0, ".ShouldNotBe(") > 0 \
          || index($0, ".Received(") > 0 \
          || index($0, ".DidNotReceive(") > 0) {
        has_assert = 1
      }

      # When braces close back to zero, the method body just ended.
      if (brace_depth <= 0 && (open_count > 0 || close_count > 0)) {
        if (!has_assert && method_name != "") {
          printf "%s:%d %s\n", file, method_line, method_name
        }
        in_method = 0
        method_name = ""
        method_line = 0
      }
    }
  ' "$file" >> /tmp/audit-test-asserts.violations || true

done < <(find Tests -type f -name "*.cs" \
  -not -path "*/bin/*" -not -path "*/obj/*" -print0)

# Count scanned tests for the summary line. Approximate; relies on the same
# attribute regex above without filtering Skip.
SCANNED_TESTS=$(grep -rcE '^\s*\[(Fact|Theory)(\([^)]*\))?\]\s*$' Tests \
  --include="*.cs" \
  --exclude-dir=bin --exclude-dir=obj 2>/dev/null \
  | awk -F: '{s += $2} END {print s}')

if [[ -f /tmp/audit-test-asserts.violations ]] && [[ -s /tmp/audit-test-asserts.violations ]]; then
  VIOLATION_COUNT=$(wc -l < /tmp/audit-test-asserts.violations)
else
  VIOLATION_COUNT=0
fi

echo ""
echo "Audit summary:"
echo "  Files scanned:    $SCANNED_FILES"
echo "  [Fact]/[Theory]:  ~$SCANNED_TESTS"
echo "  Violations:       $VIOLATION_COUNT"

if [[ $VIOLATION_COUNT -gt 0 ]]; then
  echo ""
  echo "Tests lacking any assertion:"
  cat /tmp/audit-test-asserts.violations
  rm -f /tmp/audit-test-asserts.violations
  if [[ $REPORT_ONLY -eq 1 ]]; then
    exit 0
  fi
  exit 1
fi

rm -f /tmp/audit-test-asserts.violations
echo ""
echo "OK — every test method exercises at least one Assert./Should() expression."

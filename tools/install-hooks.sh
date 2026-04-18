#!/usr/bin/env bash
# Install per-clone git hooks for Asterisk.Sdk contributors.
#
# Why: git does not track .git/hooks/; each contributor must opt-in once.
# What: installs a pre-commit hook that runs `claudelint` when CLAUDE.md or
#       .claude/ files are staged. Non-blocking if claudelint is not installed.
#
# Usage:
#   ./tools/install-hooks.sh
#
# Uninstall:
#   rm .git/hooks/pre-commit

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOOK_FILE="$REPO_ROOT/.git/hooks/pre-commit"

if [[ ! -d "$REPO_ROOT/.git" ]]; then
    echo "ERROR: $REPO_ROOT/.git does not exist. Run this script from a cloned repo." >&2
    exit 1
fi

cat >"$HOOK_FILE" <<'HOOK'
#!/usr/bin/env bash
# Asterisk.Sdk pre-commit: lint CLAUDE.md and .claude/ via claudelint.
# Installed by tools/install-hooks.sh. Non-blocking if claudelint is absent.

set -euo pipefail

# Only run if a tracked Claude-related file is staged.
if ! git diff --cached --name-only --diff-filter=ACM |
     grep -E '^(CLAUDE\.md|\.claude/)' >/dev/null; then
    exit 0
fi

if ! command -v claudelint >/dev/null 2>&1; then
    cat >&2 <<MSG
[pre-commit] claudelint not installed; skipping CLAUDE.md / .claude/ lint.
           Install once with: npm install -g claude-code-lint
           Bypass this check: git commit --no-verify
MSG
    exit 0
fi

echo "[pre-commit] Running claudelint on staged CLAUDE.md / .claude/ changes..."
if ! claudelint; then
    cat >&2 <<MSG
[pre-commit] claudelint reported errors. Fix them and re-stage, or bypass with:
           git commit --no-verify
MSG
    exit 1
fi
HOOK

chmod +x "$HOOK_FILE"
echo "Installed $HOOK_FILE"

# Optional: ensure claudelint is installed.
if command -v claudelint >/dev/null 2>&1; then
    version="$(claudelint --version 2>/dev/null || echo unknown)"
    echo "Detected claudelint $version"
else
    cat <<MSG

Note: claudelint is not installed. To enable the hook, run:
    npm install -g claude-code-lint
The hook will skip gracefully until claudelint is available.
MSG
fi

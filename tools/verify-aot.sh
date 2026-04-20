#!/usr/bin/env bash
set -euo pipefail

# Usage: verify-aot.sh [rid]
#   rid (optional): target runtime (linux-x64 default). Pass win-x64 or osx-arm64
#                   for multi-runtime validation. Self-contained publish is forced.

RID="${1:-linux-x64}"
PROJECT="tools/AotCanary/AotCanary.csproj"
OUT_DIR="tools/AotCanary/bin/Release/net10.0/${RID}/publish"

echo "Verifying AOT publish safety for RID=${RID}..."
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained \
  --nologo \
  /warnaserror 2>&1

# Smoke-run the binary when the target RID matches the host RID.
# Detect host RID via `dotnet --info` rather than uname so we don't false-fail
# across musl/glibc variants inside CI containers.
HOST_RID=$(dotnet --info 2>/dev/null | awk -F': ' '/RID:/ { print $2; exit }' | tr -d ' \t\n\r')

if [[ "$RID" == "$HOST_RID" ]]; then
  BIN_NAME="AotCanary"
  if [[ "$RID" == win-* ]]; then
    BIN_NAME="${BIN_NAME}.exe"
  fi
  BIN_PATH="${OUT_DIR}/${BIN_NAME}"
  if [[ ! -x "$BIN_PATH" ]]; then
    echo "ERROR: expected binary not found at $BIN_PATH"
    ls -la "$OUT_DIR" || true
    exit 2
  fi
  echo "Smoke-running $BIN_PATH..."
  "$BIN_PATH"
else
  echo "Skipping smoke-run (host RID=$HOST_RID, target RID=$RID — cross-RID)."
fi

echo "AOT verification passed for RID=${RID} — 0 trim warnings"

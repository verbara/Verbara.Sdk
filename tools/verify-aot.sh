#!/usr/bin/env bash
set -euo pipefail
echo "Verifying AOT trim safety..."
dotnet publish tools/AotCanary/AotCanary.csproj \
  -c Release \
  --nologo \
  /p:PublishAot=true \
  /warnaserror 2>&1
echo "AOT verification passed — 0 trim warnings"

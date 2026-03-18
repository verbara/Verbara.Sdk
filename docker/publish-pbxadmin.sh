#!/bin/bash
set -euo pipefail

IMAGE="hreina/asterisk-pbx-admin"
VERSION="${1:-}"

if [ -z "$VERSION" ]; then
    echo "Usage: $0 <version>"
    echo "Example: $0 0.5.0-beta"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "Building $IMAGE:$VERSION ..."
docker build \
    -f "$REPO_ROOT/docker/Dockerfile.pbxadmin" \
    -t "$IMAGE:$VERSION" \
    -t "$IMAGE:latest" \
    "$REPO_ROOT"

echo ""
echo "Pushing $IMAGE:$VERSION ..."
docker push "$IMAGE:$VERSION"

echo "Pushing $IMAGE:latest ..."
docker push "$IMAGE:latest"

echo ""
echo "Done! Pull with:"
echo "  docker pull $IMAGE:$VERSION"

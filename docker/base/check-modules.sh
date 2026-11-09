#!/bin/bash
# Usage: check-modules.sh <image> <asterisk version>
#
# The module gate of the base image. Fails (exit 1) unless:
#   - `asterisk -V` in the image is exactly "Asterisk <version>";
#   - every module of expected-modules/<major>.txt is a file in /usr/lib/asterisk/modules;
#   - every one of them is loaded once Asterisk is fully booted on the image's own sample configuration, except those
#     in expected-modules/present-not-loaded.txt;
#   - neither Digium Opus binary (codec_opus.so, format_ogg_opus.so) is in the image.
# A module present but not expected is reported, not failed: the promise is what the image ships at least.
# Exit 2 means the check could not run (bad arguments, no list for the major, Asterisk never booted).
set -euo pipefail

if [ $# -ne 2 ]; then
    echo "usage: $0 <image> <asterisk version>" >&2
    exit 2
fi
image="$1"
version="$2"
here="$(cd "$(dirname "$0")" && pwd)"
major="${version%%.*}"
expected="$here/expected-modules/$major.txt"
not_loaded="$here/expected-modules/present-not-loaded.txt"
forbidden="codec_opus.so format_ogg_opus.so"

if [ ! -f "$expected" ]; then
    echo "no expected-modules/$major.txt: a new major needs its module list before it is built" >&2
    exit 2
fi

work="$(mktemp -d)"
name="check-modules-$$"
cleanup() {
    docker rm -f "$name" >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT

strip_list() { sed -e 's/#.*//' -e 's/[[:space:]]//g' -e '/^$/d' "$1" | LC_ALL=C sort -u; }

actual_version="$(docker run --rm --entrypoint /usr/sbin/asterisk "$image" -V)"
if [ "$actual_version" != "Asterisk $version" ]; then
    echo "FAIL: the image reports '$actual_version', not 'Asterisk $version'" >&2
    exit 1
fi

docker run -d --name "$name" "$image" >/dev/null
booted=false
for _ in $(seq 1 120); do
    if docker exec "$name" asterisk -rx 'core waitfullybooted' >/dev/null 2>&1; then
        booted=true
        break
    fi
    sleep 1
done
if [ "$booted" != true ]; then
    echo "Asterisk did not finish booting in 120 s; container log:" >&2
    docker logs --tail 50 "$name" >&2 || true
    exit 2
fi

docker exec "$name" sh -c 'cd /usr/lib/asterisk/modules && ls -1 -- *.so' | LC_ALL=C sort -u > "$work/files"
docker exec "$name" asterisk -rx 'module show' | awk 'NR > 1 && $1 ~ /\.so$/ { print $1 }' | LC_ALL=C sort -u > "$work/loaded"
strip_list "$expected" > "$work/expected"
strip_list "$not_loaded" > "$work/not-loaded"
LC_ALL=C comm -23 "$work/expected" "$work/not-loaded" > "$work/must-load"

LC_ALL=C comm -23 "$work/expected" "$work/files" > "$work/missing-files"
LC_ALL=C comm -23 "$work/must-load" "$work/loaded" > "$work/missing-loaded"
: > "$work/forbidden"
for module in $forbidden; do
    if grep -qx -- "$module" "$work/files"; then echo "$module" >> "$work/forbidden"; fi
done
LC_ALL=C comm -13 "$work/expected" "$work/files" | grep -vxF -f "$work/forbidden" > "$work/extra" || true

echo "$actual_version: $(wc -l < "$work/files") module files, $(wc -l < "$work/loaded") loaded; expected $(wc -l < "$work/expected") (expected-modules/$major.txt)"
failed=0
if [ -s "$work/missing-files" ]; then
    echo "FAIL: expected modules missing from /usr/lib/asterisk/modules:" >&2
    sed 's/^/  /' "$work/missing-files" >&2
    failed=1
fi
if [ -s "$work/missing-loaded" ]; then
    echo "FAIL: expected modules present but not loaded:" >&2
    sed 's/^/  /' "$work/missing-loaded" >&2
    failed=1
fi
if [ -s "$work/forbidden" ]; then
    echo "FAIL: the image carries the proprietary Opus binary, which stays out of a published base:" >&2
    sed 's/^/  /' "$work/forbidden" >&2
    failed=1
fi
if [ -s "$work/extra" ]; then
    echo "note: modules present but not in expected-modules/$major.txt (not a failure; add them to promise them):"
    sed 's/^/  /' "$work/extra"
fi
if [ "$failed" -ne 0 ]; then
    exit 1
fi
echo "OK: every expected module is present and loaded"

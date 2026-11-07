#!/bin/bash
# Usage: verify-tarball.sh <asterisk version> <empty download directory>
#
# Downloads asterisk-<version>.tar.gz and its .tar.gz.asc from downloads.asterisk.org into the directory and fails
# (exit 1) unless the signature is a valid signature by the Asterisk Development Team key in
# keys/asterisk-dev-team.asc, whose fingerprint must be the one pinned below, and the tarball's sha256 is the one
# versions.txt holds for that version. Prints the sha256 on success. Exit 2: bad arguments or no versions.txt line.
set -euo pipefail

PINNED_FINGERPRINT="F2FC93DB7587BD1FB49E045A5D984BE337191CE7"
RELEASES="https://downloads.asterisk.org/pub/telephony/asterisk/releases"

if [ $# -ne 2 ]; then
    echo "usage: $0 <asterisk version> <empty download directory>" >&2
    exit 2
fi
version="$1"
dir="$2"
here="$(cd "$(dirname "$0")" && pwd)"

if ! printf '%s\n' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-rc[0-9]+)?$'; then
    echo "'$version' is not <major>.<minor>.<patch>[-rcN]" >&2
    exit 2
fi
expected_sha="$(awk -v v="$version" '$1 == v { print $2 }' "$here/versions.txt")"
if [ -z "$expected_sha" ]; then
    echo "versions.txt has no line for $version" >&2
    exit 2
fi
mkdir -p "$dir"
if [ -n "$(ls -A "$dir")" ]; then
    echo "$dir is not empty" >&2
    exit 2
fi

gnupg="$(mktemp -d)"
trap 'rm -rf "$gnupg"' EXIT
chmod 700 "$gnupg"

tarball="$dir/asterisk-$version.tar.gz"
curl -fsSL -o "$tarball" "$RELEASES/asterisk-$version.tar.gz"
curl -fsSL -o "$tarball.asc" "$RELEASES/asterisk-$version.tar.gz.asc"

GNUPGHOME="$gnupg" gpg --batch --quiet --import "$here/keys/asterisk-dev-team.asc"
key_fingerprints="$(GNUPGHOME="$gnupg" gpg --batch --with-colons --fingerprint | awk -F: '$1 == "fpr" { print $10 }')"
primary="$(printf '%s\n' "$key_fingerprints" | head -n 1)"
if [ "$primary" != "$PINNED_FINGERPRINT" ]; then
    echo "FAIL: keys/asterisk-dev-team.asc is key $primary, not the pinned $PINNED_FINGERPRINT" >&2
    exit 1
fi

status="$(GNUPGHOME="$gnupg" gpg --batch --status-fd 1 --verify "$tarball.asc" "$tarball" 2>/dev/null || true)"
# VALIDSIG <signing key fpr> ... <primary key fpr>: the last field names the primary key that made the signature.
if ! printf '%s\n' "$status" | awk -v fpr="$PINNED_FINGERPRINT" '$1 == "[GNUPG:]" && $2 == "VALIDSIG" && $NF == fpr { found = 1 } END { exit !found }'; then
    echo "FAIL: asterisk-$version.tar.gz has no valid signature by $PINNED_FINGERPRINT" >&2
    printf '%s\n' "$status" >&2
    exit 1
fi

actual_sha="$(sha256sum "$tarball" | awk '{ print $1 }')"
if [ "$actual_sha" != "$expected_sha" ]; then
    echo "FAIL: asterisk-$version.tar.gz has sha256 $actual_sha; versions.txt holds $expected_sha" >&2
    exit 1
fi
echo "asterisk-$version.tar.gz: good signature by $PINNED_FINGERPRINT, sha256 $actual_sha"

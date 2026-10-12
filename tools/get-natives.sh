#!/usr/bin/env bash
# Fetches the native libraries Deadwax carries, for a development build on
# Linux or macOS (the Windows twin is get-natives.ps1): the natives-N release
# named in tools/NATIVES_RELEASE, checked against its SHA256SUMS, unpacked
# into native/<rid>/, where the project picks it up. A checkout without it
# builds and runs on the distribution's libraries, as before.
#
#   tools/get-natives.sh            fetch the pinned release, if not here already
#   tools/get-natives.sh --force    fetch it again regardless
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
REPO=gsa700/deadwax
TAG=$(tr -d '[:space:]' < "$HERE/NATIVES_RELEASE")
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    Darwin-arm64) RID=osx-arm64 ;;
    *) echo "no natives build for $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac
DIR="$ROOT/native/$RID"
if [ "${1:-}" != "--force" ] && [ -f "$DIR/natives-build-info.txt" ]; then
    echo "native/$RID already holds: $(head -1 "$DIR/natives-build-info.txt")"; exit 0
fi
. "$HERE/build-natives/pins.sh"   # sha256_of
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
BASE="https://github.com/$REPO/releases/download/$TAG"
echo "fetching $TAG ($TAG-$RID.zip)"
curl -sSL -o "$WORK/$TAG-$RID.zip" "$BASE/$TAG-$RID.zip"
curl -sSL -o "$WORK/SHA256SUMS" "$BASE/SHA256SUMS"
expected=$(grep " $TAG-$RID.zip\$" "$WORK/SHA256SUMS" | cut -d' ' -f1)
[ "$(sha256_of "$WORK/$TAG-$RID.zip")" = "$expected" ] || { echo "$TAG-$RID.zip does not match SHA256SUMS; nothing installed" >&2; exit 1; }
rm -rf "$DIR"; mkdir -p "$DIR"
unzip -q -o "$WORK/$TAG-$RID.zip" -d "$DIR"
chmod 755 "$DIR"/*.so.* "$DIR"/*.dylib 2>/dev/null || true
echo "native/$RID <- $(head -1 "$DIR/natives-build-info.txt")"

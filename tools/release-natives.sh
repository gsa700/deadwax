#!/usr/bin/env bash
# Deadwax - publish the native libraries: one natives-N pre-release, from native/.
#
#   tools/release-natives.sh natives-1            a DRAFT pre-release, to check first
#   tools/release-natives.sh natives-1 --publish  published at once
#
# WHAT GOES UP. native/<rid>/ for every platform listed below, each zipped with
# its natives-build-info.txt as natives-<N>-<rid>.zip (linux-x64 and win-x64 from
# tools/build-natives/build.sh here, osx-arm64 from the natives-macos.yml
# workflow's artifact, unpacked into native/osx-arm64/ first); SHA256SUMS over
# those; and the three source tarballs they were built from
# (tools/build-natives/sources, pinned in pins.sh) with SHA256SUMS-sources.
# That last part is not optional: libcdio and libcdio-paranoia are GPL, and
# handing out the binary obliges us to hand out the source.
#
# A PRE-RELEASE ON PURPOSE: the app's updater reads /releases/latest, which skips
# pre-releases, and the Edge feed takes only vX.Y.Z tags (ReleaseFeed), so a
# natives release is never mistaken for an app version. Then tools/NATIVES_RELEASE
# names the tag and tools/release.sh fetches it for every app release.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
REPO=gsa700/deadwax
TAG=${1:?usage: release-natives.sh natives-N [--publish] [--notes-file FILE]}
shift
PUBLISH=0; NOTES=""
while [ $# -gt 0 ]; do
    case "$1" in
        --publish) PUBLISH=1 ;;
        --notes-file) NOTES=${2:?--notes-file needs a file}; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done
die() { echo "release-natives: $*" >&2; exit 1; }
[[ $TAG =~ ^natives-[0-9]+$ ]] || die "tag must look like natives-1, not $TAG"
command -v gh >/dev/null || die "the GitHub CLI (gh) is needed"
gh release view "$TAG" -R "$REPO" >/dev/null 2>&1 && die "$TAG is already released on $REPO"
[ -n "$NOTES" ] && [ ! -f "$NOTES" ] && die "no such notes file: $NOTES"
. "$HERE/build-natives/pins.sh"
fetch_sources "$HERE/build-natives/sources"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
for rid in linux-x64 win-x64 osx-arm64; do
    dir="$ROOT/native/$rid"
    case $rid in
        linux-*) libs=(libcdio.so.19 libcdio_cdda.so.2 libcdio_paranoia.so.2 libFLAC.so.14) ;;
        win-*)   libs=(libcdio-19.dll libcdio_cdda-2.dll libcdio_paranoia-2.dll libFLAC.dll) ;;
        osx-*)   libs=(libcdio.19.dylib libcdio_cdda.2.dylib libcdio_paranoia.2.dylib libFLAC.14.dylib) ;;
    esac
    for lib in "${libs[@]}"; do [ -f "$dir/$lib" ] || die "missing $dir/$lib"; done
    [ -f "$dir/natives-build-info.txt" ] || die "missing $dir/natives-build-info.txt"
    grep -q "libcdio $LIBCDIO_VERSION, libcdio-paranoia $PARANOIA_VERSION, FLAC $FLAC_VERSION" "$dir/natives-build-info.txt" \
        || die "$rid was built from other versions than pins.sh names"
    (cd "$dir" && zip -9 -q "$WORK/$TAG-$rid.zip" "${libs[@]}" natives-build-info.txt)
    echo "  $rid: $(head -1 "$dir/natives-build-info.txt")"
done
(cd "$WORK" && sha256sum natives-*.zip > SHA256SUMS)
mkdir -p "$WORK/src"
cp "$HERE/build-natives/sources/"* "$WORK/src/"
(cd "$WORK/src" && sha256sum -- * > ../SHA256SUMS-sources)

ARGS=(--repo "$REPO" --prerelease --title "$TAG (libcdio $LIBCDIO_VERSION, libcdio-paranoia $PARANOIA_VERSION, FLAC $FLAC_VERSION; three platforms)")
if [ -n "$NOTES" ]; then ARGS+=(--notes-file "$NOTES"); else ARGS+=(--notes "The native libraries Deadwax carries, built from the attached sources by tools/build-natives. Not an app release."); fi
[ $PUBLISH -eq 1 ] || ARGS+=(--draft)
gh release create "$TAG" "${ARGS[@]}" "$WORK"/natives-*.zip "$WORK/SHA256SUMS" "$WORK"/src/* "$WORK/SHA256SUMS-sources"
echo
[ $PUBLISH -eq 1 ] && echo "PUBLISHED. Put $TAG in tools/NATIVES_RELEASE and commit it; tools/release.sh fetches it from then on." \
                   || echo "DRAFT. Look at it on GitHub, then publish it there or rerun with --publish."

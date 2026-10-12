#!/usr/bin/env bash
# Builds the native libraries that ship inside Deadwax: libcdio, libcdio-paranoia
# and libFLAC, from pinned source, in a container, so the result does not depend
# on the machine that built it. Same arrangement as AlbumWall's libmpv.
#
#   tools/build-natives/build.sh            linux-x64, native, for this machine
#   tools/build-natives/build.sh win-x64    the Windows DLLs, cross-compiled with mingw
#   (macOS is built on a GitHub macOS runner: .github/workflows/natives-macos.yml,
#    the same recipe in tools/build-natives/macos.sh)
#
# The result lands in native/<rid>/ (git-ignored), where the project picks it up
# and the single-file publish folds it in. Debian 12 sets the Linux floor: glibc
# 2.36 (Fedora, Debian 12, Ubuntu 24.04 and later). A RARE build: only when
# pins.sh changes. The output is published as a natives-N pre-release
# (tools/release-natives.sh) and fetched by tools/release.sh.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
. "$HERE/pins.sh"

case "${1:-native}" in
    native)
        [ "$(uname -m)" = x86_64 ] || { echo "only x86_64 here; arm64 builds natively on a Pi" >&2; exit 1; }
        RID=linux-x64; TARGET=linux; IMAGE=debian:12 ;;
    win-x64) RID=win-x64; TARGET=win64; IMAGE=debian:13 ;;
    *) echo "usage: build.sh [native|win-x64]" >&2; exit 1 ;;
esac

fetch_sources "$HERE/sources"
OUT="$ROOT/native/$RID"
rm -rf "$OUT"; mkdir -p "$OUT"
# The shared files are labelled "z" (shared), the output "Z" (private to this
# container): two builds at once relabelling the same recipe with "Z" took it
# away from each other (SELinux, 2026-10-12).
"${CONTAINER:-podman}" run --rm \
    -v "$HERE/inner.sh:/inner.sh:ro,z" -v "$HERE/pins.sh:/pins.sh:ro,z" \
    -v "$HERE/sources:/tarballs:ro,z" -v "$HERE/patches:/patches:ro,z" -v "$OUT:/out:Z" \
    "$IMAGE" bash /inner.sh "$TARGET"
echo "== native/$RID:"; ls -la "$OUT"

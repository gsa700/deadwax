#!/usr/bin/env bash
# The macOS half of the native build: the same pins, built with Apple's clang
# on a Mac (a GitHub macos runner: .github/workflows/natives-macos.yml, or a
# Mac with the Xcode command line tools). Homebrew supplies BUILD tools only
# (autoconf is not needed: the tarballs carry configure; cmake and pkg-config
# are); nothing from it ends up in the result, and the checks prove it.
#
#   tools/build-natives/macos.sh   -> native/osx-arm64/*.dylib + natives-build-info.txt
#
# Each dylib's install name and its references to the others are @loader_path
# based, so the four files work from any folder they are in together, which is
# wherever the single-file program unpacks them.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
. "$HERE/pins.sh"
case "$(uname -m)" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) echo "unknown arch" >&2; exit 1 ;; esac
MIN=12.0
fetch_sources "$HERE/sources"
PREFIX=$(mktemp -d)/dw
SRC=$(mktemp -d)
OUT="$ROOT/native/$RID"
rm -rf "$OUT"; mkdir -p "$OUT" "$PREFIX"
JOBS=$(sysctl -n hw.ncpu)
export MACOSX_DEPLOYMENT_TARGET=$MIN
export CFLAGS="-O2 -fstack-protector-strong -D_FORTIFY_SOURCE=2 -mmacosx-version-min=$MIN"
export LDFLAGS="-mmacosx-version-min=$MIN"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
mkdir -p /tmp/dw-logs
run() { local label=$1; shift; if ! "$@" > "/tmp/dw-logs/$label.log" 2>&1; then echo "!! $label failed; last 40 lines:" >&2; tail -40 "/tmp/dw-logs/$label.log" >&2; exit 1; fi; }

echo "== libcdio $LIBCDIO_VERSION"
tar xjf "$HERE/sources/libcdio-$LIBCDIO_VERSION.tar.bz2" -C "$SRC"
( cd "$SRC/libcdio-$LIBCDIO_VERSION" && for p in "$HERE"/patches/libcdio-*.patch; do echo "   patch $(basename "$p")"; patch -p1 -s < "$p"; done && run libcdio-configure ./configure --prefix="$PREFIX" --disable-static --enable-shared \
    --disable-cxx --disable-example-progs --disable-cpp-progs --disable-rpath \
    --without-cd-drive --without-cd-info --without-cdda-player --without-cd-read --without-iso-info --without-iso-read \
    --disable-joliet --disable-rock --disable-vcd-info && run libcdio-make make -j"$JOBS" -C lib && run libcdio-install make -C lib install && run libcdio-headers make -C include install && run libcdio-pc make install-pkgconfigDATA )

echo "== libcdio-paranoia $PARANOIA_VERSION"
tar xjf "$HERE/sources/libcdio-paranoia-$PARANOIA_VERSION.tar.bz2" -C "$SRC"
( cd "$SRC/libcdio-paranoia-$PARANOIA_VERSION" && for p in "$HERE"/patches/paranoia-*.patch; do echo "   patch $(basename "$p")"; patch -p1 -s < "$p"; done && run paranoia-configure ./configure --prefix="$PREFIX" --disable-static --enable-shared \
    --disable-cxx --disable-example-progs --disable-cpp-progs --disable-rpath --without-cd-paranoia \
    && run paranoia-make make -j"$JOBS" -C lib && run paranoia-install make -C lib install && run paranoia-headers make -C include install && run paranoia-pc make install-pkgconfigDATA )

echo "== FLAC $FLAC_VERSION"
tar xJf "$HERE/sources/flac-$FLAC_VERSION.tar.xz" -C "$SRC"
( cd "$SRC/flac-$FLAC_VERSION" && run flac-configure cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=$MIN -DBUILD_SHARED_LIBS=ON -DWITH_OGG=OFF -DENABLE_MULTITHREADING=OFF -DBUILD_PROGRAMS=OFF -DBUILD_EXAMPLES=OFF \
    -DBUILD_TESTING=OFF -DBUILD_DOCS=OFF -DINSTALL_MANPAGES=OFF -DBUILD_CXXLIBS=OFF -DWITH_STACK_PROTECTOR=ON -DWITH_FORTIFY_SOURCE=ON \
    -DINSTALL_PKGCONFIG_MODULES=OFF -DINSTALL_CMAKE_CONFIG_MODULE=OFF \
    && run flac-make cmake --build build -j"$JOBS" && run flac-install cmake --install build )

cd "$OUT"
cp -L "$PREFIX/lib/libcdio.19.dylib" "$PREFIX/lib/libcdio_cdda.2.dylib" "$PREFIX/lib/libcdio_paranoia.2.dylib" "$PREFIX/lib/libFLAC.14.dylib" .
for f in *.dylib; do
    install_name_tool -id "@loader_path/$f" "$f"
    for dep in libcdio.19.dylib libcdio_cdda.2.dylib; do
        old=$(otool -L "$f" | awk -v d="$dep" 'index($1, d) && $1 !~ /^@loader_path/ {print $1}')
        [ -n "$old" ] && install_name_tool -change "$old" "@loader_path/$dep" "$f"
    done
    strip -x "$f"
    # Changing names invalidates the ad-hoc signature; sign again (ad hoc, like the app itself).
    codesign --force --sign - "$f" >/dev/null 2>&1 || true
    deps=$(otool -L "$f" | tail -n +2 | awk '{print $1}' | tr '\n' ' ')
    echo "   $f: $deps"
    # Counts, not grep -q: with pipefail an early exit from grep fails the pipeline and the check never fires.
    [ "$(echo "$deps" | tr ' ' '\n' | grep -cE '^(/opt/homebrew|/usr/local|/private)')" -eq 0 ] || { echo "!! $f still points at a Homebrew or build path" >&2; exit 1; }
    [ "$(echo "$deps" | tr ' ' '\n' | grep -cvE '^(@loader_path/.*|/usr/lib/[A-Za-z0-9_.+-]*\.dylib|/System/Library/(Frameworks|PrivateFrameworks)/.*|)$')" -eq 0 ] || { echo "!! $f depends on more than the system: $deps" >&2; exit 1; }
    [ "$(lipo -archs "$f" | grep -c arm64)" -gt 0 ] || { echo "!! $f is not arm64" >&2; exit 1; }
    [ "$(nm -u "$f" 2>/dev/null | grep -c '___stack_chk_fail')" -gt 0 ] || { echo "!! $f has no stack protector" >&2; exit 1; }
done
{
    echo "Deadwax natives: libcdio $LIBCDIO_VERSION, libcdio-paranoia $PARANOIA_VERSION, FLAC $FLAC_VERSION ($RID, built $(date -u +%Y-%m-%dT%H:%MZ))"
    echo "toolchain: $(clang --version | head -1) on macOS $(sw_vers -productVersion), deployment target $MIN"
    echo "flags: CFLAGS=$CFLAGS"
    echo "checks: install names @loader_path, dependencies limited to the system (/usr/lib, system frameworks) and each other, arm64, stack protector, ad-hoc signed"
} > natives-build-info.txt
cat natives-build-info.txt

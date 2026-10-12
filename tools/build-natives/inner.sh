#!/usr/bin/env bash
# Runs INSIDE the build container (build.sh is the one to run).
#
#   inner.sh linux   -> /out/libcdio.so.19 libcdio_cdda.so.2 libcdio_paranoia.so.2 libFLAC.so.14
#   inner.sh win64   -> /out/libcdio-19.dll libcdio_cdda-2.dll libcdio_paranoia-2.dll libFLAC.dll
#
# Each library is built as small as its project allows: no programs, no C++
# bindings, no Ogg, no ISO 9660 extras. On Linux the result depends on glibc
# only (the two paranoia libraries on libcdio beside them, found through
# RUNPATH $ORIGIN); on Windows on DLLs that are part of Windows. Hardened the
# way AlbumWall's engine is (security review 2026-10-10): stack protector,
# FORTIFY, stack-clash and CET, full RELRO. The checks at the end are part of
# the recipe: a build that fails them is not a build.
set -euo pipefail
TARGET=${1:-linux}
. /pins.sh
PREFIX=/opt/dw
JOBS=$(nproc)
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends build-essential pkg-config cmake make bzip2 xz-utils ca-certificates file patchelf patch >/dev/null

HOST_FLAG=()
CMAKE_CROSS=()
MAKE_EXTRA=()
case "$TARGET" in
linux)
    HARDEN="-fstack-protector-strong -D_FORTIFY_SOURCE=2 -fstack-clash-protection -fcf-protection"
    export CFLAGS="-O2 -fPIC $HARDEN" CXXFLAGS="-O2 -fPIC $HARDEN"
    # RUNPATH $ORIGIN is set afterwards with patchelf: libtool re-evaluates
    # LDFLAGS and turns "$ORIGIN" into "RIGIN" on the way to the linker.
    export LDFLAGS="-Wl,-z,relro -Wl,-z,now -Wl,--enable-new-dtags"
    export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
    ;;
win64)
    apt-get install -y -qq --no-install-recommends mingw-w64 >/dev/null
    T=x86_64-w64-mingw32
    # The stack protector and pthreads come as DLLs on mingw (libssp-0.dll,
    # libwinpthread-1.dll) unless the linker can only find the static
    # archives: remove the import libraries, so every build links them in.
    find /usr \( -name 'libwinpthread.dll.a' -o -name 'libpthread.dll.a' -o -name 'libssp.dll.a' \) -print -delete
    export CC=$T-gcc-posix CXX=$T-g++-posix AR=$T-ar RANLIB=$T-ranlib STRIP=$T-strip WINDRES=$T-windres
    export CFLAGS="-O2 -fstack-protector-strong -D_FORTIFY_SOURCE=2" CXXFLAGS="-O2"
    # No libgcc/libwinpthread DLLs at run time: the runtime bits go in statically.
    # --exclude-libs: the static runtimes' symbols stay private to each DLL,
    # or libcdio_cdda.dll.a exports __stack_chk_fail and the next link sees
    # it twice.
    export LDFLAGS="-static-libgcc -Wl,--exclude-libs,libssp.a:libwinpthread.a -Wl,--nxcompat -Wl,--dynamicbase -Wl,--high-entropy-va"
    # -no-undefined: without it libtool quietly builds static libraries only on
    # mingw. A libtool flag, not a compiler one, so it goes to make, not to
    # configure (whose compiler test would choke on it).
    MAKE_EXTRA=("LDFLAGS=-no-undefined $LDFLAGS")
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"
    HOST_FLAG=(--host=$T)
    cat > /tmp/toolchain.cmake <<CM
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)
set(CMAKE_C_COMPILER $T-gcc-posix)
set(CMAKE_CXX_COMPILER $T-g++-posix)
set(CMAKE_RC_COMPILER $T-windres)
set(CMAKE_FIND_ROOT_PATH /usr/$T $PREFIX)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
CM
    CMAKE_CROSS=(-DCMAKE_TOOLCHAIN_FILE=/tmp/toolchain.cmake)
    ;;
*) echo "unknown target: $TARGET" >&2; exit 1 ;;
esac

mkdir -p /tmp/logs /src "$PREFIX"
run() { local label=$1; shift; if ! "$@" > "/tmp/logs/$label.log" 2>&1; then echo "!! $label failed; last 40 lines:" >&2; tail -40 "/tmp/logs/$label.log" >&2; exit 1; fi; }

# ---- libcdio: the drive layer, nothing else --------------------------------
echo "== libcdio $LIBCDIO_VERSION"
tar xjf "/tarballs/libcdio-$LIBCDIO_VERSION.tar.bz2" -C /src
cd "/src/libcdio-$LIBCDIO_VERSION"
# The security scan's fixes (patches/README.md): TOC parsers bounded on every
# platform, the macOS free routine ordered.
for p in /patches/libcdio-*.patch; do echo "   patch $(basename "$p")"; patch -p1 -s < "$p"; done
run libcdio-configure ./configure --prefix="$PREFIX" "${HOST_FLAG[@]}" --disable-static --enable-shared \
    --disable-cxx --disable-example-progs --disable-cpp-progs --disable-rpath \
    --without-cd-drive --without-cd-info --without-cdda-player --without-cd-read --without-iso-info --without-iso-read \
    --disable-joliet --disable-rock --disable-vcd-info
# Only the library: the programs and examples are not wanted, and on mingw
# they do not even link against the static runtimes chosen above.
run libcdio-make make -j"$JOBS" -C lib "${MAKE_EXTRA[@]}"
run libcdio-install make -C lib install
run libcdio-install-headers make -C include install
run libcdio-install-pc make install-pkgconfigDATA

# ---- libcdio-paranoia: cdda + paranoia, no cd-paranoia program -------------
echo "== libcdio-paranoia $PARANOIA_VERSION"
tar xjf "/tarballs/libcdio-paranoia-$PARANOIA_VERSION.tar.bz2" -C /src
cd "/src/libcdio-paranoia-$PARANOIA_VERSION"
for p in /patches/paranoia-*.patch; do echo "   patch $(basename "$p")"; patch -p1 -s < "$p"; done
run paranoia-configure ./configure --prefix="$PREFIX" "${HOST_FLAG[@]}" --disable-static --enable-shared \
    --disable-cxx --disable-example-progs --disable-cpp-progs --disable-rpath --without-cd-paranoia
run paranoia-make make -j"$JOBS" -C lib "${MAKE_EXTRA[@]}"
run paranoia-install make -C lib install
run paranoia-install-headers make -C include install
run paranoia-install-pc make install-pkgconfigDATA

# ---- libFLAC: the encoder, no Ogg, no programs -------------------------------
echo "== FLAC $FLAC_VERSION"
tar xJf "/tarballs/flac-$FLAC_VERSION.tar.xz" -C /src
cd "/src/flac-$FLAC_VERSION"
run flac-configure cmake -S . -B build "${CMAKE_CROSS[@]}" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DBUILD_SHARED_LIBS=ON -DWITH_OGG=OFF -DENABLE_MULTITHREADING=OFF -DBUILD_PROGRAMS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_TESTING=OFF \
    -DBUILD_DOCS=OFF -DINSTALL_MANPAGES=OFF -DBUILD_CXXLIBS=OFF -DWITH_STACK_PROTECTOR=ON -DWITH_FORTIFY_SOURCE=ON \
    -DINSTALL_PKGCONFIG_MODULES=OFF -DINSTALL_CMAKE_CONFIG_MODULE=OFF
run flac-make cmake --build build -j"$JOBS"
run flac-install cmake --install build

# ---- collect, strip, check -----------------------------------------------------
cd /out
case "$TARGET" in
linux)
    cp -L "$PREFIX/lib/libcdio.so.19" "$PREFIX/lib/libcdio_cdda.so.2" "$PREFIX/lib/libcdio_paranoia.so.2" "$PREFIX/lib/libFLAC.so.14" .
    strip --strip-unneeded ./*.so.*
    for f in *.so.*; do patchelf --set-rpath '$ORIGIN' "$f"; done
    for f in *.so.*; do
        needed=$(readelf -d "$f" | awk '/NEEDED/ {gsub(/[\[\]]/,"",$5); print $5}' | tr '\n' ' ')
        runpath=$(readelf -d "$f" | awk '/RUNPATH/ {print $5}')
        echo "   $f: needs $needed runpath $runpath"
        case "$f" in
            libcdio.so.19|libFLAC.so.14) [ "$(echo "$needed" | tr ' ' '\n' | grep -cvE '^(libc\.so\.6|libm\.so\.6|ld-linux.*|)$')" -eq 0 ] || { echo "!! $f depends on more than glibc: $needed" >&2; exit 1; } ;;
            *) [ "$(echo "$needed" | tr ' ' '\n' | grep -cvE '^(libc\.so\.6|libm\.so\.6|libcdio\.so\.19|libcdio_cdda\.so\.2|)$')" -eq 0 ] || { echo "!! $f depends on more than glibc and libcdio: $needed" >&2; exit 1; } ;;
        esac
        [ "$runpath" = '[$ORIGIN]' ] || { echo "!! $f has no RUNPATH \$ORIGIN" >&2; exit 1; }
        # grep -c, not grep -q: with pipefail, -q closing the pipe early makes readelf fail the pipeline.
        [ "$(readelf -d "$f" | grep -c 'BIND_NOW')" -gt 0 ] || { echo "!! $f is not BIND_NOW (full RELRO)" >&2; exit 1; }
        [ "$(readelf --dyn-syms -W "$f" | grep -c '__stack_chk_fail')" -gt 0 ] || { echo "!! $f has no stack protector" >&2; exit 1; }
    done
    GLIBC=$(ldd --version 2>/dev/null | sed -n '1p')   # not head: with pipefail its early exit fails the pipeline
    ;;
win64)
    # libcdio's own Makefiles install their DLLs under lib/, paranoia's and
    # FLAC's under bin/: take each from wherever it landed.
    for dll in libcdio-19.dll libcdio_cdda-2.dll libcdio_paranoia-2.dll libFLAC.dll; do
        found=$(find "$PREFIX" -name "$dll" -type f | head -1)
        [ -n "$found" ] || { echo "!! $dll was not built (find in $PREFIX):" >&2; find "$PREFIX" -name '*.dll' >&2; exit 1; }
        cp "$found" .
    done
    "$STRIP" --strip-unneeded ./*.dll
    for f in *.dll; do
        file "$f"
        needed=$($T-objdump -p "$f" | awk '/DLL Name/ {print $3}' | tr '\n' ' ')
        echo "   $f: imports $needed"
        [ "$(echo "$needed" | tr ' ' '\n' | grep -cviE '^(KERNEL32\.dll|msvcrt\.dll|USER32\.dll|ADVAPI32\.dll|SHELL32\.dll|libcdio-19\.dll|libcdio_cdda-2\.dll|)$')" -eq 0 ] \
            || { echo "!! $f imports more than Windows and libcdio: $needed" >&2; exit 1; }
    done
    GLIBC="mingw-w64 $($T-gcc-posix -dumpversion)"
    ;;
esac
{
    echo "Deadwax natives: libcdio $LIBCDIO_VERSION, libcdio-paranoia $PARANOIA_VERSION, FLAC $FLAC_VERSION ($TARGET, built $(date -u +%Y-%m-%dT%H:%MZ))"
    echo "toolchain: $GLIBC in $(. /etc/os-release; echo "$PRETTY_NAME")"
    echo "flags: CFLAGS=$CFLAGS LDFLAGS=$LDFLAGS"
    echo "checks: dependencies limited to the platform and each other; $([ $TARGET = linux ] && echo 'RUNPATH $ORIGIN, full RELRO, stack protector' || echo 'NX, ASLR, high-entropy VA')"
} > natives-build-info.txt
cat natives-build-info.txt

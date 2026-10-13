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
    # No CXX here: libcdio's configure never probes a C++ compiler (C++ is
    # disabled), but with CXX in the environment libtool still runs its C++
    # tag, decides that compiler is "not GCC", picks MSVC-style library
    # naming, and because libtool's naming variables are global the C++ pass
    # overwrites the C one: the import library then gets the DLL's own name
    # and the link writes an ar archive called libcdio-19.dll (2026-10-12).
    # FLAC's cmake takes its compilers from the toolchain file below.
    export CC=$T-gcc-posix AR=$T-ar RANLIB=$T-ranlib STRIP=$T-strip WINDRES=$T-windres
    export CFLAGS="-O2 -fstack-protector-strong -D_FORTIFY_SOURCE=2"
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
grep -i "building static\|undefined symbols not allowed\|warning: .*libtool" /tmp/logs/libcdio-make.log | head -5 || true
# The DLL is taken from the build tree: libcdio's install step puts it under
# lib/ rather than bin/, and a mis-configured libtool (see CXX above) can
# leave an ar archive under the DLL's name, which the PE check catches.
if [ "$TARGET" = win64 ]; then
    dll=lib/driver/.libs/libcdio-19.dll
    file -b "$dll" | grep -q 'PE32+' || { echo "!! $dll is not a DLL: $(file -b "$dll")" >&2; grep -n 'libtool: link' /tmp/logs/libcdio-make.log | grep -i driver >&2; exit 1; }
    cp "$dll" /out/
fi
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
[ "$TARGET" = win64 ] && cp lib/cdda_interface/.libs/libcdio_cdda-2.dll lib/paranoia/.libs/libcdio_paranoia-2.dll /out/
run paranoia-install make -C lib install
run paranoia-install-headers make -C include install
run paranoia-install-pc make install-pkgconfigDATA

# ---- libFLAC: the encoder, no Ogg, no programs -------------------------------
echo "== FLAC $FLAC_VERSION"
tar xJf "/tarballs/flac-$FLAC_VERSION.tar.xz" -C /src
cd "/src/flac-$FLAC_VERSION"
# THE SAME BYTES ON EVERY PLATFORM (2026-10-13): the encoder's floating-point
# choices are made identically everywhere, so the same audio gives the same
# file on Linux, Windows and macOS, on any CPU. Three parts: cosf and log
# through CORE-MATH (correctly rounded) instead of the platform's libm; no SIMD
# (WITH_ASM=OFF: AVX2/FMA/NEON sum in other orders); no fused multiply-add
# (-ffp-contract=off). See patches/README.md.
for p in /patches/flac-*.patch; do echo "   patch $(basename "$p")"; patch -p1 -s < "$p"; done
cp /coremath/cosf.c src/libFLAC/crmath_cosf.c; cp /coremath/log.c src/libFLAC/crmath_log.c; cp /coremath/dint.h src/libFLAC/
CFLAGS="$CFLAGS -ffp-contract=off" run flac-configure cmake -S . -B build "${CMAKE_CROSS[@]}" -DCMAKE_BUILD_TYPE=Release -DWITH_ASM=OFF -DCMAKE_INSTALL_PREFIX="$PREFIX" \
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
    # The libcdio DLLs were taken from their build trees above; FLAC's cmake
    # build installs its DLL under bin/.
    cp "$PREFIX/bin/libFLAC.dll" .
    "$STRIP" --strip-unneeded ./*.dll
    for f in *.dll; do
        file -b "$f" | grep -q 'PE32+ executable.*DLL' || { echo "!! $f is not a Windows DLL: $(file -b "$f")" >&2; exit 1; }
        # DllCharacteristics: 0x20 high-entropy VA, 0x40 ASLR (dynamic base), 0x100 NX.
        chars=$(( 16#$($T-objdump -p "$f" | awk '/DllCharacteristics/ {print $2}') ))
        [ $(( chars & 0x160 )) -eq $(( 0x160 )) ] || { echo "!! $f lacks NX/ASLR/high-entropy VA: $(printf '0x%x' "$chars")" >&2; exit 1; }
        # The stack protector is linked in statically (libssp.a): its failure message is the trace it leaves.
        [ "$(strings -a "$f" | grep -c 'stack smashing detected')" -gt 0 ] || { echo "!! $f has no stack protector" >&2; exit 1; }
        needed=$($T-objdump -p "$f" | awk '/DLL Name/ {print $3}' | tr '\n' ' ')
        echo "   $f: imports $needed"
        [ "$(echo "$needed" | tr ' ' '\n' | grep -cviE '^(KERNEL32\.dll|msvcrt\.dll|USER32\.dll|ADVAPI32\.dll|SHELL32\.dll|WINMM\.dll|libcdio-19\.dll|libcdio_cdda-2\.dll|)$')" -eq 0 ] \
            || { echo "!! $f imports more than Windows and libcdio: $needed" >&2; exit 1; }
    done
    GLIBC="mingw-w64 $($T-gcc-posix -dumpversion)"
    ;;
esac
# ---- the same bytes as every other platform ---------------------------------
# encode-check.c encodes a generated signal through the libFLAC just built;
# its SHA-256 is pinned in encode-check.sha256 (the Linux build's). A tripwire,
# not a proof: it catches a glibc-style rounding difference, and a build that
# sums or fuses differently. Windows runs it under Wine.
echo "== encode check"
case "$TARGET" in
linux)
    gcc -O2 -o /tmp/encode-check /encode-check.c -ldl
    /tmp/encode-check /out/libFLAC.so.14 /tmp/encode-check.flac ;;
win64)
    apt-get install -y -qq --no-install-recommends wine wine64 >/dev/null
    $T-gcc-posix -O2 -o /tmp/encode-check.exe /encode-check.c
    WINEDEBUG=-all wineboot -i >/dev/null 2>&1 || true
    cp /out/libFLAC.dll /tmp/
    (cd /tmp && WINEDEBUG=-all wine encode-check.exe libFLAC.dll encode-check.flac >/dev/null 2>&1) ;;
esac
(cd /tmp && sha256sum -c --quiet /encode-check.sha256) || { echo "!! libFLAC encodes differently from the pinned build (encode-check.sha256)" >&2; exit 1; }
echo "   encode-check.flac matches the pinned SHA-256"
{
    echo "Deadwax natives: libcdio $LIBCDIO_VERSION, libcdio-paranoia $PARANOIA_VERSION, FLAC $FLAC_VERSION ($TARGET, built $(date -u +%Y-%m-%dT%H:%MZ))"
    echo "toolchain: $GLIBC in $(. /etc/os-release; echo "$PRETTY_NAME")"
    echo "flags: CFLAGS=$CFLAGS LDFLAGS=$LDFLAGS"
    echo "checks: dependencies limited to the platform and each other; $([ $TARGET = linux ] && echo 'RUNPATH $ORIGIN, full RELRO, stack protector' || echo 'NX, ASLR, high-entropy VA, stack protector'); encode check (same bytes)"
} > natives-build-info.txt
cat natives-build-info.txt

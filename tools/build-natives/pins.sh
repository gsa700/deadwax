# The native libraries Deadwax carries, pinned. Change deliberately, together,
# re-run the code scan (Code-Security-Review-Policy: a bundled dependency is
# scanned on first import and on every bump) and re-test a rip on every platform.
#
# libcdio 2.3.0 and libcdio-paranoia 10.2+2.0.2 are what Fedora ships and what
# every log in the library names ("libcdio-paranoia (libcdio 2.3.0 ...)");
# FLAC 1.5.0 likewise ("reference libFLAC 1.5.0" is the vendor string in every
# file). Same versions bundled = same engine everywhere.
LIBCDIO_VERSION=2.3.0
LIBCDIO_URL=https://github.com/libcdio/libcdio/releases/download/2.3.0/libcdio-2.3.0.tar.bz2
LIBCDIO_SHA256=53e83d284667535a767fd2d31edad1a6701591960459df373a10f1f21e80a7ed
PARANOIA_VERSION=10.2+2.0.2
PARANOIA_URL=https://ftp.gnu.org/gnu/libcdio/libcdio-paranoia-10.2+2.0.2.tar.bz2
PARANOIA_SHA256=186892539dedd661276014d71318c8c8f97ecb1250a86625256abd4defbf0d0c
FLAC_VERSION=1.5.0
FLAC_URL=https://ftp.osuosl.org/pub/xiph/releases/flac/flac-1.5.0.tar.xz
FLAC_SHA256=f2c1c76592a82ffff8413ba3c4a1299b6c7ab06c734dee03fd88630485c2b920

# Fetches every pinned tarball into $1, checking each against its checksum;
# a file already there and correct is kept.
fetch_sources() {
    local dir=$1
    mkdir -p "$dir"
    local name url sum
    for spec in "libcdio-$LIBCDIO_VERSION.tar.bz2 $LIBCDIO_URL $LIBCDIO_SHA256" \
                "libcdio-paranoia-$PARANOIA_VERSION.tar.bz2 $PARANOIA_URL $PARANOIA_SHA256" \
                "flac-$FLAC_VERSION.tar.xz $FLAC_URL $FLAC_SHA256"; do
        set -- $spec; name=$1; url=$2; sum=$3
        if [ ! -f "$dir/$name" ] || ! echo "$sum  $dir/$name" | sha256sum -c --quiet 2>/dev/null; then
            echo "== fetching $name"
            # To a temporary name, then into place: a build reading the folder
            # at the same moment never sees a half-written file.
            curl -sSL -o "$dir/$name.part" "$url"
            echo "$sum  $dir/$name.part" | sha256sum -c --quiet || { echo "checksum mismatch: $name" >&2; rm -f "$dir/$name.part"; return 1; }
            mv -f "$dir/$name.part" "$dir/$name"
        fi
    done
}

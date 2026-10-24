#!/usr/bin/env bash
# Deadwax - build Deadwax.app, the Mac's form of the program. On a Mac only.
#
#   tools/make-macos-app.sh            dist/Deadwax.app, for the machine's own arch
#   tools/make-macos-app.sh --install  and copy it to /Applications (replacing one there)
#   tools/make-macos-app.sh --notarize also have Apple notarize it and staple the ticket
#                                      (needs the Developer ID; options combine)
#
# WHAT IS IN IT: the same single-file program release.sh publishes for every
# platform (libcdio and libcdio-paranoia from native/osx-<arch>/ are
# inside it), in the folder layout macOS expects of an application:
#
#   Deadwax.app/Contents/Info.plist        name, version, identity, icon
#   Deadwax.app/Contents/MacOS/Deadwax     the program
#   Deadwax.app/Contents/Resources/Deadwax.icns
#
# Installing on a Mac IS putting that folder in /Applications: Finder,
# Launchpad, Spotlight and the Dock find it there. AlbumWall's
# scripts/make-macos-app.sh is the model; the two stay alike.
#
# SIGNED WITH APPLE'S DEVELOPER ID when this Mac has that certificate (since
# 2026-10-23, team 4QX5GG2276), else with a local certificate, else AD HOC.
# Not yet notarized, and without the hardened runtime: a DOWNLOADED copy still
# doesn't get past Gatekeeper, so this is for building at home, not for a
# release. macOS remembers a permission (the Music folder the rips go into)
# against the app's signature; ad hoc, every rebuild would ask again. Without a
# Developer ID, make a code-signing certificate once in Keychain Access
# (Certificate Assistant, Create a Certificate: Self-Signed Root, Certificate
# Type Code Signing), named "Deadwax Local", or name any certificate in
# DEADWAX_SIGN_ID.
#
# The identity, io.github.gsa700.Deadwax, is where macOS keeps the app's
# permissions and preferences, so it must not change.
#
# Proven 2026-10-15 on the first Mac: subchannel, read and rip of the
# Honeydrippers reference disc matched whipper's (G1-G5), eject works.

set -euo pipefail

[ "$(uname -s)" = Darwin ] || { echo "make-macos-app.sh: on a Mac only" >&2; exit 1; }

INSTALL=no; NOTARIZE=no
for arg in "$@"; do
    case "$arg" in
        --install) INSTALL=yes ;;
        --notarize) NOTARIZE=yes ;;
        *) echo "usage: $0 [--install] [--notarize]" >&2; exit 2 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/Deadwax.App"
case "$(uname -m)" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) echo "unknown arch" >&2; exit 1 ;; esac
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
BUNDLE_ID=io.github.gsa700.Deadwax
MIN_MACOS=13.0      # .NET 10's floor (the natives themselves go back to 12.0)

OUT="$ROOT/dist"
APP="$OUT/Deadwax.app"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Without its own libraries the app has nothing to read a disc with: a Mac
# has no libcdio. A bundle without them is not worth making.
for lib in libcdio.19.dylib libcdio_cdda.2.dylib libcdio_paranoia.2.dylib; do
    [ -f "$ROOT/native/$RID/$lib" ] || {
        echo "no native/$RID/$lib: fetch the natives first (tools/get-natives.sh)" >&2; exit 1; }
done

echo "== publish $RID $VERSION"
# The flags tools/release.sh publishes with.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$WORK/pub" -nologo -v quiet

echo "== icon"
# Rendered from icon.svg by macOS itself (sips reads SVG), every size drawn at
# that size. Apple's grid puts the artwork at 824 of 1024 with clear margin all
# round; a full-bleed square looks oversized beside every other Dock icon, so
# each size is drawn at 80.5% and centred on a transparent canvas.
ICONSET="$WORK/Deadwax.iconset"
mkdir -p "$ICONSET"
for spec in 16:icon_16x16 32:icon_16x16@2x 32:icon_32x32 64:icon_32x32@2x \
            128:icon_128x128 256:icon_128x128@2x 256:icon_256x256 512:icon_256x256@2x \
            512:icon_512x512 1024:icon_512x512@2x; do
    size=${spec%%:*} name=${spec#*:}
    art=$(( size * 824 / 1024 ))
    sips -s format png -z "$art" "$art" "$PROJECT/Assets/icon.svg" --out "$WORK/art.png" >/dev/null
    sips -p "$size" "$size" "$WORK/art.png" --out "$ICONSET/$name.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$WORK/Deadwax.icns"

echo "== bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$WORK/pub/Deadwax" "$APP/Contents/MacOS/Deadwax"
cp "$WORK/Deadwax.icns" "$APP/Contents/Resources/Deadwax.icns"
# What the licenses ask to travel with the program, as in a release zip.
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/"
cp -R "$ROOT/licenses" "$APP/Contents/Resources/licenses"
printf 'APPL????' > "$APP/Contents/PkgInfo"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key>
	<string>$BUNDLE_ID</string>
	<key>CFBundleName</key>
	<string>Deadwax</string>
	<key>CFBundleDisplayName</key>
	<string>Deadwax</string>
	<key>CFBundleExecutable</key>
	<string>Deadwax</string>
	<key>CFBundleIconFile</key>
	<string>Deadwax</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>$VERSION</string>
	<key>CFBundleVersion</key>
	<string>$VERSION</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>LSMinimumSystemVersion</key>
	<string>$MIN_MACOS</string>
	<key>LSApplicationCategoryType</key>
	<string>public.app-category.music</string>
	<key>NSHighResolutionCapable</key>
	<true/>
	<key>NSHumanReadableCopyright</key>
	<string>GPLv3.</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist" >/dev/null

# Which certificate: DEADWAX_SIGN_ID if set (a name or a SHA-1); else this Mac's
# Apple Developer ID Application certificate when there is exactly one (two
# while one is being renewed: then name one); else the local "Deadwax Local".
if [ -n "${DEADWAX_SIGN_ID:-}" ]; then
    SIGN_ID=$DEADWAX_SIGN_ID
else
    DEVID=$(security find-identity -v -p codesigning | sed -n 's/^ *[0-9]*) \([0-9A-F]\{40\}\) "Developer ID Application: .*"$/\1/p')
    case $(printf '%s' "$DEVID" | grep -c . || true) in
        0) SIGN_ID="Deadwax Local" ;;
        1) SIGN_ID=$DEVID ;;
        *) echo "several Developer ID Application certificates on this Mac: name one in DEADWAX_SIGN_ID" >&2
           security find-identity -v -p codesigning | grep "Developer ID Application" >&2; exit 1 ;;
    esac
fi
# A Developer ID signature gets the hardened runtime, which notarization
# requires, with the two exceptions .NET needs (tools/macos/Deadwax.entitlements:
# JIT, and loading the libraries the single-file program unpacks into ~/.net;
# without the second, Skia fails to load and the app aborts at start, tested
# 2026-10-23), and Apple's timestamp.
if security find-identity -p codesigning | grep -qF -e "\"$SIGN_ID\"" -e ") $SIGN_ID "; then
    LABEL=$(security find-identity -p codesigning | grep -F -e "\"$SIGN_ID\"" -e ") $SIGN_ID " | head -1 | sed 's/^[^"]*"\([^"]*\)".*/\1/')
    echo "== sign ($LABEL)"
    case $LABEL in
        "Developer ID Application:"*)
            codesign --force --options runtime --timestamp \
                --entitlements "$ROOT/tools/macos/Deadwax.entitlements" --sign "$SIGN_ID" "$APP" ;;
        *)
            [ "$NOTARIZE" = yes ] && { echo "--notarize needs a Developer ID certificate, not \"$LABEL\"" >&2; exit 1; }
            codesign --force --sign "$SIGN_ID" "$APP" ;;
    esac
else
    [ "$NOTARIZE" = yes ] && { echo "--notarize needs a Developer ID certificate" >&2; exit 1; }
    echo "== sign (ad hoc: no \"$SIGN_ID\" certificate on this Mac, so macOS will ask for its permissions again)"
    codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"

# NOTARIZE: Apple checks the signed app and issues a ticket; stapling puts the
# ticket inside the app, so a Mac that downloads it can check it offline.
# Credentials are a notarytool keychain profile (made once with `xcrun notarytool
# store-credentials`), named in DEADWAX_NOTARY_PROFILE, "notary" by default.
if [ "$NOTARIZE" = yes ]; then
    echo "== notarize (Apple; usually a few minutes)"
    ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
    xcrun notarytool submit "$WORK/notarize.zip" --keychain-profile "${DEADWAX_NOTARY_PROFILE:-notary}" --wait \
        | tee "$WORK/notary.txt"
    if ! grep -q "status: Accepted" "$WORK/notary.txt"; then
        id=$(sed -n 's/^ *id: //p' "$WORK/notary.txt" | head -1)
        [ -n "$id" ] && xcrun notarytool log "$id" --keychain-profile "${DEADWAX_NOTARY_PROFILE:-notary}" >&2
        echo "notarization was not accepted" >&2; exit 1
    fi
    xcrun stapler staple "$APP"
    xcrun stapler validate "$APP"
    spctl --assess --type execute -vv "$APP"
fi

echo "== $APP"
du -sh "$APP" | cut -f1
file "$APP/Contents/MacOS/Deadwax"

if [ "$INSTALL" = yes ]; then
    echo "== install to /Applications"
    if pgrep -xq Deadwax; then
        echo "Deadwax is running: quit it first" >&2; exit 1
    fi
    rm -rf /Applications/Deadwax.app
    cp -R "$APP" /Applications/Deadwax.app
    # Tell Launch Services about it now rather than whenever it next looks.
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /Applications/Deadwax.app
    echo "installed: /Applications/Deadwax.app"
fi

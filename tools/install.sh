#!/usr/bin/env bash
# Install Deadwax for this user: a Native AOT build (no dotnet needed to run
# it; the program with its two graphics libraries beside it, as a release has
# since 0.8.0), its icon, and an app-menu entry. Rerun after pulling changes.
#
#   tools/install.sh            build and install
#   tools/install.sh --remove   take it all out again
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
dest="$HOME/.local/share/deadwax"
apps="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor"
entry="$apps/deadwax.desktop"   # its name is the Wayland app_id (WaylandShell.AppId)

if [[ "${1:-}" == "--remove" ]]; then
    rm -rf "$dest" "$entry" "$icons/scalable/apps/deadwax.svg" "$icons/256x256/apps/deadwax.png"  # svg: older runs wrote one
    command -v update-desktop-database >/dev/null && update-desktop-database -q "$apps" || true
    echo "Deadwax removed."
    exit 0
fi

dotnet="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
[[ -x "$dotnet" ]] || { echo "No .NET SDK found (looked on PATH and in ~/.dotnet)." >&2; exit 1; }

# Native AOT builds for this machine only, and links with the system's C
# toolchain: clang, and zlib's headers. A one-line compile checks both.
case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) echo "No Native AOT build for $(uname -m)." >&2; exit 1 ;;
esac
if ! command -v clang >/dev/null || ! echo '#include <zlib.h>' | clang -E -x c - >/dev/null 2>&1; then
    echo "Native AOT needs clang and zlib's development files:" >&2
    echo "  Fedora: sudo dnf install clang zlib-ng-compat-devel" >&2
    echo "  Debian, Ubuntu: sudo apt install clang zlib1g-dev" >&2
    exit 1
fi

# The libraries the program has beside it: the same list as
# src/Deadwax.App/Install/InstallLayout.cs NativeLibraries (which the
# self-updater installs) and tools/release.sh native_libraries; keep all three
# in step. A build with any other .so is refused rather than half installed.
libs="libSkiaSharp.so libHarfBuzzSharp.so"

echo "Building Deadwax for $rid (Native AOT, a minute or two)..."
pub="$(mktemp -d)"
trap 'rm -rf "$pub"; rm -f "$dest"/.*.new' EXIT
"$dotnet" publish "$repo/src/Deadwax.App/Deadwax.App.csproj" -c Release -r "$rid" \
    -p:PublishAot=true -o "$pub" -v q --nologo
have=$(cd "$pub" && { ls *.so 2>/dev/null || true; } | sort | paste -sd' ' -)
want=$(printf '%s\n' $libs | sort | paste -sd' ' -)
[[ "$have" == "$want" ]] || { echo "The build has [$have] beside the program, expected [$want]: update the three lists." >&2; exit 1; }

# Not under a running Deadwax: it may load a library later, and must not get
# one from another build.
if pgrep -x Deadwax >/dev/null; then
    echo "Deadwax is running: quit it, then run this again." >&2; exit 1
fi

# The libraries first and the program last, as the app's installer and updater
# do, each under a temporary name renamed over the old one; then whatever
# earlier builds left that this one does not have (other .so files, debug files).
mkdir -p "$dest"
for f in $libs Deadwax; do
    cp "$pub/$f" "$dest/.$f.new"
    mv -f "$dest/.$f.new" "$dest/$f"
done
for f in "$dest"/*.so "$dest"/*.pdb "$dest"/*.dbg; do
    [[ -e "$f" ]] || continue
    case " $libs " in *" $(basename "$f") "*) ;; *) rm -f "$f" ;; esac
done

# The 256px PNG only, the icon the app itself writes and its uninstall removes.
mkdir -p "$apps" "$icons/256x256/apps"
cp "$repo/src/Deadwax.App/Assets/icon-256.png" "$icons/256x256/apps/deadwax.png"

cat > "$entry" <<DESKTOP
[Desktop Entry]
Type=Application
Version=1.5
Name=Deadwax
Comment=Rip CDs into a FLAC library you own
Exec="$dest/Deadwax"
Icon=deadwax
Terminal=false
Categories=AudioVideo;Audio;DiscBurning;
Keywords=CD;rip;FLAC;AccurateRip;MusicBrainz;
StartupWMClass=Deadwax
DESKTOP

command -v update-desktop-database >/dev/null && update-desktop-database -q "$apps" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$icons" || true
echo "Installed: $dest/Deadwax, with an app-menu entry."

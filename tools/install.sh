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

# Native AOT links with the system's C toolchain: clang, and zlib's headers.
if ! command -v clang >/dev/null; then
    echo "Native AOT needs clang and zlib's development files:" >&2
    echo "  Fedora: sudo dnf install clang zlib-ng-compat-devel" >&2
    echo "  Debian, Ubuntu: sudo apt install clang zlib1g-dev" >&2
    exit 1
fi

echo "Building Deadwax (Native AOT, a minute or two)..."
pub="$(mktemp -d)"
trap 'rm -rf "$pub"' EXIT
"$dotnet" publish "$repo/src/Deadwax.App/Deadwax.App.csproj" -c Release -r linux-x64 \
    -p:PublishAot=true -o "$pub" -v q --nologo

# The program and its libraries only (InstallLayout.NativeLibraries), not the
# debug files beside them. Each goes in under a temporary name and is renamed
# over the old one: a running Deadwax keeps its file, and copying over a
# running program would fail. Debug files of earlier builds go.
mkdir -p "$dest"
for f in "$pub"/Deadwax "$pub"/*.so; do
    cp "$f" "$dest/.$(basename "$f").new"
    mv -f "$dest/.$(basename "$f").new" "$dest/$(basename "$f")"
done
chmod +x "$dest/Deadwax"
rm -f "$dest"/*.pdb "$dest"/*.dbg

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

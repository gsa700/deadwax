#!/usr/bin/env bash
# Install Deadwax for this user: a self-contained build (no dotnet needed to run
# it), its icon, and an app-menu entry. Rerun after pulling changes.
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
    rm -rf "$dest" "$entry" "$icons/scalable/apps/deadwax.svg" "$icons/256x256/apps/deadwax.png"
    command -v update-desktop-database >/dev/null && update-desktop-database -q "$apps" || true
    echo "Deadwax removed."
    exit 0
fi

dotnet="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
[[ -x "$dotnet" ]] || { echo "No .NET SDK found (looked on PATH and in ~/.dotnet)." >&2; exit 1; }

echo "Building Deadwax..."
"$dotnet" publish "$repo/src/Deadwax.App/Deadwax.App.csproj" -c Release -r linux-x64 \
    --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$dest" -v q --nologo

mkdir -p "$apps" "$icons/scalable/apps" "$icons/256x256/apps"
cp "$repo/src/Deadwax.App/Assets/icon.svg" "$icons/scalable/apps/deadwax.svg"
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

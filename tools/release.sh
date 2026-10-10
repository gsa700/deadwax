#!/usr/bin/env bash
# Deadwax - cut a release: one GitHub release carrying the Linux build.
#
#   tools/release.sh 0.2.0                     a DRAFT release, to look at first
#   tools/release.sh 0.2.0 --notes-file N.md   with the notes written by hand
#   tools/release.sh 0.2.0 --publish           published at once (installed copies are offered it)
#   tools/release.sh 0.2.0 --edge --publish    an EDGE build: a GitHub pre-release, offered only to
#                                              copies set to Edge (Preferences > About). The usual way
#                                              every build goes out since 0.2.11.
#   PROMOTE to Stable, once it has run on Edge without trouble (his call, every time):
#                                              gh release edit vX.Y.Z -R gsa700/deadwax --prerelease=false --latest
#   tools/release.sh 0.2.0 --dry-run           build and zip only: no tag, no release; the
#                                              version/tree checks only warn. Leaves the zip in ./dist.
#
# WHAT A RELEASE IS, because the updater (src/Deadwax.App/UpdateService.cs) reads
# exactly this and nothing else: a GitHub release tagged vX.Y.Z, carrying
# Deadwax-linux-x64.zip (the single-file program) and SHA256SUMS listing it.
# Two channels (Deadwax.Core/ReleaseFeed.cs): Stable reads /releases/latest,
# which never returns a pre-release; Edge reads the release list and takes the
# newest version, pre-release or not. Both refuse a download SHA256SUMS does not
# vouch for and never see drafts. So a DRAFT is safe to make, look at and
# delete; publishing an --edge build offers it to Edge copies only; promoting it
# (clearing the pre-release flag) offers the same binary to everyone.
#
# REFUSES TO RUN unless: the tree is clean, HEAD is what origin/main has (a
# release is built from pushed code), <Version> in the csproj equals the version
# asked for (bump it in a commit of its own first), and neither the tag nor the
# release exists yet. Same shape as AlbumWall's scripts/release.sh.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
REPO=gsa700/deadwax
CSPROJ="$ROOT/src/Deadwax.App/Deadwax.App.csproj"
RIDS=(linux-x64)

VERSION=${1:?usage: release.sh X.Y.Z [--notes-file FILE] [--edge] [--publish] [--dry-run]}
shift
PUBLISH=0; NOTES=""; DRY=0; EDGE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --publish) PUBLISH=1 ;;
        --edge) EDGE=1 ;;
        --dry-run) DRY=1 ;;
        --notes-file) NOTES=${2:?--notes-file needs a file}; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

die() { echo "release: $*" >&2; exit 1; }
# A precondition that stops a real release and only warns in a dry run.
must() { if [ $DRY -eq 1 ]; then echo "release (dry run, would stop here): $*" >&2; else die "$*"; fi; }
say() { echo "== $*"; }

# ---- preconditions ----------------------------------------------------------
[[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "version must look like 0.2.0, not $VERSION (the updater never sees pre-releases)"
TAG="v$VERSION"
command -v gh >/dev/null || die "the GitHub CLI (gh) is needed"
command -v python3 >/dev/null || die "python3 is needed"
DOTNET=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
[ -x "$DOTNET" ] || die "dotnet is not on PATH and not in ~/.dotnet"
[ -n "$NOTES" ] && [ ! -f "$NOTES" ] && die "no such notes file: $NOTES"

cd "$ROOT"
[ -z "$(git status --porcelain)" ] || must "the working tree is not clean"
git fetch -q origin
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || must "HEAD is not origin/main: push first, release from what is pushed"
have=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -1)
[ "$have" = "$VERSION" ] || must "the csproj says <Version>$have</Version>; bump it to $VERSION in a commit first"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && must "tag $TAG already exists here"
git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1 && must "tag $TAG already exists on origin"
gh release view "$TAG" -R "$REPO" >/dev/null 2>&1 && must "release $TAG already exists"

# PRIVACY (see CLAUDE.md). The repository is public; a release is the moment its
# state is announced to everyone. Every author and committer in the history must
# be the GitHub no-reply address, and no tracked file may carry a private LAN
# address (four-part 10.x, 192.168.x, 172.16-31.x).
NOREPLY="298062495+gsa700@users.noreply.github.com"
bad=$(git log HEAD --format='%ae%n%ce' | sort -u | grep -vx "$NOREPLY" || true)
[ -z "$bad" ] || die "commits by an address other than the no-reply one: $bad (rewrite them before releasing; CLAUDE.md)"
PRIVATE='\b(10(\.[0-9]{1,3}){3}|192\.168(\.[0-9]{1,3}){2}|172\.(1[6-9]|2[0-9]|3[01])(\.[0-9]{1,3}){2})\b'
if git grep -qE "$PRIVATE" -- .; then
    git grep -nE "$PRIVATE" -- . | head -5 >&2
    die "a tracked file holds a private network address (above); use the machine's name instead"
fi

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# ---- build ------------------------------------------------------------------
for rid in "${RIDS[@]}"; do
    say "publish $rid"
    out="$WORK/pub/$rid"
    "$DOTNET" publish "$ROOT/src/Deadwax.App" -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out" -v quiet -nologo \
        | grep -E ' error |warning CS' || true
    exe=Deadwax
    [ -f "$out/$exe" ] || die "$rid: no $exe after publish"
    head -c 4 "$out/$exe" | od -An -tx1 | tr -d ' \n' | grep -q '^7f454c46' || die "$rid: $exe is not an ELF executable"
    [ "$(stat -c %s "$out/$exe")" -gt 50000000 ] || die "$rid: $exe is too small to be the single-file build"
    python3 - "$WORK/Deadwax-$rid.zip" "$out/$exe" <<'PY'
import sys, zipfile, os
out, exe = sys.argv[1:]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    info = zipfile.ZipInfo.from_file(exe, os.path.basename(exe))
    info.external_attr = 0o755 << 16          # executable when unpacked
    info.compress_type = zipfile.ZIP_DEFLATED
    with open(exe, "rb") as f: z.writestr(info, f.read())
PY
    echo "   Deadwax-$rid.zip  $(du -h "$WORK/Deadwax-$rid.zip" | cut -f1)"
done
(cd "$WORK" && sha256sum Deadwax-*.zip > SHA256SUMS)
cat "$WORK/SHA256SUMS"

if [ $DRY -eq 1 ]; then
    mkdir -p "$ROOT/dist"; rm -f "$ROOT"/dist/Deadwax-*.zip "$ROOT/dist/SHA256SUMS"
    cp "$WORK"/Deadwax-*.zip "$WORK/SHA256SUMS" "$ROOT/dist/"
    echo; echo "DRY RUN: nothing tagged, nothing released. The zip is in dist/."
    exit 0
fi

# ---- tag and release --------------------------------------------------------
say "tag $TAG"
git tag -a "$TAG" -m "Deadwax $VERSION"
git push -q origin "$TAG"

if [ -z "$NOTES" ]; then
    NOTES="$WORK/notes.md"
    prev=$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$TAG^" 2>/dev/null || true)
    {
        echo "Deadwax $VERSION, for Linux x86-64.$([ $EDGE -eq 1 ] && echo " An Edge build: offered to copies set to Edge until it is promoted to Stable.")"
        echo
        echo "Unpack the zip and run Deadwax. It needs libcdio, libcdio-paranoia, cdrdao and libFLAC from your distribution."
        echo "A running copy offers newer releases from its title bar and updates itself in place."
        echo
        echo "Changes${prev:+ since $prev}:"
        git log --no-merges --format='- %s' ${prev:+"$prev..$TAG"} | head -60
    } > "$NOTES"
fi

ARGS=(--repo "$REPO" --title "Deadwax $VERSION" --notes-file "$NOTES" --verify-tag)
[ $PUBLISH -eq 1 ] || ARGS+=(--draft)
[ $EDGE -eq 1 ] && ARGS+=(--prerelease --latest=false)
gh release create "$TAG" "${ARGS[@]}" "$WORK"/Deadwax-*.zip "$WORK/SHA256SUMS"
echo
if [ $PUBLISH -eq 1 ]; then
    if [ $EDGE -eq 1 ]; then
        echo "PUBLISHED on EDGE: copies set to Edge will be offered $VERSION. Promote to Stable later with:"
        echo "   gh release edit $TAG -R $REPO --prerelease=false --latest"
    else
        echo "PUBLISHED: every installed copy will be offered $VERSION."
    fi
else
    echo "DRAFT created. Nobody is offered it until it is published:"
    echo "   gh release edit $TAG -R $REPO --draft=false"
fi

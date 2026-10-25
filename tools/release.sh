#!/usr/bin/env bash
# Deadwax - cut a release: one GitHub release carrying the build for every platform in RIDS.
#
#   tools/release.sh 0.2.0                     a DRAFT release, to look at first
#   tools/release.sh 0.2.0 --notes-file N.md   with the notes written by hand
#   tools/release.sh 0.2.0 --publish           published at once (installed copies are offered it)
#   tools/release.sh 0.2.0 --edge --publish    an EDGE build: a GitHub pre-release, offered only to
#                                              copies set to Edge (Preferences > About). The usual way
#                                              every build goes out since 0.2.11.
#   PROMOTE to Stable, once it has run on Edge without trouble (his call, every time):
#                                              tools/promote.sh X.Y.Z   (flag cleared, latest, notes say so)
#   tools/release.sh 0.2.0 --dry-run           build and zip only: no tag, no release; the
#                                              version/tree checks only warn. Leaves the zip in ./dist.
#   tools/release.sh 0.2.0 --no-mac            without the Mac build, on a machine that is not a Mac
#
# RUN ON THE MAC (Control) since 0.7.0: the Mac build is made, signed and
# notarized there by tools/make-macos-app.sh.
#
# NATIVE AOT SINCE 0.8: every build is compiled ahead of time, and AOT cannot
# build for another system. So the Linux and Windows builds are the ones CI
# made on their own systems' runners (.github/workflows/build.yml, artifact
# Deadwax-aot-<rid>) from THIS commit: the run must have passed and be for
# HEAD exactly, and if there is none yet one is started and waited for. CI
# builds Linux on Ubuntu 22.04 so the program runs on glibc 2.35 and up. Each
# is checked (its files exactly, its program and libraries by their first
# bytes, and "X.Y.Z+<commit>" inside the program) before it is packed.
#
# WHAT A RELEASE IS, because the updater (src/Deadwax.App/UpdateService.cs) reads
# exactly this and nothing else: a GitHub release tagged vX.Y.Z, carrying
# Deadwax-<rid>-aot.zip for Linux and Windows (the program with its native
# libraries beside it, InstallLayout.NativeLibraries), Deadwax-osx-arm64.zip
# (Deadwax.app as a whole, signed and notarized), Deadwax.dmg (the Mac
# download for people) and SHA256SUMS listing them all. Until 0.7.5 Linux
# and Windows were Deadwax-<rid>.zip, one single file; copies before 0.7.6
# install only the program and do not look for the -aot name, so they are
# told there is no build for them rather than left with one that cannot start.
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
CSPROJ="$ROOT/Directory.Build.props"   # one <Version> for every project
# The platforms an app release carries. Each joined once a rip was proven on
# it: linux-x64 (Techbench), win-x64 (Hambench, 2026-10-13: natives-2 rip
# byte-identical to Linux), osx-arm64 in 0.7.0 (Control, 2026-10-24: the
# notarized bundle with library validation on, proven on a rip). The Native
# AOT builds of all three were proven on rips on 2026-10-25.
RIDS=(linux-x64 win-x64 osx-arm64)

# What a Native AOT build has beside its program, per system: the same list as
# src/Deadwax.App/Install/InstallLayout.cs NativeLibraries, which installs and
# updates them. A build with anything more or less beside its program is
# refused, so a library Avalonia adds is noticed here, not on someone's computer.
native_libraries() {
    case $1 in
        win-*) echo "libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll" ;;
        linux-*) echo "libSkiaSharp.so libHarfBuzzSharp.so" ;;
    esac
}

VERSION=${1:?usage: release.sh X.Y.Z [--notes-file FILE] [--edge] [--publish] [--dry-run] [--no-mac]}
shift
PUBLISH=0; NOTES=""; DRY=0; EDGE=0; MAC=1
while [ $# -gt 0 ]; do
    case "$1" in
        --publish) PUBLISH=1 ;;
        --edge) EDGE=1 ;;
        --dry-run) DRY=1 ;;
        --no-mac) MAC=0 ;;
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
if [ $MAC -eq 0 ]; then
    RIDS=(linux-x64 win-x64)
else
    [ "$(uname -s)" = Darwin ] || die "the Mac build is made on a Mac: run this on Control, or pass --no-mac"
fi

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

# ---- CI's run of this commit -------------------------------------------------
# The Linux and Windows builds come from it (above). Found by the commit, not
# by "the latest run": a run of any other commit is not this release's build.
# build.yml runs on a push touching the source or Directory.Build.props, so the
# version commit has one; if not, one is started here and waited for.
CI_RUN=""
ci_run_line() {
    gh run list -R "$REPO" --workflow build.yml --commit "$(git rev-parse HEAD)" -L 20 \
        --json databaseId,headSha,status,conclusion \
        -q '[.[] | select(.headSha=="'"$(git rev-parse HEAD)"'")][0] | select(. != null) | "\(.databaseId) \(.status)"'
}
ci_run() {
    [ -n "$CI_RUN" ] && return
    local line; line=$(ci_run_line)
    if [ -z "$line" ]; then
        say "no CI run of $(git rev-parse --short HEAD) yet: starting one"
        gh workflow run build.yml -R "$REPO" --ref main
        for _ in $(seq 1 36); do sleep 5; line=$(ci_run_line); [ -n "$line" ] && break; done
        [ -n "$line" ] || die "the CI run did not appear: https://github.com/$REPO/actions"
    fi
    CI_RUN=${line%% *}
    if [ "${line#* }" != completed ]; then
        say "waiting for CI run $CI_RUN (the Native AOT builds take about ten minutes)"
        gh run watch "$CI_RUN" -R "$REPO" --interval 30 >/dev/null 2>&1 || true
    fi
    local conclusion; conclusion=$(gh run view "$CI_RUN" -R "$REPO" --json conclusion -q .conclusion)
    [ "$conclusion" = success ] || die "CI run $CI_RUN did not pass ($conclusion): https://github.com/$REPO/actions/runs/$CI_RUN"
}
# ci_build RID DIR: that run's Deadwax-aot-RID, unpacked into DIR/x.
ci_build() {
    ci_run
    mkdir -p "$2"
    gh run download "$CI_RUN" -R "$REPO" -n "Deadwax-aot-$1" -D "$2" >/dev/null \
        || die "$1: CI run $CI_RUN has no artifact Deadwax-aot-$1 (kept 30 days)"
    unzip -q "$2/Deadwax-aot-$1.zip" -d "$2/x"
}

# ---- no native libraries ----------------------------------------------------
# None since 0.7.4: Deadwax reads the drive with its own code on every platform
# (macOS since 0.7.2, Linux 0.7.3, Windows 0.7.4), and nothing in the build
# picks up a native/ folder left in an older checkout.

# ---- build ------------------------------------------------------------------
for rid in "${RIDS[@]}"; do
    if [[ $rid == osx-* ]]; then
        # The Mac: tools/make-macos-app.sh builds Deadwax.app (its libraries
        # signed inside it), notarizes it and the dmg, and staples both; a dry
        # run skips Apple. The updater's zip holds the bundle whole, made with
        # ditto so the signature and the stapled ticket survive.
        say "Deadwax.app and Deadwax.dmg ($rid)"
        macflags=(--dmg); [ $DRY -eq 1 ] || macflags+=(--notarize)
        PATH="$(dirname "$DOTNET"):$PATH" "$HERE/make-macos-app.sh" "${macflags[@]}" > "$WORK/make-macos-app.log" 2>&1 \
            || { tail -40 "$WORK/make-macos-app.log" >&2; die "$rid: make-macos-app.sh failed (above)"; }
        [ -d "$ROOT/dist/Deadwax.app" ] && [ -f "$ROOT/dist/Deadwax.dmg" ] || die "$rid: make-macos-app.sh made no app or dmg"
        [ "$(defaults read "$ROOT/dist/Deadwax.app/Contents/Info" CFBundleShortVersionString)" = "$VERSION" ] \
            || die "$rid: the app's CFBundleShortVersionString is not $VERSION"
        codesign --verify --deep --strict "$ROOT/dist/Deadwax.app" || die "$rid: the app's signature does not verify"
        ditto -c -k --keepParent "$ROOT/dist/Deadwax.app" "$WORK/Deadwax-$rid.zip"
        cp "$ROOT/dist/Deadwax.dmg" "$WORK/Deadwax.dmg"
        echo "   Deadwax-$rid.zip  $(du -h "$WORK/Deadwax-$rid.zip" | cut -f1)    Deadwax.dmg  $(du -h "$WORK/Deadwax.dmg" | cut -f1)"
        continue
    fi
    say "$rid: the Native AOT build CI made of $(git rev-parse --short HEAD)"
    ci_build "$rid" "$WORK/ci/$rid"
    from="$WORK/ci/$rid/x"
    # The program's name and first bytes per platform, as UpdateService checks
    # them before installing an update: ELF or PE ("MZ"); the libraries too.
    case $rid in
        win-*) exe=Deadwax.exe; magic='^4d5a'; kind=PE ;;
        *)     exe=Deadwax; magic='^7f454c46'; kind=ELF ;;
    esac
    libs=$(native_libraries "$rid")
    want=$(printf '%s\n' $exe $libs | sort | paste -sd' ' -)
    have=$(cd "$from" && find . -maxdepth 1 -type f ! -name '*.pdb' ! -name '*.dbg' | sed 's:^\./::' | sort | paste -sd' ' -)
    [ "$have" = "$want" ] || die "$rid: CI's build holds [$have], expected [$want] (a library added or gone? update native_libraries here and InstallLayout.NativeLibraries)"
    for f in $exe $libs; do
        head -c 4 "$from/$f" | od -An -tx1 | tr -d ' \n' | grep -qE "$magic" || die "$rid: $f is not a $kind binary"
    done
    # Built from this commit as this version: the informational version
    # (X.Y.Z+<commit>) is in the program as plain text.
    grep -qaF "$VERSION+$(git rev-parse HEAD)" "$from/$exe" || die "$rid: $exe does not say it is $VERSION+$(git rev-parse HEAD)"
    # The program, its libraries, and beside them its license, the third-party
    # notices and the license texts (tools/fetch-licenses.py): what the
    # licenses ask to travel with it. UpdateService takes the program and the
    # libraries out of the zip.
    [ -f "$ROOT/licenses/README.md" ] || die "licenses/ is missing: run tools/fetch-licenses.py"
    python3 - "$WORK/Deadwax-$rid-aot.zip" "$from" "$ROOT" $exe $libs <<'PY'
import sys, zipfile, os
out, src, root, *files = sys.argv[1:]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for name in files:                          # the program first, then its libraries
        info = zipfile.ZipInfo.from_file(os.path.join(src, name), name)
        info.external_attr = 0o755 << 16        # executable when unpacked
        info.compress_type = zipfile.ZIP_DEFLATED
        with open(os.path.join(src, name), "rb") as f: z.writestr(info, f.read())
    for name in ["LICENSE", "THIRD-PARTY-NOTICES.md"]:
        z.write(os.path.join(root, name), name)
    for name in sorted(os.listdir(os.path.join(root, "licenses"))):
        z.write(os.path.join(root, "licenses", name), "licenses/" + name)
PY
    echo "   Deadwax-$rid-aot.zip  $(du -h "$WORK/Deadwax-$rid-aot.zip" | cut -f1)"
done
(cd "$WORK" && sha256sum Deadwax-*.zip $([ -f "$WORK/Deadwax.dmg" ] && echo Deadwax.dmg) > SHA256SUMS)
cat "$WORK/SHA256SUMS"

if [ $DRY -eq 1 ]; then
    mkdir -p "$ROOT/dist"; rm -f "$ROOT"/dist/Deadwax-*.zip "$ROOT/dist/SHA256SUMS"
    cp "$WORK"/Deadwax-*.zip "$WORK/SHA256SUMS" "$ROOT/dist/"   # the dmg is there already
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
        platforms=$(for rid in "${RIDS[@]}"; do case $rid in (linux-x64) echo "Linux x86-64";; (win-x64) echo "Windows x64";; (osx-arm64) echo "macOS (Apple silicon)";; (*) echo "$rid";; esac; done | paste -sd, - | sed 's/,/, /g')
        echo "Deadwax $VERSION, for $platforms.$([ $EDGE -eq 1 ] && echo " An Edge build: offered to copies set to Edge until it is promoted to Stable.")"
        echo
        if printf '%s\n' "${RIDS[@]}" | grep -q '^osx-'; then
            echo "Mac: open Deadwax.dmg and drag Deadwax to Applications. Signed with a Developer ID and notarized by Apple, so it opens with no warning."
            echo
            echo "Linux and Windows: unpack the zip for your system into a folder and run Deadwax; the library files beside it are part of it."
        else
            echo "Unpack the zip into a folder and run Deadwax; the library files beside it are part of it."
        fi
        echo "Nothing else to install: Deadwax reads the drive with its own code."
        echo "A running copy offers newer releases from its title bar and updates itself in place."
        echo "On Linux and Windows a copy older than 0.7.6 cannot install this build itself, and says there is no build for it: download it here."
        if printf '%s\n' "${RIDS[@]}" | grep -q '^win-'; then
            echo
            echo "Windows: Deadwax.exe is not signed, so the first run may show \"Windows protected your PC\": More info, then Run anyway."
        fi
        echo
        echo "Changes${prev:+ since $prev}:"
        git log --no-merges --format='- %s' ${prev:+"$prev..$TAG"} | head -60
    } > "$NOTES"
fi

ARGS=(--repo "$REPO" --title "Deadwax $VERSION" --notes-file "$NOTES" --verify-tag)
[ $PUBLISH -eq 1 ] || ARGS+=(--draft)
[ $EDGE -eq 1 ] && ARGS+=(--prerelease --latest=false)
gh release create "$TAG" "${ARGS[@]}" "$WORK"/Deadwax-*.zip $([ -f "$WORK/Deadwax.dmg" ] && echo "$WORK/Deadwax.dmg") "$WORK/SHA256SUMS"
echo
if [ $PUBLISH -eq 1 ]; then
    if [ $EDGE -eq 1 ]; then
        echo "PUBLISHED on EDGE: copies set to Edge will be offered $VERSION. Promote to Stable later with:"
        echo "   tools/promote.sh $VERSION"
    else
        echo "PUBLISHED: every installed copy will be offered $VERSION."
    fi
else
    echo "DRAFT created. Nobody is offered it until it is published:"
    echo "   gh release edit $TAG -R $REPO --draft=false"
fi

#!/usr/bin/env python3
# Deadwax - fetch the license TEXTS of everything a release carries, verbatim.
#
#   python3 tools/fetch-licenses.py
#
# THIRD-PARTY-NOTICES.md names every bundled component and its license. A release
# also has to carry the license texts themselves, and a license text is not
# something to type from memory or paste from wherever: it has to be the file
# the project itself ships, byte for byte, at the version that was built. This
# fetches each one from the project's own repository into licenses/, and writes
# licenses/README.md saying where every file came from, at which ref, and its
# SHA-256, so that a reader can check any of them against upstream.
# release.sh puts LICENSE, THIRD-PARTY-NOTICES.md and licenses/ in every zip.
#
# The native libraries' versions are READ OUT OF tools/build-natives/pins.sh, not
# repeated here: the pins are the one place those versions live. Run this again
# whenever a pin moves, and look at the diff. AlbumWall's tools/fetch-licenses.py
# is the model; the two stay alike.
#
# It refuses to write a file that does not look like a license - an HTML error
# page saved as COPYING is exactly the failure this kind of script has.

import hashlib
import io
import os
import re
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "licenses")
PINS = os.path.join(ROOT, "tools", "build-natives", "pins.sh")
CORE_MATH = "29f5112"   # the upstream commit the flac patch's crmath_*.c come from


def pins():
    text = io.open(PINS, encoding="utf-8").read()
    found = dict(re.findall(r"^([A-Z_]+)=([^\s#]+)\s*$", text, flags=re.M))
    wanted = ["LIBCDIO_VERSION", "PARANOIA_VERSION", "FLAC_VERSION"]
    missing = [w for w in wanted if w not in found]
    if missing:
        sys.exit("could not read these pins from pins.sh: " + ", ".join(missing))
    return found


P = pins()

GITHUB = "https://raw.githubusercontent.com/{repo}/{ref}/{path}"
INRIA = "https://gitlab.inria.fr/{repo}/-/raw/{ref}/{path}"

# (file to write, what it covers, host, repository, refs to try in order, paths to try in order)
# A ref of None means the repository's default branch, used only for components
# whose license does not change with the version and whose tags are not
# predictable from a package version. Which ref was used is recorded either way.
WANTED = [
    # ---- reading the disc and writing FLAC: exactly the pins Deadwax builds
    ("libcdio-COPYING.txt", "libcdio (GPL v3 or later)", GITHUB, "libcdio/libcdio", [P["LIBCDIO_VERSION"]], ["COPYING"]),
    ("libcdio-paranoia-COPYING.txt", "libcdio-paranoia (GPL v3 or later)", GITHUB, "libcdio/libcdio-paranoia",
     ["release-" + P["PARANOIA_VERSION"]], ["COPYING"]),
    ("flac-COPYING.Xiph.txt", "libFLAC (BSD 3-Clause)", GITHUB, "xiph/flac", [P["FLAC_VERSION"]], ["COPYING.Xiph"]),
    ("core-math-LICENSE.txt", "CORE-MATH, cosf and log (MIT)", INRIA, "core-math/core-math", [CORE_MATH], ["LICENSE"]),
    ("cdrdao-COPYING.txt", "cdrdao, the CD-Text and Q-subchannel decoders ported from it (GPL v2 or later)", GITHUB,
     "cdrdao/cdrdao", [None], ["COPYING"]),

    # ---- the application
    ("dotnet-runtime-LICENSE.TXT", ".NET runtime and System.IO.Hashing (MIT)", GITHUB, "dotnet/runtime", ["v10.0.0", None], ["LICENSE.TXT"]),
    ("avalonia-licence.md", "Avalonia (MIT)", GITHUB, "AvaloniaUI/Avalonia", ["12.1.2", None], ["licence.md", "LICENSE.md", "LICENSE"]),
    ("skiasharp-LICENSE.md", "SkiaSharp and HarfBuzzSharp (MIT)", GITHUB, "mono/SkiaSharp", [None], ["LICENSE.md", "LICENSE.txt", "LICENSE"]),
    ("skia-LICENSE.txt", "Skia (BSD 3-Clause)", GITHUB, "google/skia", [None], ["LICENSE"]),
    ("microcom-LICENSE.txt", "MicroCom (MIT)", GITHUB, "kekekeks/MicroCom", [None], ["LICENSE", "LICENSE.md", "LICENSE.txt", "licence.md"]),
    ("nwayland-licence.md", "NWayland (MIT)", GITHUB, "kekekeks/NWayland", [None], ["licence.md", "LICENSE", "LICENSE.md"]),
    ("tmds-dbus-COPYING.txt", "Tmds.DBus.Protocol (MIT)", GITHUB, "tmds/Tmds.DBus", [None], ["COPYING", "LICENSE"]),
    ("taglib-sharp-COPYING.txt", "TagLib# (LGPL v2.1), until it leaves", GITHUB, "mono/taglib-sharp", [None], ["COPYING"]),
    ("inter-LICENSE.txt", "Inter, the typeface (SIL OFL 1.1)", GITHUB, "rsms/inter", [None], ["LICENSE.txt", "LICENSE"]),
]

# Something every real license in the list above contains at least one of.
LOOKS_LIKE = re.compile(
    r"licen[sc]e|permission is hereby granted|redistribution and use|copyright|warranty", re.I)


def fetch(host, repo, ref, path):
    url = host.format(repo=repo, ref=ref or "HEAD", path=path)
    request = urllib.request.Request(url, headers={"User-Agent": "deadwax-fetch-licenses"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return url, response.read()
    except urllib.error.HTTPError:
        return url, None


def acceptable(data):
    if data is None or len(data) < 200:
        return False
    head = data[:300].lstrip().lower()
    if head.startswith(b"<!doctype") or head.startswith(b"<html"):
        return False
    return bool(LOOKS_LIKE.search(data.decode("utf-8", errors="replace")))


def main():
    os.makedirs(OUT, exist_ok=True)
    rows, failed = [], []

    for name, covers, host, repo, refs, paths in WANTED:
        got = None
        for ref in refs:
            for path in paths:
                url, data = fetch(host, repo, ref, path)
                if acceptable(data):
                    got = (url, ref, path, data)
                    break
            if got:
                break

        if not got:
            failed.append("%s (%s)" % (name, repo))
            print("FAILED  %-32s %s" % (name, repo))
            continue

        url, ref, path, data = got
        with open(os.path.join(OUT, name), "wb") as f:      # bytes in, bytes out: verbatim
            f.write(data)
        digest = hashlib.sha256(data).hexdigest()
        site = "https://gitlab.inria.fr/" if host == INRIA else "https://github.com/"
        rows.append((name, covers, site + repo, ref or "default branch", path, digest))
        print("ok      %-32s %s @ %s" % (name, repo, ref or "default branch"))

    with io.open(os.path.join(OUT, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# The license texts\n\n")
        f.write("Every file here is the license file its project ships, byte for byte, fetched from\n")
        f.write("the project's own repository by `tools/fetch-licenses.py`. Nothing in this folder\n")
        f.write("was typed or edited. What each component is and what it does in Deadwax is in\n")
        f.write("[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md); Deadwax's own license is\n")
        f.write("[LICENSE](../LICENSE), the GNU GPL version 3.\n\n")
        f.write("The native libraries' files come from exactly the versions pinned in\n")
        f.write("`tools/build-natives/pins.sh`. Where a ref says *default branch*, the project's\n")
        f.write("tags could not be predicted from a package version and its license does not vary\n")
        f.write("with it; the SHA-256 says exactly which text this is.\n\n")
        f.write("| file | covers | from | ref | path | SHA-256 |\n|---|---|---|---|---|---|\n")
        for name, covers, site, ref, path, digest in rows:
            f.write("| [%s](%s) | %s | %s | `%s` | `%s` | `%s` |\n" % (name, name, covers, site, ref, path, digest))

    print("\n%d written to licenses/, %d failed" % (len(rows), len(failed)))
    if failed:
        print("not fetched: " + "; ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

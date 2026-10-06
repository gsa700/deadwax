# Working in this repository

This repository is PUBLIC. Everything committed and pushed here is published:
it can be copied, and a force-push does not fully take it back (GitHub keeps
old commits reachable by id). The rules are AlbumWall's, which were written
after they were broken once on the day that repository went public.

## Privacy: before every commit and every push

1. **Author email is the GitHub no-reply address**, never a personal one. In
   every clone, on every machine, before the first commit:

       git config user.email "298062495+gsa700@users.noreply.github.com"
       git config user.name  "David Erickson"

   Check with `git log -1 --format='%ae %ce'` after committing.
2. **No private network details in any file or commit message**: no LAN IP
   addresses (10.x, 192.168.x, 172.16-31.x), no share paths built on them.
   Use the machine's NAME instead (Techbench, Hambench, NASBOX are fine).
3. **No personal email, keys, tokens or passwords** anywhere, including
   test notes and the spec.
4. **Scan before pushing**:

       git log origin/main..HEAD --format='%ae%n%ce' | sort -u      # only the no-reply address
       git diff origin/main..HEAD | grep -nE '\b(10(\.[0-9]{1,3}){3}|192\.168(\.[0-9]{1,3}){2}|172\.(1[6-9]|2[0-9]|3[01])(\.[0-9]{1,3}){2})\b'

   Anything found: fix it in the commit BEFORE pushing. Once pushed, it is public.
5. **Never push old history.** The private original is `gsa700/deadwax-private`
   and stays separate; its commits carry a personal address. If `git push` is
   rejected as non-fast-forward, do not force it: fetch, reset onto
   `origin/main`, then ask.

`tools/release.sh` refuses to cut a release if any commit author is not the
no-reply address or the tree contains a private address, but that only
catches it at release time; the rules above are for every push.

## Releases

- `tools/release.sh X.Y.Z` makes a DRAFT (`--publish` to publish, `--dry-run`
  to build only). Bump `<Version>` in `src/Deadwax.App/Deadwax.App.csproj` in
  its own commit first.
- Publishing offers the release to every installed copy through the in-app
  updater. It is his call, every time, and he tries the Release build first.
- `tools/install.sh` builds and installs the app-menu copy from source; the
  installed copy then updates itself from releases.

## Drive rules

- Never read from the CD drive (`/dev/sr0`, `/dev/cdrom`) without asking: a
  rip may be running in the window, and an idle check a moment earlier is not
  enough.
- Never `timeout`-kill a read on the BDR-209D: it triggers USB resets and
  leaves the next command blocked. Power-cycle the enclosure instead.

## More

The design record is `~/Documents/Projects/Deadwax/deadwax-spec.md` (outside
the repository) and the commit messages.

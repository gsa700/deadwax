# Deadwax

A CD ripper for a FLAC library you own, and the companion to
[AlbumWall](https://github.com/gsa700/albumwall). It reads the disc itself
through libcdio-paranoia, verifies every rip against AccurateRip, and files the
album into the library the way the library wants it.

It is in daily use on the author's library since 2026-09-24: every disc
is read twice until two passes agree, checked against AccurateRip, encoded with
libFLAC, tagged from MusicBrainz and written with `.cue`, `.m3u`, `.toc` and a
rip log in whipper's layout, so a library ripped with whipper and one ripped
with Deadwax look the same. The window shows the disc, its MusicBrainz
releases (with a barcode match against the disc's catalog number), the rip as
it happens with a read map of the disc surface, and the post-rip filing steps.

Linux only for now; the drive layer is libcdio-paranoia and cdrdao. The design
is in a spec kept outside the repository, and the build went through its
validation gates in order, each checked against the 309 whipper 0.10.0 rips
already in the library.

## Download

Each [release](https://github.com/gsa700/deadwax/releases) carries
`Deadwax-linux-x64.zip`: one self-contained program, no .NET needed. Unpack it
anywhere and run `Deadwax`. It needs these from your distribution: `libcdio`,
`libcdio-paranoia`, `cdrdao` and `flac` (libFLAC). On Fedora:

```
sudo dnf install libcdio libcdio-paranoia cdrdao flac-libs
```

A running copy checks GitHub for a newer release a few seconds after launch
and, if there is one, offers it in the title bar. The update is verified against
the release's `SHA256SUMS` before it is installed, and Deadwax restarts into it.

There are two channels, chosen in Preferences > About. **Edge** gets every build
as it is released (published as GitHub pre-releases). **Stable**, the default,
gets a build only once it has run on Edge without trouble and been promoted:
the same binary, with its pre-release flag cleared.

Every drive reads audio a few samples early or late, always by the same
amount: its read offset. The first time Deadwax meets a drive it asks to
measure it, once, from a well-known commercial CD checked against AccurateRip
(sometimes it needs a second CD). The result is remembered in
`~/.config/deadwax/drives.json`, one entry per drive model and firmware, for
example `{"PIONEER BD-RW BDR-209D 1.10": 667}`, and recalled whenever that drive
is plugged in. Several external drives each keep their own entry; a drive
carried to another computer is measured once there too. Preferences shows the
drive and its offset, with or without a disc in, and every drive remembered.

For a disc that vibrates in the drive, choose a **Drive speed** on the Disc
screen (16x down to 1x; try 4x first). The cap holds for the scan and the rip,
is remembered for the next disc, and is recorded in the log.

## After the rip

When every track read the same twice, Deadwax finishes the album for the
library (Preferences, Library tab, **After a rip**; on by default). Nothing
else needs to be installed for this.

- **Front cover.** `cover.jpg` is the Cover Art Archive's 500-pixel front for
  the release, or for its release group when the release has none. It is
  fetched before the rip starts; if the archive is down, the rip goes ahead
  without it.
- **Back cover** (Preferences > Filing, on by default). It is the back of
  the CD case, usually the track list as printed: AlbumWall turns the sleeve
  over to show it when you open an album. `back.jpg` is the release's own back when the archive has
  one, otherwise the back of another edition of the same album: the same
  country first, then the nearest year. An image filed as the front is never
  taken for the back, even when it is flagged as both. Every back written is
  recorded, with where it came from, in `~/.local/state/deadwax/back-covers.tsv`,
  and one from another edition says so in the log.
- **A disc from a set.** For a release of several discs, the Disc screen asks
  once per set, and remembers the answer for the set's other discs:
  - *Keep the set together, as it was sold*: one album in the library, the way
    a career compilation or a live box should be.
  - *File this disc as the album it originally was*: for "Original Album
    Classics" and other boxes of complete albums. The disc is retagged with
    that album's own title, first-release year and front cover, its folder is
    renamed for that album in your chosen folder style, and the box's back is
    replaced. This happens only
    when the disc's title is found in MusicBrainz as an album or EP by that
    artist; a disc called "Bonus Tracks" or "Dawn to Dusk" stays with its set.
    The old tags and art are kept in `~/.local/state/deadwax/unbox-backup/`
    first, and an existing folder is never written over.
- **Filed correctly.** The new album is checked against your Filing choices:
  every track tagged and agreeing on album, artist, year and disc, track
  numbers 1 to N, file names in the chosen style, the `.cue` and `.m3u`
  pointing at files that exist, and the cover embedded when asked. Deadwax
  checks only the album it has just made; keeping a whole library in order
  over the years is a job for a separate tool.
- **Two editions of one set.** Before a disc of a set is ripped, Deadwax looks
  for the set's other discs in the library. If one came from a different
  edition, the Disc screen says so and offers to use the same one: most
  players show a set's discs from two editions as two separate albums.

When the archive was down at rip time, `deadwax art ALBUM_DIR` fetches the
missing covers later (`--replace` fetches both again).

## Filing: how the library is laid out

Preferences > Filing. The defaults are shown first; anything you change
applies to new rips only, and albums already in the library are never renamed.

| Choice | Options |
|---|---|
| Album folder | `Artist/1976 - Album` (default), `Artist/Album (1976)`, `Artist/Album`, `Artist - Album` (one level) |
| Track files | `Artist - 01 - Title.flac` (default), `01 - Title.flac`, `01 Title.flac` |
| Embed the front cover in each file | off for an existing setup, on for a fresh install: phones and car stereos show only embedded art |
| Back cover as `back.jpg` | on |
| `.cue` sheet, `.m3u` playlist | on |

The front cover is always saved as `cover.jpg`, and the rip log (`.log`) and
table of contents (`.toc`) are always written: they are the record that the
rip was verified, and how Deadwax knows a disc is already in your library.

## Files outside the library

| Path | What |
|---|---|
| `~/.config/deadwax/settings.json` | the window's settings, including each set's choice |
| `~/.config/deadwax/drives.json` | drive read offsets |
| `~/.config/deadwax/discs/` | discs described by hand when MusicBrainz has no match |
| `~/.cache/deadwax/musicbrainz/` | MusicBrainz release data (safe to delete) |
| `~/.local/state/deadwax/back-covers.tsv` | where each `back.jpg` came from |
| `~/.local/state/deadwax/unbox-backup/` | tags and art of set discs before they were refiled |

## Status

| Gate | What it proves | State |
|---|---|---|
| G1 · IDs | TOC, MusicBrainz, CDDB and AccurateRip disc IDs, catalog and ISRCs equal whipper's | Offline: all 309 logs and cdrdao TOC files match. Drive: 52nd Street, Turnstiles, Anthology of Bread, Shout at the Devil PASS |
| G2 · Audio | Same audio as whipper, sample for sample | Offline: CRC of all 3,529 tracks in the library matches. Drive: Turnstiles (8 tracks) and Anthology of Bread (20, gap before track 1) read twice, identical, same CRC and FLAC MD5 as whipper |
| G3 · AccurateRip | Same v1/v2 checksums as whipper | Offline: all 1,617 v1 and 3,376 v2 checksums in 309 logs match. Drive: Anthology of Bread, all 20 tracks accurate (79–87 matching rips) and equal to whipper's |
| G4 · Output | Same tags and sidecar files, `music-audit` clean | Tags: 177 of 199 non-box albums identical (the rest is MusicBrainz drift). FLAC: byte-identical frames to whipper's. .m3u: 308 of 309 identical (the other was hand-edited, and is wrong). .cue: structure identical on all 306 comparable. Log: written and verifiable. `deadwax rip` works end to end |
| G5 · Side by side | 10 discs through both tools | **PASS, 10 of 10.** Seven identical on every check, art included; the rest differ only by the deliberate `"`→`'` file name, one composer MusicBrainz added after the rip, and his hand-renamed folder. Two naming rules (compilation year, disambiguation) found and fixed on the way |

## Install

`tools/install.sh` builds a self-contained Deadwax into `~/.local/share/deadwax/`
and adds it to the app menu with its icon; rerun it after changes, and
`tools/install.sh --remove` takes it out.

## Build and run

Needs the .NET 10 SDK, libcdio (`libcdio.so.19`; the -devel package is not
needed) and cdrdao.

```
dotnet build
dotnet test
dotnet src/Deadwax.Cli/bin/Debug/net10.0/deadwax.dll check-logs ~/Music --online 8
dotnet src/Deadwax.Cli/bin/Debug/net10.0/deadwax.dll check-audio ~/Music
dotnet src/Deadwax.Cli/bin/Debug/net10.0/deadwax.dll scan --full --online --against "~/Music/Billy Joel/1978 - 52nd Street"
dotnet src/Deadwax.Cli/bin/Debug/net10.0/deadwax.dll read --against "~/Music/Billy Joel/1976 - Turnstiles"
```

`check-logs` rebuilds the TOC from every whipper log under a folder and checks
the IDs Deadwax computes against the ones whipper recorded. `check-audio` decodes
whipper's FLACs (with the `flac` tool) and checks the copy CRC and AccurateRip
v1/v2 checksums of that audio against the log: G2 and G3 without the drive. `scan` reads the disc
in the drive; with `--against` it compares the TOC and IDs to that album's
whipper log. `--full` adds a `cdrdao read-toc` pass (about two minutes) for the
catalog number, ISRCs, CD-Text and pregaps, and compares those with the cue.
`rip` makes a complete album; with `--post-rip` it then does the steps under
[After the rip](#after-the-rip) (a set's disc is refiled only with
`--unbox`), and `post-rip DIR` does them for an album already ripped. `art DIR`
fetches a missing `cover.jpg` and `back.jpg`. `compare` checks a
Deadwax rip against whipper's rip of the same disc. `read` reads tracks securely through paranoia, twice, at the drive's read offset
(from `drives.json`, or `--offset`), and with `--against` checks each track's audio
against whipper's logged CRC and the MD5 stored in the existing FLAC. It writes
nothing.

## Layout

```
src/Deadwax.Drive      libcdio via P/Invoke (TOC, drive identity); cdrdao TOC files
                       (catalog, ISRCs, CD-Text, pregaps); libcdio-paranoia secure
                       reads with offset correction
src/Deadwax.Metadata   disc IDs; MusicBrainz client, tags and library conventions;
                       front and back covers from the Cover Art Archive
src/Deadwax.Verify     AccurateRip IDs, database and checksums; CRC32/MD5 audio
                       checks; whipper log, cue and config readers
src/Deadwax.Output     libFLAC encoding, tags, file names, .cue/.m3u, the rip log
src/Deadwax.Core       the rip session and the steps after it (set discs refiled,
                       back cover), shared by the command line and the window
src/Deadwax.Cli        the deadwax command
src/Deadwax.App        the window (Avalonia, native Wayland) and its updater
tests/Deadwax.Tests    golden files from real rips
```

## Licence

GPLv3. libcdio and libcdio-paranoia are GPLv3 themselves.

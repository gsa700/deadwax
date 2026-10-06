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

The drive's read offset is taken from `~/.config/whipper/whipper.conf` if you
have one, otherwise from `~/.config/deadwax/drives.json`, a map of the drive's
name to its offset in samples, for example `{"PIONEER BD-RW BDR-209D 1.10": 667}`.
The number for your drive is in the [AccurateRip list](https://www.accuraterip.com/driveoffsets.htm).

For a disc that vibrates in the drive, tick **Slow spin** on the Disc screen: the
drive is capped at 4x (changeable in `~/.config/deadwax/settings.json`) for
the scan and the rip, and the cap is recorded in the log.

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
`rip` makes a complete album; with `--post-rip` it then runs the library
tools (`music-unbox` with `--unbox`, `music-backart`, `music-audit`), and
`post-rip DIR` does that for an album already ripped. `compare` checks a
Deadwax rip against whipper's rip of the same disc. `read` reads tracks securely through paranoia, twice, at the drive's read offset
(taken from whipper.conf), and with `--against` checks each track's audio
against whipper's logged CRC and the MD5 stored in the existing FLAC. It writes
nothing.

## Layout

```
src/Deadwax.Drive      libcdio via P/Invoke (TOC, drive identity); cdrdao TOC files
                       (catalog, ISRCs, CD-Text, pregaps); libcdio-paranoia secure
                       reads with offset correction
src/Deadwax.Metadata   disc IDs; MusicBrainz client, tags and library conventions
src/Deadwax.Verify     AccurateRip IDs, database and checksums; CRC32/MD5 audio
                       checks; whipper log, cue and config readers
src/Deadwax.Output     libFLAC encoding, tags, file names, .cue/.m3u, the rip log
src/Deadwax.Core       the rip session, shared by the command line and the window
src/Deadwax.Cli        the deadwax command
tests/Deadwax.Tests    golden files from real rips
```

The App project arrives with the window.

## Licence

GPLv3. libcdio and libcdio-paranoia are GPLv3 themselves.

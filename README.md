# Deadwax

A CD ripper for a FLAC library you own, and the companion to
[AlbumWall](https://github.com/gsa700/albumwall). It reads the disc itself
through libcdio-paranoia, verifies every rip against AccurateRip, and files the
album into the library the way the library wants it.

Early days: nothing rips yet. The design is in the spec (kept outside the repo,
`~/Documents/Projects/Deadwax/deadwax-spec.md`), and the build is going through
the spec's validation gates in order, each checked against the 309 whipper
0.10.0 rips already in the library.

## Status

| Gate | What it proves | State |
|---|---|---|
| G1 · IDs | TOC, MusicBrainz, CDDB and AccurateRip disc IDs, catalog and ISRCs equal whipper's | Offline: all 309 logs and cdrdao TOC files match. Drive: 52nd Street, Turnstiles, Anthology of Bread, Shout at the Devil PASS |
| G2 · Audio | Same audio as whipper, sample for sample | Offline: CRC of all 3,529 tracks in the library matches. Drive: Turnstiles (8 tracks) and Anthology of Bread (20, gap before track 1) read twice, identical, same CRC and FLAC MD5 as whipper |
| G3 · AccurateRip | Same v1/v2 checksums as whipper | Offline: all 1,617 v1 and 3,376 v2 checksums in 309 logs match. Drive: Anthology of Bread, all 20 tracks accurate (79–87 matching rips) and equal to whipper's |
| G4 · Output | Same tags and sidecar files, `music-audit` clean | Not started |
| G5 · Side by side | 10 discs through both tools | Not started |

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
`read` reads tracks securely through paranoia, twice, at the drive's read offset
(taken from whipper.conf), and with `--against` checks each track's audio
against whipper's logged CRC and the MD5 stored in the existing FLAC. It writes
nothing.

## Layout

```
src/Deadwax.Drive      libcdio via P/Invoke (TOC, drive identity); cdrdao TOC files
                       (catalog, ISRCs, CD-Text, pregaps); libcdio-paranoia secure
                       reads with offset correction
src/Deadwax.Metadata   MusicBrainz and CDDB disc IDs
src/Deadwax.Verify     AccurateRip IDs and database; CRC32/MD5 audio checks; whipper
                       log, cue and config readers
src/Deadwax.Cli        the deadwax command
tests/Deadwax.Tests    golden files from real rips
```

The spec's Core, Output and App projects arrive with the gates that need them.

## Licence

GPLv3. libcdio and libcdio-paranoia are GPLv3 themselves.

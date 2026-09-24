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
| G1 · IDs | TOC, MusicBrainz, CDDB and AccurateRip disc IDs equal whipper's | Offline half done: all 309 logs match, AccurateRip IDs confirmed against the live database. Drive half needs discs in the drive |
| G2 · Audio | Same audio as whipper, sample for sample | Not started |
| G3 · AccurateRip | Same v1/v2 checksums as whipper | Not started |
| G4 · Output | Same tags and sidecar files, `music-audit` clean | Not started |
| G5 · Side by side | 10 discs through both tools | Not started |

## Build and run

Needs the .NET 10 SDK and libcdio (`libcdio.so.19`; the -devel package is not
needed).

```
dotnet build
dotnet test
dotnet src/Deadwax.Cli/bin/Debug/net10.0/deadwax.dll check-logs ~/Music --online 8
dotnet src/Deadwax.Cli/bin/Debug/net10.0/deadwax.dll scan --isrc --online --against "~/Music/Billy Joel/1978 - 52nd Street"
```

`check-logs` rebuilds the TOC from every whipper log under a folder and checks
the IDs Deadwax computes against the ones whipper recorded. `scan` reads the disc
in the drive; with `--against` it compares the TOC, the IDs, the catalog number
and (with `--isrc`) the ISRCs to that album's whipper log and cue sheet.

## Layout

```
src/Deadwax.Drive      libcdio via P/Invoke: TOC, drive identity, catalog, ISRCs
src/Deadwax.Metadata   MusicBrainz and CDDB disc IDs
src/Deadwax.Verify     AccurateRip IDs and database, whipper log reader
src/Deadwax.Cli        the deadwax command
tests/Deadwax.Tests    golden files from real rips
```

The spec's Core, Output and App projects arrive with the gates that need them.

## Licence

GPLv3. libcdio and libcdio-paranoia are GPLv3 themselves.

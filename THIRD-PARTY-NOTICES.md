# What's inside Deadwax

Deadwax is free software under the GNU General Public License, version 3 or
later. A release is a single file, and that file carries other people's work
inside it: the libraries that talk to the drive and read the audio, the FLAC
encoder, the toolkit the window is drawn with, a font. This page says what each
one is, what it does here, the license it comes under, and where its source is.

## Reading the disc and writing FLAC

These three are C libraries that Deadwax builds itself from pinned source
(`tools/build-natives/pins.sh`, each archive checked against its SHA-256), so
that the same ones ship on every platform. Each `natives-N` release on GitHub
carries the libraries and, beside them, the exact source archives they were
built from. Unlike the rest of this page, they are **patched**: the changes are
in `tools/build-natives/patches/`, and its README says what each one fixes.

- **libcdio 2.3.0**: opens the drive and reads the disc's table of contents,
  and carries the raw commands Deadwax uses to read ISRCs, the catalog number,
  CD-Text and pregaps.
  License: GNU GPL, version 3 or later.
  Source: https://github.com/libcdio/libcdio (release `2.3.0`)
  Patched: three range checks on the table of contents a drive returns (Linux,
  Windows, macOS), and a use-after-free when a drive is closed on macOS, found
  in the security review of 2026-10-12.

- **libcdio-paranoia 10.2+2.0.2**: the careful read. Every sector of a track
  comes through it, with jitter correction and re-reads of what does not
  verify.
  License: GNU GPL, version 3 or later.
  Source: https://ftp.gnu.org/gnu/libcdio/ (`libcdio-paranoia-10.2+2.0.2`)
  Patched: a buffer sized in bytes for a count of samples, from the same review.

- **libFLAC 1.5.0**: encodes each track as FLAC and checks every frame as it
  goes.
  License: BSD 3-Clause (Xiph.Org).
  Source: https://github.com/xiph/flac (release `1.5.0`)
  Patched: its two maths functions go through CORE-MATH's correctly rounded
  versions, so that the same audio encodes to the same bytes on Linux, Windows
  and macOS.

- **CORE-MATH** (`cosf` and `log` only): the correctly rounded functions that
  patch uses, copied in from upstream commit `29f5112`.
  License: MIT.
  Source: https://core-math.gitlabpages.inria.fr

## Code taken from cdrdao

Deadwax reads the disc's subchannel itself, and two parts of that reader are
ported line for line from **cdrdao**: the CD-Text decoder (`CdTextPacks`, from
`processPacks` in `dao/CdrDriver.cc`) and the Q-subchannel decoding
(`QSubchannel`). The `.toc` file Deadwax writes is cdrdao's format, written
byte for byte as cdrdao writes it.
License: GNU GPL, version 2 or later (so it can be part of a GPLv3 program).
Source: https://github.com/cdrdao/cdrdao

## The application

- **.NET 10 runtime**: a release is self-contained, so the runtime is inside
  it.
  License: MIT.
  Source: https://github.com/dotnet/runtime

- **Avalonia 12.1.2**: the user-interface toolkit: every window and control.
  Includes the Fluent theme.
  License: MIT.
  Source: https://github.com/AvaloniaUI/Avalonia

- **SkiaSharp 3.119.4 and Skia**: what Avalonia draws with, and what scales a
  front cover to 500 pixels for `cover.jpg` when the archive has only a larger
  one.
  License: MIT (SkiaSharp); BSD 3-Clause (Skia).
  Source: https://github.com/mono/SkiaSharp and https://skia.googlesource.com/skia

- **HarfBuzzSharp 8.3.1.3**: text shaping for the interface, with its own copy
  of HarfBuzz.
  License: MIT.
  Source: https://github.com/mono/SkiaSharp

- **MicroCom 0.11.6** and **NWayland 0.11.0**: how Avalonia talks to Windows,
  and to a Wayland desktop on Linux.
  License: MIT.
  Source: https://github.com/kekekeks/MicroCom and https://github.com/kekekeks/NWayland

- **Tmds.DBus.Protocol 0.94.1**: on Linux, how Avalonia talks to the desktop
  over D-Bus.
  License: MIT.
  Source: https://github.com/tmds/Tmds.DBus

- **System.IO.Hashing 10.0.12**: the CRC-32 in every rip log.
  License: MIT.
  Source: https://github.com/dotnet/runtime

- **TagLib# 2.3.0**: read and wrote the tags and embedded cover art of FLAC
  files up to Deadwax 0.5.0. Since 0.5.1 Deadwax does that itself, and TagLib#
  is used only when `DEADWAX_TAGS=taglib` asks for it, or by
  `deadwax check-flac-metadata` to compare the two. It leaves in the release
  after 0.5.1.
  License: GNU LGPL, version 2.1.
  Source: https://github.com/mono/taglib-sharp

- **Inter**: the typeface the whole interface is set in, by Rasmus Andersson.
  License: SIL Open Font License 1.1.
  Source: https://github.com/rsms/inter

## Services Deadwax asks

Not part of the program, but what it relies on while it rips: **MusicBrainz**
for the releases and tags (its data is CC0), **AccurateRip** for other
people's checksums of the same pressing, and the **Cover Art Archive** for the
covers. Deadwax only reads from them; it never submits anything.

# What's inside Deadwax

Deadwax is free software under the GNU General Public License, version 3 or
later. A release is a single file, and that file carries other people's work
inside it: the toolkit the window is drawn with, the runtime, a font. This page
says what each one is, what it does here, the license it comes under, and where
its source is.

## Reading the disc and writing FLAC

Deadwax's own code: it talks to the drive itself (the system's own interface on
each platform), reads the audio itself, and encodes FLAC itself. No C library
is involved. Releases up to 0.7.3 carried libcdio and libcdio-paranoia (the
Mac's up to 0.7.1, Linux's up to 0.7.2); the `natives-N` releases on GitHub
keep those libraries and the exact source archives they were built from.

- **cdrdao**: the decoders for CD-Text and the Q subchannel (ISRCs, the
  catalog number, pregaps) were ported from it into Deadwax's own code.
  License: GNU GPL, version 2 or later.
  Source: https://github.com/cdrdao/cdrdao

## The program around it

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

- **Inter**: the typeface the whole interface is set in, by Rasmus Andersson.
  License: SIL Open Font License 1.1.
  Source: https://github.com/rsms/inter

## Services Deadwax asks

Not part of the program, but what it relies on while it rips: **MusicBrainz**
for the releases and tags (its data is CC0), **AccurateRip** for other
people's checksums of the same pressing, and the **Cover Art Archive** for the
covers. Deadwax only reads from them; it never submits anything.

## The license texts

Each license named above is in the `licenses/` folder, exactly as its project
ships it: fetched from the project's own repository by
`tools/fetch-licenses.py`, never typed or edited, and listed with its SHA-256
in `licenses/README.md`. Every release zip carries that folder, this page and
Deadwax's own `LICENSE` beside the program; the Mac app has them in
`Deadwax.app/Contents/Resources`.

# Patches applied to the bundled sources

From the security scan of 2026-10-12 (Code-Security-Review-Policy: a bundled
dependency is read on first import). Each is a few lines of range checks or
ordering; none changes what the libraries do with a well-behaved drive. They
are applied by inner.sh (Linux, Windows) and macos.sh before configure. The
flac patch is not from the scan; it makes the encoder platform-independent.

| patch | finding | what it fixes |
|---|---|---|
| libcdio-01-linux-toc-bounds | H1 | `read_toc_linux` looped to the drive's unclamped last-track byte: TOC entries written past `tocent[100]`, over a pointer freed later. Now refuses a header naming tracks outside 1..99. |
| libcdio-02-windows-toc-bounds | H2, H3 | `read_fulltoc_win32mmc` indexed `tocent[]`/`track_flags[]` with the drive's POINT and PMIN bytes (overwriting the device handle and a function pointer) and read one entry past `TrackData[]`; `read_toc_win32ioctl` looped to the drive's LastTrack. Now bounded to 1..99 and first..last. |
| libcdio-03-macos-toc-and-free | H4, H5 | `read_toc_osx` sized `pp_lba` by descriptor count but indexed it by track number, and wrote at a negative index when a TOC named no track; the second loop skipped the ADR check. `_free_osx` freed the driver struct and then read and freed its fields, and closed the descriptor twice (every `cdio_destroy`). |
| paranoia-01-skip-root-size | M1 | `verify_skip_case` allocated the new root in bytes for a count of 16-bit samples, so a skipped region could return heap garbage as audio. |
| flac-01-correctly-rounded-math | (2026-10-13, not a scan finding) | The same audio encoded to different bytes on Windows than on Linux (Honeydrippers tracks 1 and 4: same audio MD5, a few frames differ). libFLAC picks its window with `cosf` and estimates each frame's predictor with `log`; glibc, mingw-w64 and Apple's libm differ in the last bit for some inputs, which now and then tips a choice. The patch sends both through CORE-MATH's correctly rounded versions (`../coremath/`, MIT, upstream commit 29f5112, copied in as `crmath_*.c`), plus a `roundeven` for mingw, and drops FLAC's own `-fassociative-math -freciprocal-math` (gcc and clang reassociate differently, and it would break CORE-MATH's exact arithmetic). Together with `WITH_ASM=OFF` (no AVX2/FMA/NEON paths) and `-ffp-contract=off` in the recipes, every platform and CPU makes the same choices; `encode-check.c` guards it. Cost: plain C, about 0.3 to 0.7 s per track. Consequence: some new rips differ in bytes (never in audio) from rips encoded by glibc-era builds. |

Not patched (handled in Deadwax instead): M2, the default log handler's
`exit()`/`abort()`, replaced by `cdio_log_set_handler` (LibCdio.cs); L1/L5/L6,
app-side discipline about track numbers, seeks and cache size.
Known and planned around: L2, libcdio's macOS `mmc_run_cmd` is compiled out
(`GET_SCSI_FIXED`), so the subchannel reader needs its own macOS path.

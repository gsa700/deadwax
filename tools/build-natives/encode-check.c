/* Deadwax natives: the same audio must encode to the same bytes everywhere.
 *
 *   encode-check LIBFLAC OUT.flac
 *
 * Generates ten minutes of CD audio from integer arithmetic only (so the
 * input itself is identical on every platform), encodes it through LIBFLAC
 * with Deadwax's settings (FlacWriter.cs: verify, level 5, 16-bit stereo
 * 44.1 kHz, 4096-frame chunks), and writes OUT. The recipe compares OUT's
 * SHA-256 with encode-check.sha256: a libFLAC that rounds, sums or fuses
 * differently from the Linux build fails here instead of in a rip.
 */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#ifdef _WIN32
#include <windows.h>
#define LOAD(p) (void *)LoadLibraryA(p)
#define SYM(h, n) (void *)GetProcAddress((HMODULE)(h), n)
#else
#include <dlfcn.h>
#define LOAD(p) dlopen(p, RTLD_NOW)
#define SYM(h, n) dlsym(h, n)
#endif

typedef void *(*fn_new)(void);
typedef int (*fn_set)(void *, unsigned);
typedef int (*fn_set64)(void *, uint64_t);
typedef int (*fn_init)(void *, const char *, void *, void *);
typedef int (*fn_process)(void *, const int32_t *, unsigned);
typedef int (*fn_finish)(void *);

/* A signal with structure for LPC to find and noise for it to fight: two
 * integer oscillators (triangle waves whose periods drift), a slow envelope,
 * and a filtered random walk, all wrapped into 16 bits. */
static uint32_t rng = 12345 + 3 * 7919;
static int32_t next_rand(void) { rng = rng * 1664525u + 1013904223u; return (int32_t)(rng >> 16) - 32768; }
static int32_t tri(uint32_t phase, uint32_t period) { uint32_t p = phase % period; return (int32_t)(p < period / 2 ? p : period - p) * 2 - (int32_t)period / 2; }

int main(int argc, char **argv) {
    if (argc != 3) { fprintf(stderr, "usage: encode-check LIBFLAC OUT.flac\n"); return 2; }
    void *h = LOAD(argv[1]);
    if (!h) { fprintf(stderr, "encode-check: cannot load %s\n", argv[1]); return 1; }
    const unsigned frames = 44100u * 600u;
    void *e = ((fn_new)SYM(h, "FLAC__stream_encoder_new"))();
    ((fn_set)SYM(h, "FLAC__stream_encoder_set_verify"))(e, 1);
    ((fn_set)SYM(h, "FLAC__stream_encoder_set_compression_level"))(e, 5);
    ((fn_set)SYM(h, "FLAC__stream_encoder_set_channels"))(e, 2);
    ((fn_set)SYM(h, "FLAC__stream_encoder_set_bits_per_sample"))(e, 16);
    ((fn_set)SYM(h, "FLAC__stream_encoder_set_sample_rate"))(e, 44100);
    ((fn_set64)SYM(h, "FLAC__stream_encoder_set_total_samples_estimate"))(e, frames);
    if (((fn_init)SYM(h, "FLAC__stream_encoder_init_file"))(e, argv[2], 0, 0) != 0) { fprintf(stderr, "encode-check: init failed\n"); return 1; }
    fn_process process = (fn_process)SYM(h, "FLAC__stream_encoder_process_interleaved");
    static int32_t buf[4096 * 2];
    int32_t walk_l = 0, walk_r = 0;
    for (unsigned at = 0; at < frames; at += 4096) {
        unsigned n = frames - at < 4096 ? frames - at : 4096;
        for (unsigned i = 0; i < n; i++) {
            uint32_t t = at + i;
            uint32_t p1 = 100 + (t / 22050) % 300, p2 = 37 + (t / 9000) % 90;
            int32_t env = (int32_t)((t / 64) % 1024);
            walk_l += (next_rand() - walk_l) / 16; walk_r += (next_rand() - walk_r) / 8;
            int32_t l = tri(t * 7, p1 * 7) * 40 * env / 1024 * (1 + (int32_t)((t / 30000) % 3)) + tri(t, p2) * 60 + walk_l / 4;
            int32_t r = tri(t * 5 + 1000, p1 * 5) * 35 * (1024 - env) / 1024 + tri(t + 7, p2) * 50 + walk_r / 3;
            buf[2 * i] = (int16_t)l; buf[2 * i + 1] = (int16_t)r;
        }
        if (!process(e, buf, n)) { fprintf(stderr, "encode-check: encode failed\n"); return 1; }
    }
    if (!((fn_finish)SYM(h, "FLAC__stream_encoder_finish"))(e)) { fprintf(stderr, "encode-check: finish failed\n"); return 1; }
    return 0;
}

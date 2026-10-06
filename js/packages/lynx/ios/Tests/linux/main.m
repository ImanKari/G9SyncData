// The iOS Brotli core (ios/src/core/G9SyncDataBrotliCore.m) on Linux with clang + GNUstep base: the same checks as
// the Kotlin G9BrotliCoreTest over the same test vectors (test/fixtures/brotli), so both platforms answer the module
// contract alike. On Linux the Compression framework does not exist: the decoder handed to the core is libbrotlidec
// (Google's C decoder, the one Node and .NET embed) behind compression_decode_buffer's contract. On macOS it is the
// module's own Apple decoder (ios/src/core/G9SyncDataAppleBrotli.h, G9_HAVE_APPLE_COMPRESSION) - N-SD-02.
// Run: npm run test:ios-core (Windows: inside WSL Ubuntu 24.04 with clang, libgnustep-base-dev, gobjc, libbrotli1;
// macOS: Apple clang, nothing to install).
#import <Foundation/Foundation.h>
#include <math.h>
#include <stdint.h>
#import "../../src/core/G9SyncDataBrotliCore.h"
#ifdef G9_HAVE_APPLE_COMPRESSION
#import "../../src/core/G9SyncDataAppleBrotli.h"
#endif

static int failures = 0;
static int checks = 0;

// The condition is the LAST argument: a comma inside an Objective-C message would split an earlier one.
#define CHECK(what, ...)                                      \
  do {                                                        \
    checks++;                                                 \
    if (!(__VA_ARGS__)) {                                     \
      failures++;                                             \
      fprintf(stderr, "FAIL %s (line %d)\n", what, __LINE__); \
    }                                                         \
  } while (0)

#ifdef G9_HAVE_LIBBROTLIDEC
// libbrotlidec's one-shot API (brotli/decode.h; declared here because only the runtime library is installed).
typedef enum {
  BROTLI_DECODER_RESULT_ERROR = 0,
  BROTLI_DECODER_RESULT_SUCCESS = 1,
  BROTLI_DECODER_RESULT_NEEDS_MORE_INPUT = 2,
  BROTLI_DECODER_RESULT_NEEDS_MORE_OUTPUT = 3
} BrotliDecoderResult;
BrotliDecoderResult BrotliDecoderDecompress(size_t encoded_size, const uint8_t *encoded_buffer, size_t *decoded_size,
                                            uint8_t *decoded_buffer);

/** libbrotlidec with compression_decode_buffer's answer: bytes written, `capacity` when full, -1 on error. */
static NSInteger linuxBrotli(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length) {
  size_t written = capacity;
  BrotliDecoderResult result = BrotliDecoderDecompress(length, source, &written, destination);
  if (result == BROTLI_DECODER_RESULT_SUCCESS) return (NSInteger)written;
  return written == capacity ? (NSInteger)capacity : -1;
}
#endif

// The decoder the test vectors run through: Apple's on macOS, libbrotlidec on Linux, none without either.
#if defined(G9_HAVE_APPLE_COMPRESSION)
#define G9_VECTOR_DECODER G9SyncDataAppleBrotli
#define G9_VECTOR_DECODER_NAME "Apple's Compression framework (the module's decoder)"
#elif defined(G9_HAVE_LIBBROTLIDEC)
#define G9_VECTOR_DECODER linuxBrotli
#define G9_VECTOR_DECODER_NAME "libbrotlidec"
#endif

/** Stand-in decoders for the core's own rules (independent of any Brotli library). */
static NSInteger failingDecoder(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length) {
  return -1;
}

static NSInteger fillingDecoder(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length) {
  memset(destination, 0x5a, capacity);
  return (NSInteger)capacity;  // the output never ends: longer than any expected length
}

static NSInteger fiveBytesDecoder(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length) {
  size_t n = capacity < 5 ? capacity : 5;
  for (size_t i = 0; i < n; i++) destination[i] = (uint8_t)(i + 1);
  return (NSInteger)n;
}

static uint32_t crc32Of(NSData *data) {
  const uint8_t *bytes = (const uint8_t *)[data bytes];
  uint32_t crc = 0xffffffffu;
  for (NSUInteger i = 0; i < [data length]; i++) {
    crc ^= bytes[i];
    for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1u)));
  }
  return crc ^ 0xffffffffu;
}

static BOOL ok(NSDictionary *reply) {
  return [[reply objectForKey:@"ok"] boolValue];
}

static NSData *value(NSDictionary *reply) {
  if (!ok(reply)) {
    fprintf(stderr, "unexpected failure: %s\n", [[reply description] UTF8String]);
    failures++;
    return [NSData data];
  }
  id v = [reply objectForKey:@"value"];
  return [v isKindOfClass:[NSData class]] ? v : [NSData data];
}

/** The error code; a failure never carries a value (no partial buffer). */
static NSString *code(NSDictionary *reply) {
  if (ok(reply)) return @"";
  if ([reply objectForKey:@"value"] != nil) return @"value-on-failure";
  return [[reply objectForKey:@"error"] objectForKey:@"code"];
}

static NSString *fixturesDir(void) {
  return @"test/fixtures/brotli";
}

static NSData *fixture(NSString *name) {
  NSData *data = [NSData dataWithContentsOfFile:[fixturesDir() stringByAppendingPathComponent:name]];
  if (data == nil) {
    fprintf(stderr, "missing test vector %s (run from the package root)\n", [name UTF8String]);
    failures++;
    return [NSData data];
  }
  return data;
}

static NSData *bytesOf(const char *text) {
  return [NSData dataWithBytes:text length:strlen(text)];
}

int main(void) {
  @autoreleasepool {
    NSString *arg = G9SyncDataBrotliCodeArg;
    NSString *invalid = G9SyncDataBrotliCodeInvalid;
    NSString *unavailable = G9SyncDataBrotliCodeUnavailable;
    CHECK("code names", [arg isEqual:@"G9SYNC-BROTLI-ARG"] && [invalid isEqual:@"G9SYNC-BROTLI-INVALID"] &&
                            [unavailable isEqual:@"G9SYNC-BROTLI-UNAVAILABLE"]);

    // capabilities
    NSDictionary *caps = [[G9SyncDataBrotliCore capabilitiesWithVersion:@"1.2.2" platform:@"ios" brotli:NO] objectForKey:@"value"];
    CHECK("capabilities version", [[caps objectForKey:@"version"] isEqual:@"1.2.2"]);
    CHECK("capabilities platform", [[caps objectForKey:@"platform"] isEqual:@"ios"]);
    CHECK("capabilities brotli", [[caps objectForKey:@"brotli"] isKindOfClass:[NSNumber class]] && ![[caps objectForKey:@"brotli"] boolValue]);

    // arguments (checked before anything else, also without a decoder)
    NSData *some = bytesOf("x");
    CHECK("nil data", [code([G9SyncDataBrotliCore decompress:nil expectedLength:1 decoder:fiveBytesDecoder]) isEqual:arg]);
    CHECK("string data", [code([G9SyncDataBrotliCore decompress:@"x" expectedLength:1 decoder:fiveBytesDecoder]) isEqual:arg]);
    CHECK("NSNull data", [code([G9SyncDataBrotliCore decompress:[NSNull null] expectedLength:1 decoder:fiveBytesDecoder]) isEqual:arg]);
    double bad[] = {-1, 1.5, NAN, INFINITY, -INFINITY, 603979777.0};
    for (unsigned i = 0; i < sizeof(bad) / sizeof(bad[0]); i++) {
      CHECK("bad expectedLength", [code([G9SyncDataBrotliCore decompress:some expectedLength:bad[i] decoder:fiveBytesDecoder]) isEqual:arg]);
    }
    CHECK("ARG before UNAVAILABLE", [code([G9SyncDataBrotliCore decompress:some expectedLength:-1 decoder:NULL]) isEqual:arg]);
    CHECK("bound 0", [G9SyncDataBrotliCore validate:some expectedLength:0] == nil);
    CHECK("bound -0", [G9SyncDataBrotliCore validate:some expectedLength:-0.0] == nil);
    CHECK("bound 576 MiB", [G9SyncDataBrotliCore validate:some expectedLength:603979776.0] == nil);

    // no decoder (iOS < 15)
    CHECK("no decoder", [code([G9SyncDataBrotliCore decompress:some expectedLength:5 decoder:NULL]) isEqual:unavailable]);

    // the exact-length rule, independent of Brotli
    NSData *five = value([G9SyncDataBrotliCore decompress:some expectedLength:5 decoder:fiveBytesDecoder]);
    const uint8_t oneToFive[] = {1, 2, 3, 4, 5};
    CHECK("exact length", [five isEqual:[NSData dataWithBytes:oneToFive length:5]]);
    CHECK("shorter than expected", [code([G9SyncDataBrotliCore decompress:some expectedLength:6 decoder:fiveBytesDecoder]) isEqual:invalid]);
    CHECK("longer than expected", [code([G9SyncDataBrotliCore decompress:some expectedLength:4 decoder:fiveBytesDecoder]) isEqual:invalid]);
    CHECK("never ending", [code([G9SyncDataBrotliCore decompress:some expectedLength:1000 decoder:fillingDecoder]) isEqual:invalid]);
    CHECK("decoder error", [code([G9SyncDataBrotliCore decompress:some expectedLength:5 decoder:failingDecoder]) isEqual:invalid]);
    CHECK("empty input", [code([G9SyncDataBrotliCore decompress:[NSData data] expectedLength:0 decoder:failingDecoder]) isEqual:invalid]);

#ifdef G9_VECTOR_DECODER
    // every vector decodes to exactly its original (length + CRC-32 from manifest.txt)
    NSString *manifest = [NSString stringWithContentsOfFile:[fixturesDir() stringByAppendingPathComponent:@"manifest.txt"]
                                                   encoding:NSUTF8StringEncoding
                                                      error:NULL];
    int vectors = 0;
    for (NSString *raw in [manifest componentsSeparatedByString:@"\n"]) {
      NSString *line = [raw stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceAndNewlineCharacterSet]];
      if ([line length] == 0 || [line hasPrefix:@"#"]) continue;
      NSMutableArray *parts = [NSMutableArray array];
      for (NSString *part in [line componentsSeparatedByString:@" "]) {
        if ([part length] > 0) [parts addObject:part];
      }
      NSString *name = [parts objectAtIndex:0];
      NSInteger length = [[parts objectAtIndex:1] integerValue];
      NSData *out = value([G9SyncDataBrotliCore decompress:fixture(name) expectedLength:(double)length decoder:G9_VECTOR_DECODER]);
      CHECK([name UTF8String], (NSInteger)[out length] == length);
      CHECK([name UTF8String], [[NSString stringWithFormat:@"%08x", crc32Of(out)] isEqual:[parts objectAtIndex:3]]);
      vectors++;
    }
    CHECK("four vectors", vectors == 4);
    const uint8_t fortyTwo[] = {0x2a};
    CHECK("one byte", [value([G9SyncDataBrotliCore decompress:fixture(@"one-byte.br") expectedLength:1 decoder:G9_VECTOR_DECODER]) isEqual:[NSData dataWithBytes:fortyTwo length:1]]);
    CHECK("empty stream", [value([G9SyncDataBrotliCore decompress:fixture(@"empty.br") expectedLength:0 decoder:G9_VECTOR_DECODER]) length] == 0);

    // an output length other than expected
    NSData *mixed = fixture(@"mixed-300k.br");
    CHECK("mixed: expected - 1", [code([G9SyncDataBrotliCore decompress:mixed expectedLength:307199 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    CHECK("mixed: expected + 1", [code([G9SyncDataBrotliCore decompress:mixed expectedLength:307201 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    CHECK("one byte as 0", [code([G9SyncDataBrotliCore decompress:fixture(@"one-byte.br") expectedLength:0 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    CHECK("empty as 1", [code([G9SyncDataBrotliCore decompress:fixture(@"empty.br") expectedLength:1 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);

    // corrupt and truncated streams (the same derivations as the Kotlin and vitest suites)
    NSMutableData *flipped = [NSMutableData dataWithData:mixed];
    ((uint8_t *)[flipped mutableBytes])[2] ^= 0xff;
    CHECK("flipped head byte", [code([G9SyncDataBrotliCore decompress:flipped expectedLength:307200 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    CHECK("not brotli", [code([G9SyncDataBrotliCore decompress:bytesOf("G9SyncData: this is not a Brotli stream.") expectedLength:40 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    NSMutableData *ff = [NSMutableData dataWithLength:16];
    memset([ff mutableBytes], 0xff, 16);
    CHECK("16 x 0xff", [code([G9SyncDataBrotliCore decompress:ff expectedLength:0 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    CHECK("no bytes", [code([G9SyncDataBrotliCore decompress:[NSData data] expectedLength:0 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    CHECK("truncated half", [code([G9SyncDataBrotliCore decompress:[mixed subdataWithRange:NSMakeRange(0, [mixed length] / 2)] expectedLength:307200 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    NSData *cut = [mixed subdataWithRange:NSMakeRange(0, [mixed length] - 1)];
    NSMutableData *trailing = [NSMutableData dataWithData:mixed];
    [trailing appendData:bytesOf("tail")];
    // Bytes after a complete stream: the one-shot decoders on both sides of this suite (libbrotlidec's
    // BrotliDecoderDecompress, Apple's compression_decode_buffer) stop at the stream's end and ignore the rest, so the
    // core returns the exact bytes (N-SD-04); Android (org.brotli) and Lynxtron (node:zlib) reject it. Recorded, not
    // asserted: the bytes are right either way, and the frame's own length check stands behind it.
    NSData *whole = value([G9SyncDataBrotliCore decompress:mixed expectedLength:307200 decoder:G9_VECTOR_DECODER]);
    NSDictionary *trailingReply = [G9SyncDataBrotliCore decompress:trailing expectedLength:307200 decoder:G9_VECTOR_DECODER];
    printf("N-SD-04 bytes after the end: %s\n", ok(trailingReply) ? ([value(trailingReply) isEqual:whole] ? "accepted, exact bytes" : "accepted, WRONG bytes") : [code(trailingReply) UTF8String]);
    // A stream cut inside its final empty block: rejected by both (Apple's on macOS 26.6 arm64, 2026-10-06 - N-SD-04).
    CHECK("truncated last byte", [code([G9SyncDataBrotliCore decompress:cut expectedLength:307200 decoder:G9_VECTOR_DECODER]) isEqual:invalid]);
    printf("ios brotli core: test vectors decoded with %s\n", G9_VECTOR_DECODER_NAME);
#else
    printf("ios brotli core: no Brotli decoder (Linux: libbrotlidec.so.1 not found - apt-get install -y libbrotli1), test vectors SKIPPED\n");
#endif

    printf("ios brotli core: %d checks, %d failures\n", checks, failures);
  }
  return failures == 0 ? 0 : 1;
}

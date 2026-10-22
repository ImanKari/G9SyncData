// The iOS cores (ios/src/core: G9SyncDataBrotliCore.m, G9SyncDataSnapshotFileCore.m) on Linux with clang + GNUstep
// base: the same checks as the Kotlin G9BrotliCoreTest and G9SnapshotFileCoreTest over the same test vectors
// (test/fixtures/brotli), so both platforms answer the module contract alike. On Linux the Compression framework does not exist: the decoder handed to the core is libbrotlidec
// (Google's C decoder, the one Node and .NET embed) behind compression_decode_buffer's contract. On macOS it is the
// module's own Apple decoder (ios/src/core/G9SyncDataAppleBrotli.h, G9_HAVE_APPLE_COMPRESSION) - N-SD-02.
// Run: npm run test:ios-core (Windows: inside WSL Ubuntu 24.04 with clang, libgnustep-base-dev, gobjc, libbrotli1;
// macOS: Apple clang, nothing to install).
#import <Foundation/Foundation.h>
#include <math.h>
#include <stdint.h>
#import "../../src/core/G9SyncDataBrotliCore.h"
#import "../../src/core/G9SyncDataSnapshotFileCore.h"
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

/** The value of a successful reply whatever its type (a number, a list); NSNull on failure. */
static id anyValue(NSDictionary *reply) {
  if (!ok(reply)) {
    fprintf(stderr, "unexpected failure: %s\n", [[reply description] UTF8String]);
    failures++;
    return [NSNull null];
  }
  return [reply objectForKey:@"value"];
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

/** A new empty directory under the system temp directory. */
static NSString *freshDirectory(void) {
  NSString *path = [NSTemporaryDirectory() stringByAppendingPathComponent:
                                               [NSString stringWithFormat:@"g9-snapshot-%@", [[NSProcessInfo processInfo] globallyUniqueString]]];
  [[NSFileManager defaultManager] createDirectoryAtPath:path withIntermediateDirectories:YES attributes:nil error:NULL];
  return path;
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
    NSDictionary *caps = [[G9SyncDataBrotliCore capabilitiesWithVersion:@"1.5.5" platform:@"ios" brotli:NO snapshotFiles:YES] objectForKey:@"value"];
    CHECK("capabilities version", [[caps objectForKey:@"version"] isEqual:@"1.5.5"]);
    CHECK("capabilities platform", [[caps objectForKey:@"platform"] isEqual:@"ios"]);
    CHECK("capabilities brotli", [[caps objectForKey:@"brotli"] isKindOfClass:[NSNumber class]] && ![[caps objectForKey:@"brotli"] boolValue]);
    CHECK("capabilities snapshotFiles", [[caps objectForKey:@"snapshotFiles"] boolValue]);

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

    // ------------------------------------------------------------ snapshot files (ADR-0072)
    NSString *fileArg = G9SyncDataFileCodeArg;
    NSString *fileInvalid = G9SyncDataFileCodeInvalid;
    NSString *fileIo = G9SyncDataFileCodeIo;
    CHECK("file code names", [fileArg isEqual:@"G9SYNC-FILE-ARG"] && [fileInvalid isEqual:@"G9SYNC-FILE-INVALID"] &&
                                 [fileIo isEqual:@"G9SYNC-FILE-IO"]);

    // SHA-256 (CommonCrypto on Apple, the portable one here): FIPS 180-4 answers, padding edges at 55/56/64 bytes.
    CHECK("sha256 empty", [[G9SyncDataSnapshotFileCore sha256Hex:[NSData data]] isEqual:@"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"]);
    CHECK("sha256 abc", [[G9SyncDataSnapshotFileCore sha256Hex:bytesOf("abc")] isEqual:@"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"]);
    NSMutableData *letters = [NSMutableData dataWithLength:64];
    memset([letters mutableBytes], 'a', 64);
    CHECK("sha256 55 x a", [[G9SyncDataSnapshotFileCore sha256Hex:[letters subdataWithRange:NSMakeRange(0, 55)]] isEqual:@"9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318"]);
    CHECK("sha256 56 x a", [[G9SyncDataSnapshotFileCore sha256Hex:[letters subdataWithRange:NSMakeRange(0, 56)]] isEqual:@"b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a"]);
    CHECK("sha256 64 x a", [[G9SyncDataSnapshotFileCore sha256Hex:letters] isEqual:@"ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb"]);
    NSMutableData *mebi = [NSMutableData dataWithLength:1048576];
    for (NSUInteger i = 0; i < 1048576; i++) ((uint8_t *)[mebi mutableBytes])[i] = (uint8_t)i;
    CHECK("sha256 1 MiB", [[G9SyncDataSnapshotFileCore sha256Hex:mebi] isEqual:@"fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83"]);

    // create, size, list, head, remove
    NSString *directory = freshDirectory();
    NSString *nested = [[directory stringByAppendingPathComponent:@"sub"] stringByAppendingPathComponent:@"a.sqlite"];
    CHECK("create makes the directory", ok([G9SyncDataSnapshotFileCore create:nested size:5000]));
    CHECK("size of created", [anyValue([G9SyncDataSnapshotFileCore size:nested]) doubleValue] == 5000);
    CHECK("create truncates", ok([G9SyncDataSnapshotFileCore create:nested size:20]) &&
                                  [anyValue([G9SyncDataSnapshotFileCore size:nested]) doubleValue] == 20);
    CHECK("size of a missing file", [[[G9SyncDataSnapshotFileCore size:[directory stringByAppendingPathComponent:@"none"]] objectForKey:@"value"] isEqual:[NSNull null]]);
    CHECK("size of a directory", [[[G9SyncDataSnapshotFileCore size:directory] objectForKey:@"value"] isEqual:[NSNull null]]);
    NSString *subdir = [directory stringByAppendingPathComponent:@"sub"];
    [G9SyncDataSnapshotFileCore create:[subdir stringByAppendingPathComponent:@"0.sqlite"] size:1];
    [[NSFileManager defaultManager] createDirectoryAtPath:[subdir stringByAppendingPathComponent:@"z-dir"] withIntermediateDirectories:YES attributes:nil error:NULL];
    NSArray *listed = anyValue([G9SyncDataSnapshotFileCore list:subdir]);
    CHECK("list: files only, sorted", [listed count] == 2 && [[listed objectAtIndex:0] isEqual:[subdir stringByAppendingPathComponent:@"0.sqlite"]] &&
                                          [[listed objectAtIndex:1] isEqual:nested]);
    CHECK("list of a missing directory", [anyValue([G9SyncDataSnapshotFileCore list:[directory stringByAppendingPathComponent:@"none"]]) count] == 0);
    CHECK("remove", ok([G9SyncDataSnapshotFileCore remove:[subdir stringByAppendingPathComponent:@"0.sqlite"]]));
    CHECK("remove a missing file", ok([G9SyncDataSnapshotFileCore remove:[subdir stringByAppendingPathComponent:@"0.sqlite"]]));
    CHECK("head of zeros", [value([G9SyncDataSnapshotFileCore head:nested length:16]) isEqual:[NSMutableData dataWithLength:16]]);
    CHECK("head past the end", [value([G9SyncDataSnapshotFileCore head:nested length:100]) length] == 20);
    CHECK("head of a missing file", [code([G9SyncDataSnapshotFileCore head:[directory stringByAppendingPathComponent:@"none"] length:4]) isEqual:fileIo]);

    // a segment through a stand-in decoder: five bytes {1..5} whose SHA-256 is known
    NSString *five5 = @"74f81fe167d99b4cb41d6d0ccda82278caee9f3e2f25d5e5a3936ff3dcec60d0";
    CHECK("write at an offset", ok([G9SyncDataSnapshotFileCore writeSegment:nested offset:7 data:some length:5 sha256:five5 sync:NO decoder:fiveBytesDecoder]));
    NSData *withFive = value([G9SyncDataSnapshotFileCore head:nested length:20]);
    const uint8_t expectedFile[20] = {0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 0, 0, 0, 0, 0, 0, 0, 0};
    CHECK("bytes at the offset, size unchanged", [withFive isEqual:[NSData dataWithBytes:expectedFile length:20]]);
    CHECK("write with sync", ok([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:5 sha256:five5 sync:YES decoder:fiveBytesDecoder]));
    CHECK("write past the end grows the file", ok([G9SyncDataSnapshotFileCore writeSegment:nested offset:30 data:some length:5 sha256:five5 sync:NO decoder:fiveBytesDecoder]) &&
                                                   [anyValue([G9SyncDataSnapshotFileCore size:nested]) doubleValue] == 35);

    // a bad segment writes nothing
    NSData *before = value([G9SyncDataSnapshotFileCore head:nested length:64]);
    CHECK("wrong hash", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:5 sha256:@"0000000000000000000000000000000000000000000000000000000000000000" sync:NO decoder:fiveBytesDecoder]) isEqual:fileInvalid]);
    CHECK("wrong length", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:6 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileInvalid]);
    CHECK("decoder error", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:5 sha256:five5 sync:NO decoder:failingDecoder]) isEqual:fileInvalid]);
    CHECK("file untouched", [value([G9SyncDataSnapshotFileCore head:nested length:64]) isEqual:before]);
    CHECK("no decoder stays UNAVAILABLE", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:5 sha256:five5 sync:NO decoder:NULL]) isEqual:unavailable]);
    CHECK("a missing staged file is I/O", [code([G9SyncDataSnapshotFileCore writeSegment:[directory stringByAppendingPathComponent:@"none"] offset:0 data:some length:5 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileIo]);
    CHECK("missing file not created", [[[G9SyncDataSnapshotFileCore size:[directory stringByAppendingPathComponent:@"none"]] objectForKey:@"value"] isEqual:[NSNull null]]);

    // arguments
    CHECK("arg: empty path", [code([G9SyncDataSnapshotFileCore writeSegment:@"" offset:0 data:some length:5 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
    CHECK("arg: nil path", [code([G9SyncDataSnapshotFileCore create:nil size:1]) isEqual:fileArg]);
    double badCounts[] = {-1, 0.5, NAN, INFINITY, 9007199254740992.0};
    for (unsigned i = 0; i < sizeof(badCounts) / sizeof(badCounts[0]); i++) {
      CHECK("arg: offset", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:badCounts[i] data:some length:5 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
      CHECK("arg: size", [code([G9SyncDataSnapshotFileCore create:nested size:badCounts[i]]) isEqual:fileArg]);
      CHECK("arg: head length", [code([G9SyncDataSnapshotFileCore head:nested length:badCounts[i]]) isEqual:fileArg]);
    }
    CHECK("arg: length 0", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:0 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
    CHECK("arg: length 64 MiB + 1", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:67108865 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
    CHECK("arg: upper-case hash", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:5 sha256:[five5 uppercaseString] sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
    CHECK("arg: short hash", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:some length:5 sha256:@"ABC" sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
    CHECK("arg: data", [code([G9SyncDataSnapshotFileCore writeSegment:nested offset:0 data:@"x" length:5 sha256:five5 sync:NO decoder:fiveBytesDecoder]) isEqual:fileArg]);
    CHECK("arg: list", [code([G9SyncDataSnapshotFileCore list:@""]) isEqual:fileArg]);

#ifdef G9_VECTOR_DECODER
    // a real segment: the 300 KiB vector decompressed, hashed and written at a 4 KiB offset
    NSString *vectorPath = [directory stringByAppendingPathComponent:@"vector.sqlite"];
    CHECK("vector: create", ok([G9SyncDataSnapshotFileCore create:vectorPath size:4096 + 307200 + 100]));
    CHECK("vector: write", ok([G9SyncDataSnapshotFileCore writeSegment:vectorPath offset:4096 data:fixture(@"mixed-300k.br") length:307200
                                                                sha256:@"f42bc46515f75d8dae64c8e13f476a2910428d1d340af6c465a7cb4c9532457d" sync:YES decoder:G9_VECTOR_DECODER]));
    NSData *staged = [NSData dataWithContentsOfFile:vectorPath];
    CHECK("vector: size", [staged length] == 4096 + 307200 + 100);
    CHECK("vector: bytes", [[G9SyncDataSnapshotFileCore sha256Hex:[staged subdataWithRange:NSMakeRange(4096, 307200)]] isEqual:@"f42bc46515f75d8dae64c8e13f476a2910428d1d340af6c465a7cb4c9532457d"]);
    CHECK("vector: wrong hash", [code([G9SyncDataSnapshotFileCore writeSegment:vectorPath offset:0 data:fixture(@"mixed-300k.br") length:307200
                                                                        sha256:@"139239abb424e113356ad01797dc12412068a8b0e143063f91daf4cc4fd560f9" sync:NO decoder:G9_VECTOR_DECODER]) isEqual:fileInvalid]);
    CHECK("vector: length - 1", [code([G9SyncDataSnapshotFileCore writeSegment:vectorPath offset:0 data:fixture(@"mixed-300k.br") length:307199
                                                                        sha256:@"f42bc46515f75d8dae64c8e13f476a2910428d1d340af6c465a7cb4c9532457d" sync:NO decoder:G9_VECTOR_DECODER]) isEqual:fileInvalid]);
#endif
    [[NSFileManager defaultManager] removeItemAtPath:directory error:NULL];

    printf("ios cores: %d checks, %d failures\n", checks, failures);
  }
  return failures == 0 ? 0 : 1;
}

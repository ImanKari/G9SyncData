#import "G9SyncDataSnapshotFileCore.h"
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#ifdef __APPLE__
#include <CommonCrypto/CommonDigest.h>
#endif

NSString *const G9SyncDataFileCodeArg = @"G9SYNC-FILE-ARG";
NSString *const G9SyncDataFileCodeInvalid = @"G9SYNC-FILE-INVALID";
NSString *const G9SyncDataFileCodeIo = @"G9SYNC-FILE-IO";

/** 2^53 - 1: the largest integer a JS number holds exactly. */
static const double G9SyncDataMaxSafeInteger = 9007199254740991.0;

static BOOL G9SyncDataIsCount(double value) {
  return !isnan(value) && value >= 0 && value <= G9SyncDataMaxSafeInteger && floor(value) == value;
}

static BOOL G9SyncDataHasPath(NSString *path) {
  return path != nil && [path isKindOfClass:[NSString class]] && [path length] > 0;
}

static BOOL G9SyncDataIsSha256Hex(NSString *text) {
  if (text == nil || ![text isKindOfClass:[NSString class]] || [text length] != 64) return NO;
  for (NSUInteger i = 0; i < 64; i++) {
    unichar c = [text characterAtIndex:i];
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return NO;
  }
  return YES;
}

static NSDictionary *G9SyncDataIoFail(NSString *what, NSString *path, int error) {
  return G9SyncDataFail(G9SyncDataFileCodeIo,
                        [NSString stringWithFormat:@"%@ %@: %s (errno %d).", what, path, strerror(error), error]);
}

#ifndef __APPLE__
// SHA-256 (FIPS 180-4) for builds without CommonCrypto: the GNUstep suite on Linux. Apple builds use CC_SHA256.
typedef struct {
  uint32_t state[8];
  uint8_t block[64];
} G9SyncDataSha256;

static const uint32_t G9SyncDataSha256K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01,
    0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
    0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
    0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08,
    0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};

#define G9_ROTR(x, n) (((x) >> (n)) | ((x) << (32 - (n))))

static void G9SyncDataSha256Block(G9SyncDataSha256 *h, const uint8_t *p) {
  uint32_t w[64];
  for (int i = 0; i < 16; i++) {
    w[i] = ((uint32_t)p[i * 4] << 24) | ((uint32_t)p[i * 4 + 1] << 16) | ((uint32_t)p[i * 4 + 2] << 8) | p[i * 4 + 3];
  }
  for (int i = 16; i < 64; i++) {
    uint32_t s0 = G9_ROTR(w[i - 15], 7) ^ G9_ROTR(w[i - 15], 18) ^ (w[i - 15] >> 3);
    uint32_t s1 = G9_ROTR(w[i - 2], 17) ^ G9_ROTR(w[i - 2], 19) ^ (w[i - 2] >> 10);
    w[i] = w[i - 16] + s0 + w[i - 7] + s1;
  }
  uint32_t a = h->state[0], b = h->state[1], c = h->state[2], d = h->state[3];
  uint32_t e = h->state[4], f = h->state[5], g = h->state[6], k = h->state[7];
  for (int i = 0; i < 64; i++) {
    uint32_t t1 = k + (G9_ROTR(e, 6) ^ G9_ROTR(e, 11) ^ G9_ROTR(e, 25)) + ((e & f) ^ (~e & g)) + G9SyncDataSha256K[i] + w[i];
    uint32_t t2 = (G9_ROTR(a, 2) ^ G9_ROTR(a, 13) ^ G9_ROTR(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
    k = g;
    g = f;
    f = e;
    e = d + t1;
    d = c;
    c = b;
    b = a;
    a = t1 + t2;
  }
  h->state[0] += a;
  h->state[1] += b;
  h->state[2] += c;
  h->state[3] += d;
  h->state[4] += e;
  h->state[5] += f;
  h->state[6] += g;
  h->state[7] += k;
}

static void G9SyncDataSha256Digest(const uint8_t *data, size_t length, uint8_t out[32]) {
  G9SyncDataSha256 h = {{0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19}, {0}};
  uint64_t bits = (uint64_t)length * 8;
  size_t i = 0;
  for (; i + 64 <= length; i += 64) G9SyncDataSha256Block(&h, data + i);
  size_t rest = length - i;
  if (rest > 0) memcpy(h.block, data + i, rest);
  h.block[rest++] = 0x80;
  if (rest > 56) {
    memset(h.block + rest, 0, 64 - rest);
    G9SyncDataSha256Block(&h, h.block);
    rest = 0;
  }
  memset(h.block + rest, 0, 56 - rest);
  for (int j = 0; j < 8; j++) h.block[56 + j] = (uint8_t)(bits >> (56 - j * 8));
  G9SyncDataSha256Block(&h, h.block);
  for (int j = 0; j < 8; j++) {
    out[j * 4] = (uint8_t)(h.state[j] >> 24);
    out[j * 4 + 1] = (uint8_t)(h.state[j] >> 16);
    out[j * 4 + 2] = (uint8_t)(h.state[j] >> 8);
    out[j * 4 + 3] = (uint8_t)h.state[j];
  }
}
#undef G9_ROTR
#endif

/** Writes all `length` bytes at `offset`, retrying short writes and EINTR. 0, or the errno. */
static int G9SyncDataWriteAll(int fd, const uint8_t *bytes, size_t length, off_t offset) {
  while (length > 0) {
    ssize_t n = pwrite(fd, bytes, length, offset);
    if (n < 0) {
      if (errno == EINTR) continue;
      return errno;
    }
    bytes += n;
    length -= (size_t)n;
    offset += n;
  }
  return 0;
}

/** Stable storage: F_FULLFSYNC where the OS has it (Apple's fsync stops at the drive's cache), else fsync. */
static int G9SyncDataFlush(int fd) {
#ifdef F_FULLFSYNC
  if (fcntl(fd, F_FULLFSYNC) == 0) return 0;
#endif
  return fsync(fd) == 0 ? 0 : errno;
}

@implementation G9SyncDataSnapshotFileCore

+ (NSString *)sha256Hex:(NSData *)data {
  uint8_t digest[32];
#ifdef __APPLE__
  CC_SHA256([data bytes], (CC_LONG)[data length], digest);
#else
  G9SyncDataSha256Digest((const uint8_t *)[data bytes], [data length], digest);
#endif
  static const char hex[] = "0123456789abcdef";
  char text[65];
  for (int i = 0; i < 32; i++) {
    text[i * 2] = hex[digest[i] >> 4];
    text[i * 2 + 1] = hex[digest[i] & 0x0f];
  }
  text[64] = 0;
  return [NSString stringWithUTF8String:text];
}

+ (NSDictionary *)create:(NSString *)path size:(double)size {
  if (!G9SyncDataHasPath(path)) return G9SyncDataFail(G9SyncDataFileCodeArg, @"path is empty.");
  if (!G9SyncDataIsCount(size)) {
    return G9SyncDataFail(G9SyncDataFileCodeArg,
                          [NSString stringWithFormat:@"size must be a non-negative integer; got %g.", size]);
  }
  NSString *directory = [path stringByDeletingLastPathComponent];
  if ([directory length] > 0) {
    NSError *error = nil;
    if (![[NSFileManager defaultManager] createDirectoryAtPath:directory
                                   withIntermediateDirectories:YES
                                                    attributes:nil
                                                         error:&error]) {
      return G9SyncDataFail(G9SyncDataFileCodeIo,
                            [NSString stringWithFormat:@"Cannot create %@: %@", directory, [error localizedDescription]]);
    }
  }
  int fd = open([path fileSystemRepresentation], O_RDWR | O_CREAT | O_TRUNC, 0600);
  if (fd < 0) return G9SyncDataIoFail(@"Cannot create", path, errno);
  int failed = ftruncate(fd, (off_t)size) == 0 ? 0 : errno;
  close(fd);
  return failed == 0 ? G9SyncDataOk(nil) : G9SyncDataIoFail(@"Cannot size", path, failed);
}

+ (NSDictionary *)writeSegment:(NSString *)path
                        offset:(double)offset
                          data:(id)data
                        length:(double)length
                        sha256:(NSString *)sha256
                          sync:(BOOL)sync
                       decoder:(G9SyncDataBrotliDecoder)decoder {
  if (!G9SyncDataHasPath(path)) return G9SyncDataFail(G9SyncDataFileCodeArg, @"path is empty.");
  if (!G9SyncDataIsCount(offset)) {
    return G9SyncDataFail(G9SyncDataFileCodeArg,
                          [NSString stringWithFormat:@"offset must be a non-negative integer; got %g.", offset]);
  }
  if (!G9SyncDataIsCount(length) || length < 1 || length > G9SyncDataSnapshotFileMaxSegment) {
    return G9SyncDataFail(G9SyncDataFileCodeArg,
                          [NSString stringWithFormat:@"length must be an integer in [1, %d]; got %g.",
                                                     G9SyncDataSnapshotFileMaxSegment, length]);
  }
  if (!G9SyncDataIsSha256Hex(sha256)) {
    return G9SyncDataFail(G9SyncDataFileCodeArg, @"sha256 must be 64 lower-case hex characters.");
  }
  if (data == nil || ![data isKindOfClass:[NSData class]]) {
    return G9SyncDataFail(G9SyncDataFileCodeArg, @"data must be an ArrayBuffer.");
  }

  NSDictionary *decoded = [G9SyncDataBrotliCore decompress:data expectedLength:length decoder:decoder];
  if (![[decoded objectForKey:@"ok"] boolValue]) {
    NSDictionary *error = [decoded objectForKey:@"error"];
    // No decoder (iOS < 15) is not a bad segment: the caller should not retry or discard on it.
    if ([[error objectForKey:@"code"] isEqual:G9SyncDataBrotliCodeUnavailable]) return decoded;
    return G9SyncDataFail(G9SyncDataFileCodeInvalid,
                          [NSString stringWithFormat:@"The segment does not decompress to %.0f bytes: %@", length,
                                                     [error objectForKey:@"message"]]);
  }
  NSData *raw = [decoded objectForKey:@"value"];
  if (![[self sha256Hex:raw] isEqualToString:sha256]) {
    return G9SyncDataFail(G9SyncDataFileCodeInvalid, @"The segment fails its SHA-256.");
  }

  // No O_CREAT: the target is the staged file snapshotFileCreate sized; a missing one is an I/O error, not a new file.
  int fd = open([path fileSystemRepresentation], O_WRONLY);
  if (fd < 0) return G9SyncDataIoFail(@"Cannot open", path, errno);
  int failed = G9SyncDataWriteAll(fd, (const uint8_t *)[raw bytes], [raw length], (off_t)offset);
  if (failed == 0 && sync) failed = G9SyncDataFlush(fd);
  if (close(fd) != 0 && failed == 0) failed = errno;
  return failed == 0 ? G9SyncDataOk(nil) : G9SyncDataIoFail(@"Cannot write", path, failed);
}

+ (NSDictionary *)size:(NSString *)path {
  if (!G9SyncDataHasPath(path)) return G9SyncDataFail(G9SyncDataFileCodeArg, @"path is empty.");
  struct stat info;
  if (stat([path fileSystemRepresentation], &info) != 0 || !S_ISREG(info.st_mode)) return G9SyncDataOk(nil);
  return G9SyncDataOk([NSNumber numberWithDouble:(double)info.st_size]);
}

+ (NSDictionary *)head:(NSString *)path length:(double)length {
  if (!G9SyncDataHasPath(path)) return G9SyncDataFail(G9SyncDataFileCodeArg, @"path is empty.");
  if (!G9SyncDataIsCount(length) || length > G9SyncDataSnapshotFileMaxSegment) {
    return G9SyncDataFail(G9SyncDataFileCodeArg,
                          [NSString stringWithFormat:@"length must be an integer in [0, %d]; got %g.",
                                                     G9SyncDataSnapshotFileMaxSegment, length]);
  }
  int fd = open([path fileSystemRepresentation], O_RDONLY);
  if (fd < 0) return G9SyncDataIoFail(@"Cannot open", path, errno);
  size_t wanted = (size_t)length;
  NSMutableData *buffer = [NSMutableData dataWithLength:wanted];
  size_t done = 0;
  int failed = 0;
  while (done < wanted) {
    ssize_t n = pread(fd, (uint8_t *)[buffer mutableBytes] + done, wanted - done, (off_t)done);
    if (n < 0 && errno == EINTR) continue;
    if (n < 0) failed = errno;
    if (n <= 0) break;
    done += (size_t)n;
  }
  close(fd);
  if (failed != 0) return G9SyncDataIoFail(@"Cannot read", path, failed);
  [buffer setLength:done];
  return G9SyncDataOk(buffer);
}

+ (NSDictionary *)remove:(NSString *)path {
  if (!G9SyncDataHasPath(path)) return G9SyncDataFail(G9SyncDataFileCodeArg, @"path is empty.");
  if (unlink([path fileSystemRepresentation]) != 0 && errno != ENOENT) {
    return G9SyncDataIoFail(@"Cannot delete", path, errno);
  }
  return G9SyncDataOk(nil);
}

+ (NSDictionary *)list:(NSString *)directory {
  if (!G9SyncDataHasPath(directory)) return G9SyncDataFail(G9SyncDataFileCodeArg, @"directory is empty.");
  NSArray *names = [[NSFileManager defaultManager] contentsOfDirectoryAtPath:directory error:NULL];
  NSMutableArray *files = [NSMutableArray arrayWithCapacity:[names count]];
  for (NSString *name in names) {
    NSString *full = [directory stringByAppendingPathComponent:name];
    struct stat info;
    if (stat([full fileSystemRepresentation], &info) == 0 && S_ISREG(info.st_mode)) [files addObject:full];
  }
  [files sortUsingSelector:@selector(compare:)];
  return G9SyncDataOk(files);
}

@end

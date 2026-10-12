#import "G9SyncDataBrotliCore.h"
#include <math.h>

NSString *const G9SyncDataBrotliCodeArg = @"G9SYNC-BROTLI-ARG";
NSString *const G9SyncDataBrotliCodeInvalid = @"G9SYNC-BROTLI-INVALID";
NSString *const G9SyncDataBrotliCodeUnavailable = @"G9SYNC-BROTLI-UNAVAILABLE";

/** What an empty input points at: decoders get a valid pointer even for zero bytes. */
static const uint8_t G9SyncDataNoBytes[1] = {0};

@implementation G9SyncDataBrotliCore

+ (NSDictionary *)capabilitiesWithVersion:(NSString *)version
                                 platform:(NSString *)platform
                                   brotli:(BOOL)brotli
                            snapshotFiles:(BOOL)snapshotFiles {
  NSDictionary *value = [NSDictionary dictionaryWithObjectsAndKeys:version, @"version", platform, @"platform",
                                                                    [NSNumber numberWithBool:brotli], @"brotli",
                                                                    [NSNumber numberWithBool:snapshotFiles],
                                                                    @"snapshotFiles", nil];
  return G9SyncDataOk(value);
}

+ (NSDictionary *)validate:(id)data expectedLength:(double)expectedLength {
  if (data == nil || ![data isKindOfClass:[NSData class]]) {
    return G9SyncDataFail(G9SyncDataBrotliCodeArg, @"data must be an ArrayBuffer.");
  }
  if (isnan(expectedLength) || expectedLength < 0 || expectedLength > G9SyncDataBrotliMaxExpectedLength ||
      floor(expectedLength) != expectedLength) {
    return G9SyncDataFail(G9SyncDataBrotliCodeArg,
                          [NSString stringWithFormat:@"expectedLength must be an integer in [0, %d]; got %g.",
                                                     G9SyncDataBrotliMaxExpectedLength, expectedLength]);
  }
  return nil;
}

+ (NSDictionary *)decompress:(id)data expectedLength:(double)expectedLength decoder:(G9SyncDataBrotliDecoder)decoder {
  NSDictionary *invalid = [self validate:data expectedLength:expectedLength];
  if (invalid != nil) return invalid;
  if (decoder == NULL) {
    return G9SyncDataFail(G9SyncDataBrotliCodeUnavailable,
                          @"Brotli decoding needs iOS 15 or later (Apple's Compression framework).");
  }
  NSData *input = (NSData *)data;
  NSUInteger expected = (NSUInteger)expectedLength;
  // One byte more than expected: a stream that fills it is longer than expected; decoding stops there.
  NSMutableData *output = nil;
  @try {
    output = [NSMutableData dataWithLength:expected + 1];
  } @catch (NSException *exception) {
    output = nil;
  }
  if (output == nil) {
    return G9SyncDataFail(G9SyncDataBrotliCodeInvalid,
                          [NSString stringWithFormat:@"Out of memory while decoding %lu bytes.", (unsigned long)expected]);
  }
  const uint8_t *source = [input length] > 0 ? (const uint8_t *)[input bytes] : G9SyncDataNoBytes;
  NSInteger written = decoder((uint8_t *)[output mutableBytes], expected + 1, source, [input length]);
  if (written < 0) return G9SyncDataFail(G9SyncDataBrotliCodeInvalid, @"The Brotli stream is invalid.");
  if ((NSUInteger)written > expected) {
    return G9SyncDataFail(G9SyncDataBrotliCodeInvalid,
                          [NSString stringWithFormat:@"The Brotli stream holds more than the %lu bytes expected.",
                                                     (unsigned long)expected]);
  }
  if ((NSUInteger)written < expected) {
    return G9SyncDataFail(G9SyncDataBrotliCodeInvalid,
                          [NSString stringWithFormat:@"The Brotli stream ends after %ld bytes; %lu were expected.",
                                                     (long)written, (unsigned long)expected]);
  }
  [output setLength:expected];
  return G9SyncDataOk(output);
}

@end

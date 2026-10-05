#import <Foundation/Foundation.h>
#include <stddef.h>
#include <stdint.h>
#import "G9SyncDataEnvelope.h"

NS_ASSUME_NONNULL_BEGIN

/** 576 MiB: the largest output the contract accepts. */
#define G9SyncDataBrotliMaxExpectedLength 603979776

/** Error codes of the module contract (types/g9-syncdata-lynx-module.d.ts). Public API: add, never rename. */
FOUNDATION_EXPORT NSString *const G9SyncDataBrotliCodeArg;          // G9SYNC-BROTLI-ARG
FOUNDATION_EXPORT NSString *const G9SyncDataBrotliCodeInvalid;      // G9SYNC-BROTLI-INVALID
FOUNDATION_EXPORT NSString *const G9SyncDataBrotliCodeUnavailable;  // G9SYNC-BROTLI-UNAVAILABLE

/**
 * A raw Brotli decoder with `compression_decode_buffer`'s contract: writes at most `capacity` bytes to `destination`
 * and returns how many it wrote — `capacity` when the output does not fit — or -1 when the stream is invalid.
 * `source` is never NULL (an empty input points at a dummy byte).
 */
typedef NSInteger (*G9SyncDataBrotliDecoder)(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length);

/**
 * The Brotli half of the module contract, without the decoder itself: argument checks, the `expectedLength + 1`
 * output buffer (an overlong stream shows as one byte too many), the exact-length rule and the envelopes. Foundation
 * only, so it also builds with GNUstep; the module passes Apple's Compression framework as the decoder.
 */
@interface G9SyncDataBrotliCore : NSObject

/** `{ ok: true, value: { version, platform, brotli } }`. */
+ (NSDictionary *)capabilitiesWithVersion:(NSString *)version platform:(NSString *)platform brotli:(BOOL)brotli;

/** `nil` when the arguments are usable, else the `G9SYNC-BROTLI-ARG` envelope. */
+ (nullable NSDictionary *)validate:(nullable id)data expectedLength:(double)expectedLength;

/**
 * `{ ok: true, value: NSData of exactly expectedLength bytes }`, or `G9SYNC-BROTLI-ARG`, `G9SYNC-BROTLI-UNAVAILABLE`
 * (`decoder` is NULL) or `G9SYNC-BROTLI-INVALID`. Never a partial buffer.
 */
+ (NSDictionary *)decompress:(nullable id)data
              expectedLength:(double)expectedLength
                     decoder:(nullable G9SyncDataBrotliDecoder)decoder;

@end

NS_ASSUME_NONNULL_END

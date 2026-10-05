#import "G9SyncDataLynxModule.h"
#import "core/G9SyncDataBrotliCore.h"
#include <compression.h>

typedef void (^G9SyncDataReply)(id envelope);

static NSString *const G9SyncDataLynxVersion = @"1.2.0";

static NSInteger G9SyncDataAppleBrotli(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length)
    API_AVAILABLE(ios(15.0), macos(12.0));

/**
 * Apple's Brotli decoder in the core's decoder contract. `compression_decode_buffer` returns the bytes written
 * (`capacity` when the output does not fit) and 0 on error — so for `expectedLength >= 1` (capacity >= 2) a 0 is an
 * error. For `expectedLength == 0` a 0 is ambiguous (valid empty stream or error); the stream API tells the two apart:
 * COMPRESSION_STATUS_END only comes after the stream's final block.
 */
static NSInteger G9SyncDataAppleBrotli(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length) {
  if (capacity > 1) {
    size_t written = compression_decode_buffer(destination, capacity, source, length, NULL, COMPRESSION_BROTLI);
    return written == 0 ? -1 : (NSInteger)written;
  }
  compression_stream stream;
  if (compression_stream_init(&stream, COMPRESSION_STREAM_DECODE, COMPRESSION_BROTLI) != COMPRESSION_STATUS_OK) return -1;
  stream.src_ptr = source;
  stream.src_size = length;
  stream.dst_ptr = destination;
  stream.dst_size = capacity;
  compression_status status = compression_stream_process(&stream, COMPRESSION_STREAM_FINALIZE);
  size_t written = capacity - stream.dst_size;
  compression_stream_destroy(&stream);
  if (written > 0) return (NSInteger)written;  // longer than the (empty) output expected
  return status == COMPRESSION_STATUS_END ? 0 : -1;
}

/** The platform's decoder, or NULL before iOS 15 (no Brotli in the Compression framework). */
static G9SyncDataBrotliDecoder G9SyncDataPlatformDecoder(void) {
  if (@available(iOS 15.0, macOS 12.0, *)) {
    return G9SyncDataAppleBrotli;
  }
  return NULL;
}

static void G9SyncDataSend(id callback, NSDictionary *envelope) {
  G9SyncDataReply reply = (G9SyncDataReply)callback;
  if (reply) reply(envelope);
}

@implementation G9SyncDataLynxModule

+ (NSString *)name {
  return @"G9SyncDataLynxModule";
}

+ (NSDictionary<NSString *, NSString *> *)methodLookup {
  return @{
    @"capabilities" : NSStringFromSelector(@selector(capabilities:)),
    @"brotliDecompress" : NSStringFromSelector(@selector(brotliDecompress:expectedLength:callback:)),
  };
}

/** Decoding runs on one serial queue, off the Lynx JS thread. */
+ (dispatch_queue_t)decodeQueue {
  static dispatch_queue_t queue;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    queue = dispatch_queue_create("g9.syncdata.brotli", DISPATCH_QUEUE_SERIAL);
  });
  return queue;
}

- (void)capabilities:(id)callback {
  G9SyncDataSend(callback, [G9SyncDataBrotliCore capabilitiesWithVersion:G9SyncDataLynxVersion
                                                                platform:@"ios"
                                                                  brotli:G9SyncDataPlatformDecoder() != NULL]);
}

- (void)brotliDecompress:(id)data expectedLength:(double)expectedLength callback:(id)callback {
  G9SyncDataBrotliDecoder decoder = G9SyncDataPlatformDecoder();
  NSDictionary *invalid = [G9SyncDataBrotliCore validate:data expectedLength:expectedLength];
  if (invalid != nil || decoder == NULL) {
    // Argument errors and "no decoder" reply at once, like the Android module.
    G9SyncDataSend(callback, invalid ?: [G9SyncDataBrotliCore decompress:data expectedLength:expectedLength decoder:NULL]);
    return;
  }
  NSData *input = [(NSData *)data copy];  // immutable from here on (a no-op retain for NSData)
  dispatch_async([G9SyncDataLynxModule decodeQueue], ^{
    NSDictionary *envelope;
    @autoreleasepool {
      envelope = [G9SyncDataBrotliCore decompress:input expectedLength:expectedLength decoder:decoder];
    }
    G9SyncDataSend(callback, envelope);
  });
}

@end

#import "G9SyncDataLynxModule.h"
#import "core/G9SyncDataAppleBrotli.h"
#import "core/G9SyncDataBrotliCore.h"

typedef void (^G9SyncDataReply)(id envelope);

static NSString *const G9SyncDataLynxVersion = @"1.2.2";

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

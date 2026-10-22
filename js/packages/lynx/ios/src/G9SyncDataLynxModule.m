#import "G9SyncDataLynxModule.h"
#import "core/G9SyncDataAppleBrotli.h"
#import "core/G9SyncDataBrotliCore.h"
#import "core/G9SyncDataSnapshotFileCore.h"

typedef void (^G9SyncDataReply)(id envelope);

static NSString *const G9SyncDataLynxVersion = @"1.5.5";

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
    @"snapshotFileCreate" : NSStringFromSelector(@selector(snapshotFileCreate:size:callback:)),
    @"snapshotFileWriteSegment" :
        NSStringFromSelector(@selector(snapshotFileWriteSegment:offset:data:length:sha256:sync:callback:)),
    @"snapshotFileSize" : NSStringFromSelector(@selector(snapshotFileSize:callback:)),
    @"snapshotFileHead" : NSStringFromSelector(@selector(snapshotFileHead:length:callback:)),
    @"snapshotFileRemove" : NSStringFromSelector(@selector(snapshotFileRemove:callback:)),
    @"snapshotFileList" : NSStringFromSelector(@selector(snapshotFileList:callback:)),
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

/**
 * Snapshot-file work (ADR-0072) runs on its own serial queue: a segment's decode + SHA-256 + write never waits behind
 * a frame decode, and the writes of one file land in the order the sink sent them.
 */
+ (dispatch_queue_t)fileQueue {
  static dispatch_queue_t queue;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    queue = dispatch_queue_create("g9.syncdata.files", DISPATCH_QUEUE_SERIAL);
  });
  return queue;
}

/** Runs `work` on the file queue in an autorelease pool (a 1 MiB segment's buffers go at once) and replies from there. */
static void G9SyncDataOnFileQueue(id callback, NSDictionary * (^work)(void)) {
  dispatch_async([G9SyncDataLynxModule fileQueue], ^{
    NSDictionary *envelope;
    @autoreleasepool {
      envelope = work();
    }
    G9SyncDataSend(callback, envelope);
  });
}

- (void)capabilities:(id)callback {
  BOOL brotli = G9SyncDataPlatformDecoder() != NULL;
  NSDictionary *envelope = [G9SyncDataBrotliCore capabilitiesWithVersion:G9SyncDataLynxVersion
                                                                platform:@"ios"
                                                                  brotli:brotli
                                                           snapshotFiles:brotli];
  // Where staged snapshot files belong: Caches is app-private, outside the iCloud/iTunes backup, reclaimable by iOS.
  NSMutableDictionary *value = [[envelope objectForKey:@"value"] mutableCopy];
  NSString *caches = [NSSearchPathForDirectoriesInDomains(NSCachesDirectory, NSUserDomainMask, YES) firstObject];
  [value setObject:(caches ?: (id)[NSNull null]) forKey:@"cacheDirectory"];
  G9SyncDataSend(callback, G9SyncDataOk(value));
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

// ---------------------------------------------------------------- snapshot files (ADR-0072)

- (void)snapshotFileCreate:(NSString *)path size:(double)size callback:(id)callback {
  NSString *target = [path copy];
  G9SyncDataOnFileQueue(callback, ^{
    return [G9SyncDataSnapshotFileCore create:target size:size];
  });
}

- (void)snapshotFileWriteSegment:(NSString *)path
                          offset:(double)offset
                            data:(id)data
                          length:(double)length
                          sha256:(NSString *)sha256
                            sync:(BOOL)sync
                        callback:(id)callback {
  NSString *target = [path copy];
  NSString *hash = [sha256 copy];
  id input = [data isKindOfClass:[NSData class]] ? [(NSData *)data copy] : data;
  G9SyncDataBrotliDecoder decoder = G9SyncDataPlatformDecoder();
  G9SyncDataOnFileQueue(callback, ^{
    return [G9SyncDataSnapshotFileCore writeSegment:target
                                             offset:offset
                                               data:input
                                             length:length
                                             sha256:hash
                                               sync:sync
                                            decoder:decoder];
  });
}

- (void)snapshotFileSize:(NSString *)path callback:(id)callback {
  NSString *target = [path copy];
  G9SyncDataOnFileQueue(callback, ^{
    return [G9SyncDataSnapshotFileCore size:target];
  });
}

- (void)snapshotFileHead:(NSString *)path length:(double)length callback:(id)callback {
  NSString *target = [path copy];
  G9SyncDataOnFileQueue(callback, ^{
    return [G9SyncDataSnapshotFileCore head:target length:length];
  });
}

- (void)snapshotFileRemove:(NSString *)path callback:(id)callback {
  NSString *target = [path copy];
  G9SyncDataOnFileQueue(callback, ^{
    return [G9SyncDataSnapshotFileCore remove:target];
  });
}

- (void)snapshotFileList:(NSString *)directory callback:(id)callback {
  NSString *target = [directory copy];
  G9SyncDataOnFileQueue(callback, ^{
    return [G9SyncDataSnapshotFileCore list:target];
  });
}

@end

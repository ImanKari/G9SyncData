#import <Foundation/Foundation.h>
#import "G9SyncDataBrotliCore.h"

NS_ASSUME_NONNULL_BEGIN

/** 64 MiB: the largest segment the contract accepts (the server cuts 1 MiB). */
#define G9SyncDataSnapshotFileMaxSegment 67108864

/** Error codes of the module contract (types/g9-syncdata-lynx-module.d.ts). Public API: add, never rename. */
FOUNDATION_EXPORT NSString *const G9SyncDataFileCodeArg;      // G9SYNC-FILE-ARG
FOUNDATION_EXPORT NSString *const G9SyncDataFileCodeInvalid;  // G9SYNC-FILE-INVALID
FOUNDATION_EXPORT NSString *const G9SyncDataFileCodeIo;       // G9SYNC-FILE-IO

/**
 * The snapshot-file half of the module contract (ADR-0072): the device's G9SnapshotFileSink. A segment is
 * decompressed by G9SyncDataBrotliCore (exact length or an error, never a partial buffer), checked against its
 * SHA-256 and only then written at its offset — nothing reaches the file from a segment that fails. POSIX I/O and
 * Foundation only, so it builds with GNUstep too (ios/Tests/linux/main.m); every result is an envelope.
 */
@interface G9SyncDataSnapshotFileCore : NSObject

/** Creates `path` (its directory too) — or truncates it — as `size` zero bytes. Value: null. */
+ (NSDictionary *)create:(nullable NSString *)path size:(double)size;

/**
 * Decompresses `data` with `decoder`, checks `length` and `sha256` (64 lower-case hex characters) and writes the bytes
 * at `offset`; with `sync` the file reaches stable storage before the reply (F_FULLFSYNC where the OS has it).
 */
+ (NSDictionary *)writeSegment:(nullable NSString *)path
                        offset:(double)offset
                          data:(nullable id)data
                        length:(double)length
                        sha256:(nullable NSString *)sha256
                          sync:(BOOL)sync
                       decoder:(nullable G9SyncDataBrotliDecoder)decoder;

/** Value: the size in bytes (NSNumber), or null when `path` is not a regular file. */
+ (NSDictionary *)size:(nullable NSString *)path;

/** Value: NSData of the first `length` bytes (fewer when the file is shorter). */
+ (NSDictionary *)head:(nullable NSString *)path length:(double)length;

/** Deletes `path`; a missing file is not an error. Value: null. */
+ (NSDictionary *)remove:(nullable NSString *)path;

/** Value: the sorted full paths of the regular files in `directory` (empty when it does not exist). */
+ (NSDictionary *)list:(nullable NSString *)directory;

/** Lower-case hex SHA-256 of `data` (CommonCrypto on Apple, a portable implementation elsewhere). */
+ (NSString *)sha256Hex:(NSData *)data;

@end

NS_ASSUME_NONNULL_END

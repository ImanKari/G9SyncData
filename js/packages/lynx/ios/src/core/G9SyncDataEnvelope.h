#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

// Built with plain messages (no object literals, subscripting or blocks) so the core also compiles with clang +
// GNUstep base on Linux, where its suite runs (ios/Tests/linux/main.m).

/** `{ ok: true, value }` (types/g9-syncdata-lynx-module.d.ts). `nil` becomes NSNull. */
static inline NSDictionary *G9SyncDataOk(id _Nullable value) {
  return [NSDictionary dictionaryWithObjectsAndKeys:[NSNumber numberWithBool:YES], @"ok", (value ?: [NSNull null]), @"value", nil];
}

/** `{ ok: false, error: { code, message } }`. */
static inline NSDictionary *G9SyncDataFail(NSString *code, NSString *_Nullable message) {
  NSDictionary *error = [NSDictionary dictionaryWithObjectsAndKeys:code, @"code", (message ?: code), @"message", nil];
  return [NSDictionary dictionaryWithObjectsAndKeys:[NSNumber numberWithBool:NO], @"ok", error, @"error", nil];
}

NS_ASSUME_NONNULL_END

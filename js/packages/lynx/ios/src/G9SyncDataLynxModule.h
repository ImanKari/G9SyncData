#import <Foundation/Foundation.h>
#import <Lynx/LynxModule.h>
#import "generated/G9SyncDataLynxModuleSpec.h"

NS_ASSUME_NONNULL_BEGIN

// Lynx 4.0.3 (the iOS pods) has no self-registration: hosts register the module on their LynxConfig
// (`[config registerModule:G9SyncDataLynxModule.class]`), next to any other native module they use.

/**
 * `NativeModules.G9SyncDataLynxModule` on iOS (types/g9-syncdata-lynx-module.d.ts): Brotli decompression of the
 * G9SyncData frames with Apple's Compression framework (iOS 15+), and the snapshot-file sink (ADR-0072): a downloaded
 * segment is decompressed, checked against its SHA-256 and written into the staged file natively. Frame decodes and
 * file work each run on their own process-wide serial queue, never on the Lynx JS thread; replies come from there.
 */
@interface G9SyncDataLynxModule : NSObject <G9SyncDataLynxModuleSpec>
@end

NS_ASSUME_NONNULL_END

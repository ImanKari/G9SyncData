#import <Foundation/Foundation.h>
#import <Lynx/LynxModule.h>

NS_ASSUME_NONNULL_BEGIN

/**
 * `NativeModules.G9DeviceTestLog.line(text)`: one line of the device test to the process's stdout (what
 * `xcrun devicectl device process launch --console` shows on the Mac) and to Documents/g9sync-device-test.log.
 * The page calls it next to console.log, which on iOS goes to os_log only.
 */
@interface G9DeviceTestLogModule : NSObject <LynxModule>
/** The log file of this run (truncated when the app starts). */
+ (NSString *)logPath;
@end

NS_ASSUME_NONNULL_END

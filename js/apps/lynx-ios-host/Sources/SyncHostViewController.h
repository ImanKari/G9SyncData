#import <UIKit/UIKit.h>

/**
 * One LynxView running the device-test page (../lynx-android-host/src, the same bundle). The run is configured by the
 * launch arguments, handed to the page as global props (`lynx.__globalProps`), like the Android host's intent extras:
 *
 *   xcrun devicectl device process launch --console --device <udid> com.g9tm.syncdatahost \
 *     --server https://host/path --protocol messagepack --runId r1 --files on
 *
 * The test's SQLite files (Documents/g9sync-*) and staged snapshot files (Caches/g9sync-files) of a previous run are
 * deleted first, so every run starts empty.
 */
@interface SyncHostViewController : UIViewController
@end

#import "AppDelegate.h"
#import "G9HostHttpService.h"
#import "SyncHostViewController.h"

@implementation AppDelegate

- (BOOL)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)launchOptions {
  // Lynx `fetch` (the scenario's POST /sample/pots) needs an HTTP service; Lynx 4.0.3 ships none by default.
  [G9HostHttpService install];
  // The run must not be cut short by the screen locking.
  application.idleTimerDisabled = YES;
  self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
  self.window.rootViewController = [[SyncHostViewController alloc] init];
  [self.window makeKeyAndVisible];
  return YES;
}

@end

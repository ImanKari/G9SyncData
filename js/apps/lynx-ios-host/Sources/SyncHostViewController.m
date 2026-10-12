#import "SyncHostViewController.h"

#import <Lynx/LynxView.h>
#import <sys/utsname.h>

#import "G9DeviceTestLogModule.h"
#import "G9HostModule.h"
#import "G9SignalRLynxModule.h"
#import "G9SqliteModule.h"
#import "G9SyncDataLynxModule.h"
#import "G9TemplateProvider.h"

/** `--name value` from the launch arguments, else `fallback`. */
static NSString *Argument(NSArray<NSString *> *arguments, NSString *name, NSString *fallback) {
  NSUInteger at = [arguments indexOfObject:[@"--" stringByAppendingString:name]];
  return at != NSNotFound && at + 1 < arguments.count ? arguments[at + 1] : fallback;
}

@implementation SyncHostViewController {
  LynxView *_lynxView;
}

- (void)viewDidLoad {
  [super viewDidLoad];
  self.view.backgroundColor = UIColor.whiteColor;

  NSArray<NSString *> *arguments = NSProcessInfo.processInfo.arguments;
  NSString *server = Argument(arguments, @"server", @"http://127.0.0.1:5210");
  NSString *protocol = Argument(arguments, @"protocol", @"messagepack");
  NSString *runId = Argument(arguments, @"runId", [NSString stringWithFormat:@"%lld", (long long)NSDate.date.timeIntervalSince1970]);
  NSString *files = Argument(arguments, @"files", @"off");
  NSUInteger deleted = [self deleteOldTestFiles];
  [[NSFileManager defaultManager] removeItemAtPath:[G9DeviceTestLogModule logPath] error:nil];
  NSLog(@"G9SYNC-DEVICE host: server=%@ protocol=%@ files=%@ runId=%@ (deleted %lu old test files)", server, protocol, files, runId,
        (unsigned long)deleted);

  struct utsname machine;
  uname(&machine);
  NSString *device = [NSString stringWithFormat:@"Apple %s, %@ %@", machine.machine, UIDevice.currentDevice.systemName,
                                                UIDevice.currentDevice.systemVersion];

  CGSize size = self.view.bounds.size;
  _lynxView = [[LynxView alloc] initWithBuilderBlock:^(LynxViewBuilder *builder) {
    LynxConfig *config = [[LynxConfig alloc] initWithProvider:[[G9TemplateProvider alloc] init]];
    // Lynx 4.0.3 on iOS has no module autoregistration: every module the page uses is registered here.
    [config registerModule:G9SqliteModule.class];
    [config registerModule:G9HostModule.class];
    [config registerModule:G9SignalRLynxModule.class];
    [config registerModule:G9SyncDataLynxModule.class];
    [config registerModule:G9DeviceTestLogModule.class];
    builder.config = config;
    builder.screenSize = size;
    builder.fontScale = 1.0;
  }];
  _lynxView.frame = self.view.bounds;
  _lynxView.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
  _lynxView.preferredLayoutWidth = size.width;
  _lynxView.preferredLayoutHeight = size.height;
  _lynxView.layoutWidthMode = LynxViewSizeModeExact;
  _lynxView.layoutHeightMode = LynxViewSizeModeExact;
  [_lynxView updateGlobalPropsWithDictionary:@{
    @"g9Server" : server,
    @"g9Protocol" : protocol,
    @"g9RunId" : runId,
    @"g9Files" : files,
    @"g9LynxVersion" : @"4.0.3 (CocoaPods)",
    @"g9PrimjsVersion" : @"4.0.0",
    @"g9Device" : device,
  }];
  [self.view addSubview:_lynxView];
  [_lynxView loadTemplateFromURL:@"main.lynx.bundle" initData:nil];
}

/** The test's databases and staged snapshot files of earlier runs. */
- (NSUInteger)deleteOldTestFiles {
  NSFileManager *fm = NSFileManager.defaultManager;
  NSUInteger deleted = 0;
  NSString *documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
  NSString *support = NSSearchPathForDirectoriesInDomains(NSApplicationSupportDirectory, NSUserDomainMask, YES).firstObject;
  for (NSString *folder in @[ documents, support ]) {
    for (NSString *name in [fm contentsOfDirectoryAtPath:folder error:nil]) {
      if ([name hasPrefix:@"g9sync-"] && ![name isEqualToString:@"g9sync-device-test.log"]) {
        if ([fm removeItemAtPath:[folder stringByAppendingPathComponent:name] error:nil]) deleted++;
      }
    }
  }
  NSString *caches = NSSearchPathForDirectoriesInDomains(NSCachesDirectory, NSUserDomainMask, YES).firstObject;
  [fm removeItemAtPath:[caches stringByAppendingPathComponent:@"g9sync-files"] error:nil];
  return deleted;
}

@end

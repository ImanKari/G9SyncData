#import "G9DeviceTestLogModule.h"

@implementation G9DeviceTestLogModule

+ (NSString *)name {
  return @"G9DeviceTestLog";
}

+ (NSDictionary<NSString *, NSString *> *)methodLookup {
  return @{@"line" : NSStringFromSelector(@selector(line:))};
}

+ (NSString *)logPath {
  NSString *documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
  return [documents stringByAppendingPathComponent:@"g9sync-device-test.log"];
}

/** Serial: lines keep their order on stdout and in the file. */
+ (dispatch_queue_t)queue {
  static dispatch_queue_t queue;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    queue = dispatch_queue_create("g9.synchost.log", DISPATCH_QUEUE_SERIAL);
  });
  return queue;
}

- (void)line:(NSString *)text {
  NSString *line = [NSString stringWithFormat:@"G9SYNC-DEVICE %@\n", text ?: @""];
  dispatch_async([G9DeviceTestLogModule queue], ^{
    fputs(line.UTF8String, stdout);
    fflush(stdout);
    NSFileHandle *file = [NSFileHandle fileHandleForWritingAtPath:[G9DeviceTestLogModule logPath]];
    if (file == nil) {
      [[NSFileManager defaultManager] createFileAtPath:[G9DeviceTestLogModule logPath] contents:nil attributes:nil];
      file = [NSFileHandle fileHandleForWritingAtPath:[G9DeviceTestLogModule logPath]];
    }
    [file seekToEndOfFile];
    [file writeData:[line dataUsingEncoding:NSUTF8StringEncoding]];
    [file closeFile];
  });
}

@end

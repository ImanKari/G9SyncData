#import <Foundation/Foundation.h>
#import <Lynx/LynxServiceHttpProtocol.h>

NS_ASSUME_NONNULL_BEGIN

/**
 * Lynx `fetch` over NSURLSession for the test page (the scenario's POST /sample/pots). Status 499 + the error text when
 * the request cannot be made; streaming hands the whole body over as one chunk. The sync itself never uses fetch: it
 * runs on G9SignalRLynxModule's WebSocket.
 */
@interface G9HostHttpService : NSObject <LynxServiceHttpProtocol>
/** Registers the service with Lynx (before the first LynxView). */
+ (void)install;
@end

NS_ASSUME_NONNULL_END

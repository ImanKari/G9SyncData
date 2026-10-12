#import "G9HostHttpService.h"

#import <Lynx/LynxHttpRequest.h>
#import <Lynx/LynxHttpStreamingDelegate.h>
#import <LynxServiceAPI/ServiceAPI.h>

static const NSInteger kFailedInternally = 499;

@implementation G9HostHttpService

+ (void)install {
  [LynxServices registerServiceWithProtocol:G9HostHttpService.class protocol:@protocol(LynxServiceHttpProtocol)];
}

+ (instancetype)sharedInstance {
  static G9HostHttpService *instance;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    instance = [[G9HostHttpService alloc] init];
  });
  return instance;
}

- (void)invokeWithRequest:(LynxHttpRequest *)request callback:(LynxHttpCallback)callback {
  [self send:request callback:callback delegate:nil];
}

- (void)invokeStreamingWithRequest:(LynxHttpRequest *)request
                          callback:(LynxHttpCallback)callback
                      withDelegate:(LynxHttpStreamingDelegate *)delegate {
  [self send:request callback:callback delegate:delegate];
}

- (BOOL)setHttpInterceptor:(id<LynxHttpInterceptor>)interceptor {
  return NO;
}

- (void)send:(LynxHttpRequest *)request callback:(LynxHttpCallback)callback delegate:(nullable LynxHttpStreamingDelegate *)delegate {
  LynxHttpResponse *response = [[LynxHttpResponse alloc] init];
  response.url = request.url;
  response.statusCode = kFailedInternally;
  NSURL *url = request.url.length ? [NSURL URLWithString:request.url] : nil;
  if (url == nil) {
    response.statusText = [NSString stringWithFormat:@"invalid url: %@", request.url];
    callback(response);
    return;
  }
  NSMutableURLRequest *r = [NSMutableURLRequest requestWithURL:url];
  NSString *method = request.httpMethod.length ? request.httpMethod.uppercaseString : @"GET";
  r.HTTPMethod = method;
  if (![method isEqualToString:@"GET"] && ![method isEqualToString:@"HEAD"]) r.HTTPBody = request.httpBody ?: [NSData data];
  [request.httpHeaders enumerateKeysAndObjectsUsingBlock:^(NSString *key, NSString *value, BOOL *stop) {
    [r setValue:[value description] forHTTPHeaderField:key];
  }];
  [[NSURLSession.sharedSession dataTaskWithRequest:r
                                 completionHandler:^(NSData *data, NSURLResponse *res, NSError *error) {
                                   if (error != nil || ![res isKindOfClass:NSHTTPURLResponse.class]) {
                                     response.statusText = error.localizedDescription ?: @"no response";
                                     callback(response);
                                     if (delegate) [delegate onError:response.statusText];
                                     return;
                                   }
                                   NSHTTPURLResponse *http = (NSHTTPURLResponse *)res;
                                   response.statusCode = http.statusCode;
                                   response.statusText = [NSHTTPURLResponse localizedStringForStatusCode:http.statusCode];
                                   response.httpHeaders = http.allHeaderFields;
                                   if (delegate == nil) {
                                     response.httpBody = data ?: [NSData data];
                                     callback(response);
                                     return;
                                   }
                                   callback(response);
                                   if (data.length) [delegate onData:data];
                                   [delegate onEnd];
                                 }] resume];
}

@end

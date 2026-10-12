#import "G9TemplateProvider.h"

@implementation G9TemplateProvider

- (void)loadTemplateWithUrl:(NSString *)url onComplete:(LynxTemplateLoadBlock)callback {
  dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
    NSString *path = [[NSBundle mainBundle] pathForResource:[url stringByDeletingPathExtension] ofType:url.pathExtension];
    NSData *data = path != nil ? [NSData dataWithContentsOfFile:path] : nil;
    NSError *error = data != nil ? nil : [NSError errorWithDomain:@"g9.synchost" code:404 userInfo:@{NSLocalizedDescriptionKey : url}];
    callback(data, error);
  });
}

@end

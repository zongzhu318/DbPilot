#import <Cocoa/Cocoa.h>
#import <Sparkle/Sparkle.h>

typedef int (*CanShutdown)(void);
static CanShutdown canShutdown;
@interface DbPilotUpdaterDelegate : NSObject <SPUUpdaterDelegate>
@end
@implementation DbPilotUpdaterDelegate
- (BOOL)updater:(SPUUpdater *)updater shouldPostponeRelaunchForUpdate:(SUAppcastItem *)item untilInvokingBlock:(void (^)(void))installHandler {
    if (!canShutdown || canShutdown()) return NO;
    NSAlert *alert = [NSAlert new];
    alert.messageText = @"请先保存工作";
    alert.informativeText = @"更新已就绪。请保存 SQL、提交或撤销数据改动并停止任务，更新将随后继续。";
    [alert runModal];
    [NSTimer scheduledTimerWithTimeInterval:2 repeats:YES block:^(NSTimer *timer) {
        if (canShutdown()) { [timer invalidate]; installHandler(); }
    }];
    return YES;
}
@end
static SPUStandardUpdaterController *controller;
static DbPilotUpdaterDelegate *delegate;
__attribute__((visibility("default"))) void dbpilot_updater_start(CanShutdown callback) {
    canShutdown = callback;
    delegate = [DbPilotUpdaterDelegate new];
    controller = [[SPUStandardUpdaterController alloc] initWithStartingUpdater:YES updaterDelegate:delegate userDriverDelegate:nil];
}
__attribute__((visibility("default"))) void dbpilot_updater_check(void) {
    [controller checkForUpdates:nil];
}

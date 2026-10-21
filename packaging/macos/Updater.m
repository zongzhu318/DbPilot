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
static NSTimer *raiseTimer;
static void RaiseSparkleWindows(void) {
    for (NSWindow *window in [NSApp windows]) {
        if (!window.isVisible || window.isMiniaturized) continue;
        NSString *name = NSStringFromClass([[window windowController] class]);
        // Avalonia 主窗口会在弹窗出现后重新置前，把 Sparkle 的窗口压在后面，这里强制拉回前台
        if ([name hasPrefix:@"SU"] || [name hasPrefix:@"SPU"]) {
            [window makeKeyAndOrderFront:nil];
        }
    }
}
__attribute__((visibility("default"))) void dbpilot_updater_start(CanShutdown callback) {
    canShutdown = callback;
    delegate = [DbPilotUpdaterDelegate new];
    controller = [[SPUStandardUpdaterController alloc] initWithStartingUpdater:YES updaterDelegate:delegate userDriverDelegate:nil];
}
__attribute__((visibility("default"))) void dbpilot_updater_check(void) {
    [controller checkForUpdates:nil];
    if (raiseTimer.isValid) [raiseTimer invalidate];
    __block NSInteger ticks = 0;
    raiseTimer = [NSTimer scheduledTimerWithTimeInterval:0.25 repeats:YES block:^(NSTimer *timer) {
        if (++ticks > 80) { [timer invalidate]; return; }
        RaiseSparkleWindows();
    }];
}

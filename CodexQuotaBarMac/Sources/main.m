#import <Cocoa/Cocoa.h>
#import <CoreImage/CoreImage.h>

static void CQLog(NSString *message) {
    NSString *path = [NSTemporaryDirectory() stringByAppendingPathComponent:@"CodexQuotaBar.log"];
    NSString *line = [NSString stringWithFormat:@"%@ %@\n", NSDate.date, message];
    NSData *data = [line dataUsingEncoding:NSUTF8StringEncoding];
    if (![NSFileManager.defaultManager fileExistsAtPath:path]) [data writeToFile:path atomically:YES];
    else {
        NSFileHandle *handle = [NSFileHandle fileHandleForWritingAtPath:path];
        [handle seekToEndOfFile]; [handle writeData:data]; [handle closeFile];
    }
}

@interface CQQuotaWindow : NSObject
@property NSInteger remaining;
@property BOOL known;
@property(nonatomic, strong, nullable) NSDate *resetsAt;
@end
@implementation CQQuotaWindow
@end

@interface CQQuotaSnapshot : NSObject
@property(nonatomic, strong) CQQuotaWindow *fiveHour;
@property(nonatomic, strong) CQQuotaWindow *weekly;
@property(nonatomic, strong) NSDate *fetchedAt;
@end
@implementation CQQuotaSnapshot
@end

@interface CQAppServerClient : NSObject
@property(nonatomic, strong) NSTask *task;
@property(nonatomic, strong) NSPipe *input;
@property(nonatomic, strong) NSPipe *output;
@property(nonatomic, strong) NSMutableData *buffer;
@property NSInteger nextID;
- (CQQuotaSnapshot *_Nullable)readQuota:(NSError **)error;
@end

@implementation CQAppServerClient

- (instancetype)init {
    if ((self = [super init])) {
        _input = [NSPipe pipe];
        _output = [NSPipe pipe];
        _buffer = [NSMutableData data];
    }
    return self;
}

+ (NSString *_Nullable)findCodexExecutable {
    NSFileManager *manager = NSFileManager.defaultManager;
    NSString *explicitPath = NSProcessInfo.processInfo.environment[@"CODEX_CLI_PATH"];
    if (explicitPath.length && [manager isExecutableFileAtPath:explicitPath]) return explicitPath;

    for (NSString *path in @[@"/Applications/ChatGPT.app/Contents/Resources/codex",
                             @"/opt/homebrew/bin/codex", @"/usr/local/bin/codex"]) {
        if ([manager isExecutableFileAtPath:path]) return path;
    }

    NSString *root = [NSHomeDirectory() stringByAppendingPathComponent:@".vscode/extensions"];
    NSArray<NSString *> *directories = [[manager contentsOfDirectoryAtPath:root error:nil]
        sortedArrayUsingSelector:@selector(compare:)];
    for (NSString *directory in directories.reverseObjectEnumerator) {
        if (![directory hasPrefix:@"openai.chatgpt-"]) continue;
        for (NSString *architecture in @[@"darwin-aarch64", @"darwin-x86_64"]) {
            NSString *path = [root stringByAppendingPathComponent:
                [NSString stringWithFormat:@"%@/bin/%@/codex", directory, architecture]];
            if ([manager isExecutableFileAtPath:path]) return path;
        }
    }
    return nil;
}

- (BOOL)send:(NSDictionary *)payload error:(NSError **)error {
    NSData *json = [NSJSONSerialization dataWithJSONObject:payload options:0 error:error];
    if (!json) return NO;
    NSMutableData *line = [json mutableCopy];
    uint8_t newline = '\n';
    [line appendBytes:&newline length:1];
    @try {
        [self.input.fileHandleForWriting writeData:line];
        return YES;
    } @catch (NSException *exception) {
        if (error) *error = [NSError errorWithDomain:@"CodexQuotaBar" code:2
            userInfo:@{NSLocalizedDescriptionKey: exception.reason ?: @"无法写入 App Server"}];
        return NO;
    }
}

- (NSDictionary *_Nullable)readMessage:(NSError **)error {
    while (YES) {
        const uint8_t *bytes = self.buffer.bytes;
        for (NSUInteger index = 0; index < self.buffer.length; index++) {
            if (bytes[index] != '\n') continue;
            NSData *line = [self.buffer subdataWithRange:NSMakeRange(0, index)];
            [self.buffer replaceBytesInRange:NSMakeRange(0, index + 1) withBytes:NULL length:0];
            if (!line.length) break;
            id object = [NSJSONSerialization JSONObjectWithData:line options:0 error:error];
            return [object isKindOfClass:NSDictionary.class] ? object : nil;
        }
        NSData *chunk = self.output.fileHandleForReading.availableData;
        if (!chunk.length) return nil;
        [self.buffer appendData:chunk];
    }
}

- (NSDictionary *_Nullable)request:(NSString *)method params:(NSDictionary *)params error:(NSError **)error {
    NSInteger requestID = ++self.nextID;
    if (![self send:@{@"id": @(requestID), @"method": method, @"params": params} error:error]) return nil;
    while (YES) {
        NSDictionary *message = [self readMessage:error];
        if (!message) {
            if (error && !*error) *error = [NSError errorWithDomain:@"CodexQuotaBar" code:6
                userInfo:@{NSLocalizedDescriptionKey: @"Codex App Server 未返回数据或读取超时。"}];
            return nil;
        }
        if ([message[@"id"] integerValue] != requestID) continue;
        NSDictionary *serverError = message[@"error"];
        if (serverError) {
            if (error) *error = [NSError errorWithDomain:@"CodexQuotaBar" code:3 userInfo:@{
                NSLocalizedDescriptionKey: [NSString stringWithFormat:@"Codex App Server：%@",
                    serverError[@"message"] ?: @"未知错误"]}];
            return nil;
        }
        NSDictionary *result = message[@"result"];
        if ([result isKindOfClass:NSDictionary.class]) return result;
        if (error) *error = [NSError errorWithDomain:@"CodexQuotaBar" code:4
            userInfo:@{NSLocalizedDescriptionKey: @"Codex App Server 返回了无效响应。"}];
        return nil;
    }
}

+ (CQQuotaWindow *)windowFrom:(NSDictionary *)value {
    CQQuotaWindow *window = [CQQuotaWindow new];
    NSNumber *used = value[@"usedPercent"];
    if ([used isKindOfClass:NSNumber.class]) {
        window.known = YES;
        window.remaining = MAX(0, MIN(100, 100 - used.integerValue));
    }
    NSNumber *reset = value[@"resetsAt"];
    if ([reset isKindOfClass:NSNumber.class]) window.resetsAt = [NSDate dateWithTimeIntervalSince1970:reset.doubleValue];
    return window;
}

+ (CQQuotaSnapshot *_Nullable)mapResult:(NSDictionary *)result error:(NSError **)error {
    NSDictionary *snapshot = nil;
    NSDictionary *byID = result[@"rateLimitsByLimitId"];
    if ([byID isKindOfClass:NSDictionary.class]) {
        snapshot = byID[@"codex"];
        if (![snapshot isKindOfClass:NSDictionary.class]) {
            for (id value in byID.allValues) {
                if ([value isKindOfClass:NSDictionary.class] && (value[@"primary"] || value[@"secondary"])) {
                    snapshot = value;
                    break;
                }
            }
        }
    }
    if (!snapshot) snapshot = result[@"rateLimits"];
    if (![snapshot isKindOfClass:NSDictionary.class]) {
        if (error) *error = [NSError errorWithDomain:@"CodexQuotaBar" code:5
            userInfo:@{NSLocalizedDescriptionKey: @"额度响应中没有 rateLimits 数据。"}];
        return nil;
    }

    NSMutableArray<NSDictionary *> *windows = [NSMutableArray array];
    for (NSString *key in @[@"primary", @"secondary"]) {
        NSDictionary *value = snapshot[key];
        if (![value isKindOfClass:NSDictionary.class]) continue;
        [windows addObject:@{@"window": [self windowFrom:value], @"duration": value[@"windowDurationMins"] ?: NSNull.null}];
    }

    CQQuotaWindow *five = nil, *week = nil;
    for (NSDictionary *entry in windows) {
        NSNumber *duration = entry[@"duration"];
        if (![duration isKindOfClass:NSNumber.class]) continue;
        if (duration.integerValue >= 240 && duration.integerValue <= 360) five = entry[@"window"];
        if (duration.integerValue >= 9000 && duration.integerValue <= 11000) week = entry[@"window"];
    }
    if (!five) five = windows.firstObject[@"window"] ?: [CQQuotaWindow new];
    if (!week) week = (windows.count > 1 ? windows[1][@"window"] : nil) ?: [CQQuotaWindow new];

    CQQuotaSnapshot *mapped = [CQQuotaSnapshot new];
    mapped.fiveHour = five;
    mapped.weekly = week;
    mapped.fetchedAt = NSDate.date;
    return mapped;
}

- (CQQuotaSnapshot *_Nullable)readQuota:(NSError **)error {
    NSString *executable = [CQAppServerClient findCodexExecutable];
    if (!executable) {
        if (error) *error = [NSError errorWithDomain:@"CodexQuotaBar" code:1 userInfo:@{
            NSLocalizedDescriptionKey: @"未找到本机 Codex 组件。请安装 ChatGPT、Codex CLI 或 VS Code Codex 扩展并登录。"}];
        return nil;
    }
    self.task = [NSTask new];
    self.task.executableURL = [NSURL fileURLWithPath:executable];
    self.task.arguments = @[@"app-server", @"--stdio"];
    self.task.standardInput = self.input;
    self.task.standardOutput = self.output;
    self.task.standardError = [NSPipe pipe];
    if (![self.task launchAndReturnError:error]) return nil;
    NSTask *runningTask = self.task;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC),
                   dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        if (runningTask.running) [runningTask terminate];
    });

    NSDictionary *initialize = [self request:@"initialize" params:@{
        @"clientInfo": @{@"name": @"codex_quota_bar", @"title": @"Codex Quota Bar", @"version": @"1.0.4-macos"},
        @"capabilities": @{@"experimentalApi": @YES}
    } error:error];
    if (!initialize || ![self send:@{@"method": @"initialized"} error:error]) {
        [self.task terminate];
        return nil;
    }
    NSDictionary *result = [self request:@"account/rateLimits/read" params:@{} error:error];
    [self.input.fileHandleForWriting closeFile];
    if (self.task.running) [self.task terminate];
    return result ? [CQAppServerClient mapResult:result error:error] : nil;
}
@end

@interface CQQuotaRow : NSView
@property(nonatomic, strong) NSTextField *percent;
@property(nonatomic, strong) NSTextField *reset;
@property(nonatomic, strong) NSProgressIndicator *progress;
- (instancetype)initWithLabel:(NSString *)label;
- (void)render:(CQQuotaWindow *)window;
@end

@implementation CQQuotaRow
- (instancetype)initWithLabel:(NSString *)label {
    if (!(self = [super initWithFrame:NSZeroRect])) return nil;
    self.translatesAutoresizingMaskIntoConstraints = NO;
    NSTextField *name = [NSTextField labelWithString:label];
    name.font = [NSFont boldSystemFontOfSize:11];
    name.textColor = [NSColor colorWithWhite:.85 alpha:1];
    _percent = [NSTextField labelWithString:@"--%"];
    _percent.font = [NSFont boldSystemFontOfSize:12];
    _reset = [NSTextField labelWithString:@""];
    _reset.font = [NSFont systemFontOfSize:10];
    _reset.textColor = [NSColor colorWithWhite:.62 alpha:1];
    _reset.alignment = NSTextAlignmentRight;
    _progress = [NSProgressIndicator new];
    _progress.indeterminate = NO;
    _progress.minValue = 0; _progress.maxValue = 100;
    for (NSView *view in @[name, _percent, _reset, _progress]) {
        view.translatesAutoresizingMaskIntoConstraints = NO;
        [self addSubview:view];
    }
    [NSLayoutConstraint activateConstraints:@[
        [name.leadingAnchor constraintEqualToAnchor:self.leadingAnchor],
        [name.topAnchor constraintEqualToAnchor:self.topAnchor],
        [name.widthAnchor constraintEqualToConstant:28],
        [_percent.leadingAnchor constraintEqualToAnchor:name.trailingAnchor],
        [_percent.topAnchor constraintEqualToAnchor:self.topAnchor],
        [_percent.widthAnchor constraintEqualToConstant:44],
        [_reset.leadingAnchor constraintEqualToAnchor:_percent.trailingAnchor],
        [_reset.trailingAnchor constraintEqualToAnchor:self.trailingAnchor],
        [_reset.topAnchor constraintEqualToAnchor:self.topAnchor],
        [_progress.leadingAnchor constraintEqualToAnchor:self.leadingAnchor],
        [_progress.trailingAnchor constraintEqualToAnchor:self.trailingAnchor],
        [_progress.topAnchor constraintEqualToAnchor:name.bottomAnchor constant:6],
        [_progress.heightAnchor constraintEqualToConstant:8],
        [self.bottomAnchor constraintEqualToAnchor:_progress.bottomAnchor]
    ]];
    return self;
}

+ (NSString *)resetText:(NSDate *)date {
    if (!date) return @"重置时间未知";
    if ([date timeIntervalSinceNow] <= 0) return @"即将刷新";
    NSDateFormatter *formatter = [NSDateFormatter new];
    formatter.locale = [NSLocale localeWithLocaleIdentifier:@"zh_CN"];
    formatter.dateFormat = [[NSCalendar currentCalendar] isDateInToday:date] ? @"重置 HH:mm" : @"重置 MM-dd HH:mm";
    return [formatter stringFromDate:date];
}

- (void)render:(CQQuotaWindow *)window {
    self.percent.stringValue = window.known ? [NSString stringWithFormat:@"%ld%%", (long)window.remaining] : @"--%";
    self.progress.doubleValue = window.known ? window.remaining : 0;
    NSColor *color;
    if (window.remaining >= 51) color = [NSColor colorWithRed:.21 green:.78 blue:.47 alpha:1];
    else if (window.remaining >= 21) color = [NSColor colorWithRed:.96 green:.77 blue:.31 alpha:1];
    else if (window.remaining >= 1) color = [NSColor colorWithRed:.94 green:.36 blue:.37 alpha:1];
    else color = [NSColor colorWithWhite:.5 alpha:1];
    self.percent.textColor = color;
    CIFilter *filter = [CIFilter filterWithName:@"CIFalseColor"];
    [filter setDefaults];
    [filter setValue:[[CIColor alloc] initWithColor:color] forKey:@"inputColor0"];
    [filter setValue:[[CIColor alloc] initWithColor:[NSColor colorWithWhite:.2 alpha:1]] forKey:@"inputColor1"];
    self.progress.contentFilters = @[filter];
    self.reset.stringValue = [CQQuotaRow resetText:window.resetsAt];
}
@end

@interface CQAppDelegate : NSObject <NSApplicationDelegate>
@property(nonatomic, strong) NSWindow *window;
@property(nonatomic, strong) CQQuotaRow *fiveHour;
@property(nonatomic, strong) CQQuotaRow *weekly;
@property(nonatomic, strong) NSTextField *updated;
@property(nonatomic, strong) NSTextField *message;
@property BOOL refreshing;
@end

@implementation CQAppDelegate
- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    CQLog(@"applicationDidFinishLaunching");
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    NSString *iconPath = [NSBundle.mainBundle pathForResource:@"AppIcon" ofType:@"icns"];
    if (iconPath) NSApp.applicationIconImage = [[NSImage alloc] initWithContentsOfFile:iconPath];
    [self buildWindow];
    [NSTimer scheduledTimerWithTimeInterval:60 repeats:YES block:^(__unused NSTimer *timer) { [self refresh]; }];
    [self refresh];
    [NSApp activateIgnoringOtherApps:YES];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender { return YES; }

- (NSButton *)button:(NSString *)title action:(SEL)action {
    NSButton *button = [NSButton buttonWithTitle:title target:self action:action];
    button.bordered = NO;
    button.font = [NSFont systemFontOfSize:[title isEqual:@"↻"] ? 16 : 14];
    button.contentTintColor = [NSColor colorWithWhite:.87 alpha:1];
    button.translatesAutoresizingMaskIntoConstraints = NO;
    [[button.widthAnchor constraintEqualToConstant:25] setActive:YES];
    [[button.heightAnchor constraintEqualToConstant:25] setActive:YES];
    return button;
}

- (void)buildWindow {
    CQLog(@"buildWindow begin");
    NSSize size = NSMakeSize(230, 146);
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, size.width, size.height)
        styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"Codex Quota Bar";
    self.window.level = NSFloatingWindowLevel;
    self.window.opaque = NO;
    self.window.backgroundColor = NSColor.clearColor;
    self.window.hasShadow = YES;
    self.window.movableByWindowBackground = YES;
    self.window.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorFullScreenAuxiliary;

    NSVisualEffectView *panel = [NSVisualEffectView new];
    panel.material = NSVisualEffectMaterialHUDWindow;
    panel.state = NSVisualEffectStateActive;
    panel.wantsLayer = YES;
    panel.layer.cornerRadius = 10;
    panel.layer.masksToBounds = YES;
    panel.translatesAutoresizingMaskIntoConstraints = NO;

    NSButton *refresh = [self button:@"↻" action:@selector(refresh)];
    NSButton *pin = [self button:@"◉" action:@selector(toggleFloating:)];
    NSButton *close = [self button:@"×" action:@selector(closeClicked)];
    NSStackView *controls = [NSStackView stackViewWithViews:@[refresh, pin, close]];
    controls.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    controls.spacing = 1;
    controls.translatesAutoresizingMaskIntoConstraints = NO;
    self.fiveHour = [[CQQuotaRow alloc] initWithLabel:@"5h"];
    self.weekly = [[CQQuotaRow alloc] initWithLabel:@"周"];
    self.updated = [NSTextField labelWithString:@"等待刷新"];
    self.updated.font = [NSFont systemFontOfSize:9];
    self.updated.textColor = [NSColor colorWithWhite:.5 alpha:1];
    self.updated.alignment = NSTextAlignmentRight;
    self.updated.translatesAutoresizingMaskIntoConstraints = NO;
    self.message = [NSTextField wrappingLabelWithString:@""];
    self.message.font = [NSFont systemFontOfSize:9];
    self.message.textColor = [NSColor colorWithRed:1 green:.72 blue:.42 alpha:1];
    self.message.maximumNumberOfLines = 2;
    self.message.hidden = YES;
    self.message.translatesAutoresizingMaskIntoConstraints = NO;

    NSView *content = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, size.width, size.height)];
    [content addSubview:panel];
    for (NSView *view in @[controls, self.fiveHour, self.weekly, self.updated, self.message]) [panel addSubview:view];
    self.window.contentView = content;
    [NSLayoutConstraint activateConstraints:@[
        [panel.leadingAnchor constraintEqualToAnchor:content.leadingAnchor], [panel.trailingAnchor constraintEqualToAnchor:content.trailingAnchor],
        [panel.topAnchor constraintEqualToAnchor:content.topAnchor], [panel.bottomAnchor constraintEqualToAnchor:content.bottomAnchor],
        [controls.trailingAnchor constraintEqualToAnchor:panel.trailingAnchor constant:-7], [controls.topAnchor constraintEqualToAnchor:panel.topAnchor constant:4],
        [self.fiveHour.leadingAnchor constraintEqualToAnchor:panel.leadingAnchor constant:12], [self.fiveHour.trailingAnchor constraintEqualToAnchor:panel.trailingAnchor constant:-10],
        [self.fiveHour.topAnchor constraintEqualToAnchor:panel.topAnchor constant:36],
        [self.weekly.leadingAnchor constraintEqualToAnchor:self.fiveHour.leadingAnchor], [self.weekly.trailingAnchor constraintEqualToAnchor:self.fiveHour.trailingAnchor],
        [self.weekly.topAnchor constraintEqualToAnchor:self.fiveHour.bottomAnchor constant:12],
        [self.updated.trailingAnchor constraintEqualToAnchor:panel.trailingAnchor constant:-10], [self.updated.bottomAnchor constraintEqualToAnchor:panel.bottomAnchor constant:-5],
        [self.message.leadingAnchor constraintEqualToAnchor:panel.leadingAnchor constant:12], [self.message.trailingAnchor constraintEqualToAnchor:panel.trailingAnchor constant:-10],
        [self.message.bottomAnchor constraintEqualToAnchor:panel.bottomAnchor constant:-5]
    ]];
    NSRect visible = NSScreen.mainScreen.visibleFrame;
    [self.window setFrameOrigin:NSMakePoint(NSMaxX(visible) - size.width - 12, NSMinY(visible) + 12)];
    [self.window makeKeyAndOrderFront:nil];
    CQLog([NSString stringWithFormat:@"buildWindow visible=%d frame=%@", self.window.visible, NSStringFromRect(self.window.frame)]);
}

- (void)refresh {
    if (self.refreshing) return;
    self.refreshing = YES;
    self.message.hidden = YES;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        NSError *error = nil;
        CQQuotaSnapshot *snapshot = [[[CQAppServerClient alloc] init] readQuota:&error];
        CQLog(snapshot ? @"quota refresh succeeded" : [NSString stringWithFormat:@"quota refresh failed: %@", error]);
        dispatch_async(dispatch_get_main_queue(), ^{
            self.refreshing = NO;
            if (snapshot) {
                [self.fiveHour render:snapshot.fiveHour];
                [self.weekly render:snapshot.weekly];
                NSDateFormatter *formatter = [NSDateFormatter new]; formatter.dateFormat = @"HH:mm:ss";
                self.updated.stringValue = [NSString stringWithFormat:@"更新 %@", [formatter stringFromDate:snapshot.fetchedAt]];
                self.updated.hidden = NO;
            } else {
                self.message.stringValue = error.localizedDescription ?: @"刷新失败";
                self.message.hidden = NO;
                self.updated.stringValue = @"刷新失败";
            }
        });
    });
}
- (void)toggleFloating:(NSButton *)sender {
    BOOL floating = self.window.level != NSFloatingWindowLevel;
    self.window.level = floating ? NSFloatingWindowLevel : NSNormalWindowLevel;
    sender.title = floating ? @"◉" : @"○";
}
- (void)closeClicked { [NSApp terminate:nil]; }
@end

static CQAppDelegate *appDelegate;

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        CQLog(@"main begin");
        NSApplication *application = NSApplication.sharedApplication;
        appDelegate = [CQAppDelegate new];
        application.delegate = appDelegate;
        [application run];
    }
    return 0;
}

//
//  AdviversePlayable.mm
//  Adviverse Ads for Unity — iOS native playable presenter.
//
//  A Unity iOS build has no browser, so a playable (interactive HTML5) creative
//  is hosted in a native WKWebView presented full-screen over the Unity view.
//  The bundle is untrusted third-party code, so it loads from the CDN origin and
//  talks to us ONLY through the injected runtime, which posts to the
//  `adviversePlayable` WKScriptMessageHandler installed here. We translate those
//  messages into the SAME impression / click / reward the WebGL and mobile SDKs
//  use, then forward them to C# via UnitySendMessage — mirroring
//  AdviversePlayableViewController.swift in the mobile SDK.
//
//  Bridge protocol (from the bundle runtime, identical everywhere):
//    { __advPlayable: 1, type: "ready" | "click" | "finish" | "close" }
//  forwarded to Unity as: UnitySendMessage(objName, "OnPlayableMessage", type)
//

#import <UIKit/UIKit.h>
#import <WebKit/WebKit.h>

// Provided by the Unity iOS runtime (declared with C linkage in UnityInterface.h).
extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);
extern "C" UIViewController* UnityGetGLViewController();

static void AdvSend(NSString* objName, NSString* type) {
    if (objName == nil || type == nil) return;
    UnitySendMessage([objName UTF8String], "OnPlayableMessage", [type UTF8String]);
}

@interface AdviversePlayableVC : UIViewController <WKScriptMessageHandler, WKNavigationDelegate>
@property (nonatomic, strong) WKWebView* webView;
@property (nonatomic, strong) UIButton* closeButton;
@property (nonatomic, copy)   NSString* objName;
@property (nonatomic, copy)   NSString* assetURL;
@property (nonatomic, assign) BOOL rewarded;
- (void)teardown;
@end

// One playable at a time; held strongly so it survives until we dismiss it.
static AdviversePlayableVC* sPlayableVC = nil;

@implementation AdviversePlayableVC

- (void)viewDidLoad {
    [super viewDidLoad];
    self.view.backgroundColor = [UIColor blackColor];
    self.modalPresentationStyle = UIModalPresentationFullScreen;

    // The message handler is the only channel from the (untrusted) creative.
    WKUserContentController* ucc = [[WKUserContentController alloc] init];
    [ucc addScriptMessageHandler:self name:@"adviversePlayable"];

    WKWebViewConfiguration* config = [[WKWebViewConfiguration alloc] init];
    config.userContentController = ucc;
    config.allowsInlineMediaPlayback = YES;
    if (@available(iOS 10.0, *)) {
        config.mediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypeNone;
    }
    if (@available(iOS 9.0, *)) {
        config.websiteDataStore = [WKWebsiteDataStore nonPersistentDataStore];
    }

    self.webView = [[WKWebView alloc] initWithFrame:CGRectZero configuration:config];
    self.webView.scrollView.scrollEnabled = NO;
    self.webView.scrollView.bounces = NO;
    self.webView.opaque = NO;
    self.webView.backgroundColor = [UIColor blackColor];
    self.webView.navigationDelegate = self;
    self.webView.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:self.webView];

    self.closeButton = [UIButton buttonWithType:UIButtonTypeSystem];
    [self.closeButton setTitle:@"✕" forState:UIControlStateNormal];
    [self.closeButton setTitleColor:[UIColor whiteColor] forState:UIControlStateNormal];
    self.closeButton.titleLabel.font = [UIFont systemFontOfSize:22 weight:UIFontWeightBold];
    self.closeButton.backgroundColor = [[UIColor blackColor] colorWithAlphaComponent:0.5];
    self.closeButton.layer.cornerRadius = 18;
    self.closeButton.translatesAutoresizingMaskIntoConstraints = NO;
    self.closeButton.hidden = YES;
    self.closeButton.accessibilityLabel = @"Close advertisement";
    [self.closeButton addTarget:self action:@selector(closeTapped)
               forControlEvents:UIControlEventTouchUpInside];
    [self.view addSubview:self.closeButton];

    id topGuide;
    id bottomGuide;
    if (@available(iOS 11.0, *)) {
        topGuide = self.view.safeAreaLayoutGuide.topAnchor;
        bottomGuide = self.view.safeAreaLayoutGuide.bottomAnchor;
    } else {
        topGuide = self.topLayoutGuide.bottomAnchor;
        bottomGuide = self.bottomLayoutGuide.topAnchor;
    }

    [NSLayoutConstraint activateConstraints:@[
        [self.webView.topAnchor constraintEqualToAnchor:topGuide],
        [self.webView.bottomAnchor constraintEqualToAnchor:bottomGuide],
        [self.webView.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [self.webView.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],

        [self.closeButton.topAnchor constraintEqualToAnchor:topGuide constant:12],
        [self.closeButton.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor constant:-12],
        [self.closeButton.widthAnchor constraintEqualToConstant:36],
        [self.closeButton.heightAnchor constraintEqualToConstant:36],
    ]];

    NSURL* url = self.assetURL ? [NSURL URLWithString:self.assetURL] : nil;
    if (url) {
        [self.webView loadRequest:[NSURLRequest requestWithURL:url]];
    } else {
        // Nothing to show — report a clean close rather than a black screen.
        dispatch_async(dispatch_get_main_queue(), ^{ AdvSend(self.objName, @"close"); });
    }

    // Fallback reveal of the close button so a broken creative can never trap the
    // user (rewarded gets a longer grace, matching the mobile/WebGL presenters).
    NSTimeInterval delay = self.rewarded ? 5.0 : 3.0;
    __weak AdviversePlayableVC* weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(delay * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        weakSelf.closeButton.hidden = NO;
    });
}

// MARK: - Bridge

- (void)userContentController:(WKUserContentController*)userContentController
      didReceiveScriptMessage:(WKScriptMessage*)message {
    if (![message.name isEqualToString:@"adviversePlayable"]) return;
    NSString* type = nil;
    if ([message.body isKindOfClass:[NSDictionary class]]) {
        type = ((NSDictionary*)message.body)[@"type"];
    } else if ([message.body isKindOfClass:[NSString class]]) {
        NSData* data = [(NSString*)message.body dataUsingEncoding:NSUTF8StringEncoding];
        id obj = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
        if ([obj isKindOfClass:[NSDictionary class]]) type = obj[@"type"];
    }
    if (![type isKindOfClass:[NSString class]] || type.length == 0) return;

    NSString* t = type;
    dispatch_async(dispatch_get_main_queue(), ^{
        // A 'finish' reveals the close button immediately; the reward itself is
        // granted on the C# side when the user then closes (matches WebGL).
        if ([t isEqualToString:@"finish"]) self.closeButton.hidden = NO;
        AdvSend(self.objName, t);
    });
}

- (void)closeTapped {
    // The native close button drives the single teardown path through C#: post
    // 'close', let AdviversePresenter run the reward/close semantics, then it
    // calls AdviverseIOSPlayableClose() which dismisses us.
    AdvSend(self.objName, @"close");
}

// Keep the playable contained: only the initial CDN document loads in-frame. Any
// top-frame navigation (a raw window.location or a tapped link) is treated as a
// click and opened in the system browser instead.
- (void)webView:(WKWebView*)webView
        decidePolicyForNavigationAction:(WKNavigationAction*)navigationAction
        decisionHandler:(void (^)(WKNavigationActionPolicy))decisionHandler {
    if (navigationAction.navigationType == WKNavigationTypeLinkActivated) {
        NSURL* url = navigationAction.request.URL;
        if (url && [[UIApplication sharedApplication] canOpenURL:url]) {
            if (@available(iOS 10.0, *)) {
                [[UIApplication sharedApplication] openURL:url options:@{} completionHandler:nil];
            } else {
                [[UIApplication sharedApplication] openURL:url];
            }
        }
        decisionHandler(WKNavigationActionPolicyCancel);
        return;
    }
    decisionHandler(WKNavigationActionPolicyAllow);
}

- (void)teardown {
    // Break the retain via the content controller so we don't leak after dismiss.
    [self.webView.configuration.userContentController removeAllUserScripts];
    if (@available(iOS 9.0, *)) {
        [self.webView.configuration.userContentController
            removeScriptMessageHandlerForName:@"adviversePlayable"];
    }
    self.webView.navigationDelegate = nil;
}

@end

extern "C" {

void AdviverseIOSPlayableOpen(const char* url, const char* objName, bool rewarded) {
    NSString* urlStr = url ? [NSString stringWithUTF8String:url] : nil;
    NSString* obj = objName ? [NSString stringWithUTF8String:objName] : nil;
    dispatch_async(dispatch_get_main_queue(), ^{
        // One playable at a time — tear down any previous overlay first.
        if (sPlayableVC) {
            [sPlayableVC teardown];
            [sPlayableVC dismissViewControllerAnimated:NO completion:nil];
            sPlayableVC = nil;
        }
        AdviversePlayableVC* vc = [[AdviversePlayableVC alloc] init];
        vc.assetURL = urlStr;
        vc.objName = obj;
        vc.rewarded = rewarded;
        sPlayableVC = vc;

        UIViewController* root = UnityGetGLViewController();
        if (root == nil) {
            root = [UIApplication sharedApplication].keyWindow.rootViewController;
        }
        if (root == nil) {
            // No host to present over — report close so the presenter cleans up.
            sPlayableVC = nil;
            AdvSend(obj, @"close");
            return;
        }
        [root presentViewController:vc animated:YES completion:nil];
    });
}

void AdviverseIOSPlayableClose(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        if (sPlayableVC == nil) return;
        AdviversePlayableVC* vc = sPlayableVC;
        sPlayableVC = nil;
        [vc teardown];
        [vc dismissViewControllerAnimated:YES completion:nil];
    });
}

} // extern "C"

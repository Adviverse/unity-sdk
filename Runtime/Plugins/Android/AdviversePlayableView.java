//
//  AdviversePlayableView.java
//  Adviverse Ads for Unity — Android native playable presenter.
//
//  A Unity Android build has no browser, so a playable (interactive HTML5)
//  creative is hosted in a native WebView added full-screen over the Unity
//  activity's content view. The bundle is untrusted third-party code, so it loads
//  from the CDN over https (not file://), file/content access is disabled, and its
//  ONLY channel back is the `AdviversePlayable` JavaScript interface — a single
//  postMessage(String) we parse. We translate ready / click / finish / close into
//  the SAME impression / click / reward the WebGL and mobile SDKs use, then
//  forward them to C# via UnityPlayer.UnitySendMessage — mirroring
//  AdviverseFullscreenActivity.kt in the mobile SDK.
//
//  Bridge protocol (from the bundle runtime, identical everywhere):
//    { __advPlayable: 1, type: "ready" | "click" | "finish" | "close" }
//  forwarded to Unity as: UnitySendMessage(objName, "OnPlayableMessage", type)
//
package com.adviverse.unity;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.TextView;

import com.unity3d.player.UnityPlayer;

import org.json.JSONObject;

public final class AdviversePlayableView {

    private static final Handler sMain = new Handler(Looper.getMainLooper());

    // One playable at a time.
    private static FrameLayout sOverlay;
    private static WebView sWebView;
    private static TextView sCloseButton;
    private static String sObjName;
    private static String sAssetUrl;

    private AdviversePlayableView() {}

    /** Called from C# (AndroidJavaClass.CallStatic). Presents the overlay. */
    public static void open(final String url, final String objName, final boolean rewarded) {
        final Activity activity = UnityPlayer.currentActivity;
        if (activity == null || url == null) {
            send(objName, "close");
            return;
        }
        activity.runOnUiThread(new Runnable() {
            @Override public void run() {
                try {
                    present(activity, url, objName, rewarded);
                } catch (Throwable t) {
                    send(objName, "close");
                }
            }
        });
    }

    /** Called from C# to dismiss the overlay (drives the single teardown path). */
    public static void close() {
        sMain.post(new Runnable() {
            @Override public void run() { teardown(); }
        });
    }

    private static void present(Activity activity, String url, String objName, boolean rewarded) {
        teardown(); // one at a time

        sObjName = objName;
        sAssetUrl = url;

        FrameLayout overlay = new FrameLayout(activity);
        overlay.setBackgroundColor(Color.BLACK);

        final WebView wv = new WebView(activity);
        wv.setLayoutParams(new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        wv.setBackgroundColor(Color.BLACK);
        WebSettings s = wv.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(false);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setCacheMode(WebSettings.LOAD_NO_CACHE);
        wv.addJavascriptInterface(new Bridge(), "AdviversePlayable");
        wv.setWebViewClient(new WebViewClient() {
            // Keep the top frame on our document; any link the creative navigates
            // to opens in the system browser as a click instead. The
            // WebResourceRequest overload is API 24+, the String one API 21-23.
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest req) {
                return externalize(req.getUrl() != null ? req.getUrl().toString() : null);
            }

            @SuppressWarnings("deprecation")
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String u) {
                return externalize(u);
            }

            private boolean externalize(String u) {
                if (u == null) return false;
                if (u.equals(sAssetUrl)) return false; // the initial document
                try {
                    Activity a = UnityPlayer.currentActivity;
                    if (a != null) {
                        a.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(u)));
                    }
                } catch (Throwable ignored) {}
                return true;
            }
        });
        overlay.addView(wv);

        final TextView closeBtn = new TextView(activity);
        closeBtn.setText("✕");
        closeBtn.setTextColor(Color.WHITE);
        closeBtn.setTextSize(20f);
        closeBtn.setBackgroundColor(Color.argb(128, 0, 0, 0));
        closeBtn.setPadding(28, 12, 28, 16);
        closeBtn.setVisibility(View.GONE);
        closeBtn.setContentDescription("Close advertisement");
        FrameLayout.LayoutParams clp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        clp.gravity = Gravity.TOP | Gravity.END;
        clp.topMargin = 36;
        clp.rightMargin = 36;
        closeBtn.setLayoutParams(clp);
        closeBtn.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { send(sObjName, "close"); }
        });
        overlay.addView(closeBtn);

        // Add over the Unity view; the overlay swallows touches behind it.
        activity.addContentView(overlay, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        sOverlay = overlay;
        sWebView = wv;
        sCloseButton = closeBtn;

        wv.loadUrl(url);

        // Fallback reveal so a broken creative can never trap the user (rewarded
        // gets a longer grace, matching the mobile/WebGL presenters).
        long fallbackMs = rewarded ? 5_000L : 3_000L;
        sMain.postDelayed(new Runnable() {
            @Override public void run() {
                if (sCloseButton != null) sCloseButton.setVisibility(View.VISIBLE);
            }
        }, fallbackMs);
    }

    /** The single channel from the untrusted playable back to native code. */
    static final class Bridge {
        @JavascriptInterface
        public void postMessage(String json) {
            String type;
            try {
                type = new JSONObject(json).optString("type");
            } catch (Throwable t) {
                return;
            }
            if (type == null || type.length() == 0) return;
            final String t = type;
            sMain.post(new Runnable() {
                @Override public void run() {
                    // 'finish' reveals close immediately; reward is granted on the
                    // C# side when the user then closes (matches WebGL/iOS).
                    if ("finish".equals(t) && sCloseButton != null) {
                        sCloseButton.setVisibility(View.VISIBLE);
                    }
                    send(sObjName, t);
                }
            });
        }
    }

    private static void teardown() {
        if (sWebView != null) {
            try { sWebView.removeJavascriptInterface("AdviversePlayable"); } catch (Throwable ignored) {}
            ViewGroup wp = (ViewGroup) sWebView.getParent();
            if (wp != null) wp.removeView(sWebView);
            sWebView.destroy();
        }
        if (sOverlay != null) {
            ViewGroup op = (ViewGroup) sOverlay.getParent();
            if (op != null) op.removeView(sOverlay);
        }
        sWebView = null;
        sOverlay = null;
        sCloseButton = null;
    }

    private static void send(String objName, String type) {
        if (objName == null || type == null) return;
        try {
            UnityPlayer.UnitySendMessage(objName, "OnPlayableMessage", type);
        } catch (Throwable ignored) {}
    }
}

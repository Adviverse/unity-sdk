// AdviversePresenter — the full-screen runtime overlay that actually shows the
// ad. Built entirely in code (a Canvas + uGUI), so the game needs no prefab.
// Handles image and video creatives, the skip/complete countdown, tap-to-click,
// the impression beacon, and the rewarded completion → reward-verify gate.

using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
#if (UNITY_WEBGL || UNITY_IOS) && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Adviverse
{
    internal class AdviversePresenter : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        // Implemented in Plugins/WebGL/AdviversePlayable.jslib.
        [DllImport("__Internal")] private static extern void AdviversePlayableOpen(string url, string objName);
        [DllImport("__Internal")] private static extern void AdviversePlayableClose();
#elif UNITY_IOS && !UNITY_EDITOR
        // Implemented in Plugins/iOS/AdviversePlayable.mm (C-ABI, __Internal).
        [DllImport("__Internal")] private static extern void AdviverseIOSPlayableOpen(string url, string objName, bool rewarded);
        [DllImport("__Internal")] private static extern void AdviverseIOSPlayableClose();
#endif
#if (UNITY_WEBGL || UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        // True once a native/DOM webview playable overlay is live (WebGL iframe,
        // iOS WKWebView, or Android WebView) — so Dismiss tears the right one down.
        private bool _playableActive;
#endif
        private MonoBehaviour _host;
        private string _base;

        private Canvas _canvas;
        private RawImage _media;
        private Text _timerText;
        private Button _closeBtn;
        private VideoPlayer _video;
        private RenderTexture _rt;

        private AdResponse _ad;
        private bool _rewarded;
        private bool _completed;
        private bool _closed;
        private bool _impFired;
        private VastTracker _vast; // non-null when playing a resolved third-party VAST tag

        private Action _onReward;
        private Action _onClose;
        private Action<string> _onError;

        public static AdviversePresenter Create(MonoBehaviour host, string baseUrl)
        {
            var go = new GameObject("AdviversePresenter");
            var p = go.AddComponent<AdviversePresenter>();
            p._host = host;
            p._base = baseUrl;
            return p;
        }

        public void Play(string format, AdResponse ad, Action onReward, Action onClose, Action<string> onError,
            VastResult vast = null)
        {
            _ad = ad;
            if (vast != null) _vast = new VastTracker(_host, vast);
            _rewarded = format == "rewarded";
            _onReward = onReward;
            _onClose = onClose;
            _onError = onError;

            var cr = ad.creative;
            bool isPlayable = (cr.type ?? "").ToLowerInvariant() == "html5" && !string.IsNullOrEmpty(cr.asset_url);
            if (isPlayable)
            {
                // Playables are HTML5, so they need a browser surface to run in.
                // WebGL hosts them in a sandboxed iframe over the canvas; iOS in a
                // WKWebView and Android in a WebView (both via native plugins). On
                // desktop / console / the editor there is no webview, so we degrade
                // to the creative's endcard as a normal skippable image ad.
#if (UNITY_WEBGL || UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
                PlayPlayable(cr.asset_url);
                return;
#else
                if (string.IsNullOrEmpty(cr.main_image_url))
                {
                    // No endcard to fall back to — nothing renderable here.
                    Fail("playable_no_fallback");
                    return;
                }
                // Fall through to the static-image presentation below, which loads
                // main_image_url, fires the impression, and runs the skip/close
                // countdown exactly like any other image interstitial/rewarded.
#endif
            }

            BuildCanvas();

            string mediaUrl = _vast != null ? vast.MediaUrl : cr.asset_url;
            bool isVideo = (cr.type ?? "").ToLowerInvariant() == "video" && !string.IsNullOrEmpty(mediaUrl);
            float dur = cr.duration > 0 ? cr.duration : (_vast != null ? vast.Duration : 0f);
            float skipAfter = _rewarded
                ? Mathf.Max(1f, dur > 0 ? dur : (isVideo ? 15f : 5f))
                : Mathf.Max(1f, cr.skip_offset_sec > 0 ? cr.skip_offset_sec : 5f);
            // VAST interstitials honour the tag's skipoffset; absent = not
            // skippable before the media ends (close also unlocks on end).
            if (_vast != null && !_rewarded)
                skipAfter = Mathf.Max(1f, vast.SkipOffset >= 0 ? vast.SkipOffset : (dur > 0 ? dur : 15f));

            if (isVideo) PlayVideo(mediaUrl);
            else _host.StartCoroutine(AdviverseFetch.LoadTexture(
                string.IsNullOrEmpty(cr.main_image_url) ? cr.asset_url : cr.main_image_url,
                (tex) =>
                {
                    if (tex == null) { Fail("creative_load_failed"); return; }
                    _media.texture = tex;
                    FitAspect(tex.width, tex.height);
                    FireImpression();
                }));

            _host.StartCoroutine(Countdown(skipAfter, isVideo));
        }

        // ---- UI construction ----

        private void BuildCanvas()
        {
            var go = gameObject;
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = short.MaxValue; // above game UI
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            go.AddComponent<GraphicRaycaster>();

            // Opaque black backdrop that also swallows clicks behind the ad.
            var bg = NewChild("bg", go.transform);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = Color.black;
            Stretch(bg);

            // Media surface (image texture or video render target).
            var mediaGo = NewChild("media", go.transform);
            _media = mediaGo.AddComponent<RawImage>();
            var mBtn = mediaGo.AddComponent<Button>();
            mBtn.transition = Selectable.Transition.None;
            mBtn.onClick.AddListener(OpenClick);
            Center(mediaGo, 900, 1600);

            // "Ad" disclosure label (top-left).
            var label = MakeText("label", "AD", 26, TextAnchor.MiddleLeft);
            Anchor(label.gameObject, new Vector2(0, 1), new Vector2(24, -24), new Vector2(160, 44));

            // Countdown (top-right).
            _timerText = MakeText("timer", "", 30, TextAnchor.MiddleRight);
            Anchor(_timerText.gameObject, new Vector2(1, 1), new Vector2(-24, -24), new Vector2(160, 44));

            // Close button (top-right, hidden until skippable/complete).
            var closeGo = NewChild("close", go.transform);
            _closeBtn = closeGo.AddComponent<Button>();
            var cImg = closeGo.AddComponent<Image>();
            cImg.color = new Color(0, 0, 0, 0.6f);
            Anchor(closeGo, new Vector2(1, 1), new Vector2(-24, -24), new Vector2(64, 64));
            var x = MakeText("x", "×", 40, TextAnchor.MiddleCenter);
            x.transform.SetParent(closeGo.transform, false);
            Stretch(x.gameObject);
            _closeBtn.onClick.AddListener(OnClosePressed);
            closeGo.SetActive(false);

            // Optional CTA button (bottom-center).
            if (!string.IsNullOrEmpty(_ad.creative.cta))
            {
                var ctaGo = NewChild("cta", go.transform);
                var ctaImg = ctaGo.AddComponent<Image>();
                ctaImg.color = new Color(0.106f, 0.314f, 0.878f, 1f); // #1b50e0
                var ctaBtn = ctaGo.AddComponent<Button>();
                ctaBtn.onClick.AddListener(OpenClick);
                Anchor(ctaGo, new Vector2(0.5f, 0), new Vector2(0, 120), new Vector2(420, 96));
                var ctaTxt = MakeText("ctaTxt", _ad.creative.cta, 32, TextAnchor.MiddleCenter);
                ctaTxt.transform.SetParent(ctaGo.transform, false);
                Stretch(ctaTxt.gameObject);
            }
        }

        private void PlayVideo(string url)
        {
            _rt = new RenderTexture(1280, 720, 0);
            _media.texture = _rt;
            _video = gameObject.AddComponent<VideoPlayer>();
            _video.playOnAwake = false;
            _video.source = VideoSource.Url;
            _video.url = url;
            _video.renderMode = VideoRenderMode.RenderTexture;
            _video.targetTexture = _rt;
            _video.audioOutputMode = VideoAudioOutputMode.Direct;
            _video.isLooping = false;
            _video.prepareCompleted += (vp) =>
            {
                FireImpression();
                vp.Play();
                _vast?.Started();
            };
            _video.loopPointReached += (vp) =>
            {
                _completed = true;
                _vast?.Fire("complete");
                ShowClose();
            };
            _video.errorReceived += (vp, msg) =>
            {
                if (_vast != null)
                {
                    // Media failure on a VAST tag: chain Error URLs + ours, then
                    // a no-fill (no reward — Dismiss never grants).
                    int code = _impFired ? VastErrorCode.MediaPlay : VastErrorCode.MediaNotFound;
                    AdviverseVast.ReportError(_host, _base, _ad, _vast.Result, code);
                    Fail("no_fill");
                    return;
                }
                Fail("video_error");
            };
            _video.Prepare();
        }

        // Quartile tracking for third-party VAST (our own beacons are unchanged).
        private void Update()
        {
            if (_vast == null || _video == null || _closed || !_video.isPlaying) return;
            _vast.Progress(_video.time, _video.length);
        }

        // ---- flow ----

        private System.Collections.IEnumerator Countdown(float seconds, bool isVideo)
        {
            int remaining = Mathf.CeilToInt(seconds);
            while (remaining > 0 && !_closed)
            {
                _timerText.text = remaining.ToString();
                yield return new WaitForSecondsRealtime(1f);
                remaining--;
            }
            if (!isVideo) _completed = true; // static creative earns on dwell
            ShowClose();
        }

        private void ShowClose()
        {
            if (_closed) return;
            _timerText.text = "";
            _closeBtn.gameObject.SetActive(true);
        }

        // ---- native/DOM playable (interactive HTML5) ----

#if (UNITY_WEBGL || UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        private void PlayPlayable(string url)
        {
            _playableActive = true;
            // Every playable bridge (WebGL .jslib, iOS .mm, Android shim) posts its
            // lifecycle messages back to this exact GameObject via
            // (Unity)SendMessage(name, "OnPlayableMessage", type), so the name must
            // be stable and unique for the duration of the ad.
            gameObject.name = "AdviversePlayable_" + GetInstanceID();
#if UNITY_WEBGL && !UNITY_EDITOR
            AdviversePlayableOpen(url, gameObject.name);
#elif UNITY_IOS && !UNITY_EDITOR
            AdviverseIOSPlayableOpen(url, gameObject.name, _rewarded);
#elif UNITY_ANDROID && !UNITY_EDITOR
            OpenAndroidPlayable(url, gameObject.name, _rewarded);
#endif
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        // Full-screen WebView playable, presented by the Java shim under
        // Plugins/Android (com.adviverse.unity.AdviversePlayableView). It adds an
        // overlay to the current Unity activity and bridges the same ready / click
        // / finish / close messages back via UnityPlayer.UnitySendMessage.
        private static void OpenAndroidPlayable(string url, string objName, bool rewarded)
        {
            using (var cls = new AndroidJavaClass("com.adviverse.unity.AdviversePlayableView"))
            {
                cls.CallStatic("open", url, objName, rewarded);
            }
        }

        private static void CloseAndroidPlayable()
        {
            using (var cls = new AndroidJavaClass("com.adviverse.unity.AdviversePlayableView"))
            {
                cls.CallStatic("close");
            }
        }
#endif

        // Invoked by the WebGL .jslib, the iOS .mm, or the Android shim via
        // (Unity)SendMessage — maps the sandboxed playable's lifecycle messages
        // onto the same impression / click / reward as every other format. A
        // 'finish' marks the experience complete so a rewarded close then verifies
        // and grants the reward, exactly like the WebGL path always has.
        public void OnPlayableMessage(string type)
        {
            switch (type)
            {
                case "ready":
                    FireImpression();
                    break;
                case "click":
                    OpenClick();
                    break;
                case "finish":
                    _completed = true;
                    break;
                case "close":
                    OnClosePressed();
                    break;
            }
        }

        private void FireImpression()
        {
            if (_impFired) return;
            _impFired = true;
            _host.StartCoroutine(AdviverseFetch.FireImpression(_base, _ad.imp_url));
        }

        private void OpenClick()
        {
            // VAST: open the advertiser's ClickThrough and record OUR click via
            // /click?track=1 (pixel, no redirect) + the chain's ClickTracking.
            if (_vast != null && !string.IsNullOrEmpty(_vast.Result.ClickThrough))
            {
                if (!string.IsNullOrEmpty(_ad.click_url))
                    _host.StartCoroutine(AdviverseVast.Get(_base + _ad.click_url + "&track=1"));
                _vast.Clicked();
                Application.OpenURL(_vast.Result.ClickThrough);
                return;
            }
            if (!string.IsNullOrEmpty(_ad.click_url))
                Application.OpenURL(_base + _ad.click_url);
        }

        private void OnClosePressed()
        {
            if (_vast != null)
            {
                if (!_completed) _vast.Fire("skip");
                _vast.Fire("closeLinear");
            }
            if (_rewarded && _completed && !string.IsNullOrEmpty(_ad.reward_url))
            {
                _host.StartCoroutine(AdviverseFetch.ConfirmReward(_ad.reward_url, (granted) =>
                {
                    if (granted) _onReward?.Invoke();
                    else _onError?.Invoke("reward_unverified");
                    Dismiss();
                }));
            }
            else
            {
                Dismiss();
            }
        }

        private void Fail(string reason)
        {
            _onError?.Invoke(reason);
            Dismiss();
        }

        private void Dismiss()
        {
            if (_closed) return;
            _closed = true;
            var cb = _onClose;
            if (_video != null) _video.Stop();
            if (_rt != null) _rt.Release();
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_playableActive) AdviversePlayableClose();
#elif UNITY_IOS && !UNITY_EDITOR
            if (_playableActive) AdviverseIOSPlayableClose();
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (_playableActive) CloseAndroidPlayable();
#endif
            _onClose = null;
            Destroy(gameObject);
            cb?.Invoke();
        }

        // ---- tiny uGUI helpers ----

        private static GameObject NewChild(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private Text MakeText(string name, string value, int size, TextAnchor align)
        {
            var go = NewChild(name, _canvas.transform);
            var t = go.AddComponent<Text>();
            t.text = value;
            t.font = BuiltinFont();
            t.fontSize = size;
            t.color = Color.white;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private static Font _font;
        private static Font BuiltinFont()
        {
            if (_font != null) return _font;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _font;
        }

        private static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void Center(GameObject go, float w, float h)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = Vector2.zero;
        }

        private static void Anchor(GameObject go, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
        }

        private void FitAspect(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            var rt = _media.GetComponent<RectTransform>();
            float maxW = 960f, maxH = 1700f;
            float scale = Mathf.Min(maxW / w, maxH / h);
            rt.sizeDelta = new Vector2(w * scale, h * scale);
        }
    }
}

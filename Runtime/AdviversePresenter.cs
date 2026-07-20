// AdviversePresenter — the full-screen runtime overlay that actually shows the
// ad. Built entirely in code (a Canvas + uGUI), so the game needs no prefab.
// Handles image and video creatives, the skip/complete countdown, tap-to-click,
// the impression beacon, and the rewarded completion → reward-verify gate.

using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Adviverse
{
    internal class AdviversePresenter : MonoBehaviour
    {
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

        public void Play(string format, AdResponse ad, Action onReward, Action onClose, Action<string> onError)
        {
            _ad = ad;
            _rewarded = format == "rewarded";
            _onReward = onReward;
            _onClose = onClose;
            _onError = onError;

            BuildCanvas();

            var cr = ad.creative;
            bool isVideo = (cr.type ?? "").ToLowerInvariant() == "video" && !string.IsNullOrEmpty(cr.asset_url);
            float skipAfter = _rewarded
                ? Mathf.Max(1f, cr.duration > 0 ? cr.duration : (isVideo ? 15f : 5f))
                : Mathf.Max(1f, cr.skip_offset_sec > 0 ? cr.skip_offset_sec : 5f);

            if (isVideo) PlayVideo(cr.asset_url);
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
            Anchor(label, new Vector2(0, 1), new Vector2(24, -24), new Vector2(160, 44));

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
            _video.prepareCompleted += (vp) => { FireImpression(); vp.Play(); };
            _video.loopPointReached += (vp) => { _completed = true; ShowClose(); };
            _video.errorReceived += (vp, msg) => Fail("video_error");
            _video.Prepare();
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

        private void FireImpression()
        {
            if (_impFired) return;
            _impFired = true;
            _host.StartCoroutine(AdviverseFetch.FireImpression(_base, _ad.imp_url));
        }

        private void OpenClick()
        {
            if (!string.IsNullOrEmpty(_ad.click_url))
                Application.OpenURL(_base + _ad.click_url);
        }

        private void OnClosePressed()
        {
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

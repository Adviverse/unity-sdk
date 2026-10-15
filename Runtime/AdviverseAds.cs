// AdviverseAds — rewarded + interstitial ads for Unity games.
//
// Adviverse serves the ads itself (no GameDistribution / Unity Ads / AppLovin
// relationship needed). This is the client: it fetches a full-screen ad from the
// engine's game endpoint, presents it over a runtime Canvas, fires the
// impression, and — for rewarded — confirms the signed reward callback before
// your onReward runs, so currency is granted only for a real, completed view.
//
// Works on mobile, desktop, and WebGL builds (UnityWebRequest + VideoPlayer).
//
// Quick start:
//   AdviverseAds.Instance.Configure("https://serve.adviverse.com", "my-game");
//   AdviverseAds.Instance.ShowRewarded("game-rewarded",
//       onReward: () => Player.AddCoins(50),
//       onClose:  () => Game.Resume());

using System;
using UnityEngine;

namespace Adviverse
{
    /// <summary>Decoded ad returned by the game serve endpoint.</summary>
    [Serializable]
    public class AdCreative
    {
        public string type;
        public string asset_url;
        public string main_image_url;
        public string icon_url;
        public string title;
        public string cta;
        public string brand_name;
        public float duration;
        public float skip_offset_sec;
        public string vast_tag_url; // third-party VAST tag (resolved when asset_url is empty)
    }

    [Serializable]
    public class AdResponse
    {
        public string request_id;
        public string format;
        public AdCreative creative;
        public string imp_url;    // relative
        public string click_url;  // relative
        public string reward_url; // absolute, rewarded only
    }

    [Serializable]
    internal class RewardResult
    {
        public bool ok;
        public bool rewarded;
        public string reason;
    }

    /// <summary>
    /// Singleton entry point. Attaches itself to a persistent GameObject on first
    /// use, so you never have to place it in a scene.
    /// </summary>
    public class AdviverseAds : MonoBehaviour
    {
        private const string DefaultBase = "https://serve.adviverse.com";
        private const string UidKey = "adviverse_game_uid";

        private string _base = DefaultBase;
        private string _client = "unity";
        private string _uid;
        private bool _busy;

        private static AdviverseAds _instance;

        /// <summary>The lazily-created singleton.</summary>
        public static AdviverseAds Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("AdviverseAds");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<AdviverseAds>();
                    _instance.LoadUid();
                }
                return _instance;
            }
        }

        private void LoadUid()
        {
            _uid = PlayerPrefs.GetString(UidKey, "");
            if (string.IsNullOrEmpty(_uid))
            {
                _uid = "gm_" + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(UidKey, _uid);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Set the serve host and client label. Optional — sensible defaults.</summary>
        public void Configure(string baseUrl = DefaultBase, string client = "unity")
        {
            if (!string.IsNullOrEmpty(baseUrl)) _base = baseUrl.TrimEnd('/');
            if (!string.IsNullOrEmpty(client)) _client = client;
        }

        /// <summary>
        /// Show a rewarded ad. <paramref name="onReward"/> fires only after a
        /// completed, server-verified view. <paramref name="onClose"/> always
        /// fires when the ad dismisses; <paramref name="onError"/> on no-fill or
        /// network failure.
        /// </summary>
        public void ShowRewarded(string tag, Action onReward, Action onClose = null, Action<string> onError = null)
        {
            Show("rewarded", tag, onReward, onClose, onError);
        }

        /// <summary>Show a skippable interstitial.</summary>
        public void ShowInterstitial(string tag, Action onClose = null, Action<string> onError = null)
        {
            Show("interstitial", tag, null, onClose, onError);
        }

        private void Show(string format, string tag, Action onReward, Action onClose, Action<string> onError)
        {
            if (string.IsNullOrEmpty(tag))
            {
                onError?.Invoke("missing_tag");
                return;
            }
            if (_busy)
            {
                onError?.Invoke("busy");
                return;
            }
            _busy = true;
            StartCoroutine(AdviverseFetch.Run(this, _base, _client, _uid, format, tag,
                onAd: (ad) =>
                {
                    if (!AdviverseVast.IsWrapper(ad.creative))
                    {
                        Present(format, ad, null, onReward, onClose, onError);
                        return;
                    }
                    // Third-party VAST tag: resolve to a playable MediaFile first.
                    // Failure fires the chain's Error URLs + ours and is a no-fill,
                    // so the ad never shows and a rewarded one can never grant.
                    StartCoroutine(AdviverseVast.Resolve(ad.creative.vast_tag_url, (code, res) =>
                    {
                        if (code != 0)
                        {
                            AdviverseVast.ReportError(this, _base, ad, res, code);
                            _busy = false;
                            onError?.Invoke("no_fill");
                            return;
                        }
                        Present(format, ad, res, onReward, onClose, onError);
                    }));
                },
                onError: (err) =>
                {
                    _busy = false;
                    onError?.Invoke(err);
                }));
        }

        private void Present(string format, AdResponse ad, VastResult vast, Action onReward, Action onClose,
            Action<string> onError)
        {
            var presenter = AdviversePresenter.Create(this, _base);
            presenter.Play(format, ad, onReward, () =>
            {
                _busy = false;
                onClose?.Invoke();
            }, (err) =>
            {
                _busy = false;
                onError?.Invoke(err);
            }, vast);
        }

        internal string Base => _base;
    }
}

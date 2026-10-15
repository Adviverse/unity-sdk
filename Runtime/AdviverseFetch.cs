// Network helpers: fetch a game ad, fire the impression, confirm the reward.
// Kept separate from AdviverseAds so the API surface stays readable.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Adviverse
{
    internal static class AdviverseFetch
    {
        /// <summary>
        /// GET /game/ad and hand back a parsed <see cref="AdResponse"/>. 204 / error /
        /// empty creative all resolve as onError("no_fill").
        /// </summary>
        public static IEnumerator Run(MonoBehaviour host, string baseUrl, string client, string uid,
            string format, string tag, Action<AdResponse> onAd, Action<string> onError)
        {
            string url = $"{baseUrl}/game/ad?tag={UnityWebRequest.EscapeURL(tag)}" +
                         $"&format={format}&client={UnityWebRequest.EscapeURL(client)}" +
                         $"&uid={UnityWebRequest.EscapeURL(uid)}" +
                         "&vast=1"; // wrapper (third-party VAST tag) creatives are resolvable client-side
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success || req.responseCode == 204)
                {
                    onError?.Invoke("no_fill");
                    yield break;
                }
                AdResponse ad = null;
                try { ad = JsonUtility.FromJson<AdResponse>(req.downloadHandler.text); }
                catch { ad = null; }
                if (ad == null || ad.creative == null ||
                    (string.IsNullOrEmpty(ad.creative.asset_url) && string.IsNullOrEmpty(ad.creative.main_image_url) &&
                     string.IsNullOrEmpty(ad.creative.vast_tag_url)))
                {
                    onError?.Invoke("no_fill");
                    yield break;
                }
                onAd?.Invoke(ad);
            }
        }

        /// <summary>Fire the impression beacon once (fire-and-forget).</summary>
        public static IEnumerator FireImpression(string baseUrl, string impUrl)
        {
            if (string.IsNullOrEmpty(impUrl)) yield break;
            using (var req = UnityWebRequest.Get(baseUrl + impUrl))
            {
                yield return req.SendWebRequest();
            }
        }

        /// <summary>
        /// Call the signed reward_url and report whether the server granted the
        /// reward. The HMAC + single-use enforcement live in the engine.
        /// </summary>
        public static IEnumerator ConfirmReward(string rewardUrl, Action<bool> done)
        {
            if (string.IsNullOrEmpty(rewardUrl)) { done?.Invoke(false); yield break; }
            using (var req = UnityWebRequest.Get(rewardUrl))
            {
                yield return req.SendWebRequest();
                bool granted = false;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try { granted = JsonUtility.FromJson<RewardResult>(req.downloadHandler.text).rewarded; }
                    catch { granted = false; }
                }
                done?.Invoke(granted);
            }
        }

        /// <summary>Load a texture from a URL (for static creatives).</summary>
        public static IEnumerator LoadTexture(string url, Action<Texture2D> done)
        {
            using (var req = UnityWebRequestTexture.GetTexture(url))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    done?.Invoke(DownloadHandlerTexture.GetContent(req));
                else
                    done?.Invoke(null);
            }
        }
    }
}

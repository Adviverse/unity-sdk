// Third-party VAST tag resolution + beacons (UnityWebRequest — works on
// mobile, desktop and WebGL; on WebGL the advertiser's ad server must allow
// CORS, exactly as for the web SDK). A creative with an empty asset_url and a
// vast_tag_url is resolved here at load time: follow the Wrapper chain (≤5
// hops), pick a MediaFile VideoPlayer can play, and collect every third-party
// beacon so the presenter can fire them alongside ours.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Adviverse
{
    internal static class AdviverseVast
    {
        private const int HopTimeoutSec = 8;

        /// <summary>True when the creative must be resolved from its third-party tag.</summary>
        public static bool IsWrapper(AdCreative cr)
        {
            return cr != null && string.IsNullOrEmpty(cr.asset_url) && !string.IsNullOrEmpty(cr.vast_tag_url);
        }

        // MediaFile preference for Unity's VideoPlayer: H.264 MP4 everywhere;
        // VP8 WebM is decodable on desktop / Android / most WebGL browsers but
        // not on iOS, so it's excluded there.
        private static string[] PreferredTypes()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new[] { "video/mp4" };
#else
            return new[] { "video/mp4", "video/webm" };
#endif
        }

        /// <summary>
        /// Resolve the chain. done(code, res): code 0 on success (res.MediaUrl
        /// set), else a VAST error code with the beacons collected so far.
        /// </summary>
        public static IEnumerator Resolve(string tagUrl, Action<int, VastResult> done)
        {
            var res = new VastResult();
            string url = tagUrl;
            for (int depth = 1; ; depth++)
            {
                if (depth > VastParser.MaxWrappers) { done(VastErrorCode.WrapperLimit, res); yield break; }
                string xml;
                using (var req = UnityWebRequest.Get(VastParser.Macros(url)))
                {
                    req.timeout = HopTimeoutSec;
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        done(VastErrorCode.Timeout, res);
                        yield break;
                    }
                    xml = req.downloadHandler.text;
                }
                VastDoc d;
                try { d = VastParser.ParseDoc(xml); }
                catch (Exception) { done(VastErrorCode.Xml, res); yield break; }
                if (!d.HasAd) { done(VastErrorCode.NoAd, res); yield break; }
                if (!VastParser.Merge(res, d, PreferredTypes()))
                {
                    url = d.Next;
                    continue;
                }
                done(string.IsNullOrEmpty(res.MediaUrl) ? VastErrorCode.MediaUnsupported : 0, res);
                yield break;
            }
        }

        /// <summary>Fire a list of third-party GET beacons (macros filled).</summary>
        public static void FireAll(MonoBehaviour host, List<string> urls, int code = 0)
        {
            if (urls == null || host == null) return;
            foreach (var u in urls) host.StartCoroutine(Get(VastParser.Macros(u, code)));
        }

        /// <summary>Fire one GET beacon (fire-and-forget).</summary>
        public static IEnumerator Get(string url)
        {
            if (string.IsNullOrEmpty(url)) yield break;
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = HopTimeoutSec;
                yield return req.SendWebRequest();
            }
        }

        /// <summary>
        /// Report a VAST failure: the chain's Error URLs ([ERRORCODE]) plus OUR
        /// tracking-only /imp?…&amp;ev=error beacon (never billing).
        /// </summary>
        public static void ReportError(MonoBehaviour host, string baseUrl, AdResponse ad, VastResult res, int code)
        {
            FireAll(host, res?.Errors, code);
            if (!string.IsNullOrEmpty(ad?.imp_url))
                host.StartCoroutine(Get(baseUrl + ad.imp_url + "&ev=error&code=" + code));
        }
    }

    /// <summary>
    /// Fires a resolved chain's third-party tracking while the VideoPlayer runs:
    /// Impression + creativeView/start on start, quartiles from progress,
    /// complete on end, skip/closeLinear on close, ClickTracking on click.
    /// </summary>
    internal class VastTracker
    {
        private readonly MonoBehaviour _host;
        private readonly VastResult _res;
        private readonly HashSet<string> _fired = new HashSet<string>();

        public VastTracker(MonoBehaviour host, VastResult res)
        {
            _host = host;
            _res = res;
        }

        public VastResult Result => _res;

        public void Fire(string ev)
        {
            if (!_fired.Add(ev)) return;
            AdviverseVast.FireAll(_host, _res.TrackingFor(ev));
        }

        public void Started()
        {
            if (!_fired.Add("__imp")) return;
            AdviverseVast.FireAll(_host, _res.Impressions);
            Fire("creativeView");
            Fire("start");
        }

        public void Progress(double time, double length)
        {
            if (length <= 0) return;
            double f = time / length;
            if (f >= 0.25) Fire("firstQuartile");
            if (f >= 0.5) Fire("midpoint");
            if (f >= 0.75) Fire("thirdQuartile");
        }

        public void Clicked()
        {
            AdviverseVast.FireAll(_host, _res.ClickTracking);
        }
    }
}

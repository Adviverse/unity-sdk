// Pure (engine-free) VAST parsing for third-party tag creatives. Kept free of
// UnityEngine so it can be unit-tested outside the editor; the network walk
// lives in AdviverseVast.cs. Mirrors the engine's web resolver (sdk_vast.js).
// Uses System.Xml only, which IL2CPP ships on every platform incl. WebGL.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace Adviverse
{
    /// <summary>VAST error codes the resolver/player reports ([ERRORCODE]).</summary>
    public static class VastErrorCode
    {
        public const int Xml = 100;
        public const int Timeout = 301;
        public const int WrapperLimit = 302;
        public const int NoAd = 303;
        public const int MediaNotFound = 401;
        public const int MediaUnsupported = 403;
        public const int MediaPlay = 405;
    }

    /// <summary>A resolved third-party VAST chain: media + every beacon along it.</summary>
    public class VastResult
    {
        public string MediaUrl = "";
        public string MediaType = "";
        public string ClickThrough = "";
        /// <summary>Seconds after which the ad may be skipped; -1 = not skippable.</summary>
        public float SkipOffset = -1f;
        /// <summary>Linear Duration in seconds (0 when absent).</summary>
        public float Duration;
        public readonly List<string> Impressions = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> ClickTracking = new List<string>();
        public readonly Dictionary<string, List<string>> Tracking = new Dictionary<string, List<string>>();

        public List<string> TrackingFor(string ev)
        {
            return Tracking.TryGetValue(ev, out var l) ? l : null;
        }
    }

    /// <summary>One hop of the chain.</summary>
    public class VastDoc
    {
        public bool HasAd;
        public string Next = "";
        public readonly List<string> Impressions = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> ClickTracking = new List<string>();
        public readonly Dictionary<string, List<string>> Tracking = new Dictionary<string, List<string>>();
        public string ClickThrough = "";
        public string SkipOffsetRaw = "";
        public float Duration;
        public readonly List<VastMediaFile> MediaFiles = new List<VastMediaFile>();
    }

    public struct VastMediaFile
    {
        public string Url;
        public string Type;
        public string Api;
    }

    public static class VastParser
    {
        public const int MaxWrappers = 5;
        private static readonly Random Rng = new Random();

        /// <summary>Fill the IAB macros a player is responsible for.</summary>
        public static string Macros(string url, int code = 0)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int cb;
            lock (Rng) cb = Rng.Next(10000000, 99999999);
            return url
                .Replace("[CACHEBUSTING]", cb.ToString(CultureInfo.InvariantCulture))
                .Replace("[TIMESTAMP]", Uri.EscapeDataString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)))
                .Replace("[ERRORCODE]", code > 0 ? code.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>"HH:MM:SS(.mmm)" → seconds; -1 when unparseable.</summary>
        public static float ParseTime(string s)
        {
            var m = Regex.Match(s ?? "", @"^\s*(\d+):(\d{1,2}):(\d{1,2}(?:\.\d+)?)\s*$");
            if (!m.Success) return -1f;
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600f
                 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60f
                 + float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>skipoffset ("HH:MM:SS" or "N%") → seconds; -1 = none.</summary>
        public static float SkipOffsetSeconds(string raw, float duration)
        {
            var v = (raw ?? "").Trim();
            if (v.Length == 0) return -1f;
            if (v.EndsWith("%", StringComparison.Ordinal))
            {
                if (duration > 0 && float.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var pct))
                    return pct / 100f * duration;
                return -1f;
            }
            return ParseTime(v);
        }

        /// <summary>Parse one VAST document. Throws XmlException on malformed XML.</summary>
        public static VastDoc ParseDoc(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            };
            var doc = new XmlDocument { XmlResolver = null };
            using (var sr = new StringReader(xml ?? ""))
            using (var xr = XmlReader.Create(sr, settings))
                doc.Load(xr);

            var d = new VastDoc();
            var ad = First(doc.DocumentElement, "Ad");
            if (ad == null) return d;
            d.HasAd = true;
            foreach (XmlElement n in ad.GetElementsByTagName("Impression")) Add(d.Impressions, n);
            foreach (XmlElement n in ad.GetElementsByTagName("Error")) Add(d.Errors, n);
            foreach (XmlElement n in ad.GetElementsByTagName("ClickTracking")) Add(d.ClickTracking, n);
            foreach (XmlElement n in ad.GetElementsByTagName("Tracking"))
            {
                var ev = n.GetAttribute("event");
                var u = Text(n);
                if (ev.Length == 0 || u.Length == 0) continue;
                if (!d.Tracking.TryGetValue(ev, out var l)) d.Tracking[ev] = l = new List<string>();
                l.Add(u);
            }
            if (First(ad, "Wrapper") != null) d.Next = Text(First(ad, "VASTAdTagURI"));
            d.ClickThrough = Text(First(ad, "ClickThrough"));
            var linear = First(ad, "Linear");
            if (linear != null) d.SkipOffsetRaw = linear.GetAttribute("skipoffset");
            d.Duration = Math.Max(0f, ParseTime(Text(First(ad, "Duration"))));
            foreach (XmlElement n in ad.GetElementsByTagName("MediaFile"))
                d.MediaFiles.Add(new VastMediaFile
                {
                    Url = Text(n),
                    Type = n.GetAttribute("type").ToLowerInvariant(),
                    Api = n.GetAttribute("apiFramework"),
                });
            return d;
        }

        /// <summary>
        /// Accumulate one hop into the chain result. Returns true when this hop
        /// is the final InLine (media/click/skip are then taken from it).
        /// </summary>
        public static bool Merge(VastResult res, VastDoc d, string[] preferredTypes)
        {
            res.Impressions.AddRange(d.Impressions);
            res.Errors.AddRange(d.Errors);
            res.ClickTracking.AddRange(d.ClickTracking);
            foreach (var kv in d.Tracking)
            {
                if (!res.Tracking.TryGetValue(kv.Key, out var l)) res.Tracking[kv.Key] = l = new List<string>();
                l.AddRange(kv.Value);
            }
            if (d.Next.Length > 0) return false;
            res.ClickThrough = d.ClickThrough;
            res.Duration = d.Duration;
            res.SkipOffset = SkipOffsetSeconds(d.SkipOffsetRaw, d.Duration);
            var pick = PickMedia(d.MediaFiles, preferredTypes);
            if (pick.HasValue)
            {
                res.MediaUrl = pick.Value.Url;
                res.MediaType = pick.Value.Type;
            }
            return true;
        }

        /// <summary>First progressive, non-interactive MediaFile in preference order.</summary>
        public static VastMediaFile? PickMedia(List<VastMediaFile> files, string[] preferredTypes)
        {
            foreach (var pref in preferredTypes)
                foreach (var f in files)
                {
                    if (f.Type != pref || !string.IsNullOrEmpty(f.Api)) continue;
                    if (f.Type == "application/javascript") continue;
                    if (!f.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !f.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                    return f;
                }
            return null;
        }

        private static XmlElement First(XmlElement root, string tag)
        {
            if (root == null) return null;
            if (root.Name == tag) return root;
            var l = root.GetElementsByTagName(tag);
            return l.Count > 0 ? (XmlElement)l[0] : null;
        }

        private static string Text(XmlElement n)
        {
            return n == null ? "" : (n.InnerText ?? "").Trim();
        }

        private static void Add(List<string> into, XmlElement n)
        {
            var t = Text(n);
            if (t.Length > 0) into.Add(t);
        }
    }
}

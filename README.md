# Adviverse Ads for Unity

Rewarded + interstitial ads for **Unity** games (mobile, desktop, WebGL).
Adviverse serves the ads itself — no GameDistribution / Unity Ads / AppLovin SDK.
Rewards are gated on a signed, single-use, server-verified callback.

## Install

Package Manager ▸ **Add package from git URL**:

```
https://github.com/Adviverse/unity-sdk.git
```

…or copy the `Runtime/` folder into your project.

## Use

```csharp
using Adviverse;

void Start() {
    AdviverseAds.Instance.Configure("https://serve.adviverse.com", "my-game");
}

// Rewarded — onReward fires ONLY after a completed + verified view
public void OnFreeCoinsButton() {
    AdviverseAds.Instance.ShowRewarded(
        tag: "game-rewarded",
        onReward: () => Player.AddCoins(50),
        onClose:  () => Game.Resume(),
        onError:  (why) => Debug.Log("no ad: " + why));   // e.g. "no_fill"
}

// Interstitial — skippable after a few seconds
void OnLevelComplete() {
    AdviverseAds.Instance.ShowInterstitial("game-interstitial", onClose: NextLevel);
}
```

`AdviverseAds.Instance` lazily creates a persistent `DontDestroyOnLoad` object —
no scene setup or prefab required.

## What it does

1. `GET /game/ad?tag=…&format=rewarded|interstitial` via `UnityWebRequest`.
2. Presents the ad on a runtime `Canvas` built in code — `VideoPlayer` for video
   creatives, a downloaded texture for display — with an **Ad** label and a
   skip/close countdown.
3. Fires the impression when shown; a tap calls `Application.OpenURL` on the
   advertiser's page.
4. **Rewarded:** on completion, calls the signed `reward_url`; `onReward` runs
   only if the engine returns `rewarded: true` (HMAC-verified, single-use).

## Playable (interactive HTML5) ads

Playables are mini-games served *inside* the ad. Because they're HTML5, they need
a browser surface — the SDK now provides one natively on every real target, so a
`type: html5` creative just works with **no code change on your side**:

- **WebGL builds** — the SDK layers a sandboxed `<iframe>` over the Unity canvas
  (via `Plugins/WebGL/AdviversePlayable.jslib`).
- **iOS builds** — the playable is presented full-screen in a native `WKWebView`
  (via `Plugins/iOS/AdviversePlayable.mm`).
- **Android builds** — the playable is presented full-screen in a native
  `WebView` overlaid on the Unity activity (via
  `Plugins/Android/AdviversePlayableView.java`).
- **Desktop / console / editor** — there is no embeddable webview, so the SDK
  gracefully **degrades to the creative's endcard** (`main_image_url`) shown as a
  normal skippable image interstitial/rewarded — it still fires the impression and
  honors the reward. If the creative ships no endcard image, it reports
  `onError("playable_no_fallback")`.

On every surface the playable's `ready` / `click` / `finish` / `close` lifecycle
is mapped onto the same impression / click / reward as any other format, and a
**rewarded** playable grants its reward on completion exactly like the image and
video paths (the reward is still gated on the signed, single-use `reward_url`).

For playables inside a **native app** (outside a Unity build) use the native
[**Adviverse mobile SDK**](https://github.com/Adviverse/ios-sdk) directly.

## Notes

- Requires the built-in **UI** (uGUI) and **Video** modules (default in Unity).
- Only one ad shows at a time (`onError("busy")` otherwise).
- Never grant on `onClose` alone — the verified `onReward` is the reward signal.
- WebGL builds work; for a browser-first game you can also use
  [`@adviverse/games`](https://github.com/Adviverse/game-web-sdk) directly.
- **Third-party VAST tags (0.2.0+)** — video creatives that are an advertiser's
  VAST tag are requested (`vast=1`), resolved at load time (Wrapper chain ≤5
  hops, MP4 preferred; WebM too except on iOS) and played through the same
  `VideoPlayer`, firing the tag's tracking alongside ours. Resolution or media
  failure → `onError("no_fill")`, never a reward. On WebGL the tag's ad server
  must allow CORS.

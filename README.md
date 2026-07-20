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

## Notes

- Requires the built-in **UI** (uGUI) and **Video** modules (default in Unity).
- Only one ad shows at a time (`onError("busy")` otherwise).
- Never grant on `onClose` alone — the verified `onReward` is the reward signal.
- WebGL builds work; for a browser-first game you can also use
  [`@adviverse/games`](https://github.com/Adviverse/game-web-sdk) directly.

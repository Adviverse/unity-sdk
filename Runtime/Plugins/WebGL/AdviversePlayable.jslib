// WebGL bridge for playable (interactive HTML5) ads.
//
// A Unity WebGL build runs inside a browser, so a playable can render exactly
// like on the web: a sandboxed <iframe> layered OVER the Unity canvas. Unity's
// own UI can't sit above a DOM iframe, so this plugin owns the overlay chrome
// (the "Ad" label + close button) as DOM, and forwards the playable's lifecycle
// messages back into C# via SendMessage(objName, "OnPlayableMessage", type).
//
// Same trust model as the web SDK: sandbox="allow-scripts" (no allow-same-origin
// → origin-"null"), and messages are honored ONLY from the iframe's own window.
mergeInto(LibraryManager.library, {

  // AdviversePlayableOpen(urlPtr, objNamePtr): mount the overlay + iframe.
  AdviversePlayableOpen: function (urlPtr, objNamePtr) {
    var url = UTF8ToString(urlPtr);
    var objName = UTF8ToString(objNamePtr);

    // Tear down any previous overlay first (one playable at a time).
    if (window.__advPlayableTeardown) { try { window.__advPlayableTeardown(); } catch (e) {} }

    var ov = document.createElement('div');
    ov.id = 'adv-playable-overlay';
    ov.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:#000;' +
      'display:flex;align-items:center;justify-content:center;font-family:system-ui,-apple-system,sans-serif';

    var frame = document.createElement('iframe');
    frame.setAttribute('sandbox', 'allow-scripts');
    frame.setAttribute('allow', 'autoplay; fullscreen');
    frame.style.cssText = 'border:0;width:min(420px,100%);height:100%;max-height:100vh;background:#000';
    frame.src = url;
    ov.appendChild(frame);

    var label = document.createElement('span');
    label.textContent = 'Ad';
    label.style.cssText = 'position:absolute;top:10px;left:10px;background:rgba(0,0,0,.6);color:#fff;' +
      'font-size:11px;line-height:1;padding:4px 7px;border-radius:4px;letter-spacing:.04em;text-transform:uppercase';
    ov.appendChild(label);

    var closeBtn = document.createElement('button');
    closeBtn.textContent = '×';
    closeBtn.setAttribute('aria-label', 'Close ad');
    closeBtn.style.cssText = 'position:absolute;top:8px;right:8px;width:32px;height:32px;border-radius:16px;' +
      'background:rgba(0,0,0,.6);color:#fff;border:0;font-size:20px;cursor:pointer;display:none';
    ov.appendChild(closeBtn);

    function send(type) {
      try { SendMessage(objName, 'OnPlayableMessage', type); } catch (e) {}
    }
    function reveal() { closeBtn.style.display = 'block'; }

    function onMsg(e) {
      if (e.source !== frame.contentWindow) return;      // trust only this frame
      var d = e.data;
      if (!d || d.__advPlayable !== 1) return;
      if (d.type === 'finish') reveal();
      send(d.type);
    }
    window.addEventListener('message', onMsg);
    closeBtn.addEventListener('click', function () { send('close'); });

    // Fallback reveal so a broken creative can never trap the player.
    var revealTimer = setTimeout(reveal, 5000);

    document.body.appendChild(ov);

    window.__advPlayableTeardown = function () {
      clearTimeout(revealTimer);
      window.removeEventListener('message', onMsg);
      if (ov.parentNode) ov.parentNode.removeChild(ov);
      window.__advPlayableTeardown = null;
    };
  },

  // AdviversePlayableClose(): remove the overlay.
  AdviversePlayableClose: function () {
    if (window.__advPlayableTeardown) { try { window.__advPlayableTeardown(); } catch (e) {} }
  }
});

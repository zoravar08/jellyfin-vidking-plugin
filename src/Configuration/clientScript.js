// VidKing web-client hook — three-tier playback: iframe, native stream, or proxy.
//
// Tier 1 (Option B): When extraction succeeded, ShortcutPath is a real MP4 URL.
//   Jellyfin's native player handles it server-side, apps, and web (Jellyfin proxies
//   the remote URL so the browser never hits CORS). The hook lets normal playback proceed.
//
// Tier 2 (Option A): /VidKing/Stream/{itemId} is a CORS proxy for web clients that
//   cannot play a cross-origin MP4 directly. The proxy relays Range headers for seeking.
//   Most clients never hit this — only when Jellyfin's built-in remote streaming does not
//   apply (rare).
//
// Tier 3 (existing): When ShortcutPath points at an HTML embed page, the hook opens an
//   iframe overlay so the target page's own player runs inside Jellyfin.
//
// The server decides the mode via /VidKing/Info/{itemId}: Mode=Stream → native,
// Mode=Iframe → overlay.
(function () {
    'use strict';

    if (window.__vkingHook) {
        return;
    }

    window.__vkingHook = true;

    var SELECTOR = '[data-action="play"],[data-action="resume"],.btnPlay,.cardOverlayButton]';
    var HASH_ID = /[?&]id=([0-9a-f]{32})/i;
    var DATA_ID = /^[0-9a-f]{32}$/i;
    var cache = new Map();

    function itemId(el) {
        var holder = el.closest('[data-id]');
        var id = holder && holder.getAttribute('data-id');
        if (id && DATA_ID.test(id)) {
            return id;
        }

        var match = HASH_ID.exec(location.hash || '');
        return match ? match[1] : null;
    }

    // Resolves to { mode, url, id }, or null for anything that is not a .vking item.
    // The server tells us whether to play natively (Stream) or via iframe (Iframe).
    function lookup(id) {
        if (cache.has(id)) {
            return Promise.resolve(cache.get(id));
        }

        var api = window.ApiClient;
        if (!api) {
            return Promise.resolve(null);
        }

        // Try /VidKing/Info/{id} first — it returns the playback mode.
        return api.ajax({
            type: 'GET',
            url: api.getUrl('VidKing/Info/' + id),
            dataType: 'json'
        }).then(
            function (res) {
                var info = res && typeof res === 'object' ? res : null;
                var hit = info && info.Url ? {
                    mode: info.Mode || 'Stream',
                    url: info.Url,
                    id: info.ItemId || info.itemId || id
                } : null;
                cache.set(id, hit);
                return hit;
            },
            function () {
                // Info endpoint failed — fall back to /VidKing/Embed/{id} (iframe-only).
                return api.ajax({
                    type: 'GET',
                    url: api.getUrl('VidKing/Embed/' + id),
                    dataType: 'json'
                }).then(
                    function (res) {
                        var url = (res && (res.Url || res.url)) || null;
                        var hit = url ? { mode: 'Iframe', url: url, id: (res.ItemId || res.itemId || id) } : null;
                        cache.set(id, hit);
                        return hit;
                    },
                    function () {
                        cache.set(id, null);
                        return null;
                    });
            });
    }

    function ticks(seconds) {
        return Math.floor(seconds * 10000000);
    }

    // The embed page posts its own player events. Read defensively: anything carrying a
    // numeric currentTime counts, wrapped in a "data" object or not.
    function playerEvent(raw) {
        var payload = raw;
        if (typeof payload === 'string') {
            try {
                payload = JSON.parse(payload);
            } catch (err) {
                return null;
            }
        }

        if (!payload || typeof payload !== 'object') {
            return null;
        }

        var body = payload.data && typeof payload.data === 'object' ? payload.data : payload;
        var time = Number(body.currentTime);
        if (!isFinite(time) || time < 0) {
            return null;
        }

        return {
            name: String(body.event || body.type || payload.event || payload.type || '').toLowerCase(),
            time: time,
            duration: Number(body.duration)
        };
    }

    // Turns embed-page events into normal Jellyfin playback reports.
    function tracker(id, origin) {
        var api = window.ApiClient;
        var session = 'vking-' + id + '-' + Date.now();
        var position = 0;
        var started = false;
        var stopped = false;
        var runtimeSent = false;

        function info(paused) {
            return {
                ItemId: id,
                PlaySessionId: session,
                PositionTicks: ticks(position),
                IsPaused: !!paused,
                CanSeek: true,
                PlayMethod: 'DirectPlay'
            };
        }

        function stop() {
            if (!api || !started || stopped) {
                return;
            }

            stopped = true;
            api.reportPlaybackStopped(info(false));
        }

        function onMessage(ev) {
            if (!api || ev.origin !== origin) {
                return;
            }

            var event = playerEvent(ev.data);
            if (!event) {
                return;
            }

            position = event.time;

            if (!runtimeSent && isFinite(event.duration) && event.duration > 0) {
                runtimeSent = true;
                api.ajax({
                    type: 'POST',
                    url: api.getUrl('VidKing/Runtime/' + id, { seconds: event.duration })
                });
            }

            if (!started) {
                started = true;
                api.reportPlaybackStart(info(false));
                return;
            }

            if (event.name === 'ended') {
                stop();
                return;
            }

            var report = info(event.name === 'pause');
            report.EventName = event.name || 'timeupdate';
            api.reportPlaybackProgress(report);
        }

        window.addEventListener('message', onMessage);

        return function () {
            stop();
            window.removeEventListener('message', onMessage);
        };
    }

    // Overlay bar auto-hides like Jellyfin's native video OSD (3s idle).
    var IDLE_MS = 3000;

    function overlay(url, id) {
        var wrap = document.createElement('div');
        wrap.className = 'vkingOverlay';
        wrap.style.cssText = 'position:fixed;inset:0;z-index:9999;background:#000';

        var bar = document.createElement('div');
        bar.style.cssText = 'position:absolute;top:0;left:0;right:0;display:flex;justify-content:flex-end;' +
            'padding:.2em;background:linear-gradient(180deg,rgba(0,0,0,.75),transparent);' +
            'transition:opacity .3s ease-out;opacity:1';

        var close = document.createElement('button');
        close.textContent = 'Close';
        close.style.cssText = 'background:#222;color:#fff;border:0;border-radius:4px;padding:.25em 1.2em;cursor:pointer;font:inherit;font-size:.85em';

        var frame = document.createElement('iframe');
        frame.src = url;
        frame.allow = 'autoplay; fullscreen; encrypted-media; picture-in-picture';
        frame.setAttribute('allowfullscreen', '');
        frame.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;border:0';

        var untrack = tracker(id, new URL(url, location.href).origin);
        var idleTimer = null;

        function showBar() {
            bar.style.opacity = '1';
            bar.style.pointerEvents = 'auto';
            clearTimeout(idleTimer);
            idleTimer = setTimeout(hideBar, IDLE_MS);
        }

        function hideBar() {
            bar.style.opacity = '0';
            bar.style.pointerEvents = 'none';
        }

        function shut() {
            untrack();
            clearTimeout(idleTimer);
            document.removeEventListener('keydown', onKey, true);
            wrap.remove();
        }

        function onKey(ev) {
            if (ev.key === 'Escape') {
                ev.stopPropagation();
                shut();
            }
            showBar();
        }

        close.addEventListener('click', shut);
        document.addEventListener('keydown', onKey, true);
        wrap.addEventListener('mousemove', showBar);
        wrap.addEventListener('touchstart', showBar, { passive: true });

        bar.appendChild(close);
        wrap.appendChild(frame);
        wrap.appendChild(bar);
        document.body.appendChild(wrap);
        showBar();
    }

    // Attempt extraction on demand via the extractor endpoint. When extraction is enabled
    // but the resolver did not extract (e.g. scanner skipped it), the client can trigger
    // extraction and then replay the click so the next lookup gets the real URL.
    function triggerExtraction(id, fallback) {
        var api = window.ApiClient;
        if (!api) {
            return fallback();
        }

        return api.ajax({
            type: 'POST',
            url: api.getUrl('VidKing/Extract/' + id),
            dataType: 'json'
        }).then(
            function (res) {
                if (res && res.ok) {
                    // Extraction succeeded — clear the cache so the next lookup gets the new URL.
                    cache.delete(id);
                    return lookup(id).then(function (hit) {
                        if (hit && hit.mode === 'Stream') {
                            // Let normal playback proceed.
                            var btn = document.querySelector('[data-id="' + id + '"]') ||
                                document.querySelector('*[data-id="' + id + '"]');
                            if (btn) {
                                btn.__vkPass = true;
                                btn.click();
                            }
                        }
                        return hit;
                    });
                }
                return fallback();
            },
            function () {
                return fallback();
            });
    }

    document.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest && e.target.closest(SELECTOR);
        if (!btn) {
            return;
        }

        // Our own replayed click for a normal item.
        if (btn.__vkPass) {
            delete btn.__vkPass;
            return;
        }

        var id = itemId(btn);
        // Already known to be a normal item: no round trip, no delay.
        if (!id || cache.get(id) === null) {
            return;
        }

        e.preventDefault();
        e.stopImmediatePropagation();

        // If the item is known to be an iframe item, show overlay immediately.
        var cached = cache.get(id);
        if (cached && cached.mode === 'Iframe') {
            overlay(cached.url, cached.id);
            return;
        }

        // If the item is known to be a stream item, let normal playback proceed.
        if (cached && cached.mode === 'Stream') {
            btn.__vkPass = true;
            btn.click();
            return;
        }

        // Unknown mode — ask the server. If the response says "Stream", let playback
        // proceed. If "Iframe", show the overlay. If extraction is enabled and failed,
        // offer to trigger extraction first.
        lookup(id).then(function (hit) {
            if (!hit) {
                btn.__vkPass = true;
                btn.click();
                return;
            }

            if (hit.mode === 'Stream') {
                btn.__vkPass = true;
                btn.click();
                return;
            }

            if (hit.mode === 'Iframe') {
                overlay(hit.url, hit.id);
                return;
            }

            // Unknown mode — fall through to normal playback.
            btn.__vkPass = true;
            btn.click();
        });
    }, true);
})();
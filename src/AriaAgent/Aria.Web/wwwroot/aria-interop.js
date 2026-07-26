// Cursor-following [data-tip] tooltip. A single shared #aria-tip element is positioned next to the
// cursor; because we measure its REAL size we can place it tight to the pointer and flip/clamp so it
// never runs off any edge — no fixed-size guesses, no large offsets.
(function () {
    let tip = null;
    let curEl = null;
    function ensureTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.id = 'aria-tip';
            tip.setAttribute('data-nosnippet', '');
            document.body.appendChild(tip);
        }
        return tip;
    }
    function hide() {
        curEl = null;
        if (tip) tip.style.opacity = '0';
    }
    document.addEventListener('mousemove', function (e) {
        const el = e.target.closest('[data-tip]');
        if (!el) { if (curEl) hide(); return; }
        const text = el.getAttribute('data-tip');
        if (!text) { hide(); return; }

        const t = ensureTip();
        if (el !== curEl) {
            t.textContent = text;
            curEl = el;
            // Optional stylized variant, e.g. data-tip-variant="loading" for a pulsing/spinner look —
            // generic so any future data-tip caller can opt in without touching this file again.
            t.className = el.getAttribute('data-tip-variant') ? 'aria-tip-' + el.getAttribute('data-tip-variant') : '';
        }
        t.style.opacity = '1';

        const w = t.offsetWidth, h = t.offsetHeight;   // real rendered size
        const vw = window.innerWidth, vh = window.innerHeight;
        let x = e.clientX + 14;          // prefer just right of cursor
        let y = e.clientY + 16;          // …and just below
        if (x + w > vw - 8) x = e.clientX - w - 14;   // no room right → flip left (tight to cursor)
        if (y + h > vh - 8) y = e.clientY - h - 14;   // no room below → flip up
        x = Math.max(8, Math.min(x, vw - w - 8));     // final viewport clamp
        y = Math.max(8, Math.min(y, vh - h - 8));
        t.style.left = x + 'px';
        t.style.top  = y + 'px';
    });
})();

window.ariaInterop = {
    // Per-circuit attestation (§12): relay a sign request to the bridge on THIS browser's own
    // localhost. Returns the bridge's {publicKey, signature} JSON, or null if no reachable/linked
    // local bridge — which keeps the circuit locked (onboarding). The server then verifies the
    // signature against the soul's key set. http://127.0.0.1 is exempt from mixed-content blocking.
    // True only when the page can reach loopback for automatic attestation (https:// or localhost).
    // When false, the UI steers the user to the manual session-code pairing fallback (§12).
    isSecureContext: function () {
        return window.isSecureContext === true;
    },

    // Detect the broad platform family from the UA so the bridge onboarding modal can
    // surface the matching one-line installer by default.
    getPlatformType: function () {
        const ua = navigator.userAgent.toLowerCase();
        if (ua.includes('win')) return 'windows';
        if (ua.includes('mac') || ua.includes('darwin')) return 'mac';
        if (ua.includes('linux')) return 'linux';
        return 'unknown';
    },

    copyText: function (text) {
        return navigator.clipboard.writeText(text || '');
    },

    // Poll the local bridge's /health endpoint from the browser. The bridge runs on the user's
    // own machine (localhost:5741), so only the browser can reach it reliably in this local-first
    // architecture. Returns { ok: true, version: string } or { ok: false, error: string }.
    getLocalBridgeVersion: async function () {
        try {
            const resp = await fetch('http://localhost:5741/health', { method: 'GET' });
            if (!resp.ok) return { ok: false, error: resp.statusText };
            const data = await resp.json();
            return { ok: true, version: data.version || 'unknown' };
        } catch (e) {
            return { ok: false, error: e.message };
        }
    },

    // Layer A device trust: ask the server to have THIS soul's node sign a trust-device grant for
    // this browser. Must run in the browser so the HttpOnly aria-device cookie rides along; the node
    // opens a local approval page and the request resolves once the human approves (or it times out).
    // Returns { ok: bool, error?: string }.
    trustThisBrowser: async function (userId, label) {
        try {
            const resp = await fetch('/api/devices/trust-this', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ userId: userId, label: label })
            });
            let data = {};
            try { data = await resp.json(); } catch (_) { /* non-JSON body */ }
            return { ok: resp.ok && data.ok === true, error: data.error };
        } catch (e) {
            return { ok: false, error: 'Request failed: ' + e.message };
        }
    },

    // Fetch the latest bridge release version from GitHub's API. Public repos support CORS,
    // but we fall back gracefully if the call is blocked.
    getLatestBridgeVersion: async function () {
        try {
            const resp = await fetch('https://api.github.com/repos/jlrouzies-fr/Aria.Agent/releases/latest');
            if (!resp.ok) return { ok: false, error: resp.statusText };
            const data = await resp.json();
            const tag = data.tag_name || '';
            // strip the 'bridge-v' prefix
            const version = tag.replace(/^bridge-v/, '');
            return { ok: true, version: version || 'unknown' };
        } catch (e) {
            return { ok: false, error: e.message };
        }
    },

    // Shared loopback fetch to /node/attest. Called for both user-bound attestation and
    // bridge-discovered soul selection (one bridge = one soul).
    _attestViaLocalBridge: async function (payload, label) {
        if (!window.isSecureContext) {
            console.error('[aria] ' + label + ': page is NOT a secure context (' + location.origin +
                '). Browsers block the loopback bridge fetch from here. Serve Aria.Web over HTTPS ' +
                'or open it via http://localhost. Attestation skipped → UI stays locked.');
            return null;
        }
        try {
            const resp = await fetch('http://localhost:5741/node/attest', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ payloadBase64: btoa(payload) })
            });
            if (!resp.ok) {
                console.error('[aria] ' + label + ': bridge responded ' + resp.status + ' ' + resp.statusText);
                return null;
            }
            return await resp.text();
        } catch (e) {
            console.error('[aria] ' + label + ': fetch to http://localhost:5741/node/attest failed:', e);
            return null;
        }
    },

    attestViaLocalBridge: async function (payload) {
        return await ariaInterop._attestViaLocalBridge(payload, 'attest');
    },

    discoverViaLocalBridge: async function (payload) {
        return await ariaInterop._attestViaLocalBridge(payload, 'discover');
    },

    // Hard scroll to bottom and reset any user-pause (called on load/new cogitation).
    scrollToBottom: function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;
        el._userPaused = false;
        el.scrollTop = el.scrollHeight;
        const btn = document.getElementById('chat-scroll-down');
        if (btn) btn.style.display = 'none';
        // A brand-new assistant message (just the avatar/name header, no content yet) can land
        // right at the container's previous bottom edge — its height isn't reflected in
        // scrollHeight until the browser finishes layout, one frame after this DOM mutation, so
        // the immediate scroll above can leave that header (and its streaming cursor) clipped.
        // Settle once more after layout catches up.
        requestAnimationFrame(() => { el.scrollTop = el.scrollHeight; });
    },
    // Smart scroll: only follows if user hasn't manually scrolled up.
    smartScrollToBottom: function (elementId) {
        const el = document.getElementById(elementId);
        if (!el || el._userPaused) return;
        el.scrollTop = el.scrollHeight;
        // See the comment in scrollToBottom — a newly appended, still-short streaming message
        // (header + cursor, no content yet) needs a post-layout settle to be fully in view.
        requestAnimationFrame(() => {
            if (!el._userPaused) el.scrollTop = el.scrollHeight;
        });
    },
    // Wire up user-scroll detection on the chat messages container.
    // Intent-based: any upward wheel/touch immediately pauses (no fighting programmatic scrolls);
    // the scroll handler only un-pauses once the user is back at the bottom + toggles the button.
    initChatScroll: function (containerId) {
        const el = document.getElementById(containerId);
        if (!el || el._scrollListenerAttached) return;
        el._scrollListenerAttached = true;
        el._userPaused = false;
        const updateBtn = () => {
            const btn = document.getElementById('chat-scroll-down');
            if (btn) btn.style.display = el._userPaused ? 'flex' : 'none';
        };
        // Immediate intent: wheeling/dragging up pauses follow on the very first notch.
        el.addEventListener('wheel', function (e) {
            if (e.deltaY < 0) { el._userPaused = true; updateBtn(); }
        }, { passive: true });
        let touchY = 0;
        el.addEventListener('touchstart', function (e) { touchY = e.touches[0].clientY; }, { passive: true });
        el.addEventListener('touchmove', function (e) {
            if (e.touches[0].clientY > touchY) { el._userPaused = true; updateBtn(); }
            touchY = e.touches[0].clientY;
        }, { passive: true });
        // Position: un-pause as soon as the user returns to the bottom.
        el.addEventListener('scroll', function () {
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
            if (atBottom && el._userPaused) { el._userPaused = false; }
            updateBtn();
        }, { passive: true });
    },
    // Chat tab bar: click-to-scroll arrows shown only while the strip actually overflows (the
    // native scrollbar is too thin at this bar height to be a reliable grab target). Binds once per
    // element (guarded via _ariaTabsInit); called again on every Blazor render to refresh arrow
    // visibility since adding/removing a tab changes scrollWidth without resizing the container
    // itself (so a ResizeObserver on the scroller alone wouldn't catch it).
    initChatTabsScroll: function (scrollerId, leftBtnId, rightBtnId) {
        const scroller = document.getElementById(scrollerId);
        const leftBtn  = document.getElementById(leftBtnId);
        const rightBtn = document.getElementById(rightBtnId);
        if (!scroller || !leftBtn || !rightBtn) return;

        if (!scroller._ariaTabsInit) {
            scroller._ariaTabsInit = true;
            const update = () => {
                const overflowing = scroller.scrollWidth > scroller.clientWidth + 1;
                leftBtn.style.display  = overflowing && scroller.scrollLeft > 2 ? 'flex' : 'none';
                rightBtn.style.display = overflowing && scroller.scrollLeft < scroller.scrollWidth - scroller.clientWidth - 2 ? 'flex' : 'none';
            };
            scroller._ariaTabsUpdate = update;
            scroller.addEventListener('scroll', update, { passive: true });
            if (window.ResizeObserver) new ResizeObserver(update).observe(scroller);
            leftBtn.onclick  = () => scroller.scrollBy({ left: -160, behavior: 'smooth' });
            rightBtn.onclick = () => scroller.scrollBy({ left:  160, behavior: 'smooth' });
        }
        scroller._ariaTabsUpdate();
    },

    // Keep the streaming thinking block scrolled to its newest tokens — but only if the user is
    // already near the bottom, so manually scrolling up to re-read isn't yanked back down.
    followThinkingStream: function () {
        const el = document.getElementById('streamingThinkBody');
        if (!el) return;
        if (el.scrollHeight - el.scrollTop - el.clientHeight < 60)
            el.scrollTop = el.scrollHeight;
    },

    focusElement: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.focus();
    },
    // Generic localStorage read/write — used to persist explorer/viewer UI state (which project,
    // collapsed or not, which file's open) across a hard page refresh, which tears down the whole
    // Blazor circuit and resets every C# field to its default. Same technique already used for the
    // resize-handle widths above.
    getLocalStorage: function (key) {
        try { return localStorage.getItem(key); } catch (e) { return null; }
    },
    setLocalStorage: function (key, value) {
        try {
            if (value === null || value === undefined) localStorage.removeItem(key);
            else localStorage.setItem(key, value);
        } catch (e) { }
    },
    // Full-height edge-drag width resize for a side panel (explorer tree / file viewer). Width is
    // applied by JS directly to the target's style — never rendered by Blazor for this element, so
    // there's no attribute for the renderer to fight over on re-render (unlike the DOM-mutation bug
    // in docs/Bugs/markdown-colorcode-freezes-blazor-circuit.md, which was about foreign child nodes
    // breaking Blazor's sibling bookkeeping — a plain untracked style attribute is a different, safe
    // category). Re-applies the persisted width on every call so it survives the target node being
    // torn down and recreated (e.g. the file viewer panel toggling open/closed via @if).
    initResizablePanel: function (handleId, targetId, opts) {
        const handle = document.getElementById(handleId);
        const target = document.getElementById(targetId);
        if (!handle || !target) return;

        const min = (opts && opts.min) || 150;
        const max = (opts && opts.max) || 800;
        const storageKey = opts && opts.storageKey;

        if (storageKey) {
            const saved = parseInt(localStorage.getItem(storageKey), 10);
            if (!isNaN(saved)) target.style.width = Math.max(min, Math.min(max, saved)) + 'px';
        }

        if (handle._resizeBound) return;
        handle._resizeBound = true;

        let dragging = false, startX = 0, startWidth = 0;

        handle.addEventListener('mousedown', function (e) {
            dragging = true;
            startX = e.clientX;
            startWidth = target.getBoundingClientRect().width;
            target.classList.add('resizing');
            document.body.style.userSelect = 'none';
            document.body.style.cursor = 'col-resize';
            e.preventDefault();
        });

        document.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            const newWidth = Math.max(min, Math.min(max, startWidth + (e.clientX - startX)));
            target.style.width = newWidth + 'px';
        });

        document.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            target.classList.remove('resizing');
            document.body.style.userSelect = '';
            document.body.style.cursor = '';
            if (storageKey) localStorage.setItem(storageKey, Math.round(target.getBoundingClientRect().width));
        });
    },
    // Vertical resize for a bottom panel (shared terminal). Dragging the handle up/down adjusts the
    // target's height. Persisted to localStorage and re-applied on every call so it survives @if toggles.
    initResizableHeight: function (handleId, targetId, opts) {
        const handle = document.getElementById(handleId);
        const target = document.getElementById(targetId);
        if (!handle || !target) return;

        const min = (opts && opts.min) || 120;
        const max = (opts && opts.max) || 600;
        const storageKey = opts && opts.storageKey;

        if (storageKey) {
            const saved = parseInt(localStorage.getItem(storageKey), 10);
            if (!isNaN(saved)) target.style.height = Math.max(min, Math.min(max, saved)) + 'px';
        }

        if (handle._resizeHeightBound) return;
        handle._resizeHeightBound = true;

        let dragging = false, startY = 0, startHeight = 0;

        handle.addEventListener('mousedown', function (e) {
            dragging = true;
            startY = e.clientY;
            startHeight = target.getBoundingClientRect().height;
            target.classList.add('resizing');
            document.body.style.userSelect = 'none';
            document.body.style.cursor = 'row-resize';
            e.preventDefault();
        });

        document.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            // Dragging handle upward (smaller clientY) increases height; downward decreases it.
            const newHeight = Math.max(min, Math.min(max, startHeight - (e.clientY - startY)));
            target.style.height = newHeight + 'px';
        });

        document.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            target.classList.remove('resizing');
            document.body.style.userSelect = '';
            document.body.style.cursor = '';
            if (storageKey) localStorage.setItem(storageKey, Math.round(target.getBoundingClientRect().height));
        });
    },

    // Smooth-scroll a message into view (used by the chat timeline rail).
    scrollToElement: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    },
    setChannelTooltip: function (show, anchorId) {
        const TOOLTIP_ID = 'aria-channel-tooltip-fixed';
        let tooltip = document.getElementById(TOOLTIP_ID);

        if (!show) {
            if (tooltip) tooltip.remove();
            return;
        }

        const anchor = document.getElementById(anchorId);
        if (!anchor) return;

        const rect = anchor.getBoundingClientRect();

        if (!tooltip) {
            tooltip = document.createElement('div');
            tooltip.id = TOOLTIP_ID;
            tooltip.className = 'channel-warn-tooltip';
            tooltip.textContent = '⚠ SELECT CHANNEL TO BEGIN TRANSMISSION';
            document.body.appendChild(tooltip);
        }

        tooltip.style.top = (rect.top + 2) + 'px';
    },

    openOAuthPopup: function (url) {
        const w = 520, h = 680;
        const left = Math.max(0, (screen.width  - w) / 2);
        const top  = Math.max(0, (screen.height - h) / 2);
        window.open(url, 'aria_oauth',
            `width=${w},height=${h},left=${left},top=${top},popup=true,resizable=yes`);
    },

    startVox: function (dotnetHelper, userId, channelName) {
        if (channelName && channelName.indexOf('Local:') === 0) {
            // ── On-device path: raw PCM → 16 kHz WAV → local whisper.cpp on the bridge ──
            // Captured with the Web Audio API (works in every browser incl. Safari/Edge, no codec),
            // downsampled to 16 kHz mono, and POSTed straight to the node. Nothing leaves the machine.
            var size = channelName.substring('Local:'.length);
            navigator.mediaDevices.getUserMedia({ audio: true }).then(async function (stream) {
                try {
                    var Ctx = window.AudioContext || window.webkitAudioContext;
                    var ctx = new Ctx();
                    // AudioWorklet is far more reliable than ScriptProcessorNode (which is deprecated and
                    // often yields silence when started outside a user gesture, as it is via Blazor).
                    await ctx.audioWorklet.addModule('/vox-recorder-worklet.js');
                    if (ctx.state !== 'running') { try { await ctx.resume(); } catch (e) {} }
                    var source = ctx.createMediaStreamSource(stream);
                    var node = new AudioWorkletNode(ctx, 'vox-recorder');
                    var buffers = [];
                    node.port.onmessage = function (e) { buffers.push(e.data); };
                    source.connect(node);
                    node.connect(ctx.destination); // worklet emits no audio → silent, no feedback
                    window._ariaVoxRec = {
                        type: 'pcm', stream: stream, ctx: ctx, source: source, node: node,
                        buffers: buffers, size: size, dotnet: dotnetHelper
                    };
                } catch (err) {
                    stream.getTracks().forEach(function (t) { t.stop(); });
                    dotnetHelper.invokeMethodAsync('OnVoxError', 'Audio capture init failed: ' + err.message);
                }
            }).catch(function (err) {
                dotnetHelper.invokeMethodAsync('OnVoxError', 'Microphone access denied (' + err.name + ')');
            });
        } else if (channelName) {
            // ── Cloud path: MediaRecorder → bridge → OpenAI/Groq Whisper ────
            navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
                var mimeType = MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
                    ? 'audio/webm;codecs=opus' : 'audio/webm';
                var recorder = new MediaRecorder(stream, { mimeType: mimeType });
                var chunks = [];

                recorder.ondataavailable = function (e) {
                    if (e.data.size > 0) chunks.push(e.data);
                };
                recorder.onstop = function () {
                    stream.getTracks().forEach(function (t) { t.stop(); });
                    var blob = new Blob(chunks, { type: mimeType });
                    var form = new FormData();
                    form.append('audio', blob, 'vox.webm');
                    form.append('provider', channelName);
                    // Post straight to the local bridge: the audio and the API key both stay on the
                    // user's machine — the server is never involved in cloud transcription.
                    fetch('http://localhost:5741/transcribe', {
                        method: 'POST', body: form
                    })
                    .then(function (r) { return r.json(); })
                    .then(function (data) {
                        if (data.text !== undefined && data.text !== null)
                            dotnetHelper.invokeMethodAsync('OnVoxTranscript', data.text);
                        else
                            dotnetHelper.invokeMethodAsync('OnVoxError', data.error || 'Transcription returned no text');
                    })
                    .catch(function (err) {
                        dotnetHelper.invokeMethodAsync('OnVoxError', 'Request failed: ' + err.message);
                    });
                };
                window._ariaVoxRec = { type: 'recorder', rec: recorder };
                recorder.start();
            }).catch(function (err) {
                dotnetHelper.invokeMethodAsync('OnVoxError',
                    'Microphone access denied (' + err.name + ')');
            });
        } else {
            // ── Default path: browser Web Speech API ────────────────────────
            var SR = window.SpeechRecognition || window.webkitSpeechRecognition;
            if (!SR) {
                dotnetHelper.invokeMethodAsync('OnVoxError',
                    'Speech recognition not supported — configure a Transcription Channel (OpenAI or Groq) in Tools → Voice Input as an alternative');
                return;
            }
            var r = new SR();
            r.continuous = false;
            r.interimResults = false;
            r.lang = 'en-US';
            var done = false;
            r.onresult = function (e) {
                done = true;
                var text = Array.from(e.results).map(function (x) { return x[0].transcript; }).join(' ');
                dotnetHelper.invokeMethodAsync('OnVoxTranscript', text);
            };
            r.onerror = function (e) {
                done = true;
                dotnetHelper.invokeMethodAsync('OnVoxError', e.error === 'network'
                    ? 'Browser speech engine unreachable. The built-in Web Speech API routes audio to the browser vendor’s cloud (Google for Chrome/Chromium) — Brave/Arc and some Chromium builds block it. Try Chrome or Safari, or configure an OpenAI/Groq Whisper channel (key stays on your node).'
                    : e.error);
            };
            r.onend = function () {
                window._ariaVoxRec = null;
                if (!done) dotnetHelper.invokeMethodAsync('OnVoxEnd');
            };
            window._ariaVoxRec = { type: 'speech', rec: r };
            r.start();
        }
    },
    stopVox: function () {
        var v = window._ariaVoxRec;
        if (!v) return;
        if (v.type === 'pcm') {
            window._ariaVoxRec = null;
            // Tear down capture, then assemble a 16 kHz mono WAV and post it to the node.
            try { v.node.disconnect(); v.source.disconnect(); } catch (e) {}
            v.stream.getTracks().forEach(function (t) { t.stop(); });
            var inRate = v.ctx.sampleRate;
            if (v.ctx.close) { try { v.ctx.close(); } catch (e) {} }

            var pcm = ariaInterop._voxMergeBuffers(v.buffers);
            if (pcm.length === 0) { v.dotnet.invokeMethodAsync('OnVoxTranscript', ''); return; }
            // Guard against a dead/silent mic: if the loudest sample is ~0, don't ship silence to
            // Whisper (it would return "[BLANK_AUDIO]"). Tell the user their mic captured nothing.
            var peak = 0;
            for (var pi = 0; pi < pcm.length; pi++) { var a = Math.abs(pcm[pi]); if (a > peak) peak = a; }
            if (peak < 0.005) {
                v.dotnet.invokeMethodAsync('OnVoxError',
                    'No audio captured from the microphone. Check the mic input/permission and try again.');
                return;
            }
            var down = ariaInterop._voxDownsample(pcm, inRate, 16000);
            var wav = ariaInterop._voxEncodeWav(down, 16000);

            var form = new FormData();
            form.append('audio', new Blob([wav], { type: 'audio/wav' }), 'vox.wav');
            fetch('http://localhost:5741/transcribe/local?size=' + encodeURIComponent(v.size), {
                method: 'POST', body: form
            })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.text !== undefined && data.text !== null)
                    v.dotnet.invokeMethodAsync('OnVoxTranscript', data.text);
                else
                    v.dotnet.invokeMethodAsync('OnVoxError', data.error || 'Transcription returned no text');
            })
            .catch(function (err) {
                v.dotnet.invokeMethodAsync('OnVoxError', 'Local transcription request failed: ' + err.message);
            });
            return;
        }
        if (v.type === 'recorder') v.rec.stop(); // triggers onstop → fetch
        else                       v.rec.stop(); // triggers onend
        window._ariaVoxRec = null;
    },

    // ── Local-Whisper audio helpers (raw PCM → 16 kHz mono WAV) ──────────────
    _voxMergeBuffers: function (buffers) {
        var len = 0, i;
        for (i = 0; i < buffers.length; i++) len += buffers[i].length;
        var out = new Float32Array(len), off = 0;
        for (i = 0; i < buffers.length; i++) { out.set(buffers[i], off); off += buffers[i].length; }
        return out;
    },
    _voxDownsample: function (input, inRate, outRate) {
        if (outRate >= inRate) return input;
        var ratio = inRate / outRate;
        var outLen = Math.round(input.length / ratio);
        var out = new Float32Array(outLen);
        var pos = 0;
        for (var i = 0; i < outLen; i++) {
            var next = Math.round((i + 1) * ratio);
            var sum = 0, count = 0;
            for (var j = pos; j < next && j < input.length; j++) { sum += input[j]; count++; }
            out[i] = count ? sum / count : 0;
            pos = next;
        }
        return out;
    },
    _voxEncodeWav: function (samples, rate) {
        var buffer = new ArrayBuffer(44 + samples.length * 2);
        var view = new DataView(buffer);
        function str(off, s) { for (var i = 0; i < s.length; i++) view.setUint8(off + i, s.charCodeAt(i)); }
        str(0, 'RIFF');
        view.setUint32(4, 36 + samples.length * 2, true);
        str(8, 'WAVE');
        str(12, 'fmt ');
        view.setUint32(16, 16, true);          // PCM chunk size
        view.setUint16(20, 1, true);           // format = PCM
        view.setUint16(22, 1, true);           // mono
        view.setUint32(24, rate, true);
        view.setUint32(28, rate * 2, true);    // byte rate
        view.setUint16(32, 2, true);           // block align
        view.setUint16(34, 16, true);          // bits per sample
        str(36, 'data');
        view.setUint32(40, samples.length * 2, true);
        var off = 44;
        for (var i = 0; i < samples.length; i++, off += 2) {
            var s = Math.max(-1, Math.min(1, samples[i]));
            view.setInt16(off, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
        }
        return buffer;
    },

    // NOTE: Local-Whisper model status/download are driven server-side through the bridge tunnel
    // (see NavMenu.Channels.RefreshVoxLocalStatusAsync / DownloadLocalModelAsync), not from here —
    // the node rejects direct cross-origin browser POSTs. Only the audio upload stays browser-direct.

    registerOAuthListener: function (dotnetHelper) {
        window.addEventListener('storage', function (e) {
            if (e.key === 'aria_oauth_result' && e.newValue) {
                try {
                    const result = JSON.parse(e.newValue);
                    localStorage.removeItem('aria_oauth_result');
                    dotnetHelper.invokeMethodAsync('OnOAuthConnected', result.tool || '');
                } catch (_) {}
            }
        });
    },

    // Agent theme — injects a <style id="aria-agent-theme"> into <head>.
    // Scoped to .main-content and .sidebar-pinned-top (New Cogitation button only).
    // Uses HSL derivation so ANY accent hue produces readable contrast on dark backgrounds.
    // Persists in sessionStorage so Blazor enhanced-navigation can restore it.
    applyTheme: function (hex) {
        const h = hex.replace('#', '');
        if (h.length !== 6) return;
        const ri = parseInt(h.slice(0, 2), 16) / 255;
        const gi = parseInt(h.slice(2, 4), 16) / 255;
        const bi = parseInt(h.slice(4, 6), 16) / 255;

        // Extract hue (0-1) from the accent color — only H is used; S and L are fixed
        // per-role to guarantee readability regardless of how dark the input is.
        const max = Math.max(ri, gi, bi), min = Math.min(ri, gi, bi), d = max - min;
        let hue = 0;
        if (d > 0.01) {
            if      (max === ri) hue = ((gi - bi) / d + (gi < bi ? 6 : 0)) / 6;
            else if (max === gi) hue = ((bi - ri) / d + 2) / 6;
            else                 hue = ((ri - gi) / d + 4) / 6;
        }

        // HSL → [R, G, B] (0-255 ints)
        const hsl = (h, s, l) => {
            if (s < 0.01) { const v = Math.round(l * 255); return [v, v, v]; }
            const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            const c = t => {
                t = ((t % 1) + 1) % 1;
                return t < 1/6 ? p + (q-p)*6*t : t < 1/2 ? q : t < 2/3 ? p + (q-p)*(2/3-t)*6 : p;
            };
            return [Math.round(c(h+1/3)*255), Math.round(c(h)*255), Math.round(c(h-1/3)*255)];
        };
        const toHex = ([r,g,b]) => '#' + [r,g,b].map(v => ('0'+v.toString(16)).slice(-2)).join('');

        // WCAG relative luminance — ensures contrast targets hold across all hues
        const lin = v => v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
        const luma = ([r,g,b]) => 0.2126*lin(r/255) + 0.7152*lin(g/255) + 0.0722*lin(b/255);
        // Binary search: find L (0-1) for the given hue+saturation that achieves targetLuma
        const findL = (s, targetLuma) => {
            let lo = 0, hi = 1;
            for (let i = 0; i < 18; i++) {
                const mid = (lo + hi) / 2;
                if (luma(hsl(hue, s, mid)) < targetLuma) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        };

        // bg-panel is always luma≈0.009 — text vars need luma≥0.18 for ≈3.9:1 contrast
        const glow = hsl(hue, 0.80, findL(0.80, 0.30));

        const vars = [
            `--border-glow:${toHex(glow)}`,
            `--text-bright:${toHex(hsl(hue, 0.68, findL(0.68, 0.62)))}`,
            `--text-normal:${toHex(hsl(hue, 0.52, findL(0.52, 0.44)))}`,
            `--text-muted:${toHex(hsl(hue, 0.38, findL(0.38, 0.28)))}`,
            `--text-dead:${toHex(hsl(hue, 0.22, findL(0.22, 0.18)))}`,
            `--text-body:${toHex(hsl(hue, 0.58, findL(0.58, 0.50)))}`,
            `--text-title:${toHex(hsl(hue, 0.62, findL(0.62, 0.48)))}`,
            `--text-system:${toHex(hsl(hue, 0.26, findL(0.26, 0.18)))}`,
            `--bg-panel:${toHex(hsl(hue, 0.28, 0.11))}`,
            `--bg-surface:${toHex(hsl(hue, 0.22, 0.14))}`,
            `--bg-hover:${toHex(hsl(hue, 0.25, 0.13))}`,
            `--bg-input:${toHex(hsl(hue, 0.20, 0.12))}`,
            `--border-dim:${toHex(hsl(hue, 0.30, 0.22))}`,
            `--border-normal:${toHex(hsl(hue, 0.42, 0.32))}`,
            `--border-active:${toHex(hsl(hue, 0.58, 0.44))}`,
            `--glow-sm:0 0 6px rgba(${glow[0]},${glow[1]},${glow[2]},0.4)`,
            `--glow-md:0 0 12px rgba(${glow[0]},${glow[1]},${glow[2]},0.45)`,
            `--glow-lg:0 0 24px rgba(${glow[0]},${glow[1]},${glow[2]},0.35)`,
            // Thinking block = same hue, inverse (light/washed) shade so internal cogitation reads
            // as the opposite of the main content. Light tint stays readable on the dark panel for
            // every hue (a literal "dark yellow on dark" would not, so we always go light).
            `--think-text:${toHex(hsl(hue, 0.45, findL(0.45, 0.66)))}`,
            `--think-accent:${toHex(hsl(hue, 0.58, findL(0.58, 0.34)))}`,
            `--think-border:${toHex(hsl(hue, 0.40, 0.26))}`,
            `--think-bg:${toHex(hsl(hue, 0.45, 0.12))}`,
        ].join(';');

        sessionStorage.setItem('aria-agent-theme-hex', hex);
        let el = document.getElementById('aria-agent-theme');
        if (!el) {
            el = document.createElement('style');
            el.id = 'aria-agent-theme';
            document.head.appendChild(el);
        }
        // Scope the agent theme to the chat page + the New Cogitation button only — NOT the whole
        // content region (.main-content also wraps Hive, Wargame, etc.).
        // .chat-shell = chat area + input; .sidebar-pinned-top = New Cogitation button.
        el.textContent = `.chat-shell,.sidebar-pinned-top{${vars}}`;
    },

    clearTheme: function () {
        sessionStorage.removeItem('aria-agent-theme-hex');
        const el = document.getElementById('aria-agent-theme');
        if (el) el.textContent = '';
    },

    // Skill editor — wires Tab-indent and Ctrl+S onto a textarea by id.
    // Safe to call multiple times: removes any previous listener first.
    initSkillEditor: function (elementId, dotnetHelper) {
        const el = document.getElementById(elementId);
        if (!el) return;
        if (el._ariaSkillHandler) el.removeEventListener('keydown', el._ariaSkillHandler);
        el._ariaSkillHandler = function (e) {
            if (e.key === 'Tab') {
                e.preventDefault();
                const start = el.selectionStart, end = el.selectionEnd;
                el.setRangeText('  ', start, end, 'end');
                el.dispatchEvent(new Event('input', { bubbles: true }));
            } else if (e.ctrlKey && e.key === 's') {
                e.preventDefault();
                dotnetHelper.invokeMethodAsync('SaveSkillFromJs');
            }
        };
        el.addEventListener('keydown', el._ariaSkillHandler);
    },

    // Chat input — intercepts navigation keys while the "#"/"/project" picker is open so the
    // textarea caret doesn't move and Enter accepts a selection instead of inserting a newline.
    // The picker-open flag is set from .NET via setPickerOpen so this can decide synchronously.
    // Idempotent: removes any previous listener first.
    initChatInput: function (elementId, dotnetHelper) {
        const el = document.getElementById(elementId);
        if (!el) return;
        if (el._ariaPickerHandler) el.removeEventListener('keydown', el._ariaPickerHandler);
        el._ariaPickerHandler = function (e) {
            if (el.dataset.pickerOpen !== '1') return;
            if (['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].includes(e.key)) {
                e.preventDefault();
                dotnetHelper.invokeMethodAsync('OnPickerKey', e.key);
            }
        };
        el.addEventListener('keydown', el._ariaPickerHandler);
    },

    setPickerOpen: function (elementId, open) {
        const el = document.getElementById(elementId);
        if (el) el.dataset.pickerOpen = open ? '1' : '0';
    }
};

// Copy-to-clipboard for code blocks.
//
// The COPY button is rendered server-side by MarkdownHelper.ToHtml, so it lives inside
// Blazor's MarkupString and is owned by the Blazor renderer. We must NOT insert or remove
// DOM nodes around Blazor-rendered content: the old MutationObserver here appended a
// <button> child into Blazor-owned <pre> elements, which desynced Blazor Server's logical
// DOM tree and froze the circuit on the next re-render (drawer animation / streaming token).
// See docs/Bugs/markdown-colorcode-freezes-blazor-circuit.md.
//
// A single delegated listener only READS the DOM and mutates the clicked button's own text
// (no structural changes), which is safe.
window.ariaInterop.enhanceCodeBlocks = function () { /* no-op: buttons are server-rendered */ };

document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('.code-copy-btn');
    if (!btn) return;
    var pre = btn.closest('pre');
    if (!pre) return;
    // Clone (off the live DOM, so Blazor is unaffected) and drop the copy button before
    // reading text. ColorCode-highlighted blocks have no <code>, so without this the copied
    // text would include the "COPY" label itself.
    var clone = pre.cloneNode(true);
    var cloneBtn = clone.querySelector('.code-copy-btn');
    if (cloneBtn) cloneBtn.remove();
    var code = clone.querySelector('code');
    var text = (code ? code.textContent : clone.textContent).replace(/^\n/, '');
    navigator.clipboard.writeText(text || '').then(function () {
        btn.textContent = 'COPIED';
        btn.classList.add('copied');
        setTimeout(function () {
            btn.textContent = 'COPY';
            btn.classList.remove('copied');
        }, 1500);
    }).catch(function () {
        btn.textContent = 'FAILED';
        setTimeout(function () { btn.textContent = 'COPY'; }, 1500);
    });
});

window.ariaInterop.setSidebarCollapsed = function (collapsed) {
    // localStorage first — MutationObserver reads it when it fires after classList.toggle
    localStorage.setItem('aria-sidebar-collapsed', collapsed ? '1' : '0');
    var sidebar = document.querySelector('.sidebar');
    if (sidebar) sidebar.classList.toggle('sidebar-collapsed', collapsed);
};

// Sets up a MutationObserver on the sidebar element so that whenever Blazor's
// DOM diffing resets <aside class="sidebar"> (stripping sidebar-collapsed),
// we immediately re-add it — without depending on any navigation event.
window.ariaInterop.initSidebarCollapse = function () {
    var sidebar = document.querySelector('.sidebar');
    if (!sidebar) return;

    function applyIfNeeded() {
        if (localStorage.getItem('aria-sidebar-collapsed') === '1' &&
            !sidebar.classList.contains('sidebar-collapsed')) {
            sidebar.style.transition = 'none';
            sidebar.classList.add('sidebar-collapsed');
            requestAnimationFrame(function () { sidebar.style.transition = ''; });
        }
    }

    applyIfNeeded();

    if (sidebar._sidebarObserver) return; // already watching this element

    var observer = new MutationObserver(applyIfNeeded);
    observer.observe(sidebar, { attributes: true, attributeFilter: ['class'] });
    sidebar._sidebarObserver = observer;
};

// Re-apply theme and re-init sidebar observer after Blazor enhanced navigation
// (enhanced nav can replace the <aside> element, disconnecting the old observer).
document.addEventListener('enhancedload', function () {
    window.ariaInterop.initSidebarCollapse();
    var hex = sessionStorage.getItem('aria-agent-theme-hex');
    if (hex) window.ariaInterop.applyTheme(hex);
});

// ── Memory (Noosphere) canvas pan + zoom ──────────────────────────────────────
// Same mechanics as initHiveCanvas below, on the mem- namespace — kept as a separate
// function (rather than a shared/parameterized one) so neither page risks the other's regressions.
window.ariaInterop.initMemoryCanvas = function (canvasEl, centerX, centerY) {
    if (typeof canvasEl === 'string') canvasEl = document.querySelector(canvasEl);
    if (!canvasEl || !(canvasEl instanceof HTMLElement) || !canvasEl.classList.contains('mem-canvas-wrap') || canvasEl._memInit) return;
    canvasEl._memInit = true;

    var panX = 0, panY = 0, zoom = 1;
    var dragging = false, startX = 0, startY = 0, startPX = 0, startPY = 0;
    var ZMIN = 0.15, ZMAX = 4;

    var inner = canvasEl.querySelector('.mem-canvas-inner');
    if (!inner) return;

    var zoomBar = canvasEl.querySelector('.mem-zoom-bar');
    var zoomTrack = zoomBar ? zoomBar.querySelector('.mem-zoom-track') : null;
    var zoomFill = zoomBar ? zoomBar.querySelector('.mem-zoom-fill') : null;
    var zoomThumb = zoomBar ? zoomBar.querySelector('.mem-zoom-thumb') : null;
    var zoomPct = zoomBar ? zoomBar.querySelector('.mem-zoom-pct') : null;

    function updateZoomUI() {
        if (!zoomFill) return;
        var t = (zoom - ZMIN) / (ZMAX - ZMIN);
        var pct = Math.max(0, Math.min(1, t)) * 100;
        zoomFill.style.height = pct + '%';
        if (zoomThumb) zoomThumb.style.bottom = pct + '%';
        if (zoomPct) zoomPct.textContent = Math.round(zoom * 100) + '%';
    }

    function apply() {
        inner.style.transform = 'translate(' + panX + 'px,' + panY + 'px) scale(' + zoom + ')';
        updateZoomUI();
    }
    // Center the initial view on the graph's world-space center, computed server-side by
    // MemoryGraphLayout.ComputeClusteredLayout (Memory.razor.cs passes it in) rather than a fixed guess.
    var rect = canvasEl.getBoundingClientRect();
    var cx = typeof centerX === 'number' ? centerX : 1400;
    var cy = typeof centerY === 'number' ? centerY : 1100;
    panX = rect.width / 2 - cx;
    panY = rect.height / 2 - cy;
    apply();

    // Zoom toward the center of the current viewport (there's no cursor position to anchor to, unlike
    // the wheel handler below) — keep whatever world point is centered on screen still centered after.
    function setZoomCentered(newZ) {
        newZ = Math.max(ZMIN, Math.min(ZMAX, newZ));
        var r = canvasEl.getBoundingClientRect();
        var vcx = r.width / 2, vcy = r.height / 2;
        panX = vcx - (vcx - panX) * (newZ / zoom);
        panY = vcy - (vcy - panY) * (newZ / zoom);
        zoom = newZ;
        apply();
    }

    canvasEl.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        if (e.target.closest('.mem-node') || e.target.closest('.mem-zoom-bar')) return;
        dragging = true;
        startX = e.clientX; startY = e.clientY;
        startPX = panX; startPY = panY;
        canvasEl.style.cursor = 'grabbing';
        e.preventDefault();
    });

    window.addEventListener('mousemove', function (e) {
        if (!dragging) return;
        panX = startPX + (e.clientX - startX);
        panY = startPY + (e.clientY - startY);
        apply();
    });

    window.addEventListener('mouseup', function () {
        if (dragging) { dragging = false; canvasEl.style.cursor = 'grab'; }
    });

    canvasEl.addEventListener('wheel', function (e) {
        e.preventDefault();
        var factor = e.deltaY < 0 ? 1.12 : 0.88;
        var newZ = Math.max(ZMIN, Math.min(ZMAX, zoom * factor));
        var r = canvasEl.getBoundingClientRect();
        var cx = e.clientX - r.left, cy = e.clientY - r.top;
        panX = cx - (cx - panX) * (newZ / zoom);
        panY = cy - (cy - panY) * (newZ / zoom);
        zoom = newZ;
        apply();
    }, { passive: false });

    if (zoomTrack) {
        var thumbDragging = false;
        function trackToZoom(clientY) {
            var r = zoomTrack.getBoundingClientRect();
            var t = 1 - Math.max(0, Math.min(1, (clientY - r.top) / r.height));
            return ZMIN + t * (ZMAX - ZMIN);
        }
        zoomTrack.addEventListener('mousedown', function (e) {
            thumbDragging = true;
            setZoomCentered(trackToZoom(e.clientY));
            e.preventDefault();
            e.stopPropagation();
        });
        window.addEventListener('mousemove', function (e) {
            if (!thumbDragging) return;
            setZoomCentered(trackToZoom(e.clientY));
        });
        window.addEventListener('mouseup', function () { thumbDragging = false; });
    }
    if (zoomBar) {
        var btnIn = zoomBar.querySelector('.mem-zoom-btn-in');
        var btnOut = zoomBar.querySelector('.mem-zoom-btn-out');
        if (btnIn) btnIn.addEventListener('click', function () { setZoomCentered(zoom * 1.2); });
        if (btnOut) btnOut.addEventListener('click', function () { setZoomCentered(zoom * 0.8); });
    }
};

// ── Hive canvas pan + zoom ────────────────────────────────────────────────────
window.ariaInterop.initHiveCanvas = function (canvasEl) {
    if (typeof canvasEl === 'string') canvasEl = document.querySelector(canvasEl);
    if (!canvasEl || !(canvasEl instanceof HTMLElement) || !canvasEl.classList.contains('hv-canvas-wrap') || canvasEl._hiveInit) return;
    canvasEl._hiveInit = true;

    var ZMIN = 0.15, ZMAX = 4, ZDEFAULT = 1;
    // Anchor mid-way between the Overmind (centre ~180 with the larger avatar) and the drone row (~605) so
    // both, plus the edge nodes between them, sit in the initial view. Raised from 330 → 390 after the
    // Overmind grew: keeps it centred above the empty-state text and lifts the whole frame a bit higher.
    // ANCHOR_X is the shared node-centre axis.
    var ANCHOR_X = 425, ANCHOR_Y = 390;
    var Hive_OVERMIND_CY = 180;   // Overmind block centre in world coords (top 40 + ~half-height 140)
    var panX = 0, panY = 0, zoom = ZDEFAULT;
    var dragging = false, startX = 0, startY = 0, startPX = 0, startPY = 0;
    var moved = false;

    var inner = canvasEl.querySelector('.hv-canvas-inner');
    if (!inner) return;

    var zoomBar   = canvasEl.querySelector('.hv-zoom-bar');
    var zoomTrack = zoomBar ? zoomBar.querySelector('.hv-zoom-track') : null;
    var zoomFill  = zoomBar ? zoomBar.querySelector('.hv-zoom-fill')  : null;
    var zoomThumb = zoomBar ? zoomBar.querySelector('.hv-zoom-thumb') : null;
    var zoomPct   = zoomBar ? zoomBar.querySelector('.hv-zoom-pct')   : null;

    function updateZoomUI() {
        if (!zoomFill) return;
        var t = (zoom - ZMIN) / (ZMAX - ZMIN);
        var pct = Math.max(0, Math.min(1, t)) * 100;
        zoomFill.style.height = pct + '%';
        if (zoomThumb) zoomThumb.style.bottom = pct + '%';
        if (zoomPct) zoomPct.textContent = Math.round(zoom * 100) + '%';
    }

    function apply() {
        inner.style.transform = 'translate(' + panX + 'px,' + panY + 'px) scale(' + zoom + ')';
        // Expose current transform so drag handler can read it
        canvasEl._hiveZoom = zoom;
        canvasEl._hivePanX = panX;
        canvasEl._hivePanY = panY;
        updateZoomUI();
    }

    // Default graph layout is anchored at Overmind ~ (400,90) with drones spread around (400,280).
    // Pan so that anchor sits centered horizontally and above the empty-state label vertically.
    // Re-centre whenever the canvas size lands — creating the FIRST hive (empty→populated sidebar) and
    // the left panel collapsing both resize the canvas AFTER init, which otherwise leaves the anchor
    // centred against a stale (wrong) size. Only auto-centres until the user pans/zooms (_hiveTouched),
    // so it never fights a deliberate view.
    function center() {
        if (canvasEl._hiveTouched) return;
        var rect = canvasEl.getBoundingClientRect();
        if (!rect.width) return;
        // Anchor is in world (pre-scale) coords, so multiply by zoom to keep it screen-centred.
        panX = rect.width / 2 - ANCHOR_X * zoom;
        // Empty hive (no drones yet): place the Overmind centre (~180) at 40% of the viewport HEIGHT rather
        // than at a fixed world anchor. panY depends on rect.height, so a fixed anchor drifts when the
        // viewport size / browser zoom changes; a fraction keeps the larger block a touch above middle with
        // the recruit caption (70%) below it, at any zoom. Populated hives keep ANCHOR_Y so the drone row
        // (~605) stays in view — a lower anchor there would push the drones off the bottom edge.
        var isEmpty = !inner.querySelector('.hv-drone-node');
        panY = isEmpty
            ? rect.height * 0.25 - Hive_OVERMIND_CY * zoom
            : rect.height / 2   - ANCHOR_Y * zoom;
        apply();
    }
    canvasEl._hiveCenter = center;

    // Zoom toward the centre of the viewport, keeping the currently-centred world point fixed.
    function setZoomCentered(newZ) {
        newZ = Math.max(ZMIN, Math.min(ZMAX, newZ));
        canvasEl._hiveTouched = true;
        var r = canvasEl.getBoundingClientRect();
        var vcx = r.width / 2, vcy = r.height / 2;
        panX = vcx - (vcx - panX) * (newZ / zoom);
        panY = vcy - (vcy - panY) * (newZ / zoom);
        zoom = newZ;
        apply();
    }

    center();                          // initial (may run before layout settles on create)
    requestAnimationFrame(center);     // re-centre once the browser has laid the canvas out
    setTimeout(center, 60);            // safety pass for slower settles / transitions

    // Keep re-centring on any real pane resize (first-hive sidebar swap, panel collapse, window
    // resize) until the user takes control. Observing the canvas element catches all of these.
    if (window.ResizeObserver) {
        var ro = new ResizeObserver(function () { center(); });
        ro.observe(canvasEl);
        canvasEl._hiveResizeObserver = ro;
    }

    canvasEl.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        var interactive = e.target.closest('.hv-gate-node,.hv-edge-insert,.hv-overmind-node,.hv-drone-node,.hv-insert-menu,.hv-zoom-bar');
        if (interactive) return;
        dragging = true; moved = false;
        startX = e.clientX; startY = e.clientY;
        startPX = panX; startPY = panY;
        canvasEl.style.cursor = 'grabbing';
        e.preventDefault();
    });

    window.addEventListener('mousemove', function (e) {
        if (!dragging) return;
        var dx = e.clientX - startX, dy = e.clientY - startY;
        if (Math.abs(dx) > 3 || Math.abs(dy) > 3) { moved = true; canvasEl._hiveTouched = true; }
        panX = startPX + dx; panY = startPY + dy;
        apply();
    });

    window.addEventListener('mouseup', function () {
        if (dragging) { dragging = false; canvasEl.style.cursor = 'grab'; }
    });

    canvasEl.addEventListener('wheel', function (e) {
        e.preventDefault();
        canvasEl._hiveTouched = true;
        var factor = e.deltaY < 0 ? 1.12 : 0.88;
        var newZ = Math.max(ZMIN, Math.min(ZMAX, zoom * factor));
        var rect = canvasEl.getBoundingClientRect();
        var cx = e.clientX - rect.left, cy = e.clientY - rect.top;
        panX = cx - (cx - panX) * (newZ / zoom);
        panY = cy - (cy - panY) * (newZ / zoom);
        zoom = newZ;
        apply();
    }, { passive: false });

    // ── Zoom bar (track drag + / − buttons) ──
    if (zoomTrack) {
        var thumbDragging = false;
        function trackToZoom(clientY) {
            var r = zoomTrack.getBoundingClientRect();
            var t = 1 - Math.max(0, Math.min(1, (clientY - r.top) / r.height));
            return ZMIN + t * (ZMAX - ZMIN);
        }
        zoomTrack.addEventListener('mousedown', function (e) {
            thumbDragging = true;
            setZoomCentered(trackToZoom(e.clientY));
            e.preventDefault();
            e.stopPropagation();
        });
        window.addEventListener('mousemove', function (e) {
            if (!thumbDragging) return;
            setZoomCentered(trackToZoom(e.clientY));
        });
        window.addEventListener('mouseup', function () { thumbDragging = false; });
    }
    if (zoomBar) {
        var btnIn  = zoomBar.querySelector('.hv-zoom-btn-in');
        var btnOut = zoomBar.querySelector('.hv-zoom-btn-out');
        if (btnIn)  btnIn.addEventListener('click',  function () { setZoomCentered(zoom * 1.2); });
        if (btnOut) btnOut.addEventListener('click', function () { setZoomCentered(zoom * 0.8); });
    }
};

// ── Fleet canvas pan + zoom ───────────────────────────────────────────────────
// Same mechanics as initMemoryCanvas, on the fl- namespace — kept as a separate
// function (rather than a shared/parameterized one) so neither page risks the other's regressions.
window.ariaInterop.initFleetCanvas = function (canvasEl, centerX, centerY) {
    if (typeof canvasEl === 'string') canvasEl = document.querySelector(canvasEl);
    if (!canvasEl || !(canvasEl instanceof HTMLElement) || !canvasEl.classList.contains('fl-canvas-wrap') || canvasEl._fleetInit) return;
    canvasEl._fleetInit = true;

    var panX = 0, panY = 0, zoom = 1;
    var dragging = false, startX = 0, startY = 0, startPX = 0, startPY = 0;
    var ZMIN = 0.15, ZMAX = 4;

    var inner = canvasEl.querySelector('.fl-canvas-inner');
    if (!inner) return;

    var zoomBar   = canvasEl.querySelector('.fl-zoom-bar');
    var zoomTrack = zoomBar ? zoomBar.querySelector('.fl-zoom-track') : null;
    var zoomFill  = zoomBar ? zoomBar.querySelector('.fl-zoom-fill')  : null;
    var zoomThumb = zoomBar ? zoomBar.querySelector('.fl-zoom-thumb') : null;
    var zoomPct   = zoomBar ? zoomBar.querySelector('.fl-zoom-pct')   : null;

    function updateZoomUI() {
        if (!zoomFill) return;
        var t = (zoom - ZMIN) / (ZMAX - ZMIN);
        var pct = Math.max(0, Math.min(1, t)) * 100;
        zoomFill.style.height = pct + '%';
        if (zoomThumb) zoomThumb.style.bottom = pct + '%';
        if (zoomPct) zoomPct.textContent = Math.round(zoom * 100) + '%';
    }

    function apply() {
        inner.style.transform = 'translate(' + panX + 'px,' + panY + 'px) scale(' + zoom + ')';
        updateZoomUI();
    }
    // Center the initial view on ARIA CORE — the ring of node cards is laid out around
    // this world-space point server-side (Fleet.razor.cs passes it in).
    var rect = canvasEl.getBoundingClientRect();
    var cx = typeof centerX === 'number' ? centerX : 2000;
    var cy = typeof centerY === 'number' ? centerY : 1500;
    panX = rect.width / 2 - cx;
    panY = rect.height / 2 - cy;
    apply();

    // Zoom toward the center of the current viewport — keep whatever world point is
    // centered on screen still centered after.
    function setZoomCentered(newZ) {
        newZ = Math.max(ZMIN, Math.min(ZMAX, newZ));
        var r = canvasEl.getBoundingClientRect();
        var vcx = r.width / 2, vcy = r.height / 2;
        panX = vcx - (vcx - panX) * (newZ / zoom);
        panY = vcy - (vcy - panY) * (newZ / zoom);
        zoom = newZ;
        apply();
    }

    canvasEl.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        if (e.target.closest('.fl-node') || e.target.closest('.fl-core-node') || e.target.closest('.fl-zoom-bar')) return;
        dragging = true;
        startX = e.clientX; startY = e.clientY;
        startPX = panX; startPY = panY;
        canvasEl.style.cursor = 'grabbing';
        e.preventDefault();
    });

    window.addEventListener('mousemove', function (e) {
        if (!dragging) return;
        panX = startPX + (e.clientX - startX);
        panY = startPY + (e.clientY - startY);
        apply();
    });

    window.addEventListener('mouseup', function () {
        if (dragging) { dragging = false; canvasEl.style.cursor = 'grab'; }
    });

    canvasEl.addEventListener('wheel', function (e) {
        e.preventDefault();
        var factor = e.deltaY < 0 ? 1.12 : 0.88;
        var newZ = Math.max(ZMIN, Math.min(ZMAX, zoom * factor));
        var r = canvasEl.getBoundingClientRect();
        var cx = e.clientX - r.left, cy = e.clientY - r.top;
        panX = cx - (cx - panX) * (newZ / zoom);
        panY = cy - (cy - panY) * (newZ / zoom);
        zoom = newZ;
        apply();
    }, { passive: false });

    if (zoomTrack) {
        var thumbDragging = false;
        function trackToZoom(clientY) {
            var r = zoomTrack.getBoundingClientRect();
            var t = 1 - Math.max(0, Math.min(1, (clientY - r.top) / r.height));
            return ZMIN + t * (ZMAX - ZMIN);
        }
        zoomTrack.addEventListener('mousedown', function (e) {
            thumbDragging = true;
            setZoomCentered(trackToZoom(e.clientY));
            e.preventDefault();
            e.stopPropagation();
        });
        window.addEventListener('mousemove', function (e) {
            if (!thumbDragging) return;
            setZoomCentered(trackToZoom(e.clientY));
        });
        window.addEventListener('mouseup', function () { thumbDragging = false; });
    }
    if (zoomBar) {
        var btnIn  = zoomBar.querySelector('.fl-zoom-btn-in');
        var btnOut = zoomBar.querySelector('.fl-zoom-btn-out');
        if (btnIn)  btnIn.addEventListener('click',  function () { setZoomCentered(zoom * 1.2); });
        if (btnOut) btnOut.addEventListener('click', function () { setZoomCentered(zoom * 0.8); });
    }
};

// ── Hive drone node dragging ──────────────────────────────────────────────────
window.ariaInterop.initDragNodes = function (canvasEl, dotNetRef) {
    if (typeof canvasEl === 'string') canvasEl = document.querySelector(canvasEl);
    if (!canvasEl || !(canvasEl instanceof HTMLElement) || !canvasEl.classList.contains('hv-canvas-wrap') || canvasEl._dragInit) return;
    canvasEl._dragInit = true;

    var dragging = false, dragNode = null, memberId = 0;
    var startMouseX = 0, startMouseY = 0, startNodeX = 0, startNodeY = 0;
    var moved = false;

    canvasEl.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        var node = e.target.closest('.hv-drone-node');
        if (!node) return;
        memberId = parseInt(node.dataset.memberId, 10);
        if (!memberId) return;

        e.stopPropagation(); // don't start canvas pan
        dragging  = true;
        moved     = false;
        dragNode  = node;
        startMouseX = e.clientX;
        startMouseY = e.clientY;
        startNodeX  = parseFloat(node.style.left) || 0;
        startNodeY  = parseFloat(node.style.top)  || 0;
        node.style.cursor = 'grabbing';
        node.style.zIndex = '200';
        e.preventDefault();
    });

    window.addEventListener('mousemove', function (e) {
        if (!dragging || !dragNode) return;
        var zoom = canvasEl._hiveZoom || 1;
        var dx = (e.clientX - startMouseX) / zoom;
        var dy = (e.clientY - startMouseY) / zoom;
        // Moving a node is taking control of the layout — stop the auto-center ResizeObserver from
        // resetting the pan when the drone drawer opens on the follow-up click (it would shift the view
        // and make the just-dropped drone look misplaced).
        if (Math.abs(dx) > 2 || Math.abs(dy) > 2) { moved = true; canvasEl._hiveTouched = true; }
        var nx = startNodeX + dx;
        var ny = startNodeY + dy;
        dragNode.style.left = nx + 'px';
        dragNode.style.top  = ny + 'px';
    });

    window.addEventListener('mouseup', function (e) {
        if (!dragging || !dragNode) return;
        var wasMoved = moved;
        var savedNode = dragNode;
        dragging = false;
        moved    = false;
        dragNode = null;
        savedNode.style.cursor = '';
        savedNode.style.zIndex = '';
        if (wasMoved && memberId) {
            var nx = parseFloat(savedNode.style.left) || 0;
            var ny = parseFloat(savedNode.style.top)  || 0;
            // Suppress the click event that fires immediately after mouseup
            savedNode._suppressClick = true;
            dotNetRef.invokeMethodAsync('OnDroneMoved', memberId, nx, ny);
        }
    });

    // Capture-phase click handler — intercepts before Blazor's @onclick
    canvasEl.addEventListener('click', function (e) {
        var node = e.target.closest('.hv-drone-node');
        if (node && node._suppressClick) {
            node._suppressClick = false;
            e.stopPropagation();
            e.preventDefault();
        }
    }, true);
};


// ── Debounced text inputs / textareas ─────────────────────────────────────────
// Reduces Blazor Server round-trips while typing. The DOM value is only reported
// to .NET after the user pauses for DebounceMs, or immediately on blur/Enter.
// Chat composer: auto-grow the prompt textarea. The layout is now permanently stacked
// (textarea on its own row, controls underneath), so this only needs to adjust height.
window.ariaInterop.initChatComposer = function (id) {
    var el = document.getElementById(id);
    if (!el || el._composerInit) return;
    el._composerInit = true;

    var apply = function () {
        el.style.height = 'auto';
        // With box-sizing: border-box, height must include borders; scrollHeight does not.
        var borderHeight = el.offsetHeight - el.clientHeight;
        el.style.height = Math.min(el.scrollHeight + borderHeight, 120) + 'px';
    };

    el._composerApply = apply;
    el.addEventListener('input', apply);
    el.addEventListener('focus', apply);

    // Ctrl+Enter (or Cmd+Enter on Mac) sends the message; plain Enter and Shift+Enter insert a
    // newline. Blazor's @onkeydown roundtrips to the server, so it can't cancel the browser's
    // default synchronously — without this the textarea would insert a newline and auto-grow for
    // one frame before the server-side send clears it (visible flash). Cancel the default here,
    // synchronously, only for the send chord. IME composition (isComposing) keeps the default.
    el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && !e.isComposing) {
            e.preventDefault();
        }
    });

    requestAnimationFrame(apply);
};

window.ariaInterop.debouncedInput = {
    _states: new WeakMap(),

    init: function (element, dotNetRef, delayMs) {
        if (!element) return;
        this.destroy(element);

        var state = {
            dotNetRef: dotNetRef,
            delayMs: delayMs || 150,
            timer: null,
            value: element.value,
            onInput: null,
            onBlur: null,
            dispose: null,
            setValue: null
        };

        state.onInput = function () {
            state.value = element.value;
            if (state.timer) clearTimeout(state.timer);
            state.timer = setTimeout(function () {
                state.timer = null;
                state.dotNetRef.invokeMethodAsync('NotifyValue', element.value);
            }, state.delayMs);
        };

        state.onBlur = function () {
            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = null;
                state.dotNetRef.invokeMethodAsync('NotifyValue', element.value);
            }
        };

        state.dispose = function () {
            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = null;
            }
            element.removeEventListener('input', state.onInput);
            element.removeEventListener('blur', state.onBlur);
        };

        state.setValue = function (value) {
            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = null;
            }
            var v = value == null ? '' : String(value);
            element.value = v;
            state.value = v;
            // Programmatic changes (vox fill, send-clear, queued) don't fire 'input' — re-run the
            // composer auto-grow/expand so the box tracks height and collapses when emptied.
            if (element._composerApply) element._composerApply();
        };

        state.setValueAndCursor = function (value, pos) {
            state.setValue(value);
            var p = Math.max(0, Math.min(value == null ? 0 : String(value).length, pos == null ? 0 : pos));
            element.setSelectionRange(p, p);
            element.focus();
        };

        element.addEventListener('input', state.onInput);
        element.addEventListener('blur', state.onBlur);
        this._states.set(element, state);
    },

    destroy: function (element) {
        if (!element) return;
        var state = this._states.get(element);
        if (state) {
            state.dispose();
            this._states.delete(element);
        }
    },

    setValue: function (element, value) {
        if (!element) return;
        var state = this._states.get(element);
        if (state) {
            state.setValue(value);
        } else {
            element.value = value == null ? '' : String(value);
        }
    },

    setValueAndCursor: function (element, value, pos) {
        if (!element) return;
        var state = this._states.get(element);
        if (state) {
            state.setValueAndCursor(value, pos);
        } else {
            element.value = value == null ? '' : String(value);
            var p = Math.max(0, Math.min(element.value.length, pos == null ? 0 : pos));
            element.setSelectionRange(p, p);
            element.focus();
        }
    },

    flush: async function (element) {
        if (!element) return;
        var state = this._states.get(element);
        if (state && state.timer) {
            clearTimeout(state.timer);
            state.timer = null;
            await state.dotNetRef.invokeMethodAsync('NotifyValue', element.value);
        }
    }
};

// ── Terminal prompt input ───────────────────────────────────────────────────
// Tab triggers shell-style completion via the Chat circuit. The DOM owns the text,
// so we read value + selectionStart synchronously and hand them to .NET.
window.ariaInterop.terminalInput = {
    init: function (elementOrId, dotnetHelper) {
        var element = typeof elementOrId === 'string' ? document.getElementById(elementOrId) : elementOrId;
        if (!element || element._terminalTabBound) return;
        element._terminalTabBound = true;

        element.addEventListener('keydown', function (e) {
            if (e.key !== 'Tab' || e.ctrlKey || e.altKey || e.metaKey) return;
            e.preventDefault();
            if (!dotnetHelper) return;
            var text = element.value;
            var cursor = element.selectionStart ?? text.length;
            dotnetHelper.invokeMethodAsync('OnTerminalTabAsync', text, cursor, e.shiftKey);
        });

        var update = function () { window.ariaInterop.terminalInput.updateCursor(element); };
        element.addEventListener('input', update);
        element.addEventListener('keyup', update);
        element.addEventListener('click', update);
        element.addEventListener('selectionchange', update);
        update();
    },

    // Focus the prompt input when the user clicks anywhere in the scrollback body — but only on a
    // genuine click, not a click-drag (text selection) and not a click on an interactive child
    // (buttons/links). Distinguishes click from drag by pointer travel between down and up.
    bindBodyFocus: function (bodyId, inputId) {
        var body = document.getElementById(bodyId);
        if (!body || body._focusBound) return;
        body._focusBound = true;

        var downX = 0, downY = 0, dragged = false;
        var THRESHOLD = 5; // px of travel that reclassifies a click as a drag

        body.addEventListener('mousedown', function (e) {
            downX = e.clientX; downY = e.clientY; dragged = false;
        });
        body.addEventListener('mousemove', function (e) {
            if (e.buttons && (Math.abs(e.clientX - downX) > THRESHOLD || Math.abs(e.clientY - downY) > THRESHOLD))
                dragged = true;
        });
        body.addEventListener('mouseup', function (e) {
            if (e.button !== 0) return;                 // primary button only
            if (dragged) return;                        // was a drag-select — leave selection intact
            if (e.target.closest('button, a, input, textarea')) return; // let controls handle their own click
            var sel = window.getSelection && window.getSelection();
            if (sel && sel.toString().length > 0) return;               // a selection exists — don't steal focus
            var input = document.getElementById(inputId);
            if (input) input.focus();
        });
    },

    updateCursor: function (elementOrId) {
        var element = typeof elementOrId === 'string' ? document.getElementById(elementOrId) : elementOrId;
        if (!element) return;
        var wrap = element.parentElement;
        if (!wrap) return;
        var text = element.value || '';
        var pos = element.selectionStart == null ? text.length : element.selectionStart;
        var before = text.substring(0, pos);
        var style = window.getComputedStyle(element);
        var canvas = document.createElement('canvas');
        var ctx = canvas.getContext('2d');
        ctx.font = style.font;
        var width = ctx.measureText(before).width;
        wrap.style.setProperty('--terminal-cursor-left', width + 'px');
    }
};

// ── Terminal PTY mode (xterm.js) ───────────────────────────────────────────
// Full pseudo-terminal rendering. Keystrokes go straight to the bridge PTY;
// output bytes come back from the bridge and are written into the xterm buffer.
window.ariaInterop.terminalPty = {
    _terminal: null,
    _fitAddon: null,
    _dotnetRef: null,

    create: function (containerId, dotnetRef, cols, rows) {
        var container = document.getElementById(containerId);
        if (!container) return null;
        this.dispose();

        this._dotnetRef = dotnetRef;
        var term = new Terminal({
            cols: cols || 80,
            rows: rows || 24,
            // Concrete monospace stack — NOT a CSS var(): xterm uses this string directly to
            // measure cell width, and var() resolves to an invalid font token there, desyncing
            // glyph advance from cell width (text renders horizontally stretched).
            fontFamily: 'Menlo, Monaco, Consolas, "Courier New", monospace',
            fontSize: 12,
            theme: {
                background: '#050605',
                foreground: '#33ff33',
                cursor: '#33ff33',
                selectionBackground: '#1a3a1a'
            },
            cursorBlink: true,
            allowProposedApi: true
        });
        var fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);
        term.open(container);

        term.onData(function (data) {
            if (!dotnetRef) return;
            var bytes = new TextEncoder().encode(data);
            var b64 = '';
            for (var i = 0; i < bytes.length; i++) b64 += String.fromCharCode(bytes[i]);
            dotnetRef.invokeMethodAsync('OnPtyData', btoa(b64));
        });

        term.onResize(function (size) {
            if (!dotnetRef) return;
            dotnetRef.invokeMethodAsync('OnPtyResize', size.cols, size.rows);
        });

        try { fitAddon.fit(); } catch (e) { }

        this._terminal = term;
        this._fitAddon = fitAddon;
        return { cols: term.cols, rows: term.rows };
    },

    write: function (dataBase64) {
        if (!this._terminal) return;
        var raw = atob(dataBase64);
        var bytes = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        var text = new TextDecoder().decode(bytes);
        this._terminal.write(text);
    },

    resize: function (cols, rows) {
        if (!this._terminal) return;
        this._terminal.resize(cols, rows);
    },

    fit: function () {
        if (!this._terminal || !this._fitAddon) return null;
        try { this._fitAddon.fit(); } catch (e) { return null; }
        return { cols: this._terminal.cols, rows: this._terminal.rows };
    },

    focus: function () {
        if (this._terminal) this._terminal.focus();
    },

    getBufferLines: function (maxLines) {
        if (!this._terminal) return [];
        var buffer = this._terminal.buffer.active;
        var lines = [];
        var count = Math.min(buffer.length, maxLines || 80);
        for (var i = buffer.length - count; i < buffer.length; i++) {
            if (i < 0) continue;
            lines.push(buffer.getLine(i)?.translateToString(true) ?? '');
        }
        return lines;
    },

    dispose: function () {
        if (this._fitAddon) { try { this._fitAddon.dispose(); } catch (e) { } this._fitAddon = null; }
        if (this._terminal) { try { this._terminal.dispose(); } catch (e) { } this._terminal = null; }
        this._dotnetRef = null;
    }
};

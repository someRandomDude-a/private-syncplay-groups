/* Guest credentials are HttpOnly cookies. Never load the normal Jellyfin web application here. */
(() => {
    'use strict';
    const base = new URL('./', location.href);
    const token = location.hash.slice(1);
    history.replaceState(null, '', location.pathname);
    const video = document.getElementById('video');
    const status = document.getElementById('status');
    const play = document.getElementById('play');
    const join = document.getElementById('join');
    const leave = document.getElementById('leave');
    const signIn = document.getElementById('signin');
    let hls, generation, active = false, timer;
    const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
        ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : x;
    signIn.href = new URL('../web/', base).href + '#privateSyncPlayInvite=' + encodeURIComponent(token);
    if (!token) { join.hidden = true; signIn.hidden = true; }
    async function request(path, body) {
        const r = await fetch(new URL('Guest/' + path, base), { credentials: 'same-origin', cache: 'no-store',
            method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json' },
            body: body === undefined ? undefined : JSON.stringify(body) });
        if (!r.ok) {
            const error = normalize(await r.json().catch(() => ({})));
            throw Object.assign(new Error(error.error || 'Party unavailable.'), { status: r.status });
        }
        return r.status === 204 ? null : normalize(await r.json());
    }
    function clearStream() {
        if (hls) { hls.destroy(); hls = null; }
        video.pause(); video.removeAttribute('src'); video.load(); generation = null;
    }
    function end(message) {
        active = false; clearTimeout(timer); clearStream(); leave.hidden = true; play.hidden = true;
        status.textContent = message;
    }
    async function enablePlayback() {
        try { await video.play(); play.hidden = true; } catch { play.hidden = false; }
    }
    play.onclick = enablePlayback;
    document.getElementById('volume').oninput = e => { video.volume = Number(e.target.value); };
    async function poll() {
        if (!active) return;
        try {
            const state = await request('State');
            status.textContent = !state.playing ? 'Waiting for the party to play a video…'
                : state.paused ? 'Party paused.' : (state.title || 'Watching');
            if (!state.playing || state.paused) { clearStream(); }
            else if (state.ready && state.generation !== generation) {
                clearStream(); generation = state.generation;
                const url = new URL(`Guest/media/${generation}/index.m3u8`, base).href;
                if (window.Hls?.isSupported()) {
                    hls = new Hls({ enableWorker: true, backBufferLength: 10, maxBufferLength: 12 });
                    hls.loadSource(url); hls.attachMedia(video);
                    hls.on(Hls.Events.MANIFEST_PARSED, enablePlayback);
                    hls.on(Hls.Events.ERROR, (_, data) => {
                        if (data.fatal) { clearStream(); status.textContent = 'Reconnecting to party stream…'; }
                    });
                } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
                    video.src = url; void enablePlayback();
                } else { end('This browser does not support HLS playback.'); return; }
            }
            if (state.ready && generation === state.generation) {
                const target = Math.max(0, state.position - state.offset);
                for (let i = 0; i < video.buffered.length; i++) {
                    if (target >= video.buffered.start(i) && target <= video.buffered.end(i) && Math.abs(video.currentTime - target) > 1.5)
                        video.currentTime = target;
                }
            }
        } catch (error) {
            if ([401, 403, 404, 410].includes(error.status)) { end('This guest session has ended.'); return; }
            clearStream(); status.textContent = error.message;
        }
        timer = setTimeout(poll, 1000);
    }
    async function start() {
        active = true; document.getElementById('actions').hidden = true; leave.hidden = false; await poll();
    }
    join.onclick = async () => {
        join.disabled = true;
        try { await request('Redeem', { Token: token }); await start(); }
        catch (error) { status.textContent = error.message; join.disabled = false; }
    };
    leave.onclick = async () => { try { await request('Leave', {}); } finally { end('You left the party.'); } };
    if (!token) void start(); // HttpOnly session survives a refresh, never an account.
    window.addEventListener('pagehide', () => { active = false; clearStream(); });
})();

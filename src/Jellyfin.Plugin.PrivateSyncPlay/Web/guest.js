/* Guest credentials stay in HttpOnly cookies. No authenticated Jellyfin app, token or playback API is loaded. */
(() => {
    'use strict';
    const base = new URL('./', location.href), token = location.hash.slice(1);
    history.replaceState(null, '', location.pathname);
    const byId = id => document.getElementById(id);
    const video = byId('video'), player = byId('videoOsdPage'), status = byId('status');
    const play = byId('play'), join = byId('join'), signIn = byId('signin');
    const volume = byId('volume'), select = byId('subtitles'), subtitleStatus = byId('subtitleStatus');
    const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
        ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : x;
    let hls, generation, active = false, timer, hideTimer, epoch = 0, state, receivedAt = 0;
    let desiredPlaying = false, subtitleKey, subtitleElement, subtitleChoice = 'auto', trackSignature;
    const mobile = window.matchMedia('(max-width: 50em)');
    function layout() {
        document.documentElement.classList.toggle('layout-mobile', mobile.matches);
        document.documentElement.classList.toggle('layout-desktop', !mobile.matches);
    }
    mobile.addEventListener('change', layout); layout();
    signIn.href = new URL('Account', base).href + '#' + encodeURIComponent(token);
    if (!token) { join.hidden = true; signIn.hidden = true; }
    function message(text) { status.textContent = text; byId('playerStatus').textContent = text; }
    function showControls() {
        player.classList.remove('privateSyncPlayControlsHidden'); clearTimeout(hideTimer);
        if (active && desiredPlaying && play.hidden && byId('subtitleSettings').hidden) hideTimer = setTimeout(() => {
            const focus = document.activeElement;
            if (focus === player || focus === video || !player.contains(focus)) player.classList.add('privateSyncPlayControlsHidden');
        }, 5000);
    }
    player.addEventListener('pointermove', showControls); player.addEventListener('pointerdown', showControls);
    player.addEventListener('focusin', showControls);
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
    function clearSubtitle() {
        if (subtitleElement) { subtitleElement.track.mode = 'disabled'; subtitleElement.remove(); subtitleElement = null; }
        subtitleKey = null;
    }
    function clearStream() {
        generation = null; clearSubtitle();
        if (hls) { hls.destroy(); hls = null; }
        video.pause(); video.removeAttribute('src'); video.load();
    }
    function end(text) {
        epoch++; active = false; desiredPlaying = false;
        clearTimeout(timer); clearTimeout(hideTimer); clearStream();
        player.hidden = true; status.hidden = false; play.hidden = true; document.querySelector('h1').hidden = false;
        byId('actions').hidden = !token; join.disabled = false; message(text);
        if (document.fullscreenElement === player) void document.exitFullscreen().catch(() => {});
    }
    async function enablePlayback() {
        if (!active || !desiredPlaying) return;
        try { await video.play(); if (active && desiredPlaying) { play.hidden = true; showControls(); } }
        catch { play.hidden = false; showControls(); }
    }
    play.onclick = enablePlayback;
    function volumeChanged() {
        volume.value = String(video.volume);
        byId('volumeProgress').style.width = video.volume * 100 + '%';
        const muted = video.muted || video.volume === 0;
        byId('mute').setAttribute('aria-label', muted ? 'Unmute' : 'Mute');
        byId('mute').setAttribute('aria-pressed', String(muted));
        byId('muteIcon').textContent = '';
        byId('muteIcon').className = 'xlargePaperIconButton material-icons ' + (muted ? 'volume_off' : 'volume_up');
    }
    volume.oninput = () => { video.volume = Number(volume.value); video.muted = false; volumeChanged(); };
    byId('mute').onclick = () => { video.muted = !(video.muted || video.volume === 0); if (!video.muted && video.volume === 0) video.volume = 0.5; volumeChanged(); };
    video.addEventListener('volumechange', volumeChanged); volumeChanged();
    async function fullscreen() {
        try {
            if (document.fullscreenElement === player) await document.exitFullscreen();
            else await player.requestFullscreen();
        } catch { message('Fullscreen is unavailable in this browser.'); }
    }
    byId('fullscreen').hidden = typeof player.requestFullscreen !== 'function';
    byId('fullscreen').onclick = fullscreen;
    document.addEventListener('fullscreenchange', () => {
        const full = document.fullscreenElement === player;
        byId('fullscreen').setAttribute('aria-label', full ? 'Exit fullscreen' : 'Enter fullscreen');
        byId('fullscreenIcon').className = 'xlargePaperIconButton material-icons ' + (full ? 'fullscreen_exit' : 'fullscreen');
        showControls();
    });
    function captionsPanel() {
        const panel = byId('subtitleSettings'); panel.hidden = !panel.hidden;
        byId('subtitlesButton').setAttribute('aria-expanded', String(!panel.hidden));
        if (!panel.hidden) select.focus();
        showControls();
    }
    byId('subtitlesButton').onclick = captionsPanel;
    function updateSubtitles() {
        if (!state || !generation) return;
        const tracks = state.subtitles || [], burned = state.burnedSubtitleIndex != null;
        const signature = JSON.stringify([tracks, burned]);
        if (signature !== trackSignature) {
            trackSignature = signature; select.replaceChildren();
            for (const [value, label] of [['auto', 'Follow party'], ['-1', 'Off'], ...tracks.map(t => [String(t.index), t.label])]) {
                const option = document.createElement('option'); option.value = value; option.textContent = label; select.append(option);
            }
            if (![...select.options].some(o => o.value === subtitleChoice)) subtitleChoice = 'auto';
            select.value = subtitleChoice;
        }
        select.disabled = burned;
        if (burned) {
            clearSubtitle(); subtitleStatus.textContent = 'The party’s styled/bitmap subtitles are rendered into the shared video. Their selection follows the signed-in player.'; return;
        }
        const index = subtitleChoice === 'auto' ? state.selectedSubtitleIndex : Number(subtitleChoice);
        const info = tracks.find(t => t.index === index);
        const key = info ? generation + ':' + index : null;
        if (subtitleKey === key) return;
        clearSubtitle();
        if (!info) { subtitleStatus.textContent = 'Subtitles off. Styled/bitmap tracks follow the party when selected by its player.'; return; }
        subtitleKey = key;
        const offset = state.offset;
        const track = document.createElement('track'); subtitleElement = track;
        track.kind = 'subtitles'; track.label = info.label;
        if (/^[a-z]{2,3}(?:-[a-z0-9]+)*$/i.test(info.language || '')) track.srclang = info.language;
        track.src = new URL(`Guest/media/${generation}/subtitles/${index}.vtt`, base).href;
        track.addEventListener('load', () => {
            if (subtitleElement !== track || subtitleKey !== key) return;
            // Native subtitles preserve movie timestamps. HLS starts at this generation's seek offset.
            for (const cue of Array.from(track.track.cues || [])) {
                if (cue.endTime <= offset) track.track.removeCue(cue);
                else { cue.startTime = Math.max(0, cue.startTime - offset); cue.endTime = Math.max(cue.startTime, cue.endTime - offset); }
            }
            track.track.mode = 'showing'; subtitleStatus.textContent = info.label;
        });
        track.addEventListener('error', () => { if (subtitleElement === track) subtitleStatus.textContent = 'This subtitle track is unavailable. Try another track.'; });
        video.append(track); track.track.mode = 'hidden'; subtitleStatus.textContent = 'Loading subtitles…';
    }
    select.onchange = () => { subtitleChoice = select.value; subtitleKey = undefined; updateSubtitles(); };
    const clock = value => {
        const seconds = Math.max(0, Math.floor(value || 0)), minutes = Math.floor(seconds / 60);
        return (minutes >= 60 ? Math.floor(minutes / 60) + ':' + String(minutes % 60).padStart(2, '0') : String(minutes)) + ':' + String(seconds % 60).padStart(2, '0');
    };
    function targetTime() { return Math.max(0, (state?.position || 0) - (state?.offset || 0) + (desiredPlaying ? Math.min(1.5, (performance.now() - receivedAt) / 1000) : 0)); }
    function followPosition() {
        if (!generation || !state) return;
        const target = targetTime();
        for (let i = 0; i < video.buffered.length; i++) if (target >= video.buffered.start(i) && target <= video.buffered.end(i) && Math.abs(video.currentTime - target) > 1.5) { video.currentTime = target; break; }
    }
    // No local transport controls. These guards also restore accidental browser/media-key changes.
    video.addEventListener('pause', () => { if (active && desiredPlaying && generation && !video.ended) void enablePlayback(); });
    video.addEventListener('seeking', followPosition);
    video.addEventListener('ratechange', () => { if (video.playbackRate !== 1) video.playbackRate = 1; });
    video.addEventListener('contextmenu', e => e.preventDefault());
    player.addEventListener('keydown', e => {
        showControls();
        if (e.ctrlKey || e.metaKey || e.altKey || e.target.closest('button,input,select')) return;
        const key = e.key.toLowerCase();
        if (key === 'm') byId('mute').click();
        else if (key === 'f') void fullscreen();
        else if (key === 'c') captionsPanel();
        else if (['arrowup', 'arrowdown', '+', '-'].includes(key)) { video.volume = Math.min(1, Math.max(0, video.volume + (['arrowup', '+'].includes(key) ? 0.05 : -0.05))); }
        else if (![' ', 'k', 'j', 'l', 'arrowleft', 'arrowright', 'home', 'end', 'mediaplaypause', 'mediatracknext', 'mediatrackprevious'].includes(key)) return;
        e.preventDefault(); e.stopPropagation();
    });
    async function poll() {
        if (!active) return;
        const mine = epoch;
        try {
            const next = await request('State');
            if (!active || mine !== epoch) return;
            state = next; receivedAt = performance.now(); desiredPlaying = state.playing && !state.paused;
            message(!state.playing ? 'Waiting for the party to play a video…' : state.paused ? 'Party paused.' : state.title || 'Watching');
            byId('position').textContent = clock(state.position); byId('duration').textContent = clock(state.duration);
            const progress = state.duration ? Math.min(100, Math.max(0, state.position / state.duration * 100)) : 0;
            byId('progress').setAttribute('aria-valuenow', String(Math.round(progress)));
            byId('progress').setAttribute('aria-valuetext', `${clock(state.position)} of ${clock(state.duration)}`);
            byId('positionProgress').style.width = progress + '%';
            if (!state.playing) clearStream();
            else if (state.paused) { hls?.stopLoad(); video.pause(); play.hidden = true; select.disabled = true; subtitleStatus.textContent = 'Party paused. Subtitle selection is available when it resumes.'; showControls(); }
            else if (state.ready && state.generation !== generation) {
                clearStream(); generation = state.generation;
                const url = new URL(`Guest/media/${generation}/index.m3u8`, base).href;
                if (window.Hls?.isSupported()) {
                    hls = new Hls({ enableWorker: true, backBufferLength: 10, maxBufferLength: 12 });
                    hls.loadSource(url); hls.attachMedia(video);
                    hls.on(Hls.Events.MANIFEST_PARSED, enablePlayback);
                    hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) { clearStream(); message('Reconnecting to party stream…'); } });
                } else if (video.canPlayType('application/vnd.apple.mpegurl')) { video.src = url; void enablePlayback(); }
                else { end('This browser does not support HLS playback.'); return; }
            }
            if (state.ready && generation === state.generation) { updateSubtitles(); followPosition(); }
        } catch (error) {
            if (!active || mine !== epoch) return;
            if ([401, 403, 404, 410].includes(error.status)) { end('This guest session has ended.'); return; }
            desiredPlaying = false; clearStream(); message(error.message);
        }
        if (active && mine === epoch) timer = setTimeout(poll, 1000);
    }
    async function start() {
        epoch++; active = true; player.hidden = false; status.hidden = true; document.querySelector('h1').hidden = true;
        byId('actions').hidden = true; player.focus(); showControls(); await poll();
    }
    join.onclick = async () => {
        join.disabled = true;
        try { await request('Redeem', { Token: token }); await start(); }
        catch (error) { message(error.message); join.disabled = false; }
    };
    byId('leave').onclick = async () => { end('You left the party.'); try { await request('Leave', {}); } catch { message('Playback stopped. The guest seat will expire if the server could not be reached.'); } };
    if (!token) void start();
    window.addEventListener('pagehide', () => { epoch++; active = false; desiredPlaying = false; clearTimeout(timer); clearTimeout(hideTimer); clearStream(); });
})();

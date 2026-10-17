/* Guest credentials stay in HttpOnly cookies. No authenticated Jellyfin app, token or playback API is loaded. */
(() => {
    'use strict';
    const base = new URL('./', location.href);
    history.replaceState(null, '', location.pathname);
    const byId = id => document.getElementById(id);
    const video = byId('video'), player = byId('videoOsdPage'), status = byId('status');
    const play = byId('play');
    const volume = byId('volume'), select = byId('subtitles'), subtitleStatus = byId('subtitleStatus');
    const preview = document.body.dataset.guestPreview === 'true' ? window.PrivateSyncPlayGuestPreview : null;
    const renderer = window.PrivateSyncPlayGuestPlayer.mount(player, (feature, value) => {
        if (feature === 'volume') { if (renderer.can('volume')) { video.volume = value; video.muted = false; } }
        else if (feature === 'mute') byId('mute').click();
        else if (feature === 'subtitles') captionsPanel();
        else if (feature === 'fullscreen') void fullscreen();
    }, detail => preview?.select(detail));
    renderer.apply(null);
    function applyPolicy(policy) {
        renderer.apply(policy, !!preview && preview.editing);
        if (preview) renderer.editable();
        if (!renderer.can('subtitles')) { clearSubtitle(); byId('subtitlesButton').setAttribute('aria-expanded','false'); }
        byId('fullscreen').hidden ||= typeof player.requestFullscreen !== 'function';
        if (typeof player.requestFullscreen !== 'function') player.querySelectorAll('[data-guest-feature="fullscreen"]').forEach(node => node.hidden = true);
        volumeChanged();
    }
    const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
        ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : x;
    let hls, generation, active = false, timer, hideTimer, epoch = 0, state, receivedAt = 0;
    let desiredPlaying = false, subtitleKey, subtitleElement, subtitleChoice = 'auto', trackSignature;
    let bandwidthKbps, attachedAt = 0, feedbackAt = 0, stalledAt = 0;
    let nativeAbsoluteTimeline = false, timelineKnown = false;
    const mobile = window.matchMedia('(max-width: 50em)');
    function layout() {
        document.documentElement.classList.toggle('layout-mobile', mobile.matches);
        document.documentElement.classList.toggle('layout-desktop', !mobile.matches);
    }
    mobile.addEventListener('change', layout); layout();
    function message(text) { status.textContent = text; byId('playerStatus').textContent = text; }
    function showControls() {
        player.classList.remove('privateSyncPlayControlsHidden'); clearTimeout(hideTimer);
        if (!preview && active && desiredPlaying && play.hidden && byId('subtitleSettings').hidden) hideTimer = setTimeout(() => {
            const focus = document.activeElement;
            if (focus === player || focus === video || !player.contains(focus)) player.classList.add('privateSyncPlayControlsHidden');
        }, 5000);
    }
    player.addEventListener('pointermove', showControls); player.addEventListener('pointerdown', showControls);
    player.addEventListener('focusin', showControls);
    async function request(path, body) {
        if (preview) throw new Error('Server requests are unavailable in the preview.');
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
        if (subtitleElement) { subtitleElement.track.mode = 'disabled'; if (preview) URL.revokeObjectURL(subtitleElement.src); subtitleElement.remove(); subtitleElement = null; }
        subtitleKey = null;
    }
    function clearStream() {
        generation = null; clearSubtitle();
        bandwidthKbps = undefined; attachedAt = 0; feedbackAt = 0; stalledAt = 0;
        nativeAbsoluteTimeline = false; timelineKnown = false;
        if (hls) { hls.destroy(); hls = null; }
        video.pause(); video.removeAttribute('src'); video.load();
    }
    function end(text) {
        epoch++; active = false; desiredPlaying = false;
        document.body.classList.remove('privateSyncPlayWatching');
        clearTimeout(timer); clearTimeout(hideTimer); clearStream();
        player.hidden = true; status.hidden = false; play.hidden = true; document.querySelector('h1').hidden = false;
        message(text);
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
        renderer.volume(video.volume, muted);
    }
    volume.oninput = () => { if (!renderer.can('volume')) return; video.volume = Number(volume.value); video.muted = false; volumeChanged(); };
    byId('mute').onclick = () => { if (!renderer.can('mute')) return; video.muted = !(video.muted || video.volume === 0); if (!video.muted && video.volume === 0) video.volume = 0.5; volumeChanged(); };
    video.addEventListener('volumechange', volumeChanged); volumeChanged();
    async function fullscreen() {
        if (!renderer.can('fullscreen')) return;
        try {
            if (document.fullscreenElement === player) await document.exitFullscreen();
            else await player.requestFullscreen();
        } catch { message('Fullscreen is unavailable in this browser.'); }
    }
    byId('fullscreen').hidden ||= typeof player.requestFullscreen !== 'function';
    byId('fullscreen').onclick = fullscreen;
    document.addEventListener('fullscreenchange', () => {
        const full = document.fullscreenElement === player;
        byId('fullscreen').setAttribute('aria-label', full ? 'Exit fullscreen' : 'Enter fullscreen');
        byId('fullscreenIcon').className = 'xlargePaperIconButton material-icons ' + (full ? 'fullscreen_exit' : 'fullscreen');
        player.querySelectorAll('[data-guest-extension] .fullscreen, [data-guest-extension] .fullscreen_exit').forEach(icon => {
            icon.className = 'xlargePaperIconButton material-icons ' + (full ? 'fullscreen_exit' : 'fullscreen');
        });
        showControls();
    });
    function captionsPanel() {
        if (!renderer.can('subtitles')) return;
        const panel = byId('subtitleSettings'); panel.hidden = !panel.hidden;
        player.querySelectorAll('.btnSubtitles').forEach(button => button.setAttribute('aria-expanded', String(!panel.hidden)));
        if (!panel.hidden) select.focus({ preventScroll:!!preview });
        showControls();
    }
    byId('subtitlesButton').onclick = captionsPanel;
    function updateSubtitles() {
        if (!renderer.can('subtitles') || !state || !generation) return;
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
        const offset = timelineOffset();
        const track = document.createElement('track'); subtitleElement = track;
        track.kind = 'subtitles'; track.label = info.label;
        if (/^[a-z]{2,3}(?:-[a-z0-9]+)*$/i.test(info.language || '')) track.srclang = info.language;
        track.src = preview ? URL.createObjectURL(new Blob(['WEBVTT\n\n00:00:00.000 --> 00:10:00.000\nExample subtitle\n'], {type:'text/vtt'}))
            : new URL(`Guest/media/${generation}/subtitles/${index}.vtt`, base).href;
        if (preview) track.addEventListener('load', () => URL.revokeObjectURL(track.src), {once:true});
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
    function timelineOffset() { return nativeAbsoluteTimeline ? 0 : (state?.offset || 0); }
    function targetTime() { return Math.max(0, (state?.position || 0) - timelineOffset() + (desiredPlaying ? Math.min(1.5, (performance.now() - receivedAt) / 1000) : 0)); }
    function followPosition() {
        if (preview || !generation || !state) return;
        // HLS.js rebases a borrowed window to zero; native HLS may retain the original TS timeline.
        // Infer that axis from the initial seekable range, not browser sniffing or a guessed seek.
        if (!hls && state.playbackMethod === 'Shared' && !timelineKnown && video.seekable.length) {
            nativeAbsoluteTimeline = state.offset > 1.5 && Math.abs(video.seekable.start(0) - state.offset) < 1.5;
            timelineKnown = true;
            if (nativeAbsoluteTimeline) { clearSubtitle(); updateSubtitles(); }
        }
        const target = targetTime();
        for (let i = 0; i < video.buffered.length; i++) if (target >= video.buffered.start(i) && target <= video.buffered.end(i) && Math.abs(video.currentTime - target) > 1.5) { video.currentTime = target; break; }
    }
    video.addEventListener('loadedmetadata', followPosition);
    video.addEventListener('waiting', () => { if (active && desiredPlaying && video.currentTime > 0) stalledAt = performance.now(); });
    function networkStateRequest() {
        const now = performance.now();
        if (preview || !generation || !desiredPlaying || !play.hidden || now - attachedAt < 10000 || now - feedbackAt < 5000) return 'State';
        let buffer = 0;
        for (let i = 0; i < video.buffered.length; i++) if (video.currentTime >= video.buffered.start(i) && video.currentTime <= video.buffered.end(i)) buffer = video.buffered.end(i) - video.currentTime;
        const parameters = new URLSearchParams({ generation, bufferSeconds:String(Math.min(120, Math.max(0, buffer))), stalled:String(stalledAt > feedbackAt) });
        if (bandwidthKbps) parameters.set('bandwidthKbps',String(bandwidthKbps));
        feedbackAt = now;
        return 'State?' + parameters;
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
        else if (['arrowup', 'arrowdown', '+', '-'].includes(key)) { if (renderer.can('volume')) video.volume = Math.min(1, Math.max(0, video.volume + (['arrowup', '+'].includes(key) ? 0.05 : -0.05))); }
        else if (![' ', 'k', 'j', 'l', 'arrowleft', 'arrowright', 'home', 'end', 'mediaplaypause', 'mediatracknext', 'mediatrackprevious'].includes(key)) return;
        e.preventDefault(); e.stopPropagation();
    });
    async function poll() {
        if (!active) return;
        const mine = epoch;
        try {
            const next = preview ? preview.state(video) : await request(networkStateRequest());
            if (!active || mine !== epoch) return;
            state = next; receivedAt = performance.now(); desiredPlaying = state.playing && !state.paused;
            applyPolicy(state.player);
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
                attachedAt = performance.now();
                const url = new URL(`Guest/media/${generation}/index.m3u8`, base).href;
                const sampleGeneration = generation;
                if (window.Hls?.isSupported()) {
                    hls = new Hls({ enableWorker: true, backBufferLength: 10, maxBufferLength: 12, startPosition:Math.max(0, state.position - state.offset) });
                    hls.loadSource(url); hls.attachMedia(video);
                    hls.on(Hls.Events.MANIFEST_PARSED, () => { if (generation === sampleGeneration) void enablePlayback(); });
                    hls.on(Hls.Events.FRAG_LOADED, (_, data) => {
                        if (generation !== sampleGeneration) return;
                        if (data.frag?.type && data.frag.type !== 'main') return;
                        const stats = data.frag?.stats || data.stats;
                        const elapsed = stats?.loading?.end - stats?.loading?.start;
                        if (!(elapsed > 0) || !(stats?.loaded > 0)) return;
                        const sample = Math.min(200000, Math.max(1, Math.round(stats.loaded * 8 / elapsed)));
                        bandwidthKbps = bandwidthKbps ? Math.round(sample < bandwidthKbps ? sample : bandwidthKbps * .7 + sample * .3) : sample;
                    });
                    hls.on(Hls.Events.ERROR, (_, data) => { if (generation === sampleGeneration && data.fatal) { clearStream(); message('Reconnecting to party stream…'); } });
                } else if (video.canPlayType('application/vnd.apple.mpegurl')) { video.src = url; void enablePlayback(); }
                else { end('This browser does not support HLS playback.'); return; }
            }
            if (preview) { generation = 'preview'; updateSubtitles(); }
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
        document.body.classList.add('privateSyncPlayWatching');
        player.focus(); showControls(); await poll();
    }
    byId('leave').onclick = async () => {
        if (preview) { message('Preview: this action leaves the party in the real player.'); return; }
        end('You left the party.'); try { await request('Leave', {}); } catch { message('Playback stopped. The guest seat will expire if the server could not be reached.'); }
    };
    if (preview) { preview.configure(applyPolicy); preview.start(video); void start(); void enablePlayback(); }
    else void start();
    window.addEventListener('pagehide', () => { epoch++; active = false; desiredPlaying = false; clearTimeout(timer); clearTimeout(hideTimer); clearStream(); });
})();

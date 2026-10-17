/* Public, synthetic preview: no guest redemption, credentials, private media or API requests. */
(() => {
    'use strict';
    let port, policy = { features:[], controls:[] }, editing = true, apply = () => {}, dispose = () => {};
    document.body.classList.add('privateSyncPlayPreview');
    window.addEventListener('message', event => {
        if (event.source !== window.parent || event.data?.type !== 'PrivateSyncPlay.Preview.Connect' || event.ports.length !== 1 || port) return;
        port = event.ports[0];
        port.onmessage = message => {
            if (message.data?.type !== 'configure') return;
            policy = window.PrivateSyncPlayGuestPlayer.policy(message.data.policy); editing = message.data.editing === true; apply(policy);
        };
        port.postMessage({ type:'ready' }); port.start();
    });
    window.PrivateSyncPlayGuestPreview = {
        get policy() { return policy; },
        get editing() { return editing; },
        configure(handler) { apply = handler; handler(policy); },
        select(detail) { port?.postMessage({ type:'select', ...detail }); },
        start(video) {
            const canvas = document.createElement('canvas'); canvas.width = 960; canvas.height = 540;
            const context = canvas.getContext('2d'); let frame, stream;
            const start = performance.now();
            const paint = () => {
                const css = getComputedStyle(document.body);
                context.fillStyle = css.backgroundColor === 'rgba(0, 0, 0, 0)' ? getComputedStyle(document.documentElement).backgroundColor : css.backgroundColor;
                context.fillRect(0, 0, canvas.width, canvas.height);
                context.fillStyle = css.color; context.font = '28px ' + css.fontFamily;
                context.fillText('Guest player · live sample', 32, 90);
                context.fillText('No account or library media is used in this preview.', 32, 135);
                context.fillRect(32 + ((performance.now() - start) / 15 % 800), 200, 40, 40);
                frame = requestAnimationFrame(paint);
            };
            paint();
            if (typeof canvas.captureStream === 'function') { stream = canvas.captureStream(24); video.srcObject = stream; }
            dispose = () => { cancelAnimationFrame(frame); stream?.getTracks().forEach(t => t.stop()); };
        },
        state(video) { return { playing:true, paused:false, title:'Example video', position:video.currentTime || 0, duration:600, offset:0,
            ready:false, generation:'preview', subtitles:[{index:2,label:'Example captions',language:'en'}], selectedSubtitleIndex:2, player:policy }; }
    };
    window.addEventListener('pagehide', () => { dispose(); port?.close(); });
})();

/* Loaded synchronously before Jellyfin: its player also listens in window's capture phase. */
(() => {
    'use strict';
    if (window.privateSyncPlayInputGuard) return;
    window.privateSyncPlayInputGuard = true;
    function guard(event) {
        if (!document.querySelector('.privateSyncPlayDialog[open]')) return;
        // Do not prevent keyboard/wheel defaults: typing, focus, Escape and native scrolling must still work.
        // Stop at window, before any player capture listeners (not merely at the dialog during bubbling).
        if (event.type === 'command') event.preventDefault();
        event.stopImmediatePropagation();
    }
    for (const type of ['keydown','keyup','keypress','wheel','mousewheel','command'])
        window.addEventListener(type, guard, {capture:true, passive:type === 'wheel' || type === 'mousewheel'});
})();

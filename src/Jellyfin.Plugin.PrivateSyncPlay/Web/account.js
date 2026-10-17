/* Keep bearer invitations out of Jellyfin's route hash, URLs, referrers and account tokens. */
(() => {
    'use strict';
    const root = new URL('./', location.href);
    const token = location.hash.slice(1);
    history.replaceState(null, '', location.pathname);
    try {
        if (!/^[a-f0-9]{64}$/i.test(token)) throw new Error('Invalid invitation.');
        sessionStorage.setItem('PrivateSyncPlay.Invite:' + root.pathname, JSON.stringify({ token, expires: Date.now() + 30 * 60000 }));
        location.replace(new URL('../web/index.html', root).href);
    } catch {
        document.getElementById('status').textContent = 'Could not continue to sign-in. Allow session storage for this server, then reopen the invitation.';
    }
})();

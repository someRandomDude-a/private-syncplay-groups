/* Account-aware invitation landing only. The separate guest player never loads this script or account credentials. */
(() => {
    'use strict';
    const root = new URL('./', location.href), server = new URL('../', root), token = location.hash.slice(1);
    history.replaceState(null, '', location.pathname);
    const join = document.getElementById('join'), signIn = document.getElementById('signin'), status = document.getElementById('status');
    let disposed = false, navigating = false;
    window.addEventListener('pagehide', () => disposed = true);
    // A second invitation can be a same-document hash navigation. Recheck its identity and policy from scratch.
    window.addEventListener('hashchange', () => { if (!disposed && !navigating) { navigating = true; location.reload(); } });
    const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
        ? Object.fromEntries(Object.entries(x).map(([k,v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : x;
    const guid = x => typeof x === 'string' && /^[a-f0-9]{32}$/i.test(x.replace(/-/g,'')) ? x.replace(/-/g,'').toLowerCase() : null;
    async function json(url, options = {}) {
        const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 8000);
        try {
            const response = await fetch(url, { credentials:'omit', cache:'no-store', redirect:'error', referrerPolicy:'no-referrer', ...options, signal:controller.signal });
            if (!response.ok) throw Object.assign(new Error('Request unavailable.'), { status:response.status });
            return response.status === 204 ? null : normalize(await response.json());
        } finally { clearTimeout(timeout); }
    }
    function matchesAddress(address) {
        if (typeof address !== 'string' || address.length > 2048) return false;
        try {
            const url = new URL(address);
            return url.origin === server.origin && !url.username && !url.password && !url.search && !url.hash
                && url.pathname.replace(/\/+$/,'') === server.pathname.replace(/\/+$/,'');
        } catch { return false; }
    }
    function savedAccount(serverId) {
        // The native credential record is server-scoped. Never take the first token from a multi-server client.
        for (const storageName of ['sessionStorage','localStorage']) {
            try {
                const raw = window[storageName].getItem('jellyfin_credentials');
                if (!raw || raw.length > 65536) continue;
                const entries = JSON.parse(raw)?.Servers;
                if (!Array.isArray(entries) || entries.length > 64) continue;
                const matches = entries.filter(s => s && guid(s.Id) === serverId && [s.ManualAddress,s.LocalAddress,s.RemoteAddress].some(matchesAddress));
                if (matches.length !== 1) continue;
                const entry = matches[0];
                if (guid(entry.UserId) && typeof entry.AccessToken === 'string' && /^[A-Za-z0-9._~+/=-]{1,4096}$/.test(entry.AccessToken))
                    return { user:guid(entry.UserId), token:entry.AccessToken };
            } catch { /* Blocked/malformed storage is not proof of a signed-in account. */ }
        }
        return null;
    }
    async function signedIn() {
        try {
            const info = await json(new URL('System/Info/Public', server));
            const serverId = guid(info?.id);
            if (!serverId) return false;
            const account = savedAccount(serverId);
            if (!account) return false;
            const user = await json(new URL('Users/Me', server), { headers:{ Authorization:`MediaBrowser Token="${account.token}"` } });
            return guid(user?.id) === account.user && user?.policy?.isDisabled !== true
                && (!user?.serverId || guid(user.serverId) === serverId);
        } catch { return false; } // Expired/revoked tokens and network errors must not auto-admit anyone.
    }
    function accountHandoff() {
        if (disposed || navigating) return;
        navigating = true;
        location.replace(signIn.href); // Only the invitation is handed off, never the Jellyfin account token.
    }
    signIn.addEventListener('click', () => navigating = true);
    async function options() {
        join.hidden = true; join.disabled = true;
        try {
            const result = await json(new URL('Guest/Options', root), { method:'POST', credentials:'same-origin',
                headers:{'Content-Type':'application/json'}, body:JSON.stringify({Token:token}) });
            if (disposed || navigating) return;
            join.hidden = result?.allowGuests !== true; join.disabled = join.hidden;
            status.textContent = join.hidden ? 'Sign in to join this party.' : 'Sign in to join, or watch as a guest.';
        } catch {
            if (!disposed && !navigating) status.textContent = 'This invitation is unavailable or could not be checked. Sign in to continue.';
        }
    }
    join.onclick = async () => {
        if (join.hidden || join.disabled || disposed || navigating) return;
        join.disabled = true;
        if (await signedIn()) { accountHandoff(); return; }
        if (disposed || navigating) return;
        try {
            await json(new URL('Guest/Redeem', root), { method:'POST', credentials:'same-origin',
                headers:{'Content-Type':'application/json'}, body:JSON.stringify({Token:token}) });
            if (disposed || navigating) return;
            navigating = true; location.replace(new URL('Player', root).href);
        } catch { await options(); }
    };
    async function initialize() {
        if (!token) { navigating = true; location.replace(new URL('Player', root).href); return; }
        if (!/^[a-f0-9]{64}$/i.test(token)) { status.textContent = 'This invitation is invalid. Sign in to continue.'; return; }
        signIn.href = new URL('Account', root).href + '#' + encodeURIComponent(token);
        if (await signedIn()) accountHandoff();
        else if (!disposed && !navigating) await options();
    }
    void initialize();
})();

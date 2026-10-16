(() => {
    'use strict';
    if (window.privateSyncPlayLoaded) return;
    window.privateSyncPlayLoaded = true;
    const root = new URL('../', document.currentScript.src);
    let dialog, button;
    const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
        ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : x;
    function api() { return window.ApiClient; }
    async function request(path, method = 'GET', body) {
        if (!api()?.accessToken()) throw new Error('Sign in to Jellyfin first.');
        const result = await api().ajax({ url: api().getUrl(path), type: method,
            data: body === undefined ? undefined : JSON.stringify(body), contentType: 'application/json', dataType: method === 'GET' || path.endsWith('/Invites') || path.endsWith('/Groups') ? 'json' : undefined });
        return normalize(result);
    }
    function element(tag, text, parent) { const e = document.createElement(tag); if (text) e.textContent = text; if (parent) parent.append(e); return e; }
    function action(label, parent, run) {
        const b = element('button', label, parent); b.type = 'button'; b.className = 'raised button-submit';
        b.onclick = async () => { b.disabled = true; try { await run(); } catch (error) { alert(error.message || 'Request failed. Check your permissions and limits.'); } finally { b.disabled = false; } };
        return b;
    }
    function field(label, type, value, parent) {
        const row = element('label', label + ' ', parent); row.style.display = 'block'; row.style.margin = '1em 0';
        const input = element('input', null, row); input.type = type; input.value = value; return input;
    }
    async function show() {
        const me = await request('PrivateSyncPlay/Me');
        if (dialog) dialog.remove();
        dialog = element('dialog', null, document.body);
        Object.assign(dialog.style, { background: '#171b22', color: '#eee', padding: '2em', borderRadius: '12px', maxWidth: '550px', maxHeight: '85vh' });
        element('h2', 'Watch parties', dialog);
        action('Close', dialog, () => dialog.close());
        if (me.party) {
            element('p', `${me.party.mode} party · ${me.party.viewers}/${me.party.capacity} viewers`, dialog);
            if (me.party.code) action('Copy join code', dialog, () => navigator.clipboard.writeText(me.party.code));
            if (me.party.isOwner) {
                const guests = field('Allow guests', 'checkbox', '', dialog); guests.disabled = !me.canInviteGuests || location.protocol !== 'https:';
                if (!me.canInviteGuests) element('p', 'Guest invites are disabled by your administrator.', dialog);
                else if (location.protocol !== 'https:') element('p', 'Open Jellyfin over HTTPS to create usable guest invite links.', dialog);
                const uses = field('Maximum invite uses', 'number', me.maxInviteUses, dialog); uses.min = '1'; uses.max = me.maxInviteUses;
                const expiry = field('Expires in minutes (optional)', 'number', '', dialog); expiry.min = '1';
                action('Create invite link', dialog, async () => {
                    const invite = await request('PrivateSyncPlay/Invites', 'POST', { AllowGuests: guests.checked,
                        MaxUses: Number(uses.value), ExpiresAt: expiry.value ? new Date(Date.now() + Number(expiry.value) * 60000).toISOString() : null });
                    const url = new URL('Watch', root).href + '#' + invite.token;
                    const output = field('Invite link — copy now', 'text', url, dialog); output.readOnly = true; output.select();
                    action('Copy link', dialog, () => navigator.clipboard.writeText(url));
                    action('Revoke this invite', dialog, async () => { await request('PrivateSyncPlay/Invites/' + invite.id, 'DELETE'); output.value = 'Revoked'; });
                });
                for (const invite of me.party.invites) {
                    const row = element('p', `${invite.uses}/${invite.maxUses} uses · ${invite.allowGuests ? 'guests allowed' : 'accounts only'} `, dialog);
                    action('Revoke', row, async () => { await request('PrivateSyncPlay/Invites/' + invite.id, 'DELETE'); row.remove(); });
                }
            }
            action('Leave party', dialog, async () => { await request('SyncPlay/Leave', 'POST'); dialog.close(); });
        } else {
            const groups = await request('SyncPlay/List');
            for (const group of groups) action('Join ' + group.groupName, dialog, async () => {
                await request('SyncPlay/Join', 'POST', { GroupId: group.groupId }); dialog.close();
            });
            const code = field('Private join code', 'text', '', dialog); code.maxLength = 8;
            action('Join by code', dialog, async () => { await request('PrivateSyncPlay/JoinCode', 'POST', { Code: code.value.trim() }); dialog.close(); });
            if (me.canCreatePublic || me.canCreatePrivate) {
                element('h3', 'Create party', dialog);
                const name = field('Name', 'text', 'Watch party', dialog); name.maxLength = 200;
                const mode = element('select', null, dialog);
                for (const value of ['Public', 'Private']) if (me['canCreate' + value]) {
                    const option = element('option', value, mode); option.value = value === 'Public' ? '0' : '1'; option.selected = value === me.defaultMode;
                }
                const capacity = field('Maximum simultaneous viewers (including you)', 'number', me.maxViewers, dialog);
                capacity.min = '1'; capacity.max = me.maxViewers;
                action('Create', dialog, async () => {
                    await request('PrivateSyncPlay/Groups', 'POST', { Name: name.value, Mode: Number(mode.value), MaxViewers: Number(capacity.value) });
                    await show();
                });
            }
        }
        dialog.showModal();
    }
    let pending = new URLSearchParams(location.hash.slice(1)).get('privateSyncPlayInvite');
    if (pending) history.replaceState(null, '', location.pathname + location.search);
    // A standalone launcher avoids patching React internals and works in both web layouts.
    setInterval(() => {
        const signedIn = !!api()?.accessToken();
        if (signedIn && !button) {
            button = action('Watch parties', document.body, show);
            Object.assign(button.style, { position: 'fixed', right: '1rem', bottom: '1rem', zIndex: '9999', padding: '.7rem', borderRadius: '6px' });
        }
        if (button) button.hidden = !signedIn;
        if (!signedIn && dialog?.open) dialog.close();
        if (signedIn && pending) {
            const token = pending; pending = null;
            void request('PrivateSyncPlay/JoinInvite', 'POST', { Token: token }).then(show).catch(() => alert('Invitation invalid, full, expired, or media access denied.'));
        }
    }, 1000);
})();

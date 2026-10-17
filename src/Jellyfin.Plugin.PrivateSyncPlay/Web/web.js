(async () => {
    'use strict';
    if (window.privateSyncPlayLoaded) return;
    window.privateSyncPlayLoaded = true;
    const root = new URL('../', document.currentScript.src);
    const UI = await (window.privateSyncPlayUILoading ||= new Promise((resolve, reject) => {
        if (window.PrivateSyncPlayUI) { resolve(window.PrivateSyncPlayUI); return; }
        const script = document.createElement('script'); script.src = new URL('assets/ui.js', root).href;
        script.onload = () => resolve(window.PrivateSyncPlayUI); script.onerror = reject; document.head.append(script);
    }));
    let current, launcher;
    const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
        ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : x;
    function api() { return window.ApiClient; }
    async function request(path, method = 'GET', body) {
        if (!api()?.accessToken()) throw new Error('Sign in to Jellyfin first.');
        const result = await api().ajax({ url: api().getUrl(path), type: method,
            data: body === undefined ? undefined : JSON.stringify(body), contentType: 'application/json',
            dataType: method === 'GET' || path.endsWith('/Invites') || path.endsWith('/Groups') ? 'json' : undefined });
        return normalize(result);
    }
    function error(message) {
        if (!current?.dialog.open) { current = UI.dialog('Watch parties'); current.dialog.showModal(); }
        UI.message(current.feedback, message, true);
    }
    function action(label, parent, run, kind = 'default', id) {
        const button = UI.button(label, parent, run, kind); if (id) button.dataset.pspAction = id; return button;
    }
    function description(text, parent) { return UI.element('p', text, parent, 'fieldDescription'); }
    async function show() {
        const me = await request('PrivateSyncPlay/Me');
        const groups = me.party ? [] : await request('SyncPlay/List');
        current?.dialog.close();
        current = UI.dialog('Watch parties');
        const { content, dialog, feedback } = current;
        if (me.party) {
            description(`${me.party.mode} party · ${me.party.viewers}/${me.party.capacity} viewers`, content);
            if (me.party.code) action('Copy join code', content, async () => {
                await navigator.clipboard.writeText(me.party.code); UI.message(feedback, 'Join code copied.');
            });
            if (me.party.isOwner) {
                const guests = UI.field('Allow guests', 'checkbox', false, content);
                guests.disabled = !me.canInviteGuests || location.protocol !== 'https:';
                if (!me.canInviteGuests) description('Guest invites are disabled by your administrator.', content);
                else if (location.protocol !== 'https:') description('Open Jellyfin over HTTPS to create usable guest invite links.', content);
                const uses = UI.field('Maximum invite uses', 'number', me.maxInviteUses, content); uses.min = '1'; uses.max = me.maxInviteUses;
                const expiry = UI.field('Expires in minutes (optional)', 'number', '', content); expiry.min = '1';
                action('Create invite link', content, async () => {
                    if (!uses.reportValidity() || !expiry.reportValidity()) return;
                    const invite = await request('PrivateSyncPlay/Invites', 'POST', { AllowGuests: guests.checked,
                        MaxUses: Number(uses.value), ExpiresAt: expiry.value ? new Date(Date.now() + Number(expiry.value) * 60000).toISOString() : null });
                    const url = new URL('Watch', root).href + '#' + invite.token;
                    const output = UI.field('Invite link — copy now', 'text', url, content); output.readOnly = true; output.select();
                    action('Copy link', content, async () => { await navigator.clipboard.writeText(url); UI.message(feedback, 'Invite link copied.'); });
                    action('Revoke this invite', content, async () => {
                        await request('PrivateSyncPlay/Invites/' + invite.id, 'DELETE'); output.value = 'Revoked'; UI.message(feedback, 'Invitation revoked.');
                    }, 'delete');
                }, 'primary', 'create-invite');
                if (me.party.invites.length) {
                    UI.element('h3', 'Invitations', content, 'sectionTitle');
                    const list = UI.element('div', null, content, 'paperList');
                    for (const invite of me.party.invites) {
                        const row = UI.element('div', null, list, 'listItem listItem-border');
                        const body = UI.element('div', null, row, 'listItemBody');
                        UI.element('div', `${invite.uses}/${invite.maxUses} uses`, body, 'listItemBodyText');
                        UI.element('div', invite.allowGuests ? 'Guests allowed' : 'Accounts only', body, 'listItemBodyText secondary');
                        const revoke = action('Revoke', row, async () => { await request('PrivateSyncPlay/Invites/' + invite.id, 'DELETE'); row.remove(); });
                        revoke.className = 'emby-button button-flat listItemButton';
                    }
                }
            }
            action('Leave party', content, async () => { await request('SyncPlay/Leave', 'POST'); dialog.close(); }, 'default', 'leave-party');
        } else {
            if (groups.length) {
                UI.element('h3', 'Public parties', content, 'sectionTitle');
                const list = UI.element('div', null, content, 'paperList');
                for (const group of groups) {
                    const row = UI.element('div', null, list, 'listItem listItem-border');
                    const body = UI.element('div', null, row, 'listItemBody'); UI.element('div', group.groupName, body, 'listItemBodyText');
                    const join = action('Join', row, async () => { await request('SyncPlay/Join', 'POST', { GroupId: group.groupId }); dialog.close(); });
                    join.className = 'emby-button button-flat listItemButton';
                }
            }
            const code = UI.field('Private join code', 'text', '', content); code.maxLength = 8;
            action('Join by code', content, async () => { await request('PrivateSyncPlay/JoinCode', 'POST', { Code: code.value.trim() }); dialog.close(); });
            if (me.canCreatePublic || me.canCreatePrivate) {
                UI.element('h3', 'Create party', content, 'sectionTitle');
                const name = UI.field('Name', 'text', 'Watch party', content); name.maxLength = 200; name.required = true;
                const choices = ['Public', 'Private'].filter(value => me['canCreate' + value])
                    .map(value => ({ label: value, value: value === 'Public' ? '0' : '1' }));
                const mode = UI.select('Group visibility', choices, me.defaultMode === 'Public' ? '0' : '1', content);
                const capacity = UI.field('Maximum simultaneous viewers (including you)', 'number', me.maxViewers, content);
                capacity.min = '1'; capacity.max = me.maxViewers; capacity.required = true;
                action('Create', content, async () => {
                    if (!name.reportValidity() || !capacity.reportValidity()) return;
                    await request('PrivateSyncPlay/Groups', 'POST', { Name: name.value, Mode: Number(mode.value), MaxViewers: Number(capacity.value) });
                    await show();
                }, 'primary', 'create-party');
            }
        }
        dialog.showModal();
    }
    let pending = new URLSearchParams(location.hash.slice(1)).get('privateSyncPlayInvite');
    if (pending) history.replaceState(null, '', location.pathname + location.search);
    function refresh() {
        const signedIn = !!api()?.accessToken();
        if (!signedIn) { launcher?.remove(); launcher = null; current?.dialog.close(); return; }
        const native = ['button[aria-controls="app-sync-play-menu"]', '.headerSyncButton', '.btnSyncPlay',
            '.skinHeader .headerRight button.paper-icon-button-light', 'header .MuiToolbar-root button.MuiIconButton-root']
            .flatMap(selector => [...document.querySelectorAll(selector)])
            .find(node => node !== launcher && !node.classList.contains('privateSyncPlayLauncher') && node.getClientRects().length);
        if (native && (!launcher?.isConnected || launcher.parentNode !== native.parentNode)) {
            launcher?.remove();
            // Reuse the actual toolbar button's skin/MUI classes, but none of its React handlers.
            launcher = native.cloneNode(false); launcher.removeAttribute('id'); launcher.removeAttribute('style');
            launcher.classList.add('privateSyncPlayLauncher'); launcher.type = 'button';
            launcher.title = 'Watch parties'; launcher.setAttribute('aria-label', 'Watch parties');
            launcher.setAttribute('aria-haspopup', 'dialog'); launcher.setAttribute('aria-controls', 'privateSyncPlayDialog');
            const svg = native.querySelector('svg')?.cloneNode(false);
            if (svg) {
                svg.setAttribute('aria-hidden', 'true'); svg.removeAttribute('data-testid');
                const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                path.setAttribute('d', 'M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6M3 20v-3c0-4 12-4 12 0v3zM17 7h6v2h-6zM19 5h2v6h-2z');
                svg.append(path); launcher.append(svg);
            }
            else UI.element('span', null, launcher, 'material-icons group_add').setAttribute('aria-hidden', 'true');
            launcher.onclick = () => { void show().catch(e => error(e.message || 'Could not load watch parties.')); };
            native.after(launcher);
        }
        if (pending) {
            const token = pending; pending = null;
            void request('PrivateSyncPlay/JoinInvite', 'POST', { Token: token }).then(show)
                .catch(() => error('Invitation invalid, full, expired, or media access denied.'));
        }
    }
    setInterval(refresh, 1000);
    window.addEventListener('hashchange', () => current?.dialog.close());
    window.addEventListener('popstate', () => current?.dialog.close());
    refresh();
})();

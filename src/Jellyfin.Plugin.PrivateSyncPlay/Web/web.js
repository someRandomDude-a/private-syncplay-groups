(async () => {
    'use strict';
    if (window.privateSyncPlayLoaded) return;
    window.privateSyncPlayLoaded = true;
    const root = new URL('../', document.currentScript.src);
    const inviteKey = 'PrivateSyncPlay.Invite:' + root.pathname;
    // Capture before awaiting UI loading or login navigation. Never use an unknown route hash for new links.
    let pending, inviteAttempts = 0, retryAt = 0;
    try {
        const legacy = new URLSearchParams(location.hash.slice(1)).get('privateSyncPlayInvite');
        if (legacy && /^[a-f0-9]{64}$/i.test(legacy)) {
            sessionStorage.setItem(inviteKey, JSON.stringify({ token: legacy, expires: Date.now() + 30 * 60000 }));
            history.replaceState(null, '', location.pathname + location.search);
            // Older bookmarked account links may already have been parsed as a missing app route.
            location.replace(new URL('../web/index.html', root).href);
            return;
        }
        const saved = JSON.parse(sessionStorage.getItem(inviteKey) || 'null');
        if (saved?.expires > Date.now() && /^[a-f0-9]{64}$/i.test(saved.token)) pending = saved.token;
        else sessionStorage.removeItem(inviteKey);
    } catch { /* Storage failure must not break the normal Jellyfin web app. */ }
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
    function confirm(title, text, run) {
        const popup = UI.dialog(title); description(text, popup.content);
        action('Cancel', popup.content, () => popup.dialog.close());
        action('Confirm', popup.content, async () => { await run(); popup.dialog.close(); }, 'delete');
        popup.dialog.showModal();
    }
    function ownerManagement(me, content, data) {
        const base = 'PrivateSyncPlay/Groups/' + me.party.id;
        UI.element('h3', 'Party permissions', content, 'sectionTitle');
        description('These restrictions apply to this party only. The owner retains control; Jellyfin account permissions and administrator limits cannot be increased here.', content);
        const joins = UI.field('Accept new viewers', 'checkbox', data.policy.allowJoins, content);
        const guests = UI.field('Allow guest access', 'checkbox', data.policy.allowGuests, content);
        description('Disabling guest access disconnects existing guests and permanently revokes guest-enabled invitation links. Locking new viewers keeps existing viewers connected.', content);
        const control = UI.field('Allow participants to control playback', 'checkbox', data.policy.participantsControlPlayback, content);
        const queue = UI.field('Allow participants to edit the queue (requires playback control)', 'checkbox', data.policy.participantsEditQueue, content);
        const capacity = UI.field('Party viewer limit', 'number', data.capacity, content); capacity.min = 1; capacity.max = me.maxViewers; capacity.required = true;
        description('Guest player options are configured by the server administrator and apply equally to all guests. Party owners manage admission and removal, not guest player features.', content);
        action('Save party permissions', content, async () => {
            if (!capacity.reportValidity()) return;
            const save = async () => { await request(base + '/Policy', 'POST', { AllowJoins:joins.checked, AllowGuests:guests.checked,
                ParticipantsControlPlayback:control.checked, ParticipantsEditQueue:queue.checked, MaxViewers:Number(capacity.value) }); await show(); };
            if (data.policy.allowGuests && !guests.checked) confirm('Disable guest access?', 'All current guests will be disconnected and their guest-enabled invitation links revoked. Re-enabling access will not restore those sessions or links.', save);
            else await save();
        }, 'primary', 'save-party-permissions');
        UI.element('h3', 'Participants', content, 'sectionTitle');
        description('Account permissions apply to all devices of that account in this party. Read-only participants cannot stall the group with buffering reports. Queue editing also requires playback control.', content);
        const scroll = UI.element('div', null, content, 'privateSyncPlayTableScroll'); scroll.tabIndex = 0; scroll.setAttribute('role','region'); scroll.setAttribute('aria-label','Party participants');
        const table = UI.element('table', null, scroll, 'detailTable privateSyncPlayParticipants');
        UI.element('caption', 'Current party participants', table, 'fieldDescription');
        const head = UI.element('tr', null, UI.element('thead', null, table));
        for (const label of ['Participant','Type / devices','Permissions','Actions']) UI.element('th', label, head, 'detailTableHeaderCell').scope = 'col';
        const body = UI.element('tbody', null, table);
        for (const p of data.participants) {
            const row = UI.element('tr', null, body, 'listItem-border'); row.dataset.participant = p.id;
            UI.element('th', p.name + (p.isOwner ? ' (owner)' : ''), row, 'detailTableBodyCell').scope = 'row';
            UI.element('td', p.kind === 'guest' ? 'Guest' : `Account · ${p.sessions} device(s)`, row, 'detailTableBodyCell');
            const permissions = UI.element('td', null, row, 'detailTableBodyCell'), actions = UI.element('td', null, row, 'detailTableBodyCell');
            const path = base + '/Participants/' + p.id;
            if (p.isOwner) { description('Owner control', permissions); continue; }
            if (p.kind === 'account') {
                const playback = UI.field('Playback — ' + p.name, 'checkbox', p.playbackAllowed, permissions);
                const edits = UI.field('Queue editing — ' + p.name, 'checkbox', p.queueAllowed, permissions);
                playback.disabled = !data.policy.participantsControlPlayback;
                const checkQueue = () => edits.disabled = playback.disabled || !playback.checked || !data.policy.participantsEditQueue;
                playback.onchange = checkQueue; checkQueue();
                action('Save permissions', actions, async () => { await request(path + '/Permissions', 'POST', {Kind:'account',ControlPlayback:playback.checked,EditQueue:edits.checked}); await show(); }, 'default', 'save-member-permissions');
                action('Kick and block', actions, () => confirm('Remove ' + p.name + '?', 'All of this account’s devices will leave the party and be blocked from rejoining it by code, invitation or the public list. This does not ban their Jellyfin account.', async () => { await request(path, 'DELETE'); await show(); }), 'delete', 'kick-member');
            } else {
                description('Guest player options: administrator-managed', permissions);
                action('Kick and revoke link', actions, () => confirm('Remove this guest?', `The guest’s invitation will be revoked. ${p.sharedInviteViewers} guest session(s) using that same link will be disconnected, and the link can no longer admit anyone. Anonymous people cannot be identified across new browsers or new invitations.`, async () => { await request(path, 'DELETE'); await show(); }), 'delete', 'kick-guest');
            }
        }
        if (data.blocked.length) {
            UI.element('h3', 'Blocked from this party', content, 'sectionTitle');
            for (const account of data.blocked) {
                const row = UI.element('div', null, content, 'listItem listItem-border'); UI.element('div', account.name, row, 'listItemBody');
                action('Allow rejoining', row, async () => { await request(base + '/Blocked/' + account.id + '/Unblock', 'POST', {}); await show(); });
            }
        }
        action('Refresh participants', content, show, 'default', 'refresh-participants');
        action('End party for everyone', content, () => confirm('End this party?', 'All participants will leave, invitations will be revoked, and guest streams will end.', async () => {
            await request(base, 'DELETE'); current?.dialog.close();
        }), 'delete', 'end-party');
    }
    async function show() {
        const me = await request('PrivateSyncPlay/Me');
        const groups = me.party ? [] : await request('SyncPlay/List');
        const management = me.party?.isOwner ? await request('PrivateSyncPlay/Groups/' + me.party.id + '/Management') : null;
        current?.dialog.close();
        current = UI.dialog('Watch parties');
        const { content, dialog, feedback } = current;
        if (me.party) {
            description(`${me.party.mode} party · ${me.party.viewers}/${me.party.capacity} viewers`, content);
            if (me.party.code) action('Copy join code', content, async () => {
                await navigator.clipboard.writeText(me.party.code); UI.message(feedback, 'Join code copied.');
            });
            if (me.party.isOwner) {
                ownerManagement(me, content, management);
                UI.element('h3', 'Create invitation', content, 'sectionTitle');
                const guests = UI.field('Allow guests', 'checkbox', false, content);
                guests.disabled = !me.canInviteGuests || me.party.allowGuests === false || location.protocol !== 'https:';
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
            } else description(`Your party permissions: playback ${me.party.canControlPlayback ? 'allowed' : 'read only'}; queue editing ${me.party.canEditQueue ? 'allowed' : 'not allowed'}.`, content);
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
        if (pending && native && Date.now() >= retryAt) {
            // A web client can manage more than one server. Never send this secret to another server.
            if (new URL(api().getUrl('PrivateSyncPlay/JoinInvite')).href !== new URL('JoinInvite', root).href) return;
            const token = pending; pending = null;
            // Jellyfin 12's SDK socket is separate from the legacy ApiClient.isWebSocketOpen flag.
            // Check the actual server-side session controller, not that stale client flag.
            void request('PrivateSyncPlay/Me').then(me => {
                if (!me.realtimeReady) { pending = token; retryAt = Date.now() + 1000; return false; }
                inviteAttempts++;
                return request('PrivateSyncPlay/JoinInvite', 'POST', { Token: token }).then(() => true);
            }).then(joined => {
                if (!joined) return;
                try { sessionStorage.removeItem(inviteKey); } catch { }
                // Core GroupJoined/PlayQueue messages drive Jellyfin's normal player/navigation.
                // Do not replace that with a plugin dialog or a full-page redirect (which drops the socket).
                current?.dialog.close();
            }).catch(e => {
                if ((!e?.status || e.status >= 500) && inviteAttempts < 3) { pending = token; retryAt = Date.now() + inviteAttempts * 5000; }
                else { try { sessionStorage.removeItem(inviteKey); } catch { } }
                error('Could not join this invitation. It may be expired, full, revoked, or your account may not have access to its media.');
            });
        }
    }
    setInterval(refresh, 1000);
    window.addEventListener('hashchange', () => current?.dialog.close());
    window.addEventListener('popstate', () => current?.dialog.close());
    refresh();
})();

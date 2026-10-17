/* Shared live renderer. Descriptors are data; only these local actions can ever be bound. */
(() => {
    'use strict';
    const features = Object.freeze(['title', 'position', 'volume', 'mute', 'subtitles', 'fullscreen']);
    const actions = Object.freeze(['volume', 'mute', 'subtitles', 'fullscreen']);
    const validId = value => typeof value === 'string' && /^[a-z][a-z0-9.-]{0,63}$/.test(value);
    function policy(input) {
        const enabled = features.filter(f => Array.isArray(input?.features) && input.features.includes(f));
        const controls = [], seen = new Set();
        for (const c of Array.isArray(input?.controls) ? input.controls.slice(0, 24) : []) {
            if (!validId(c?.id) || seen.has(c.id) || !actions.includes(c.feature) || !enabled.includes(c.feature)
                || typeof c.label !== 'string' || !c.label.trim() || c.label.length > 80
                || typeof c.icon !== 'string' || !/^[a-z][a-z0-9_]{0,47}$/.test(c.icon)) continue;
            controls.push({ id:c.id, feature:c.feature, label:c.label, icon:c.icon }); seen.add(c.id);
        }
        return { features:enabled, controls };
    }
    function mount(root, dispatch, selected = () => {}) {
        const toolbar = root.querySelector('#guestPlayerToolbar');
        const native = new Map([...toolbar.children].map(node => [node.dataset.guestControl, node]));
        let current = policy(null), editing = false, signature;
        function can(feature) { return current.features.includes(feature); }
        function apply(input, edit = false) {
            const next = policy(input), key = JSON.stringify([next, edit]);
            if (key === signature) return current;
            signature = key; current = next; editing = edit;
            for (const [id, node] of native) { node.hidden = true; node.dataset.guestFeature = id; }
            toolbar.querySelectorAll('[data-guest-extension]').forEach(node => node.remove());
            for (const c of current.controls) {
                let node = native.get(c.id);
                // A descriptor cannot rebind a built-in ID to a different action.
                if (node && c.feature !== c.id) continue;
                if (!node) {
                    // Reuse the native control DOM, including slider/checkbox skin hooks; never plugin HTML.
                    node = native.get(c.feature).cloneNode(true);
                    node.removeAttribute('id'); node.querySelectorAll('[id]').forEach(n => n.removeAttribute('id'));
                    node.dataset.guestExtension = c.id; node.dataset.guestControl = c.id; node.dataset.guestFeature = c.feature;
                    if (c.feature === 'volume') {
                        const input = node.querySelector('input'); input.id = 'guest-control-' + c.id;
                        input.setAttribute('aria-label',c.label); const label = node.querySelector('label'); if (label) label.htmlFor = input.id;
                        input.oninput = () => { if (can(c.feature)) dispatch(c.feature, Number(input.value)); };
                    } else {
                        const button = node.matches('button') ? node : node.querySelector('button');
                        button.setAttribute('aria-label', c.label); button.title = c.label;
                        button.querySelector('.material-icons').className = 'xlargePaperIconButton material-icons ' + c.icon;
                        button.onclick = () => { if (can(c.feature)) dispatch(c.feature); };
                    }
                }
                node.hidden = false; toolbar.append(node);
            }
            root.querySelectorAll('[data-guest-feature]').forEach(node => { node.hidden = !can(node.dataset.guestFeature)
                || (node.dataset.guestControl && !current.controls.some(c => c.id === node.dataset.guestControl)); });
            if (!can('subtitles')) root.querySelector('#subtitleSettings').hidden = true;
            root.classList.toggle('privateSyncPlayEditing', editing);
            return current;
        }
        root.addEventListener('click', event => {
            if (!editing) return;
            const node = event.target.closest('[data-guest-feature]');
            if (node && root.contains(node)) {
                root.querySelectorAll('.privateSyncPlaySelected').forEach(n => n.classList.remove('privateSyncPlaySelected'));
                node.classList.add('privateSyncPlaySelected'); selected({ feature:node.dataset.guestFeature, control:node.dataset.guestControl || null });
            }
        }, true);
        root.addEventListener('dragstart', event => {
            if (!editing) return;
            const node = event.target.closest('[data-guest-control]');
            if (node && !node.hidden) { event.dataTransfer.setData('text/plain', node.dataset.guestControl); event.dataTransfer.effectAllowed = 'move'; }
        });
        root.addEventListener('dragover', event => { if (editing && event.target.closest('[data-guest-control]')) event.preventDefault(); });
        root.addEventListener('drop', event => {
            if (!editing) return;
            const target = event.target.closest('[data-guest-control]'), source = event.dataTransfer.getData('text/plain');
            if (target && current.controls.some(c => c.id === source)) { event.preventDefault(); selected({ move:source, before:target.dataset.guestControl }); }
        });
        return { apply, can, controls:() => current.controls, volume(value, muted) {
            toolbar.querySelectorAll('[data-guest-extension]').forEach(node => {
                const input = node.querySelector('input'); if (input) input.value = String(value);
                const progress = node.querySelector('.mdl-slider-background-lower'); if (progress) progress.style.width = value * 100 + '%';
                if (node.dataset.guestFeature === 'mute') {
                    node.querySelector('button').setAttribute('aria-pressed',String(muted));
                    node.querySelector('.material-icons').className = 'xlargePaperIconButton material-icons ' + (muted ? 'volume_off' : 'volume_up');
                }
            });
        }, editable() {
            toolbar.querySelectorAll('[data-guest-control]').forEach(node => { node.draggable = true; });
        } };
    }
    window.PrivateSyncPlayGuestPlayer = Object.freeze({ features, actions, policy, mount });
})();

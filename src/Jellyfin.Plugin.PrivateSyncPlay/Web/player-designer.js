(() => {
    'use strict';
    const page = document.getElementById('privateSyncPlayPlayerSettings');
    if (!page || page.dataset.initialized) return;
    page.dataset.initialized = 'true';
    const id = '09514a9e-f7a0-4c5a-91bc-3a46338298ca', form = page.querySelector('form');
    const labels = { title:'Video title', position:'Read-only position', volume:'Local volume', mute:'Local mute', subtitles:'Text subtitle selection', fullscreen:'Fullscreen' };
    const defaults = () => ({ Features:['title','position','volume','mute','subtitles','fullscreen'], Controls:['mute','volume','subtitles','fullscreen'] });
    const copy = x => JSON.parse(JSON.stringify(x));
    let UI, catalog = [], draft, saved, history = [], future = [], current, port, frame, feedback, panel, list, inspector;
    let undoButton, redoButton, removeButton, discardButton, saveButton, canvasStatus, readyTimer, featureInputs = new Map(), dirty = false, saving = false, editing = true, channelReady = false;
    function loadScript(name) {
        return new Promise((resolve, reject) => {
            const script = document.createElement('script'); script.src = ApiClient.getUrl('PrivateSyncPlay/assets/' + name);
            script.onload = resolve; script.onerror = () => reject(new Error('Could not load player designer controls.')); document.head.append(script);
        });
    }
    function clean(input) {
        const known = Object.keys(labels);
        return { Features:known.filter(f => (input?.Features || []).includes(f)),
            Controls:[...new Set(input?.Controls || [])].filter(c => catalog.some(item => item.id === c)).slice(0, 24) };
    }
    function view() { return { features:draft.Features, controls:draft.Controls.map(c => catalog.find(item => item.id === c)).filter(c => c && draft.Features.includes(c.feature)) }; }
    function preview() { if (channelReady) port.postMessage({ type:'configure', policy:view(), editing }); }
    function action(label, parent, run, kind) {
        const button = UI.button(label, parent, null, kind);
        button.onclick = () => { if (!saving) run(); }; return button;
    }
    function edit(run) {
        history.push(copy(draft)); if (history.length > 30) history.shift(); future = []; run(); sync();
    }
    function move(source, before) {
        if (source === before || !draft.Controls.includes(source) || !draft.Controls.includes(before)) return;
        edit(() => { draft.Controls.splice(draft.Controls.indexOf(source),1); draft.Controls.splice(draft.Controls.indexOf(before),0,source); });
    }
    function remove() {
        if (!current) return;
        edit(() => {
            if (current.control) {
                const c = catalog.find(c => c.id === current.control);
                draft.Controls = draft.Controls.filter(c => c !== current.control);
                if (c && !draft.Controls.some(id => catalog.find(x => x.id === id)?.feature === c.feature)) draft.Features = draft.Features.filter(f => f !== c.feature);
            } else {
                draft.Features = draft.Features.filter(f => f !== current.feature);
                draft.Controls = draft.Controls.filter(id => catalog.find(c => c.id === id)?.feature !== current.feature);
            }
            current = null;
        });
    }
    function sync() {
        const focused = list?.contains(document.activeElement) ? { control:document.activeElement.closest('[data-control]')?.dataset.control,
            action:document.activeElement.dataset.action } : null;
        dirty = JSON.stringify(draft) !== JSON.stringify(saved);
        featureInputs.forEach((input, feature) => input.checked = draft.Features.includes(feature));
        undoButton.disabled = !history.length; redoButton.disabled = !future.length; discardButton.disabled = !dirty; saveButton.disabled = !dirty;
        inspector.textContent = current ? 'Selected: ' + (catalog.find(c => c.id === current.control)?.label || labels[current.feature]) : 'Click a control in the live canvas or the list to select it.';
        removeButton.disabled = !current;
        list.replaceChildren();
        draft.Controls.forEach((id, index) => {
            const c = catalog.find(c => c.id === id), row = UI.element('li', null, list, 'listItem listItem-border privateSyncPlayDesignerControl');
            row.draggable = true; row.dataset.control = id;
            row.ondragstart = event => { event.dataTransfer.setData('text/plain', id); event.dataTransfer.effectAllowed = 'move'; };
            row.ondragover = event => event.preventDefault();
            row.ondrop = event => { event.preventDefault(); if (!saving) move(event.dataTransfer.getData('text/plain'), id); };
            action(c.label + (draft.Features.includes(c.feature) ? '' : ' (disabled)'), row, () => { current = { control:id, feature:c.feature }; sync(); }).dataset.action = 'select';
            const up = action('Move up', row, () => move(id, draft.Controls[index - 1])); up.disabled = index === 0; up.setAttribute('aria-label','Move ' + c.label + ' up'); up.dataset.action = 'up';
            const down = action('Move down', row, () => {
                const next = draft.Controls[index + 1]; if (next) edit(() => { draft.Controls.splice(index, 1); draft.Controls.splice(index + 1, 0, id); });
            }); down.disabled = index === draft.Controls.length - 1; down.setAttribute('aria-label','Move ' + c.label + ' down'); down.dataset.action = 'down';
        });
        if (focused) {
            const row = [...list.children].find(row => row.dataset.control === focused.control);
            const button = row?.querySelector('[data-action="' + focused.action + '"]');
            (button && !button.disabled ? button : row?.querySelector('button'))?.focus();
        }
        preview();
        UI.message(feedback, dirty ? 'Unsaved layout. Select Save to apply to guest players.' : 'Layout matches the saved settings.');
    }
    function connect() {
        if (frame.getAttribute('src') === 'about:blank') return;
        port?.close(); channelReady = false;
        clearTimeout(readyTimer);
        UI.message(canvasStatus,'Connecting to the sandboxed live player…');
        readyTimer = setTimeout(() => { if (!channelReady) UI.message(canvasStatus,'The preview could not start. Check browser frame/CSP restrictions. Isolation has not been relaxed.',true); },10000);
        const channel = new MessageChannel(); port = channel.port1;
        port.onmessage = event => {
            const data = event.data;
            if (data?.type === 'ready') { channelReady = true; clearTimeout(readyTimer); UI.message(canvasStatus,'Live guest renderer connected.'); preview(); }
            if (data?.type !== 'select' || !editing || saving) return;
            if (data.move) { move(data.move, data.before); return; }
            if (!draft.Features.includes(data.feature) || (data.control && !draft.Controls.includes(data.control))) return;
            current = { feature:data.feature, control:data.control || null }; sync();
        };
        port.start();
        // The preview deliberately has an opaque sandbox origin; the channel is bound to this exact frame.
        frame.contentWindow.postMessage({ type:'PrivateSyncPlay.Preview.Connect' }, '*', [channel.port2]);
    }
    async function load() {
        try {
            if (!window.PrivateSyncPlayUI) await loadScript('ui.js'); UI = window.PrivateSyncPlayUI;
            const [config, available] = await Promise.all([ApiClient.getPluginConfiguration(id), ApiClient.ajax({type:'GET', url:ApiClient.getUrl('PrivateSyncPlay/PlayerDesigner/Catalog'), dataType:'json'})]);
            const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object' ? Object.fromEntries(Object.entries(x).map(([k,v]) => [k[0].toLowerCase()+k.slice(1),normalize(v)])) : x;
            catalog = normalize(available).controls; saved = clean(config.GuestPlayer || defaults()); draft = copy(saved);
            form.replaceChildren(); panel = UI.element('fieldset', null, form, 'privateSyncPlaySettingsEditor');
            UI.element('legend', 'Guest player layout', panel, 'privateSyncPlayVisuallyHidden');
            const tools = UI.element('div', null, panel, 'privateSyncPlayUserToolbar');
            undoButton = action('Undo', tools, () => { if (history.length) { future.push(copy(draft)); draft = history.pop(); current = null; sync(); } }); undoButton.id = 'pspPlayerUndo';
            redoButton = action('Redo', tools, () => { if (future.length) { history.push(copy(draft)); draft = future.pop(); current = null; sync(); } }); redoButton.id = 'pspPlayerRedo';
            action('Reset default layout', tools, () => edit(() => { draft = defaults(); current = null; })).id = 'pspPlayerReset';
            const mode = action('Try viewing controls', tools, () => { editing = !editing; mode.querySelector('span').textContent = editing ? 'Try viewing controls' : 'Edit layout'; preview(); });
            const grid = UI.element('div', null, panel, 'privateSyncPlayDesignerGrid');
            const canvas = UI.element('section', null, grid, 'privateSyncPlayDesignerCanvas');
            UI.element('h3', 'Live player canvas', canvas, 'sectionTitle');
            canvasStatus = UI.status(canvas,'pspPlayerCanvasStatus');
            const size = UI.select('Preview viewport', ['Fit canvas','Wide player','Narrow player'], 'Fit canvas', canvas, 'pspPlayerViewport');
            const viewport = UI.element('div', null, canvas, 'privateSyncPlayPreviewViewport');
            frame = UI.element('iframe', null, viewport, 'privateSyncPlayPlayerPreview'); frame.title = 'Live isolated guest player preview';
            UI.element('p', 'Fit canvas follows the available settings-page width automatically. Fixed wide/narrow viewports scroll inside the canvas without widening the page.', canvas, 'fieldDescription');
            size.onchange = () => { frame.style.inlineSize = size.value === 'Wide player' ? '64em' : size.value === 'Narrow player' ? '24em' : '100%'; };
            frame.setAttribute('sandbox','allow-scripts'); frame.setAttribute('allow','autoplay; fullscreen'); frame.setAttribute('allowfullscreen','');
            frame.onload = connect; frame.src = ApiClient.getUrl('PrivateSyncPlay/PlayerPreview');
            UI.element('p', 'Explore the working controls. In edit mode, click to select or drag toolbar controls to reorder them. Use the list buttons for keyboard editing. Leaving and enabling playback stay available.', canvas, 'fieldDescription');
            const sidebar = UI.element('aside', null, grid);
            UI.element('h3', 'Allowed guest features', sidebar, 'sectionTitle');
            Object.entries(labels).forEach(([feature,label]) => {
                const input = UI.field(label, 'checkbox', false, sidebar, 'pspFeature-' + feature); featureInputs.set(feature,input);
                input.onchange = () => edit(() => {
                    if (input.checked) {
                        draft.Features.push(feature);
                        if (!draft.Controls.some(id => catalog.find(c => c.id === id)?.feature === feature)) { const c = catalog.find(c => c.id === feature); if (c) draft.Controls.push(c.id); }
                    } else { draft.Features = draft.Features.filter(f => f !== feature); draft.Controls = draft.Controls.filter(id => catalog.find(c => c.id === id)?.feature !== feature); }
                    current = null;
                });
            });
            UI.element('p', 'Subtitles are authorized server-side on every request. Local volume/mute and fullscreen are browser features, not enforceable restrictions on already-delivered video. Position data remains necessary for synchronization even when its display is removed.', sidebar, 'fieldDescription');
            UI.element('h3', 'Selection', sidebar, 'sectionTitle'); inspector = UI.status(sidebar,'pspPlayerSelection');
            removeButton = action('Remove selected', sidebar, remove); removeButton.id = 'pspPlayerRemove';
            UI.element('p', 'Removing the last control for a feature also disables that feature. Reset/undo are drafts until Save.', sidebar, 'fieldDescription');
            UI.element('h3', 'Toolbar order', panel, 'sectionTitle'); list = UI.element('ol', null, panel, 'paperList'); list.id = 'pspPlayerControls';
            const addRow = UI.element('div', null, panel, 'privateSyncPlayUserToolbar');
            const choose = UI.select('Available native and plugin controls', catalog.map(c => ({value:c.id,label:c.label + ' (' + c.id + ')'})), catalog[0]?.id, addRow, 'pspPlayerAddControl');
            action('Add control', addRow, () => {
                const c = catalog.find(c => c.id === choose.value); if (!c || draft.Controls.includes(c.id) || draft.Controls.length >= 24) return;
                edit(() => { draft.Controls.push(c.id); if (!draft.Features.includes(c.feature)) draft.Features.push(c.feature); current = {control:c.id,feature:c.feature}; });
            }).id = 'pspPlayerAdd';
            discardButton = action('Discard unsaved layout', panel, () => { draft = copy(saved); history = []; future = []; current = null; sync(); }); discardButton.id = 'pspPlayerDiscard';
            saveButton = UI.button('Save guest player', panel, null, 'primary'); saveButton.type = 'submit'; saveButton.id = 'pspPlayerSave';
            feedback = UI.status(form,'pspPlayerResult'); sync();
        } catch { form.replaceChildren(); const text = document.createElement('p'); text.setAttribute('role','alert'); text.textContent = 'Could not load the player designer. An administrator account and the active plugin are required. Reload to retry.'; form.append(text); }
    }
    form.addEventListener('submit', async event => {
        event.preventDefault(); if (!draft || !dirty || saving) return;
        saving = true; panel.disabled = true;
        try {
            // Do not overwrite permissions edited on another settings page while the designer was open.
            const latest = await ApiClient.getPluginConfiguration(id); latest.GuestPlayer = copy(draft);
            await ApiClient.updatePluginConfiguration(id, latest); saved = copy(draft); history = []; future = []; sync();
            UI.message(feedback,'Saved. Active guests receive the policy on their next state update.');
        } catch { UI.message(feedback,'Save failed. Your layout is still available here; check permissions and try again.',true); }
        finally { saving = false; panel.disabled = false; }
    });
    page.addEventListener('viewbeforehide', () => { clearTimeout(readyTimer); port?.close(); channelReady = false; if (frame) frame.src = 'about:blank'; });
    page.addEventListener('viewshow', () => { if (frame && !channelReady) { frame.src = ApiClient.getUrl('PrivateSyncPlay/PlayerPreview'); } });
    window.addEventListener('pagehide', () => { clearTimeout(readyTimer); port?.close(); });
    void load();
})();

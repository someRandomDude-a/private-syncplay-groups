/* Native Jellyfin legacy DOM/CSS contracts, also used by its Modern layout's plugin pages. */
(() => {
    'use strict';
    if (window.PrivateSyncPlayUI) return;
    const assets = new URL('./', document.currentScript.src);
    let sequence = 0;
    function element(tag, text, parent, className) {
        const node = document.createElement(tag);
        if (text != null) node.textContent = text;
        if (className) node.className = className;
        if (parent) parent.append(node);
        return node;
    }
    function styles() {
        for (const [name, id] of [['native.css', 'privateSyncPlayNativeCss'], ['ui.css', 'privateSyncPlayCss']]) {
            if (document.getElementById(id)) continue;
            const link = element('link'); link.id = id; link.rel = 'stylesheet'; link.href = new URL(name, assets).href;
            // Existing themes and server/user skin overrides keep precedence.
            document.head.prepend(link);
        }
    }
    function button(label, parent, run, kind = 'default') {
        const b = element('button', null, parent, 'emby-button raised block' + (kind === 'primary' ? ' button-submit' : kind === 'delete' ? ' button-delete' : ''));
        b.setAttribute('is', 'emby-button');
        b.type = 'button'; element('span', label, b, 'emby-button-foreground');
        if (run) b.onclick = async () => {
            b.disabled = true;
            try { await run(); } catch (error) { message(parent.closest('dialog')?.querySelector('[data-psp-status]') || parent.querySelector('[data-psp-status]'), error.message || 'Request failed. Check your permissions and limits.', true); }
            finally { b.disabled = false; }
        };
        return b;
    }
    function focusLabel(input, label, prefix) {
        input.addEventListener('focus', () => { label.classList.add(prefix + 'Focused'); label.classList.remove(prefix + 'Unfocused'); });
        input.addEventListener('blur', () => { label.classList.remove(prefix + 'Focused'); label.classList.add(prefix + 'Unfocused'); });
    }
    function field(label, type, value, parent, id = 'pspField' + ++sequence) {
        if (type === 'checkbox') {
            const row = element('div', null, parent, 'checkboxContainer');
            const l = element('label', null, row, 'emby-checkbox-label');
            const input = element('input', null, l, 'emby-checkbox'); input.type = 'checkbox'; input.id = id; input.checked = !!value;
            l.htmlFor = id; element('span', label, l, 'checkboxLabel');
            const outline = element('span', null, l, 'checkboxOutline'); outline.setAttribute('aria-hidden', 'true');
            element('span', null, outline, 'material-icons checkboxIcon checkboxIcon-checked check');
            element('span', null, outline, 'material-icons checkboxIcon checkboxIcon-unchecked');
            input.setAttribute('data-embycheckbox', 'true'); // Already has the native outline structure.
            input.setAttribute('is', 'emby-checkbox');
            return input;
        }
        const row = element('div', null, parent, 'inputContainer');
        const l = element('label', label, row, 'inputLabel inputLabelUnfocused'); l.htmlFor = id;
        const input = element('input', null, row, 'emby-input'); input.type = type; input.id = id; input.value = value ?? '';
        input.labelElement = l; // Preserve the native upgrade contract if its polyfill runs later.
        input.setAttribute('is', 'emby-input');
        focusLabel(input, l, 'inputLabel'); return input;
    }
    function select(label, choices, value, parent, id = 'pspSelect' + ++sequence) {
        const row = element('div', null, parent, 'selectContainer');
        const l = element('label', label, row, 'selectLabel'); l.htmlFor = id;
        const input = element('select', null, row, 'emby-select emby-select-withcolor'); input.id = id;
        choices.forEach(choice => { const o = element('option', choice.label ?? choice, input); o.value = choice.value ?? choice; });
        input.value = value;
        const arrow = element('div', null, row, 'selectArrowContainer'); arrow.setAttribute('aria-hidden', 'true');
        element('span', null, arrow, 'selectArrow material-icons keyboard_arrow_down');
        input.setAttribute('is', 'emby-select');
        focusLabel(input, l, 'selectLabel'); return input;
    }
    function status(parent, id) {
        const p = element('p', '', parent, 'fieldDescription'); p.dataset.pspStatus = ''; if (id) p.id = id;
        p.setAttribute('role', 'status'); p.setAttribute('aria-live', 'polite'); return p;
    }
    function message(target, text, error = false) {
        if (!target) return;
        target.textContent = text;
        target.classList.toggle('infoBanner', error);
        target.setAttribute('role', error ? 'alert' : 'status');
    }
    function dialog(title) {
        const focus = document.activeElement;
        const d = element('dialog', null, document.body, 'dialog formDialog privateSyncPlayDialog');
        d.id = 'privateSyncPlayDialog';
        const header = element('div', null, d, 'formDialogHeader');
        const h = element('h2', title, header, 'formDialogHeaderTitle'); h.id = 'privateSyncPlayTitle'; d.setAttribute('aria-labelledby', h.id);
        const close = button('Close', header, () => d.close()); close.className = 'emby-button button-flat privateSyncPlayClose';
        const scroll = element('div', null, d, 'formDialogContent scrollY');
        const content = element('div', null, scroll, 'dialogContentInner');
        const feedback = status(content);
        d.addEventListener('close', () => { d.remove(); if (focus?.isConnected) focus.focus(); });
        return { dialog: d, content, feedback };
    }
    window.PrivateSyncPlayUI = { element, button, field, select, status, message, dialog, styles };
    styles();
})();

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

const directory = new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/', import.meta.url);
const source = await readFile(new URL('ui.js', directory), 'utf8');
function page() {
    const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', { url:'https://party.example/web/', runScripts:'outside-only' });
    const script = dom.window.document.createElement('script'); script.src='https://party.example/PrivateSyncPlay/assets/ui.js';
    Object.defineProperty(dom.window.document, 'currentScript', { value:script });
    dom.window.eval(source);
    return dom;
}
test('buttons expose native skin hooks and let skin CSS own appearance', () => {
    const dom = page(), { document, PrivateSyncPlayUI:UI } = dom.window;
    const style = document.createElement('style'); style.textContent='.button-submit {background:rgb(10, 20, 30)}'; document.head.append(style);
    const button = UI.button('Create', document.body, null, 'primary');
    assert(button.classList.contains('emby-button')); assert(button.classList.contains('raised'));
    assert.equal(button.getAttribute('is'),'emby-button');
    assert.equal(button.querySelector('span').textContent,'Create');
    assert.equal(button.getAttribute('style'),null);
    assert.equal(dom.window.getComputedStyle(button).backgroundColor,'rgb(10, 20, 30)');
    assert(UI.button('Revoke',document.body,null,'delete').classList.contains('button-delete'));
    dom.window.close();
});
test('inputs have native containers, associated labels and focus state classes', () => {
    const dom=page(), {document,PrivateSyncPlayUI:UI}=dom.window;
    const input=UI.field('Name','text','Party',document.body);
    assert(input.matches('.inputContainer > .emby-input'));
    assert.equal(input.getAttribute('is'),'emby-input');
    const label=input.parentNode.querySelector('.inputLabel'); assert.equal(label.htmlFor,input.id);
    input.focus(); assert(label.classList.contains('inputLabelFocused'));
    input.blur(); assert(label.classList.contains('inputLabelUnfocused'));
    const select=UI.select('Visibility',['Public','Private'],'Private',document.body);
    assert(select.matches('.selectContainer > .emby-select.emby-select-withcolor'));
    assert.equal(select.value,'Private'); assert.equal(select.labels[0].textContent,'Visibility');
    assert(select.parentNode.querySelector('.selectArrowContainer .selectArrow'));
    dom.window.close();
});
test('checkbox structure matches native checked/disabled skin selectors', () => {
    const dom=page(), {document,PrivateSyncPlayUI:UI}=dom.window;
    const input=UI.field('Allow guests','checkbox',false,document.body);
    assert(input.matches('.checkboxContainer .emby-checkbox-label > .emby-checkbox'));
    assert(input.nextElementSibling.matches('.checkboxLabel'));
    assert(input.nextElementSibling.nextElementSibling.matches('.checkboxOutline'));
    input.labels[0].click(); assert.equal(input.checked,true);
    input.disabled=true; input.labels[0].click(); assert.equal(input.checked,true);
    dom.window.close();
});
test('dialogs and feedback use native classes and accessible naming', () => {
    const dom=page(), {document,PrivateSyncPlayUI:UI}=dom.window;
    const d=UI.dialog('Watch parties');
    assert(d.dialog.matches('.dialog.formDialog'));
    assert(d.content.matches('.formDialogContent > .dialogContentInner'));
    assert.equal(document.getElementById(d.dialog.getAttribute('aria-labelledby')).textContent,'Watch parties');
    UI.message(d.feedback,'Invalid code',true); assert.equal(d.feedback.getAttribute('role'),'alert');
    assert(d.feedback.classList.contains('infoBanner'));
    assert.equal(d.dialog.getAttribute('style'),null);
    dom.window.close();
});
test('shared plugin CSS has no fixed palette, font, floating launcher or global control rules', async () => {
    const css=await readFile(new URL('ui.css',directory),'utf8');
    assert.doesNotMatch(css,/#[\da-f]{3,8}\b|rgba?\(|font-family\s*:|background(?:-color)?\s*:/i);
    assert.doesNotMatch(css,/position\s*:\s*fixed|z-index/);
    const web=await readFile(new URL('web.js',directory),'utf8');
    assert.doesNotMatch(web,/\balert\(|Object\.assign\([^)]*style/);
    assert.match(web,/cloneNode\(false\)/);
});
test('guest player and settings page retain native hooks without loading the normal app', async () => {
    const html=await readFile(new URL('guest.html',directory),'utf8');
    const dom=new JSDOM(html);
    assert(dom.window.document.querySelector('button.emby-button.button-submit'));
    assert(dom.window.document.querySelector('.mdl-slider-container .mdl-slider'));
    assert(dom.window.document.querySelector('link[href="../Branding/Css"]'));
    assert.doesNotMatch(html,/<style|style=|main\.jellyfin.*\.js|ApiClient/);
    const settings=await readFile(new URL('settings.html',directory),'utf8');
    const settingsDom=new JSDOM(settings); const script=settingsDom.window.document.querySelector('script').textContent;
    assert.doesNotThrow(()=>new Function(script));
    assert.match(script,/UI\.field/); assert.match(script,/UI\.select/); assert.match(script,/UI\.button/);
    dom.window.close(); settingsDom.window.close();
});

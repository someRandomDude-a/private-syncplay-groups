import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const [html,ui,source,renderer,guest,preview,watch]=await Promise.all(['player-settings.html','ui.js','player-designer.js','guest-player.js','guest.js','guest-preview.js','guest.html'].map(n=>readFile(new URL(n,root),'utf8')));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const clone=x=>JSON.parse(JSON.stringify(x));
const features=['title','position','volume','mute','subtitles','fullscreen'];
const controls=[{Id:'mute',Feature:'mute',Label:'Mute',Icon:'volume_up'},{Id:'volume',Feature:'volume',Label:'Volume',Icon:'volume_up'},
    {Id:'subtitles',Feature:'subtitles',Label:'Subtitles',Icon:'closed_caption'},{Id:'fullscreen',Feature:'fullscreen',Label:'Fullscreen',Icon:'fullscreen'},
    {Id:'example.captions',Feature:'subtitles',Label:'Theme captions',Icon:'closed_caption'}];
const defaultOptions=()=>({Features:[...features],Controls:['mute','volume','subtitles','fullscreen']});

async function designer(){
    const dom=new JSDOM(html,{url:'https://media.example/jellyfin/web/index.html#/configurationpage?name=privateSyncPlayPlayer',runScripts:'outside-only'});
    const w=dom.window,d=w.document, fixture={config:{EnableGuestInvites:false,MaxViewers:7,Users:[{UserId:'saved',GuestInvites:'Deny'}],GuestPlayer:defaultOptions()},writes:[],fail:false,messages:[],channels:[]};
    w.ApiClient={getUrl:p=>'https://media.example/jellyfin/'+p,getPluginConfiguration:async()=>clone(fixture.config),
        ajax:async()=>({Features:features,Controls:controls}),updatePluginConfiguration:async(_,c)=>{fixture.writes.push(clone(c));if(fixture.fail)throw new Error('No');fixture.config=clone(c);}};
    Object.defineProperty(d,'currentScript',{value:{src:'https://media.example/jellyfin/PrivateSyncPlay/assets/ui.js'}});w.eval(ui);
    w.MessageChannel=class{
        constructor(){this.port1={postMessage:m=>fixture.messages.push(clone(m)),start(){},close(){}};this.port2={};fixture.channels.push(this);}
    };
    w.eval(source);await tick();await tick();
    const frame=d.querySelector('iframe');frame.contentWindow.postMessage=()=>{};frame.dispatchEvent(new w.Event('load'));
    const port=fixture.channels.at(-1).port1;port.onmessage({data:{type:'ready'}});
    return {dom,w,d,fixture,port,close:()=>w.close(),submit:async()=>{d.querySelector('form').dispatchEvent(new w.Event('submit',{cancelable:true,bubbles:true}));await tick();await tick();}};
}
test('designer renders an opaque sandbox of the actual guest shell, with native inputs and no account/media passed to the preview',async()=>{
    const p=await designer();try{
        const f=p.d.querySelector('iframe');assert.equal(f.getAttribute('sandbox'),'allow-scripts');assert(f.src.endsWith('/jellyfin/PrivateSyncPlay/PlayerPreview'));
        assert.equal(p.d.querySelectorAll('input.emby-checkbox').length,6);assert(p.d.querySelector('button.button-submit'));
        assert.equal(p.fixture.messages.at(-1).type,'configure');assert.equal(p.fixture.messages.at(-1).policy.controls.length,4);
        assert(!JSON.stringify(p.fixture.messages).includes('UserId'));assert(!JSON.stringify(p.fixture.messages).includes('Token'));
        assert(p.d.getElementById('pspPlayerCanvasStatus').textContent.includes('connected'));
        const size=p.d.getElementById('pspPlayerViewport');size.value='Wide player';size.dispatchEvent(new p.w.Event('change'));assert.equal(f.style.inlineSize,'64em');
        size.value='Narrow player';size.dispatchEvent(new p.w.Event('change'));assert.equal(f.style.inlineSize,'24em');
        size.value='Fit canvas';size.dispatchEvent(new p.w.Event('change'));assert.equal(f.style.inlineSize,'100%');
    }finally{p.close();}
});
test('designer gives the player the available settings width and moves the sidebar only when the content is wide enough',async()=>{
    const css=await readFile(new URL('ui.css',root),'utf8');
    assert.match(css,/\.privateSyncPlayDesigner \.content-primary\s*\{[^}]*max-inline-size:\s*none;[^}]*container-type:\s*inline-size/);
    assert.match(css,/\.privateSyncPlayDesigner #pspPlayerForm\s*\{[^}]*max-inline-size:\s*none/);
    assert.match(css,/\.privateSyncPlayDesigner \.privateSyncPlayDesignerGrid\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\);/);
    assert.match(css,/@container \(min-width: 100em\)\s*\{\s*\.privateSyncPlayDesigner \.privateSyncPlayDesignerGrid\s*\{[^}]*20em/);
    assert.match(css,/\.privateSyncPlayDesigner \.privateSyncPlayPlayerPreview\s*\{[^}]*display:\s*block;[^}]*inline-size:\s*100%;[^}]*aspect-ratio:\s*16\/9/);
});
test('embedded player neutralizes normal page padding without replacing theme colors or control styling',async()=>{
    const css=await readFile(new URL('ui.css',root),'utf8');
    const dom=new JSDOM(watch,{runScripts:'outside-only'});try{
        const d=dom.window.document,s=d.createElement('style');s.textContent='.page { padding: 120px 0 80px; }\n'+css;d.head.append(s);
        assert.equal(dom.window.getComputedStyle(d.getElementById('videoOsdPage')).padding,'0px');
        assert.equal(dom.window.getComputedStyle(d.getElementById('videoOsdPage')).boxSizing,'border-box');
    }finally{dom.window.close();}
});
test('feature toggles, canvas selection/removal, undo/redo, plugin controls and reset are live drafts until Save',async()=>{
    const p=await designer();try{
        p.d.getElementById('pspFeature-subtitles').click();assert(!p.fixture.messages.at(-1).policy.features.includes('subtitles'));
        assert(!p.fixture.messages.at(-1).policy.controls.some(c=>c.feature==='subtitles'));assert.equal(p.fixture.writes.length,0);
        p.d.getElementById('pspPlayerUndo').click();assert(p.fixture.messages.at(-1).policy.features.includes('subtitles'));
        p.d.getElementById('pspPlayerRedo').click();assert(!p.fixture.messages.at(-1).policy.features.includes('subtitles'));
        p.d.getElementById('pspPlayerAddControl').value='example.captions';p.d.getElementById('pspPlayerAdd').click();
        assert(p.fixture.messages.at(-1).policy.controls.some(c=>c.id==='example.captions'));
        p.port.onmessage({data:{type:'select',feature:'fullscreen',control:'fullscreen'}});p.d.getElementById('pspPlayerRemove').click();
        assert(!p.fixture.messages.at(-1).policy.features.includes('fullscreen'));
        p.d.getElementById('pspPlayerReset').click();await p.submit();assert.deepEqual(p.fixture.config.GuestPlayer,defaultOptions());
        // Default layout already matched saved settings, so no unnecessary update was sent.
        assert.equal(p.fixture.writes.length,0);
    }finally{p.close();}
});
test('drag reorder and keyboard reorder work and canvas cannot select/move unknown controls',async()=>{
    const p=await designer();try{
        p.port.onmessage({data:{type:'select',move:'fullscreen',before:'mute'}});
        assert.equal(p.fixture.messages.at(-1).policy.controls[0].id,'fullscreen');
        const up=p.d.querySelector('[data-control="subtitles"] [data-action="up"]');up.focus();up.click();
        assert.equal(p.d.activeElement.getAttribute('aria-label'),'Move Subtitles up');
        p.port.onmessage({data:{type:'select',feature:'download',control:'evil'}});assert(!p.d.getElementById('pspPlayerSelection').textContent.includes('evil'));
        p.port.onmessage({data:{type:'select',move:'evil',before:'mute'}});
        assert(!p.fixture.messages.at(-1).policy.controls.some(c=>c.id==='evil'));
    }finally{p.close();}
});
test('designer saves only its settings merged into the latest server config, retains failed drafts, and supports discard',async()=>{
    const p=await designer();try{
        p.d.getElementById('pspFeature-subtitles').click();p.fixture.config.MaxViewers=22;
        p.fixture.fail=true;await p.submit();assert(p.d.getElementById('pspPlayerResult').textContent.includes('Save failed'));
        assert.equal(p.d.querySelector('fieldset').disabled,false);p.fixture.fail=false;await p.submit();
        assert.equal(p.fixture.config.MaxViewers,22);assert.equal(p.fixture.config.Users[0].GuestInvites,'Deny');
        assert(!p.fixture.config.GuestPlayer.Features.includes('subtitles'));assert.equal(p.fixture.config.EnableGuestInvites,false);
        p.d.getElementById('pspFeature-mute').click();p.d.getElementById('pspPlayerDiscard').click();
        assert(p.d.getElementById('pspFeature-mute').checked);assert(p.d.getElementById('pspPlayerSave').disabled);
    }finally{p.close();}
});
test('preview mounts the same renderer with generated video and never performs guest or account API requests',async()=>{
    const dom=new JSDOM(watch.replace('{{PREVIEW}}','true'),{url:'https://media.example/jellyfin/PrivateSyncPlay/PlayerPreview',runScripts:'outside-only'});
    const w=dom.window,d=w.document,calls=[],posts=[];
    try{
        w.matchMedia=()=>({matches:false,addEventListener(){}});w.requestAnimationFrame=()=>1;w.cancelAnimationFrame=()=>{};
        w.HTMLCanvasElement.prototype.getContext=()=>({fillRect(){},fillText(){}});
        w.HTMLCanvasElement.prototype.captureStream=()=>({getTracks:()=>[{stop(){}}]});
        w.HTMLMediaElement.prototype.play=()=>Promise.resolve();w.HTMLMediaElement.prototype.pause=()=>{};w.HTMLMediaElement.prototype.load=()=>{};
        Object.defineProperty(w.HTMLTrackElement.prototype,'track',{get(){return {mode:'hidden',cues:[]};}});
        w.URL.createObjectURL=()=> 'blob:preview';w.URL.revokeObjectURL=()=>{};w.fetch=(...args)=>{calls.push(args);throw new Error('Preview must not fetch');};
        w.eval(renderer);w.eval(preview);w.eval(guest);
        const port={postMessage:m=>posts.push(m),start(){},close(){}};
        w.dispatchEvent(new w.MessageEvent('message',{source:w.parent,data:{type:'PrivateSyncPlay.Preview.Connect'},ports:[port]}));
        const policy={features,controls:controls.slice(0,4).map(c=>({id:c.Id,feature:c.Feature,label:c.Label,icon:c.Icon}))};
        port.onmessage({data:{type:'configure',policy,editing:true}});
        d.getElementById('subtitlesButton').click();assert.equal(d.getElementById('subtitleSettings').hidden,false);
        assert(posts.some(p=>p.type==='select' && p.feature==='subtitles'));assert.equal(calls.length,0);
        assert(d.getElementById('video').srcObject);assert(d.getElementById('videoOsdPage').classList.contains('privateSyncPlayEditing'));
        assert(w.document.querySelector('script[src="assets/guest-player.js"]'));
    }finally{w.close();}
});

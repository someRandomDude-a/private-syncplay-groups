import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import { JSDOM } from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const html=await readFile(new URL('guest.html',root),'utf8');
const js=await readFile(new URL('guest.js',root),'utf8');
const renderer=await readFile(new URL('guest-player.js',root),'utf8');
const account=await readFile(new URL('account.js',root),'utf8');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const secret='A'.repeat(64);
const defaultPolicy={features:['title','position','volume','mute','subtitles','fullscreen'],controls:[
    {id:'mute',feature:'mute',label:'Mute',icon:'volume_up'},{id:'volume',feature:'volume',label:'Volume',icon:'volume_up'},
    {id:'subtitles',feature:'subtitles',label:'Subtitles',icon:'closed_caption'},{id:'fullscreen',feature:'fullscreen',label:'Fullscreen',icon:'fullscreen'}]};

test('account handoff retains a short-lived server-scoped invitation across login without giving the router an unknown hash',()=>{
    const saved=new Map(); let target, cleaned;
    runInNewContext(account,{URL,Date,location:{href:`https://media.example/jellyfin/PrivateSyncPlay/Account#${secret}`,hash:'#'+secret,pathname:'/jellyfin/PrivateSyncPlay/Account',replace:url=>target=url},
        history:{replaceState:(_,__,url)=>cleaned=url},sessionStorage:{setItem:(k,v)=>saved.set(k,v)},document:{getElementById:()=>({})}});
    assert.equal(target,'https://media.example/jellyfin/web/index.html');
    assert.equal(cleaned,'/jellyfin/PrivateSyncPlay/Account');
    const pending=JSON.parse(saved.get('PrivateSyncPlay.Invite:/jellyfin/PrivateSyncPlay/'));
    assert.equal(pending.token,secret); assert(pending.expires>Date.now());
    assert(!target.includes(secret));
});

async function player(overrides={},nativeHls=false) {
    const dom=new JSDOM(html,{url:'https://media.example/jellyfin/PrivateSyncPlay/Player',runScripts:'outside-only'});
    const w=dom.window, video=w.document.getElementById('video'); let playing=false, plays=0, now=0;
    const engines=[];Object.defineProperty(w.performance,'now',{value:()=>now});
    w.matchMedia=()=>({matches:false,addEventListener(){}});
    w.HTMLMediaElement.prototype.load=function(){};
    w.HTMLMediaElement.prototype.play=function(){ playing=true; plays++; return Promise.resolve(); };
    w.HTMLMediaElement.prototype.pause=function(){ if(playing) { playing=false; this.dispatchEvent(new w.Event('pause')); } };
    Object.defineProperty(video,'buffered',{value:{length:1,start:()=>nativeHls?60:0,end:()=>1000}});
    if(nativeHls){Object.defineProperty(video,'seekable',{value:{length:1,start:()=>60,end:()=>1000}});video.canPlayType=()=> 'maybe';}
    Object.defineProperty(w.HTMLTrackElement.prototype,'track',{get(){return this._track ||= {mode:'disabled',cues:[{startTime:62,endTime:70}],removeCue(cue){this.cues=this.cues.filter(c=>c!==cue);}};}});
    w.HTMLElement.prototype.requestFullscreen=async function(){Object.defineProperty(w.document,'fullscreenElement',{configurable:true,value:this});w.document.dispatchEvent(new w.Event('fullscreenchange'));};
    w.document.exitFullscreen=async()=>{Object.defineProperty(w.document,'fullscreenElement',{configurable:true,value:null});w.document.dispatchEvent(new w.Event('fullscreenchange'));};
    w.Hls=class {
        constructor(options){this.options=options;this.handlers=new Map();engines.push(this);}
        static isSupported=()=>!nativeHls;
        static Events={MANIFEST_PARSED:'manifest',ERROR:'error',FRAG_LOADED:'fragment'};
        loadSource(url){this.url=url;} attachMedia(){} destroy(){} stopLoad(){}
        on(name,fn){this.handlers.set(name,fn);if(name==='manifest') void fn();}
    };
    const calls=[];let nextPoll;const timeout=w.setTimeout.bind(w);
    w.setTimeout=(fn,delay)=>delay===1000?(nextPoll=fn,-1):timeout(fn,delay);
    const state={playing:true,paused:false,generation:'abc',offset:60,position:65,duration:180,ready:true,
        subtitles:[{index:2,label:'English',language:'en'}],selectedSubtitleIndex:2,burnedSubtitleIndex:null,player:defaultPolicy,...overrides};
    w.fetch=async(url,options)=>{calls.push({url:String(url),options});return {ok:true,status:options.method==='POST'?204:200,json:async()=>state};};
    w.eval(renderer);w.eval(js); await tick(); await tick();
    return {dom,w,video,calls,state,poll:()=>nextPoll(),plays:()=>plays,time:value=>now=value,engines};
}

test('player offers native-style volume, mute, fullscreen and integrated leave, but no transport controls',async()=>{
    const p=await player(); const d=p.w.document;
    try {
        assert.equal(d.getElementById('videoOsdPage').hidden,false);
        assert(d.body.classList.contains('privateSyncPlayWatching'));assert(!d.fullscreenElement,'Viewport filling must not automatically request OS/browser fullscreen');
        assert(d.querySelector('.videoOsdHeader #leave.paper-icon-button-light'));
        assert(d.querySelector('.osdControls .volumeButtons .osdVolumeSlider'));
        assert(!d.querySelector('.btnPause,.btnRewind,.btnFastForward,input.osdPositionSlider'));
        assert.equal(p.video.controls,false);
        assert.equal(p.engines[0].options.startPosition,5,'HLS must start at the party position, not a guessed live edge');
        assert(!d.getElementById('signin'));assert(!d.getElementById('join'));
        const volume=d.getElementById('volume'); volume.value='0.35'; volume.dispatchEvent(new p.w.Event('input'));
        assert.equal(p.video.volume,0.35);
        d.getElementById('mute').click(); assert.equal(p.video.muted,true);
        d.getElementById('fullscreen').click(); await tick(); assert.equal(d.fullscreenElement,d.getElementById('videoOsdPage'));
        const before=p.plays(); p.video.pause(); await tick(); assert(p.plays()>before,'Accidental local pause must resume party playback');
        p.video.currentTime=100; p.video.dispatchEvent(new p.w.Event('seeking')); assert(Math.abs(p.video.currentTime-5)<1);
        p.video.playbackRate=2; p.video.dispatchEvent(new p.w.Event('ratechange')); assert.equal(p.video.playbackRate,1);
        d.getElementById('leave').click(); await tick(); assert.equal(d.getElementById('videoOsdPage').hidden,true);
        assert(!d.body.classList.contains('privateSyncPlayWatching'));
        assert(p.calls.every(c=>new URL(c.url).pathname.includes('/PrivateSyncPlay/Guest/')),'Local controls must never call normal playback APIs');
    } finally {p.dom.window.close();}
});

test('standalone guest player fills its viewport without page margins while fullscreen uses the browser API',async()=>{
    const css=await readFile(new URL('ui.css',root),'utf8');
    assert.match(css,/\.privateSyncPlayGuest #videoOsdPage\s*\{[^}]*position:\s*fixed;[^}]*inset:\s*0;[^}]*margin:\s*0;[^}]*block-size:\s*100dvh/);
    assert.match(css,/\.privateSyncPlayGuest \.privateSyncPlayVideo\s*\{[^}]*object-fit:\s*contain/);
    assert.match(js,/player\.requestFullscreen\(\)/);assert.match(js,/document\.exitFullscreen\(\)/);
});

test('network feedback uses measured HLS fragments, is paced and generation scoped, and cannot send playback controls',async()=>{
    const p=await player();try{
        const first=p.engines[0];
        first.handlers.get('fragment')('fragment',{frag:{type:'main',stats:{loaded:125000,loading:{start:0,end:1000}}}});
        p.time(11000);await p.poll();
        let parameters=new URL(p.calls.at(-1).url).searchParams;
        assert.equal(parameters.get('bandwidthKbps'),'1000');assert.equal(parameters.get('generation'),'abc');assert.equal(parameters.get('bufferSeconds'),'120');
        p.time(12000);await p.poll();assert.equal(new URL(p.calls.at(-1).url).search,'');
        p.state.generation='replacement';await p.poll();
        first.handlers.get('fragment')('fragment',{frag:{type:'main',stats:{loaded:1,loading:{start:0,end:1000}}}});
        p.time(23000);await p.poll();parameters=new URL(p.calls.at(-1).url).searchParams;
        assert.equal(parameters.get('generation'),'replacement');assert(!parameters.has('bandwidthKbps'),'Old generation callbacks must not contaminate the replacement');
        assert.equal(p.calls.filter(c=>c.options.method==='POST').length,0);assert(!parameters.has('itemId'));assert(!parameters.has('token'));
    }finally{p.dom.window.close();}
});

test('native HLS retaining original timestamps follows a borrowed party stream and keeps captions on that same axis',async()=>{
    const p=await player({playbackMethod:'Shared'},true);try{
        assert.equal(p.engines.length,0);assert(Math.abs(p.video.currentTime-65)<1);
        const track=p.w.document.querySelector('video track');track.dispatchEvent(new p.w.Event('load'));
        assert.equal(track.track.cues[0].startTime,62);assert.equal(track.track.cues[0].endTime,70);
    }finally{p.dom.window.close();}
});

test('local text subtitles are generation-scoped, correctly offset after seek and can be disabled without changing the party',async()=>{
    const p=await player(); const d=p.w.document;
    try {
        const track=d.querySelector('video track'); assert(track);
        assert(track.src.endsWith('/Guest/media/abc/subtitles/2.vtt'));
        track.dispatchEvent(new p.w.Event('load'));
        assert.equal(track.track.cues[0].startTime,2); assert.equal(track.track.cues[0].endTime,10);
        assert.equal(track.track.mode,'showing');
        const select=d.getElementById('subtitles'); select.value='-1'; select.dispatchEvent(new p.w.Event('change'));
        assert(!d.querySelector('video track'));
        assert.equal(p.calls.filter(c=>c.options.method==='POST').length,0,'The isolated player cannot redeem invitations or perform account operations');
    } finally {p.dom.window.close();}
});

test('host-selected burned subtitles are described and cannot start guest-specific encoders',async()=>{
    const p=await player({burnedSubtitleIndex:3});
    try {assert.equal(p.w.document.getElementById('subtitles').disabled,true);assert(!p.w.document.querySelector('video track'));}
    finally {p.dom.window.close();}
});
test('missing or unknown guest capabilities fail closed even when their controls are clicked or shortcuts are used',async()=>{
    const p=await player({player:{features:['pause','download','futureFeature'],controls:defaultPolicy.controls}});
    try{
        const d=p.w.document;assert([...d.querySelectorAll('[data-guest-feature]')].every(n=>n.hidden));
        assert(!d.querySelector('video track'));d.getElementById('mute').click();assert.equal(p.video.muted,false);
        const volume=d.getElementById('volume');volume.value='.1';volume.dispatchEvent(new p.w.Event('input'));assert.equal(p.video.volume,1);
        d.getElementById('subtitlesButton').click();assert.equal(d.getElementById('subtitleSettings').hidden,true);
        d.getElementById('videoOsdPage').dispatchEvent(new p.w.KeyboardEvent('keydown',{key:'f',bubbles:true}));assert(!d.fullscreenElement);
        assert.equal(d.getElementById('leave').hidden,false);
    }finally{p.dom.window.close();}
});
test('declarative plugin controls render with native hooks, follow configured order, and only invoke approved local actions',async()=>{
    const c={id:'example.captions',feature:'subtitles',label:'Plugin captions <script>',icon:'closed_caption'};
    const p=await player({player:{features:['subtitles','fullscreen'],controls:[c,defaultPolicy.controls[3],{id:'evil.pause',feature:'pause',label:'Pause',icon:'pause'}]}});
    try{
        const d=p.w.document, ext=d.querySelector('[data-guest-extension="example.captions"]');assert(ext.matches('.paper-icon-button-light'));
        assert(!ext.querySelector('script'));assert.equal(ext.getAttribute('aria-label'),c.label);
        assert(!d.querySelector('[data-guest-extension="evil.pause"]'));
        const order=[...d.querySelector('#guestPlayerToolbar').children].filter(n=>!n.hidden).map(n=>n.dataset.guestControl);
        assert.deepEqual(order,['example.captions','fullscreen']);ext.click();assert.equal(d.getElementById('subtitleSettings').hidden,false);
        assert.equal(p.calls.filter(c=>c.options.method==='POST').length,0);
    }finally{p.dom.window.close();}
});
test('live policy revocation removes active captions and controls on the next state update',async()=>{
    const p=await player();try{
        assert(p.w.document.querySelector('video track'));
        p.state.player={features:[],controls:[]};await p.poll();
        assert(!p.w.document.querySelector('video track'));
        assert([...p.w.document.querySelectorAll('[data-guest-feature]')].every(n=>n.hidden));
        assert.equal(p.w.document.getElementById('leave').hidden,false);
    }finally{p.dom.window.close();}
});

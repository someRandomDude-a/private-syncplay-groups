import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import { JSDOM } from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const html=await readFile(new URL('guest.html',root),'utf8');
const js=await readFile(new URL('guest.js',root),'utf8');
const account=await readFile(new URL('account.js',root),'utf8');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const secret='A'.repeat(64);

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

async function player(overrides={}) {
    const dom=new JSDOM(html,{url:`https://media.example/jellyfin/PrivateSyncPlay/Watch#${secret}`,runScripts:'outside-only'});
    const w=dom.window, video=w.document.getElementById('video'); let playing=false, plays=0;
    w.matchMedia=()=>({matches:false,addEventListener(){}});
    w.HTMLMediaElement.prototype.load=function(){};
    w.HTMLMediaElement.prototype.play=function(){ playing=true; plays++; return Promise.resolve(); };
    w.HTMLMediaElement.prototype.pause=function(){ if(playing) { playing=false; this.dispatchEvent(new w.Event('pause')); } };
    Object.defineProperty(video,'buffered',{value:{length:1,start:()=>0,end:()=>1000}});
    Object.defineProperty(w.HTMLTrackElement.prototype,'track',{get(){return this._track ||= {mode:'disabled',cues:[{startTime:62,endTime:70}],removeCue(cue){this.cues=this.cues.filter(c=>c!==cue);}};}});
    w.HTMLElement.prototype.requestFullscreen=async function(){Object.defineProperty(w.document,'fullscreenElement',{configurable:true,value:this});w.document.dispatchEvent(new w.Event('fullscreenchange'));};
    w.document.exitFullscreen=async()=>{Object.defineProperty(w.document,'fullscreenElement',{configurable:true,value:null});w.document.dispatchEvent(new w.Event('fullscreenchange'));};
    w.Hls=class {
        static isSupported=()=>true;
        static Events={MANIFEST_PARSED:'manifest',ERROR:'error'};
        loadSource(url){this.url=url;} attachMedia(){} destroy(){} stopLoad(){}
        on(name,fn){if(name==='manifest') void fn();}
    };
    const calls=[];
    const state={playing:true,paused:false,generation:'abc',offset:60,position:65,duration:180,ready:true,
        subtitles:[{index:2,label:'English',language:'en'}],selectedSubtitleIndex:2,burnedSubtitleIndex:null,...overrides};
    w.fetch=async(url,options)=>{calls.push({url:String(url),options});return {ok:true,status:options.method==='POST'?204:200,json:async()=>state};};
    w.eval(js); w.document.getElementById('join').click(); await tick(); await tick();
    return {dom,w,video,calls,plays:()=>plays};
}

test('player offers native-style volume, mute, fullscreen and integrated leave, but no transport controls',async()=>{
    const p=await player(); const d=p.w.document;
    try {
        assert.equal(d.getElementById('videoOsdPage').hidden,false);
        assert(d.querySelector('.videoOsdHeader #leave.paper-icon-button-light'));
        assert(d.querySelector('.osdControls .volumeButtons .osdVolumeSlider'));
        assert(!d.querySelector('.btnPause,.btnRewind,.btnFastForward,input.osdPositionSlider'));
        assert.equal(p.video.controls,false);
        assert(d.getElementById('signin').href.endsWith('/jellyfin/PrivateSyncPlay/Account#'+secret));
        const volume=d.getElementById('volume'); volume.value='0.35'; volume.dispatchEvent(new p.w.Event('input'));
        assert.equal(p.video.volume,0.35);
        d.getElementById('mute').click(); assert.equal(p.video.muted,true);
        d.getElementById('fullscreen').click(); await tick(); assert.equal(d.fullscreenElement,d.getElementById('videoOsdPage'));
        const before=p.plays(); p.video.pause(); await tick(); assert(p.plays()>before,'Accidental local pause must resume party playback');
        p.video.currentTime=100; p.video.dispatchEvent(new p.w.Event('seeking')); assert(Math.abs(p.video.currentTime-5)<1);
        p.video.playbackRate=2; p.video.dispatchEvent(new p.w.Event('ratechange')); assert.equal(p.video.playbackRate,1);
        d.getElementById('leave').click(); await tick(); assert.equal(d.getElementById('videoOsdPage').hidden,true);
        assert(p.calls.every(c=>new URL(c.url).pathname.includes('/PrivateSyncPlay/Guest/')),'Local controls must never call normal playback APIs');
    } finally {p.dom.window.close();}
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
        assert.equal(p.calls.filter(c=>c.options.method==='POST').length,1,'Only admission is a server mutation');
    } finally {p.dom.window.close();}
});

test('host-selected burned subtitles are described and cannot start guest-specific encoders',async()=>{
    const p=await player({burnedSubtitleIndex:3});
    try {assert.equal(p.w.document.getElementById('subtitles').disabled,true);assert(!p.w.document.querySelector('video track'));}
    finally {p.dom.window.close();}
});

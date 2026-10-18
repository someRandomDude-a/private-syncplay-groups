import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';
const source=await readFile(new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/input-guard.js',import.meta.url),'utf8');
test('a dialog bubbling guard alone cannot stop native window capture shortcuts',()=>{
    const dom=new JSDOM('<dialog class="privateSyncPlayDialog" open><input></dialog>');
    try{
        const w=dom.window,d=w.document.querySelector('dialog');let muted=false,volume=0.5;
        d.addEventListener('keydown',e=>e.stopPropagation());d.addEventListener('wheel',e=>e.stopPropagation());
        w.addEventListener('keydown',()=>muted=!muted,true);w.addEventListener('wheel',()=>volume+=0.1,true);
        d.querySelector('input').dispatchEvent(new w.KeyboardEvent('keydown',{key:'m',bubbles:true}));
        d.dispatchEvent(new w.WheelEvent('wheel',{bubbles:true}));
        assert(muted);assert.equal(volume,0.6);
    }finally{dom.window.close();}
});
test('early guard blocks native window capture listeners, including events outside the modal, without cancelling browser defaults',()=>{
    const dom=new JSDOM('<dialog class="privateSyncPlayDialog"><input><button>Close</button></dialog>',{runScripts:'outside-only'});
    const w=dom.window,d=w.document.querySelector('dialog'),input=d.querySelector('input');
    try{
        w.eval(source);w.eval(source);
        let muted=false,volume=0.5,captures=0;
        // Reproduce native player handlers: capture listeners on WINDOW, bypassing any dialog bubbling guards.
        w.addEventListener('keydown',e=>{captures++;if(e.key.toLowerCase()==='m')muted=!muted;},true);
        w.addEventListener('wheel',()=>{volume+=0.1;captures++;},{capture:true});
        input.dispatchEvent(new w.KeyboardEvent('keydown',{key:'m',bubbles:true}));assert(muted);
        d.setAttribute('open','');captures=0;
        for(const target of [input,d,w.document.body,w]){
            for(const key of ['m','ArrowUp',' ','Tab','Escape']){
                const e=new w.KeyboardEvent('keydown',{key,bubbles:true,cancelable:true});target.dispatchEvent(e);assert(!e.defaultPrevented);
            }
            const wheel=new w.WheelEvent('wheel',{bubbles:true,cancelable:true,deltaY:100});target.dispatchEvent(wheel);assert(!wheel.defaultPrevented);
            const command=new w.CustomEvent('command',{bubbles:true,cancelable:true});target.dispatchEvent(command);assert(command.defaultPrevented);
        }
        assert.equal(captures,0);assert(muted);assert.equal(volume,0.5);
        d.removeAttribute('open');input.dispatchEvent(new w.KeyboardEvent('keydown',{key:'m',bubbles:true}));assert(!muted);
        input.dispatchEvent(new w.WheelEvent('wheel',{bubbles:true,deltaY:100}));assert.equal(volume,0.6);
    }finally{w.close();}
});

import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const [ui,web]=await Promise.all(['ui.js','web.js'].map(n=>readFile(new URL(n,root),'utf8')));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const party='11111111-1111-1111-1111-111111111111',host='22222222-2222-2222-2222-222222222222',friend='33333333-3333-3333-3333-333333333333',guest='44444444-4444-4444-4444-444444444444';
async function setup(owner=true){
    const dom=new JSDOM('<html><body><header class="skinHeader"><div class="headerRight"><button class="paper-icon-button-light btnSyncPlay"></button></div></header></body></html>',
        {url:'https://media.example/jellyfin/web/index.html#/home',runScripts:'outside-only'});
    const w=dom.window,d=w.document,calls=[];
    const data={id:party,capacity:8,policy:{allowJoins:true,allowGuests:true,participantsControlPlayback:true,participantsEditQueue:true},
        participants:[{id:host,name:'Host',kind:'account',sessions:1,isOwner:true},
            {id:friend,name:'Friend <img src=x>',kind:'account',sessions:2,isOwner:false,playbackAllowed:true,queueAllowed:true},
            {id:guest,name:'Guest ab1234',kind:'guest',sessions:1,isOwner:false,sharedInviteViewers:2}],blocked:[]};
    Object.defineProperty(d,'currentScript',{value:{src:'https://media.example/jellyfin/PrivateSyncPlay/assets/web.js'}});
    w.HTMLElement.prototype.getClientRects=()=>[{width:10,height:10}];w.setInterval=()=>1;
    w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};
    w.ApiClient={accessToken:()=> 'account-token',getUrl:p=>'https://media.example/jellyfin/'+p,ajax:async options=>{
        calls.push(options);const path=new URL(options.url).pathname;
        if(path.endsWith('/Me'))return {canInviteGuests:true,maxViewers:10,maxInviteUses:10,party:{id:party,mode:'Private',isOwner:owner,viewers:4,capacity:8,allowGuests:true,invites:[],canControlPlayback:false,canEditQueue:false}};
        if(path.endsWith('/Management')){assert(owner);return structuredClone(data);}
    }};
    w.eval(ui);w.eval(web);await tick();d.querySelector('.privateSyncPlayLauncher').click();await tick();await tick();
    const button=(label,parent=d)=>[...parent.querySelectorAll('button')].find(b=>b.textContent.trim()===label);
    const checkbox=(label,parent=d)=>[...parent.querySelectorAll('.emby-checkbox-label')].find(l=>l.textContent===label)?.querySelector('input');
    return {dom,w,d,calls,data,button,checkbox,mutations:()=>calls.filter(c=>c.type!=='GET'),close:()=>w.close()};
}
test('owner sees a native participant table, scoped permissions and moderation, never raw identity/session secrets',async()=>{
    const p=await setup();try{
        assert(p.d.querySelector('table.detailTable caption'));assert.equal(p.d.querySelectorAll('tbody tr').length,3);
        assert(!p.d.querySelector('tbody img'));assert(p.d.querySelector(`[data-participant="${friend}"]`).textContent.includes('2 device(s)'));
        assert(!p.d.querySelector(`[data-participant="${host}"] button`));assert(p.d.querySelector('[data-psp-action="end-party"]'));
        assert.equal(p.d.querySelectorAll('input[id^="pspPartyFeature-"]').length,0);
        assert(!p.d.querySelector('input[id$="-pause"]'));assert.equal(p.mutations().length,0);
    }finally{p.close();}
});
test('non-owners have no management controls and never fetch the owner roster',async()=>{
    const p=await setup(false);try{
        assert(!p.calls.some(c=>c.url.endsWith('/Management')));assert(!p.d.querySelector('table'));
        assert(!p.d.querySelector('[data-psp-action="end-party"]'));assert(p.d.querySelector('dialog').textContent.includes('read only'));
    }finally{p.close();}
});
test('account permission changes are scoped to the opaque participant ID and cannot confer management rights',async()=>{
    const p=await setup();try{
        const row=p.d.querySelector(`[data-participant="${friend}"]`);row.querySelector('input').click();
        assert.equal(row.querySelectorAll('input')[1].disabled,true);
        row.querySelector('[data-psp-action="save-member-permissions"]').click();await tick();await tick();
        const call=p.mutations()[0];assert(call.url.endsWith(`/Groups/${party}/Participants/${friend}/Permissions`));
        assert.deepEqual(JSON.parse(call.data),{Kind:'account',ControlPlayback:false,EditQueue:true});
        assert(!call.data.includes('UserId'));assert(!call.data.includes('Owner'));
    }finally{p.close();}
});
test('owner cannot configure guest player features at either party or individual level',async()=>{
    const p=await setup();try{
        const row=p.d.querySelector(`[data-participant="${guest}"]`);
        assert.equal(row.querySelectorAll('input,select').length,0);assert(row.textContent.includes('administrator-managed'));
        assert(!p.d.querySelector('[data-psp-action="save-guest-permissions"]'));
        p.d.querySelector('[data-psp-action="save-party-permissions"]').click();await tick();await tick();
        assert.deepEqual(Object.keys(JSON.parse(p.mutations()[0].data)).sort(),['AllowGuests','AllowJoins','MaxViewers','ParticipantsControlPlayback','ParticipantsEditQueue']);
    }finally{p.close();}
});
test('guest removal requires confirmation and warns that everyone on the shared link is affected',async()=>{
    const p=await setup();try{
        p.d.querySelector('[data-psp-action="kick-guest"]').click();await tick();
        const popup=[...p.d.querySelectorAll('dialog')].at(-1);assert(popup.textContent.includes('2 guest session(s)'));assert(popup.textContent.includes('new browsers'));
        assert.equal(p.mutations().length,0);p.button('Confirm',popup).click();await tick();await tick();
        assert.equal(p.mutations()[0].type,'DELETE');assert(p.mutations()[0].url.endsWith(`/Participants/${guest}`));
    }finally{p.close();}
});
test('party guest disable and ending the party require confirmation; cancel changes nothing',async()=>{
    const p=await setup();try{
        p.checkbox('Allow guest access').click();p.d.querySelector('[data-psp-action="save-party-permissions"]').click();await tick();
        let popup=[...p.d.querySelectorAll('dialog')].at(-1);assert(popup.textContent.includes('permanently')||popup.textContent.includes('will not restore'));
        p.button('Cancel',popup).click();await tick();assert.equal(p.mutations().length,0);
        p.d.querySelector('[data-psp-action="save-party-permissions"]').click();await tick();popup=[...p.d.querySelectorAll('dialog')].at(-1);
        p.button('Confirm',popup).click();await tick();await tick();
        assert.equal(JSON.parse(p.mutations()[0].data).AllowGuests,false);assert.equal(JSON.parse(p.mutations()[0].data).MaxViewers,8);
        p.d.querySelector('[data-psp-action="end-party"]').click();await tick();popup=[...p.d.querySelectorAll('dialog')].at(-1);p.button('Confirm',popup).click();await tick();await tick();
        assert(p.mutations().some(c=>c.type==='DELETE' && c.url.endsWith('/Groups/'+party)));
    }finally{p.close();}
});

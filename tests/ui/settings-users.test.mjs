import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const html=await readFile(new URL('settings.html',root),'utf8');
const ui=await readFile(new URL('ui.js',root),'utf8');
const ids={alice:'11111111-1111-1111-1111-111111111111',bob:'22222222-2222-2222-2222-222222222222',carol:'33333333-3333-3333-3333-333333333333',unlisted:'ffffffff-ffff-ffff-ffff-ffffffffffff'};
const initial=()=>({EnablePrivateGroups:true,AllowPublicGroups:true,DefaultMode:0,EnableGuestInvites:false,DefaultGuestPermission:false,
    MaxViewers:10,MaxInviteUses:10,GuestLeaseSeconds:30,GuestVideoBitrateKbps:2000,MaxGuestStreams:2,FutureSetting:{Keep:true},
    Users:[{UserId:ids.alice.replaceAll('-','').toUpperCase(),DefaultMode:1,CreatePublic:2,CreatePrivate:1,GuestInvites:1},
        {UserId:ids.bob,DefaultMode:null,CreatePublic:0,CreatePrivate:2,GuestInvites:0},
        {UserId:ids.unlisted,DefaultMode:null,CreatePublic:0,CreatePrivate:0,GuestInvites:2}]});
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const clone=x=>JSON.parse(JSON.stringify(x));
const key=x=>x.replaceAll('-','').toLowerCase();
async function settings({users,config}={}) {
    const dom=new JSDOM(html,{url:'https://server.example/jellyfin/web/configurationpage?name=privateSyncPlay',runScripts:'outside-only'});
    const w=dom.window, d=w.document;
    const fixture={server:clone(config||initial()),writes:[],reads:0,fail:false,hold:false,resolve:null};
    w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};
    w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};
    w.ApiClient={getUrl:path=>'https://server.example/jellyfin/'+path,
        getPluginConfiguration:async()=>{fixture.reads++;return clone(fixture.server);},
        getUsers:async()=>users||[{Id:ids.carol,Name:'Carol'},{Id:ids.bob,Name:'Bob <img src=x onerror=alert(1)>',Policy:{IsDisabled:true}},{Id:ids.alice,Name:'Alice'}],
        ajax:async()=>({PluginVersion:'0.1.3.0',ServerVersion:'12.2'}),
        updatePluginConfiguration:async(_,next)=>{fixture.writes.push(clone(next));if(fixture.fail)throw new Error('Server rejected settings');
            if(fixture.hold)await new Promise(resolve=>fixture.resolve=resolve);fixture.server=clone(next);}};
    Object.defineProperty(d,'currentScript',{value:{src:'https://server.example/jellyfin/PrivateSyncPlay/assets/ui.js'}});
    w.eval(ui);w.eval(d.querySelector('script').textContent);await tick();await tick();
    const row=id=>[...d.querySelectorAll('tbody tr')].find(r=>key(r.dataset.userId)===key(id));
    const field=(id,name)=>row(id).querySelector(`select[id$="${name}"]`);
    const change=(element,value)=>{element.value=value;element.dispatchEvent(new w.Event('change',{bubbles:true}));};
    const select=id=>row(id).querySelector('input[type=checkbox]').click();
    const search=text=>{const input=d.getElementById('pspUserSearch');input.value=text;input.dispatchEvent(new w.Event('input'));};
    const submit=async()=>{d.getElementById('pspForm').dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true}));await tick();};
    return {dom,w,d,fixture,row,field,change,select,search,submit,close:()=>dom.window.close()};
}

test('overrides use an accessible native-style table and normalize numeric enums/GUIDs without interpreting user names as HTML',async()=>{
    const p=await settings();try{
        assert(p.d.querySelector('table.detailTable caption'));assert.equal(p.d.querySelectorAll('thead th[scope=col]').length,7);
        assert.equal(p.d.querySelectorAll('tbody th[scope=row]').length,3);
        assert.equal(p.field(ids.alice,'DefaultMode').value,'Private');assert.equal(p.field(ids.alice,'CreatePublic').value,'Deny');
        assert.equal(p.field(ids.carol,'GuestInvites').value,'Inherit');assert(!p.row(ids.bob).querySelector('img'));
        assert(p.row(ids.bob).textContent.includes('Account disabled'));
        assert(p.field(ids.alice,'GuestInvites').labels[0].textContent.includes('Alice'));
        assert(p.d.querySelector('.privateSyncPlayTableScroll[tabindex="0"]'));
        assert.equal(p.d.getElementById('pspBulkToggle').disabled,true);
    }finally{p.close();}
});
test('select-all applies only to visible rows, reports hidden selections, and uses indeterminate checkbox state',async()=>{
    const p=await settings();try{
        p.select(ids.alice);const all=p.d.getElementById('pspSelectAllUsers');assert.equal(all.indeterminate,true);assert.equal(all.getAttribute('aria-checked'),'mixed');
        p.search('Bob');all.click();assert.equal(p.row(ids.bob).querySelector('input').checked,true);assert.equal(p.row(ids.carol).querySelector('input').checked,false);
        assert(p.d.getElementById('pspSelectionStatus').textContent.includes('1 hidden by search'));
        p.search('no matching user');assert.equal(all.disabled,true);assert.equal(p.d.getElementById('pspBulkToggle').disabled,false);
        assert(p.d.getElementById('pspSelectionStatus').textContent.includes('2 hidden by search'));
        p.d.getElementById('pspClearSelection').click();await tick();assert.equal(p.d.getElementById('pspClearSelection').disabled,true);
        assert.equal(p.row(ids.alice).querySelector('input').checked,false);
    }finally{p.close();}
});
test('bulk menu changes only checked fields for selected users, including hidden selections, and does not enable server permissions',async()=>{
    const p=await settings();try{
        p.select(ids.alice);p.select(ids.bob);p.search('Alice');
        p.d.getElementById('pspBulkToggle').click();await tick();assert.equal(p.d.getElementById('pspBulkPanel').hidden,false);
        assert.equal(p.d.getElementById('pspBulkApply').disabled,true);
        p.d.getElementById('pspBulkChangeGuestInvites').click();p.change(p.d.getElementById('pspBulkGuestInvites'),'Allow');
        p.d.getElementById('pspBulkApply').click();await tick();
        assert.equal(p.field(ids.bob,'GuestInvites').value,'Allow');assert.equal(p.field(ids.alice,'CreatePublic').value,'Deny');
        assert.equal(p.field(ids.bob,'CreatePrivate').value,'Deny');assert.equal(p.field(ids.carol,'GuestInvites').value,'Inherit');assert.equal(p.fixture.writes.length,0);
        await p.submit();assert.equal(p.fixture.writes.length,1);
        const saved=p.fixture.server;assert.equal(saved.EnableGuestInvites,false);assert.deepEqual(saved.FutureSetting,{Keep:true});
        assert(saved.Users.some(u=>key(u.UserId)===key(ids.unlisted)),'Preserve unlisted saved overrides unless explicitly reset');
        assert(!saved.Users.some(u=>key(u.UserId)===key(ids.carol)),'Do not create redundant inherited overrides');
    }finally{p.close();}
});
test('reset-selected and per-user reset inherit server defaults, are undoable drafts and serialize sparsely',async()=>{
    const p=await settings();try{
        p.select(ids.alice);p.d.getElementById('pspBulkReset').click();await tick();
        for(const name of ['DefaultMode','CreatePublic','CreatePrivate','GuestInvites'])assert.equal(p.field(ids.alice,name).value,'Inherit');
        assert.equal(p.fixture.writes.length,0);p.d.getElementById('pspUndo').click();await tick();
        assert.equal(p.field(ids.alice,'DefaultMode').value,'Private');assert.equal(p.d.getElementById('pspUndo').disabled,true);
        p.row(ids.alice).querySelector('button').click();await tick();await p.submit();
        assert(!p.fixture.server.Users.some(u=>key(u.UserId)===key(ids.alice)));assert.equal(p.fixture.server.AllowPublicGroups,true);
        assert.equal(p.fixture.server.DefaultMode,'Public');
    }finally{p.close();}
});
test('reset-all requires confirmation, leaves server defaults alone, and clears unlisted overrides only on Save',async()=>{
    const p=await settings();try{
        p.d.getElementById('pspResetAllUsers').click();await tick();
        let dialog=p.d.querySelector('dialog[open]');assert(dialog.textContent.includes('Nothing is applied until Save'));
        [...dialog.querySelectorAll('button')].find(b=>b.textContent==='Cancel').click();await tick();assert.equal(p.field(ids.alice,'DefaultMode').value,'Private');
        p.d.getElementById('pspResetAllUsers').click();await tick();dialog=p.d.querySelector('dialog[open]');
        [...dialog.querySelectorAll('button')].find(b=>b.textContent==='Confirm').click();await tick();
        assert.equal(p.fixture.writes.length,0);await p.submit();assert.deepEqual(p.fixture.server.Users,[]);
        assert.equal(p.fixture.server.EnablePrivateGroups,true);assert.equal(p.fixture.server.EnableGuestInvites,false);assert.equal(p.fixture.server.MaxViewers,10);
    }finally{p.close();}
});
test('bulk permission denial cannot silently save an invalid explicit creation default, even when that row is filtered out',async()=>{
    const p=await settings();try{
        p.select(ids.alice);p.d.getElementById('pspBulkChangeCreatePrivate').click();p.change(p.d.getElementById('pspBulkCreatePrivate'),'Deny');
        p.d.getElementById('pspBulkApply').click();await tick();p.search('Bob');await p.submit();
        assert.equal(p.fixture.writes.length,0);assert.equal(p.row(ids.alice).hidden,false);assert.equal(p.field(ids.alice,'DefaultMode').validity.valid,false);
        assert(p.d.getElementById('pspResult').textContent.includes('Alice'));
        p.change(p.field(ids.alice,'DefaultMode'),'Inherit');await p.submit();assert.equal(p.fixture.writes.length,1);
    }finally{p.close();}
});
test('unsaved edits survive viewshow and server save failure; saving blocks concurrent edits/submissions',async()=>{
    const p=await settings();try{
        p.change(p.field(ids.bob,'GuestInvites'),'Deny');const reads=p.fixture.reads;
        p.d.getElementById('privateSyncPlaySettings').dispatchEvent(new p.w.Event('viewshow'));await tick();assert.equal(p.fixture.reads,reads);
        p.fixture.fail=true;await p.submit();assert(p.d.getElementById('pspResult').textContent.includes('Save failed'));assert.equal(p.field(ids.bob,'GuestInvites').value,'Deny');
        assert.equal(p.d.querySelector('fieldset').disabled,false);
        p.fixture.fail=false;p.fixture.hold=true;await p.submit();
        assert.equal(p.d.querySelector('fieldset').disabled,true);const writes=p.fixture.writes.length;await p.submit();assert.equal(p.fixture.writes.length,writes);
        p.fixture.resolve();await tick();assert.equal(p.d.querySelector('fieldset').disabled,false);assert(p.d.getElementById('pspResult').textContent.includes('Saved.'));
    }finally{p.close();}
});
test('empty user lists and no-result searches never leave an enabled bulk action',async()=>{
    const p=await settings({users:[],config:{...initial(),Users:[]}});try{
        assert.equal(p.d.querySelectorAll('tbody tr').length,0);assert.equal(p.d.getElementById('pspSelectAllUsers').disabled,true);
        assert.equal(p.d.getElementById('pspBulkApply').disabled,true);assert.equal(p.d.getElementById('pspResetAllUsers').disabled,true);
        await p.submit();assert.equal(p.fixture.writes.length,1);
    }finally{p.close();}
});
test('undoing reset-all restores both listed and unlisted overrides',async()=>{
    const p=await settings();try{
        p.d.getElementById('pspResetAllUsers').click();await tick();
        [...p.d.querySelector('dialog[open]').querySelectorAll('button')].find(b=>b.textContent==='Confirm').click();await tick();
        p.d.getElementById('pspUndo').click();await tick();await p.submit();
        assert.equal(p.fixture.server.Users.length,3);
        assert.equal(p.fixture.server.Users.find(u=>key(u.UserId)===key(ids.alice)).DefaultMode,'Private');
        assert.equal(p.fixture.server.Users.find(u=>key(u.UserId)===key(ids.unlisted)).GuestInvites,2);
    }finally{p.close();}
});
test('discard reloads saved configuration only after confirmation and searching never implicitly submits',async()=>{
    const p=await settings();try{
        p.change(p.field(ids.bob,'GuestInvites'),'Allow');
        p.d.getElementById('pspDiscardChanges').click();await tick();
        [...p.d.querySelector('dialog[open]').querySelectorAll('button')].find(b=>b.textContent==='Confirm').click();await tick();await tick();
        assert.equal(p.field(ids.bob,'GuestInvites').value,'Inherit');assert.equal(p.fixture.reads,2);
        assert.equal(p.d.getElementById('pspDiscardChanges').disabled,true);assert.equal(p.fixture.writes.length,0);
        const enter=new p.w.KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true});
        p.d.getElementById('pspUserSearch').dispatchEvent(enter);assert.equal(enter.defaultPrevented,true);
        assert.equal(p.d.getElementById('pspForm').noValidate,true,'Run validation in the submit handler so hidden invalid rows can be revealed');
    }finally{p.close();}
});

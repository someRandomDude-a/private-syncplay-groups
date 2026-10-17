import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM, VirtualConsole } from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const [html,source,player]=await Promise.all(['watch.html','watch-entry.js','guest.js'].map(n=>readFile(new URL(n,root),'utf8')));
const invite='A'.repeat(64),server='b'.repeat(32),user='c'.repeat(32);
const entry={Id:server,UserId:user,AccessToken:'valid-account-token',ManualAddress:'https://media.example/jellyfin'};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function setup({entries=[entry],authenticated=true,allowGuests=true,scheme='https',storage='localStorage',raw,infoId=server,returnedUser=user,disabled=false,optionsError=false,deferOptions=false}={}) {
    const calls=[],navigations=[],virtualConsole=new VirtualConsole();let release;
    virtualConsole.on('jsdomError',e=>{if(e.message.includes('navigation'))navigations.push(e.message);else throw e;});
    const dom=new JSDOM(html,{url:`${scheme}://media.example/jellyfin/PrivateSyncPlay/Watch#${invite}`,runScripts:'outside-only',virtualConsole});
    const w=dom.window;
    if(entries)w[storage].setItem('jellyfin_credentials',raw??JSON.stringify({Servers:entries}));
    w.fetch=async(url,options)=>{
        const path=new URL(url).pathname;calls.push({path,options});
        if(path.endsWith('/System/Info/Public'))return {ok:true,status:200,json:async()=>({Id:infoId})};
        if(path.endsWith('/Users/Me'))return {ok:authenticated,status:authenticated?200:401,json:async()=>({Id:returnedUser,ServerId:server,Policy:{IsDisabled:disabled}})};
        if(path.endsWith('/Options')){
            if(deferOptions)await new Promise(resolve=>release=resolve);
            return {ok:!optionsError,status:optionsError?404:200,json:async()=>({AllowGuests:allowGuests})};
        }
        if(path.endsWith('/Redeem'))return {ok:true,status:204};
        throw new Error('Unexpected request '+path);
    };
    assert(w.document.getElementById('join').hidden,'Guest admission starts hidden before any async checks');
    w.eval(source);await tick();await tick();
    return {dom,w,calls,navigations,release:()=>release(),join:w.document.getElementById('join'),signIn:w.document.getElementById('signin')};
}

test('a valid stored account is verified by the server and automatically handed to native account joining, without guest admission',async()=>{
    const p=await setup();try{
        assert.deepEqual(p.calls.map(c=>c.path),['/jellyfin/System/Info/Public','/jellyfin/Users/Me']);
        assert.equal(p.calls[1].options.headers.Authorization,`MediaBrowser Token="${entry.AccessToken}"`);
        assert.equal(p.calls[1].options.credentials,'omit');assert.equal(p.calls[1].options.redirect,'error');
        assert.equal(p.navigations.length,1);assert(p.signIn.href.endsWith('/Account#'+invite));assert(p.join.hidden);
        assert.equal(p.w.location.hash,'','Remove the bearer invite from the landing URL immediately');
        assert(!p.signIn.href.includes(entry.AccessToken));
    }finally{p.dom.window.close();}
});

for(const [name,options] of Object.entries({
    'revoked token':{authenticated:false},'wrong user':{returnedUser:'d'.repeat(32)},'disabled user':{disabled:true},
    'missing credentials':{entries:null},'malformed storage':{raw:'{'},'different server id':{entries:[{...entry,Id:'e'.repeat(32)}]},
    'different origin':{entries:[{...entry,ManualAddress:'https://evil.example/jellyfin'}]},
    'different server path':{entries:[{...entry,ManualAddress:'https://media.example/other'}]},
    'ambiguous server records':{entries:[entry,entry]},'invalid public id':{infoId:'not-a-guid'}
}))test(name+' does not auto-join or create a guest lease',async()=>{
    const p=await setup({...options,allowGuests:false});try{
        assert.equal(p.navigations.length,0);assert(p.join.hidden);assert(p.join.disabled);
        assert.equal(p.calls.at(-1).path,'/jellyfin/PrivateSyncPlay/Guest/Options');
        assert(!p.calls.some(c=>c.path.endsWith('/Redeem')));
        if(name.startsWith('different ')||name==='ambiguous server records')assert(!p.calls.some(c=>c.options.headers?.Authorization),'Do not send unrelated account tokens');
    }finally{p.dom.window.close();}
});

test('guest option stays hidden until the server explicitly permits admission, and a click rechecks account validity',async()=>{
    const p=await setup({authenticated:false,deferOptions:true});try{
        assert(p.join.hidden);p.release();await tick();assert(!p.join.hidden);assert(!p.join.disabled);
        p.join.click();await tick();await tick();
        assert.equal(p.calls.filter(c=>c.path.endsWith('/Users/Me')).length,2);
        const redeem=p.calls.find(c=>c.path.endsWith('/Redeem'));assert(redeem);
        assert.equal(redeem.options.credentials,'same-origin');assert.equal(redeem.options.headers.Authorization,undefined);
        assert.deepEqual(JSON.parse(redeem.options.body),{Token:invite});assert.equal(p.navigations.length,1);
    }finally{p.dom.window.close();}
});

test('an unavailable invitation fails closed with sign-in only',async()=>{
    const p=await setup({entries:null,optionsError:true});try{assert(p.join.hidden);assert(p.join.disabled);assert(p.signIn.href.endsWith('/Account#'+invite));}
    finally{p.dom.window.close();}
});

test('native session-storage credentials work without requiring persistent login storage',async()=>{
    const p=await setup({storage:'sessionStorage'});try{assert.equal(p.navigations.length,1);assert(p.calls.some(c=>c.path.endsWith('/Users/Me')));}
    finally{p.dom.window.close();}
});

test('opening another invitation in the same tab reloads admission checks rather than retaining the old link policy',async()=>{
    const p=await setup({entries:null,allowGuests:false});try{
        p.w.history.replaceState(null,'','#'+'B'.repeat(64));p.w.dispatchEvent(new p.w.HashChangeEvent('hashchange'));
        assert.equal(p.navigations.length,1);assert(p.join.hidden);assert(!p.calls.some(c=>c.path.endsWith('/Redeem')));
    }finally{p.dom.window.close();}
});

test('the isolated guest player never loads the account-aware invitation script or reads saved account credentials',async()=>{
    const guest=await readFile(new URL('guest.html',root),'utf8');
    assert(!guest.includes('watch-entry.js'));assert(!guest.includes('id="join"'));assert(!guest.includes('id="signin"'));
    for(const accountApi of ['localStorage','sessionStorage','jellyfin_credentials','Users/Me','Authorization','Redeem'])assert(!player.includes(accountApi));
    assert(!html.includes('guest.js'));assert(!html.includes('hls.js'));
});

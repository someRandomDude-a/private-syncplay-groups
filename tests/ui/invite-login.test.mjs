import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

const source=await readFile(new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/web.js',import.meta.url),'utf8');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function setup(otherServer=false) {
    const dom=new JSDOM('<html><body><header class="skinHeader"><div class="headerRight"><button class="paper-icon-button-light"></button></div></header></body></html>',
        {url:'https://media.example/jellyfin/web/index.html#!/login.html',runScripts:'outside-only'});
    const w=dom.window; let signedIn=false,socket=false,refresh,dialogs=0;
    const calls=[];
    Object.defineProperty(w.document,'currentScript',{value:{src:'https://media.example/jellyfin/PrivateSyncPlay/assets/web.js'}});
    w.HTMLElement.prototype.getClientRects=()=>[{width:10,height:10}];
    w.PrivateSyncPlayUI={dialog:()=>{dialogs++;throw new Error('Native join navigation must not be replaced with a plugin modal');},
        element:(tag,text,parent,className)=>{const node=w.document.createElement(tag);node.className=className;parent.append(node);return node;}};
    w.setInterval=fn=>{refresh=fn;};
    w.ApiClient={accessToken:()=>signedIn?'account-token':null,isWebSocketOpen:()=>false,
        getUrl:path=>new URL(path,otherServer?'https://different.example/jellyfin/':'https://media.example/jellyfin/').href,
        ajax:async options=>{calls.push(options);if(options.url.endsWith('/Me'))return {RealtimeReady:socket};}};
    w.sessionStorage.setItem('PrivateSyncPlay.Invite:/jellyfin/PrivateSyncPlay/',JSON.stringify({token:'A'.repeat(64),expires:Date.now()+60000}));
    w.eval(source); await tick();
    return {dom,w,calls,dialogs:()=>dialogs,refresh:()=>refresh(),login:()=>{signedIn=true;},connect:()=>{socket=true;}};
}
test('invitation survives sign-in, waits for the native socket, joins once, and leaves GroupJoined navigation to Jellyfin',async()=>{
    const p=await setup();
    try {
        assert.equal(p.calls.length,0);
        p.login();p.refresh();await tick();assert(!p.calls.some(c=>c.url.endsWith('/JoinInvite')),'Do not join before GroupJoined can be received');
        p.connect();const later=Date.now()+5000;p.w.Date.now=()=>later;p.refresh();await tick();p.refresh();await tick();
        const joins=p.calls.filter(c=>c.url.endsWith('/PrivateSyncPlay/JoinInvite'));
        assert.equal(joins.length,1);
        assert.equal(JSON.parse(joins[0].data).Token,'A'.repeat(64));
        assert.equal(p.dialogs(),0); assert.equal(p.w.location.hash,'#!/login.html','Plugin must not hard-code another player route or reload the socket');
        assert.equal(p.w.sessionStorage.getItem('PrivateSyncPlay.Invite:/jellyfin/PrivateSyncPlay/'),null);
    } finally {p.dom.window.close();}
});
test('a multi-server web client never sends an invitation to the wrong server',async()=>{
    const p=await setup(true);
    try {p.login();p.connect();p.refresh();await tick();assert.equal(p.calls.length,0);}
    finally {p.dom.window.close();}
});

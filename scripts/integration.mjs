// Disposable real-server integration test. Never mounts existing Jellyfin data.
// The self-signed TLS exception is scoped to this loopback-only test HTTP client.
import { spawnSync } from 'node:child_process';
import { mkdir, writeFile, copyFile, readFile } from 'node:fs/promises';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import assert from 'node:assert/strict';

const root = resolve(process.argv[2] || join(tmpdir(), 'opencode', 'private-syncplay-integration-' + Date.now()));
const resume = process.argv.find(x => x.startsWith('--resume='))?.slice(9);
if (resume && !resume.startsWith('private-syncplay-integration-')) throw new Error('Refusing to reuse an unrelated container');
const name = resume || 'private-syncplay-integration-' + process.pid;
const image = 'jellyfin/jellyfin:12.2';
const keep = process.argv.includes('--keep');
function command(exe, args) {
    const result = spawnSync(exe, args, { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
    if (result.status !== 0) throw new Error(`${exe} ${args[0]} failed: ${result.stderr || result.stdout}`);
    return result.stdout.trim();
}
const docker = (...args) => command('docker', args);
const wait = ms => new Promise(r => setTimeout(r, ms));
const normalize = x => Array.isArray(x) ? x.map(normalize) : x && typeof x === 'object'
    ? Object.fromEntries(Object.entries(x).map(([k,v]) => [k[0].toLowerCase()+k.slice(1), normalize(v)])) : x;
// Plugin configuration is deserialized by Jellyfin separately from MVC and requires PascalCase.
const pascal = x => Array.isArray(x) ? x.map(pascal) : x && typeof x === 'object'
    ? Object.fromEntries(Object.entries(x).map(([k,v]) => [k[0].toUpperCase()+k.slice(1), pascal(v)])) : x;
let base, cookie;
async function request(path, { method = 'GET', body, auth, guest = false, origin = false, expected = 200 } = {}) {
    const url = new URL(path.replace(/^\//, ''), base + '/');
    const headers = {};
    if (auth) headers.Authorization = `MediaBrowser Client="PSPIntegration", Device="Test", DeviceId="${auth.device}", Version="1.0", Token="${auth.token || ''}"`;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (guest && cookie) headers.Cookie = cookie;
    if (origin) headers.Origin = new URL(base).origin;
    const result = await new Promise((resolve, reject) => {
        const req = (url.protocol === 'https:' ? httpsRequest : httpRequest)(url, { method, headers, rejectUnauthorized: false, agent: false }, res => {
            const chunks = []; res.on('data', b => chunks.push(b));
            res.on('end', () => {
                const bytes = Buffer.concat(chunks); let data;
                try { data = normalize(JSON.parse(bytes)); } catch { data = bytes.toString(); }
                resolve({ status: res.statusCode, headers: res.headers, data, bytes });
            });
        });
        req.on('error', reject); req.setTimeout(30000, () => req.destroy(new Error('HTTP timeout')));
        if (body !== undefined) req.write(JSON.stringify(path.startsWith('Plugins/') || path.startsWith('System/Configuration/') || path.endsWith('/Policy') ? pascal(body) : body)); req.end();
    });
    if (expected !== null && result.status !== expected)
        throw new Error(`${method} ${path.split('?')[0]}: expected ${expected}, got ${result.status}: ${String(JSON.stringify(result.data)).slice(0, 1600)}`);
    return result;
}

let started = false;
try {
    if (!resume) {
    await mkdir(join(root, 'config', 'config'), { recursive: true });
    await mkdir(join(root, 'config', 'plugins', 'PrivateSyncPlay'), { recursive: true });
    await mkdir(join(root, 'media'), { recursive: true });
    await mkdir(join(root, 'cache'), { recursive: true });
    await copyFile('src/Jellyfin.Plugin.PrivateSyncPlay/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.dll', join(root, 'config', 'plugins', 'PrivateSyncPlay', 'Jellyfin.Plugin.PrivateSyncPlay.dll'));
    docker('run', '--rm', '-v', `${root}:/test`, '--entrypoint', '/usr/bin/openssl', image,
        'req', '-x509', '-newkey', 'rsa:2048', '-keyout', '/test/key.pem', '-out', '/test/cert.pem', '-days', '2', '-nodes', '-subj', '/CN=localhost');
    docker('run', '--rm', '-v', `${root}:/test`, '--entrypoint', '/usr/bin/openssl', image,
        'pkcs12', '-export', '-out', '/test/config/test.pfx', '-inkey', '/test/key.pem', '-in', '/test/cert.pem', '-passout', 'pass:test-only');
    await writeFile(join(root, 'config', 'config', 'network.xml'), `<?xml version="1.0"?><NetworkConfiguration>
<EnableHttps>true</EnableHttps><RequireHttps>false</RequireHttps><CertificatePath>/config/test.pfx</CertificatePath>
<CertificatePassword>test-only</CertificatePassword><InternalHttpPort>8096</InternalHttpPort><InternalHttpsPort>8920</InternalHttpsPort>
<EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>true</EnableRemoteAccess></NetworkConfiguration>`);
    docker('run', '--rm', '-v', `${root}:/test`, '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', image,
        '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=440',
        '-t', '180', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', '/test/media/Party Test.mp4');
    docker('run', '-d', '--name', name, '-p', '127.0.0.1::8096', '-p', '127.0.0.1::8920',
        '-v', `${join(root,'config')}:/config`, '-v', `${join(root,'cache')}:/cache`, '-v', `${join(root,'media')}:/media:ro`, image);
    } else { docker('restart', name); }
    started = true;
    const port = docker('port', name, '8096/tcp').split(':').at(-1);
    const tlsPort = docker('port', name, '8920/tcp').split(':').at(-1);
    base = `http://127.0.0.1:${port}`;
    let ready = false;
    for (let i = 0; i < 120; i++) {
        try {
            const r = await request('PrivateSyncPlay/assets/web.js', { expected: null });
            if (r.status === 200 && typeof r.data === 'string' && r.data.includes('privateSyncPlayLoaded')) { ready = true; break; }
        } catch { }
        await wait(1000);
    }
    assert(ready, 'Jellyfin startup timed out');
    console.log('Disposable Jellyfin started:', base, 'test data:', root);
    const password = 'Temporary-Test-Only-9714!';
    if (!(await request('System/Info/Public')).data.startupWizardCompleted && !(await request('System/Info/Public')).data.isStartupWizardCompleted) {
    await request('Startup/Configuration', { method:'POST', body:{ UICulture:'en-US', MetadataCountryCode:'US', PreferredMetadataLanguage:'en' }, expected:204 });
    await request('Startup/User'); // Jellyfin 12 initializes its first user lazily here.
    await request('Startup/User', { method:'POST', body:{ Name:'party-admin', Password:password }, expected:204 });
    await request('Startup/RemoteAccess', { method:'POST', body:{ EnableRemoteAccess:true, EnableAutomaticPortMapping:false }, expected:204 });
    await request('Startup/Complete', { method:'POST', expected:204 });
    }
    async function login(username, device) {
        const r = await request('Users/AuthenticateByName', { method:'POST', auth:{device}, body:{ Username:username, Pw:password } });
        return { token:r.data.accessToken, device, user:r.data.user };
    }
    const admin = await login('party-admin','host');
    if (!(await request('Users',{auth:admin})).data.some(u=>u.name==='party-friend'))
        await request('Users/New', { method:'POST', auth:admin, body:{ Name:'party-friend', Password:password } });
    const friend = await login('party-friend','friend');
    // Grant the test friend the ordinary core create/join policy, as the real UI would.
    const policy = (await request(`Users/${friend.user.id}`, {auth:admin})).data.policy;
    policy.syncPlayAccess = 'CreateAndJoinGroups';
    await request(`Users/${friend.user.id}/Policy`, { method:'POST', auth:admin, body:policy, expected:204 });
    const settingsPath = 'Plugins/09514a9e-f7a0-4c5a-91bc-3a46338298ca/Configuration';
    const settings = (await request(settingsPath,{auth:admin})).data;
    settings.enableGuestInvites = true; settings.defaultGuestPermission = true;
    settings.guestVideoBitrateKbps = 10000; // Compatible fixture should remux, not encode.
    await request(settingsPath, {method:'POST',auth:admin,body:settings,expected:204});
    const shell = await request('web/index.html'); assert(shell.data.includes('PrivateSyncPlay/assets/web.js'), 'Web launcher injection missing');
    await request('PrivateSyncPlay/assets/web.js'); await request('PrivateSyncPlay/assets/hls.js');
    console.log('PASS plugin loaded, configuration and bundled web assets');
    const group = (await request('PrivateSyncPlay/Groups', { method:'POST', auth:admin, body:{Name:'Private Test',Mode:1,MaxViewers:3} })).data;
    const me = (await request('PrivateSyncPlay/Me',{auth:admin})).data;
    assert.equal(me.party.id,group.groupId);
    assert.equal((await request('SyncPlay/List',{auth:friend})).data.length,0);
    await request(`SyncPlay/${group.groupId}`,{auth:friend,expected:404});
    await request('SyncPlay/Join',{method:'POST',auth:friend,body:{GroupId:group.groupId},expected:404});
    await request('PrivateSyncPlay/JoinCode',{method:'POST',auth:friend,body:{Code:me.party.code.toLowerCase()},expected:204});
    console.log('PASS private list/detail/direct-join guards and join code');
    const accountOnly = (await request('PrivateSyncPlay/Invites',{method:'POST',auth:admin,body:{AllowGuests:false,MaxUses:1}})).data;
    const invite = (await request('PrivateSyncPlay/Invites',{method:'POST',auth:admin,body:{AllowGuests:true,MaxUses:2}})).data;
    base = `https://127.0.0.1:${tlsPort}`;
    await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:accountOnly.token},origin:true,expected:403});
    const redeem = await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:invite.token},guest:true,origin:true,expected:204});
    cookie = redeem.headers['set-cookie'][0].split(';')[0];
    assert.match(redeem.headers['set-cookie'][0],/httponly/i);
    await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:invite.token},origin:true,expected:409});
    await request('PrivateSyncPlay/Guest/State',{guest:true});
    await request('Users/Me',{guest:true,expected:401});
    await request('Items',{guest:true,expected:401});
    await request('SyncPlay/List',{guest:true,expected:401});
    console.log('PASS HTTPS guest lease and no normal API privileges');
    if (!(await request('Library/VirtualFolders',{auth:admin})).data.some(v=>v.name==='Integration'))
        await request('Library/VirtualFolders?name=Integration&collectionType=movies&refreshLibrary=true', {method:'POST',auth:admin,body:{LibraryOptions:{EnableInternetProviders:false,PathInfos:[{Path:'/media'}]}},expected:204});
    let item;
    for(let i=0;i<60;i++) {
        const items=(await request('Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path',{auth:admin})).data.items;
        item=items?.find(i=>i.name.includes('Party Test')); if(item)break; await wait(1000);
    }
    assert(item,'Synthetic movie not scanned');
    await request('SyncPlay/SetNewQueue',{method:'POST',auth:admin,body:{PlayingQueue:[item.id],PlayingItemPosition:0,StartPositionTicks:0},expected:204});
    await request('Sessions/Playing',{method:'POST',auth:admin,body:{ItemId:item.id,PositionTicks:0,IsPaused:false,CanSeek:true,PlayMethod:'DirectPlay'},expected:204});
    await request('SyncPlay/Unpause',{method:'POST',auth:admin,expected:204});
    let state;
    for(let i=0;i<30;i++) { state=(await request('PrivateSyncPlay/Guest/State',{guest:true})).data; if(state.ready)break; await wait(1000); }
    assert(state.ready,'Guest HLS did not start');
    assert.equal(state.playbackMethod,'Remux','Compatible video/audio should be stream-copied');
    const copyProcess=docker('top',name,'-eo','pid,args');
    assert(copyProcess.includes('-codec:v:0 copy') && copyProcess.includes('-codec:a:0 copy'),'Running FFmpeg must copy both streams');
    const manifest=(await request(`PrivateSyncPlay/Guest/media/${state.generation}/index.m3u8`,{guest:true})).data;
    assert(manifest.startsWith('#EXTM3U'));
    const segment=manifest.split('\n').find(x=>x.startsWith('segment'))?.trim(); assert(segment);
    const bytes=await request(`PrivateSyncPlay/Guest/media/${state.generation}/${segment}`,{guest:true}); assert(bytes.bytes.length>188);
    await request(`PrivateSyncPlay/Guest/media/${state.generation}/secret.ts`,{guest:true,expected:404});
    await request('SyncPlay/Pause',{method:'POST',guest:true,expected:401});
    console.log('PASS real stream-copy HLS manifest/segment, no guest party controls');
    const ownerPolicy=(await request(`Users/${admin.user.id}`,{auth:admin})).data.policy;
    const originalVideoPermission=ownerPolicy.enableVideoPlaybackTranscoding;
    const originalAudioPermission=ownerPolicy.enableAudioPlaybackTranscoding;
    ownerPolicy.enableVideoPlaybackTranscoding=false; ownerPolicy.enableAudioPlaybackTranscoding=false;
    await request(`Users/${admin.user.id}/Policy`,{method:'POST',auth:admin,body:ownerPolicy,expected:204});
    assert.equal((await request('PrivateSyncPlay/Guest/State',{guest:true})).data.playbackMethod,'Remux');
    const nativeEncoding=(await request('System/Configuration/encoding',{auth:admin})).data;
    nativeEncoding.hardwareAccelerationType='none'; nativeEncoding.enableHardwareEncoding=false;
    nativeEncoding.encodingThreadCount=1; nativeEncoding.encoderPreset='slow';
    await request('System/Configuration/encoding',{method:'POST',auth:admin,body:nativeEncoding,expected:204});
    settings.guestVideoBitrateKbps=256; // Force a genuine video conversion for the same source.
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:403});
    ownerPolicy.enableVideoPlaybackTranscoding=originalVideoPermission; ownerPolicy.enableAudioPlaybackTranscoding=originalAudioPermission;
    await request(`Users/${admin.user.id}/Policy`,{method:'POST',auth:admin,body:ownerPolicy,expected:204});
    let converted;
    for(let i=0;i<30;i++) {
        converted=(await request('PrivateSyncPlay/Guest/State',{guest:true})).data;
        if(converted.ready)break;
        await wait(1000);
    }
    assert(converted.ready,'Bitrate-limited guest conversion did not start');
    assert.equal(converted.playbackMethod,'Transcode');
    const conversionProcess=docker('top',name,'-eo','pid,args');
    assert(conversionProcess.includes('-threads 1') && conversionProcess.includes('-preset slow'),'Live FFmpeg must honor native threads and preset');
    assert.notEqual(converted.generation,state.generation,'Settings change must replace the prior stream');
    const convertedManifest=(await request(`PrivateSyncPlay/Guest/media/${converted.generation}/index.m3u8`,{guest:true})).data;
    const convertedSegment=convertedManifest.split('\n').find(x=>x.startsWith('segment'))?.trim(); assert(convertedSegment);
    const convertedBytes=await request(`PrivateSyncPlay/Guest/media/${converted.generation}/${convertedSegment}`,{guest:true});
    assert(convertedBytes.bytes.length>188);
    console.log('PASS required bitrate conversion using native Jellyfin encoding settings');
    // Master disable must invalidate the active lease permanently.
    settings.enableGuestInvites=false;
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
    settings.enableGuestInvites=true;
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
    cookie=undefined;
    const next=await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:invite.token},guest:true,origin:true,expected:204});
    cookie=next.headers['set-cookie'][0].split(';')[0];
    settings.users=[{userId:admin.user.id,guestInvites:'Deny'}];
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
    await request('PrivateSyncPlay/Invites',{method:'POST',auth:admin,body:{AllowGuests:true,MaxUses:1},expected:403});
    settings.users=[];
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
    const lastInvite=(await request('PrivateSyncPlay/Invites',{method:'POST',auth:admin,body:{AllowGuests:true,MaxUses:1}})).data;
    cookie=undefined;
    const last=await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:lastInvite.token},origin:true,expected:204});
    cookie=last.headers['set-cookie'][0].split(';')[0];
    await request('SyncPlay/Leave',{method:'POST',auth:admin,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true});
    await request('SyncPlay/Leave',{method:'POST',auth:friend,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
    await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:invite.token},origin:true,expected:404});
    console.log('PASS admin revocation, no resurrection, final account leaving destroys guest access');
    settings.defaultMode='Private';
    settings.users=[{userId:friend.user.id,createPublic:'Deny'}];
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Groups',{method:'POST',auth:friend,body:{Name:'Forbidden public',Mode:0,MaxViewers:3},expected:403});
    await request('SyncPlay/New',{method:'POST',auth:friend,body:{GroupName:'Inherited private default'}});
    assert.equal((await request('SyncPlay/List',{auth:admin})).data.length,0);
    await request('SyncPlay/Leave',{method:'POST',auth:friend,expected:204});
    settings.defaultMode='Public'; settings.users=[];
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    const publicGroup=(await request('SyncPlay/New',{method:'POST',auth:admin,body:{GroupName:'Public Test'}})).data;
    assert.equal((await request('SyncPlay/List',{auth:friend})).data[0].groupId,publicGroup.groupId);
    await request('SyncPlay/Leave',{method:'POST',auth:admin,expected:204});
    console.log('PASS per-user guest/creation denies, stock creation defaults and public browsing');
    await writeFile(join(root,'result.json'), JSON.stringify({passed:true,base,container:name},null,2));
    console.log('ALL INTEGRATION CHECKS PASSED');
} catch(error) {
    if(started) {
        const logs=docker('logs',name); await writeFile(join(root,'server.log'),logs);
        console.error('Server diagnostics saved:',join(root,'server.log'));
    }
    throw error;
} finally {
    if(started && !keep) docker('rm','-f',name);
    if(started && keep) console.log('Test container retained:',name);
}

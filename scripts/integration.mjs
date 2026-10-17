// Disposable real-server integration test. Never mounts existing Jellyfin data.
// The self-signed TLS exception is scoped to this loopback-only test HTTP client.
import { spawnSync } from 'node:child_process';
import { mkdir, writeFile, copyFile, readFile } from 'node:fs/promises';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import assert from 'node:assert/strict';
import { target } from './jellyfin-target.mjs';

const root = resolve(process.argv.slice(2).find(arg => !arg.startsWith('--')) || join(tmpdir(), 'opencode', 'private-syncplay-integration-' + Date.now()));
const withConsumer = process.argv.includes('--with-consumer');
const catalogInstall = process.argv.includes('--install-from-catalog');
const testUpgrade = process.argv.includes('--test-upgrade');
const packagesDirectory = resolve(process.argv.find(x=>x.startsWith('--packages='))?.slice(11) || 'artifacts');
if(testUpgrade && !catalogInstall) throw new Error('Upgrade testing requires catalog installation');
const resume = process.argv.find(x => x.startsWith('--resume='))?.slice(9);
if (catalogInstall && resume) throw new Error('Catalog installation test requires a fresh disposable server');
if (resume && !resume.startsWith('private-syncplay-integration-')) throw new Error('Refusing to reuse an unrelated container');
const name = resume || 'private-syncplay-integration-' + process.pid;
const image = process.argv.find(x=>x.startsWith('--image='))?.slice(8) || target.image;
if (image !== target.image) throw new Error(`Only the supported latest Jellyfin image ${target.image} may be tested`);
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
const sameId = (a,b) => typeof a === 'string' && a.replaceAll('-','').toLowerCase() === b.replaceAll('-','').toLowerCase();
// Plugin configuration is deserialized by Jellyfin separately from MVC and requires PascalCase.
const pascal = x => Array.isArray(x) ? x.map(pascal) : x && typeof x === 'object'
    ? Object.fromEntries(Object.entries(x).map(([k,v]) => [k[0].toUpperCase()+k.slice(1), pascal(v)])) : x;
let base, cookie;
let upgradeCatalog;
async function request(path, { method = 'GET', body, auth, guest = false, origin = false, expected = 200, extraHeaders = {} } = {}) {
    const url = new URL(path.replace(/^\//, ''), base + '/');
    const headers = { ...extraHeaders };
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
    if (expected !== null && !(Array.isArray(expected) ? expected.includes(result.status) : result.status === expected))
        throw new Error(`${method} ${path.split('?')[0]}: expected ${expected}, got ${result.status}: ${String(JSON.stringify(result.data)).slice(0, 1600)}`);
    return result;
}

let started = false;
let repositoryStarted = false, networkStarted = false;
const network = name + '-network', repository = name + '-repository';
try {
    if (!resume) {
    await mkdir(join(root, 'config', 'config'), { recursive: true });
    await mkdir(join(root, 'config', 'plugins', 'PrivateSyncPlay'), { recursive: true });
    await mkdir(join(root, 'media'), { recursive: true });
    await mkdir(join(root, 'cache'), { recursive: true });
    if (!catalogInstall) {
        await copyFile('src/Jellyfin.Plugin.PrivateSyncPlay/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.dll', join(root, 'config', 'plugins', 'PrivateSyncPlay', 'Jellyfin.Plugin.PrivateSyncPlay.dll'));
        await copyFile('src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll', join(root, 'config', 'plugins', 'PrivateSyncPlay', 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll'));
    } else {
        const catalog=JSON.parse(await readFile(join(packagesDirectory,'manifest.json'),'utf8'));
        if(testUpgrade) assert.equal(catalog[0].versions.length,2,'Upgrade fixtures must contain two real package versions');
        await mkdir(join(root,'repository'),{recursive:true});
        // Only the URL changes for the isolated test; ZIP bytes and checksum are the release package's.
        for(const entry of catalog[0].versions) {
            const file=new URL(entry.sourceUrl).pathname.split('/').at(-1);
            await copyFile(join(packagesDirectory,file),join(root,'repository',file));
            entry.sourceUrl=`http://repository/${file}`;
        }
        if(testUpgrade) {
            upgradeCatalog=structuredClone(catalog);
            catalog[0].versions=[catalog[0].versions[1]]; // Publish the older version first.
        }
        await writeFile(join(root,'repository','manifest.json'),JSON.stringify(catalog));
        docker('network','create',network); networkStarted=true;
        docker('run','-d','--name',repository,'--network',network,'--network-alias','repository',
            '-v',`${join(root,'repository')}:/usr/share/nginx/html:ro`,'nginx:alpine'); repositoryStarted=true;
    }
    if (withConsumer) {
        const folder=join(root,'config','plugins','WatchPartyConsumer'); await mkdir(folder,{recursive:true});
        await copyFile('examples/WatchPartyConsumer/bin/Release/net10.0/WatchPartyConsumer.dll',join(folder,'WatchPartyConsumer.dll'));
        await copyFile('examples/WatchPartyConsumer/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll',join(folder,'Jellyfin.Plugin.PrivateSyncPlay.Api.dll'));
    }
    docker('run', '--rm', '-v', `${root}:/test`, '--entrypoint', '/usr/bin/openssl', image,
        'req', '-x509', '-newkey', 'rsa:2048', '-keyout', '/test/key.pem', '-out', '/test/cert.pem', '-days', '2', '-nodes', '-subj', '/CN=localhost');
    docker('run', '--rm', '-v', `${root}:/test`, '--entrypoint', '/usr/bin/openssl', image,
        'pkcs12', '-export', '-out', '/test/config/test.pfx', '-inkey', '/test/key.pem', '-in', '/test/cert.pem', '-passout', 'pass:test-only');
    await writeFile(join(root, 'config', 'config', 'network.xml'), `<?xml version="1.0"?><NetworkConfiguration>
<EnableHttps>true</EnableHttps><RequireHttps>false</RequireHttps><CertificatePath>/config/test.pfx</CertificatePath>
<CertificatePassword>test-only</CertificatePassword><InternalHttpPort>8096</InternalHttpPort><InternalHttpsPort>8920</InternalHttpsPort>
<EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>true</EnableRemoteAccess></NetworkConfiguration>`);
    await writeFile(join(root,'embedded.srt'),'1\n00:00:00,000 --> 00:00:05,000\nEmbedded caption\n\n2\n00:01:00,000 --> 00:01:05,000\nEmbedded caption after seek\n\n3\n00:02:55,000 --> 00:03:00,000\nEnd caption\n');
    await writeFile(join(root,'media','Party Test.fr.srt'),'1\n00:00:00,000 --> 00:00:05,000\nExternal caption\n\n2\n00:01:00,000 --> 00:01:05,000\nExternal caption after seek\n');
    await writeFile(join(root,'media','Party Test.de.ass'),'[Script Info]\nScriptType: v4.00+\nPlayResX: 640\nPlayResY: 360\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,DejaVu Sans,28,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,10,10,10,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:03:00.00,Default,,0,0,0,,Styled party caption\n');
    docker('run', '--rm', '-v', `${root}:/test`, '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', image,
        '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=440',
        '-i','/test/embedded.srt','-map','0:v:0','-map','1:a:0','-map','2:s:0','-c:s','mov_text','-metadata:s:s:0','language=eng',
        '-t', '180', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', '/test/media/Party Test.mp4');
    docker('run', '-d', '--name', name, '-p', '127.0.0.1::8096', '-p', '127.0.0.1::8920',
        ...(catalogInstall ? ['--network',network] : []),
        '-v', `${join(root,'config')}:/config`, '-v', `${join(root,'cache')}:/cache`, '-v', `${join(root,'media')}:/media:ro`, image);
    } else { docker('restart', name); }
    started = true;
    const port = docker('port', name, '8096/tcp').split(':').at(-1);
    let tlsPort = docker('port', name, '8920/tcp').split(':').at(-1);
    base = `http://127.0.0.1:${port}`;
    let ready = false;
    for (let i = 0; i < 120; i++) {
        try {
            const r = await request(catalogInstall ? 'Startup/Configuration' : 'PrivateSyncPlay/assets/web.js', { expected: null });
            if (r.status === 200 && (catalogInstall || typeof r.data === 'string' && r.data.includes('privateSyncPlayLoaded'))) { ready = true; break; }
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
    if(catalogInstall) {
        const id='09514a9e-f7a0-4c5a-91bc-3a46338298ca';
        assert(!(await request('Plugins',{auth:admin})).data.some(p=>sameId(p.id,id)),'Plugin must not be preinstalled for catalog test');
        await request('Repositories',{method:'POST',auth:admin,body:[{Name:'Private SyncPlay test repository',Url:'http://repository/manifest.json',Enabled:true}],expected:204});
        const packages=(await request('Packages',{auth:admin})).data;
        const plugin=packages.find(p=>sameId(p.guid || p.id,id));
        assert(plugin,`Jellyfin must discover the plugin from manifest.json: ${JSON.stringify(packages)}`);
        const version=plugin.versions[0].version;
        await request(`Packages/Installed/Private%20SyncPlay?assemblyGuid=${id}&version=${version}`,{method:'POST',auth:admin,expected:204});
        for(const file of ['Jellyfin.Plugin.PrivateSyncPlay.dll','Jellyfin.Plugin.PrivateSyncPlay.Api.dll','meta.json'])
            docker('exec',name,'test','-f',`/config/plugins/Private SyncPlay_${version}/${file}`);
        docker('restart',name);
        base=`http://127.0.0.1:${docker('port',name,'8096/tcp').split(':').at(-1)}`;
        tlsPort=docker('port',name,'8920/tcp').split(':').at(-1);
        let loaded=false;
        for(let i=0;i<120;i++) {
            try { if((await request('PrivateSyncPlay/assets/web.js',{expected:null})).status===200) { loaded=true; break; } } catch { }
            await wait(1000);
        }
        assert(loaded,'Catalog-installed plugin must load after restart');
        const installed=(await request('Plugins',{auth:admin})).data.find(p=>sameId(p.id,id));
        assert(installed && installed.version===version,'Installed plugin must report the catalog version');
        console.log('PASS real Jellyfin repository discovery, checksum-verified ZIP install, versioned folder, metadata and restart');
        assert.equal((await request('PrivateSyncPlay/Version',{auth:admin})).data.pluginVersion,version);
        if(testUpgrade) {
            const configPath=`Plugins/${id}/Configuration`;
            const saved=(await request(configPath,{auth:admin})).data;
            saved.maxViewers=7; saved.maxInviteUses=4; saved.enableGuestInvites=true;
            saved.defaultGuestPermission=false;
            saved.users=[{userId:admin.user.id,defaultMode:'Private',guestInvites:'Allow'}];
            await request(configPath,{method:'POST',auth:admin,body:saved,expected:204});
            const canonicalSettings=(await request(configPath,{auth:admin})).data;
            await request('PrivateSyncPlay/Groups',{method:'POST',auth:admin,body:{Name:'Expires on upgrade restart',Mode:1,MaxViewers:3}});
            const oldInvite=(await request('PrivateSyncPlay/Invites',{method:'POST',auth:admin,body:{AllowGuests:true,MaxUses:2}})).data;
            base=`https://127.0.0.1:${tlsPort}`;
            const guest=await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:oldInvite.token},origin:true,expected:204});
            cookie=guest.headers['set-cookie'][0].split(';')[0];
            await request('PrivateSyncPlay/Guest/State',{guest:true});
            const newer=upgradeCatalog[0].versions[0];
            // Future-ABI entries must not appear in Jellyfin 12's native version selector.
            upgradeCatalog[0].versions.unshift({...newer,version:'99.0.0.0',targetAbi:'99.0.0.0'});
            await writeFile(join(root,'repository','manifest.json'),JSON.stringify(upgradeCatalog));
            const listed=(await request('Packages/Private%20SyncPlay',{auth:admin})).data.versions;
            assert.deepEqual(listed.map(v=>v.version),[newer.version,version]);
            assert(listed.every(v=>v.changelog && v.timestamp),'Version selector must have release notes and timestamps');
            const tasks=(await request('ScheduledTasks',{auth:admin})).data;
            const updateTask=tasks.find(t=>t.key==='PluginUpdateTask' || t.name==='Update Plugins');
            assert(updateTask,'Native plugin update task must be available');
            await request(`ScheduledTasks/Running/${updateTask.id}`,{method:'POST',auth:admin,expected:204});
            let downloaded=false;
            for(let i=0;i<90;i++) {
                const installed=(await request('Plugins',{auth:admin})).data;
                if(installed.some(p=>sameId(p.id,id) && p.version===newer.version)) { downloaded=true; break; }
                await wait(1000);
            }
            assert(downloaded,'Native automatic update task must download the newer catalog version');
            assert.equal((await request('PrivateSyncPlay/Version',{auth:admin})).data.pluginVersion,version,'Running code must remain old until restart');
            docker('restart',name);
            base=`http://127.0.0.1:${docker('port',name,'8096/tcp').split(':').at(-1)}`;
            tlsPort=docker('port',name,'8920/tcp').split(':').at(-1);
            let upgraded=false;
            for(let i=0;i<120;i++) {
                try { if((await request('PrivateSyncPlay/Version',{auth:admin,expected:null})).data.pluginVersion===newer.version) { upgraded=true; break; } } catch { }
                await wait(1000);
            }
            assert(upgraded,'Restart must load the updated plugin assembly');
            const active=(await request('Plugins',{auth:admin})).data.filter(p=>sameId(p.id,id) && p.status==='Active');
            assert.equal(active.length,1,'Only one plugin version may be active');
            assert.equal(active[0].version,newer.version);
            assert.deepEqual((await request(configPath,{auth:admin})).data,canonicalSettings,'Server/per-user settings must survive an upgrade');
            assert.equal((await request('PrivateSyncPlay/Me',{auth:admin})).data.party ?? null,null);
            base=`https://127.0.0.1:${tlsPort}`;
            await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
            await request('PrivateSyncPlay/JoinInvite',{method:'POST',auth:admin,body:{Token:oldInvite.token},expected:404});
            await request('PrivateSyncPlay/Version',{guest:true,expected:401});
            cookie=undefined;
            base=`http://127.0.0.1:${docker('port',name,'8096/tcp').split(':').at(-1)}`;
            console.log('PASS native version history/ABI filtering, automatic update task, restart activation, settings retention and expired guest credentials');
        }
    }
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
    await request('PrivateSyncPlay/assets/web.js'); await request('PrivateSyncPlay/assets/hls.js'); await request('PrivateSyncPlay/assets/ui.js');
    const nativeCss=(await request('PrivateSyncPlay/assets/native.css')).data;
    assert(nativeCss.includes('main.jellyfin'),'Guest styles must reuse the installed Jellyfin CSS');
    let controlStyles='';
    for(const match of nativeCss.matchAll(/url\("([^"]+)"\)/g)) {
        const url=new URL(match[1],new URL('PrivateSyncPlay/assets/native.css',base+'/'));
        controlStyles+=(await request(url.href)).data;
    }
    assert(controlStyles.includes('.emby-checkbox') && controlStyles.includes('.formDialog') && controlStyles.includes('.mdl-slider'),'Native control CSS chunks missing');
    const watch=(await request('PrivateSyncPlay/Watch')).data;
    assert(watch.includes('../Branding/Css') && watch.includes('themes/dark/theme.css') && !watch.includes('<style>'),'Guest player must reuse native theme/branding');
    assert(watch.includes('videoOsdBottom') && watch.includes('buttonMute') && watch.includes('btnSubtitles') && !watch.includes('btnPause'),'Guest controls must use native player hooks without party transport controls');
    const account=(await request('PrivateSyncPlay/Account')).data;
    assert(account.includes('assets/account.js') && !account.includes('privateSyncPlayInvite='));
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
    await request(`Items?api_key=${invite.token}`,{expected:401});
    await request('PrivateSyncPlay/Guest/State',{guest:true,extraHeaders:{Origin:'https://evil.example'},expected:403});
    await request('PrivateSyncPlay/Guest/State',{guest:true,extraHeaders:{'Sec-Fetch-Site':'same-site'},expected:403});
    await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:null},origin:true,expected:400});
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
    await request('Sessions/Playing',{method:'POST',auth:admin,body:{ItemId:item.id,PositionTicks:0,IsPaused:false,CanSeek:true,PlayMethod:'DirectPlay',SubtitleStreamIndex:-1},expected:204});
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
    assert(state.subtitles.length>=2,'Embedded and external text subtitles must be listed without paths');
    for(const track of state.subtitles) {
        const captions=(await request(`PrivateSyncPlay/Guest/media/${state.generation}/subtitles/${track.index}.vtt`,{guest:true})).data;
        assert(captions.startsWith('WEBVTT') && captions.includes('caption'));
    }
    await request(`PrivateSyncPlay/Guest/media/${state.generation}/subtitles/999.vtt`,{guest:true,expected:404});
    await request('PrivateSyncPlay/Guest/media/wrong-generation/subtitles/2.vtt',{guest:true,expected:404});
    const itemDetails=(await request(`Items/${item.id}?userId=${admin.user.id}&Fields=MediaSources`,{auth:admin})).data;
    const ass=itemDetails.mediaSources?.flatMap(s=>s.mediaStreams).find(s=>s.type==='Subtitle' && s.codec==='ass');
    assert(ass,'Styled subtitle fixture must be scanned');
    async function progress(position,index) {
        await request('Sessions/Playing/Progress',{method:'POST',auth:admin,body:{ItemId:item.id,PositionTicks:position*10000000,IsPaused:false,CanSeek:true,PlayMethod:'DirectPlay',SubtitleStreamIndex:index},expected:204});
    }
    async function readyGuest() {
        for(let i=0;i<30;i++) { const s=(await request('PrivateSyncPlay/Guest/State',{guest:true})).data; if(s.ready)return s; await wait(1000); }
        throw new Error('Guest subtitle/media stream did not become ready');
    }
    await progress(0,ass.index);
    const burned=await readyGuest();
    assert.equal(burned.burnedSubtitleIndex,ass.index); assert.equal(burned.playbackMethod,'Transcode');
    assert(docker('top',name,'-eo','pid,args').includes('subtitles='),'Styled captions must use native FFmpeg rendering');
    await progress(60,-1);
    const sought=await readyGuest();
    assert.equal(sought.offset,60); assert.equal(sought.playbackMethod,'Remux');
    const seekCaptions=(await request(`PrivateSyncPlay/Guest/media/${sought.generation}/subtitles/${sought.subtitles[0].index}.vtt`,{guest:true})).data;
    assert(seekCaptions.includes('00:01:00'),'Text delivery preserves movie timestamps for the player offset adjustment');
    await progress(0,-1); state=await readyGuest();
    console.log('PASS embedded/external text subtitles, native styled burn-in, seek offsets and scoped subtitle guards');
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
    // Inject an actual encoder launch failure only inside this disposable container.
    const ffmpeg='/usr/lib/jellyfin-ffmpeg/ffmpeg';
    docker('exec',name,'mv',ffmpeg,ffmpeg+'.psp-test-disabled');
    try {
        settings.guestVideoBitrateKbps=300;
        await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
        const failed=await request('PrivateSyncPlay/Guest/State',{guest:true,expected:503});
        assert(!JSON.stringify(failed.data).includes('/media/') && !JSON.stringify(failed.data).includes(ffmpeg),'Guest errors must not disclose server paths');
        await request(`PrivateSyncPlay/Guest/media/${converted.generation}/index.m3u8`,{guest:true,expected:404});
        await request('System/Info/Public'); await request('PrivateSyncPlay/Me',{auth:admin});
        await request('Items',{guest:true,expected:401});
    } finally { docker('exec',name,'mv',ffmpeg+'.psp-test-disabled',ffmpeg); }
    await wait(5100);
    for(let i=0;i<30;i++) {
        const result=await request('PrivateSyncPlay/Guest/State',{guest:true,expected:null});
        if(result.status===200 && result.data.ready)break;
        assert([200,503].includes(result.status)); await wait(1000);
        assert(i<29,'Guest stream did not recover after encoder restoration');
    }
    console.log('PASS encoder failure containment, no server/path exposure and guest isolation during recovery');
    settings.maxGuestStreams=1;
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    const primaryCookie=cookie;
    await request('SyncPlay/Leave',{method:'POST',auth:friend,expected:204});
    await request('PrivateSyncPlay/Groups',{method:'POST',auth:friend,body:{Name:'Overflow guest stream',Mode:1,MaxViewers:3}});
    const overflow=(await request('PrivateSyncPlay/Invites',{method:'POST',auth:friend,body:{AllowGuests:true,MaxUses:1}})).data;
    const overflowGuest=await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:overflow.token},origin:true,expected:204});
    cookie=overflowGuest.headers['set-cookie'][0].split(';')[0];
    await request('SyncPlay/SetNewQueue',{method:'POST',auth:friend,body:{PlayingQueue:[item.id],PlayingItemPosition:0,StartPositionTicks:0},expected:204});
    await request('Sessions/Playing',{method:'POST',auth:friend,body:{ItemId:item.id,PositionTicks:0,IsPaused:false,CanSeek:true,PlayMethod:'DirectPlay'},expected:204});
    await request('SyncPlay/Unpause',{method:'POST',auth:friend,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:503});
    await request('SyncPlay/Leave',{method:'POST',auth:friend,expected:204});
    cookie=primaryCookie;
    await request('PrivateSyncPlay/Guest/State',{guest:true});
    await request('PrivateSyncPlay/JoinCode',{method:'POST',auth:friend,body:{Code:me.party.code},expected:204});
    console.log('PASS server-wide guest encoder cap without disrupting an existing stream');
    // Master disable must invalidate the active lease permanently.
    settings.enableGuestInvites=false;
    await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
    await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
    await request(`PrivateSyncPlay/Guest/media/${state.generation}/subtitles/${state.subtitles[0].index}.vtt`,{guest:true,expected:401});
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
    if (withConsumer) {
        const generated=(await request('WatchPartyExample/Invite',{method:'POST',auth:admin,body:{ServerBaseUrl:base,AllowGuests:true}})).data;
        assert(generated.providerAvailable);
        assert.equal(generated.clientType,'WatchPartyClient','Separate plugin must resolve its own typed adapter');
        const link=new URL(generated.invite.inviteUrl);
        assert.equal(link.pathname,'/PrivateSyncPlay/Watch'); assert.equal(link.hash.length,65);
        assert.equal((await request('SyncPlay/List',{auth:friend})).data.length,0);
        cookie=undefined;
        const admission=await request('PrivateSyncPlay/Guest/Redeem',{method:'POST',body:{Token:link.hash.slice(1)},origin:true,expected:204});
        cookie=admission.headers['set-cookie'][0].split(';')[0];
        await request('PrivateSyncPlay/Guest/State',{guest:true});
        await request(`WatchPartyExample/Invites/${generated.invite.inviteId}`,{method:'DELETE',auth:admin,expected:204});
        await request('PrivateSyncPlay/Guest/State',{guest:true,expected:401});
        await request('SyncPlay/Leave',{method:'POST',auth:admin,expected:204});
        settings.enableGuestInvites=false;
        await request(settingsPath,{method:'POST',auth:admin,body:settings,expected:204});
        await request('WatchPartyExample/Invite',{method:'POST',auth:admin,body:{ServerBaseUrl:base,AllowGuests:true},expected:403});
        assert.equal((await request('PrivateSyncPlay/Me',{auth:admin})).data.party ?? null,null,'Denied invitation must not leave a new party behind');
        console.log('PASS isolated consumer plugin DI/client, on-behalf link, guest revocation and no permission bypass');
    }
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
    if(repositoryStarted && !keep) docker('rm','-f',repository);
    if(networkStarted && !keep) docker('network','rm',network);
    if(started && keep) console.log('Test container retained:',name);
}

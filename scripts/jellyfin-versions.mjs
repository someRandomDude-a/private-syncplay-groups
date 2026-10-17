import { appendFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { verifyLatest } from './jellyfin-target.mjs';

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    async function api(path) {
        const headers={Accept:'application/vnd.github+json'};
        if(process.env.GH_TOKEN) headers.Authorization=`Bearer ${process.env.GH_TOKEN}`;
        const response=await fetch(`https://api.github.com/repos/jellyfin/jellyfin/${path}`,{headers,signal:AbortSignal.timeout(30000)});
        if(!response.ok) throw new Error(`Jellyfin release discovery failed (HTTP ${response.status})`);
        return response.json();
    }
    const target=verifyLatest(await api('releases/latest'));
    if(process.env.GITHUB_OUTPUT) await appendFile(process.env.GITHUB_OUTPUT,`latest=${target.tag}\nimage=${target.image}\n`);
    console.log(`Latest stable Jellyfin ${target.tag}; SDK ${target.version}; target ABI ${target.abi}; image ${target.image}`);
}

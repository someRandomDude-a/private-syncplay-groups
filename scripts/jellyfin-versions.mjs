import { appendFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export function latest12(releases) {
    const stable = releases.filter(r => !r.draft && !r.prerelease && /^v12\.\d+(?:\.\d+)?$/.test(r.tag_name));
    stable.sort((a,b) => {
        const av=a.tag_name.slice(1).split('.').map(Number), bv=b.tag_name.slice(1).split('.').map(Number);
        return (bv[1]-av[1]) || ((bv[2] || 0)-(av[2] || 0));
    });
    if (!stable.length) throw new Error('No official stable Jellyfin 12.x release found; refusing to test a different major version');
    return stable[0].tag_name.slice(1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    async function api(path) {
        const headers={Accept:'application/vnd.github+json'};
        if(process.env.GH_TOKEN) headers.Authorization=`Bearer ${process.env.GH_TOKEN}`;
        const response=await fetch(`https://api.github.com/repos/jellyfin/jellyfin/${path}`,{headers,signal:AbortSignal.timeout(30000)});
        if(!response.ok) throw new Error(`Jellyfin release discovery failed (HTTP ${response.status})`);
        return response.json();
    }
    let releases=[await api('releases/latest')];
    if(!releases.some(r=>/^v12\./.test(r.tag_name))) {
        for(let page=1;page<=5;page++) {
            const batch=await api(`releases?per_page=100&page=${page}`); releases.push(...batch);
            if(batch.some(r=>!r.draft && !r.prerelease && /^v12\./.test(r.tag_name)) || !batch.length) break;
        }
    }
    const latest=latest12(releases);
    const matrix={include:[{server:'minimum-12.0',image:'jellyfin/jellyfin:12.0'},
        {server:`latest-${latest}`,image:`jellyfin/jellyfin:${latest}`}]};
    if(process.env.GITHUB_OUTPUT) await appendFile(process.env.GITHUB_OUTPUT,`matrix=${JSON.stringify(matrix)}\nlatest=${latest}\n`);
    console.log(JSON.stringify(matrix));
}

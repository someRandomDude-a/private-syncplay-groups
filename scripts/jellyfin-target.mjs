import { readFileSync } from 'node:fs';

const props=readFileSync(new URL('../Directory.Build.props',import.meta.url),'utf8');
const version=props.match(/<JellyfinVersion>(\d+\.\d+\.\d+)<\/JellyfinVersion>/)?.[1];
const tag=props.match(/<JellyfinServerTag>(\d+\.\d+(?:\.\d+)?)(?:<\/JellyfinServerTag>)/)?.[1];
if(!version || !tag || `${tag}.0`.split('.').slice(0,3).join('.')!==version) throw new Error('Inconsistent Jellyfin target in Directory.Build.props');
export const target={version,tag,abi:`${version}.0`,image:`jellyfin/jellyfin:${tag}`};

export function verifyLatest(release) {
    if(release.draft || release.prerelease || !/^v\d+\.\d+(?:\.\d+)?$/.test(release.tag_name)) throw new Error('Expected an official stable Jellyfin release');
    const latest=release.tag_name.slice(1);
    if(latest!==target.tag) throw new Error(`Latest stable Jellyfin is ${latest}, but this build targets ${target.tag}. Update Directory.Build.props and verify the new SDK/server before releasing. No older-server fallback is allowed.`);
    return target;
}

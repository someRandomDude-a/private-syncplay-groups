import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { target } from './jellyfin-target.mjs';

const folder=process.argv[2] || 'artifacts';
const catalog=JSON.parse(await readFile(join(folder,'manifest.json'),'utf8'));
assert.equal(catalog.length,1);
assert.equal(catalog[0].guid,'09514a9e-f7a0-4c5a-91bc-3a46338298ca');
assert(catalog[0].versions.length>0,'Published catalog must contain a real version');
for(const field of ['name','description','overview','owner','category']) assert.equal(typeof catalog[0][field],'string',field);
const seen=new Set();
for(const version of catalog[0].versions) {
    assert.match(version.version,/^\d+\.\d+\.\d+\.\d+$/);
    assert(!seen.has(version.version),'Duplicate catalog version'); seen.add(version.version);
    assert.match(version.targetAbi,/^\d+\.\d+\.\d+\.\d+$/);
    assert.match(version.checksum,/^[a-f0-9]{32}$/i);
    const source=new URL(version.sourceUrl); assert.equal(source.protocol,'https:');
    assert(source.pathname.endsWith('.zip'),'Jellyfin requires a direct ZIP URL');
    assert(Number.isFinite(Date.parse(version.timestamp)));
}
const release=catalog[0].versions[0];
assert.match(release.version,/^\d+\.\d+\.\d+\.0$/);
assert.equal(release.targetAbi,target.abi);
const url=new URL(release.sourceUrl); assert.equal(url.protocol,'https:');
const filename=url.pathname.split('/').at(-1);
assert.match(filename,/^PrivateSyncPlay-\d+\.\d+\.\d+\.zip$/);
const checksum=createHash('md5').update(await readFile(join(folder,filename))).digest('hex');
assert.equal(release.checksum,checksum,'Catalog checksum does not match its exact release ZIP');
assert(Number.isFinite(Date.parse(release.timestamp)));
assert((await readFile(join(folder,'PrivateSyncPlay.Api-1.0.0.zip'))).length>0);
console.log('PASS plugin repository schema, ABI, release asset URL, real ZIP checksum and SDK archive');

import test from 'node:test';
import assert from 'node:assert/strict';
import { target, verifyLatest } from '../scripts/jellyfin-target.mjs';

test('the only supported server matches its SDK, ABI and Docker image',()=>{
    assert.equal(verifyLatest({tag_name:`v${target.tag}`}),target);
    assert.equal(target.abi,`${target.version}.0`);
    assert.equal(target.image,`jellyfin/jellyfin:${target.tag}`);
});
test('new releases fail clearly instead of silently falling back to an older stable server',()=>{
    assert.throws(()=>verifyLatest({tag_name:'v99.0'}),/Update Directory.Build.props/);
    assert.throws(()=>verifyLatest({tag_name:`v${target.tag}`,prerelease:true}));
    assert.throws(()=>verifyLatest({tag_name:`v${target.tag}`,draft:true}));
});

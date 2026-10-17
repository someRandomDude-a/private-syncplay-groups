import test from 'node:test';
import assert from 'node:assert/strict';
import { latest12 } from '../scripts/jellyfin-versions.mjs';

test('selects latest numeric stable 12.x, excluding other majors, previews and drafts',()=>{
    assert.equal(latest12([
        {tag_name:'v13.0'}, {tag_name:'v12.2'}, {tag_name:'v12.10'}, {tag_name:'v12.9'},
        {tag_name:'v12.11',draft:true}, {tag_name:'v12.12',prerelease:true}, {tag_name:'v12.10.1'}
    ]),'12.10.1');
});
test('fails explicitly instead of silently switching to unsupported server releases',()=>{
    assert.throws(()=>latest12([{tag_name:'v13.0'},{tag_name:'v12.3-rc1'}]));
});

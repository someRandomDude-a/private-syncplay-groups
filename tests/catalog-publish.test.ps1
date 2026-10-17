# Offline publication protocol regression: gh is mocked; no GitHub writes occur.
$ErrorActionPreference = 'Stop'
$calls = [Collections.Generic.List[object]]::new()
function gh {
    $arguments = @($args)
    $text = $input | Out-String
    $body = if ($text.Trim()) { $text | ConvertFrom-Json } else { $null }
    $calls.Add(@{ Arguments = $arguments; Body = $body })
    $global:LASTEXITCODE = 0
    if ($arguments -contains 'git/refs') { throw 'Unexpected endpoint' }
    if (($arguments -join ' ') -match 'git/commits/[a-f0-9]{40}') { return '{"tree":{"sha":"previous-tree"}}' }
    return '{"sha":"mock-sha"}'
}
$root = Split-Path $PSScriptRoot -Parent
$manifest = Join-Path $root 'artifacts/manifest.json'
$catalog = @(Get-Content $manifest -Raw | ConvertFrom-Json | ForEach-Object { $_ })
$version = $catalog[0].versions[0].version -replace '\.0$', ''
$zip = Join-Path $root "artifacts/PrivateSyncPlay-$version.zip"
$before = (Get-FileHash $zip).Hash
$rejected = $false
try { & (Join-Path $root 'scripts/package.ps1') -Version $version -PreviousManifest $manifest -Dotnet 'must-not-be-invoked' }
catch { if ($_.Exception.Message -notlike '*already in the published catalog*') { throw }; $rejected = $true }
if (-not $rejected -or (Get-FileHash $zip).Hash -ne $before) { throw 'Duplicate release must be rejected before rebuilding or replacing its ZIP' }
& (Join-Path $root 'scripts/publish-catalog.ps1') -Repository 'test/repository' -Manifest $manifest
$create = $calls | Where-Object { $_.Arguments -contains 'repos/test/repository/git/refs' }
if ($create.Body.ref -ne 'refs/heads/catalog') { throw 'First publication must create the catalog branch' }
$calls.Clear()
$parent = 'a' * 40
& (Join-Path $root 'scripts/publish-catalog.ps1') -Repository 'test/repository' -Manifest $manifest -ParentCommit $parent
$update = $calls | Where-Object { $_.Arguments -contains 'repos/test/repository/git/refs/heads/catalog' }
if ($update.Body.force -ne $false -or $update.Arguments -notcontains 'PATCH') { throw 'Catalog updates must be non-forced' }
$commit = $calls | Where-Object { $_.Arguments -contains 'repos/test/repository/git/commits' }
if ($commit.Body.parents.Count -ne 1 -or $commit.Body.parents[0] -ne $parent) { throw 'Catalog commit must preserve its parent' }
$tree = $calls | Where-Object { $_.Arguments -contains 'repos/test/repository/git/trees' }
if ($tree.Body.base_tree -ne 'previous-tree' -or $tree.Body.tree[0].path -ne 'manifest.json') { throw 'Only update manifest.json, preserving other catalog files' }
Write-Output 'PASS duplicate-version rejection, offline catalog branch creation, parent history, non-forced update and preserved tree'

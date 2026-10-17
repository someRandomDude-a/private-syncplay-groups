param([Parameter(Mandatory)][string]$Repository, [string]$Manifest = 'artifacts/manifest.json', [string]$ParentCommit = '')
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or ($ParentCommit -and $ParentCommit -notmatch '^[a-f0-9]{40}$')) { throw 'Invalid repository or parent commit' }
$catalog = @(Get-Content $Manifest -Raw | ConvertFrom-Json | ForEach-Object { $_ })
if ($catalog.Count -ne 1 -or $catalog[0].versions.Count -eq 0) { throw 'Refusing to publish an empty catalog' }
function Invoke-GitHub($Endpoint, $Body, $Method = 'POST') {
    $json = ConvertTo-Json -InputObject $Body -Depth 12 -Compress
    $result = $json | gh api --method $Method "repos/$Repository/$Endpoint" --input -
    if ($LASTEXITCODE -ne 0) { throw "Catalog publication failed: $Endpoint" }
    return $result | ConvertFrom-Json
}
$blob = Invoke-GitHub 'git/blobs' @{ content = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $Manifest))); encoding = 'base64' }
$treeBody = @{ tree = @(@{ path = 'manifest.json'; mode = '100644'; type = 'blob'; sha = $blob.sha }) }
if ($ParentCommit) {
    $oldCommitJson = gh api "repos/$Repository/git/commits/$ParentCommit"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read catalog parent tree' }
    $treeBody.base_tree = ($oldCommitJson | ConvertFrom-Json).tree.sha
}
$tree = Invoke-GitHub 'git/trees' $treeBody
$parents = @(); if ($ParentCommit) { $parents = @($ParentCommit) }
$commit = Invoke-GitHub 'git/commits' @{ message = 'Update Jellyfin plugin catalog'; tree = $tree.sha; parents = $parents;
    author = @{ name = 'github-actions[bot]'; email = '41898282+github-actions[bot]@users.noreply.github.com' } }
if ($ParentCommit) {
    # A concurrent branch change fails rather than overwriting someone else's catalog.
    $null = Invoke-GitHub 'git/refs/heads/catalog' @{ sha = $commit.sha; force = $false } 'PATCH'
} else {
    $null = Invoke-GitHub 'git/refs' @{ ref = 'refs/heads/catalog'; sha = $commit.sha }
}
Write-Output "Repository URL: https://raw.githubusercontent.com/$Repository/catalog/manifest.json"

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$folder = Join-Path ([IO.Path]::GetTempPath()) ('opencode/catalog-history-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $folder | Out-Null
try {
    $source = Get-Content (Join-Path $root 'artifacts/manifest.json') -Raw
    $incoming = @(ConvertFrom-Json $source | ForEach-Object { $_ })
    $previous = @(ConvertFrom-Json $source | ForEach-Object { $_ })
    $incoming[0].versions[0].version = '2.0.0.0'
    $previous[0].versions[0].version = '1.10.0.0'
    $older = ConvertFrom-Json (ConvertTo-Json $previous[0].versions[0])
    $older.version = '1.9.0.0'
    $previous[0].versions = @($older, $previous[0].versions[0])
    $newFile = Join-Path $folder 'manifest.json'; $oldFile = Join-Path $folder 'previous.json'
    [IO.File]::WriteAllText($newFile, (ConvertTo-Json -InputObject $incoming -Depth 12))
    [IO.File]::WriteAllText($oldFile, (ConvertTo-Json -InputObject $previous -Depth 12))
    & (Join-Path $root 'scripts/merge-catalog.ps1') -Manifest $newFile -PreviousManifest $oldFile
    $merged = @(Get-Content $newFile -Raw | ConvertFrom-Json | ForEach-Object { $_ })
    if (($merged[0].versions.version -join ',') -ne '2.0.0.0,1.10.0.0,1.9.0.0') { throw 'Catalog must preserve and numerically sort all versions' }
    if ($merged[0].versions[1].checksum -ne $previous[0].versions[1].checksum -or $merged[0].versions[1].sourceUrl -ne $previous[0].versions[1].sourceUrl) { throw 'Historical release metadata changed' }
    [IO.File]::WriteAllText($newFile, (ConvertTo-Json -InputObject $incoming -Depth 12))
    [IO.File]::WriteAllText($oldFile, (ConvertTo-Json -InputObject $incoming -Depth 12))
    $before = (Get-FileHash $newFile).Hash; $rejected = $false
    try { & (Join-Path $root 'scripts/merge-catalog.ps1') -Manifest $newFile -PreviousManifest $oldFile }
    catch { if ($_.Exception.Message -notlike '*already in the published catalog*') { throw }; $rejected = $true }
    if (-not $rejected -or (Get-FileHash $newFile).Hash -ne $before) { throw 'Duplicate merge must fail without altering catalog' }
    Write-Output 'PASS catalog history preservation, numeric version ordering, immutable metadata and duplicate rejection'
} finally { Remove-Item $folder -Recurse -Force }

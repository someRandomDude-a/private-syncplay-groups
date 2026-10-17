param([string]$Version = '0.1.0', [string]$Dotnet = 'dotnet',
    [string]$Repository = 'someRandomDude-a/private-syncplay-groups', [string]$PreviousManifest = '',
    [string]$Changelog = 'Preview release. See GitHub release notes for changes.')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have the form X.Y.Z' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository must have the form owner/name' }
$root = Split-Path $PSScriptRoot -Parent
& $Dotnet build (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay') -c Release "-p:Version=$Version.0"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$output = Join-Path $root 'artifacts'
$stage = Join-Path $output ('PrivateSyncPlay-' + $Version)
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.dll') $stage
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'node_modules/hls.js/LICENSE') (Join-Path $stage 'hls.js.LICENSE')
$file = 'PrivateSyncPlay-' + $Version + '.zip'
$zip = Join-Path $output $file
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$manifest = @(Get-Content (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json | ForEach-Object { $_ })
if ($manifest.Count -ne 1 -or $manifest[0].guid -ne '09514a9e-f7a0-4c5a-91bc-3a46338298ca') { throw 'Invalid repository metadata' }
$history = @()
if ($PreviousManifest) {
    $previous = @(Get-Content $PreviousManifest -Raw | ConvertFrom-Json | ForEach-Object { $_ })
    if ($previous.Count -ne 1 -or $previous[0].guid -ne $manifest[0].guid) { throw 'Previous manifest belongs to another plugin' }
    foreach ($entry in $previous[0].versions) {
        if ($entry.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $entry.checksum -notmatch '^[a-fA-F0-9]{32}$' -or $entry.sourceUrl -notmatch '^https://') { throw 'Invalid previous release metadata' }
        if ($entry.version -ne ($Version + '.0')) { $history += $entry }
    }
}
$current = @{
    version = $Version + '.0'; targetAbi = '12.0.0.0'; changelog = $Changelog
    sourceUrl = 'https://github.com/' + $Repository + '/releases/download/v' + $Version + '/' + $file
    checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
    timestamp = [DateTime]::UtcNow.ToString('o')
}
$manifest[0].versions = @(@($current) + $history | Sort-Object { [Version]$_.version } -Descending)
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText((Join-Path $output 'manifest.json'), $json, [System.Text.UTF8Encoding]::new($false))
$sdk = Join-Path $output 'PrivateSyncPlay.Api-1.0.0'
New-Item -ItemType Directory -Force $sdk | Out-Null
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll') $sdk
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.xml') $sdk
Copy-Item (Join-Path $root 'docs/plugin-api.md') $sdk
Copy-Item (Join-Path $root 'LICENSE') $sdk
Compress-Archive -Path (Join-Path $sdk '*') -DestinationPath (Join-Path $output 'PrivateSyncPlay.Api-1.0.0.zip') -Force
Write-Output "Package: $zip"

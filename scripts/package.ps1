param([string]$Version = '0.1.0', [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& $Dotnet build (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay') -c Release "-p:Version=$Version.0"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$output = Join-Path $root 'artifacts'
$stage = Join-Path $output ('PrivateSyncPlay-' + $Version)
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.dll') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'node_modules/hls.js/LICENSE') (Join-Path $stage 'hls.js.LICENSE')
$file = 'PrivateSyncPlay-' + $Version + '.zip'
$zip = Join-Path $output $file
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$manifest = @(@{
    guid = '09514a9e-f7a0-4c5a-91bc-3a46338298ca'; name = 'Private SyncPlay'; category = 'General'
    owner = 'someRandomDude-a'; description = 'Private SyncPlay groups, limited invites and isolated guest viewing.'
    overview = 'Private watch parties with administrator-controlled guest sharing.'
    versions = @(@{
        version = $Version + '.0'; targetAbi = '12.0.0.0'; changelog = 'Initial preview release.'
        sourceUrl = 'https://github.com/someRandomDude-a/private-syncplay-groups/releases/download/v' + $Version + '/' + $file
        checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('o')
    })
})
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText((Join-Path $output 'manifest.json'), $json, [System.Text.UTF8Encoding]::new($false))
Write-Output "Package: $zip"

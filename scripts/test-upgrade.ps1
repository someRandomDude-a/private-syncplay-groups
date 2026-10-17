param([Parameter(Mandatory)][string]$Directory, [string]$Dotnet = 'dotnet', [string]$Image = 'jellyfin/jellyfin:12.2')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$folder = [IO.Path]::GetFullPath($Directory)
$packages = Join-Path $folder 'packages'
# These versions exist only in disposable fixtures, never in the public catalog/artifacts directory.
& (Join-Path $PSScriptRoot 'package.ps1') -Version '0.0.1' -Dotnet $Dotnet -OutputDirectory $packages -Changelog 'Upgrade fixture: initial version.'
New-Item -ItemType Directory -Force $folder | Out-Null
$previous = Join-Path $folder 'initial-manifest.json'
Copy-Item (Join-Path $packages 'manifest.json') $previous
& (Join-Path $PSScriptRoot 'package.ps1') -Version '0.0.2' -Dotnet $Dotnet -OutputDirectory $packages -PreviousManifest $previous -Changelog 'Upgrade fixture: next version.'
& node (Join-Path $PSScriptRoot 'integration.mjs') (Join-Path $folder 'server') --install-from-catalog --with-consumer --test-upgrade "--packages=$packages" "--image=$Image"
if ($LASTEXITCODE -ne 0) { throw 'Jellyfin upgrade integration failed' }

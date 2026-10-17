param([string]$Version = '', [string]$Dotnet = 'dotnet',
    [string]$Repository = 'someRandomDude-a/private-syncplay-groups', [string]$PreviousManifest = '',
    [string]$Changelog = 'Preview release. See GitHub release notes for changes.', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $project = [xml](Get-Content (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay/Jellyfin.Plugin.PrivateSyncPlay.csproj') -Raw)
    $Version = ([string]$project.Project.PropertyGroup.Version) -replace '\.0$', ''
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have the form X.Y.Z' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository must have the form owner/name' }
$target = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$targetAbi = [string]$target.Project.PropertyGroup.JellyfinVersion + '.0'
$manifest = @(Get-Content (Join-Path $PSScriptRoot 'plugin-metadata.json') -Raw | ConvertFrom-Json | ForEach-Object { $_ })
if ($manifest.Count -ne 1 -or $manifest[0].guid -ne '09514a9e-f7a0-4c5a-91bc-3a46338298ca') { throw 'Invalid repository metadata' }
$history = @()
if ($PreviousManifest) {
    $previous = @(Get-Content $PreviousManifest -Raw | ConvertFrom-Json | ForEach-Object { $_ })
    if ($previous.Count -ne 1 -or $previous[0].guid -ne $manifest[0].guid) { throw 'Previous manifest belongs to another plugin' }
    foreach ($entry in $previous[0].versions) {
        if ($entry.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $entry.checksum -notmatch '^[a-fA-F0-9]{32}$' -or $entry.sourceUrl -notmatch '^https://') { throw 'Invalid previous release metadata' }
        if ($entry.version -eq ($Version + '.0')) { throw 'This version is already in the published catalog; publish a new version instead of replacing its ZIP/checksum' }
        if ([Version]$entry.version -gt [Version]($Version + '.0')) { throw 'New releases must increase the plugin version' }
        $history += $entry
    }
}
& $Dotnet build (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay') -c Release "-p:Version=$Version.0"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts' }
$stage = Join-Path $output ('PrivateSyncPlay-' + $Version)
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.dll') $stage
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'node_modules/hls.js/LICENSE') (Join-Path $stage 'hls.js.LICENSE')
$file = 'PrivateSyncPlay-' + $Version + '.zip'
$zip = Join-Path $output $file
$pluginFiles = @('Jellyfin.Plugin.PrivateSyncPlay.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll', 'LICENSE', 'README.md', 'hls.js.LICENSE')
Compress-Archive -Path @($pluginFiles | ForEach-Object { Join-Path $stage $_ }) -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    foreach ($required in @('Jellyfin.Plugin.PrivateSyncPlay.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll')) {
        if ($required -notin $names) { throw "Plugin DLL must be at ZIP root: $required" }
    }
    if (@($names | Where-Object { $_ -match '[/\\]|^(Jellyfin.Controller|Jellyfin.Model|MediaBrowser\.).*\.dll$' }).Count -gt 0) { throw 'Invalid plugin ZIP layout or bundled server assemblies' }
} finally { $archive.Dispose() }
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage 'Jellyfin.Plugin.PrivateSyncPlay.dll')).Version.ToString() -ne "$Version.0") { throw 'Assembly version does not match catalog version' }
$current = @{
    version = $Version + '.0'; targetAbi = $targetAbi; changelog = $Changelog
    sourceUrl = 'https://github.com/' + $Repository + '/releases/download/v' + $Version + '/' + $file
    checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
    timestamp = [DateTime]::UtcNow.ToString('o')
}
$manifest[0].versions = @(@($current) + $history | Sort-Object { [Version]$_.version } -Descending)
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText((Join-Path $output 'manifest.json'), $json, [System.Text.UTF8Encoding]::new($false))
$sdkProject = [xml](Get-Content (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/Jellyfin.Plugin.PrivateSyncPlay.Api.csproj') -Raw)
$sdkVersion = [string]$sdkProject.Project.PropertyGroup.Version
$sdkName = "PrivateSyncPlay.Api-$sdkVersion"
$sdk = Join-Path $output $sdkName
New-Item -ItemType Directory -Force $sdk | Out-Null
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll') $sdk
Copy-Item (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.xml') $sdk
Copy-Item (Join-Path $root 'docs/plugin-api.md') $sdk
Copy-Item (Join-Path $root 'LICENSE') $sdk
Copy-Item (Join-Path $root 'Directory.Build.props') $sdk
$exampleFiles = @('WatchPartyConsumer.csproj', 'ConsumerPlugin.cs', 'InviteController.cs', 'PartyAutomation.cs', 'README.md')
$examplePath = 'examples/WatchPartyConsumer'
New-Item -ItemType Directory -Force (Join-Path $sdk $examplePath) | Out-Null
foreach ($example in $exampleFiles) { Copy-Item (Join-Path $root "$examplePath/$example") (Join-Path $sdk "$examplePath/$example") }
$sdkFiles = @('Jellyfin.Plugin.PrivateSyncPlay.Api.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.xml', 'plugin-api.md', 'LICENSE', 'Directory.Build.props') + @($exampleFiles | ForEach-Object { "$examplePath/$_" })
$sdkZip = Join-Path $output "$sdkName.zip"
if (Test-Path -LiteralPath $sdkZip) { Remove-Item -LiteralPath $sdkZip }
$sdkArchive = [IO.Compression.ZipFile]::Open($sdkZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    # Explicit files only: never pick up a previous example build's bin/obj or other staging files.
    foreach ($entry in $sdkFiles) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sdkArchive, (Join-Path $sdk $entry), $entry) | Out-Null }
} finally { $sdkArchive.Dispose() }
Write-Output "Package: $zip"

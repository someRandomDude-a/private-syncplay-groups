param([string]$Dotnet = 'dotnet', [string]$Artifacts = 'artifacts', [string]$Directory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = [xml](Get-Content (Join-Path $root 'src/Jellyfin.Plugin.PrivateSyncPlay.Api/Jellyfin.Plugin.PrivateSyncPlay.Api.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$zip = Join-Path ([IO.Path]::GetFullPath($Artifacts)) "PrivateSyncPlay.Api-$version.zip"
if (-not $Directory) { $Directory = Join-Path ([IO.Path]::GetTempPath()) ('opencode/psp-sdk-' + [Guid]::NewGuid().ToString('N')) }
$Directory = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $Directory) { throw 'SDK validation requires a fresh disposable directory.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $expected = @('Jellyfin.Plugin.PrivateSyncPlay.Api.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.xml', 'plugin-api.md', 'LICENSE', 'Directory.Build.props',
        'examples/WatchPartyConsumer/WatchPartyConsumer.csproj', 'examples/WatchPartyConsumer/ConsumerPlugin.cs', 'examples/WatchPartyConsumer/InviteController.cs', 'examples/WatchPartyConsumer/PartyAutomation.cs', 'examples/WatchPartyConsumer/README.md')
    $actual = @($archive.Entries | ForEach-Object { $_.FullName })
    if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $_ -notin $expected }).Count -gt 0 -or @($expected | Where-Object { $_ -notin $actual }).Count -gt 0) { throw 'Unexpected SDK files, missing example, or accidentally packaged build outputs.' }
} finally { $archive.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $Directory)
$xml = Get-Content (Join-Path $Directory 'Jellyfin.Plugin.PrivateSyncPlay.Api.xml') -Raw
foreach ($api in @('IWatchPartyService', 'GetManagementAsync', 'KickGuestAndRevokeInviteAsync', 'AddPrivateSyncPlayGuestControl')) {
    if (-not $xml.Contains($api)) { throw "Missing IntelliSense documentation for $api" }
}
if ($xml.Contains('IWatchPartyManagementService')) { throw 'The SDK must expose one current service, not a compatibility companion.' }
if ($xml.Contains('SetGuestPermissionsAsync') -or $xml.Contains('WatchPartyGuestPermissions')) { throw 'Guest player options must be administrator-only, not owner API commands.' }
& $Dotnet build (Join-Path $Directory 'examples/WatchPartyConsumer/WatchPartyConsumer.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'The portable SDK consumer example failed to build.' }
if (-not (Test-Path (Join-Path $Directory 'examples/WatchPartyConsumer/bin/Release/net10.0/Jellyfin.Plugin.PrivateSyncPlay.Api.dll'))) { throw 'Consumer did not copy its SDK DLL.' }
Write-Output "PASS SDK $version archive whitelist, XML documentation and standalone example build: $Directory"

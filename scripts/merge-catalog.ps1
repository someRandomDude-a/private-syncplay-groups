param([Parameter(Mandatory)][string]$Manifest, [Parameter(Mandatory)][string]$PreviousManifest)
$ErrorActionPreference = 'Stop'
$current = @(Get-Content $Manifest -Raw | ConvertFrom-Json | ForEach-Object { $_ })
$previous = @(Get-Content $PreviousManifest -Raw | ConvertFrom-Json | ForEach-Object { $_ })
if ($current.Count -ne 1 -or $previous.Count -ne 1 -or $current[0].guid -ne $previous[0].guid -or $current[0].versions.Count -ne 1) {
    throw 'Expected one new plugin version and matching previous plugin metadata'
}
$release = $current[0].versions[0]
$seen = @{}
foreach ($entry in @($release) + @($previous[0].versions)) {
    $timestamp = [DateTimeOffset]::MinValue
    if ($entry.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $entry.targetAbi -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        $entry.checksum -notmatch '^[a-fA-F0-9]{32}$' -or $entry.sourceUrl -notmatch '^https://[^\s]+\.zip$' -or
        -not [DateTimeOffset]::TryParse([string]$entry.timestamp, [ref]$timestamp)) { throw 'Invalid release metadata' }
    if ($seen.ContainsKey($entry.version)) { throw 'This version is already in the published catalog; publish a new version instead of replacing its ZIP/checksum' }
    $seen[$entry.version] = $true
}
foreach ($entry in $previous[0].versions) {
    if ([Version]$entry.version -gt [Version]$release.version) { throw 'New releases must increase the plugin version' }
}
$current[0].versions = @(@($release) + @($previous[0].versions) | Sort-Object { [Version]$_.version } -Descending)
[IO.File]::WriteAllText((Resolve-Path $Manifest), (ConvertTo-Json -InputObject $current -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output "Catalog retains $($current[0].versions.Count) published versions; ZIPs unchanged."

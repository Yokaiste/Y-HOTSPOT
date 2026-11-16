param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'HotspotApp\HotspotApp.csproj'
[xml]$metadata = Get-Content -LiteralPath $project -Raw -Encoding utf8
$version = [string]$metadata.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected project version' }
$publish = Join-Path $repoRoot 'dist\package-build'
$packageName = "Y-HOTSPOT-$version-win-x64"
$package = Join-Path $repoRoot "dist\releases\$packageName"

if (-not $SkipBuild) {
    # Never delete the live dist folder, its logs, or pending recovery journals.
    & dotnet publish $project -c Release -r win-x64 -o $publish -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
}
$executable = Join-Path $publish 'HotspotApp.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw "Missing executable: $executable" }
if ((Get-Item -LiteralPath $executable).VersionInfo.FileVersion -ne "$version.0") { throw 'Executable version does not match project' }

# Explicit allowlist: a release must never inherit live configuration or captures.
$files = [ordered]@{
    'Y-HOTSPOT.exe' = $executable
    'README.md' = (Join-Path $repoRoot 'README.md')
    'LICENSE' = (Join-Path $repoRoot 'LICENSE')
    'NOTICE' = (Join-Path $repoRoot 'NOTICE')
    'THIRD-PARTY-NOTICES.md' = (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md')
    'docs\built-in-dns.md' = (Join-Path $repoRoot 'docs\built-in-dns.md')
    'docs\release-readiness.md' = (Join-Path $repoRoot 'docs\release-readiness.md')
    'docs\release-notes-2.0.0.md' = (Join-Path $repoRoot 'docs\release-notes-2.0.0.md')
    'licenses\ndisapi-MIT.txt' = (Join-Path $repoRoot 'HotspotApp\Import\ndisapi\LICENSE.txt')
    'licenses\wintun-prebuilt.txt' = (Join-Path $repoRoot 'HotspotApp\Import\wintun\LICENSE.txt')
    'licenses\QRCoder.txt' = (Join-Path $repoRoot 'licenses\QRCoder.txt')
    'licenses\dotnet-runtime.txt' = (Join-Path $repoRoot 'licenses\dotnet-runtime.txt')
    'licenses\dotnet-third-party.txt' = (Join-Path $repoRoot 'licenses\dotnet-third-party.txt')
    'licenses\WindowsAppSDK.txt' = (Join-Path $repoRoot 'licenses\WindowsAppSDK.txt')
    'licenses\WindowsAppSDK-NOTICE.txt' = (Join-Path $repoRoot 'licenses\WindowsAppSDK-NOTICE.txt')
    'HotspotApp\Import\ndisapi\README.md' = (Join-Path $repoRoot 'HotspotApp\Import\ndisapi\README.md')
    'HotspotApp\Import\ndisapi\LICENSE.txt' = (Join-Path $repoRoot 'HotspotApp\Import\ndisapi\LICENSE.txt')
    'HotspotApp\Import\wintun\LICENSE.txt' = (Join-Path $repoRoot 'HotspotApp\Import\wintun\LICENSE.txt')
}
if (Test-Path -LiteralPath $package) {
    foreach ($existing in Get-ChildItem -LiteralPath $package -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($package, $existing.FullName)
        if (-not $files.Contains($relative)) { throw "Unexpected release file; refusing to package: $relative" }
    }
}
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path $package $entry.Key
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $entry.Value -Destination $target -Force
}
$zip = Join-Path $repoRoot "dist\releases\$packageName.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $actual = @($archive.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('\','/') })
    $expected = @($files.Keys | ForEach-Object { "$packageName/" + $_.Replace('\','/') })
    if (Compare-Object $expected $actual) { throw 'Release ZIP contents differ from allowlist' }
} finally { $archive.Dispose() }
Write-Output "Prepared release: $zip"

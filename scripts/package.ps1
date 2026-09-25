param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "DiagnosticDamageProbe.csproj"
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Canonical project Version is missing." }

$packageName = "CombatMeter-$version"
$outputRoot = Join-Path $projectRoot "outputs/package"
$staging = Join-Path $outputRoot $packageName
$zipPath = Join-Path $outputRoot "$packageName.zip"
$dllPath = Join-Path $projectRoot "outputs/build/$Configuration/netstandard2.1/CombatMeter.dll"
$manifestPath = Join-Path $projectRoot "manifest.json"
$licensePath = Join-Path $projectRoot "LICENSE"

& dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
if ($Configuration -ne "Release") { throw "Thunderstore packages must use the Release configuration." }
if (!(Test-Path -LiteralPath $dllPath)) { throw "Release CombatMeter.dll is missing: $dllPath" }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.version_number = $version
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
if ($manifest.name -ne "CombatMeter") { throw "Manifest package name must be CombatMeter." }
if ($manifest.version_number -ne $version) { throw "Manifest version $($manifest.version_number) differs from project version $version." }
if ($manifest.name -notmatch '^[A-Za-z0-9_]+$') { throw "Manifest package name contains invalid characters." }
if ([string]::IsNullOrWhiteSpace($manifest.description) -or $manifest.description.Length -gt 250) { throw "Manifest description is missing or too long." }
if ($manifest.website_url -ne "https://github.com/Likhtenvald/CombatMeter") { throw "Manifest website_url must point to the public CombatMeter repository." }
if ($manifest.dependencies.Count -ne 1 -or $manifest.dependencies[0] -ne "denikson-BepInExPack_Valheim-5.4.2350") { throw "Unexpected dependency list." }
if (!(Test-Path -LiteralPath $licensePath)) { throw "LICENSE is required for the Thunderstore package." }

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $staging "plugins") -Force | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot "CHANGELOG.md") -Destination $staging
Copy-Item -LiteralPath $licensePath -Destination $staging
Copy-Item -LiteralPath $dllPath -Destination (Join-Path $staging "plugins/CombatMeter.dll")

$icon = Join-Path $projectRoot "icon.png"
if (!(Test-Path -LiteralPath $icon)) {
    Write-Host "Incomplete staging created at: $staging"
    Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object { $_.FullName.Substring($staging.Length + 1) }
    throw "icon.png is required before a ready-to-upload Thunderstore ZIP can be created."
}
Copy-Item -LiteralPath $icon -Destination $staging

$forbidden = Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb" -or $_.Name -eq "DiagnosticDamageProbe.dll" -or $_.Name -like "*ProbeChecks*"
}
if ($forbidden) { throw "Forbidden package artifact: $($forbidden.FullName -join ', ')" }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -CompressionLevel Optimal

$expected = @("CHANGELOG.md", "LICENSE", "README.md", "icon.png", "manifest.json", "plugins/CombatMeter.dll")
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try { $actual = @($archive.Entries | Where-Object { $_.FullName -notmatch '/$' } | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object) }
finally { $archive.Dispose() }
$expected = @($expected | Sort-Object)
if (Compare-Object $expected $actual) { throw "ZIP entries differ from the packaging allowlist." }
if ([IO.Path]::GetFileName($zipPath) -ne "CombatMeter-$version.zip") { throw "ZIP filename/version mismatch." }
Write-Host "Package validated: $zipPath"
$actual | ForEach-Object { Write-Host $_ }

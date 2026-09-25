param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dependencyDir = Join-Path $projectRoot 'work/dependencies/BepInEx-5.4.23.5'
$archiveHash = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'
New-Item -ItemType Directory -Path $dependencyDir -Force | Out-Null
if (!$ArchivePath) {
    $ArchivePath = Join-Path $projectRoot 'work/BepInEx_win_x64_5.4.23.5.zip'
    if (!(Test-Path -LiteralPath $ArchivePath)) {
        Invoke-WebRequest -Uri 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip' -OutFile $ArchivePath
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $archiveHash) {
    throw 'Unexpected BepInEx archive SHA256. References were not extracted.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    foreach ($fileName in @('BepInEx.dll', '0Harmony.dll')) {
        $entry = $archive.GetEntry("BepInEx/core/$fileName")
        if (!$entry) { throw "Missing BepInEx/core/$fileName in archive." }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dependencyDir $fileName), $true)
    }
} finally {
    $archive.Dispose()
}
Write-Output "Build references prepared in $dependencyDir. No game files were changed."

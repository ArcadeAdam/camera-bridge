param([switch]$NoZip)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $sourceRoot 'package.json') -Raw | ConvertFrom-Json).version
if ($version -notmatch '^[0-9A-Za-z._-]+$') { throw 'Invalid package version' }
$outputFolder = Join-Path $sourceRoot "dist\camera-bridge-$version-win-x64"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is missing.' }
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
$exePath = Join-Path $outputFolder 'CameraBridge.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'portable') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ "/out:$exePath" "/win32manifest:$(Join-Path $sourceRoot 'portable\app.manifest')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'portable\CameraBridge.cfg') -Destination $outputFolder -Force
if (-not $NoZip) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $sourceRoot "dist\camera-bridge-$version-win-x64.zip"
    $temporaryZip = $zipPath + '.tmp'
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip }
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('CameraBridge.exe','CameraBridge.cfg')) {
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead((Join-Path $outputFolder $name))
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
    Get-Item -LiteralPath $zipPath | Select-Object Name,Length
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
}

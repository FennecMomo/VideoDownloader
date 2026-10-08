param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root 'dist\BiliCache'
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
foreach ($name in @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $root "tools\$name"))) { throw "Missing tools/$name. Run scripts/Install-Tools.ps1 first." }
}
dotnet publish (Join-Path $root 'src\BiliCache\BiliCache.csproj') -c Release -r win-x64 --self-contained $selfContainedValue -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$toolsDestination = Join-Path $destination 'tools'
New-Item -ItemType Directory -Force -Path $toolsDestination | Out-Null
foreach ($name in @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $root "tools\$name") -Destination $toolsDestination -Force
}
Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $destination -Force
Write-Host "Ready: $destination\BiliCache.exe"

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'tools'
New-Item -ItemType Directory -Force -Path $target | Out-Null

function Get-VerifiedAsset($Repository, $AssetName, $Destination) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest"
    $asset = $release.assets | Where-Object name -eq $AssetName | Select-Object -First 1
    if (!$asset -or !$asset.digest -or !$asset.digest.StartsWith('sha256:')) { throw "Missing asset or SHA256 digest: $AssetName" }
    $temp = "$Destination.download"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $temp
    $actual = 'sha256:' + (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $asset.digest) { throw "Checksum mismatch: $AssetName" }
    Move-Item -LiteralPath $temp -Destination $Destination -Force
    $release | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$Destination.release.json" -Encoding utf8
    Write-Host "Verified: $AssetName ($($release.tag_name))"
}

Get-VerifiedAsset 'yt-dlp/yt-dlp' 'yt-dlp.exe' (Join-Path $target 'yt-dlp.exe')
$zip = Join-Path $target 'ffmpeg.zip'
Get-VerifiedAsset 'yt-dlp/FFmpeg-Builds' 'ffmpeg-master-latest-win64-gpl.zip' $zip
$extract = Join-Path $target ('ffmpeg-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $zip -DestinationPath $extract
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt')) {
    $source = Get-ChildItem -LiteralPath $extract -Recurse -File | Where-Object Name -eq $name | Select-Object -First 1
    if (!$source) { throw "Missing extracted file: $name" }
    Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $target $name) -Force
}
Write-Host "Tools ready: $target"

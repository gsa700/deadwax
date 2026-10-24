# Fetches the native libraries Deadwax carries, for a Windows development build.
#
# libcdio and libcdio-paranoia are C libraries, so NuGet cannot bring
# them in. This fetches OUR build of them (tools/build-natives, published as a
# natives-N pre-release on this repository) into native\win-x64\, where the
# project picks them up. The same files a release carries inside its exe.
# Never committed: native\ is git-ignored, and they are binaries of GPL
# projects whose source is attached to the release they came from.
#
#   .\tools\get-natives.ps1           fetch the pinned release, if not here already
#   .\tools\get-natives.ps1 -Force    fetch it again regardless
#
# Then `dotnet run --project src\Deadwax.Cli -- natives` says what loaded.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tag = (Get-Content (Join-Path $PSScriptRoot 'NATIVES_RELEASE') -Raw).Trim()
$rid = 'win-x64'
$dir = Join-Path $root "native\$rid"
$stamp = Join-Path $dir 'natives-build-info.txt'
if (-not $Force -and (Test-Path $stamp) -and (Test-Path (Join-Path $dir 'libcdio-19.dll'))) {
    Write-Host "native\$rid already holds: $((Get-Content $stamp -TotalCount 1))"
    exit 0
}
$zip = "$tag-$rid.zip"
$base = "https://github.com/gsa700/deadwax/releases/download/$tag"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "deadwax-natives-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "fetching $tag ($zip)"
    Invoke-WebRequest -Uri "$base/$zip" -OutFile (Join-Path $tmp $zip)
    Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS')
    $expected = (Get-Content (Join-Path $tmp 'SHA256SUMS') | Where-Object { $_ -like "*$zip" }) -split '\s+' | Select-Object -First 1
    $actual = (Get-FileHash (Join-Path $tmp $zip) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "$zip does not match SHA256SUMS ($actual vs $expected); nothing installed" }
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Path $dir | Out-Null
    Expand-Archive -Path (Join-Path $tmp $zip) -DestinationPath $dir -Force
    Write-Host "native\$rid <- $((Get-Content $stamp -TotalCount 1))"
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

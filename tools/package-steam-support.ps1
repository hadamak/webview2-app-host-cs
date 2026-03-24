$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sdkRoot = if ($env:STEAMWORKS_SDK_ROOT) { $env:STEAMWORKS_SDK_ROOT } else { "" }

if ([string]::IsNullOrWhiteSpace($sdkRoot)) {
    throw "STEAMWORKS_SDK_ROOT is not set."
}

$header = Join-Path $sdkRoot "public\steam\steam_api.h"
$dll = Join-Path $sdkRoot "redistributable_bin\win64\steam_api64.dll"
$lib = Join-Path $sdkRoot "redistributable_bin\win64\steam_api64.lib"

foreach ($path in @($header, $dll, $lib)) {
    if (-not (Test-Path $path)) {
        throw "Missing Steamworks SDK file: $path"
    }
}

Push-Location $repoRoot
try {
    msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
    msbuild src\steam-bridge\SteamBridge.vcxproj /p:Configuration=Release /p:Platform=x64 /p:SteamworksSdkDir="$sdkRoot"

    $base = "src/bin/x64/Release/net472"
    $outDir = "steam-support/_build/WebView2AppHost-SteamSupport-win-x64"
    $zipPath = "steam-support/WebView2AppHost-SteamSupport-win-x64.zip"
    $hashPath = "$zipPath.sha256"

    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath) { Remove-Item $hashPath -Force }

    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    New-Item -ItemType Directory -Force -Path "steam-support" | Out-Null

    Copy-Item "$base/steam_bridge.dll" $outDir
    Copy-Item $dll $outDir
    Copy-Item "web-content/steam.js" $outDir
    Copy-Item "samples/steam-complete" "$outDir/steam-sample" -Recurse
    Copy-Item "docs/steam/overview.md" "$outDir/STEAM.md"
    Copy-Item "docs/steam" "$outDir/steam-docs" -Recurse
    Copy-Item "docs/README_STEAM.txt" "$outDir/README.txt"
    Copy-Item "docs/README_STEAM.en.txt" "$outDir/README.en.txt"
    Copy-Item "LICENSE" $outDir
    Copy-Item "THIRD_PARTY_NOTICES.md" $outDir

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zipPath)

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path -Leaf $zipPath)" | Out-File $hashPath -Encoding ascii
}
finally {
    Pop-Location
}

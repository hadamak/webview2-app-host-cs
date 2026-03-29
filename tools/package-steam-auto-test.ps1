<#
.SYNOPSIS
    Steam 自動テスト用コンテンツを ZIP にパッケージする。

.DESCRIPTION
    tests/steam-auto/ を ZIP に固め、tests/steam-auto/steam-auto.zip に出力する。
    setup-local-dev.ps1 の最後に表示されるコマンドでそのまま使える。

    起動方法:
      .\src\bin\x64\Debug\net472\WebView2AppHost.exe tests\steam-auto\steam-auto.zip

.EXAMPLE
    .\tools\package-steam-auto-test.ps1
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    $srcDir = "tests\steam-auto"
    $steamJs = "src\steam.js"
    $buildDir = "tests\steam-auto\_build"
    $zipOut = "tests\steam-auto\steam-auto.zip"

    # ---- _build/ に素材を集める ----
    if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

    Copy-Item "$srcDir\index.html"    $buildDir
    Copy-Item "$srcDir\app.conf.json" $buildDir

    if (Test-Path $steamJs) {
        Copy-Item $steamJs $buildDir
    } else {
        Write-Warning "steam.js が見つかりません: $steamJs （steam.js なしでパッケージします）"
    }

    # ---- ZIP 化 ----
    if (Test-Path $zipOut) { Remove-Item $zipOut -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($buildDir, $zipOut)

    Remove-Item $buildDir -Recurse -Force

    Write-Host "完了: $zipOut" -ForegroundColor Green
    Write-Host ""
    Write-Host "起動コマンド例:" -ForegroundColor Yellow
    Write-Host "  .\src\bin\x64\Debug\net472\WebView2AppHost.exe $zipOut"
}
finally {
    Pop-Location
}

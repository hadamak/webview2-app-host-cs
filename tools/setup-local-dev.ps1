<#
.SYNOPSIS
    ローカル開発環境をビルド・セットアップする。

.DESCRIPTION
    以下をまとめて実行する:
      1. ホスト本体 (WebView2AppHost.csproj) をビルド
      2. 汎用プラグイン (WebView2AppHost.GenericDllPlugin.dll) をビルド
      3. テスト用 DLL (TestLib.dll) をビルド
      4. Facepunch.Steamworks DLL を EXE と同じフォルダにコピー
      5. テスト用 DLL を EXE と同じフォルダにコピー
      6. 汎用プラグイン DLL を EXE と同じフォルダにコピー
      7. test-www/ を出力先の www/ にコピー
      8. テスト用サイドカーを出力先にコピー

    実行後は生成された EXE をそのまま起動して動作確認できる。

.PARAMETER Configuration
    ビルド構成。Debug (既定) または Release。

.EXAMPLE
    # 通常のローカル開発セットアップ
    .\tools\setup-local-dev.ps1

.EXAMPLE
    # Release ビルドでセットアップ（配布物に近い構成で確認したい場合）
    .\tools\setup-local-dev.ps1 -Configuration Release
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # ---------------------------------------------------------------------------
    # 1. ホスト本体ビルド
    # ---------------------------------------------------------------------------
    Write-Host "==> ホスト本体をビルド中... ($Configuration/x64)" -ForegroundColor Cyan
    msbuild src\WebView2AppHost.csproj `
        "/t:Restore;Build" `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "ホスト本体のビルドに失敗しました" }

    $outDir = "src\bin\x64\$Configuration\net472"
    Write-Host "    出力先: $outDir" -ForegroundColor Gray

    # ---------------------------------------------------------------------------
    # 2. 汎用プラグイン DLL ビルド
    # ---------------------------------------------------------------------------
    Write-Host "==> 汎用プラグイン DLL をビルド中..." -ForegroundColor Cyan
    dotnet build src-generic\WebView2AppHost.GenericDllPlugin.csproj `
        -c $Configuration -r win-x64 --no-self-contained `
        /p:Platform=x64 `
        /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "汎用プラグイン DLL のビルドに失敗しました" }

    Write-Host "==> 汎用サイドカープラグイン DLL をビルド中..." -ForegroundColor Cyan
    dotnet build src-generic\WebView2AppHost.GenericSidecarPlugin.csproj `
        -c $Configuration -r win-x64 --no-self-contained `
        /p:Platform=x64 `
        /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "汎用サイドカープラグイン DLL のビルドに失敗しました" }

    # ---------------------------------------------------------------------------
    # 3. テスト用 DLL ビルド
    # ---------------------------------------------------------------------------
    Write-Host "==> テスト用 DLL をビルド中..." -ForegroundColor Cyan
    dotnet build tests\TestDll\TestDll.csproj `
        -c $Configuration -r win-x64 --no-self-contained `
        /p:Platform=x64 `
        /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "テスト用 DLL のビルドに失敗しました" }

    # ---------------------------------------------------------------------------
    # 4. Facepunch.Steamworks DLL をコピー
    # ---------------------------------------------------------------------------
    Write-Host "==> Facepunch.Steamworks DLL を配置中..." -ForegroundColor Cyan

    $steamDllPath = "Facepunch.Steamworks\Facepunch.Steamworks\bin\x64\$Configuration\net46\Facepunch.Steamworks.Win64.dll"
    if (Test-Path $steamDllPath) {
        Copy-Item $steamDllPath $outDir -Force
        Write-Host "    Facepunch.Steamworks.Win64.dll -> $outDir" -ForegroundColor Gray
    }
    else {
        Write-Warning "Facepunch.Steamworks.Win64.dll が見つかりません: $steamDllPath"
        Write-Warning "  Steam 機能を使用するには Facepunch.Steamworks をビルドしてください"
    }

    # steam_api64.dll もコピー（存在する場合）
    $steamApiPath = "Facepunch.Steamworks\Facepunch.Steamworks\bin\x64\$Configuration\net46\steam_api64.dll"
    if (Test-Path $steamApiPath) {
        Copy-Item $steamApiPath $outDir -Force
        Write-Host "    steam_api64.dll -> $outDir" -ForegroundColor Gray
    }

    # ---------------------------------------------------------------------------
    # 5. テスト用 DLL をコピー
    # ---------------------------------------------------------------------------
    Write-Host "==> テスト用 DLL を配置中..." -ForegroundColor Cyan

    $testDllBuildDir = "tests\TestDll\bin\x64\$Configuration\net472\win-x64"
    $testDllFiles = @("TestLib.dll")
    foreach ($file in $testDllFiles) {
        $src = Join-Path $testDllBuildDir $file
        if (-not (Test-Path $src)) {
            throw "テスト用 DLL ファイルが見つかりません: $src"
        }
        Copy-Item $src $outDir -Force
        Write-Host "    $file -> $outDir" -ForegroundColor Gray
    }

    # ---------------------------------------------------------------------------
    # 6. 汎用プラグイン DLL をコピー
    # ---------------------------------------------------------------------------
    Write-Host "==> 汎用プラグイン DLL を配置中..." -ForegroundColor Cyan

    $genericDllBuildDir = "src-generic\bin\x64\$Configuration\net472\win-x64"
    $genericDllFiles = @("WebView2AppHost.GenericDllPlugin.dll", "WebView2AppHost.GenericSidecarPlugin.dll")
    foreach ($file in $genericDllFiles) {
        $src = Join-Path $genericDllBuildDir $file
        if (-not (Test-Path $src)) {
            Write-Warning "汎用プラグイン DLL ファイルが見つかりません: $src"
        }
        else {
            Copy-Item $src $outDir -Force
            Write-Host "    $file -> $outDir" -ForegroundColor Gray
        }
    }

    # ---------------------------------------------------------------------------
    # 7. test-www/ を www/ にコピー
    # ---------------------------------------------------------------------------
    Write-Host "==> テスト用 Web コンテンツを配置中..." -ForegroundColor Cyan

    $wwwDest = Join-Path $outDir "www"
    New-Item -ItemType Directory -Force -Path $wwwDest | Out-Null

    # test-www/ 以下を www/ にコピー（既存ファイルは上書き）
    Copy-Item "test-www\*" $wwwDest -Recurse -Force
    Write-Host "    test-www -> $wwwDest" -ForegroundColor Gray

    # ---------------------------------------------------------------------------
    # 8. テスト用サイドカーをコピー
    # ---------------------------------------------------------------------------
    Write-Host "==> テスト用サイドカーを配置中..." -ForegroundColor Cyan

    $testSidecarDest = Join-Path $outDir "test-sidecar"
    New-Item -ItemType Directory -Force -Path $testSidecarDest | Out-Null

    # test_sidecar.js が存在する場合はコピー
    $testSidecarJs = "tests\TestSidecar\test_sidecar.js"
    if (Test-Path $testSidecarJs) {
        Copy-Item $testSidecarJs $testSidecarDest -Force
        Write-Host "    test_sidecar.js -> $testSidecarDest" -ForegroundColor Gray
    }
    else {
        Write-Warning "テスト用サイドカーが見つかりません: $testSidecarJs"
    }

    # node-runtime/ をコピー
    $nodeRuntimeDest = Join-Path $outDir "node-runtime"
    New-Item -ItemType Directory -Force -Path $nodeRuntimeDest | Out-Null
    if (Test-Path "node-runtime\server.js") {
        Copy-Item "node-runtime\server.js" $nodeRuntimeDest -Force
        Write-Host "    node-runtime\server.js -> $nodeRuntimeDest" -ForegroundColor Gray
    }

    # ---------------------------------------------------------------------------
    # 完了メッセージ
    # ---------------------------------------------------------------------------
    $exePath = Join-Path $outDir "WebView2AppHost.exe"
    Write-Host ""
    Write-Host "セットアップ完了" -ForegroundColor Green
    Write-Host "EXE: $exePath"
}
finally {
    Pop-Location
}

<#
.SYNOPSIS
    ローカル開発環境をビルド・セットアップする。

.DESCRIPTION
    以下をまとめて実行する:
      1. ホスト本体 (WebView2AppHost.csproj) をビルド
      2. Steam DLL (WebView2AppHost.Steam.csproj) をビルド
      3. Steam 関連 DLL を EXE と同じフォルダにコピー
      4. test-www/ と steam.js を出力先の www/ にコピー

    実行後は生成された EXE をそのまま起動して動作確認できる。

.PARAMETER Configuration
    ビルド構成。Debug (既定) または Release。

.PARAMETER SkipSteam
    Steam DLL のビルドと配置をスキップする。
    Steamworks SDK サブモジュールが未取得の場合に使う。

.EXAMPLE
    # 通常のローカル開発セットアップ
    .\tools\setup-local-dev.ps1

.EXAMPLE
    # Steam なしでホスト本体だけセットアップ
    .\tools\setup-local-dev.ps1 -SkipSteam

.EXAMPLE
    # Release ビルドでセットアップ（配布物に近い構成で確認したい場合）
    .\tools\setup-local-dev.ps1 -Configuration Release
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$SkipSteam
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
    # 2. Steam DLL ビルド（オプション）
    # ---------------------------------------------------------------------------
    if (-not $SkipSteam) {
        $sdkCheck = "Facepunch.Steamworks\Facepunch.Steamworks\Facepunch.Steamworks.Win64.csproj"
        if (-not (Test-Path $sdkCheck)) {
            Write-Warning "Steamworks サブモジュールが見つかりません: $sdkCheck"
            Write-Warning "  git submodule update --init --recursive を実行してください"
            Write-Warning "  Steam DLL のビルドをスキップします（-SkipSteam で明示的に省略可能）"
            $SkipSteam = $true
        }
    }

    if (-not $SkipSteam) {
        Write-Host "==> Steam DLL をビルド中..." -ForegroundColor Cyan
        dotnet build src-steam\WebView2AppHost.Steam.csproj `
            -c $Configuration -r win-x64 --no-self-contained `
            /p:Platform=x64 `
            /v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Steam DLL のビルドに失敗しました" }

        # ---------------------------------------------------------------------------
        # 3. Steam 関連 DLL を EXE 隣接フォルダにコピー
        # ---------------------------------------------------------------------------
        Write-Host "==> Steam DLL を配置中..." -ForegroundColor Cyan

        $steamBuildDir = "src-steam\bin\x64\$Configuration\net472\win-x64"

        $steamFiles = @(
            "WebView2AppHost.Steam.dll",
            "Facepunch.Steamworks.Win64.dll",
            "steam_api64.dll"
        )
        foreach ($file in $steamFiles) {
            $src = Join-Path $steamBuildDir $file
            if (-not (Test-Path $src)) {
                throw "Steam ファイルが見つかりません: $src"
            }
            Copy-Item $src $outDir -Force
            Write-Host "    $file -> $outDir" -ForegroundColor Gray
        }
    } else {
        Write-Host "==> Steam DLL のビルドをスキップ" -ForegroundColor Gray
    }

    # ---------------------------------------------------------------------------
    # 4. test-www/ と steam.js を www/ にコピー
    #    Debug ビルドでは .csproj の CopyManualWwwContent ターゲットが既に実行済みだが、
    #    Release や手動での確認に備えて明示的にコピーする。
    # ---------------------------------------------------------------------------
    Write-Host "==> テスト用 Web コンテンツを配置中..." -ForegroundColor Cyan

    $wwwDest = Join-Path $outDir "www"
    New-Item -ItemType Directory -Force -Path $wwwDest | Out-Null

    # test-www/ 以下を www/ にコピー（既存ファイルは上書き）
    Copy-Item "test-www\*" $wwwDest -Recurse -Force
    Write-Host "    test-www\ -> $wwwDest\" -ForegroundColor Gray

    # steam.js を www/ に配置（test-www 内に存在しない場合は src/ からコピー）
    $steamJs = "src\steam.js"
    if (Test-Path $steamJs) {
        Copy-Item $steamJs $wwwDest -Force
        Write-Host "    steam.js -> $wwwDest\" -ForegroundColor Gray
    }

    # ---------------------------------------------------------------------------
    # 完了メッセージ
    # ---------------------------------------------------------------------------
    $exePath = Join-Path $outDir "WebView2AppHost.exe"
    Write-Host ""
    Write-Host "セットアップ完了" -ForegroundColor Green
    Write-Host "EXE: $exePath"
    if (-not $SkipSteam) {
        Write-Host "Steam: 有効 (AppID は test-www\app.conf.json の steamAppId を参照)"
    } else {
        Write-Host "Steam: 無効"
    }
    Write-Host ""
    Write-Host "起動するには:" -ForegroundColor Yellow
    Write-Host "  & '$exePath'"
    Write-Host ""
    Write-Host "Steam 自動テストページで起動するには:" -ForegroundColor Yellow
    Write-Host "  & '$exePath' tests\steam-auto\steam-auto.zip"
}
finally {
    Pop-Location
}

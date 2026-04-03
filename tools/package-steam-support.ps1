$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    WebView2AppHost の Steam サポート ZIP を作成する。

.DESCRIPTION
    WebView2AppHost.Steam.dll プロジェクトをビルドし、
    Steam 関連ファイルを ZIP に固めるスクリプト。

    必要なもの:
      - Steamworks SDK（steam_api64.dll の取得元、Facepunch.Steamworks サブモジュールに同梱）
      - dotnet CLI（ビルド・NuGet リストア用）

    使い方:
      .\tools\package-steam-support.ps1
#>

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # --- 1. Steam DLL プロジェクトをビルド ---
    Write-Host "Steam DLL をビルド中..." -ForegroundColor Cyan
    dotnet build src-steam\WebView2AppHost.Steam.csproj `
        -c Release -r win-x64 --no-self-contained `
        /p:Platform=x64

    $base   = "src-steam\bin\x64\Release\net48\win-x64"
    $outDir = "steam-support\_build\WebView2AppHost-SteamSupport-win-x64"
    $zipPath = "steam-support\WebView2AppHost-SteamSupport-win-x64.zip"
    $hashPath = "$zipPath.sha256"

    # --- 2. 出力ディレクトリを初期化 ---
    if (Test-Path $outDir)  { Remove-Item $outDir  -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath){ Remove-Item $hashPath -Force }

    New-Item -ItemType Directory -Force -Path $outDir   | Out-Null
    New-Item -ItemType Directory -Force -Path "steam-support" | Out-Null

    # --- 3. ファイルをコピー ---
    Write-Host "ファイルをコピー中..." -ForegroundColor Cyan

    # WebView2AppHost.Steam.dll
    $steamBridgeDll = Join-Path $base "WebView2AppHost.Steam.dll"
    if (-not (Test-Path $steamBridgeDll)) {
        throw "WebView2AppHost.Steam.dll が見つかりません: $steamBridgeDll`nビルドが成功していることを確認してください。"
    }
    Copy-Item $steamBridgeDll $outDir

    # Facepunch.Steamworks DLL
    $facepunchDll = Join-Path $base "Facepunch.Steamworks.Win64.dll"
    if (-not (Test-Path $facepunchDll)) {
        throw "Facepunch.Steamworks.Win64.dll が見つかりません: $facepunchDll`nビルドが成功していることを確認してください。"
    }
    Copy-Item $facepunchDll $outDir

    # Steamworks SDK の steam_api64.dll
    $steamApiDll = Join-Path $base "steam_api64.dll"
    if (-not (Test-Path $steamApiDll)) {
        throw "steam_api64.dll が見つかりません: $steamApiDll"
    }
    Copy-Item $steamApiDll $outDir

    # JS ブリッジ
    Copy-Item "src\steam.js" $outDir

    # サンプルフォルダをコピーし、そこに正しい steam.js を配置する
    Copy-Item "samples\steam-complete" "$outDir\steam-sample" -Recurse
    Copy-Item "src\steam.js" "$outDir\steam-sample\steam.js" -Force

    # ドキュメント（overview と getting-started のみ）
    Copy-Item "docs\steam\overview.md"       "$outDir\STEAM.md"
    Copy-Item "docs\steam\getting-started.md" "$outDir\STEAM-SETUP.md"

    # README
    Copy-Item "docs\README_STEAM.txt"    "$outDir\README.txt"
    Copy-Item "docs\README_STEAM.en.txt" "$outDir\README.en.txt"

    # ライセンス
    Copy-Item "LICENSE"               $outDir
    Copy-Item "THIRD_PARTY_NOTICES.md" $outDir

    # --- 4. ZIP に圧縮 ---
    Write-Host "ZIP に圧縮中..." -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zipPath)

    # --- 5. SHA256 ハッシュを生成 ---
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path -Leaf $zipPath)" | Out-File $hashPath -Encoding ascii

    Write-Host ""
    Write-Host "完了: $zipPath" -ForegroundColor Green
    Write-Host "SHA256: $hash"
}
finally {
    Pop-Location
}

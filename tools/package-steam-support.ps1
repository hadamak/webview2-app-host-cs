$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    WebView2AppHost の Steam サポート ZIP を作成する。

.DESCRIPTION
    以前は C++ DLL（steam_bridge.dll）をビルドしていたが、
    Facepunch.Steamworks ベースへのリファクタリングにより、
    NuGet で取得した DLL と steam_api64.dll を ZIP に固めるだけになった。

    必要なもの:
      - ビルド済みの WebView2AppHost（Release x64）
      - Steamworks SDK（steam_api64.dll の取得元）
      - dotnet CLI（NuGet リストア用）

    使い方:
      .\tools\package-steam-support.ps1
#>

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # --- 1. C# プロジェクトをビルド（Facepunch.Steamworks を NuGet リストア含む） ---
    Write-Host "ビルド中..." -ForegroundColor Cyan
    dotnet build src\WebView2AppHost.csproj `
        -c Release -r win-x64 --no-self-contained `
        /p:Platform=x64 `
        -o "src\bin\x64\Release\net472"

    $base   = "src\bin\x64\Release\net472"
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

    # Facepunch.Steamworks DLL（Submodule ビルド出力から取得）
    $facepunchDll = Join-Path $base "Facepunch.Steamworks.Win64.dll"
    if (-not (Test-Path $facepunchDll)) {
        throw "Facepunch.Steamworks.Win64.dll が見つかりません: $facepunchDll`nビルドが成功していることを確認してください。"
    }
    Copy-Item $facepunchDll $outDir

    # Steamworks SDK の steam_api64.dll（ビルド出力から取得）
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

$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    WebView2AppHost の Core (本体) ZIP を作成する。

.DESCRIPTION
    ビルド済みの WebView2AppHost.exe と必須ライブラリを ZIP に固めるスクリプト。
    サードパーティ製のプラグイン用ライブラリ (Newtonsoft.Json 等) は含めず、
    最小限の構成で配布するために使用する。

    使い方:
      .\tools\package-core.ps1
#>

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # --- 1. ホストプロジェクトをビルド ---
    Write-Host "Core ホストをビルド中..." -ForegroundColor Cyan
    dotnet build src\WebView2AppHost.csproj `
        -c Release -r win-x64 --no-self-contained `
        /p:Platform=x64

    $base   = "src\bin\x64\Release\net472\win-x64"
    $outDir = "dist\_build\WebView2AppHost-Core-win-x64"
    $zipPath = "dist\WebView2AppHost-Core-win-x64.zip"
    $hashPath = "$zipPath.sha256"

    # --- 2. 出力ディレクトリを初期化 ---
    if (Test-Path $outDir)  { Remove-Item $outDir  -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath){ Remove-Item $hashPath -Force }

    New-Item -ItemType Directory -Force -Path $outDir   | Out-Null
    New-Item -ItemType Directory -Force -Path "dist"    | Out-Null

    # --- 3. ファイルをコピー ---
    Write-Host "ファイルをコピー中..." -ForegroundColor Cyan

    # 実行ファイルと構成
    Copy-Item "$base\WebView2AppHost.exe" $outDir
    Copy-Item "$base\WebView2AppHost.exe.config" $outDir
    
    # WebView2 必須 DLL
    Copy-Item "$base\Microsoft.Web.WebView2.Core.dll" $outDir
    Copy-Item "$base\Microsoft.Web.WebView2.WinForms.dll" $outDir
    Copy-Item "$base\WebView2Loader.dll" $outDir

    # Web コンテンツ (デフォルト)
    Copy-Item "web-content" "$outDir\www" -Recurse

    # ドキュメント
    Copy-Item "README.md" $outDir
    Copy-Item "README.ja.md" $outDir
    Copy-Item "LICENSE" $outDir
    Copy-Item "THIRD_PARTY_NOTICES.md" $outDir
    Copy-Item "docs\README_TEMPLATE.txt" $outDir
    Copy-Item "docs\README_TEMPLATE.en.txt" $outDir

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

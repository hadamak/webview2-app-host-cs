$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    WebView2AppHost のリリース用 ZIP を作成する。

.DESCRIPTION
    ビルド済みの WebView2AppHost.exe と必須ライブラリを 1 つの ZIP に固めるスクリプト。
    すべてのコネクター機能（DLL / Sidecar）は本体に内包されています。

    使い方:
      .\tools\package-release.ps1
#>

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # --- 1. ビルド ---
    Write-Host "ビルド中..." -ForegroundColor Cyan
    msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64 /v:minimal

    $baseHost = "src\bin\x64\Release\net48"
    $outDir   = "dist\_build\WebView2AppHost-win-x64"
    $zipPath  = "dist\WebView2AppHost-win-x64.zip"
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
    Copy-Item "$baseHost\WebView2AppHost.exe" $outDir
    Copy-Item "$baseHost\WebView2AppHost.exe.config" $outDir
    
    # WebView2 必須 DLL
    Copy-Item "$baseHost\Microsoft.Web.WebView2.Core.dll" $outDir
    Copy-Item "$baseHost\Microsoft.Web.WebView2.WinForms.dll" $outDir
    Copy-Item "$baseHost\WebView2Loader.dll" $outDir

    # Web コンテンツ (デフォルト)
    Copy-Item "web-content" "$outDir\www" -Recurse

    # ドキュメント
    Copy-Item "README.md" $outDir
    Copy-Item "README.ja.md" $outDir
    Copy-Item "LICENSE" $outDir
    Copy-Item "THIRD_PARTY_NOTICES.md" $outDir
    
    $docOut = New-Item -ItemType Directory -Force -Path (Join-Path $outDir "docs")
    Copy-Item "docs\*" $docOut -Recurse -Exclude "maintainer"

    # サンプル
    Copy-Item "samples" $outDir -Recurse

    # --- 4. ZIP に圧縮 ---
    Write-Host "ZIP に圧縮中..." -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zipPath)

    # --- 5. SHA256 ハッシュを生成 ---
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $fileStream = [System.IO.File]::OpenRead($zipPath)
    $hashBytes = $sha256.ComputeHash($fileStream)
    $fileStream.Close()
    $hash = ([System.BitConverter]::ToString($hashBytes) -replace "-").ToLower()
    "$hash  $(Split-Path -Leaf $zipPath)" | Out-File $hashPath -Encoding ascii

    Write-Host ""
    Write-Host "完了: $zipPath" -ForegroundColor Green
    Write-Host "SHA256: $hash"
}
finally {
    Pop-Location
}

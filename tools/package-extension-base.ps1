$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    WebView2AppHost のプラグイン共有基盤 (Newtonsoft.Json) ZIP を作成する。

.DESCRIPTION
    プラグイン DLL が共通で使用する Newtonsoft.Json.dll を ZIP に固めるスクリプト。
    各プラグイン ZIP の軽量化と、バージョンの統一を図るために分離配布される。

    使い方:
      .\tools\package-extension-base.ps1
#>

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # --- 1. プラグインプロジェクトをビルドしてライブラリを取得 ---
    Write-Host "ビルド中..." -ForegroundColor Cyan
    # Node プロジェクトをビルドすれば Newtonsoft.Json.dll も出力される
    dotnet build src-node\WebView2AppHost.Node.csproj `
        -c Release -r win-x64 --no-self-contained `
        /p:Platform=x64

    $base   = "src-node\bin\x64\Release\net472\win-x64"
    $outDir = "extension-base\_build\WebView2AppHost-ExtensionBase-win-x64"
    $zipPath = "extension-base\WebView2AppHost-ExtensionBase-win-x64.zip"
    $hashPath = "$zipPath.sha256"

    # --- 2. 出力ディレクトリを初期化 ---
    if (Test-Path $outDir)  { Remove-Item $outDir  -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath){ Remove-Item $hashPath -Force }

    New-Item -ItemType Directory -Force -Path $outDir   | Out-Null
    New-Item -ItemType Directory -Force -Path "extension-base" | Out-Null

    # --- 3. ファイルをコピー ---
    Write-Host "ファイルをコピー中..." -ForegroundColor Cyan

    # Newtonsoft.Json.dll (必須)
    $jsonDll = Join-Path $base "Newtonsoft.Json.dll"
    if (-not (Test-Path $jsonDll)) {
        throw "Newtonsoft.Json.dll が見つかりません: $jsonDll"
    }
    Copy-Item $jsonDll $outDir

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

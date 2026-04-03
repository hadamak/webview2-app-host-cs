$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    WebView2AppHost の Node.js サポート ZIP を作成する。

.DESCRIPTION
    WebView2AppHost.Node.dll プロジェクトをビルドし、
    Node.js 連携に必要なファイルを ZIP に固めるスクリプト。

    ※ 配布サイズ削減のため、Node.js ランタイム本体 (node.exe) は含みません。
    利用者は別途開発環境や公式サイトから node.exe を取得し、
    node-runtime/ フォルダへ配置する必要があります。

    使い方:
      .\tools\package-node-support.ps1
#>

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    # --- 1. Node.js DLL プロジェクトをビルド ---
    Write-Host "Node.js DLL をビルド中..." -ForegroundColor Cyan
    dotnet build src-node\WebView2AppHost.Node.csproj `
        -c Release -r win-x64 --no-self-contained `
        /p:Platform=x64

    $base   = "src-node\bin\x64\Release\net48\win-x64"
    $outDir = "node-support\_build\WebView2AppHost-NodeSupport-win-x64"
    $zipPath = "node-support\WebView2AppHost-NodeSupport-win-x64.zip"
    $hashPath = "$zipPath.sha256"

    # --- 2. 出力ディレクトリを初期化 ---
    if (Test-Path $outDir)  { Remove-Item $outDir  -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath){ Remove-Item $hashPath -Force }

    New-Item -ItemType Directory -Force -Path $outDir   | Out-Null
    New-Item -ItemType Directory -Force -Path "node-support" | Out-Null

    # --- 3. ファイルをコピー ---
    Write-Host "ファイルをコピー中..." -ForegroundColor Cyan

    # WebView2AppHost.Node.dll
    $nodeDll = Join-Path $base "WebView2AppHost.Node.dll"
    if (-not (Test-Path $nodeDll)) {
        throw "WebView2AppHost.Node.dll が見つかりません: $nodeDll"
    }
    Copy-Item $nodeDll $outDir

    # node-runtime フォルダ (server.js のみ、node.exe は除外)
    $runtimeOut = New-Item -ItemType Directory -Force -Path (Join-Path $outDir "node-runtime")
    Copy-Item "node-runtime\server.js"   $runtimeOut
    Copy-Item "node-runtime\package.json" $runtimeOut  # バージョン管理用

    # ドキュメント
    Copy-Item "docs\README_NODE.txt"    "$outDir\README.txt"
    Copy-Item "docs\README_NODE.en.txt" "$outDir\README.en.txt"

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

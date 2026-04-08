<#
.SYNOPSIS
    ローカル開発環境をビルド・セットアップする。

.DESCRIPTION
    以下をまとめて実行する:
      1. ホスト本体 (WebView2AppHost.csproj) をビルド
      2. Facepunch.Steamworks DLL を EXE と同じフォルダにコピー
      3. Nodeサイドカーを出力先にコピー

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

# ---------------------------------------------------------------------------
# 環境設定
# ---------------------------------------------------------------------------
$ErrorActionPreference = "Stop"

# UTF-8 で出力を扱うように設定 (Mojibake 対策)
if ($PSVersionTable.PSVersion.Major -ge 6) {
    # pwsh 向け
    $OutputEncoding = [System.Text.Encoding]::UTF8
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    # ---------------------------------------------------------------------------
    # 0. ツールチェック
    # ---------------------------------------------------------------------------
    if (!(Get-Command msbuild -ErrorAction SilentlyContinue)) {
        Write-Error "msbuild が見つかりません。Visual Studio の開発者用コマンドプロンプトを使用するか、パスを確認してください。"
        exit 1
    }

    # ---------------------------------------------------------------------------
    # 1. ホスト本体ビルド
    # ---------------------------------------------------------------------------
    Write-Host "==> ホスト本体をビルド中... ($Configuration/x64)" -ForegroundColor Cyan
    
    # MSBuild 実行
    # NOTE: プロジェクト内の PreBuildEvent で web-content -> app.zip の圧縮が行われる
    msbuild src\WebView2AppHost.csproj `
        "/t:Restore;Build" `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /v:minimal `
        /clp:ForceConsoleColor

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "ERROR: ホスト本体のビルドに失敗しました。上記のコンパイルエラーを確認してください。" -ForegroundColor Red
        exit 1
    }

    $outDir = "src\bin\x64\$Configuration\net48"
    if (!(Test-Path $outDir)) {
        Write-Error "出力ディレクトリが見つかりません: $outDir"
        exit 1
    }

    # ---------------------------------------------------------------------------
    # 2. Facepunch.Steamworks DLL をコピー
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
    # 3. Pythonサイドカーをコピー
    # ---------------------------------------------------------------------------
    Write-Host "==> Pythonサイドカーを配置中..." -ForegroundColor Cyan

    $pythonRuntimeDest = Join-Path $outDir "python-runtime"
    if (!(Test-Path $pythonRuntimeDest)) {
        New-Item -ItemType Directory -Force -Path $pythonRuntimeDest | Out-Null
    }

    if (Test-Path "samples\sidecar\python-runtime\server.py") {
        Copy-Item "samples\sidecar\python-runtime\server.py" $pythonRuntimeDest -Force
        Write-Host "    samples\sidecar\python-runtime\server.py -> $pythonRuntimeDest" -ForegroundColor Gray
    }
    else {
        Write-Warning "Pythonサイドカーが見つかりません: samples\sidecar\python-runtime\server.py"
    }

    # ---------------------------------------------------------------------------
    # 完了メッセージ
    # ---------------------------------------------------------------------------
    $exePath = Join-Path $outDir "WebView2AppHost.exe"
    Write-Host ""
    Write-Host "セットアップ完了" -ForegroundColor Green
    Write-Host "EXE: $exePath" -ForegroundColor White
}
catch {
    Write-Host ""
    Write-Host "セットアップ中に予期しないエラーが発生しました:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}

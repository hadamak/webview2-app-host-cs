$ErrorActionPreference = "Stop"

$repoRoot = (Split-Path -Parent $PSScriptRoot).Trim()
$testDir = Join-Path $repoRoot "tests\IntegrationTests\temp"
if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force }
New-Item -ItemType Directory -Path $testDir | Out-Null

Write-Host "==> 1. Build Test Runner" -ForegroundColor Cyan
msbuild "$repoRoot\tests\IntegrationTests\LoadingOrderTests\LoadingOrderTests.csproj" "/t:Restore;Build" /p:Configuration=Debug /p:Platform=x64 /v:minimal

$runner = Join-Path $repoRoot "tests\IntegrationTests\LoadingOrderTests\bin\x64\Debug\net48\LoadingOrderTests.exe"
$baseExe = Join-Path $repoRoot "src\bin\x64\Debug\net48\WebView2AppHost.exe"

if (!(Test-Path $runner)) { throw "Runner not found at $runner" }
if (!(Test-Path $baseExe)) { throw "Base EXE not found at $baseExe" }

function Create-TestZip($path, $files) {
    $tempZipDir = Join-Path $testDir "zip_build_$( [Guid]::NewGuid().ToString('N') )"
    New-Item -ItemType Directory -Path $tempZipDir | Out-Null
    $files.GetEnumerator() | ForEach-Object {
        $content = $_.Value
        Set-Content -Path (Join-Path $tempZipDir $_.Key) -Value $content -Encoding UTF8
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempZipDir, $path)
}

# ---------------------------------------------------------------------------
Write-Host "`n==> 2. Test Case: Bundled ZIP vs Loose Folder (Security Protection)" -ForegroundColor Cyan

$bundledZip = Join-Path $testDir "bundled.zip"
Create-TestZip $bundledZip @{ "app.conf.json" = '{"marker": "BUNDLED_MARKER"}' }

$bundledExe = Join-Path $testDir "bundled_host.exe"
$exeBytes = [System.IO.File]::ReadAllBytes($baseExe)
$zipBytes = [System.IO.File]::ReadAllBytes($bundledZip)
[System.IO.File]::WriteAllBytes($bundledExe, ($exeBytes + $zipBytes))

$wwwDir = Join-Path $testDir "www"
New-Item -ItemType Directory -Path $wwwDir | Out-Null
Set-Content -Path (Join-Path $wwwDir "app.conf.json") -Value '{"marker": "LOOSE_MARKER"}' -Encoding UTF8
Set-Content -Path (Join-Path $wwwDir "media.txt") -Value 'MEDIA_MARKER' -Encoding UTF8

# Runner will use the fake bundled EXE path for ZipContentProvider
& $runner "bundled_vs_loose" $bundledExe

# ---------------------------------------------------------------------------
Write-Host "`n==> 3. Test Case: Command-line Arg ZIP vs Loose Folder (Override)" -ForegroundColor Cyan

$argZip = Join-Path $testDir "arg.zip"
Create-TestZip $argZip @{ "app.conf.json" = '{"marker": "ARG_MARKER"}' }

$plainExe = Join-Path $testDir "plain_host.exe"
Copy-Item $baseExe $plainExe

# Run runner with arg ZIP
& $runner "arg_vs_loose" $plainExe $argZip

Write-Host "`n==> Integration Tests Passed Successfully" -ForegroundColor Green

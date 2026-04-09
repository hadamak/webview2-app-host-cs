<#
 .SYNOPSIS
  server.ps1 — WebView2AppHost PowerShell サイドカー
  JSON-RPC 2.0 (NDJSON) 経由でホストと通信し、ファイル操作やコマンド実行を提供します。
#>

$OutputEncoding = [System.Text.Encoding]::UTF8

# ---------------------------------------------------------------------------
# ヘルパー関数
# ---------------------------------------------------------------------------

function Send-Json ($obj) {
    $json = $obj | ConvertTo-Json -Compress -Depth 10
    [Console]::Out.WriteLine($json)
}

function Resolve-Request ($id, $result) {
    Send-Json @{ jsonrpc = "2.0"; id = $id; result = $result }
}

function Reject-Request ($id, $message) {
    Send-Json @{ jsonrpc = "2.0"; id = $id; error = @{ code = -32000; message = [string]$message } }
}

# ---------------------------------------------------------------------------
# ハンドラ実装 (FileSystem, Terminal, PowerShell)
# ---------------------------------------------------------------------------

$Handlers = @{
    "PowerShell" = @{
        "version" = { $PSVersionTable.PSVersion.ToString() }
        "cwd"     = { Get-Location | Select-Object -ExpandProperty Path }
    }
    
    "FileSystem" = @{
        "listFiles" = {
            param($path = ".")
            Get-ChildItem -Path $path | ForEach-Object {
                @{ name = $_.Name; isDirectory = $_.PSIsContainer }
            }
        }
        "readFile" = {
            param($path)
            if (-not (Test-Path $path)) { throw "File not found: $path" }
            Get-Content -Path $path -Raw -Encoding UTF8
        }
        "writeFile" = {
            param($path, $content)
            $dir = Split-Path -Path $path -Parent
            if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Set-Content -Path $path -Value $content -Encoding UTF8 -Force
            return $true
        }
    }

    "Terminal" = @{
        "execute" = {
            param($command)
            try {
                $output = Invoke-Expression $command 2>&1 | Out-String
                @{ stdout = $output; stderr = ""; code = 0; ok = $true }
            } catch {
                @{ stdout = ""; stderr = $_.Exception.Message; code = 1; ok = $false }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# メインループ (stdin 監視)
# ---------------------------------------------------------------------------

[Console]::Error.WriteLine("[server.ps1] PowerShell サイドカー起動 (PID: $pid)")

# 起動完了を通知
Send-Json @{ ready = $true }

while ($null -ne ($line = [Console]::In.ReadLine())) {
    if (-not $line.Trim()) { continue }
    
    try {
        $msg = $line | ConvertFrom-Json
        if ($msg.jsonrpc -ne "2.0" -or -not $msg.method) {
            Reject-Request $msg.id "Invalid JSON-RPC 2.0 request"
            continue
        }

        # メソッド解釈: "Sidecar.ClassName.MethodName"
        $parts = $msg.method.Split('.')
        if ($parts.Count -lt 3) {
            Reject-Request $msg.id "Invalid method format: $($msg.method)"
            continue
        }

        $className = $parts[1]
        $methodName = $parts[2]
        $params = $msg.params

        if (-not $Handlers.ContainsKey($className) -or -not $Handlers[$className].ContainsKey($methodName)) {
            Reject-Request $msg.id "Method not found: $className.$methodName"
            continue
        }

        # 実行
        $func = $Handlers[$className][$methodName]
        $result = $null

        if ($params -is [PSCustomObject]) {
            $result = &$func @params
        } elseif ($params -is [array]) {
            $result = &$func @params
        } else {
            $result = &$func
        }

        Resolve-Request $msg.id $result

    } catch {
        [Console]::Error.WriteLine("[server.ps1] Error: $_")
        if ($msg.id) { Reject-Request $msg.id $_.Exception.Message }
    }
}
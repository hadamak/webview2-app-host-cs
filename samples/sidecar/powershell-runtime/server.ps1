# server.ps1 — WebView2AppHost PowerShell サイドカーサンプル
#
# 【概要】
# 標準入出力を介して JSON-RPC 2.0 形式で通信します。
# 外部ランタイム不要で、Windows の標準機能のみで動作します。

# 出力エンコーディングを UTF-8 に固定（重要）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Send-Json ($obj) {
    $json = $obj | ConvertTo-Json -Compress -Depth 10
    Write-Host $json
}

# 起動完了通知
Send-Json @{ ready = $true }

# 標準入力ループ
while ($null -ne ($line = [Console]::In.ReadLine())) {
    $line = $line.Trim()
    if (-not $line) { continue }

    try {
        $msg = $line | ConvertFrom-Json
        $id = $msg.id
        
        # メソッドの振り分け
        $methodParts = $msg.method.Split('.')
        $className = $methodParts[1]
        $methodName = $methodParts[2]
        $args = $msg.params

        $result = $null

        if ($className -eq "PowerShell") {
            switch ($methodName) {
                "version" { $result = $PSVersionTable.PSVersion.ToString() }
                "getServices" { $result = Get-Service | Select-Object Name, Status, DisplayName | Select-Object -First 10 }
                "getUptime" { $result = (Get-Uptime).ToString() }
                "execute" { 
                    # 任意のコマンド実行（注意：信頼できるコンテンツからのみ呼び出すこと）
                    $result = Invoke-Expression $args[0] | Out-String
                }
            }
        }

        # 成功レスポンス
        Send-Json @{ jsonrpc = "2.0"; id = $id; result = $result }
    }
    catch {
        # エラーレスポンス
        Send-Json @{ jsonrpc = "2.0"; id = $id; error = @{ code = -32000; message = $_.Exception.Message } }
    }
}

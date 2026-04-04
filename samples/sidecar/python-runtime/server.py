# server.py — WebView2AppHost Python サイドカーサンプル
#
# 【概要】
# 標準入出力 (StdIO) を介して JavaScript と JSON-RPC 2.0 形式で通信します。
# 
# 【通信プロトコル (NDJSON)】
#   - 受信 (stdin): {"jsonrpc":"2.0", "id":1, "method":"Python.ClassName.MethodName", "params":[...]}
#   - 送信 (stdout): {"jsonrpc":"2.0", "id":1, "result":...}

import sys
import json
import os
import platform

# ---------------------------------------------------------------------------
# ハンドラレジストリ
# ---------------------------------------------------------------------------

_handlers = {}

def register(class_name, methods):
    """クラス名とメソッドのマップを登録します。"""
    _handlers[class_name] = methods

# ---------------------------------------------------------------------------
# 送受信
# ---------------------------------------------------------------------------

def send(obj):
    sys.stdout.write(json.dumps(obj) + '\n')
    sys.stdout.flush()

def resolve(request_id, result):
    send({"jsonrpc": "2.0", "id": request_id, "result": result})

def reject(request_id, message):
    send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32000, "message": str(message)}})

# ---------------------------------------------------------------------------
# メッセージディスパッチ
# ---------------------------------------------------------------------------

def dispatch(msg):
    if msg.get("jsonrpc") != "2.0" or "method" not in msg:
        reject(msg.get("id", 0), "Invalid JSON-RPC 2.0 request")
        return

    method_parts = msg["method"].split('.')
    if len(method_parts) < 3:
        reject(msg["id"], f"Invalid method format: {msg['method']}")
        return

    class_name = method_parts[1]
    method_name = method_parts[2]
    args = msg.get("params", [])
    request_id = msg.get("id")

    try:
        class_handlers = _handlers.get(class_name)
        if not class_handlers:
            reject(request_id, f"PythonPlugin: クラス '{class_name}' が登録されていません")
            return

        method = class_handlers.get(method_name)
        if not method or not callable(method):
            reject(request_id, f"PythonPlugin: {class_name}.{method_name} が見つかりません")
            return

        # メソッドの実行
        if isinstance(args, list):
            result = method(*args)
        elif isinstance(args, dict):
            result = method(**args)
        else:
            result = method()

        resolve(request_id, result)
    except Exception as e:
        sys.stderr.write(f"[server.py] Error in {msg['method']}: {e}\n")
        reject(request_id, str(e))

# ---------------------------------------------------------------------------
# ハンドラ登録
# ---------------------------------------------------------------------------

# 1. 基本情報
register('Python', {
    "version": lambda: sys.version,
    "platform": lambda: platform.platform(),
    "cwd": lambda: os.getcwd()
})

# 2. 計算の例
def add(a, b): return a + b
register('Math', {
    "add": add
})

# ---------------------------------------------------------------------------
# メインループ (stdin NDJSON リーダー)
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    sys.stderr.write(f"[server.py] Python サイドカー起動 (PID: {os.getpid()})\n")
    sys.stderr.flush()

    # ホストに起動完了（Ready）を通知
    send({"ready": True})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
            dispatch(msg)
        except json.JSONDecodeError:
            sys.stderr.write(f"[server.py] JSON parse error: {line}\n")
            sys.stderr.flush()

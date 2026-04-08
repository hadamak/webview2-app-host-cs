"""
server.py — WebView2AppHost Python サイドカー
JSON-RPC 2.0 (NDJSON) を介してファイル操作・コマンド実行を提供します。
"""

import sys
import os
import json
import subprocess
import traceback
from pathlib import Path

# UTF-8 での入出力を強制
if hasattr(sys.stdin, 'reconfigure'):
    sys.stdin.reconfigure(encoding='utf-8')
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

ROOT_DIR = Path(os.getcwd()).resolve()

# ---------------------------------------------------------------------------
# セキュリティ: パスバリデーション
# ---------------------------------------------------------------------------

def secure_path(target_path):
    """
    指定されたパスがサンドボックス（ROOT_DIR）内にあることを保証する。
    """
    resolved = (ROOT_DIR / target_path).resolve()
    if not str(resolved).startswith(str(ROOT_DIR)):
        raise PermissionError(f"Access Denied: {target_path} is outside the sandbox.")
    return resolved

# ---------------------------------------------------------------------------
# ハンドラ実装
# ---------------------------------------------------------------------------

class Handlers:
    @staticmethod
    def python_version():
        return sys.version

    @staticmethod
    def python_cwd():
        return str(ROOT_DIR)

    @staticmethod
    def filesystem_listFiles(dirPath="."):
        path = secure_path(dirPath)
        items = []
        for entry in path.iterdir():
            items.append({
                "name": entry.name,
                "isDirectory": entry.is_dir()
            })
        return items

    @staticmethod
    def filesystem_readFile(filePath):
        path = secure_path(filePath)
        return path.read_text(encoding='utf-8')

    @staticmethod
    def filesystem_writeFile(filePath, content):
        path = secure_path(filePath)
        # 親ディレクトリがなければ作成
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8')
        return True

    @staticmethod
    def terminal_execute(command):
        # Windows では chcp 65001 を付けて実行
        full_command = f"chcp 65001 > nul && {command}"
        try:
            result = subprocess.run(
                full_command, 
                shell=True, 
                capture_output=True, 
                text=True, 
                encoding='utf-8', 
                timeout=30
            )
            return {
                "stdout": result.stdout,
                "stderr": result.stderr,
                "code": result.returncode,
                "ok": result.returncode == 0
            }
        except Exception as e:
            return {
                "stdout": "",
                "stderr": str(e),
                "code": 1,
                "ok": False
            }

# ---------------------------------------------------------------------------
# 通信・ディスパッチ
# ---------------------------------------------------------------------------

def send(obj):
    sys.stdout.write(json.dumps(obj) + '\n')
    sys.stdout.flush()

def resolve(request_id, result):
    send({"jsonrpc": "2.0", "id": request_id, "result": result})

def reject(request_id, message):
    send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32000, "message": str(message)}})

def dispatch(msg):
    if msg.get("jsonrpc") != "2.0" or "method" not in msg:
        reject(msg.get("id"), "Invalid JSON-RPC 2.0 request")
        return

    method_name = msg["method"]
    # "Node.FileSystem.readFile" 形式（サイドカー互換）を
    # "filesystem_readFile" 形式に変換して Handlers から探す
    parts = method_name.split('.')
    if len(parts) < 3:
        reject(msg.get("id"), f"Invalid method format: {method_name}")
        return

    internal_method = f"{parts[1].lower()}_{parts[2]}"
    handler = getattr(Handlers, internal_method, None)

    if not handler:
        reject(msg.get("id"), f"Method not found: {method_name}")
        return

    params = msg.get("params", [])
    try:
        if isinstance(params, dict):
            result = handler(**params)
        else:
            # 配列の場合はアンパックして渡す
            result = handler(*params)
        resolve(msg.get("id"), result)
    except Exception as e:
        sys.stderr.write(f"[server.py] Error: {traceback.format_exc()}\n")
        reject(msg.get("id"), str(e))

# ---------------------------------------------------------------------------
# メメインループ
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    sys.stderr.write(f"[server.py] Python サイドカー起動 (PID: {os.getpid()})\n")
    
    # 起動完了を通知
    send({"ready": True})

    for line in sys.stdin:
        trimmed = line.strip()
        if not trimmed:
            continue
        try:
            dispatch(json.loads(trimmed))
        except json.JSONDecodeError:
            sys.stderr.write(f"[server.py] JSON Parse Error: {trimmed}\n")

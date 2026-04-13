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

def truncate_text(text, max_len):
    """テキストが長すぎる場合に末尾を切り詰めて警告を付加する"""
    if len(text) > max_len:
        return text[:max_len] + f"\n... [System: Truncated. Original length was {len(text)} characters.]"
    return text

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
                "isDirectory": entry.is_dir(),
                "size": entry.stat().st_size if entry.is_file() else 0
            })
        return items

    @staticmethod
    def filesystem_readFile(filePath):
        path = secure_path(filePath)
        content = path.read_text(encoding='utf-8', errors='replace')
        return truncate_text(content, 50000)

    @staticmethod
    def filesystem_writeFile(filePath, content):
        path = secure_path(filePath)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8')
        return {"status": "success", "file": str(path.relative_to(ROOT_DIR))}

    @staticmethod
    def filesystem_replaceInFile(filePath, old_text, new_text):
        """ファイル内の特定の文字列を置換する（差分修正用）"""
        path = secure_path(filePath)
        if not path.exists():
            return {"status": "error", "message": "File not found."}
        
        content = path.read_text(encoding='utf-8', errors='replace')
        if old_text not in content:
            return {"status": "error", "message": "old_text not found in the file. Exact match required."}
            
        new_content = content.replace(old_text, new_text)
        path.write_text(new_content, encoding='utf-8')
        return {"status": "success", "message": "Text replaced successfully."}

    @staticmethod
    def filesystem_readFileLines(filePath, start_line=1, end_line=100):
        """ファイルの指定した行範囲だけを返す（1-based index）"""
        path = secure_path(filePath)
        if not path.exists():
            return {"status": "error", "message": "File not found."}
            
        with path.open('r', encoding='utf-8', errors='replace') as f:
            lines = f.readlines()
            
        start_idx = max(0, start_line - 1)
        end_idx = min(len(lines), end_line)
        
        extracted = "".join(lines[start_idx:end_idx])
        return truncate_text(extracted, 50000)

    @staticmethod
    def filesystem_listDirectoryTree(dirPath=".", max_depth=2):
        """ディレクトリ構造をテキストのツリー形式で返す"""
        base_path = secure_path(dirPath)
        if not base_path.exists() or not base_path.is_dir():
            return "Directory not found."
            
        def build_tree(current_path, current_depth):
            if current_depth > max_depth:
                return []
            tree = []
            try:
                # フォルダを先に、ファイルを後にソート
                entries = sorted(current_path.iterdir(), key=lambda x: (x.is_file(), x.name))
                for entry in entries:
                    prefix = "  " * current_depth + ("- " if entry.is_file() else "+ ")
                    tree.append(f"{prefix}{entry.name}")
                    if entry.is_dir():
                        tree.extend(build_tree(entry, current_depth + 1))
            except PermissionError:
                tree.append("  " * current_depth + "[Permission Denied]")
            return tree
            
        tree_lines = [f"[{base_path.name}/]"] + build_tree(base_path, 1)
        return truncate_text("\n".join(tree_lines), 20000)

    @staticmethod
    def terminal_execute(command):
        try:
            result = subprocess.run(
                command, 
                shell=True, 
                capture_output=True, 
                text=False, 
                timeout=30
            )

            def decode_best_effort(b):
                if not b:
                    return ""
                try:
                    return b.decode('utf-8')
                except UnicodeDecodeError:
                    if os.name == 'nt':
                        return b.decode('cp932', errors='replace')
                    return b.decode('utf-8', errors='replace')

            stdout_text = truncate_text(decode_best_effort(result.stdout), 10000)
            stderr_text = truncate_text(decode_best_effort(result.stderr), 10000)

            return {
                "stdout": stdout_text,
                "stderr": stderr_text,
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

    @staticmethod
    def agent_askHuman(question=""):
        return {
            "status": "waiting_for_human",
            "question": question,
            "message": "Execution paused. Waiting for user input."
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
            result = handler(*params)
        resolve(msg.get("id"), result)
    except Exception as e:
        sys.stderr.write(f"[server.py] Error: {traceback.format_exc()}\n")
        reject(msg.get("id"), str(e))

# ---------------------------------------------------------------------------
# メインループ
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    sys.stderr.write(f"[server.py] Python サイドカー起動 (PID: {os.getpid()})\n")
    
    send({"ready": True})

    for line in sys.stdin:
        trimmed = line.strip()
        if not trimmed:
            continue
        try:
            dispatch(json.loads(trimmed))
        except json.JSONDecodeError:
            sys.stderr.write(f"[server.py] JSON Parse Error: {trimmed}\n")

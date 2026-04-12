// File: tools/observer_py.js

(function() {
    // ツール定義（追加ツール反映）
    const TOOLS_DEFINITION = [
        { "name": "execute_terminal_command", "description": "コマンドを実行します。", "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] } },
        { "name": "list_files", "description": "ファイル一覧を取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" } } } },
        { "name": "read_file", "description": "ファイルを読み込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
        { "name": "write_file", "description": "ファイルを書き込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "content": { "type": "string" } }, "required": ["filePath", "content"] } },
        { "name": "replace_in_file", "description": "ファイル内の一部のテキストを置換します。ファイル全体を書き換えないため安全で高速です。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "old_text": { "type": "string", "description": "置換対象の元の文字列（完全一致）" }, "new_text": { "type": "string", "description": "新しい文字列" } }, "required": ["filePath", "old_text", "new_text"] } },
        { "name": "read_file_lines", "description": "ファイルの指定した行範囲だけを読み込みます。長すぎるファイルに有効です。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "start_line": { "type": "number", "description": "開始行番号 (1-based)" }, "end_line": { "type": "number", "description": "終了行番号" } }, "required": ["filePath", "start_line", "end_line"] } },
        { "name": "list_directory_tree", "description": "ディレクトリ構造をツリー形式のテキストで取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" }, "max_depth": { "type": "number", "description": "探索する深さ(デフォルト2)" } } } },
        { "name": "ask_human", "description": "自動実行を一時停止し、人間に質問や判断を求めます。", "parameters": { "type": "object", "properties": { "question": { "type": "string" } }, "required": ["question"] } }
    ];

    console.log("🚀 AI Agent Bridge: ツール監視を開始しました。(拡張ファイル操作対応版)");

    // --- UI: フローティングステータス表示 ---
    const statusDiv = document.createElement("div");
    Object.assign(statusDiv.style, {
        position: "fixed",
        bottom: "20px",
        right: "20px",
        padding: "12px 18px",
        background: "rgba(10, 20, 35, 0.9)",
        color: "#53d1b6",
        border: "1px solid rgba(83, 209, 182, 0.4)",
        borderRadius: "8px",
        fontFamily: "monospace",
        fontSize: "12px",
        zIndex: "99999",
        boxShadow: "0 8px 24px rgba(0,0,0,0.3)",
        transition: "opacity 0.3s",
        pointerEvents: "none"
    });
    statusDiv.textContent = "🤖 Agent Ready";
    document.body.appendChild(statusDiv);

    function updateStatus(message, color = "#53d1b6") {
        statusDiv.style.color = color;
        statusDiv.textContent = message;
    }

    // --- ユーティリティ ---
    const taskQueue = [];
    let isProcessingQueue = false;

    async function processQueue() {
        if (isProcessingQueue) return;
        isProcessingQueue = true;
        while (taskQueue.length > 0) {
            const task = taskQueue.shift();
            try {
                await task();
            } catch (e) {
                console.error("❌ [AI Agent] タスクエラー:", e);
                updateStatus("⚠️ Error in task queue", "#ff7f7f");
            }
            await new Promise(r => setTimeout(r, 1200));
        }
        isProcessingQueue = false;
        updateStatus("🤖 Agent Idle (Waiting for commands)", "#93a6c7");
    }

    async function waitForElement(parent, selector, timeoutMs = 10000) {
        const el = parent.querySelector(selector);
        if (el) return el;
        return new Promise((resolve) => {
            const obs = new MutationObserver(() => {
                const found = parent.querySelector(selector);
                if (found) {
                    obs.disconnect();
                    resolve(found);
                }
            });
            obs.observe(parent, { childList: true, subtree: true });
            setTimeout(() => {
                obs.disconnect();
                resolve(null);
            }, timeoutMs);
        });
    }

    async function waitForValidJson(element, timeoutMs = 15000) {
        return new Promise((resolve, reject) => {
            const tryParse = () => {
                const text = element.innerText.trim();
                if (!text) return null;
                try {
                    return JSON.parse(text);
                } catch {
                    return null;
                }
            };
            const parsed = tryParse();
            if (parsed) return resolve(parsed);

            const obs = new MutationObserver(() => {
                const parsedNow = tryParse();
                if (parsedNow) {
                    obs.disconnect();
                    resolve(parsedNow);
                }
            });
            obs.observe(element, { childList: true, characterData: true, subtree: true });
            
            setTimeout(() => {
                obs.disconnect();
                reject(new Error("JSONストリーミング待機タイムアウト"));
            }, timeoutMs);
        });
    }

    function setNativeValue(element, value) {
        const proto = element.tagName === 'TEXTAREA' 
            ? window.HTMLTextAreaElement.prototype 
            : window.HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
        if (setter) {
            setter.call(element, value);
        } else {
            element.value = value;
        }
        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
    }

    // --- メイン処理 ---
    const methodMap = {
        'execute_terminal_command': (args) => Host.PythonRuntime.Terminal.execute(args.command),
        'list_files': (args) => Host.PythonRuntime.FileSystem.listFiles(args.dirPath || "."),
        'read_file': (args) => Host.PythonRuntime.FileSystem.readFile(args.filePath),
        'write_file': (args) => Host.PythonRuntime.FileSystem.writeFile(args.filePath, args.content),
        'replace_in_file': (args) => Host.PythonRuntime.FileSystem.replaceInFile(args.filePath, args.old_text, args.new_text),
        'read_file_lines': (args) => Host.PythonRuntime.FileSystem.readFileLines(args.filePath, args.start_line, args.end_line),
        'list_directory_tree': (args) => Host.PythonRuntime.FileSystem.listDirectoryTree(args.dirPath || ".", args.max_depth || 2),
        'ask_human': (args) => Host.PythonRuntime.Agent.askHuman(args.question)
    };

    async function handleFunctionCall(chunk) {
        if (chunk.dataset.agentProcessed) return;
        chunk.dataset.agentProcessed = "true";

        taskQueue.push(async () => {
            try {
                const titleElement = await waitForElement(chunk, '.mat-expansion-panel-header-title span:last-child', 5000);
                const codeElement = await waitForElement(chunk, 'code', 5000);
                
                if (!titleElement || !codeElement) return;

                const funcName = titleElement.innerText.trim();
                if (!methodMap[funcName]) return;

                updateStatus(`⏳ Parsing args for '${funcName}'...`, "#ffb454");
                const args = await waitForValidJson(codeElement);
                
                updateStatus(`⚙️ Executing '${funcName}'...`, "#53d1b6");
                let result = await methodMap[funcName](args);

                // エラー時の自己修正プロンプトの追加
                if (funcName === 'execute_terminal_command' && result.code !== 0) {
                    result.agent_hint = "Command failed (non-zero exit code). Please analyze the error and output, then try fixing the issue or ask_human.";
                } else if (funcName === 'replace_in_file' && result.status === 'error') {
                    result.agent_hint = "Replacement failed. Check if old_text is exactly matching the file content, or use read_file_lines to verify the exact text block.";
                }

                const resultString = typeof result === 'string' ? result : JSON.stringify(result, null, 2);

                const inputElement = await waitForElement(chunk, 'input[placeholder*="response"], textarea', 10000);
                if (!inputElement) return;

                setNativeValue(inputElement, resultString);
                await new Promise(r => setTimeout(r, 600));

                const sendBtn = chunk.querySelector('button[type="submit"]') || chunk.querySelector('button[aria-label*="Send"], button[aria-label*="Submit"]');
                
                if (funcName === 'ask_human') {
                    updateStatus(`🛑 Paused for Human Input`, "#ffb454");
                    console.log("🛑 [AI Agent] ask_human が呼ばれました。自動送信を停止してユーザーの入力を待ちます。");
                    return;
                }

                if (sendBtn && !sendBtn.disabled) {
                    sendBtn.click();
                    updateStatus(`✅ Sent '${funcName}' result`, "#53d1b6");
                } else {
                    updateStatus(`⚠️ Send button not ready`, "#ff7f7f");
                }

            } catch (e) {
                console.error("❌ [AI Agent] Pipeline error:", e);
                updateStatus(`❌ Execution failed`, "#ff7f7f");
            }
        });

        processQueue();
    }

    // --- DOM監視 ---
    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            mutation.addedNodes.forEach(node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    if (node.tagName && node.tagName.toUpperCase().includes('FUNCTION-CALL')) {
                        handleFunctionCall(node);
                    } else {
                        const calls = node.querySelectorAll('function-call, [class*="function-call"]');
                        calls.forEach(call => handleFunctionCall(call));
                    }
                }
            });
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
})();
// File: tools/observer_py.js
(function() {
    // ツール定義（追加ツール反映）
    const TOOLS_DEFINITION = [
        { "name": "execute_terminal_command", "description": "コマンドを実行します。", "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] } },
        { "name": "list_files", "description": "ファイル一覧を取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" } } } },
        { "name": "read_file", "description": "ファイルを読み込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
        { "name": "write_file", "description": "ファイルを書き込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "content": { "type": "string" } }, "required": ["filePath", "content"] } },
        { "name": "confirm_dll_execute_command", "description": "[DLL検証用] C# DLL経由でコマンドを実行します。", "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] } },
        { "name": "confirm_dll_list_files", "description": "[DLL検証用] C# DLL経由でファイル一覧を取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" } } } },
        { "name": "confirm_dll_read_file", "description": "[DLL検証用] C# DLL経由でファイルを読み込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
        { "name": "confirm_dll_write_file", "description": "[DLL検証用] C# DLL経由でファイルを書き込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "content": { "type": "string" } }, "required": ["filePath", "content"] } },
        { "name": "replace_in_file", "description": "ファイル内の一部のテキストを置換します。ファイル全体を書き換えないため安全で高速です。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "old_text": { "type": "string", "description": "置換対象の元の文字列（完全一致）" }, "new_text": { "type": "string", "description": "新しい文字列" } }, "required": ["filePath", "old_text", "new_text"] } },
        { "name": "read_file_lines", "description": "ファイルの指定した行範囲だけを読み込みます。長すぎるファイルに有効です。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "start_line": { "type": "number", "description": "開始行番号 (1-based)" }, "end_line": { "type": "number", "description": "終了行番号" } }, "required": ["filePath", "start_line", "end_line"] } },
        { "name": "list_directory_tree", "description": "ディレクトリ構造をツリー形式のテキストで取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" }, "max_depth": { "type": "number", "description": "探索する深さ(デフォルト2)" } } } },
        { "name": "ask_human", "description": "自動実行を一時停止し、人間に質問や判断を求めます。", "parameters": { "type": "object", "properties": { "question": { "type": "string" } }, "required": ["question"] } }
    ];

    console.log("🚀 AI Agent Bridge: ツール監視を開始しました。(ワークスペース対応版)");

    // --- UI: フローティングステータス表示 ---
    const uiContainer = document.createElement("div");
    Object.assign(uiContainer.style, {
        position: "fixed",
        bottom: "20px",
        right: "20px",
        padding: "15px",
        background: "rgba(10, 20, 35, 0.95)",
        color: "#53d1b6",
        border: "1px solid rgba(83, 209, 182, 0.4)",
        borderRadius: "12px",
        fontFamily: "sans-serif",
        fontSize: "12px",
        zIndex: "99999",
        boxShadow: "0 12px 32px rgba(0,0,0,0.4)",
        display: "flex",
        flexDirection: "column",
        gap: "10px",
        minWidth: "240px",
        backdropFilter: "blur(8px)"
    });

    const statusText = document.createElement("div");
    statusText.textContent = "🤖 Agent Ready";
    uiContainer.appendChild(statusText);

    const workspaceInfo = document.createElement("div");
    workspaceInfo.style.color = "#93a6c7";
    workspaceInfo.style.fontSize = "11px";
    workspaceInfo.style.wordBreak = "break-all";
    workspaceInfo.textContent = "📂 Workspace: (Initializing...)";
    uiContainer.appendChild(workspaceInfo);

    const btnChangeWorkspace = document.createElement("button");
    btnChangeWorkspace.textContent = "Change Workspace";
    Object.assign(btnChangeWorkspace.style, {
        background: "rgba(83, 209, 182, 0.15)",
        border: "1px solid #53d1b6",
        color: "#53d1b6",
        padding: "6px 10px",
        borderRadius: "6px",
        cursor: "pointer",
        fontSize: "11px",
        fontWeight: "bold",
        transition: "background 0.2s"
    });
    btnChangeWorkspace.onmouseover = () => btnChangeWorkspace.style.background = "rgba(83, 209, 182, 0.25)";
    btnChangeWorkspace.onmouseout = () => btnChangeWorkspace.style.background = "rgba(83, 209, 182, 0.15)";
    
    btnChangeWorkspace.onclick = async () => {
        try {
            updateStatus("📂 Opening Folder Picker...", "#ffb454");
            const selectedPath = await Host.Browser.WebView.PickFolderAsync();
            if (selectedPath) {
                const newWorkspace = await Host.System.FileSystem.SetWorkspace(selectedPath);
                workspaceInfo.textContent = `📂 Workspace: ${newWorkspace}`;
                updateStatus("✅ Workspace Updated", "#53d1b6");
            } else {
                updateStatus("🤖 Agent Ready", "#53d1b6");
            }
        } catch (e) {
            console.error("Workspace error:", e);
            updateStatus("❌ Picker Failed", "#ff7f7f");
        }
    };
    uiContainer.appendChild(btnChangeWorkspace);

    document.body.appendChild(uiContainer);

    function updateStatus(message, color = "#53d1b6") {
        statusText.style.color = color;
        statusText.textContent = message;
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
        updateStatus("🤖 Agent Idle", "#93a6c7");
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
        'ask_human': (args) => Host.PythonRuntime.Agent.askHuman(args.question),
        'confirm_dll_execute_command': async (args) => {
            const res = await Host.System.Terminal.Execute(args.command);
            try { return JSON.parse(res); } catch { return res; }
        },
        'confirm_dll_list_files': async (args) => {
            const res = await Host.System.FileSystem.ListFiles(args.dirPath || ".");
            try { return JSON.parse(res); } catch { return res; }
        },
        'confirm_dll_read_file': (args) => Host.System.FileSystem.ReadFile(args.filePath),
        'confirm_dll_write_file': (args) => Host.System.FileSystem.WriteFile(args.filePath, args.content)
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

                if (funcName === 'execute_terminal_command' && result.code !== 0) {
                    result.agent_hint = "Command failed. Please analyze the error and output.";
                }

                const resultString = typeof result === 'string' ? result : JSON.stringify(result, null, 2);
                const inputElement = await waitForElement(chunk, 'input[placeholder*="response"], textarea', 10000);

                if (!inputElement) return;
                setNativeValue(inputElement, resultString);

                await new Promise(r => setTimeout(r, 600));
                const sendBtn = chunk.querySelector('button[type="submit"]') || chunk.querySelector('button[aria-label*="Send"], button[aria-label*="Submit"]');
                
                if (funcName === 'ask_human') {
                    updateStatus(`🛑 Paused for Human Input`, "#ffb454");
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

    // 初期ワークスペース取得
    (async () => {
        try {
            const path = await Host.System.FileSystem.GetWorkspace();
            workspaceInfo.textContent = `📂 Workspace: ${path}`;
        } catch (e) {
            workspaceInfo.textContent = "📂 Workspace: (Not connected)";
        }
    })();
})();

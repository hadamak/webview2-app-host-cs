(function() {
    const TOOLS_DEFINITION = [
        {
            "name": "execute_terminal_command",
            "description": "ローカル PC のターミナルでコマンドを実行します。",
            "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] }
        },
        {
            "name": "list_files",
            "description": "ファイルとフォルダの一覧を取得します。",
            "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" } } }
        },
        {
            "name": "read_file",
            "description": "ファイルを読み込みます。",
            "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] }
        },
        {
            "name": "write_file",
            "description": "ファイルを書き込みます。",
            "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "content": { "type": "string" } }, "required": ["filePath", "content"] }
        }
    ];

    console.log("🚀 AI Agent Bridge (Python Edition): 強化版 observer 起動しました。");

    // --- 1. ツール定義の流し込み支援 ---
    const btn = document.createElement('button');
    btn.innerText = "📋 Copy Python Tools JSON";
    btn.style = "position:fixed; top:10px; left:100px; z-index:9999; padding:8px; background:#3776ab; color:white; border:none; border-radius:4px; cursor:pointer;";
    btn.onclick = () => {
        navigator.clipboard.writeText(JSON.stringify(TOOLS_DEFINITION, null, 2));
        alert("ツール定義をクリップボードにコピーしました。\n'Add Function' を押し、JSON エディタに貼り付けてください。");
    };
    document.body.appendChild(btn);

    // --- 2. 関数呼び出しの監視と自動実行 ---
    async function handleFunctionCall(chunk) {
        if (chunk.dataset.agentProcessed) return;

        const titleElement = chunk.querySelector('.mat-expansion-panel-header-title span:last-child');
        const codeElement = chunk.querySelector('code');
        if (!titleElement || !codeElement) return;

        const funcName = titleElement.innerText.trim();
        console.log(`🔍 [AI Agent] 関数呼び出し検出: ${funcName}`);
        
        // 実行マッピング表
        const methodMap = {
            'execute_terminal_command': () => Host.PythonRuntime.Terminal.execute(JSON.parse(codeElement.innerText).command),
            'list_files': () => Host.PythonRuntime.Filesystem.listFiles(JSON.parse(codeElement.innerText).dirPath || "."),
            'read_file': () => Host.PythonRuntime.Filesystem.readFile(JSON.parse(codeElement.innerText).filePath),
            'write_file': () => {
                const args = JSON.parse(codeElement.innerText);
                return Host.PythonRuntime.Filesystem.writeFile(args.filePath, args.content);
            }
        };

        if (methodMap[funcName]) {
            chunk.dataset.agentProcessed = "true";
            try {
                console.log(`🚀 [AI Agent] Python 実行中: ${funcName}`);
                const result = await methodMap[funcName]();
                console.log(`✅ [AI Agent] 実行結果取得: ${funcName}`, result);
                
                const input = chunk.querySelector('input[placeholder="Enter function response"]');
                const sendBtn = chunk.querySelector('button[type="submit"]');

                if (input && sendBtn) {
                    // 値をセットし、各種イベントを発火させて UI に変更を通知
                    input.value = JSON.stringify(result);
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                    input.dispatchEvent(new Event('blur', { bubbles: true }));

                    // 送信ボタンが有効になるのを待ってクリック
                    setTimeout(() => {
                        const isDisabled = sendBtn.getAttribute('aria-disabled') === 'true' || sendBtn.disabled;
                        if (!isDisabled) {
                            sendBtn.click();
                            console.log(`[AI Agent] 結果を送信しました。`);
                        } else {
                            console.warn(`⚠️ [AI Agent] 送信ボタンが無効なため、送信をスキップしました。`, {
                                ariaDisabled: sendBtn.getAttribute('aria-disabled'),
                                disabled: sendBtn.disabled
                            });
                        }
                    }, 800);
                }
            } catch (e) {
                console.error("[AI Agent] 実行失敗:", e);
            }
        }
    }

    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            mutation.addedNodes.forEach(node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    if (node.tagName === 'MS-FUNCTION-CALL-CHUNK') handleFunctionCall(node);
                    else node.querySelectorAll('ms-function-call-chunk').forEach(handleFunctionCall);
                }
            });
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
})();

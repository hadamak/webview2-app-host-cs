(function() {
    // 強化されたツール定義（ブラウザ操作を含む）
    const TOOLS_DEFINITION = [
        { "name": "execute_terminal_command", "description": "コマンドを実行します。", "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] } },
        { "name": "list_files", "description": "ファイル一覧を取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" } } } },
        { "name": "read_file", "description": "ファイルを読み込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
        { "name": "write_file", "description": "ファイルを書き込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "content": { "type": "string" } }, "required": ["filePath", "content"] } }
    ];

    console.log("🚀 AI Agent Bridge: ツール監視を開始しました。");

    async function handleFunctionCall(chunk) {
        if (chunk.dataset.agentProcessed) return;

        const titleElement = chunk.querySelector('.mat-expansion-panel-header-title span:last-child');
        const codeElement = chunk.querySelector('code');
        if (!titleElement || !codeElement) return;

        const funcName = titleElement.innerText.trim();
        console.log(`🔍 [AI Agent] 関数呼び出し検出: ${funcName}`);

        // ホスト側の C# 機能を直接呼ぶマップ
        const methodMap = {
            'execute_terminal_command': (args) => Host.PythonRuntime.Terminal.execute(args.command),
            'list_files': (args) => Host.PythonRuntime.Filesystem.listFiles(args.dirPath || "."),
            'read_file': (args) => Host.PythonRuntime.Filesystem.readFile(args.filePath),
            'write_file': (args) => Host.PythonRuntime.Filesystem.writeFile(args.filePath, args.content)
        };

        if (methodMap[funcName]) {
            chunk.dataset.agentProcessed = "true";
            try {
                const args = JSON.parse(codeElement.innerText || "{}");
                console.log(`🚀 [AI Agent] 実行中: ${funcName}`, args);
                const result = await methodMap[funcName](args);
                console.log(`✅ [AI Agent] 実行結果取得: ${funcName}`, result);
                
                // 結果をUIに書き戻す処理
                const input = chunk.querySelector('input[placeholder*="response"], textarea');
                const sendBtn = chunk.querySelector('button[type="submit"]');

                if (input) {
                    // スクリーンショットの場合は Base64 データをそのまま流し込む（UI側が対応している場合）
                    input.value = typeof result === 'string' ? result : JSON.stringify(result);
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    if (sendBtn && !sendBtn.disabled) {
                        setTimeout(() => sendBtn.click(), 500);
                    }
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
                    if (node.tagName.includes('FUNCTION-CALL')) handleFunctionCall(node);
                    else node.querySelectorAll('*').forEach(n => {
                        if (n.tagName.includes('FUNCTION-CALL')) handleFunctionCall(n);
                    });
                }
            });
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
})();

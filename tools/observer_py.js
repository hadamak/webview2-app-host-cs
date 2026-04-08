(function() {
    console.log("🚀 AI Agent Bridge (Python Edition): 監視を開始しました。");

    async function handleFunctionCall(chunk) {
        if (chunk.dataset.agentProcessed) return;
        chunk.dataset.agentProcessed = "true";

        const titleElement = chunk.querySelector('.mat-expansion-panel-header-title span:last-child');
        const codeElement = chunk.querySelector('code');
        
        if (!titleElement || !codeElement) return;

        const funcName = titleElement.innerText.trim();
        if (funcName !== 'execute_terminal_command') return;

        try {
            const args = JSON.parse(codeElement.innerText);
            const command = args.command;
            console.log(`[AI Agent] Python で実行: ${command}`);

            // ※ app.conf.json で設定した alias 名に合わせてください
            const result = await Host.PythonRuntime.Terminal.execute(command);
            console.log(`[AI Agent] 実行結果を取得:`, result);

            const input = chunk.querySelector('input[placeholder="Enter function response"]');
            const sendBtn = chunk.querySelector('button[type="submit"]');

            if (input && sendBtn) {
                input.value = JSON.stringify(result);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));

                setTimeout(() => {
                    if (sendBtn.getAttribute('aria-disabled') !== 'true') {
                        sendBtn.click();
                        console.log(`[AI Agent] Python の結果を送信しました。`);
                    }
                }, 500);
            }
        } catch (e) {
            console.error("[AI Agent] Python 連携エラー:", e);
        }
    }

    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            mutation.addedNodes.forEach(node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    if (node.tagName === 'MS-FUNCTION-CALL-CHUNK') {
                        handleFunctionCall(node);
                    } else {
                        const chunks = node.querySelectorAll('ms-function-call-chunk');
                        chunks.forEach(handleFunctionCall);
                    }
                }
            });
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
    document.querySelectorAll('ms-function-call-chunk').forEach(handleFunctionCall);
})();
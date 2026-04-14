(function() {
    'use strict';

    // エージェントに公開するツール定義 (DLL 呼び出しに最適化)
    const TOOLS_DEFINITION = [
        { "name": "execute_command", "description": "コマンドを実行します。", "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] } },
        { "name": "list_files", "description": "ファイル一覧を取得します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" } } } },
        { "name": "read_file", "description": "ファイルを読み込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
        { "name": "read_file_lines", "description": "範囲を指定してファイルを読み込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "start_line": { "type": "number" }, "end_line": { "type": "number" } }, "required": ["filePath", "start_line", "end_line"] } },
        { "name": "write_file", "description": "ファイルを書き込みます。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "content": { "type": "string" } }, "required": ["filePath", "content"] } },
        { "name": "replace_in_file", "description": "テキストを置換します。", "parameters": { "type": "object", "properties": { "filePath": { "type": "string" }, "old_text": { "type": "string" }, "new_text": { "type": "string" } }, "required": ["filePath", "old_text", "new_text"] } },
        { "name": "list_directory_tree", "description": "ディレクトリ構造をツリー表示します。", "parameters": { "type": "object", "properties": { "dirPath": { "type": "string" }, "max_depth": { "type": "number" } } } }
    ];

    window.__TOOLS_DEFINITION = TOOLS_DEFINITION;

    let ui;

    // DLL (Host.System) との紐付け
    const methodMap = {
        'execute_command': async (args) => parse(await Host.System.Terminal.Execute(args.command)),
        'list_files': async (args) => parse(await Host.System.FileSystem.ListFiles(args.dirPath || ".")),
        'read_file': (args) => Host.System.FileSystem.ReadFile(args.filePath),
        'read_file_lines': (args) => Host.System.FileSystem.ReadFileLines(args.filePath, args.start_line, args.end_line),
        'write_file': (args) => Host.System.FileSystem.WriteFile(args.filePath, args.content),
        'replace_in_file': async (args) => parse(await Host.System.FileSystem.ReplaceInFile(args.filePath, args.old_text, args.new_text)),
        'list_directory_tree': (args) => Host.System.FileSystem.ListDirectoryTree(args.dirPath || ".", args.max_depth || 2)
    };

    function parse(str) { try { return JSON.parse(str); } catch { return str; } }

    async function handleFunctionCall(chunk) {
        if (chunk.dataset.agentProcessed) return;
        chunk.dataset.agentProcessed = "true";

        const title = chunk.querySelector('.mat-expansion-panel-header-title span:last-child')?.innerText.trim();
        const codeEl = chunk.querySelector('code');
        
        if (!title || !codeEl || !methodMap[title]) return;

        if (ui) ui.updateStatus(`⚙️ Executing ${title}...`, "#53d1b6");
        
        try {
            const args = JSON.parse(codeEl.innerText || "{}");
            let result = await methodMap[title](args);
            
            if (title === 'execute_command' && result.code !== 0) {
                result.agent_hint = "Command failed. Please check stderr or the command syntax.";
            }

            const input = chunk.querySelector('input[placeholder*="response"], textarea');
            if (input) {
                setNativeValue(input, typeof result === 'string' ? result : JSON.stringify(result, null, 2));
                setTimeout(() => {
                    const btn = chunk.querySelector('button[type="submit"]') || chunk.querySelector('button[aria-label*="Send"]');
                    if (btn && !btn.disabled) btn.click();
                    if (ui) ui.updateStatus("🤖 Agent Ready", "#53d1b6");
                }, 600);
            }
        } catch (e) {
            if (ui) ui.updateStatus("❌ Execution Failed", "#ff7f7f");
            console.error("[AI Agent DLL]", e);
        }
    }

    function createUI() {
        const container = document.createElement("div");
        Object.assign(container.style, {
            position: "fixed", bottom: "20px", right: "20px", padding: "15px",
            background: "rgba(10, 20, 35, 0.95)", color: "#53d1b6", border: "1px solid #53d1b644",
            borderRadius: "12px", zIndex: "99999", display: "flex", flexDirection: "column", gap: "8px", 
            minWidth: "260px", backdropFilter: "blur(8px)", boxShadow: "0 8px 32px rgba(0,0,0,0.5)",
            fontFamily: "sans-serif", fontSize: "12px"
        });

        const st = document.createElement("div"); st.textContent = "🤖 Agent (DLL Mode) Ready";
        
        const ws = document.createElement("div"); 
        ws.style.cssText = "color:#93a6c7; font-size:11px; word-break:break-all; background:rgba(0,0,0,0.2); padding:6px; border-radius:4px;";
        ws.textContent = "📂 Workspace: (Loading...)";

        const btnRow = document.createElement("div");
        btnRow.style.cssText = "display:flex; gap:8px;";

        const btnWs = document.createElement("button"); 
        btnWs.textContent = "📂 Change Workspace";
        btnWs.style.cssText = "flex:1; background:#53d1b622; border:1px solid #53d1b6; color:#53d1b6; padding:8px; border-radius:6px; cursor:pointer; font-size:10px; font-weight:bold;";
        btnWs.onclick = async () => {
            const path = await Host.Browser.WebView.PickFolderAsync();
            if (path) {
                const updated = await Host.System.FileSystem.SetWorkspace(path);
                ws.textContent = `📂 Workspace: ${updated}`;
            }
        };

        const btnSetup = document.createElement("button");
        btnSetup.textContent = "⚙️ Setup Tools (JSON)";
        btnSetup.style.cssText = "flex:1; background:#93a6c722; border:1px solid #93a6c7; color:#93a6c7; padding:8px; border-radius:6px; cursor:pointer; font-size:10px; font-weight:bold;";
        btnSetup.onclick = setupFunctionCalling;

        btnRow.append(btnWs, btnSetup);
        container.append(st, ws, btnRow);
        document.body.appendChild(container);
        
        setTimeout(async () => {
            try {
                const p = await Host.System.FileSystem.GetWorkspace();
                ws.textContent = `📂 Workspace: ${p}`;
            } catch(e) {
                ws.textContent = "📂 Workspace: (Not detected)";
            }
        }, 500);

        return { updateStatus: (m, c) => { st.style.color = c; st.textContent = m; } };
    }

    async function setupFunctionCalling() {
        if (ui) ui.updateStatus("⚙️ Attempting setup...", "#93a6c7");
        
        try {
            // 1. Function Calling セクション内の Edit ボタンをクリック
            const section = Array.from(document.querySelectorAll('.settings-item')).find(el => el.textContent.includes('Function calling'));
            if (!section) throw new Error('Function calling section not found');

            const editBtn = section.querySelector('.edit-function-declarations-button');
            if (!editBtn) throw new Error('Edit button not found in section');
            editBtn.click();

            // 2. ダイアログの出現を待機
            let dialog;
            for(let i=0; i<20; i++) {
                dialog = document.querySelector('mat-dialog-container, .ms-dialog-container');
                if (dialog) break;
                await new Promise(r => setTimeout(r, 100));
            }
            if (!dialog) throw new Error('Dialog not found after clicking Edit');

            // 3. エディタを探して値をセット
            const textarea = dialog.querySelector('textarea');
            if (!textarea) throw new Error('Textarea not found in dialog');

            setNativeValue(textarea, JSON.stringify(TOOLS_DEFINITION, null, 2));

            // 4. Import / Save ボタンをクリック
            setTimeout(() => {
                const saveBtn = Array.from(dialog.querySelectorAll('button')).find(b => b.innerText.includes('Import') || b.innerText.includes('Save') || b.innerText.includes('保存'));
                if (saveBtn) {
                    saveBtn.click();
                    if (ui) ui.updateStatus("✅ Tools Setup Completed", "#53d1b6");
                } else {
                    if (ui) ui.updateStatus("⚠️ Manual Save Required", "#f39c12");
                }
            }, 500);

        } catch (e) {
            console.error("Setup error:", e);
            if (ui) ui.updateStatus("❌ Setup Failed: " + e.message, "#ff7f7f");
        }
    }

    function setNativeValue(el, val) {
        const proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
        if (setter) {
            setter.call(el, val);
        } else {
            el.value = val;
        }
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function start() {
        console.log("🚀 AI Agent Bridge (DLL Mode): 監視を開始しました。");
        ui = createUI();

        const observer = new MutationObserver(m => m.forEach(record => {
            // 新しいノードの追加を監視
            record.addedNodes.forEach(node => {
                if (node.nodeType === 1) {
                    // 1. エージェントの関数呼び出し要素を監視
                    if (node.tagName && node.tagName.toUpperCase().includes('FUNCTION-CALL')) handleFunctionCall(node);
                    else node.querySelectorAll('[class*="function-call"]').forEach(handleFunctionCall);

                    // 2. 設定ダイアログの出現を監視して自動注入
                    if (node.matches?.('mat-dialog-container, .ms-dialog-container, .mat-mdc-dialog-container') || 
                        node.querySelector?.('mat-dialog-container, .ms-dialog-container, .mat-mdc-dialog-container')) {
                        console.log("💎 Settings dialog detected. Attempting auto-injection...");
                        setTimeout(injectJsonToDialog, 500);
                    }
                }
            });
        }));
        
        observer.observe(document.body, { childList: true, subtree: true });

        // 自動適用の試行 (ページ読み込み完了時)
        setTimeout(setupFunctionCalling, 3000);
    }

    async function injectJsonToDialog() {
        const dialog = document.querySelector('mat-dialog-container, .ms-dialog-container, .mat-mdc-dialog-container');
        if (!dialog) return;

        const textarea = dialog.querySelector('textarea');
        if (!textarea) return;

        // すでに値が入っている（空でない）場合は上書きしない（ユーザーの操作を尊重）
        if (textarea.value && textarea.value.trim().length > 10) return;

        console.log("📝 Injecting TOOLS_DEFINITION...");
        setNativeValue(textarea, JSON.stringify(TOOLS_DEFINITION, null, 2));
        if (ui) ui.updateStatus("✅ Tools Injected", "#53d1b6");
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();

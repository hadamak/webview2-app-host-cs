/**
 * main.js — GameBridge API 実装例
 *
 * このファイルは WebView2 App Host の GameBridge API の使い方を示すデモです。
 * 自分のアプリを作るときはこのファイルを参考にして書き換えてください。
 *
 * GameBridge API 一覧:
 *   GameBridge.exitApp()            アプリを終了する（appClosing を経由）
 *   GameBridge.toggleFullscreen()   フルスクリーンを切り替える
 *   GameBridge.isFullscreen()       → Promise<boolean>
 *   GameBridge.confirmClose()       appClosing リスナー内で「閉じる」を確定する
 *   GameBridge.cancelClose()        appClosing リスナー内で「閉じる」をキャンセルする
 *
 * ライフサイクルイベント:
 *   window  の 'appClosing'      ウィンドウを閉じようとしたとき
 *   document の 'visibilitychange' 最小化・フォーカス切替時（標準 API と同じ）
 */

// =============================================================================
// ブラウザ開発用 stub
// ブラウザで直接 index.html を開いたとき自動的に有効になります。
// アプリ内では GameBridge はホスト側から注入されるため、このブロックは実行されません。
// =============================================================================
if (!window.GameBridge) {
  console.info('[GameBridge] ブラウザ環境 — stub を使用します');
  window.GameBridge = {
    exitApp() { window.close(); },
    toggleFullscreen() {
      document.fullscreenElement
        ? document.exitFullscreen?.()
        : document.documentElement.requestFullscreen?.();
    },
    isFullscreen() { return Promise.resolve(!!document.fullscreenElement); },
    confirmClose() { /* stub: ブラウザでは何もしない */ },
    cancelClose()  { /* stub: ブラウザでは何もしない */ },
  };

  // ブラウザでの動作確認用: beforeunload → appClosing に変換
  window.addEventListener('beforeunload', () => {
    window.dispatchEvent(new Event('appClosing'));
  });
}

// =============================================================================
// ユーティリティ
// =============================================================================
function appendLog(el, msg) {
  const line = document.createElement('div');
  line.className = 'log-line';
  line.textContent = `${new Date().toLocaleTimeString()} ${msg}`;
  el.prepend(line);
  while (el.children.length > 5) el.removeChild(el.lastChild);
}

// =============================================================================
// ウィンドウ操作
// =============================================================================
document.getElementById('btn-fullscreen').addEventListener('click', () => {
  GameBridge.toggleFullscreen();
});

// F11 はホスト側でも処理されますが、JS 側でも受け取れます
window.addEventListener('keydown', (e) => {
  if (e.key === 'F11') {
    e.preventDefault(); // ブラウザデフォルトのフルスクリーンを抑制
    GameBridge.toggleFullscreen();
  }
});

document.getElementById('btn-check-fs').addEventListener('click', async () => {
  const isFs = await GameBridge.isFullscreen();
  document.getElementById('fs-status').textContent =
    isFs ? 'フルスクリーン中' : 'ウィンドウモード';
});

document.getElementById('btn-exit').addEventListener('click', () => {
  // exitApp() は appClosing を経由してから閉じます。
  // appClosing リスナーが登録されていない場合は即座に閉じます。
  GameBridge.exitApp();
});

// =============================================================================
// ウィンドウタイトル変更
// <title> タグを書き換えるだけでタイトルバーに反映されます。
// =============================================================================
const ORIGINAL_TITLE = document.title;

document.getElementById('btn-set-title').addEventListener('click', () => {
  const val = document.getElementById('title-input').value.trim();
  if (val) document.title = val;
});

document.getElementById('btn-reset-title').addEventListener('click', () => {
  document.title = ORIGINAL_TITLE;
  document.getElementById('title-input').value = '';
});

// =============================================================================
// localStorage 永続化
// 通常のブラウザと同じ API で読み書きできます。
// データは %LOCALAPPDATA%\<EXE名>\WebView2\ に保存されます。
// =============================================================================
const SAVE_KEY   = 'demo_text';
const saveStatus = document.getElementById('save-status');

function refreshSaveDisplay() {
  const val = localStorage.getItem(SAVE_KEY);
  saveStatus.textContent = val !== null ? `保存済み: "${val}"` : '（未保存）';
}

document.getElementById('btn-save').addEventListener('click', () => {
  const val = document.getElementById('save-input').value.trim();
  if (!val) return;
  localStorage.setItem(SAVE_KEY, val);
  refreshSaveDisplay();
  document.getElementById('save-input').value = '';
});

document.getElementById('btn-clear').addEventListener('click', () => {
  localStorage.removeItem(SAVE_KEY);
  refreshSaveDisplay();
});

refreshSaveDisplay();

// =============================================================================
// appClosing（beforeunload 相当）
//
// ⚠️ 重要: appClosing リスナーを登録した場合、処理の最後に必ず
//   confirmClose() または cancelClose() を呼んでください。
//   どちらも呼ばないとウィンドウが永久に閉じられなくなります。
// =============================================================================
const closingLog = document.getElementById('closing-log');
const chkConfirm = document.getElementById('chk-confirm-close');

window.addEventListener('appClosing', () => {
  appendLog(closingLog, 'appClosing 発火');

  if (chkConfirm.checked) {
    appendLog(closingLog, '確認ダイアログ表示中…');
    if (confirm('アプリを終了しますか？')) {
      appendLog(closingLog, 'OK → confirmClose()');
      GameBridge.confirmClose(); // 閉じる
    } else {
      appendLog(closingLog, 'キャンセル → cancelClose()');
      GameBridge.cancelClose();  // キャンセル（次回も同じように動く）
    }
  } else {
    appendLog(closingLog, '→ confirmClose()');
    GameBridge.confirmClose();
  }
});

// =============================================================================
// visibilitychange
// ブラウザ標準の document.visibilityState が更新されるため、
// 通常のブラウザ向けコードがそのまま動きます。
// =============================================================================
const visibilityState = document.getElementById('visibility-state');
const visibilityLog   = document.getElementById('visibility-log');

document.addEventListener('visibilitychange', () => {
  const state = document.visibilityState;
  visibilityState.textContent = state;
  visibilityState.className   = `value-display ${state}`;
  appendLog(visibilityLog, `visibilityState → ${state}`);
});

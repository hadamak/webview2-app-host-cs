/**
 * main.js — AppBridge API 実装例
 *
 * このファイルは WebView2 App Host の AppBridge API の使い方を示すデモです。
 * 自分のアプリを作るときはこのファイルを参考にして書き換えてください。
 *
 * AppBridge API 一覧:
 *   AppBridge.exitApp()              アプリを終了する
 *   AppBridge.requestFullscreen()    フルスクリーン化する
 *   AppBridge.exitFullscreen()       ウィンドウモードに戻す
 *
 * ライフサイクルイベント:
 *   document の 'fullscreenchange'   フルスクリーン状態変化時（標準 API と同じ）
 *   document の 'visibilitychange'   最小化・フォーカス切替時（標準 API と同じ）
 */

// =============================================================================
// ブラウザ開発用 stub
// ブラウザで直接 index.html を開いたとき自動的に有効になります。
// アプリ内では AppBridge はホスト側から注入されるため、このブロックは実行されません。
// =============================================================================
if (!window.AppBridge) {
  console.info('[AppBridge] ブラウザ環境 — stub を使用します');
  window.AppBridge = {
    exitApp() { window.close(); },
    requestFullscreen() { document.documentElement.requestFullscreen?.(); },
    exitFullscreen()    { document.exitFullscreen?.(); },
  };
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
document.getElementById('btn-enter-fs').addEventListener('click', () => {
  AppBridge.requestFullscreen();
});

document.getElementById('btn-exit-fs').addEventListener('click', () => {
  AppBridge.exitFullscreen();
});

// F11 はホスト側でも処理されますが、JS 側でも受け取れます
window.addEventListener('keydown', (e) => {
  if (e.key === 'F11') {
    e.preventDefault();
    document.fullscreenElement ? AppBridge.exitFullscreen() : AppBridge.requestFullscreen();
  }
});

document.addEventListener('fullscreenchange', () => {
  document.getElementById('fs-status').textContent =
    document.fullscreenElement ? 'フルスクリーン中' : 'ウィンドウモード';
});

document.getElementById('btn-exit').addEventListener('click', () => {
  AppBridge.exitApp();
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

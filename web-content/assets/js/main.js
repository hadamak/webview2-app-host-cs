/**
 * main.js — AppBridge API 実装例
 *
 * このファイルは WebView2 App Host の AppBridge API の使い方を示すデモです。
 * 自分のアプリを作るときはこのファイルを参考にして書き換えてください。
 *
 * AppBridge API 一覧:
 *   (なし) - 標準の Web API を使用してください
 *
 * ライフサイクルイベント:
 *   document の 'fullscreenchange'   フルスクリーン状態変化時（標準 API と同じ）
 *   document の 'visibilitychange'   最小化・フォーカス切替時（標準 API と同じ）
 */

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
  document.documentElement.requestFullscreen();
});

document.getElementById('btn-exit-fs').addEventListener('click', () => {
  if (document.fullscreenElement) {
    document.exitFullscreen();
  }
});

// F11 キーでフルスクリーンをトグル
window.addEventListener('keydown', (e) => {
  if (e.key === 'F11') {
    e.preventDefault();
    if (document.fullscreenElement) {
      document.exitFullscreen();
    } else {
      document.documentElement.requestFullscreen();
    }
  }
});

document.addEventListener('fullscreenchange', () => {
  document.getElementById('fs-status').textContent =
    document.fullscreenElement ? 'フルスクリーン中' : 'ウィンドウモード';
});

document.getElementById('btn-exit').addEventListener('click', () => {
  window.close();
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

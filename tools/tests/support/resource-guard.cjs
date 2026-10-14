'use strict';
// 前端測試程序的資源上限（2026-10-13）。
//
// 2026-10-13 的改壞檢查中，有幾個前端測試程序在幾分鐘內各自占用數十 GB 記憶體，整台電腦的服務與程式因此停擺。
// 這些程序超過 Node 內建的 JavaScript 堆積上限（約 4 GB）以後仍繼續增長，堆積上限擋不住，
// 所以每個前端測試程序都要載入這支檔案：它另開一條監看執行緒，每 100 毫秒讀一次整個程序實際占用的記憶體，
// 超過上限或執行超過時間上限，就在 stderr 寫一行以 MARKER 開頭的說明，然後直接結束程序。
// 主執行緒卡在同步迴圈裡時，監看執行緒照樣運作。
//
// 上限可用環境變數調整：JET_TEST_MAX_RSS_MB（預設 2048）與 JET_TEST_MAX_SECONDS（預設 900）。
// 同一個程序重複載入時只啟動一次監看。只需要判讀標記、本身不該受上限管的程式改讀 resource-guard-marker.cjs。
const { Worker, isMainThread } = require('node:worker_threads');
const { MARKER } = require('./resource-guard-marker.cjs');
const DEFAULT_MAX_RSS_MB = 2048;
const DEFAULT_MAX_SECONDS = 900;

function readLimit(name, fallback) {
  const raw = process.env[name];
  if (raw === undefined || raw === '') return fallback;
  const value = Number(raw);
  if (!Number.isFinite(value) || value <= 0) throw new Error(name + ' must be a positive number, got: ' + raw);
  return value;
}

const limits = {
  maxRssBytes: Math.round(readLimit('JET_TEST_MAX_RSS_MB', DEFAULT_MAX_RSS_MB) * 1024 * 1024),
  maxSeconds: readLimit('JET_TEST_MAX_SECONDS', DEFAULT_MAX_SECONDS)
};

// 監看執行緒的程式。它只讀整個程序的記憶體與經過時間，不碰測試的狀態。
const WATCHER = `
const { workerData } = require('node:worker_threads');
const fs = require('node:fs');
const started = Date.now();
const timer = setInterval(() => {
  const rss = process.memoryUsage.rss();
  const seconds = (Date.now() - started) / 1000;
  let reason = null;
  if (rss > workerData.maxRssBytes) {
    reason = 'memory ' + Math.round(rss / 1048576) + ' MB exceeded the ' + Math.round(workerData.maxRssBytes / 1048576) + ' MB limit';
  } else if (seconds > workerData.maxSeconds) {
    reason = 'running for ' + Math.round(seconds) + ' s exceeded the ' + workerData.maxSeconds + ' s limit';
  }
  if (!reason) return;
  clearInterval(timer);
  try { fs.writeSync(2, '\\n' + workerData.marker + ' ' + reason + '; stopping test process ' + process.pid + '\\n'); } catch {}
  process.kill(process.pid, 'SIGKILL');
}, 100);
`;

if (isMainThread && !globalThis.__jetTestResourceGuard) {
  const worker = new Worker(WATCHER, { eval: true, workerData: { ...limits, marker: MARKER } });
  worker.unref();
  globalThis.__jetTestResourceGuard = { limits, worker };
}

module.exports = { MARKER, limits, DEFAULT_MAX_RSS_MB, DEFAULT_MAX_SECONDS };

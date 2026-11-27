'use strict';
// 資源上限停止測試程序時，寫進 stderr 的那一行開頭。
// 標記獨立成這支檔案，只讀標記的程式不會因為載入 resource-guard.cjs 而啟動上限。
//
// 同時執行的前端測試檔數，理由見 resource-guard.cjs：用 node --test 一次跑多個測試檔時，照這個值加上 --test-concurrency。
const MAX_PARALLEL_TEST_FILES = 4;

module.exports = { MARKER: 'JET test resource guard:', MAX_PARALLEL_TEST_FILES };

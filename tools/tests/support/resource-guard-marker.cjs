'use strict';
// 資源上限停止測試程序時，寫進 stderr 的那一行開頭（2026-10-13）。
//
// resource-guard.cjs 一載入就在當下的程序啟動上限，所以只需要判讀這個標記的程式（例如改壞檢查工具 mutate-frontend.cjs）
// 改讀這支檔案，不載入 resource-guard.cjs。改壞檢查工具一次要跑很多次 node --test，若自己也受測試程序的 900 秒上限管，
// 跑長清單時會在中途把自己停掉。
//
// 同時執行的前端測試檔數上限（2026-10-14）。resource-guard.cjs 只管單一測試程序，node --test 卻預設同時執行
// 「CPU 核心數減一」個測試檔，16 核心的電腦合計最多可用到 15 × 2048 MB，幾乎是整台電腦的記憶體。
// 一次 4 個時合計最多 4 × 2048 MB，等於 tools/harness/lanes.json 給整個驗證步驟的 8192 MB。
// 2026-10-14 實測：不設上限時尖峰約 2.7 GB，一次 4 個約 1.5 GB，兩者都約 26 秒跑完。
// FrontendMapping 與改壞檢查工具都讀這個值；自己用 node --test 一次跑多個測試檔時，也加上 --test-concurrency=4。
const MAX_PARALLEL_TEST_FILES = 4;

module.exports = { MARKER: 'JET test resource guard:', MAX_PARALLEL_TEST_FILES };

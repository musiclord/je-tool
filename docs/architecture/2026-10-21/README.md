# JET 架構圖表：2026-10-21 重繪

開啟[圖表入口](index.html)。這一版依 2026-10-15 與 2026-10-16 的固定來源快照，重繪 6 個 LikeC4 視圖與 18 張 Archify 圖。沒有重新索引程式，也沒有更新到之後的產品修正；程式現況仍以 src 與測試為準。`docs/architecture/` 只保留這一個圖表目錄，以重繪日期命名；下次重繪會取代它，前一版留在 Git 歷史。

## 怎麼讀

- 循序圖與 LikeC4 用線型區分證據。灰色實線是直接呼叫，編譯時目標是具體方法；藍色或紫色虛線是介面呼叫，只確定介面方法。間接觸發在 Archify 用綠色線、在 LikeC4 用琥珀色虛線，例如 action 字串、WebMessage、委派或延遲串流。每張圖的圖例會寫明。
- 介面背後畫出的實作類別，是依 AppCompositionRoot 的建立位置推論，不是觀察到的執行結果。
- 循序圖的標題與分段寫出情境、前置條件與所選分支；沒畫的分支寫在圖下方的卡片。
- 類別圖只畫編譯時存在的依賴、實作與繼承。組裝時才決定的對象，用 AppCompositionRoot 的「建立」線與卡片說明。
- 資料表關聯圖的線是程式在 SQL 裡使用的連接欄位；這批 SQLite 建表定義沒有宣告外鍵。

## 證據與檢查

[連線證據](connections.html)列出 174 條連線的依據：直接呼叫 9 條、介面呼叫 25 條、間接觸發 8 條、回傳 21 條，其餘是實作、建立、資料讀寫、環境前提，以及依[已核對事實](evidence.html)整理的業務流程、資料流、部署與資料表連線。四張類別圖的 48 條關係列在[類別簽章](types.html#bindings)頁。

`tools/verify.ps1 -Command Documentation` 會核對這一版的檔案雜湊、來源摘錄、事實引用，以及圖上線型與證據種類是否一致。這些檢查不證明業務結果正確，也沒有執行 JET；所有連線都標成未觀察執行。

## 可編輯來源

LikeC4 模型在 [jet.c4](likec4/jet.c4)，每條關係以 `evidence` 標記連到連線證據。Archify 的候選 JSON 在各圖的資料夾，例如[篩選情境儲存的循序圖](archify/action-sequence/candidate.json)。離線 HTML 內的本機連結已改成包內相對連結。

## 第三方授權

Archify 3.1.0 與 LikeC4 1.59.4 的原始授權保留在 [Archify 授權](licenses/archify-LICENSE.txt)和 [LikeC4 授權](licenses/likec4-LICENSE.txt)。

# JET 架構圖表

開啟[圖表入口](index.html)。這些圖在 2026-10-22 依提交 07b97b3 的程式快照重繪，沒有反映之後的修改；程式現況以 src 與測試為準。

## 怎麼讀

循序圖與 LikeC4 用三種線條區分呼叫方式：

- 灰色實線是直接呼叫：程式碼直接呼叫那個類別的方法。
- 鋼藍色虛線是介面呼叫：程式碼只呼叫介面；實際執行哪個類別，看 AppCompositionRoot 在啟動時建立哪一個。
- 琥珀色線是間接觸發，例如畫面用 action 名稱送出的訊息、事先交出的函式，或逐列讀取的資料。Archify 畫成較粗的實線，LikeC4 畫成虛線。

其他圖面：

- 方框顏色兩種工具相同：石墨灰是元件與類別，鋼藍是資料庫，青銅是畫面與介面型別，暖灰是外部資源與檔案。
- 循序圖只畫標題寫明的情境與分支，沒畫的分支寫在圖下方的卡片；生命線上的長條是該類別處理呼叫的期間。
- 類別圖只畫編譯時存在的依賴、實作與繼承；實作類別不重複列出介面已有的成員。
- 資料表關聯圖的線是程式在 SQL 裡使用的連接欄位；這批 SQLite 建表定義沒有宣告外鍵。
- 所有連線都來自閱讀程式碼，沒有實際執行。每條線的依據在[連線證據](connections.html)，類別圖的關係與成員在[類別與成員](types.html)。

## 可編輯來源

LikeC4 模型在 [jet.c4](likec4/jet.c4)；Archify 的候選 JSON 在各圖的資料夾，例如[篩選情境儲存的循序圖](archify/action-sequence/candidate.json)。配色檔與封裝方式見儲存庫的 `tools/harness/architecture/README.md`。

## 第三方授權

Archify 3.1.0 與 LikeC4 1.59.4 的原始授權保留在 [Archify 授權](licenses/archify-LICENSE.txt)和 [LikeC4 授權](licenses/likec4-LICENSE.txt)。

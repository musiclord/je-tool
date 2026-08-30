# JET 業務邏輯來源與裁決程序

更新日期：2026-08-30

這份文件回答兩個問題：`je-tool` 的產品規則從哪裡來，以及後續維護者遇到不同來源說法時應如何處理。
它不重複規則全文；日常閱讀仍從 [`jet-guide.md`](jet-guide.md) 開始。

## 已核對的遷移基準

- 主要來源是乾淨的 `new-je-tool/main`，核對時 HEAD 為
  `06b445e577f2c33be34c0ce625d8ecca48a7d30a`。
- `je-tool/src/JET/` 以該來源的產品樹為基準，只套用本次已決定的檔名、測試退出與新儲存庫邊界。
- `new-je-tool` 追蹤 970 個 `src/JET` 路徑；`je-tool` 首次提交候選有 940 個。兩邊共有 936 個
  同路徑檔案，其中 889 個 SHA-256 相同，47 個差異已按已裁定遷移變更分類。
- 47 個差異只涉及四個程式隨附範本的名稱、兩個新輸出檔名、legacy 檔名、案件中立註解，以及已退出的
  真實案件驗證、Excel 控制程式與舊框架專用測試。相應的回歸測試也依新邊界調整。沒有發現其他
  Domain、AuditCore、Application、資料庫 SQL、處理器或前端業務邏輯偏移。
- 來源獨有的 34 個路徑，包括四個舊名範本和三十個已退役的真實案件測試、驗證腳本與其專用輔助程式；
  目標獨有的四個路徑則是內容不變、名稱更新後的範本。

以上 Git 提交、路徑數與雜湊只用來證明這次遷移的核對結果。來源專案退役後，`je-tool` 的日常開發不會
依賴來源工作目錄、Git 物件、未整理的 legacy 原件或舊驗證輸出。

完整來源文件也留有可檢索副本：

- [`history/superseded/jet-guide-2026-08.md`](history/superseded/jet-guide-2026-08.md)：
  與來源指南只有 19 組已裁定的檔名／定位文字正規化。
- [`history/superseded/action-contract-manifest-2026-08.md`](history/superseded/action-contract-manifest-2026-08.md)
  與 [`history/superseded/jet-frontend-description-2026-08.md`](history/superseded/jet-frontend-description-2026-08.md)：
  核對時與來源對應文件內容相同。
- [`history/superseded/development-status-2026-08.md`](history/superseded/development-status-2026-08.md)：
  只供理解遷移基準當時的已知限制與驗證背景，不能冒充 `je-tool` 現況。
- [`history/development-log.md`](history/development-log.md)：
  合併後的開發紀錄，涵蓋 2026-06-04 到 2026-08-26 兩個世代的決策脈絡。查遷移基準當時的決策時只看
  2026-07 以後的條目；更早的條目描述的是前一個世代。同樣不能冒充 `je-tool` 現況。

## 現行業務主線

遷移保留的產品主線是：建立案件、匯入 GL／TB 與支援資料、提交欄位配對、建立有效母體並驗證、
執行可選的預篩選、保存進階條件情境、產生六份正式報告。

以下邊界已由來源文件與現行程式交叉核對：

- GL、TB、科目配對、日期維度與規則結果是核心資料；來源資料先進入暫存區，再轉換成標準資料。
- `JetFieldCatalog` 是 GL／TB 欄位語意目錄；`AuditDependencyPolicy` 是上游變更造成結果失效的矩陣。
- `JetAuditProgram` 與 `ProgramGraph` 描述六個主要步驟；前端只透過 `JetApi` action 通道操作。
- 驗證、預篩選與進階篩選由資料庫以參數化的集合查詢執行；金額以縮放後的整數保存。
- Bridge、WebView2 與工作階段狀態只接收摘要、中繼資料和有界分頁，不搬運完整母體。
- 預篩選是輔助訊號，不是進階篩選的必要前置；Criteria Selection Report 與 Working Paper 仍受
  validation 與 filter revision 約束。
- 六份報告由後端產生器使用正式結果建立；程式隨附範本固定跟著產品封裝。`data/` 不會自動變成
  程式隨附範本，也不能用來反向推導產品規則。
- SQLite、DuckDB 與 SQL Server 可以有不同實作，但相同契約不得產生不同業務答案。

## 權威順序

1. 使用者在目前工作中明確裁定的需求與邊界。
2. `je-tool/src/` 的現行程式、仍有效的契約測試與現行文件。
3. `docs/history/` 內正規化的來源文件、開發紀錄、當時狀態與已記錄的遷移差異。
4. `legacy/` 的 IDEA、VBA 與早期 .NET，只用來核對已建立明確對應的特定規則。目前整理後的檔名與
   結構就是保留版本，不要求未整理的其他版本或來源專案的 Git 逐行紀錄。
5. `data/` 工作簿只作使用者裁定的業務範例與來源錨點；其封裝中繼資料、外部關聯或儲存時間
   不能單獨成立產品規則。

使用者另外提供、但混有客戶作業痕跡或連線秘密的個人筆記，只能協助理解歷史動機。這類原件不搬入
儲存庫、不當測試資料，也不直接成立欄位、規則或驗收答案；需要長期保留的內容先去識別化，再與現行程式、
契約、測試或 [`../legacy/jet-legacy-notes.md`](../legacy/jet-legacy-notes.md) 互證。

來源提交只用來完成本次遷移核對，不是來源專案退役後的操作依賴，也不會凌駕 `je-tool` 後續經核准的新決策。
若現行程式與文件不一致，
先查明哪一邊是未回寫或未遷移的差異，不能直接選較方便的一邊。

## 修改前的追溯程序

凡是會改動欄位、金額、日期、有效母體、驗證、預篩選、進階篩選、前後端 action 格式、結果失效規則、
不同資料庫的一致性或正式報告，都先完成以下步驟：

1. 在 `je-tool/src/` 找到現行 action、Domain／AuditCore 權威型別、handler、provider 實作與測試。
2. 先讀現行文件的對應段落；需要精確規則時，再讀本檔連結的完整來源章節。
3. 與本檔記錄的基準、`docs/history/` 及現行回歸測試比較，確認差異屬於已決定的遷移變更、後續新決策，
   還是尚未解釋的偏移。
4. 需要和 legacy 比對時，只讀與該規則直接相關的檔案和區段，不把整段舊程式翻譯成新架構。
5. 如果證據仍支持兩種會改變對外行為的合理答案，或有人提議恢復舊驗證框架的限制，應停下來列出
   雙方證據並請使用者裁定；不能把來源專案是否還存在當成解決條件。

已退役的 JE／TB 特定案件測試、PBC／LegacyParity 執行器與 CaseAcceptance 設定，不代表相關產品流程
已被刪除；退出的是舊驗證入口。新的 `Provider`、兩個 GUI 情境、合成 SQLite 建案、六份合成報表的原生
Excel 往返檢查，以及明示授權的 `PrivateCase` 都已完成。`PrivateCase` 已能把 JE／TB 走完正式產品流程，
並比較 INF、業務結果與六份正確底稿的內容及版面；它不接回舊執行器，也不沿用舊案件差異清單。

目前 GUI 自動化沒有操作原生檔案選擇視窗。這只表示兩個既有 GUI 情境不涵蓋該項互動，不代表 JE／TB
匯入或私人案件驗收尚未建立，也不是第一次根提交的缺口。日後若修改檔案選擇流程，再為該項互動建立獨立、
可隔離且能清理的 GUI 情境。

`new-je-tool/docs/design_handoff_jet_frontend/` 是已棄用的臨時前端模板，不遷移、不重建，也不作未來
Claude Design 迭代的視覺依據。現行產品、現行文件與保留的 `jet-template-v1.html`／`v2.html`，才是
後續在本儲存庫內繼續設計的起點。

# JET 文件地圖與寫作規範

本檔說明這個 repository 的文件體系:每份文件的角色、更新時機,以及撰寫文件時必須遵守的格式規範。任何 agent 或開發者新增、修改文件之前,先讀本檔。

## 文件體系

文件分三類,角色不同、更新節奏不同:

### 一、現況規格(living specification)

永遠反映 `main` 分支**目前的實際行為**。程式碼變更若影響這些文件描述的內容,必須在同一個 commit 內同步更新——repository 是唯一事實來源,寫在外部(對話、聊天、個人筆記)的決策等於不存在。

| 文件 | 角色 |
|:---|:---|
| `README.md`(根目錄) | 專案識別、快速開始、目錄結構 |
| `AGENTS.md` | 跨 agent 邊界地圖:架構鐵律、契約先行流程、驗證指令 |
| `CLAUDE.md`、`.github/copilot-instructions.md` | 各 AI 工具的入口守則 |
| `docs/jet-guide.md` | 業務語意與架構的最深層權威:規則規格與命名登錄表(資料驗證、預篩選、進階篩選條件)、資料規模策略、資料庫 provider 策略、審計工作流程 |
| `docs/action-contract-manifest.md` | 前端 ↔ WebView2 bridge ↔ C# handler 之間所有 action 的 wire contract 唯一事實來源 |
| `docs/jet-frontend-description.md` | 前端結構、流程、狀態與資料契約的行為描述 |
| `docs/development-status.md` | 開發現況快照:目前可用的功能、進行中的工作、規劃中的方向、已擱置的決策 |

### 二、開發紀錄(development log)

`docs/development-log.md`:依日期排列(新在上)的開發紀錄,每輪開發一個條目,記錄「做了什麼、做了哪些關鍵決策、驗證結果」。只增不改(append-only);條目寫成自含的敘述,不依賴讀者知道其他條目的內容。權威細節仍在現況規格——紀錄只負責「為什麼會變成這樣」的追溯。

### 三、設計提案與交接

| 文件 | 角色 |
|:---|:---|
| `docs/specs/YYYY-MM-DD-主題-design.md` | 設計提案:某一輪開發開始前定稿的設計快照。檔首標註「狀態」(設計中/實作中/已落地/已擱置);實作落地後,內容的權威性轉移到現況規格,提案只剩歷史參考價值。GUI 驗收通過、權威完全轉移到現況規格後,依慣例可移除(歷史脈絡由 `development-log.md` 承載)。**注意:此目錄與 `.claude/skills/` 下的 obra/superpowers skill 框架無關,純粹是 JET 的設計提案存放處** |
| `docs/windows-handoff.md` | 滾動式交接:macOS 開發端無法執行、需在 Windows 環境完成的驗證任務。任務卡完成後移到「已完成」段 |

## 寫作規範

以下規範參考業界對 AI 協作專案文件的實務建議(living specs 的雙向回流、spec 與 change 分離、repository 對 agent 的可讀性)整理而成:

1. **人類可讀的敘述優先。** 用完整句子說明,不用只有作者懂的速記。一句話講一個重點,不要把七八個子句和層層括號塞進同一句。技術名詞該保留就保留,但前後文用自然的中文寫,不要生硬地中英夾雜。表格只放可枚舉的事實,解釋寫在表格外的敘述裡。
2. **禁止流水代號。** 不要發明「M3」「W-2」「F1」「D4」這類流水編號來統稱里程碑、問題、決策——跨文件撞名、離開原文即不可解。一律用**描述性名稱**(「多來源附加匯入」「CancellationToken 尚未串接」),需要排序時用日期。規則同樣不用代號:V1–V4/R1–R8/A2–A4 已於 2026-06-11 全面退役(歷史代號跨世代歧義——legacy 的 V1 與後來重編號的 V1 指涉不同測試),規則一律用 `docs/jet-guide.md` §4 命名登錄表的具體名稱(`completeness_test` 完整性測試、`post_period_approval` 期末後核准…);歷史代號只准出現在登錄表的「歷史代號」欄與 legacy 對照敘述中。
3. **領域名詞精確。** 模糊概念用領域專有名詞講清楚(見下方名詞速查),第一次出現時給中文全名與英文原文,例如「總帳(General Ledger,GL)」。
4. **狀態明確標示,且不誇大。** 規劃中/實作中/已落地/已擱置,不用「應該」「大概」這類含糊語氣描述事實狀態。**「已驗收」只能在使用者親自做完 GUI 人工驗收後才標**;程式完成、自動化測試全綠但尚未經使用者目視驗收時,一律寫「已實作/已落地,待 GUI 驗收」,不得寫成「已驗收」。測試數字據實回報(實際 passed/failed/skipped),不臆測、不為好看而美化。
5. **文件隨程式碼同 commit 更新。** 契約先行:動 action、payload、workflow 行為之前先改 manifest;落地後回寫受影響的現況規格(雙向回流,防止規格與實作漂移)。此條約束的是變更的打包方式(文件與程式碼不拆 commit),不是授權 agent 自行發動 commit——版控時機規範見 `AGENTS.md` 的 Version Control 章節。
6. **交接任務自含。** `windows-handoff.md` 的任務卡必須讓另一端 agent 不需要讀對話紀錄就能執行:指令、涵蓋內容、預期結果、失敗回報格式缺一不可。

## 領域名詞速查

完整定義在 `docs/jet-guide.md`;這裡只列文件中高頻出現、容易被縮寫帶過的名詞:

| 名詞 | 意義 |
|:---|:---|
| 日記帳分錄測試(Journal Entry Testing,JET) | 本工具:依 ISA 240 / ISA 330 對管理階層凌駕控制風險做全母體分錄篩選 |
| 總帳(General Ledger,GL) | 受查者全年度的日記帳分錄明細,測試母體 |
| 試算表(Trial Balance,TB) | 科目層級的期初/期末餘額或變動,完整性測試的對照基準 |
| 傳票(voucher / journal entry document) | 分錄的憑證單位;一張傳票含多列分錄,借貸必須平衡(借貸不平測試的檢核對象) |
| 暫存表(staging table,`staging_*`) | 匯入檔案的原始列落地處,cell 一律存字串,不做業務解釋 |
| 正式表(target table,`target_*`) | 欄位配對提交後,由暫存列投影出的正規化資料;規則 SQL 只查這層 |
| 匯入批次(import batch) | 一次「資料集匯入」的單位:一個 GL 或 TB 資料集對應一個批次,可由多個來源檔/工作表組成 |
| 投影(projection) | 暫存列 → 正式列的轉換:金額以 `decimal` 解析後乘 `MoneyScale`(10000)轉 scaled BIGINT、日期正規化為 `"yyyy-MM-dd"` 字串。技術文件用「投影」;**UI 對使用者一律顯示「標準化」**(審計員語彙) |
| 欄位配對(field mapping) | 來源欄名 → 邏輯欄位(`docNum`、`postDate`…)的對應,提交時觸發投影 |
| 集合式 SQL(set-based SQL) | 規則計算的唯一允許形式:參數化 SQL 交給資料庫引擎,不得在 C# 對母體做 LINQ(guide §1.5.2) |
| 民國年(ROC calendar year) | 台灣常用紀年,西元 = 民國 + 1911;匯入日期正規化支援 `114/6/11` 與 `1140611` 形式(guide §3.1.3) |
| 受查者提供資料(Provided by Client,PBC) | 審計術語:受查者交付的原始檔案,格式不可控,是匯入強健化的對象 |
| 工作底稿(workpaper) | 審計留痕輸出:測試結果與篩選條件的匯出文件 |
| 資料庫提供者(database provider) | 同一套 Domain repository 介面下的可替換引擎:目前 SQLite,保留 SQL Server(雲端/大資料)與 DuckDB(本機分析)的更換縫(guide §13) |

# JET 文件導覽

JET 文件以「讓讀者快速找到答案」為原則。現行行為寫在現況文件，歷史細節交由 Git 保存，不在工作樹長期堆放已完成的計畫草稿。

## 我應該先看哪一份？

| 您想知道的事情 | 請看 |
|:---|:---|
| 這是什麼專案、如何建置 | 根目錄 `README.md` |
| 目前完成到哪裡 | `development-status.md` |
| 現在需要我人工驗收什麼 | `windows-handoff.md` |
| 前端重新設計的設計交付 | `design_handoff_jet_frontend/` |
| 重要功能為什麼這樣設計 | `development-log.md` |
| 審計規則、資料欄位與系統架構 | `jet-guide.md` |
| 前端與 C# 之間傳什麼資料 | `action-contract-manifest.md` |
| 畫面流程與操作狀態 | `jet-frontend-description.md` |
| 大型計畫如何分階段交給全新 session | `../.agents/harness/fresh-session-staged-development.md` |

如果您只是要驗收程式，不需要閱讀技術規格；直接從 `windows-handoff.md` 開始即可。

## 必備文件

### 給一般使用者與驗收者

- `README.md`：專案介紹與基本操作入口。
- `development-status.md`：只記現在的完成狀態、待辦與延後事項。
- `windows-handoff.md`：只記目前需要真人操作的驗收步驟。
- `development-log.md`：只保留重要里程碑，不保存每次測試的流水帳。

### 給開發者與審閱者

- `jet-guide.md`：業務語意、審計規則、資料規模策略與架構的主要權威。
- `action-contract-manifest.md`：前端、WebView2 Bridge 與 C# handler 的 action 契約。
- `jet-frontend-description.md`：前端結構、六步驟流程與畫面狀態。
- `agent-frameworks.md`：AI framework 的採用範圍與更新方式。
- `sqlserver-online-handoff-deferred.md`：延後到企業環境處理的 SQL Server 驗收範圍。
- `design_handoff_jet_frontend/`：前端重新設計的設計交付與參考原型（設計稿，不是 runtime）。
- `AGENTS.md` 與 `.agents/`：AI 協作規則；大型跨 session 計畫以
  `.agents/harness/fresh-session-staged-development.md` 為唯一接力框架。

### 支援資料

- `specs/evidence/`：只保留仍會支持現行效能或人工候選的可重現證據索引。

## 暫時性文件

大型改動在動工前可以於 `docs/specs/` 建立一份有明確狀態的設計或執行計畫。這類文件不是永久知識庫。

當功能落地後：

1. 現行行為必須回寫到 `jet-guide.md`、action manifest、frontend description 或 development status。
2. 重要決策濃縮到 `development-log.md`。
3. 人工待驗事項移到 `windows-handoff.md`。
4. 已完成或已封存的 spec 從工作樹移除；需要追溯時使用 Git 歷史。

不要保留已完成但仍有空白 checkbox 的計畫，也不要讓舊候選或舊測試數字看起來像目前待辦。

### 執行逐字紀錄不屬於 docs/specs/

`docs/specs/` 只放人會讀的計畫與 evidence 索引。Agent session 的逐字 transcript、
工具輸出傾印與冷稽核檔案**不是文件**：它們動輒數十 MB、無法檢閱、會污染搜尋結果，
而且一旦被整批 staging 納入就永久留在 git object store。

- 需要保存這類紀錄時，檔名以 `-execution-details.md` 結尾，它已被 `.gitignore` 排除。
- 不要在其他文件連結它，也不要對它做全文 `grep`；只在有具體疑問時依日期與 session ID 搜尋局部區段。
- Evidence 文件只保存可重現的命令、數字與 SHA-256，不貼原始輸出全文。

## 寫作規則

1. **先寫給人看。** 使用完整、自然的中文；第一次出現的縮寫要說明。不要用只有 AI 或原作者看得懂的速記。
2. **一份資訊只維護一處。** 現況放 development status，人工步驟放 Windows handoff，規則與架構放 guide，wire contract 放 manifest。
3. **歷史與現況分開。** 舊設計、舊候選與被取代的測試結果不留在現行操作文件；Git 才是完整歷史。
4. **狀態不能誇大。** 自動測試通過只能寫「已實作／已落地」；只有使用者親自完成驗收後才能寫「已驗收」。
5. **數字要有日期與範圍。** 最新完整測試結果只放 `development-status.md`；evidence 可以保存當次精確數字與 SHA-256，但不能冒充現在狀態。
6. **人工驗收要能直接照做。** 每一步都寫清楚準備、操作、預期結果與異常回報方式；不要求使用者理解架構、測試框架或內部代號。
7. **人工驗收與內部交付機制分離。** 發布 profile 名稱、時間戳資料夾、精確路徑、hash、檔案數、verifier receipt 與候選汰換史只屬 agent 內部證據，不得出現在 `windows-handoff.md`、`development-status.md`、`development-log.md` 或交付給使用者的訊息。除非使用者明確要求追查技術細節，人工清單只稱「本次驗收程式」，而且封裝身分與完整性必須由 agent 在交付前自行驗證；不得要求使用者辨識候選或手動核對 hash。
8. **不要使用流水代號。** 以「Working Paper 大案匯出」等描述性名稱取代 H1、M3、W-2 等離開原文就無法理解的代號。
9. **文件與程式一起更新。** 行為、action 或畫面改變時，同一變更包必須更新對應文件；這不代表 agent 可以自行 commit。
10. **不寫逐檔清單。** 目錄樹、檔案列表與型別列表可由 repository 直接讀出，寫進文件只會過時。文件記的是「從結構讀不出來的規則與陷阱」；需要固定某份清單時，用架構測試把關，不用散文複述。
11. **不寫客戶資料。** 公司名稱、科目代碼與名稱、傳票號碼、人員姓名與金額明細，一律不得出現在任何文件、evidence、測試碼或斷言訊息。需要引用真實案件時只用 `case-A`／`case-B` 代號與彙總數字。邊界見 `AGENTS.md` 的 Sensitive Data 章節。

## 文件收尾檢查

每輪文件更新完成前，至少確認：

- `development-status.md` 只描述現在。
- `windows-handoff.md` 只包含仍要真人驗收的內容。
- 已完成的計畫沒有留在 `docs/specs/`。
- 文件連結沒有指向已刪除的現行入口。
- 沒有新增逐檔清單或目錄樹複本。
- 沒有任何客戶資料進入追蹤檔案。
- `git diff --check` 通過。

# JET 儲存庫共通規則

這份文件只保存整個儲存庫都適用、且不容易過時的規則。`je-tool` 是新的現行專案；
`je-testing` 與 `new-je-tool` 只是已完成核對、準備退役的遷移來源，不是後續開發的外部依賴。其 Git 歷史
不會匯入 `je-tool`，也不另建 mirror、bundle、逐筆提交對照或其他歷史副本；來源 GitHub 儲存庫可在完成
遠端讀回後設為唯讀封存。

## 權威順序

- 產品目的、服務順序與正式環境限制先查 `docs/project-context.md`；「完成 CaseWare IDEA 替代」的目前定義
  與未決項目見 `docs/idea-replacement-scope.md`。
- 現行行為先查 `src/`、相應測試與 `docs/` 的現行文件；跨來源的追溯順序與核對基準見
  `docs/business-logic-provenance.md`。
- 接續大型開發前先查 `docs/development-status.md` 的「目前大型計畫」，再依
  `docs/development-workflow.md` 核對實際 branch、HEAD、工作樹與上一個續接點。
- 欄位、有效母體、規則、action、失效矩陣、provider 或報告語意有歧義時，先交叉核對現行程式、測試、
  現行文件、`docs/history/` 與 `legacy/`；這些儲存庫內的來源仍無法裁定時，列出差異請使用者決定，
  不得假設舊專案仍可用，也不能只憑簡短摘要自行補完。
- `data/` 的實際檔名是儲存庫內工作簿名稱的唯一依據；程式隨附範本與新產物不得另建別名。
- `data/` 十份頂層工作簿是使用者裁定保留的 JET 業務範例；內容逐位沿用 `new-je-tool` 對應來源，
  沒有另行授權時不得清除 metadata、外部關聯、嵌入物或重存工作簿。
- `legacy/` 是經檔名正規化的 IDEA、VBA 與早期 .NET 對照資料，不是現行程式樹。
- `docs/history/` 是經檔名正規化、由使用者裁定保留的歷史文件範圍，不是來源專案的逐位快照。
  未修改原文、舊的 Git 逐行來源與未分類快照已明確不納入新專案；不能為了補回它們重新依賴來源專案，
  也不能讓歷史副本覆蓋現行程式或使用者裁定。
- 如果現行程式、來源基準、legacy 或文件仍支持兩種會改變對外行為的答案，列出差異並請使用者裁定；
  不得因實作方便自行選邊。

## 目錄責任

- `src/`：產品程式與仍有效的測試。
- `data/`：經人工裁定保留、只做檔名更正的 JET 參考工作簿；業務邏輯歧義依上述儲存庫內權威
  順序處理，不以重新儲存範例檔的方式推導或修正行為。
- `docs/`：目前仍有效的說明、技術參考與正規化歷史副本。
- `legacy/`：舊系統規則與架構對照；不可整段翻譯成新實作。
- `docs/jet-template-v1.html` 與 `docs/jet-template-v2.html` 是使用者裁定保留的版本，沒有明確要求時不得修改。

## 機敏資料

- 真實案件、客戶資料與由真實案件產生的輸出不得進入 Git、文件、測試碼、TRX、patch、聊天或外部服務。
- `data/test-case/` 是只留在這台電腦的私人驗收案件，整個目錄都必須由 Git 排除。未獲當次工作明確授權，
  不得讀取或掃描這個目錄、`data/temporary-test-case/`、`data/legacy-parity-work/` 或其他私人資料根目錄。
- 獲准執行私人案件時，只能讀取當次明示指定的案件清單與檔案角色；一般 Build、Focused、Public、Provider、
  Package、GUI 或 Excel 驗證不得順便搜尋 `data/test-case/`。
- 不得在證據中寫出私人絕對路徑、公司名稱、工作表名稱、欄位、儲存格、科目、傳票、人員或金額。
- 測試與範例優先使用程式生成的合成資料。路徑、連結、重新解析點或資料來源無法確認時，應拒絕繼續，
  不能把不確定狀態當成通過。
- 若發現疑似機敏內容，停止擴大讀取，只回報檔案與風險類型，並先排除於提交候選，等待使用者裁定。

## Git 與新儲存庫邊界

- 保留使用者尚未提交的工作樹。沒有使用者明確指令時，不得主動提議或執行 reset、checkout、clean、stash、
  stage、commit、push 或變更 remote。計畫完成、測試通過或工作樹可提交都不是 Git 授權。
- 不匯入來源專案的 commits、tags、refs、reflogs、bundles 或其他 Git 物件。
- 不為遷移建立或要求來源專案的鏡像或 bundle；來源 Git 歷史、未整理的 legacy 版本與舊的 Git 逐行來源，
  已由使用者決定不列入 `je-tool` 交付內容。
- 第一次根提交前，應根據未追蹤檔案與忽略規則產生完整候選清單；不得用 staging 或 stash 暫存候選。
- 即使獲准 stage，也只能加入使用者確認的明確路徑。禁止 `git add -A`、`git add --all`、`git add .` 與
  `git write-tree`，避免忽略規則改變時把私人資料寫入 Git object store。
- Commit 訊息、PR 內文與同步說明不得自行加入 `Co-Authored-By`、`Generated with` 或其他 AI 署名，除非
  使用者明確要求。
- Commit、PR 與交付說明只保留結果、必要理由、風險與實際驗證；不逐檔重述 diff，也不把執行日誌改寫成
  冗長說明。
- 第一次 commit 或 push 前，必須由使用者另行確認完整內容、分支與遠端位置。

## 測試與驗證框架

- 新驗證框架的規範與分階段計畫位於 `docs/harness.md` 與
  `docs/specs/2026-08-28-harness-rebuild-plan.md`。Phase 1 至 Phase 7 已完成，日常開發應透過 `Focused` 或
  `Public` 執行公開產品測試；`Provider`、`Package`、兩個隔離的 GUI 情境，以及六份合成報表的原生 Excel
  開啟與儲存檢查都有完整通過紀錄，完整 `ReleaseCandidate` 也已通過。SQLite 與 SQL Server 的同一私人案件
  已分別通過；後續仍只能依當次實際執行的 `PrivateCase` 收據回報，不能把舊結果當成新的通過證明。
- `PrivateCase` 固定同時比較六份底稿的內容與版面；INF 依規則與實際母體判定；本次私人副本及輸出不論
  成功或失敗都要清除。這三項規則不得由執行器自行放寬。
- `ReleaseCandidate` 只使用第一次根提交候選清單建立一次性快照，固定依序執行 Contract、Documentation、
  Public、Package、Gui 與 Excel。它不要求 live SQL Server；SQL Server 實跑相容性只能另行明示執行完整
  `Provider`。兩者都不得讀取或自動執行 `PrivateCase`，也不得修改來源 Git 索引。
- 不從舊專案複製 `.agents/`、`tools/`、技能、設定檔、逐次驗證紀錄或 CI 流程。
- 舊專案的 `TestResults/`、`StrykerOutput/`、`artifacts/`、`bin/obj`、執行階段的 `projects/` 與私人案件
  資料夾，都不能作為新驗證框架的判定基準、測試資料來源或執行紀錄。新框架只使用明確指定的輸入、
  本次執行專屬輸出，以及同一次執行重新產生的證據。
- 現行 `JET.Tests` 不得要求儲存庫內存在特定真實案件、舊驗證框架檔案或舊執行產物，也不得因私人測試資料
  不存在就把必要檢查視為通過。
- GL、TB、PBC、不同資料庫實作及結果比較等產品能力，不會因舊執行器退出而刪除。`JET.Tests` 只保留使用
  合成或去識別化資料、且不自行探測私人資料根目錄的公開測試與共用程式。真實案件只透過明示授權的
  `PrivateCase` 清單與受控副本注入。
- 正式測試一律從 `tools/verify.ps1` 進入。直接執行 `dotnet test` 或測試執行檔只能用於診斷，不能取代
  `Focused`、`Public` 或後續驗證路線的正式紀錄。

## Agent 相容性與開發續接

- 本檔是 GitHub Copilot、Claude Code、Codex 與 VS Code AI Agent 的共用規則權威。Claude Code 由
  `CLAUDE.md` 匯入本檔；Copilot 與 VS Code 的薄轉接位於 `.github/copilot-instructions.md`。轉接檔不得建立
  第二套產品、Git、隱私或驗證規則，詳見 `docs/agent-compatibility.md`。
- 需要跨多個步驟或 session 的工作，一次只保留一份現行大型計畫。計畫要持續記錄範圍、使用者裁定、目前
  步驟、第一次失敗、最後有效驗證及下一動作；不能只存在聊天、Agent memory 或 ignored receipt 中。
- 換用 Agent 或新 session 時，先依 `docs/development-workflow.md` 讀取現況與現行計畫，再核對實際工作樹。
  工具品牌、原生 plan mode、子 Agent 或本機記憶不會改變驗收與權限邊界。
- 大型工作結束或使用者詢問「還有哪些未完成事項」時，必須逐項讀取並回報
  `docs/development-status.md` 的「已知但延後的事項」，包含背景、目前邊界與重啟條件。不能只列名稱，
  也不能因該項不阻擋目前工作就省略；有證據確認解決後才從清單移除。

## 程式與文件

- UI 只負責操作與呈現；審計判斷留在 Domain、AuditCore、Application 與資料庫實作邊界。
- 前端與 C# 之間只走既有 `JetApi` action channel。修改 action 時同步核對 handler、registry、文件與測試。
- 大量資料留在資料庫集合式處理；前端與 session state 只接收摘要、metadata 與有界分頁。
- 金額、日期、欄位配對與 provider 等價規則不得只為單一案件或單一 provider 特化。
- 每份文件只處理一個主要用途。現況、操作方法、技術參考、歷史及執行紀錄要分開，避免同一件事到處重複。
- 中文使用台灣常見、自然的說法。固定命令、程式名稱與必要術語可以保留英文，但第一次出現時要說明用途。
- 不自行創造近義詞、縮寫、階段名稱或流程名稱。每句只放一個主要意思，避免連續括號、斜線、箭頭和名詞堆疊。
- 舊句子若已經不自然，可以整段重寫，不必保留先前 Agent 的句型。文件中的現況必須有程式、測試、決策或
  本次執行紀錄支持。
- 改寫不得改變數字、條件、例外、不確定性、正規名稱、schema、UI 行為或使用者裁定。資料不足時標成待確認
  並請使用者補充，不能自行補猜。
- 修改現行文件後，執行 `tools/verify.ps1 -Command Documentation`，再把改動的段落完整讀一次。自動檢查只會
  阻擋缺漏與已確認錯誤的現況；風格疑點只會提示，不能取代人工閱讀。
- 大型文件改動完成前，應由不熟悉本輪細節的人或 fresh session 用自己的話說明系統目的、採用理由與下一步；
  發現無法轉述或理解錯誤時，回到現行文件修正。
- 同一資訊只維護一處。不能把舊專案的通過結果、逐次數字或對話紀錄當成 `je-tool` 現況。
- `new-je-tool/docs/design_handoff_jet_frontend/` 是已棄用的臨時前端模板，不遷移、不補存，也不作現行
  UI 或未來 Claude Design 迭代的權威；保留的視覺版本只有 `docs/jet-template-v1.html` 與 v2。

## 收尾

- 回報實際讀取、修改、驗證、跳過與未能確認的項目。
- 未執行 build、測試、GUI、Excel 或真實資料驗收時必須明說。
- 沒有明確授權時，最多整理到可提交狀態，不進行暫存、提交或推送。

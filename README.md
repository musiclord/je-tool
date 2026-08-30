# JET

JET 是一套 Windows 桌面審計工具，以 .NET 10、WinForms 與 WebView2 執行。大量資料主要留在
SQLite、DuckDB 或 SQL Server 中處理，前端只接收摘要和分頁結果。

專案從 CaseWare IDEA、台大課程合作與 Excel VBA／Access 實作逐步演進到目前架構。近期先讓 GA 能在
公司限制下以本機方式完成原本 IDEA JET 的工作，再處理新的 KCT 條件；完整背景與環境限制見
[`docs/project-context.md`](docs/project-context.md)。

這個目錄是由 `je-testing` 與 `new-je-tool` 整理而成的新儲存庫。兩個來源專案的 Git 歷史不會匯入，也不另建
mirror、bundle 或逐筆提交對照；後續開發需要的背景已整理到 [`docs/`](docs/README.md) 和
[`legacy/`](legacy/README.md)。`je-tool` 完成遠端讀回驗證後，既有來源 GitHub 儲存庫可設為唯讀封存，
但不再是 JET 執行或判定規則的必要條件。

## 目前狀態

- 產品程式位於 `src/JET/`。
- `data/` 內的十份工作簿是使用者決定保留的 JET 業務範例，檔名也是本儲存庫的命名依據。這些檔案已用
  唯讀方式檢查結構與 SHA-256，確認和 `new-je-tool` 的對應來源相同；遷移期間不會重新儲存或改寫內容。
- 舊驗證框架專用的真實案件測試、執行腳本與發布腳本已退出。產品功能、合成測試、解析器、比較器、
  路徑安全檢查及資料結構規則仍然保留。
- `new-je-tool` 的臨時前端交接資料夾 `design_handoff_jet_frontend/` 已決定不遷移。後續設計會以現行產品和
  `docs/jet-template-v1.html`、`docs/jet-template-v2.html` 為起點。
- 舊的測試結果、建置輸出、執行產物和私人案件資料夾留在原本的本機範圍，不會成為新驗證框架的輸入、
  判定依據或歷史證明。
- 新驗證框架已完成 Phase 1 至 Phase 7。`Provider`、`Package`（Release）及隔離的 GUI 情境都已通過；原生
  Excel 也已用同一輪產生的六份合成報表，完成開啟、重算、另存、重新開啟與 PDF 輸出。Phase 6 已接上
  正式 `PrivateCase` 入口，並依使用者決定同時比較六份底稿的內容與版面、按規則與實際母體檢查 INF，
  成功或失敗都清除本次私人副本與輸出。三項業務差異已依使用者裁定修正；情境 1 的整張傳票口徑只套用
  在單獨的「未預期借貸組合」，不會擴張其他情境。同一私人案件已分別用 SQLite 與 SQL Server 通過 INF、
  業務結果及六份底稿的內容與版面比較，兩次都完成清理且沒有把私人路徑寫進收據。Phase 7 的
  `ReleaseCandidate` 也已在第一次根提交候選快照中完整通過；它沒有讀取私人案件，快照與來源 Git index
  都已按規則收尾。
- `Documentation` 命令會攔截缺檔、必要指向缺漏及已確認過時的事實；風格疑點只會提出 warning。修改文件後
  仍要人工讀完改動段落，不能只看命令結果。
- `main` 尚無提交。目前只整理第一次根提交的候選內容，不會自動暫存、提交或推送。
- `AGENTS.md` 是各 AI Agent 的共用權威；`CLAUDE.md` 與 `.github/copilot-instructions.md` 是薄轉接檔。相容
  方式見 [`docs/agent-compatibility.md`](docs/agent-compatibility.md)。

第一次接手請依序閱讀：

1. [`docs/project-context.md`](docs/project-context.md)
2. [`docs/jet-guide.md`](docs/jet-guide.md)
3. [`docs/development-status.md`](docs/development-status.md)
4. [`docs/development-guide.md`](docs/development-guide.md)
5. [`docs/development-workflow.md`](docs/development-workflow.md)
6. [`docs/harness.md`](docs/harness.md)

機敏資料、私人案件資料夾及 Git 操作限制見 [`AGENTS.md`](AGENTS.md)。

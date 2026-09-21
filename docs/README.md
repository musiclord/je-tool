# JET 文件導覽

更新日期：2026-09-18

這裡把現況、操作、技術參考與歷史分開。一般閱讀先看現行文件；只有需要追查設計來源時才進入
[`history/`](history/README.md) 或 [`../legacy/`](../legacy/README.md)。

## 從哪裡開始

| 想知道什麼 | 請看 |
|:---|:---|
| JET 為什麼存在、目前先服務什麼、正式環境有何限制 | [`project-context.md`](project-context.md) |
| JET 是什麼、資料怎麼流動 | [`jet-guide.md`](jet-guide.md) |
| JET 執行時各層如何串接、資料庫如何分流 | [`architecture/README.md`](architecture/README.md) |
| 新儲存庫整理到哪裡 | [`development-status.md`](development-status.md) |
| 開發環境與目前可用入口 | [`development-guide.md`](development-guide.md) |
| 大型開發如何跨 session 續接與關閉 | [`development-workflow.md`](development-workflow.md) |
| 多個 session 的觀點、需求與待完成事項如何收斂 | [`../.agents/harness/convergence-and-memory.md`](../.agents/harness/convergence-and-memory.md) |
| CaseWare IDEA 的替代範圍與完成條件 | [`idea-replacement-scope.md`](idea-replacement-scope.md) |
| Copilot、Claude、Codex 與 VS Code 如何共用規則，以及 Claude Code 的攔截層與 hook | [`agent-compatibility.md`](agent-compatibility.md) |
| 新驗證框架的責任、資料與判定界線 | [`harness.md`](harness.md) |
| 驗證框架的重建階段與完成紀錄 | [`specs/2026-08-28-harness-rebuild-plan.md`](specs/2026-08-28-harness-rebuild-plan.md) |
| 目前的大型計畫與驗收狀態 | [`development-status.md`](development-status.md) 的「目前大型計畫」 |
| 畫面結構與六個操作步驟 | [`jet-frontend-description.md`](jet-frontend-description.md) |
| 前端與 C# 之間的 action 通道 | [`action-contract-manifest.md`](action-contract-manifest.md) |
| `data/`、程式隨附範本與 `legacy/` 的分工 | [`data-and-legacy.md`](data-and-legacy.md) |
| SQL Server 企業環境的延後範圍與已知安全缺口 | [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md) |
| 業務邏輯從哪裡來、衝突時如何裁決 | [`business-logic-provenance.md`](business-logic-provenance.md) |
| 來源專案的關係與新儲存庫決策 | [`repository-lineage.md`](repository-lineage.md) |
| `ReleaseCandidate` 的候選檔案清單（檔名沿用第一次根提交時期） | [`first-root-commit-candidate.txt`](first-root-commit-candidate.txt) |

## 全部計畫清單

2026-09-17 核對：`docs/specs/` 有 8 份計畫，其中 1 份現行、4 份已完成、3 份已由後續計畫接續。
另有 12 份前代設計與證據，逐份列在 [歷史文件清單](history/README.md#歷史設計與證據清單)。
歷史段落的「待驗收」「下一步」只描述當時；現在待驗收及待開發事項以現行計畫的
[本輪範圍與產品接續摘要](specs/2026-09-17-user-feedback-and-workflow-review-plan.md#現在要做什麼)為準。

| 計畫 | 目前歸屬 | 查閱用途 |
|:---|:---|:---|
| [2026-08-28 驗證框架重建](specs/2026-08-28-harness-rebuild-plan.md) | 已完成 | 框架各階段、當時驗證與第一次失敗；操作命令改看現行 harness 文件。 |
| [2026-08-29 Agent 治理與專案脈絡](specs/2026-08-29-agent-governance-and-context-plan.md) | 已完成 | 共用入口、背景及跨 session 規則的建立過程。 |
| [2026-08-30 儲存庫收斂](specs/2026-08-30-repository-consolidation-plan.md) | 已完成 | 文件整理與工具轉接；當時的 Git 授權不適用現在。 |
| [2026-09-01 欄位配對同步、支援日誌與損壞案件復原](specs/2026-09-01-frontend-sync-devlog-mutation-plan.md) | 已完成，使用者已驗收 | 配對同步、報告與工作檔拆分、底稿版本及變異測試。 |
| [2026-09-06 篩選流程修正](specs/2026-09-06-filter-workflow-correction-plan.md) | 已由 9/07 接續 | 「待判定」及獨立排除區域已被新裁定取代，不重新列待驗收。 |
| [2026-09-07 篩選收斂](specs/2026-09-07-filter-convergence-plan.md) | 已由 9/09 接續 | 九項篩選功能與第五步操作已驗收；公司三項於 9/17 回覆通過。 |
| [2026-09-09 第一至第四步回饋](specs/2026-09-09-user-feedback-steps-1-to-4-plan.md) | 已由 9/17 接續 | 原話、PBC 盤查及既有修正來源；未完成部分已併入現行計畫。 |
| [2026-09-17 使用者回饋與操作流程一致性](specs/2026-09-17-user-feedback-and-workflow-review-plan.md) | 唯一現行計畫，保留試用及明示延後事項 | 50 項需求、最新裁定、A–U 入口與驗證結果、建置警告修正及延後邊界。 |

## 專案文件如何分工

- 目前進度及延後條件看 `development-status.md`；本輪逐項驗收看唯一現行計畫，不另建待辦清單。
- 產品背景與完成範圍看 `project-context.md`、`idea-replacement-scope.md`；審計規則、畫面及 action 分別看
  `jet-guide.md`、`jet-frontend-description.md`、`action-contract-manifest.md`。
- 來源判定看 `business-logic-provenance.md`、`data-and-legacy.md`、`repository-lineage.md`；
  `architecture/` 是 2026-08-30 修訂的架構快照，不作為最新 action、報告數量或日誌行為的依據。
- 開發與驗證看 `development-guide.md`、`development-workflow.md`、`harness.md`、`../tools/README.md`；
  Agent 共用方式看 `agent-compatibility.md`。企業延後範圍看 `sqlserver-enterprise-deferred.md`。
- `history/` 和 `legacy/` 保留來源。兩份 `jet-template` HTML 是保留版本，`first-root-commit-candidate.txt`
  是候選檔案清單；它們不另形成現行需求或待驗收清單。

## 文件權威

- 精確程式行為以 `src/` 與仍有效的測試為準。
- 現行文件只描述現在，不承接舊專案的驗證結論。
- 需要精確規則或遷移差異時，按 `business-logic-provenance.md` 使用現行程式、測試、正規化歷史與
  legacy 證據；來源專案不是後續查核依賴。
- `docs/history/` 與 `legacy/` 已按目前檔名正規化，能解釋來歷，但不是逐位原文。
- 未保留的逐位原文、舊路徑、其他版本與 Git 逐行來源已明確排除，不為此保存鏡像、套件或未分類副本。

`new-je-tool/docs/design_handoff_jet_frontend/` 是已棄用的臨時前端模板；歷史文件若仍提到它，只代表
當時脈絡，不代表現行儲存庫缺檔或未完成遷移。

`jet-template-v1.html` 與 `jet-template-v2.html` 是使用者已裁定保留的版本，不參與一般文件重寫。

## 寫作規範

- 每份文件只處理一個主要用途。現況、操作方法、技術參考、歷史與執行紀錄分開，同一資訊只維護一處；
  不能把舊專案的通過結果、逐次數字或對話紀錄當成 `je-tool` 現況。
- 中文使用台灣常見、自然的說法。固定命令、程式名稱與必要術語可以保留英文，第一次出現時說明用途。
- 不自行創造近義詞、縮寫、階段名稱或流程名稱。每句只放一個主要意思，避免連續括號、斜線、箭頭與
  名詞堆疊；不用 `＋`、`／`、`→` 把名詞串成句子。
- 驗證框架的工作用語（receipt、gate、lane、`紅燈`、`綠燈`）留在 `harness.md` 與 `tools/README.md` 這類技術
  參考。入口文件、狀態、計畫與 commit 訊息改說收據、檢查、路線、通過、失敗。整段改寫的步驟、檢查表與
  改寫範例見 [`../.agents/skills/jet-readable-docs/SKILL.md`](../.agents/skills/jet-readable-docs/SKILL.md)。
- 舊句子已經不自然時可以整段重寫，不必保留先前 Agent 的句型；但文件中的現況必須有程式、測試、決策
  或本次執行紀錄支持。
- 引用或遷移他處內容時先找到原文，查核對象、檔名、數字與裁定逐字照抄；名稱有新舊對應時把對應寫明，
  不得默默替換成現行名稱。找不到出處的聲明刪掉或標成待確認，不以合理推測填空。
- 新名詞的准入條件：程式、UI、資料結構或外部標準本來就存在的名稱，或確實會反覆使用且能用一句白話
  定義的概念。其他情況用普通中文說清楚，不替它命名。

## 文件完成前

1. 執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Documentation`，檢查缺檔、必要指向、已確認過時的
   事實與文字風格 warning。
2. 把本次改動的段落完整讀一次。每一條 warning 都要有結論：改掉，或說明為什麼保留。命令通過不代表
   文句一定自然，也不代表沒有形近錯字。
3. 確認相對連結存在，現況與歷史沒有混寫。
4. 確認工作簿名稱與 `data/`、程式隨附範本的實際名稱一致。
5. 歷史副本要清楚標示已正規化，不得宣稱與來源逐位相同。
6. 不要修改兩份已保留的 HTML 模板。
7. 改寫前後要保留數字、條件、例外、不確定性、正規名稱、schema、UI 行為與使用者裁定；資料不足就標為
   待確認，不自行補猜。
8. 大型文件改動要讓不熟悉本輪細節的人或 fresh session 用自己的話說明目的、理由與下一步，再修正誤解點。

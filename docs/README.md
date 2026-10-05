# JET 文件導覽

這裡放給人讀的現行文件：專案背景、規格、操作、開發與驗證入口。給 AI 編碼工具讀的流程文件在 `../.agents/`，
過去的設計書、證據與已結束的計畫在 [`history/`](history/README.md)，舊系統的對照來源在 [`../legacy/`](../legacy/README.md)。
一般閱讀先看現行文件；只有要追查設計來源時才進歷史。

## 從哪裡開始

| 想知道什麼 | 請看 |
|:---|:---|
| JET 為什麼存在、目前先服務什麼、正式環境有何限制 | [`project-context.md`](project-context.md) |
| JET 是什麼、資料怎麼流動、各層怎麼分工 | [`jet-guide.md`](jet-guide.md) |
| 畫面結構、六個操作步驟與畫面用語 | [`jet-frontend-description.md`](jet-frontend-description.md) |
| 前端與 C# 之間的 action 通道 | [`action-contract-manifest.md`](action-contract-manifest.md) |
| 現在做到哪裡、哪些事已裁定不做或延後 | [`development-status.md`](development-status.md) |
| 在測試環境試用這一版前要知道的事 | [`test-environment-notes.md`](test-environment-notes.md) |
| 開發環境、建置、發佈與開發用的預覽工具 | [`development-guide.md`](development-guide.md) |
| 驗證命令、結束碼與各條驗證路線 | [`../tools/README.md`](../tools/README.md) |
| CaseWare IDEA 的替代範圍與完成條件 | [`idea-replacement-scope.md`](idea-replacement-scope.md) |
| `data/` 工作簿、程式隨附範本、`legacy/` 與來源專案 | [`data-and-legacy.md`](data-and-legacy.md) |
| 業務邏輯從哪裡來、衝突時如何裁決 | [`business-logic-provenance.md`](business-logic-provenance.md) |
| SQL Server 企業環境的延後範圍與已知安全缺口 | [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md) |

## 計畫

`specs/` 放進行中的大型計畫，目前沒有。上一份是[使用者回饋與操作流程一致性修正計畫](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md)，
2026-10-05 完成交付前修正後移到歷史文件，成果待使用者驗收。接手先讀 `development-status.md` 的「目前大型計畫」。
已結束的計畫在 `history/specs/`，清單與歸屬見 [`history/README.md`](history/README.md)。

## 文件權威

- 精確程式行為以 `src/` 與仍有效的測試為準；現行文件只描述現在，不承接舊專案的驗證結論。
- 需要精確規則或遷移差異時，依 `business-logic-provenance.md` 的順序使用現行程式、測試、歷史副本與 legacy 證據。
- `history/` 與 `legacy/` 已按目前檔名正規化，能解釋來歷，但不是逐位原文；哪些地方改過寫在 `history/README.md`。
- `jet-template-v1.html` 與 `jet-template-v2.html` 是使用者裁定保留的版本，不參與一般文件重寫。

## 寫作規範

- 每份文件只處理一個主要用途。現況、操作方法、技術參考、歷史與執行紀錄分開，同一資訊只維護一處；
  不把舊專案的通過結果、逐次數字或對話紀錄當成現況。
- 用台灣常見、自然的中文寫完整句子。固定命令、程式名稱與必要術語保留英文，第一次出現時說明用途。
- 不自行創造近義詞、縮寫、階段名稱或流程名稱。一句只放一個主要意思；不用 `＋`、`／`、`→` 把名詞串成句子，
  不用兩層括號。
- 日期與計畫檔名只在讀者需要知道「這是什麼時候定的、取代了什麼」時寫一次；規則與現況直接陳述。
- 驗證框架的工作用語（receipt、gate、lane、`紅燈`、`綠燈`）只留在 `../tools/README.md` 這類技術參考；
  其他文件、計畫與 commit 訊息改說收據、檢查、路線、通過、失敗。整段改寫的步驟、檢查表與範例見
  [`../.agents/skills/jet-readable-docs/SKILL.md`](../.agents/skills/jet-readable-docs/SKILL.md)。
- 舊句子不自然時可以整段重寫；但現況必須有程式、測試、決策或本次執行紀錄支持。
- 引用或遷移他處內容時先找到原文，查核對象、檔名、數字與裁定逐字照抄；名稱有新舊對應時寫明，不默默替換。
  找不到出處的聲明刪掉或標成待確認。
- 新名詞的准入條件：程式、UI、資料結構或外部標準本來就存在的名稱，或確實會反覆使用且能用一句白話定義的概念。
  其他情況用普通中文說清楚，不替它命名。

## 文件完成前

1. 執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Documentation`。它會檢查缺檔、失效的相對連結與錨點、
   必要的指向、已確認過時的說法，並對不自然的用語給 warning。
2. 把本次改動的段落完整讀一次。每一條 warning 都要有結論：改掉，或說明為什麼保留。命令通過不代表文句自然，
   也不代表沒有形近錯字。
3. 確認現況與歷史沒有混寫；歷史副本要標示已正規化，不宣稱與來源逐位相同。
4. 確認工作簿名稱與 `data/`、程式隨附範本的實際名稱一致。
5. 改寫前後要保留數字、條件、例外、不確定性、正式名稱、schema、UI 行為與使用者裁定；資料不足就標為待確認。
6. 不修改兩份已保留的 HTML 模板。
7. 大型文件改動要讓沒參與這輪的人能用自己的話說明目的、理由與下一步。

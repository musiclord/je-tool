# JET 開發現況

這份文件回答三件事：現在做到哪裡、哪些事已經裁定不做、哪些事延後了而且什麼時候會重啟。逐輪的進度與驗證數字
不放在這裡；進行中的工作看現行計畫，已結束的看 [`history/`](history/README.md)。Branch、HEAD 與遠端同步狀態
會變動，接手時直接查 Git，或執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Context`。

## 目前狀態

產品程式、參考工作簿、現行文件、legacy 對照來源與驗證框架都在這個儲存庫；來源專案 `je-testing` 與 `new-je-tool`
不再是執行或驗證依賴。正式驗證從 `tools/verify.ps1` 進入，公開測試、封裝、桌面操作情境、原生 Excel 檢查與明示授權的
私人案件都有各自的路線；每份收據只描述當次執行，改動後要依範圍重跑。

## 目前大型計畫

[架構文件與圖表維護計畫](specs/2026-10-16-architecture-documentation-plan.md)。本輪依使用者指示先收錄既有圖表、
可編輯模型、來源證據與共用分析流程，加入選定類別關係的檢查。圖表仍以固定快照為準，下一階段交由 Claude 重繪。

[介面動態與驗證循環整理計畫](history/specs/2026-10-15-motion-and-verification-plan.md)2026-10-16 結案，移回歷史文件。
動態依 Apple 現行的 Liquid Glass 重新校準，使用者試用後確認沒有問題。Gui 全部改在另一個 Windows 桌面執行，不再干擾使用電腦。
前端測試的假後端改用正式後端的資料形狀核對，每條審計規則都在 SQLite 與 DuckDB 比對固定答案，測試時間也照使用者的裁定縮短。
結案前依使用者要求補跑 `Excel`、`Package`、`PrivateCase` 與 `Mutation`，都通過；其中 `PrivateCase` 與 `Mutation` 先修正了各自既有、讓路線無法通過的問題。
`Provider` 等使用者設定 SQL Server 連線字串後再執行；`ReleaseCandidate` 要在提交之後執行。是否提交與推送由使用者決定。
提交 `7b4f4c6` 之後，使用者同日提出第二步「操作」按鈕、第五步分錄筆數與預篩選入口三項修正，金額文字解析也補了改壞檢查；
原話與處理記在同一份計畫文末「結案後的修正（2026-10-16）」。使用者裁定這些修改與本輪架構圖表合併成單一提交。ReleaseCandidate 發現歷史文件連結失效後，使用者改為只補文件修正與檢查，再由 Codex 推送，不重跑完整驗證。

[KCT 總帳資料整理需求與新版 JET 對照](history/specs/2026-10-06-kct-gl-requirements-plan.md)已完成第九節的驗收回饋修正，移回歷史文件，成果待使用者驗收。
篩選條件改成每條只表達一件事，借貸方向用獨立條件組合；儲存流程、日期輸入與共用元件已調整，三個畫面問題也已修正。
SQLite 與 DuckDB 的固定結果比對一致。整合時 Build、三種 Contract、Public、Package、Excel 與 Documentation 都通過，
獨立複審找到的日期提示及文件缺口也已修正；最後的日期修改只重跑相關單檔測試，13 項通過。
之後 Claude 複核時，依使用者選擇補修一句過時的說明、整張傳票條件的名稱和編輯時的按鈕順序。
提交前重跑 `Public` 時，.NET 測試全部通過，但一個前端測試仍斷言日期框顯示已拿掉的格式說明；改成新行為後，
前端測試與 `Documentation` 都通過。
本輪依使用者裁定未執行 Gui，也未執行 ReleaseCandidate、PrivateCase、Provider 或 SQL Server。
4244 未被連線、清除或重建。使用者 2026-10-14 指示提交並推送，之後在測試環境繼續驗收；推送不等於驗收通過。
下一階段計畫仍等使用者指示才開始，延後事項繼續列在下方。

[使用者回饋與操作流程一致性修正計畫](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md)，
2026-10-05 完成交付前修正後移到歷史文件，狀態是待使用者驗收。它涵蓋兩份使用者回饋的修正、2026-10-01 起的整體複審、
第二遍回饋審閱，以及最後獨立複審。最後一輪依使用者 2026-10-05 的裁定修了九項交付前問題，例如 DuckDB 的完整性科目表
會漏掉空白科目那一列；每一項的評估、做法與收據寫在計畫文末「交付前修正（2026-10-05 裁定）」。
完整驗證通過後，使用者同日授權提交並推送到 `origin/main`；推送不等於驗收通過。測試環境要注意的事寫在
[測試環境交付說明](test-environment-notes.md)。使用者在測試環境驗收後，計畫才標成已完成。

## 已裁定不做的事

| 事項 | 裁定 | 重啟條件 |
|:---|:---|:---|
| 期初與期末試算表分成兩個檔案匯入（[使用者回饋與操作流程一致性修正計畫](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md)的 D06） | 2026-10-01 使用者原話：「這個目前確實先不考慮，因為會增加功能複雜性，我擔心會影響原有業務」。目前只接受單一 TB 檔，期初加期末餘額是其中一種金額模式。 | 使用者重新提出時，先確認兩期餘額的連接規則，再以固定合成答案驗證。2026-10-06 的 KCT 簡報第 5 頁描述同一個 Excel 做法；使用者看過後同日裁定「維持不做」。 |
| 科目開頭 1 到 3 與 4 到 7 分別比對 | 2026-10-01 使用者裁定以新版為準，這是舊 Excel VBA 的敘述。 | 不重啟。 |
| 從既有報告載入欄位配對草稿 | 2026-09-22 使用者表示功能尚未定案，先撤下第三步的入口。後端 action 與測試暫留。 | 使用者要求複盤時，先確認從哪份報告、在哪個案件使用、來源欄位不同時如何處理，經裁定才恢復入口。 |
| 有情境待修正時，後端查詢與匯出不另外擋下 | 2026-10-14 記為延後事項；畫面已鎖住第五步的結果，只有不經畫面的呼叫讀得到舊命中。2026-10-15 使用者在待決事項勾選頁選「照建議（維持現狀）」，備註原話：「前面已經提過了，就算完整性測試不通過，也不應該阻止審計員繼續操作條件篩選。如果有例外情況發生應該讓審計員自行記錄在底稿，而不是你提前阻擋審計員，這裡不需要那樣的防呆機制。」 | 不重啟。 |
| 重新匯入 GL 或 TB 後，案件資料夾裡的科目配對檔不重建 | 2026-10-15 盤點找到：資料庫的科目配對已依 2026-10-08 裁定清掉，但案件資料夾裡的科目配對檔仍是舊分類，下次驗證照舊保留並顯示「不改動其中已填寫的分類」；要重建時按「重建科目配對檔」。使用者同日在待決事項勾選頁選「維持現狀」。 | 使用者重新提出時。 |
| 第五步條件用到未配對的欄位，或沒有匯入假日檔時，結果是 0 筆 | 2026-10-04 使用者裁定：「C1 未配對的欄位用在條件裡時，結果默默變成 0 筆：不修」。2026-10-05 揭露方式的裁定：「只寫交付說明（建議，維持 C1）」。KCT 條件缺欄時畫面會寫出缺哪一欄；自訂條件不會，行為寫在 `jet-guide.md` 第 6 節與[測試環境交付說明](test-environment-notes.md)。 | 使用者要求時，再決定要不要在第五步加不擋的「未配對」標示。 |

## 未完成項目

| 項目 | 尚未完成的範圍 | 下一步與重啟條件 |
|:---|:---|:---|
| 私人案件的人工旗標欄驗證 | 底稿 Step 4-1 的人工旗標改成「人工」「自動」後，兩次 SQLite 與 DuckDB 的 PrivateCase 都通過，但既有私人案件清單沒有配對人工旗標，這一欄只有合成測試。使用者 2026-10-06 原話：「關於私人案件的測試請納入後續未完成的工作事項，我會在當前計畫全部處理後，接續處理」。 | 使用者 2026-10-07 裁定併入下一階段工作計畫，不併入 KCT 需求計畫。屆時需要一份有配對人工旗標的私人案件清單，再執行 PrivateCase 核對預覽與底稿的這一欄。 |
| 總帳日期在期末之後的分錄可以檢視或篩選 | 目前這些分錄不進測試母體，畫面只顯示筆數，看不到明細，也不能篩選。使用者 2026-10-05 裁定另立需求。要看到期末後多久、能不能進入條件篩選、報告與底稿怎麼呈現，都還沒有定義。 | 使用者 2026-10-07 裁定延後處理，不排進下一階段。重啟時另立計畫，先和使用者確認定義，再寫 SQLite 與 DuckDB 的固定答案。 |

## 下一階段工作計畫要納入的事項

使用者 2026-10-07 裁定下列事項不併入現行 KCT 需求計畫，等現行計畫結束後，放進下一階段的工作計畫。
2026-10-08 使用者把驗收回饋修正定為臨時插入的工作計畫，並裁定這份清單繼續延後；要等臨時插入的工作完成、
KCT 需求計畫結案，並由使用者指示開始後才執行。KCT 需求計畫已在 2026-10-14 結案歸檔，這份清單等使用者指示開始。
使用者 2026-10-15 要求整理測試、驗證循環與 Gui，其中標明「已併入」的項目已由
[介面動態與驗證循環整理計畫](history/specs/2026-10-15-motion-and-verification-plan.md)處理，其餘仍等指示：

- 私人案件的人工旗標欄驗證（上方「未完成項目」）。
- 第五步 10 月以前存下、帶有排除區的舊情境，下次儲存時排除區會被丟掉（KCT 需求計畫第八節操作測試的 F10，原本只記錄不修）。
  2026-10-14 範圍已變小：儲存別的情境時，帶排除區的舊情境照原樣保留，已有測試；只有修改那個舊情境本身時，排除區仍會被丟掉。
- KCT F 到 J 的來源核對（下方「KCT 後續條件」）。
- 驗證報告「完整性測試出現差異時之指引」工作表改寫。
- 預篩選的後續收斂、SQLite 執行中的單一語句無法取消、驗證框架瘦身、最後獨立複審留下的低嚴重度事項。
  其中驗證框架瘦身已併入介面動態與驗證循環整理計畫，處理到哪裡寫在下方「已知但延後的事項」。
- 以審計業務和使用者體驗為準，盤點為當下個案寫的技術債：同一步驟層級的事件卻分開處理，或為單一情況另開例外的程式。
  使用者 2026-10-07 在 TB 修改改成比照 GL 時提出，同日裁定「列入下一階段」。做法是先唯讀盤點並附理由與建議，經使用者裁定才修改。
  已知的一例：`filter-step.js` 開頭註解仍寫上游資料變動時「已存定義沒變」「TB 不在此集合」，和現行的清除規則不符；
  盤點時一併確認對應的快取處理是否還走得到。
- 依專案現況產生更詳細的架構圖與規格文件：至少要有 C4 的系統情境與容器圖、資料流程圖第 0 與第 1 層、UML 部署圖與元件圖、
  ERD，並呈現模組、類別與介面之間的呼叫關係；除了標準格式，也希望用 HTML 呈現。使用者 2026-10-07 在 Codex 對話提出，
  同日選「架構圖需求」，裁定列入下一階段；原話記在 KCT 需求計畫第八節「提交前的唯讀核對與文件修正」。
  使用者 2026-10-16 授權先收錄 Codex 已完成的研究圖表與分析流程，另由現行架構文件計畫接手；
  其他原定下一階段項目沒有因此一併啟動。

SQL Server 相關事項（實機驗證、企業多人環境與只經編譯的修改）使用者 2026-10-07 裁定近期不處理，不納入現行與下一階段計畫。
總帳日期在期末之後的分錄同日裁定延後處理。

## 已知但延後的事項

這些事項不阻擋目前工作，也不能因為沒有處理就從後續整理中消失。大型工作結束或使用者詢問未完成事項時，逐項回報
目前狀態、延後原因與重啟條件；問題解決後才從表中移除。

| 事項 | 背景與目前邊界 | 何時重啟 |
|:---|:---|:---|
| 驗證報告的「完整性測試出現差異時之指引」工作表改寫 | 使用者 2026-09-12 原話：「ValidationReport.xlsx的"完整性測試出現差異時之指引"工作表，內容整份要大改 (這個要跟DPP討論，以後再說)」。目前由 `LegacyReportWriter.Validation.cs` 依 legacy 範本輸出；內容屬審計指引，不由 Agent 自行改寫。 | 使用者 2026-10-07 裁定併入下一階段工作計畫。新內容仍要等使用者與 DPP 討論出來；改寫時同步更新外觀比對夾具與 `ReportArtifactExportTests`。 |
| SQL Server 實機驗證 | 使用者 2026-09-02 確認 SQL Server 開發暫緩，日常測試集中在 SQLite 與 DuckDB。既有相容性保留；一般 `ReleaseCandidate` 不要求連線，也不能代表 SQL Server 已通過。本機服務平時關閉，使用後依 [`../tools/README.md`](../tools/README.md#日常資料庫測試與服務收尾) 收尾。之後改過、只經編譯的 SQL Server 程式，以及兩項尚未實作的功能，列在下方「SQL Server 只經編譯的修改」。 | 使用者 2026-10-07 裁定近期不處理，不納入現行與下一階段計畫。重啟時須使用者當次明示要驗證，且已準備專用的 `JET_Test` 資料庫、最低必要權限與只存在於當次程序的連線資訊，再完整執行 `Provider`，並先核對下方清單。 |
| SQL Server 企業多人環境 | 四個已確認的多人安全缺口與多人共用案件、使用鎖、容量資訊的驗收範圍，記在 [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md)。只記錄，不推測實作。 | 使用者 2026-10-07 裁定近期不處理，不納入現行與下一階段計畫。之後公司能提供 SQL Server 2022、至少兩個真實帳號、DBA 支援與已核定的授權政策時，另立短期驗收計畫。 |
| 預篩選的後續收斂 | 預篩選規則目錄已凍結，逐筆結果只是輔助訊號，預篩選報告是可選輸出。報告的長期去留與條件的最終棄用清單留待另場收斂。KCT 簡報第 11 項優化問「於期末財務報表準備期間核准之分錄」是否有保留必要；使用者 2026-10-06 裁定「保留並記入預篩選收斂」，收斂時一併評估。 | 使用者 2026-10-07 裁定併入下一階段工作計畫，取代原本的「使用者要求收斂時另立計畫」；在此之前不新增逐筆預篩選規則。 |
| 操作紀錄的查詢介面、匯出與保留政策 | 本機案件已有最小的只能附加的操作紀錄（DuckDB 沒有 trigger，靠程式紀律維持）。查詢介面、匯出與保留政策屬企業部署範圍；本機刪案留痕已裁定不做。 | 隨 SQL Server 企業線一併重啟，或使用者明示要先做本機查詢介面時另立計畫。 |
| KCT 後續條件 | 目前先讓 GA 完成原 IDEA JET 的本機替代；既有 KCT A 到 J 的對應關係在 [`history/superseded/jet-guide-2026-08.md`](history/superseded/jet-guide-2026-08.md) 第 3 到第 4 節。legacy 腳本與流程圖裡查不到 KCT 的正式名稱、A 到 J 原始清單或條件 B 的分類表。2026-10-06 使用者提供 KCT 簡報：條件 A 到 E 的定義、評估說明與條件 B 的分類已整理在 [`kct-gl-workflow.md`](kct-gl-workflow.md)；F 到 J 的來源與 KCT 正式全稱仍查不到，使用者同日對 F 到 J 裁定「不確定，先維持現況」。 | A、B、C 與 A 到 E 預設理由的調整已依使用者 2026-10-06 裁定實作，紀錄見 [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)。F 到 J 等 KCT 提供完整清單時再核對；使用者 2026-10-07 裁定這項來源核對併入下一階段工作計畫。 |
| 匯入前的資料前置處理 | 審計員在把總帳交給 JET 前，會先在 Excel 或 IDEA 移除表頭表尾、刪統制科目、合併兩份總帳、用公式算科目編號、往下填補空白儲存格，並記在 CAATs 文件；JET 目前只會自動補傳票項次。使用者 2026-10-02 裁定：這些屬於 je-tool 之外的審計作業，是 IDEA 支援且審計員熟悉才留下的紀錄，短期不列入 je-tool 範疇，也不在匯入畫面加提示。整體複審時曾建議先做「略過前幾列」與「往下填補空白」兩個小功能，使用者裁定併入本項一起延後。2026-10-06 的 KCT 簡報逐步描述了同一批整理工作；使用者同日裁定「維持 2026-10-02 裁定，仍在 JET 之外」。 | 使用者認為值得納入時另立計畫。重啟時先做這兩項：匯入時指定欄名在第幾列（讀 Excel 的程式已支援）、對審計員勾選的欄位把空白補成上一列的值並在匯入摘要寫出填了幾格；其餘仍留在 Excel。 |
| SQLite 執行中的單一語句無法取消 | 2026-10-05 最後獨立複審發現：按取消後，要等目前這一條 SQL 跑完才停。子代理用同版元件實測，帶取消的長語句仍跑完 54.7 秒，改呼叫 `sqlite3_interrupt` 則 310 毫秒停下；DuckDB 正常。使用者同日裁定交付後再修，修之前先完整評估，避免影響原有功能與業務。 | 使用者 2026-10-07 裁定併入下一階段工作計畫，取代原本的「交付測試環境後」。先寫一個用真實長語句、會失敗的取消測試，再評估把取消接到 `sqlite3_interrupt` 的影響。 |
| 驗證框架瘦身 | 驗證框架（`tools` 與 `.agents`）約 2.5 萬行，`JetHarness.psm1` 有 4,611 行；`artifacts/harness/runs` 已累積 2,115 份收據、約 1.5 GB，沒有保留期限。使用者 2026-10-05 裁定另立計畫處理。收據保留期限已在 2026-10-14 依使用者選擇加上：同一種檢查只完整保留最新 10 次，更舊的只留收據摘要，本機從 3,226 次執行、2.9 GB 整理成約 225 MB，做法見 [`../tools/README.md`](../tools/README.md#結束碼)。同日量到 `tools/harness` 約 1.9 萬行；改壞檢查的資料 `artifacts/harness/mutation/` 約 7 GB，沒有上限。2026-10-15 介面動態與驗證循環整理計畫盤點過各命令，使用者裁定 `Mutation`、`ReleaseCandidate` 與 `Provider` 都保留；改壞檢查的資料依使用者選擇加上保留期限，第一次整理後約 5.1 GB，Gui 與重複的測試也依裁定縮減。`JetHarness.psm1` 本身沒有拆小。 | 使用者 2026-10-07 裁定併入下一階段工作計畫，取代原本的「使用者決定開始時另立計畫」。第一步先整理各命令的用途與可以合併的部分，不改變驗證的判定。2026-10-15 併入介面動態與驗證循環整理計畫，該計畫 2026-10-16 結案；剩下的是 `JetHarness.psm1` 的大小，等使用者決定要不要另外處理。 |
| 最後獨立複審留下的低嚴重度事項 | 2026-10-05 最後獨立複審列出的低嚴重度事項，都不阻擋交付，清單在已歸檔計畫的[「低嚴重度事項」](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md#低嚴重度事項)。分成讀檔與解析、舊資料邊界、兩個資料庫的細節、報告與底稿、刪案與錯誤處理、前端，以及日期格式化沒有指定文化設定這幾類。另有兩項回饋只部分完成：D18 的畫面仍有「完成於」等字樣，F09 的第五步前端仍把 1 到 2,958,465 的數字都當成 Excel 日期序列值；見同一份計畫的[「第三遍確認」](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md#第三遍確認d01-到-d32f01-到-f18)。交付前修正的提交前複審另提出科目配對檔用文字檔匯入時，沒有「欄位內含換行」的提醒，仍待使用者決定。GL 或 TB 有空白科目時，產生範本可能失敗的問題已處理，見 [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)第八節第三輪的 T4。 | 使用者 2026-10-07 裁定併入下一階段工作計畫。屆時挑出要修的項目，修之前照原清單逐項重新確認。 |
| 舊情境用傳票日期做日期條件時，訊息露出「voucherDate」 | 2026-10-08 獨立複審找到、Claude 判斷不阻擋交付的四項之一，細節在 [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)第八節「驗收回饋修正的複審與修正」。不影響篩選與底稿結果，尚未經使用者裁定。同時找到的另外三項是未儲存的科目分類在重新匯入、確認配對或匯入科目配對檔前沒有提示，已套用的分類被刪掉後整批儲存一直失敗，以及第六步自動補跑預篩選時不詢問未儲存的分類；使用者 2026-10-15 裁定修正，已在[介面動態與驗證循環整理計畫](history/specs/2026-10-15-motion-and-verification-plan.md)完成。 | 使用者回報舊案件訊息難懂時再修。 |
| 兩個欄位共用同一個來源欄時，確認 GL 配對可能出現系統錯誤 | 2026-10-08 研究欄位配對頁時從程式推論，尚未用測試重現。例如傳票核准日和總帳日期選同一個來源欄，確認配對時 `LegacyFieldDefinitionProjector` 會因同一個來源欄被認領兩次，丟出「來源欄位定義被重複配對」的例外，畫面只看得到系統錯誤。前端允許這樣選，後端驗證也沒有擋。這不是驗收回饋修正造成的；那一輪只在核准日的說明建議改選「與總帳日期相同」。 | 使用者決定處理時。先在 SQLite 與 DuckDB 用合成資料寫會失敗的測試：兩個欄位選同一個來源欄後確認配對，保存第一次失敗；再請使用者決定要支援共用來源欄，或改成審計員看得懂的提示。 |
| 流程總覽與刪除案件對話框同時開著時，一次 Escape 關兩層 | 2026-10-13 總覽改用共用的焦點圈限後，複審從程式推論：總覽與刪除案件對話框同時開著時，總覽先處理 Escape，刪除案件對話框也接著關閉。目前碰不到，因為刪除鈕只在案件清單上，總覽開著時蓋住它。經過見 [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)第八節「流程總覽打開時焦點沒有進入對話框」。 | 新增可以疊在總覽上的對話框，或做共用的對話框堆疊時一併處理。 |
| 本機單人案件上的多人機制 | 科目分類儲存的版本衝突、案件鎖與一次只允許一項變更作業，是為多人同時操作設計的；本機案件幾乎不會觸發，但撞到時沒有出路。使用者 2026-09-02 裁定先不動，因為 SQL Server 線仍需要它們。整體複審第 2 階段盤點後認為三者在單人本機都有出路，第 5 階段依裁定處理相關訊息。 | 出現兩個 JET 同時開同一本機案件的實際需求，或使用者在單人操作時實際撞到其中一個錯誤。 |
| 第五步「完整命中分錄」有一條實際走不到的查詢路徑 | 第五步的 `queryFilterHitsPage` 只在傳票清單的回應是假值時才會送出，但後端一律回物件或丟例外，所以實際使用時走不到。這是 2026-10-14 讀程式的推論，沒有用測試重現；刪不刪是產品決定。 | 使用者決定要不要保留這條路徑時。 |
| 既有 CS8631 警告 | 第八節記為 12 個警告；本輪 Release 建置核對為 6 處，建置摘要又各列一次。都在測試檔 `Batch7FilterDateWorkflowTests.cs` 與 `Batch8PreparationDateReadbackTests.cs`，建置與測試仍通過。第九節只調整後者的傳票測試條件，警告依本輪延後範圍保留。 | 後續修改這兩個測試檔時一併處理。 |
| 按「讀取來源值」後焦點掉到頁面本身 | 2026-10-14 正式整合時在 Gui 查到：第三步欄位配對的人工分錄代碼按「讀取來源值」後，鍵盤焦點落到頁面本身，外層有時會把代碼清單捲出畫面。Gui 已改成先找到清單再操作；產品沒有修改。 | 使用者決定處理時。先寫一個按下後檢查焦點位置的前端測試，保存第一次失敗。 |
| 其他同功能卻各做一套的前端元件 | 2026-10-14 依使用者要求盤點同功能元件時，唯讀找到但使用者沒有選進本輪的四類：尚未儲存的對話框在 `filter-step.js` 與 `account-mapping-editor.js` 各寫一份，按鈕分別寫「不儲存，直接…」與「先不儲存」；盤點時「更多操作」選單有三種做法，第九節已移除條件組的「⋯」，已儲存情境的「操作」與資料預覽的選單仍分開；展開收合混用原生 `<details>` 與自製按鈕，切換用字有「檢視、收合」「查看結果、收起結果」「展開矩陣、收合矩陣」等；頁籤有的用 `role="tab"`，第五步工作區用 `aria-pressed`。使用者選進本輪的四類與日期輸入，寫在 [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)第九節 F。 | 使用者決定處理時。先在瀏覽器主機逐一確認目前的樣子，再挑一種做法共用。 |

Agent 開發框架相關的項目（Claude Design 交接、內建瀏覽器的調整功能）不列為產品待辦。

### SQL Server 只經編譯的修改

- 2026-10-14 篩選條件改為每條只表達一件事。共用 `FilterCompilation` 在傳票條件未帶方向時判斷整張傳票，
  `GlRulePredicates.Values.AccountSide` 未帶方向時只比對分類；常用查核與 KCT E、F、G、H 改用共用欄位條件。
  舊式組合由共用驗證列為待修正，篩選版本為 `filter-2026-10-14-v20`。SQL Server 只經編譯，未連線驗證。

下列 SQL Server 程式在 SQL Server 開發暫緩後改過，都只經過編譯，沒有實機執行。批次名稱指已歸檔計畫裡的段落。

- 整體複審第 5 階段第 3、4 批改了幾處只在 SQL Server 路線執行的程式與測試，清單寫在已歸檔計畫文末整體複審的第 3 批與第 4 批，
  例如科目分類連接、預篩選 SQL 指紋、建案回滾的測試資料、開案時改版寫回情境，以及只存在伺服器上的案件要由請求帶 `sqlServer`
  才會去登錄找。
- 第 5 批之後的補充修正另加了 `config_field_mapping_previous` 資料表，以及重新匯入時保存上次確認配對的 SQL。
- 第二遍回饋審閱第 2 批把空白判定與人員比對改成走方言的 `Trim`，字元集合和 .NET 相同，並對人員不分大小寫。SQL Server 端改到：
  - `SqlDialect.cs` 的 `SqlServerDialect.Trim`，寫成 `TRIM(NCHAR(...) FROM x)`，需要相容性層級 140 以上。
  - `SqlServerGlRepository.cs` 的必填文字欄整欄空白偵測。
  - `SqlServerPrescreenRunRepository.cs` 的編製者彙總與相異人數，以及 `SqlServerCreatorSummaryExportRepository.cs`。
  - `SqlServerNullRecordsPageRepository.cs` 與 `SqlServerValidationRunRepository.cs` 的空白紀錄述詞，原本是 `LTRIM(RTRIM())`。
  - `AccountMappingEditorRepository.cs` 的 SQL Server 路徑，以及 `GlRulePredicates` 共用述詞在 SQL Server 方言下的輸出。
- 第 3 批在既有 `schema_info` 保存授權清單的原始列數、空白與重複數；`SqlServerAuthorizedPreparerRepository.cs` 與
  `AuthorizedPreparerMetadataSql.cs` 的 GL 人員比對查詢同步修改。
- 第 4 批新增 `project.update` 的 SQL Server 登錄同步，以及 `SqlServerResultStaleStateStore.InvalidateForPreparationDateChangeAsync`
  的同交易結果清除。
- 第 5 批的 `AccountMappingEditorRepository` 分類含下層查詢，以及共用 `GlRulePredicates.Values.AccountSide` 不限借貸的 SQL。
- 第 6 批同步全列錯誤彙總與 `TbMappedColumnAudit` 必填文字全空查詢。
- 第 7 批的 `FilterCompilation` 傳票量詞、`GlRulePredicates.Values` 分類 absent 與 `GlRulePredicates` 未預期借貸組合，
  都補上空白傳票號碼的排除；攸關資料元素（RDE）的多關鍵字改為單次欄值查詢，日期條件改用案件的解析選項。
- 第 8 批 `SqlServerPrescreenRunRepository` 新增完整分錄測試範圍內的相異科目數查詢，沿用既有 11 筆以下述詞，
  對應新的 `rareAccounts.lowFrequencyAccountCount`。
- 第 9 批：
  - `SqlServerProjectDatabase.Schema` 升到第 12 版，讓既有第 11 版案件補建 `config_field_mapping_previous`；已新增缺表遷移測試。
  - 共用 `ValidationProcedures.UnbalancedCore` 排除空白傳票號碼；`WorkpaperStep4StreamReader`、`TagMatrixRowPageReader.ReadWorkpaperAsync`
    與 `WorkpaperStep41PreparedSession` 補入空白號碼命中分錄。`SqlServerValidationRunRepository` 新增同號多入帳日的兩項摘要計數，
    沿共用 SQL 使用 `COUNT_BIG`。
  - 中低項目把 TB 投影、欄位資訊與配對狀態移入同一交易，驗證的 INF 樣本、有界來源品質資料與摘要也改為同一交易；修改包括
    `SqlServerTbRepository`、`SqlServerValidationRunRepository`、`SqlServerRuleRunStore`、共用 `SourceQualityPageReader` 與
    `ResultStaleStateSql`。已移除 `SqlServerImportRepository` 的單一來源舊多載，正式批次 SQL 不變；有界摘要改用共用 50 列常數。
    只存在伺服器的案件另在本機物化前核對查核期間，該分支用假登錄測試，未連線。
- 2026-10-05 交付前修正：
  - `SqlServerCompletenessAccountPageRepository.cs` 與 `SqlServerCompletenessDiffPageRepository.cs` 改用共用的
    `CompletenessAccountPageQuery`，先依固定順序給每列序號，換頁只比較序號。
  - `GlProjectionDataReader.cs` 與 `SqlServerGlRepository.cs` 加上「只列一側」的人工或自動代碼在來源裡一筆都沒有時的提醒。

- 2026-10-06：`SqlServerDataPreviewRepository` 的有效分錄預覽新增人工或自動欄，三種資料庫共用欄序。既有 SQL Server 測試已同步九欄及人工、自動值，尚未連線執行。
- 2026-10-06：科目配對預覽改讀已存配對，與本機共用讀取方法；不再用新版欄名判斷器解讀舊匯入批次。SQL Server 尚未連線驗證。

- 2026-10-06：新增 `SqlServerAccountMappingDifferenceRepository`，匯入回兩項差異筆數，差異科目使用有界分頁。
  與本機實作共用 SQL，採完整性比對的有效 GL 與 TB 科目聯集，並排除空白科目編號；SQL Server 的科目配對清單
  也改用同一份 `AccountMappingPopulationQuery`。只經編譯，未連線驗證。
- 2026-10-06 第三輪：`SqlServerAccountMappingExportRepository` 產生空白科目配對範本時改用同一份科目母體，不再列出空白編號；
  `SqlServerDataPreviewRepository` 的人工或自動欄改呼叫與本機共用的轉換；舊結構案件升級補記「分類有沒有填」時，
  SQL Server 也改用和已存分類一致的來源欄判斷。三種資料庫共用的差異清單查詢，第一頁總數改用純量子查詢，
  SQL Server 照舊用 `COUNT_BIG`。這四項都只經編譯，未連線驗證。

- 第三輪複核提出、第四輪補記：SQL Server 的空白科目配對範本查詢尚無防止退回舊寫法的測試；跨資料庫範本比對的合成資料也未含空白科目編號。重新啟用時先補這兩項，再連線驗證。
- 差異清單第一頁的總數會另計一次科目母體。SQL Server 預設隔離設定下，同一語句不保證清單與總數讀到同一份資料；本輪只記錄限制，未連線驗證。
- 2026-10-07 上游修改清除下游：共用的 `RuleRunResultReset` 多刪 `config_filter_scenario`，`ResultStaleStateSql` 在情境清除時把篩選標回從未執行，
  `SqlServerAccountTaxonomyStore` 不再因情境使用分類而擋刪除。同日收尾時，`SqlServerResultStaleStateStore` 改成存好財報準備日設定才提交清除，
  `SqlServerProjectDatabase.Schema` 的第 7 版升級拿掉已無作用的情境改寫。SQL Server 路徑都帶 schema 前綴，只經編譯，未連線驗證。
  `SqlServerSchemaMigrationTests` 仍預期第 7 版升級後情境保留、篩選標成待重跑，和本機版測試及產品行為不符；
  下次執行 `Provider` 時預期先失敗一次，再改成情境清空、篩選回到從未執行。
- 2026-10-07 TB 比照 GL：失效政策由三種資料庫共用，`SqlServerImportRepository` 的 TB 匯入與 `SqlServerTbRepository` 的
  TB 配對確認，現在也清除驗證、預篩選、篩選命中與全部情境。SQL Server 程式本身沒改，行為隨共用政策改變。
  下列 SQL Server 測試的共用預期已跟著修改，但沒有執行：
  `CompletenessBackendGateProviderTests.ReimportedTb_ClearsPrescreenRunAndRejectsPrescreenReport_SqlServer`、
  `ResultInvalidationSqlServerTests` 經 `ResultInvalidationTestSupport` 讀到的 TB 三列，以及
  `ResultStaleStateLifecycleTests.SqlServer_ResultLifecycle_PreservesExactStaleTransitions` 的 TB 步驟。未連線驗證。
- 2026-10-08 驗收回饋修正：移除傳票日期時，`SqlServerProjectDatabase.Schema` 的 GL 建表與舊版升級拿掉 `voucher_date`，
  `GlProjectionDataReader` 少一欄、後面的欄位往前移，`SqlServerPrescreenRunRepository` 拿掉回溯過帳的計數查詢。
  資料預覽改名時，`SqlServerDataPreviewRepository` 改用新的資料集代號，SQL 沒有變。`SqlServerGlRepositoryTests` 不需要連線，
  已在 Public 執行並通過；其餘只經編譯，未連線驗證。
- 2026-10-08 Codex 後續註解：共用 `RuleRunResultReset` 在 GL、TB 匯入及配對確認時，多清除科目配對、原始配對列及匯入狀態，保留分類定義。
  `GlRowProjector` 同時支援單邊借貸代碼的補集判定。SQL Server 行為隨共用程式改變，只經編譯，未執行 Provider 或連線驗證。
- 2026-10-13 訊息分三級：`SqlServerProjectDatabase.Schema` 的 `app_message_log.level` 限制加上 `error`，版本升到第 13 版。
  既有案件由第 12 版升級時，從系統目錄找出建表時未命名的等級限制再換掉。只經編譯，未連線驗證。
  `SqlServerSchemaMigrationTests` 裡要連 SQL Server 的版本預期已跟著改成第 13 版，但沒有執行；不需連線的版本常數測試已通過。
- 2026-10-13 第五步一次只存或刪一個情境：新增的 `filter.scenario.save`、`filter.scenario.remove` 與 `filter.scenario.recheck`
  在 SQL Server 案件沿用既有的 `SqlServerFilterScenarioStore.ReplaceAllAsync` 與 `SqlServerFilterCommitRepository.CommitAsync`，
  三種資料庫共用的 `FilterFactsPort` 多認這三個動作名稱，沒有新增 SQL。只經編譯，未連線驗證。
- 2026-10-14 第三輪訊息文字：未設定連線、Express 版、死結、登入失敗、重複資料、逾時、同名案件、找不到案件與損壞的抽樣設定，
  改成使用者確認的句子。改到 `SqlServerAppConfigStore`、`SqlServerControlPlaneReconciler`、`SqlServerLockService`、`SqlServerUserDirectory`、
  `SqlServerProjectDatabase` 與 `SqlServerProjectRegistry` 的連線字串建立，`SqlServerProjectDatabase.Readiness` 的
  `EnsureSingleDatabaseAndControlPlaneAsync`，`SqlServerEngineErrors.TranslateSqlException`（重複資料不再帶資料庫引擎的原文），
  `SqlServerProjectDatabase.CreateRollback` 的 `BeginCreateProjectOnceAsync`，`SqlServerProjectRegistry` 的 `UpdateDocumentOnceAsync` 與
  `ReadRegistered`，`SqlServerImportRepository.AppendToBatchAsync`，以及會走到 SQL Server 的 `ProjectLoadHandler` 與 `CaseCreateFactsPort`。
  `DualSourceProjectTests` 只經編譯，沒有執行。全部未連線驗證。
- 2026-10-16 第五步分錄筆數改成和條件篩選報告同一口徑：`SqlServerFilterRunRepository.CountWholeVoucherRowsAsync`、
  `SqlServerTagMatrixScenariosRepository.GetWholeVoucherRowCountsAsync`（共用 `TagMatrixScenarioCounts`，用 `COUNT_BIG(*)`），
  以及 `SqlServerFilterVoucherRepository` 轉給共用 `FilterVoucherPageReader` 的整張傳票計數參數。只經編譯，未連線驗證。

另有兩項尚未實作，不只是未驗證：SQL Server 的值概況仍區分大小寫，清單代碼的完整來源存在性核對也未支援。前端會明示尚未核對；
重啟這個資料庫時，須先補齊 L32 與 Q5 的 Unicode 等價查詢，再跑固定答案。`ValidationRunLoggingTests` 與 `PrescreenRunLoggingTests`
的 SQL Server digest 仍是舊值，下次執行 `Provider` 時預期先失敗一次，再依實際 SQL 更新。

## 已知技術債

修到相關區域時要知道它們存在，不阻擋日常開發：

- DuckDB 1.5.3 遇到不帶排序的 `COUNT(*) OVER ()` 疊在「先分組、再接 NOT EXISTS」的查詢上，目前觀察到會偶發內部錯誤，
  並讓同一案件資料庫的其他連線一起失效；依序執行也曾重現，完整觸發條件仍未確認。2026-10-06 科目差異清單已改用純量子查詢避開，經過與實驗記在
  [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)第八節。新的分頁總數不要用這種視窗寫法；升級 DuckDB 時可依計畫紀錄重測。
  第三步完整性科目表的查詢形狀相近，目前沒有證據顯示會出同樣的錯；升級時一併重測，不據此宣稱已有故障。

- [架構圖表](architecture/README.md)已收錄 2026-10-15 與 2026-10-16 的研究快照，尚未反映之後的產品修改。
  重繪與補齊標準圖種由現行架構文件計畫追蹤。2026-08-30 舊圖仍留在
  [`history/architecture-2026-08-30/`](history/architecture-2026-08-30/README.md)；系統分層文字見 `jet-guide.md` 第 9 節。
- `history/specs/2026-06-21-low-frequency-account-escalation-design.md` 開頭有疑似真實案件識別內容；依規則停止擴大讀取，
  未複製也未清理。使用者 2026-10-03 裁定不處理。
- 預篩選報告的欄位來源忠實性裁決只涵蓋 R2 明細家族與 R6；R5 的彙總頁標題維持既有輸出名稱，要延伸同樣規則需另行裁決。
- CSV 欄名與資料讀取會重做一次格式偵測；只有實測成為瓶頸時才優化。
- 大型本機案件使用較多磁碟空間與 WAL；不能把母體載入記憶體換速度。
- `filter-step.js` 與 `Invoke-JetHarness` 的大檔拆分，以及 SQL Server 與本機倉儲的平行 SQL 去重，依已歸檔計畫第 9 批的裁定另立計畫，不和行為修正混做。
- 變異測試工具的批次編譯回復，會讓 MoneyScaling 的五個有效變異未被完整執行；已用獨立編譯與固定案例補證，
  上游改善後再重跑同一範圍。
- 前代的大檔煙霧測試（114 MB PBC fixture）沒有遷入；日後需要時依現行框架另立路線。
- `SupportRingBufferLogger` 以寫死的字串排除自身事件；日後若有其他要排除的 action，應集中到 action 分類表管理。
- 工作底稿版本檔會一直累積，由使用者自行刪檔；報告清單滿 4,096 筆時只移除已確認刪檔的紀錄。更大的容量需求另行量測。
- `ProjectWorkFileWriter` 與 `ProjectReportArtifactStore` 各自處理暫存檔與改名，責任不同，不能直接合併；共用底層留待維護時評估。
- 效能觀察（未量測）：每次 action 分派為支援日誌配置一次字典；匯出診斷日誌時對整個檔案逐行解析只為讀案件識別值；
  欄位配對每次選擇即整面板重建。只在實測成為瓶頸時處理。
- `ValidationProcedures.Evaluate` 正式程式不呼叫，只有測試在用；整體複審刪除只有測試在用的外殼時，它不在盤點範圍，
  刪除前先確認測試要改由哪個正式入口承接。
- 篩選情境上限 10 已收成 `FilterScenarioLimits.MaxSavedScenarios`，但底稿寫出、條件篩選報告與 SQL 範圍檢查裡還有五處
  寫死的 10，可能與底稿範本的欄位數綁在一起；調整上限前要一起核對範本。
- 2026-10-07 完整 Gui 曾兩次在既有情境偶發失敗：`filter-auditor-journey` 新增條件後的焦點與可見位置檢查逾時，
  `feedback-workflow` 的科目拖曳選取檢查失敗。兩者單獨重跑都通過，之後完整 Gui 也通過；原因未確認，
  沒有因此修改產品、放寬斷言或延長等待。驅動程式已補記作用中元素、條件列位置、科目選取與捲動位置，再發生時先看這些診斷欄位。
  經過與收據記在 [KCT 需求計畫](history/specs/2026-10-06-kct-gl-requirements-plan.md)第八節「TB 規則與使用情境補測收尾」。

## Git 交付

- 是否暫存、提交或推送由使用者另外明示；人工驗收通過或計畫完成都不是授權。
- 使用者 2026-09-07 確認 `.vscode/settings.json` 不同步到儲存庫；來源儲存庫的封存與舊本機目錄由使用者自己處理。
- 最近一次授權的提交與推送是 2026-10-16 的 `7b4f4c6`，內容是介面動態與驗證循環整理計畫。再前一次是 2026-10-14，
  內容是 KCT 需求計畫的實作、第九節的驗收回饋修正與複核後補修；推送後由使用者在測試環境驗收。

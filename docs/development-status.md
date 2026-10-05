# JET 開發現況

這份文件回答三件事：現在做到哪裡、哪些事已經裁定不做、哪些事延後了而且什麼時候會重啟。逐輪的進度與驗證數字
不放在這裡；進行中的工作看現行計畫，已結束的看 [`history/`](history/README.md)。Branch、HEAD 與遠端同步狀態
會變動，接手時直接查 Git，或執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Context`。

## 目前狀態

產品程式、參考工作簿、現行文件、legacy 對照來源與驗證框架都在這個儲存庫；來源專案 `je-testing` 與 `new-je-tool`
不再是執行或驗證依賴。正式驗證從 `tools/verify.ps1` 進入，公開測試、封裝、桌面操作情境、原生 Excel 檢查與明示授權的
私人案件都有各自的路線；每份收據只描述當次執行，改動後要依範圍重跑。

## 目前大型計畫

無。

上一份計畫是[使用者回饋與操作流程一致性修正計畫](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md)，
2026-10-05 完成交付前修正後移到歷史文件，狀態是待使用者驗收。它涵蓋兩份使用者回饋的修正、2026-10-01 起的整體複審、
第二遍回饋審閱，以及最後獨立複審。最後一輪依使用者 2026-10-05 的裁定修了九項交付前問題，例如 DuckDB 的完整性科目表
會漏掉空白科目那一列；每一項的評估、做法與收據寫在計畫文末「交付前修正（2026-10-05 裁定）」。
完整驗證通過後，使用者同日授權提交並推送到 `origin/main`；推送不等於驗收通過。測試環境要注意的事寫在
[測試環境交付說明](test-environment-notes.md)。使用者在測試環境驗收後，計畫才標成已完成。

## 已裁定不做的事

| 事項 | 裁定 | 重啟條件 |
|:---|:---|:---|
| 期初與期末試算表分成兩個檔案匯入（前一份計畫的 D06） | 2026-10-01 使用者原話：「這個目前確實先不考慮，因為會增加功能複雜性，我擔心會影響原有業務」。目前只接受單一 TB 檔，期初加期末餘額是其中一種金額模式。 | 使用者重新提出時，先確認兩期餘額的連接規則，再以固定合成答案驗證。 |
| 科目開頭 1 到 3 與 4 到 7 分別比對 | 2026-10-01 使用者裁定以新版為準，這是舊 Excel VBA 的敘述。 | 不重啟。 |
| 從既有報告載入欄位配對草稿 | 2026-09-22 使用者表示功能尚未定案，先撤下第三步的入口。後端 action 與測試暫留。 | 使用者要求複盤時，先確認從哪份報告、在哪個案件使用、來源欄位不同時如何處理，經裁定才恢復入口。 |
| 第五步條件用到未配對的欄位，或沒有匯入假日檔時，結果是 0 筆 | 2026-10-04 使用者裁定：「C1 未配對的欄位用在條件裡時，結果默默變成 0 筆：不修」。2026-10-05 揭露方式的裁定：「只寫交付說明（建議，維持 C1）」。KCT 條件缺欄時畫面會寫出缺哪一欄；自訂條件不會，行為寫在 `jet-guide.md` 第 6 節與[測試環境交付說明](test-environment-notes.md)。 | 使用者要求時，再決定要不要在第五步加不擋的「未配對」標示。 |

## 未完成項目

| 項目 | 尚未完成的範圍 | 下一步與重啟條件 |
|:---|:---|:---|
| 特定原始文字檔無法匯入總帳（前一份計畫的 D07） | 共用的診斷與失敗重試已修，但沒有用原檔確認原因，不能說原問題已解決；也沒有證實它與 2026-09-14 的 PBC 匯入失敗是同一來源。2026-10-04 第二遍審閱另發現，文字檔欄位中間出現一個雙引號時，後面的資料列會被默默併掉；同日計畫第 1 批已改成一般 CSV 讀法、略過開頭空白列、單一欄提醒，檢視與預覽失敗也寫入支援日誌。2026-10-05 交付前修正後，自動偵測編碼時檔案前段就解碼失敗，支援日誌也寫出實際用來解碼的編碼；欄位用引號包住而內含換行的資料，匯入時會提醒筆數與前五個列號。這些是否就是原檔的原因，沒有讀原檔，無法確認。 | 交付測試環境後由使用者用原檔操作驗收，再依支援日誌判讀。 |
| 2026-09-14 原始 PBC 匯入失敗的原因 | 舊日誌只定位到本機批次取代流程，原檔的觸發原因仍不明。 | 保留為尚未與 D07 建立對應的舊問題；不因共用流程修正就標成已修復。 |
| GUI 檢查的視窗不最大化、不搶前景 | 使用者 2026-10-05 原話：「另外，在 `je-tool` 進行測試驗證時，時常會發生跟我搶滑鼠的問題，這非常麻煩，請研究是否有更好的測試循環同時不會干擾我使用電腦?」同日裁定：「先照第1點去做，而第2及第3點則移至未完成的事項，後續我將會繼續請你做可行性測試」。第 1 點是改變做法，已寫進 `AGENTS.md`「測試」；這一列是第 2 點，下一列是第 3 點。目前 `AgentGuiTest` 組態在 `Form1.cs` 把視窗設成最大化，新開的程序又會取得前景。做法是只在這個測試組態改成一般大小、開啟時不取得前景。還沒實測：有幾個情境檢查輸入焦點與中文組字，視窗沒有前景時可能失敗。 | 使用者要求做可行性測試時。先只改 `AgentGuiTest` 組態的視窗設定，完整跑一次 `Gui`，逐一比對 17 個情境；有情境失敗就記下原因，不放寬斷言。 |
| GUI 檢查改在另一個 Windows 桌面執行 | 依同一次裁定記下。做法是由 GUI 驅動程式另建一個 Windows 桌面，把 JET 開在那裡，使用者的螢幕完全看不到。還不確定 WebView2 在沒有顯示出來的桌面上，能不能正常繪製畫面與截圖。 | 使用者要求做可行性測試時。先寫一個最小的試驗：在另一個桌面只跑 `startup-smoke` 情境，確認頁面載入、操作與截圖都正常，再決定要不要改整條路線。 |
| 總帳入帳日在期末之後的分錄可以檢視或篩選 | 目前這些分錄不進測試母體，畫面只顯示筆數，看不到明細，也不能篩選。使用者 2026-10-05 裁定另立需求。要看到期末後多久、能不能進入條件篩選、報告與底稿怎麼呈現，都還沒有定義。 | 交付測試環境後另立計畫。先和使用者確認定義，再寫 SQLite 與 DuckDB 的固定答案。 |

## 已知但延後的事項

這些事項不阻擋目前工作，也不能因為沒有處理就從後續整理中消失。大型工作結束或使用者詢問未完成事項時，逐項回報
目前狀態、延後原因與重啟條件；問題解決後才從表中移除。

| 事項 | 背景與目前邊界 | 何時重啟 |
|:---|:---|:---|
| 驗證報告的「完整性測試出現差異時之指引」工作表改寫 | 使用者 2026-09-12 原話：「ValidationReport.xlsx的"完整性測試出現差異時之指引"工作表，內容整份要大改 (這個要跟DPP討論，以後再說)」。目前由 `LegacyReportWriter.Validation.cs` 依 legacy 範本輸出；內容屬審計指引，不由 Agent 自行改寫。 | 使用者與 DPP 討論出新內容後另立短期計畫；改寫時同步更新外觀比對夾具與 `ReportArtifactExportTests`。 |
| SQL Server 實機驗證 | 使用者 2026-09-02 確認 SQL Server 開發暫緩，日常測試集中在 SQLite 與 DuckDB。既有相容性保留；一般 `ReleaseCandidate` 不要求連線，也不能代表 SQL Server 已通過。本機服務平時關閉，使用後依 [`../tools/README.md`](../tools/README.md#日常資料庫測試與服務收尾) 收尾。之後改過、只經編譯的 SQL Server 程式，以及兩項尚未實作的功能，列在下方「SQL Server 只經編譯的修改」。 | 使用者當次明示要驗證，且已準備專用的 `JET_Test` 資料庫、最低必要權限與只存在於當次程序的連線資訊時，完整執行 `Provider`，並先核對下方清單。 |
| SQL Server 企業多人環境 | 四個已確認的多人安全缺口與多人共用案件、使用鎖、容量資訊的驗收範圍，記在 [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md)。只記錄，不推測實作。 | 公司能提供 SQL Server 2022、至少兩個真實帳號、DBA 支援與已核定的授權政策時，另立短期驗收計畫。 |
| 預篩選的後續收斂 | 預篩選規則目錄已凍結，逐筆結果只是輔助訊號，預篩選報告是可選輸出。報告的長期去留與條件的最終棄用清單留待另場收斂。 | 使用者要求收斂時另立計畫；在此之前不新增逐筆預篩選規則。 |
| 操作紀錄的查詢介面、匯出與保留政策 | 本機案件已有最小的只能附加的操作紀錄（DuckDB 沒有 trigger，靠程式紀律維持）。查詢介面、匯出與保留政策屬企業部署範圍；本機刪案留痕已裁定不做。 | 隨 SQL Server 企業線一併重啟，或使用者明示要先做本機查詢介面時另立計畫。 |
| KCT 後續條件 | 目前先讓 GA 完成原 IDEA JET 的本機替代；既有 KCT A 到 J 的行為保留，對應關係在 [`history/superseded/jet-guide-2026-08.md`](history/superseded/jet-guide-2026-08.md) 第 3 到第 4 節。legacy 腳本與流程圖裡查不到 KCT 的正式名稱、A 到 J 原始清單或條件 B 的分類表。 | 使用者或 KCT 小組提供正式來源與條件清單後再立計畫；不先推測實作。 |
| 匯入前的資料前置處理 | 審計員在把總帳交給 JET 前，會先在 Excel 或 IDEA 移除表頭表尾、刪統制科目、合併兩份總帳、用公式算科目編號、往下填補空白儲存格，並記在 CAATs 文件；JET 目前只會自動補傳票項次。使用者 2026-10-02 裁定：這些屬於 je-tool 之外的審計作業，是 IDEA 支援且審計員熟悉才留下的紀錄，短期不列入 je-tool 範疇，也不在匯入畫面加提示。整體複審時曾建議先做「略過前幾列」與「往下填補空白」兩個小功能，使用者裁定併入本項一起延後。 | 使用者認為值得納入時另立計畫。重啟時先做這兩項：匯入時指定欄名在第幾列（讀 Excel 的程式已支援）、對審計員勾選的欄位把空白補成上一列的值並在匯入摘要寫出填了幾格；其餘仍留在 Excel。 |
| SQLite 執行中的單一語句無法取消 | 2026-10-05 最後獨立複審發現：按取消後，要等目前這一條 SQL 跑完才停。子代理用同版元件實測，帶取消的長語句仍跑完 54.7 秒，改呼叫 `sqlite3_interrupt` 則 310 毫秒停下；DuckDB 正常。使用者同日裁定交付後再修，修之前先完整評估，避免影響原有功能與業務。 | 交付測試環境後。先寫一個用真實長語句、會失敗的取消測試，再評估把取消接到 `sqlite3_interrupt` 的影響。 |
| 驗證框架瘦身與收據保留期限 | 驗證框架（`tools` 與 `.agents`）約 2.5 萬行，`JetHarness.psm1` 有 4,611 行；`artifacts/harness/runs` 已累積 2,115 份收據、約 1.5 GB，沒有保留期限。使用者 2026-10-05 裁定另立計畫處理。 | 上一份計畫已在 2026-10-05 移到歷史文件，使用者決定開始時另立計畫。第一步先整理各命令的用途與可以合併的部分，不改變驗證的判定。 |
| 最後獨立複審留下的低嚴重度事項 | 2026-10-05 最後獨立複審列出的低嚴重度事項，都不阻擋交付，清單在已歸檔計畫的[「低嚴重度事項」](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md#低嚴重度事項)。分成讀檔與解析、舊資料邊界、兩個資料庫的細節、報告與底稿、刪案與錯誤處理、前端，以及日期格式化沒有指定文化設定這幾類。另有兩項回饋只部分完成：D18 的畫面仍有「完成於」等字樣，F09 的第五步前端仍把 1 到 2,958,465 的數字都當成 Excel 日期序列值；見同一份計畫的[「第三遍確認」](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md#第三遍確認d01-到-d32f01-到-f18)。交付前修正的提交前複審另提出兩項，等使用者決定：科目配對檔用文字檔匯入時，沒有「欄位內含換行」的提醒；GL 或 TB 有空白科目時，產生科目配對範本可能失敗，這從 HEAD 就存在。 | 使用者測試環境驗收後，挑出要修的項目另立計畫；修之前照原清單逐項重新確認。 |
| 本機單人案件上的多人機制 | 科目分類儲存的版本衝突、案件鎖與一次只允許一項變更作業，是為多人同時操作設計的；本機案件幾乎不會觸發，但撞到時沒有出路。使用者 2026-09-02 裁定先不動，因為 SQL Server 線仍需要它們。整體複審第 2 階段盤點後認為三者在單人本機都有出路，第 5 階段依裁定處理相關訊息。 | 出現兩個 JET 同時開同一本機案件的實際需求，或使用者在單人操作時實際撞到其中一個錯誤。 |

Agent 開發框架相關的項目（Claude Design 交接、內建瀏覽器的調整功能）不列為產品待辦。

### SQL Server 只經編譯的修改

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

另有兩項尚未實作，不只是未驗證：SQL Server 的值概況仍區分大小寫，清單代碼的完整來源存在性核對也未支援。前端會明示尚未核對；
重啟這個資料庫時，須先補齊 L32 與 Q5 的 Unicode 等價查詢，再跑固定答案。`ValidationRunLoggingTests` 與 `PrescreenRunLoggingTests`
的 SQL Server digest 仍是舊值，下次執行 `Provider` 時預期先失敗一次，再依實際 SQL 更新。

## 已知技術債

修到相關區域時要知道它們存在，不阻擋日常開發：

- 架構圖只反映 2026-08-30 的程式，已移到 [`history/architecture-2026-08-30/`](history/architecture-2026-08-30/README.md)。
  需要呈現最新模組時，先核對來源再重畫；系統分層的文字說明在 `jet-guide.md` 第 9 節。
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

## Git 交付

- 是否暫存、提交或推送由使用者另外明示；人工驗收通過或計畫完成都不是授權。
- 使用者 2026-09-07 確認 `.vscode/settings.json` 不同步到儲存庫；來源儲存庫的封存與舊本機目錄由使用者自己處理。
- 最近一次授權的提交與推送是 2026-10-05，內容是整體複審、第二遍回饋修正與交付前修正；推送後由使用者在測試環境驗收。

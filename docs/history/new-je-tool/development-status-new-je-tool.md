# JET 開發現況

更新日期：2026-08-26

## 一眼看懂

| 項目 | 目前狀態 |
|:---|:---|
| 六步驟審計流程 | 已實作；SQLite、DuckDB、SQL Server 皆有自動測試 |
| 六份正式 Excel 報表 | 已實作並於 2026-08-26 完成人工複驗；六份輸出字族統一為微軟正黑體，Validation／Working Paper 欄位資訊頁共用無框線可見區，Working Paper step1-2 人工格維持未鎖定且無提示底色，step4-1 整張工作表（含資料外預格式區）不套用黑色框線 |
| SQLite／DuckDB 本地專案可攜性 | 已實作並受測；來源 JET 正常離開案件後，可把完整案件資料夾搬到另一個本機固定／可移除磁碟根或另一台電腦，重新開啟後案件、進度、latest runs、查詢與正式報告可直接續作。projects root 不進持久資料；匯入來源只留 leaf filename，呼叫端即使把 `fileName` 傳成路徑也會在 Application 與本地 repository 邊界正規化，既有絕對 `source_file_path` 開案時不升 schema 地收斂；artifact manifest 只接受 direct-child 相對檔名。WAL 使用中的熱複製與 SQL Server 案件不在此保證 |
| 最小資料面 audit log | 已實作；schema v8 的 project-local append-only 表記錄 GL／TB 重匯入、mapping recommit、正式報表發布／汰換與明示清理，三 provider 共用語意；不含查詢 UI／匯出／保留政策。schema v9 起 append-only 由資料庫層強制：SQLite 與 SQL Server 以 trigger 拒絕 UPDATE／DELETE，DuckDB 1.5.3 沒有 trigger 也沒有 table-level 權限，維持程式紀律並有來源守衛與引擎能力測試。SQLite／DuckDB 刪案留痕經 2026-08-20 裁決不做（本地案件性質不需持久刪案證據）；SQL Server 既有 `dbo.audit_log` 刪案紀錄維持 |
| Legacy parity | 2026-08-15「跨案件 PrivateParity 純驗證封板」以全新批次完成：`case-A`／`case-B` 全部可用 legacy／IDEA reference 底稿經 metric、normalized content 與外觀三層 fresh 比較，JET 缺陷 0、provider mismatch 0、未解釋差異 0；歷史差異裁決未沿用成白名單，每項差異由獨立 oracle 重算重分類。未提供的公司案件仍未在本機復現、未驗證 |
| 真實案件端到端自動驗收 | 五階段的自動驗證、整合收尾與人工一場制驗收均已完成。第五項先後發現的四個輸出樣式問題均已修補，包含 Working Paper step4-1 全頁無黑框；修補後 Fast 為 1 provider／12 次 Excel，fresh Full 先完成全套候選 gate，再以 6 provider runs／24 次 Excel 驗證 `case-A`。兩層皆為 JET external workbook links 0、unknown／JET defect／provider mismatch 0，私人 artifact 已清理；使用者其後明示剩餘第三與第五項全數通過 |
| 防案件特化稽核 | 未找到依案件代號分支或直接固定內容答案。`R6` 固定欄位 alias 已於 2026-08-14「底稿欄位來源忠實性 synthetic 修正」階段移除，改沿用 committed GL mapping 的來源欄名；synthetic regression 以任意合法來源欄名證明不再對已知輸出調答案 |
| Pre-screening Report 欄位忠實性 | 2026-08-14 synthetic 修正落地後，2026-08-15 已由跨案件 PrivateParity 完成兩案驗證：明細頁欄位集合為 GL 匯入批次完整欄位目錄（實際來源欄名與 ordinal），`R6` 前兩欄沿用配對來源欄名；legacy 明細頁的 IDEA 改名／衍生欄以欄名對齊＋值多重集配對證明共有欄值等值，欄位目錄差異分類為舊系統匯出資料條件。規則正本在 `jet-guide.md` §7.1。未提供的公司案件仍不進 repository、未在本機復現 |
| Working Paper／前端驗收 | 使用者已於 2026-08-12 回報十二項全過，正式銷帳 |
| 流程總覽狀態檢閱 | 已落地；六階段只切換 modal 內執行紀錄，不再複製主畫面的流程導航或在卡片、紀錄、母體間重述同一事實。使用者已於 2026-08-13 完成人工驗收，六項全過 |
| 核心能力補完與測試 Harness 現代化 | **計畫已於 2026-08-15 結案**：十四個階段完成、使用者最終一場制人工驗收五項全過；PBC envelope 裁決 (b) 的 harness 修正同日落地（skip registry 增 `PrivateParity` 段、envelope 只紅未登錄 skip、`verify.ps1` 環境變數改 process 傳遞），實機 `-Profile PrivateParity` 已誠實轉綠 |
| 現行公開契約與審計模型 | 已落地 schema v9、mapping metadata v2、project-scoped 科目 taxonomy、posting policy、唯一有效分錄母體、核准日三態、嚴格 projection quality、typed RDE 與 pair 多選。Validation v4 另回 `populationSummary`／`sourceQuality`、eligible-source 對 effective-target Part A 並對 current summary 嚴格 fail closed；filter v10 與 INF page 回 backend column metadata／`customValues`。六份正式報表共用 `VeryHidden JET_Metadata`；INF 輸出全部 committed RDE、Criteria 輸出全 revision 引用 union、Working Paper 輸出所選 positions union；prescreen 現行 v6。taxonomy、pair 多選、typed filter、source-quality／population 與 dynamic result columns 的 runtime UI 已於 2026-08-15 全部接線。2026-08-18 業務模型與產品 gate 已對齊：預篩選定位為輔助訊號，KCT A–J 為實務主線；兩者都直接讀同一有效 GL 母體，filter 不依賴既有 prescreen run 的命中結果。Step 3 完成與 Step 4 進入只要求後端完整性適格；Pre-screening Report 維持 prescreen provenance，Criteria Selection Report 與 Working Paper 已解除 prescreen 前置，只綁 validation 與 filter；19 個條件型別顯示名以 Domain `FilterConditionLabels` 為正本 |

Legacy parity 的舊完成計畫已依文件規則移出工作樹；五類差異登錄與三 provider 一致性硬規則已回流 `jet-guide.md`，重要結論保留於 `development-log.md`。新的核心能力計畫不改寫舊 receipts；2026-08-14 重排後，泛化缺陷由「底稿欄位來源忠實性 synthetic 修正」階段修正，再由序列最後的「跨案件 PrivateParity 純驗證封板」重新檢驗。

## 目前進行中的開發計畫

目前沒有進行中的已核准開發計畫。2026-08-26「真實案件端到端自動驗收」與「默認輸出對齊、本地可攜性與 UX 收斂」均已完成人工驗收、對抗複審與文件回流；`windows-handoff.md` 沒有待辦，也沒有唯一可開始的下一階段。

前一計畫「預篩選重定位與小案收斂」四個線性階段已於 2026-08-20 全部完成並通過最終一場制人工驗收：步驟四以彙總分析為主、逐筆命中明示為輔助訊號；Pre-screening Report 保留完整內容但改為純可選輸出；Criteria Selection Report 與 Working Paper 不再要求預篩選；schema v8 最小 audit log、兩份固定範本 external-workbook 衛生例外，以及五個右側頁籤顯示名均已落地。

前一計畫「業務模型收斂與全面盤查清理」五個階段已於 2026-08-18 全部完成並通過最終人工驗收。

前一計畫「核心能力補完與測試 Harness 現代化」已於 2026-08-15 結案：十四個開發階段全部完成，使用者同日完成最終一場制人工驗收（五項全過），PBC envelope 裁決 (b) 的 harness 修正 packet 也已落地並驗證。計畫的 master spec 依文件規則移出 `docs/specs/`，各階段 receipts 由 Git 歷史保存；現行行為以 `jet-guide.md`、`action-contract-manifest.md`、`jet-frontend-description.md` 與本檔為準。

跨案件 PrivateParity 驗證的最終結論（2026-08-15）：以全新 `crosscase-pure-verification` 批次對兩案各跑 SQLite／DuckDB／SQL Server 完整 action journey 各兩次（14 個 observations），metric 層 241 筆分類差異 JET 缺陷 0、provider mismatch 0、未分類 0；normalized content 145 筆與外觀 97,221 筆差異全數解析到封閉裁決、未知 0；provider 內容一致性 60 組比較 mismatch 0。未提供的公司案件仍未在本機復現、未驗證。

另依使用者 2026-08-15 常設授權，agent 可本機唯讀存取 `data/temporary-test-case/` 做產出驗收與 `legacy/idea-tool.bas` 業務邏輯一致性比對，無需逐案核准；條文見 `AGENTS.md` 機敏資料節。

## 最近驗收狀態

- 2026-08-12：Working Paper、Microsoft Excel 與前端十二項人工驗收全過。
- 2026-08-13：流程總覽六項人工驗收全過。
- 2026-08-15：核心能力補完計畫的最終一場制人工驗收五項全過（匯入與配對、資料驗證、科目分類與篩選、六份報表抽驗、舊案相容）；同場裁決 PBC envelope 選項 (b)，harness 修正同日落地並重驗。
- 本次驗收程式的發布封裝與完整性檢查已由 agent 完成；精確身分留在內部 receipt，不屬於使用者驗收內容。
- 2026-08-18：業務模型收斂與全面盤查清理計畫最終三項總驗收全過。真實案件條件情境與差異分類通過；六份正式報表的 Excel 開啟／渲染與 Working Paper step4 家族的窗格／捲動／列印依使用者明示裁決視為通過，本機未安裝 Microsoft Office，沒有執行相關操作。該計畫驗收清單已銷帳。
- 2026-08-20：預篩選重定位與小案收斂計畫最終一場制人工驗收五項全過。頁籤、無預篩選匯出、步驟四定位與可選 Pre-screening Report 均由使用者操作確認；外部連結項目由使用者明示依自動 package 證據裁決通過，本機未執行 Microsoft Excel 操作。
- 2026-08-26：使用者先回報人工一、二、四通過；第五項先後回報四個輸出樣式問題，包含 Working Paper step4-1 整頁框線。修補與 fresh Full 全綠後，使用者明示剩餘驗收全部完成，第三與第五項同輪銷帳；`windows-handoff.md` 現無待辦。

## 最近一次自動驗證

驗證日期：2026-08-26，Windows（人工驗收結案後的對抗複審＋最終候選）

| 檢查 | 結果 |
|:---|:---|
| 需求／實作反作弊複審 | 產品 assembly 不依賴 CaseAcceptance、PrivateParity、具名測試類別或真案 fixture；六份報表測試讀實際 OpenXML artifact，local portability 測試讀 SQLite／DuckDB 真實資料庫，CaseAcceptance 走產品 action journey 並由獨立 Excel driver 驗收。複審沒有調低既有 assertion，反而找到兩個產品缺口：路徑型 caller `fileName` 可進本地 DB，以及建案名稱焦點原本缺乏後端結構化欄位歸屬 |
| 可攜檔名與結構化錯誤 first-red／修正 | 合併 Affected first-red 為 **442 total／439 passed／3 failed**；結構化 error wire 的 tests-first build 另先以 16 個缺少型別／欄位的 compile errors 固定契約。修正後 `ImportSourceFileName` 在 Application 與 local repository 雙邊界收斂 leaf filename；`JetActionException`／bridge 增選填 `field`，只由後端明示 `caseName`，前端不解析 code／message。store collision 再收緊為 typed `ProjectStoreCollisionException`，一般 invalid payload 與 server-only load 都不會誤標欄位 |
| 整合 first-red 與最終 fresh ReleaseCandidate | 第一場完整候選在 Release 以 server-only 專案物化碰撞揭露第二個 caller 未轉譯 typed collision：**3865 total／3856 passed／1 failed／8 expected skips**，same-seed replay 同紅；修正產品 `ProjectLoadHandler` 後定向 **440／440**。未變完整命令 fresh-rerun 全綠：Release **3865 total／3857 passed／8 expected skips**、Provider **280／280**、GUI **20／20**、migration **468／468**、report／package **504／504**，五個 child cleanup complete、無 replay |
| 初次三項格式回歸／修補 | first-red 為 **441 total／438 passed／3 failed**，same-seed replay 仍同三項；修補後 report／package＋presentation 為 **511／511、0 skipped、無 replay** |
| step4-1 全頁無框線回歸／修補 | 新增的 whole-sheet 守衛先以 **442 total／441 passed／1 failed** 固定模板資料外框線；修補後 presentation＋封閉 appearance decision 為 **451／451、0 skipped、無 replay**。cell、row、column 的顯式 style 都必須解析為無可見 border |
| synthetic 結構與版面 read-back | 合成 Working Paper 由 OpenXML 守衛確認 step4-1 全頁無框線，並實際渲染表頭／資料區與範本末端空白區；只剩 Excel 淡色格線，未見黑色框線。臨時 capture hook 已移除，測試成品只留在 ignored evidence |
| 文件回流後最終守衛 | 新 error envelope、架構與機敏文件政策的 Affected 集合 **447／447、0 skipped、無 replay**；結案文件、人工銷帳與產品契約共同受測，`privateData.pathInspected=false` |
| 修補後真實案件快速驗收 | **1／1 全綠、0 skipped、無 replay**。SQLite 1 run、2 observations、6 content、6 appearance、12／12 Excel（legacy 6、JET 6）；legacy external links 只觀察 aggregate，JET external links 0，unknown／JET defect／provider mismatch 0，三項必要能力 available，私人 artifact 與 test lock／holder 均清理 |
| 修補後真實案件完整驗收 | **1／1 全綠、0 skipped、無 replay，總計 1594.026 秒**。完整候選 gate 五組全綠後，由 SQLite／DuckDB／SQL Server 各兩次形成 6 provider runs、7 observations、30 consistency 與 24／24 Excel（legacy 6、JET 18）；JET external links 0，unknown／JET defect／provider mismatch 0，四項必要能力 available，私人 artifact cleanup 完整 |
| fresh 完整候選 gate | Release **3854 passed／8 expected skips**、Provider **280／280**、封閉 GUI **20／20**、migration **468／468**、report／package **504／504**，五組 cleanup 均完成。首次完整嘗試只在 GUI 重試情境出現一次時序紅燈（19／20）；單情境診斷通過後，以未變的完整命令 fresh-rerun 全綠，原始紅燈仍保留 |
| 自動／人工與執行邊界 | 24／24 Excel 證明 read-only open、link policy、完整重算、公式錯誤不增加、bounded first-page PDF preview 與 exact process cleanup；結構與 render 守衛另證明六份字族、step1-2 無底色人工格、兩張欄位資訊頁同構，以及 step4-1 全頁無黑框。主觀 UX 與整份底稿可讀性另由使用者完成，兩種證據不互相冒充；`windows-handoff.md` 現無待辦 |
| Harness sandbox 合約 | `Affected -Filter 'FullyQualifiedName~VerifyHarnessContractTests'` 376／376 全綠（run `20260815-144556982`），含新增的 PrivateParity 綠燈（登錄 PBC skip → exit 0、`privateData=availableAndExecuted`、fake test host 實收 `JET_PRIVATE_PARITY_ENABLED=1`）與紅燈（未登錄 PrivateParity skip → exit 1、`PrivateDataExecution` 缺失）語意證明 |
| 實機 PrivateParity | run `20260815-144750413-aeaac93490d246b2b509796bec1f8307`（Release）**exit 0**：17 total／13 passed／0 failed／4 expected skips（`PbcFixtures`＋`PbcSqlServerFixtures`，unexpected 0）；不再使用 shell 環境變數 workaround |
| 收案 safe Release 全套（2026-08-15） | run `20260815-150103437-f2cfc7fe4f6f454083ff3e8ccee8b20e`：**3648 total／3640 passed／0 failed／8 expected skips**（unexpected／missing 0）；相對前一基準 3645 的 +3 為該輪新增的 harness 合約測試 |
| Harness 盤點輪 Release 全套（2026-08-17） | run `20260817-021209393-c772ead29ae743cabd42071d1bf0fe73`：**3670 total／3662 passed／0 failed／8 expected skips**（unexpected／missing 0）；相對 3648 的 +22 全為 2026-08-16／08-17 兩輪新增的 agent 機制守衛案例（17＋5） |
| 裁決與清理落地 Release 全套（2026-08-18） | **3674 total／3666 passed／0 failed／8 expected skips**（unexpected／missing 0）；相對 3670 的 +4 分別鎖住條件型別顯示名、legacy code 對映、刪案清理指引與未 staged rename 下的機敏來源掃描 |
| 驗收重排 fresh read-back（2026-08-18） | 樣式守衛精確 slice run `20260818-033322819-9ade92ddd3c94bec88a77c7845c4647c`：**405／405 全綠、0 skipped**；文件回流後機敏資料政策 slice run `20260818-034243153-bc1cdd99f76b41e59b5f3d4ee6461c4d`：**400／400 全綠、0 skipped**。五個既有 Stage 8 Track W 測試已在上列 Release 實際通過；本輪更正盤查 false negative，沒有新增第二份 oracle |
| 結案文件回流 gate（2026-08-18） | 首次 Affected architecture run `20260818-053652557-aabae8f809e94f688e9482d87b9424c7` 為 **398／400**，兩個 PowerShell harness probe 逾時；同 seed diagnostic replay 25／25 通過，但原始紅燈依政策保留。乾淨重跑 `20260818-053950611-04e501c7269e4e28a2265455203ee16d` 為 **400／400 全綠、0 skipped**，exit 0 |
| 報告鏈契約鬆綁 Affected（2026-08-18） | run `20260818-094353788-bbfed76255a44776a2fe132bce5399ad`：**559 total／557 passed／0 failed／2 expected capability skips**（filesystem link 建立不可用；unexpected／missing 0），涵蓋無 prescreen 的 Criteria／Working Paper、Pre-screening run gate、validation／filter stale、artifact provenance／retention、前端 mirror 與 OpenXML regressions |
| 步驟四母體概況改造 Affected（2026-08-19） | 最終 run `20260818-162910826-12ee7496ff2545e8ae079da8dd51692f`：**479／479 全綠、0 skipped**（unexpected／missing 0），涵蓋 positioning exact wire、AuditCore／frontend mirror、prescreen 執行與報告分離、集中度／逐筆分布、resume renderer、action contract 及全部 architecture／contract guards；同 tree 的 Build run `20260818-162857971-bf928d27d64e446b9acb2ac1a4409d78` 亦通過，0 warnings／0 errors |
| 步驟四 Agent GUI gate（2026-08-19） | `startup-smoke` run `e2e619088bb84d9e80b751721a70270a`：**9／9 assertions 全過**，action 0、截圖 0、offline 且無下載；EdgeDriver／WebView2 runtime major 均為 151，cleanup 自然停止且 run root 已移除。先前預設沙箱內兩場 attach timeout 經有界診斷證實為 WebView2 GPU 子程序 `0xC0000022 ACCESS_DENIED` 的執行環境限制；同一未降級 smoke 在核准的 Windows 邊界通過，暫時診斷碼已撤回，未留下 production workaround |
| 小案三軌 Affected（2026-08-19） | 最終 run `20260818-235923493-9690807a61454753a9bca7ca900bcad2`：**486／486 全綠、0 skipped**（unexpected／missing 0），涵蓋 closed audit tokens、production audit journey、SQLite／DuckDB v7→v8、兩份範本 relationship、頁籤 mirror、OpenXML package 與 Stage 8 appearance oracle；同 tree 的 Debug／Release Build 皆 0 warnings／0 errors |
| 小案三軌 Provider（2026-08-19） | 受限環境內首次因 SQL Server capability unavailable fail closed；同一完整命令於核准 Windows 邊界最終 run `20260819-000120984-8072fa59895e4737bf7d4a214575ab8e`：**279／279 全綠、0 skipped**（unexpected／missing 0），含 SQL Server v7→v8、schema catalog 與 project audit writer |
| 預篩選重定位收案 Release 全套（2026-08-20） | **3695 total／3687 passed／0 failed／8 expected skips**（unexpected／missing 0）；Release build 0 warnings／0 errors。首次完整 run 找到兩份範本來源錨點 oracle 未納入已核准衛生例外，補成封閉 package-diff 守衛後定向 433／433；其後一場 SQL Server readiness timeout 的同 seed replay 通過，但原始紅燈保留，fresh-rerun 全套才作為正式綠燈 |
| 預篩選重定位收案 Provider（2026-08-20） | **279／279 全綠、0 failed／0 skipped**（unexpected／missing 0），SQL Server capability 實際執行 |
| 收尾技術債與默認輸出 Release／Provider（2026-08-20） | Release run `20260820-070907273-efdde88f951848aab00ebb8d7c469e6d`：**3710 total／3702 passed／0 failed／8 expected skips**（unexpected／missing 0），build 0 warnings／0 errors；Provider run `20260820-070625564-68cad1583f554cb89eaa0f6f3affc13a`：**280／280 全綠、0 skipped**，SQL Server capability 實際執行 |
| 本地專案可攜性 Affected（2026-08-20） | 文件回流後 fresh run `20260820-073619636-70cb9f4282504fff8515f37d7093c7a0`：**449 total／447 passed／0 failed／2 expected capability skips**（filesystem link 建立不可用；unexpected／missing 0）。SQLite／DuckDB 皆涵蓋新匯入不保存絕對來源路徑、既有絕對值開案時不升 schema 收斂、整夾搬移後完整 resume／latestRuns／artifact 開啟與清理預覽／正式查詢，以及 rooted artifact manifest 拒絕；同 tree Release build 0 warnings／0 errors |
| UX 核准項落地（2026-08-20） | Release build 0 warnings／0 errors；Affected **422／422 全綠、0 skipped**。封閉 GUI 實跑：`frontend-usability` 41 assertions／39 actions，`export-progress-cancel` 21 assertions／7 actions，`cross-project-stale-read` 60 assertions／40 actions，均 0 screenshots、正常 cleanup；精確識別留在本階段 receipt |
| 收案完整驗證（2026-08-20） | Release 全套 run `20260820-095705600-b8421eaa3ebe4efe86925e38283bcce0`：**3722 total／3714 passed／0 failed／8 expected skips**（unexpected／missing 0），build 0 warnings／0 errors；Provider run `20260820-100048942-ec72ca888f404c51ab0b201d2d2157a3`：**280／280 全綠、0 skipped**，SQL Server capability 實際執行；封閉 GUI 20 場 run `20260820-101056795-c539e0cc189640e28e70b0f8ee280c7b`：**20／20 全過**、346 actions、0 screenshots、離線且無下載，harness 正常回到 inactive |
| GUI gate 首次紅燈與原因（2026-08-20） | 20 個場景本身全部通過，紅燈出在成功契約核對：預先準備的 EdgeDriver 憑據仍記著 2026-08-13 當時的 WebView2 runtime 版本，而本機 runtime 已自動更新，逐字比對不符使全部 child 判為不合格。以 harness 既有的 `Prepare` 重新產生憑據（driver 與 runtime 同版）後重跑全綠；產品程式、測試與守衛未因此改動 |
| 跨案件 PrivateParity 驗證結論（2026-08-15） | metric 241 筆（JET 缺陷 0、provider mismatch 0、未分類 0）、content 145 筆與外觀 97,221 筆全數封閉裁決、provider 一致性 60 組 mismatch 0、availability 53 維度 50 provided／3 notProvided |
| 機敏資料邊界 | 兩案 fixtures 僅本機唯讀；evidence 只含 sheet 名稱、counts 與去識別化 hash；無 private path、欄名、cell value、原始 diff 進入 tracked files、TRX 或對話 |
| 本地可攜性階段未執行項目 | 本 packet 只要求 Affected，未執行 Provider、Gui、Release、PrivateParity、Scale 或 Mutation；本階段不改 wire／可見 UI／SQL Server，且未讀取真實案件樣本。兩個 filesystem-link 測試是既有 exact-identity expected capability skips，不冒充實際 link 驗證 |

2026-08-16 與 2026-08-17 連續兩輪跨 host agent 機制過審與收口，只動文件、設定與測試，產品程式一行未改。08-16：`.claude/skills/` 薄入口與 canonical skill 的一一對應、`disable-model-invocation` 與 Superpowers plugin 停用改由 `SkillExplicitInvocationPolicyTests` 機器把關（此前只有 Codex 側有守衛）；第二個機敏路徑 `data/legacy-parity-work/` 補進 `AGENTS.md` 與 Copilot 規則；上游基準更新到 Superpowers v6.3.0 與 mattpocock `068b6e0`。08-17：`.claude/settings.json` 新增 `permissions.deny` 執行前硬擋（直接 `dotnet test`、git 整批 staging／`write-tree`／`stash --all`、機敏目錄寫入）並納入同一守衛；六個零使用的 workflow skills 移除（保留 `code-review`、`diagnosing-bugs`）；`docs/jet-template.html` 與孤兒稽核腳本移除、視覺參考改指 `design_handoff_jet_frontend/`；output style 歸位為本機 `~/.claude/` 正本；`AGENTS.md` 減行後與 `CLAUDE.md` 合計 199 行回到 Claude 官方建議內。完整 Release 全套已於 08-17 重跑（見上表 3670 全綠），前一輪「3648 不含新增案例」的註記至此銷帳。

08-17 第三輪（skills 理念內化與盤查銷帳）：評估 mattpocock `grill-with-docs` 與 sandeco `reversa` 後皆不安裝，改把 grilling 訪談紀律與 reversa ideation 管線內化為 project-owned explicit-only skill `jet-converge`（框定→發散→挑戰→收斂），「需求模糊先收斂再動工」同步寫進 `agent-workflow.md` 開始工作；溯源記在 `skills-lock.json`。同輪銷掉四個盤查發現：孤兒 `legacy/JET-legacy/`（106 個追蹤檔）移除、`legacy/README.md` 補齊 `idea-tool.bas`／兩套 VBA 版本／`jet-template-v1.html`／drawio 條目、設計稿 UpSet 區塊補 2026-07-30 移出範圍裁決註記、`.gitignore` 刪除指向不存在檔案的 `appsettings.Development.json` 例外規則。守衛定向重驗 398／398 全綠（run `20260817-054403512`），`AGENTS.md`＋`CLAUDE.md` 維持 199 行；本輪變更已由使用者於同日下令，隨業務模型收斂計畫立案一併 commit。

計畫各階段的精確 evidence 路徑與 SHA-256 已隨 master spec receipts 由 Git 歷史保存。過程中所有真實紅燈均保留並修到相同 oracle 轉綠；沒有降低 assertion、增加未登錄 skip 或以重跑掩蓋原始失敗。

## 目前可用功能

JET 已具備可連續操作的六步驟流程：

1. **建立或載入案件**：支援 SQLite、DuckDB 與 SQL Server。
2. **匯入資料**：支援 `.xlsx`、`.csv`、`.txt`，可匯入 GL、TB、科目配對、授權編製人員與日期維度。
3. **欄位配對**：支援 GL／TB 的不同金額欄位模式，由資料庫完成標準化。
4. **資料驗證與預篩選**：Validation v4 產生有效母體 `stats`、raw／effective／excluded `populationSummary`、eligible-source 對 effective-target Part A／金額分布／四項規則摘要與獨立 `sourceQuality`；完整明細只走有界分頁，不把母體送到前端。
5. **進階條件篩選**：建立、預覽及保存情境與命中結果；借貸組合的兩側皆可多選分類，另可用欄位配對勾選的額外欄位建立有型別條件（現行 filter v10），完整命中明細依系統端回傳的欄位定義呈現。
6. **匯出底稿**：產生 Validation Report、Account Mapping、INF Report、Pre-screening Report、Criteria Selection Report 與 Working Paper；後兩份不要求先執行 prescreen。步驟五匯出面預設把 Pre-screening Report 一併產出（可取消）；沒有現行預篩選結果時，畫面先明示會補跑一次再產出。六份報表共用 `VeryHidden JET_Metadata`。Validation Report 保留 V1–V6、blank post date 只進 `Source_Quality` 而不建 V7；INF／Criteria／Working Paper 分別輸出全 committed RDE／全 revision 引用 union／所選 positions union。Pre-screening 欄位 lineage 與上述契約均由 synthetic／OpenXML／provider regressions 鎖定，並已於 2026-08-15 由跨案件 PrivateParity 完成兩案驗證。

正式規則都由資料庫以參數化、集合式 SQL 執行。前端只操作、呈現與預覽，不自行裁定審計結果。流程總覽純鏡射既有 state：前五階段完成度沿用下一步 `stepGate`，最後階段沿用目前版本 Criteria Selection Report／Working Paper 的精確來源比對；點選階段不呼叫 `project.saveProgress`。

## 最近一次人工驗收

最近一場是 2026-08-26 的默認輸出、本地可攜性、UX 與修補後六份底稿一場制驗收。使用者先回報一、二、四通過；四個底稿呈現問題修補後，又明示剩餘第三與第五項全部驗收完畢。七項操作體驗與六份底稿主觀版面均已由使用者實際銷帳；`windows-handoff.md` 現在沒有待執行項目。其後的結構化 `error.field` 與 typed store collision 是保留相同可見行為的內部穩健化，已由對抗測試與完整候選驗證，不另冒稱人工重測。

前一場是 2026-08-20 的預篩選重定位與小案收斂計畫最終一場制驗收，使用者回報五項全過：五個右側頁籤、沒有預篩選時直接產出 Criteria Selection Report／Working Paper、步驟四彙總為主與逐筆輔助定位，以及可選 Pre-screening Report 均通過。外部連結項目由使用者明示依自動 package 證據裁決通過；本機沒有執行 Microsoft Excel 操作，不把裁決改寫成實測證據。

最近已完成的一場是 2026-08-15 的核心能力補完計畫最終一場制驗收，五項全部通過：建立新案件的匯入與欄位配對（含過帳狀態政策、核准日模式、人工／自動代碼與額外欄位）、資料驗證的母體分區與來源品質、科目分類多選與 typed 條件篩選、六份正式報表抽驗（Pre-screening 明細頁欄位 lineage、`R6` 前兩欄標題、`Source_Quality`、Working Paper step4-1）與舊案相容（mapping 確認、過期標示、recommit 重跑）。

先前場次：2026-08-12 十二項（六份 Excel 報表、Working Paper 封面與樣式、篩選、parity、防案件特化、版面與鍵盤焦點等）與 2026-08-13 流程總覽六項均全過。2026-08-13 使用者曾以未提供的公司案件發現 `Pre-screening Report` 欄位來源泛化問題；該缺陷已於 2026-08-14 修正、2026-08-15 由跨案件 PrivateParity 驗證與最終驗收收口。

最近一次原始大案效能觀察仍是 2026-07-31；它不是受控 benchmark 或 SLA：

| 案件 | Provider | GL／TB | GL 匯入 | GL 標準化 | 驗證＋3 報表 | 預篩選＋報表 | Working Paper |
|:---|:---|:---|---:|---:|---:|---:|---:|
| case-A | SQLite | 1,712,542×50／378×4 | 151.1 秒 | 38.1 秒 | 17,376 ms | 57,126 ms | 9,683 ms |
| case-B | SQLite | 20,260,435×14／447×7 | 248.1 秒 | 622.2 秒 | 313,812 ms | 724,373 ms | 209,549 ms |
| case-B | DuckDB | 20,260,435×14／447×7 | 114.9 秒 | 289.3 秒 | 9,060 ms | 106,385 ms | 13,917 ms |

## 延後或尚待決定

| 項目 | 現況 |
|:---|:---|
| 企業 SQL Server 多人環境 | 公司環境、帳號授權與多人鎖仍延後，見 `sqlserver-online-handoff-deferred.md` |
| KCT 後續條件 | 定位與現行 A–J 執行對應已於 2026-08-18 回流 `jet-guide.md`。指定的 `idea-script.bas`、`idea-tool.bas` 與 draw.io 流程圖查無 KCT 名稱、A–J 原始清單、KCT 全稱或 B 的 BS／IS／PPE 分類表；原始來源與 B 完整分類仍待使用者提供，不先推測實作 |
| 預篩選條件併入進階篩選 | 2026-08-18 收斂立案、2026-08-20 計畫結案：逐筆退場、彙總在步驟四重編為主要呈現、規則目錄凍結、Pre-screening Report 暫保完整改純可選（匯出面默認勾選的對齊已納入現行計畫）。該報告的長期去留（縮編或退役）與預篩選條件的最終棄用清單仍屬未來另場收斂 |
| SQLite／DuckDB 刪案 audit | 2026-08-20 裁決**不做**：本地案件屬個人工作性質，持久刪案證據是中心化管理議題，只屬於未來 SQL Server 線；既有 `audit_event_log` 保留為該線預備。SQL Server 既有 `dbo.audit_log` 刪案留痕維持 |
| 母體完整性逐項清單 | 刻意只留在「資料驗證與測試」作為唯一權威判定面；總覽只摘要既有後端裁定，不建立第二套判定 |

## 已知技術債

- `Pre-screening Report` 的欄位來源忠實性已於 2026-08-14 修正、2026-08-15 完成兩案跨案件驗證（規則見 `jet-guide.md` §7.1）；未提供的公司案件仍未在本機復現。`R5` 的 `CREATED_BY` 等彙總頁標題不在該裁決範圍（裁決只涵蓋 R2 明細家族與 R6），維持既有輸出契約名稱；若要延伸同樣的 lineage 規則，需另行裁決。
- `JET_PBC_DIR` PBC fixture 家族（114MB 大檔煙霧測試）仍不在本機（`jet-guide.md` §13 既有敘述一致）。2026-08-15 harness 修正後這是**已登錄的資料可用性條件**：四個 PBC 測試以精確身分與理由登錄在 `tools/verify-expected-skips.json` 的 `PrivateParity` 段，skip 不再使 envelope 紅燈，資料齊備時轉綠仍合法；未登錄或理由不符的 skip 照舊紅燈。原 `dotnet test -e` 環境變數傳遞缺陷已同輪修復（改 process 環境變數傳遞並還原）。
- 歷史 stage6／7 parity 批次封板測試已退役（其本機 capture 已不存在、觀測值屬多階段前的舊版行為，無法以現行程式重現）；跨案件驗證由 `crosscase-pure-verification` 批次與 `LegacyAuditParityCrossCaseCloseoutTests` 取代。歷史批次的 store／comparator／catalog 機制仍保留並由 synthetic 單元測試覆蓋。
- Stage 9 的 28 張基底頁包含 18 張直接實物、7 張同 writer family 投影與 3 張 summary-only 無 worksheet；固定範本／非基底區的 JET 標準不得冒稱直接 legacy parity。
- CSV 欄名與資料讀取會重做一次格式偵測；只有實測成為瓶頸時才優化。
- 大型本機案件使用較多磁碟空間與 WAL；不能以把母體載入記憶體換速度。
- 資料面敏感操作的最小 audit log 已落地，資料庫層防改防刪已於 2026-08-20 隨 schema v9 落地（DuckDB 因引擎不支援 trigger 維持程式紀律，理由記於 `jet-guide.md` §13）；查詢介面、匯出、保留政策與企業部署稽核仍另案（SQLite／DuckDB 刪案留痕經 2026-08-20 裁決不做）。正式 SQL Server 部署仍需安全連線字串與正式憑證。
- KCT 已保存 definition 在之後的 GL 重投影失去必要欄位時，惰性 materialize 的 mapping-aware 重驗仍待另立契約與 lifecycle 測試。

詳細業務語意與架構以 `jet-guide.md` 為準；過往決策摘要見 `development-log.md`，精確歷史差異由 Git 保存。

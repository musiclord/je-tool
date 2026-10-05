# JET 歷史開發紀錄

依日期排列，新的在上。這份紀錄由兩個來源專案的開發日誌合併而成，涵蓋 2026-06-04 到 2026-08-26；
`je-tool` 建立之後的開發不寫在這裡，改由 [`../development-status.md`](../development-status.md) 的現行計畫
與 [`../specs/`](../specs/) 承接。

本檔只解釋當時做了什麼、為什麼那樣決定、驗證到什麼程度。它**不描述 `je-tool` 現在的行為**；精確規則一律
以 `src/`、仍有效的測試與 [`../jet-guide.md`](../jet-guide.md)、[`../action-contract-manifest.md`](../action-contract-manifest.md)、
[`../jet-frontend-description.md`](../jet-frontend-description.md) 為準。紀錄中提到的測試通過數、命令與檔案
路徑屬於當時的環境，不能當成 `je-tool` 的通過證明。

紀錄的粒度在 2026-06-29 前後改變，原因見文中的分界說明。合併時沒有改寫任何條目的文字、數字、條件或
使用者裁定；只重新排序並補上分界說明。

## 2026-08-26 — 真實案件驗收、底稿樣式與對抗複審結案

五階段的自動能力與整合收尾完成後，使用者在同一場人工驗收先回報一、二、四通過；第五項先指出 Working Paper step1-2 人工格異常淡黃底、step4-1 混入新細明體，以及 Validation Report 欄位資訊可見區黑框，續驗再指出 step4-1 的資料右側與末列後方仍殘留黑框。兩輪都先用 Affected first-red 固定，再於不改審計資料與計算語意的邊界內修補。六份正式輸出現在把 style font 與 rich-text run font 統一為微軟正黑體；這是使用者明示核准、刻意不同於 legacy 的呈現契約。Validation／Working Paper 同名欄位資訊頁的可見 A:E 共用無框線語意，step1-2 C／F／G／H 維持未鎖定但不再使用提示底色；Working Paper step4-1 則把 cell、row、column style 的 border 全部移除，連同資料外的範本預格式區與續頁都不顯示黑框，其他樣式語意不變。使用者其後明示剩餘第三與第五項全部驗收完畢，一場制清單至此銷帳。

使用者要求結案前再做一次反作弊複審：產品 assembly 不得依賴測試、真案 fixture 或已知答案，自動測試只能作外部對抗標準。逐層追蹤後沒有找到 CaseAcceptance／PrivateParity／具名測試類別進入產品的依賴；報表守衛讀實際 OpenXML artifact，本地可攜性守衛讀 SQLite／DuckDB 真實資料庫，真案驗收走產品 action journey 與獨立 Excel driver。複審沒有放寬 assertion，反而以 first-red 找到兩個實作缺口：caller 提供路徑型 `fileName` 時可能把來源根寫進本地資料庫；原 UX-06 又缺少可靠的欄位歸屬，只能靠過寬的 create-phase error 判斷。前者改由 Application 與 local repository 雙邊界正規化 leaf filename，既有 rooted 值仍在開案時冪等清理；後者在 error envelope 增加後端選填 `field`，只有名稱格式、同名發布或 SQL Server 同名殘留明示 `caseName`，前端不解析 code／message。

建案並行碰撞再收斂為 store 的 typed `ProjectStoreCollisionException`，由 case-create 與 server-only load 各自在自己的業務邊界轉成既有錯誤語意，避免任意 `invalid_payload` 被誤標案件名稱。第一次完整 ReleaseCandidate 因 server-only 物化 caller 未轉譯而在 Release 3865 項中真紅 1 項，same-seed replay 同紅；修正產品 `ProjectLoadHandler` 後定向 440／440，再以不變完整命令 fresh-rerun：Release 3865 total／3857 passed／8 expected skips、Provider 280／280、GUI 20／20、migration 468／468、report／package 504／504，五個 child 都 cleanup complete。此前 fresh Full 的 6 provider runs、7 observations、30 組 provider consistency 與 24／24 Excel 仍全綠；JET external workbook links、unknown difference、JET defect 與 provider mismatch 均為 0，私人 artifacts 已清理。文件回流後 error envelope、架構與機敏政策 Affected 集合 447／447；`windows-handoff.md` 現無待辦，計畫已驗收，沒有下一個可由 agent 開始的階段。

## 2026-08-20 — 默認輸出對齊、本地可攜性與 UX 收斂計畫結案

五個線性階段的程式與文件全部完成；最終一場制人工驗收於 2026-08-26 經修補續驗後由使用者全數通過，結案前對抗複審與 fresh ReleaseCandidate 亦完成。

三項行為改變：**Pre-screening Report 成為匯出底稿的默認輸出**——步驟五匯出面預設勾選、可取消；沒有現行預篩選結果時畫面先明示會補跑一次，由使用者按同一顆按鈕確認後才依序執行，不無預警啟動可能十餘分鐘的長作業。這是前端以既有 action 編排的序列，三個 action 的判定語意、provenance 與 step gate 都沒有變。**SQLite／DuckDB 案件資料夾成為受測的可攜單位**——來源 JET 正常離場後把整個案件資料夾搬到別的磁碟或別台電腦，重開即可續作；匯入來源路徑與呼叫端選填 `fileName` 都在持久化前收斂為 leaf filename，既有 rooted 值於開案時就地清理，並以兩個真實本地引擎的資料庫 read-back 驗證；保證邊界是 cold copy，WAL 使用中的熱複製與 SQL Server 案件不在其內。**`audit_event_log` 的 append-only 改由資料庫層強制**（schema v9）——SQLite 與 SQL Server 以 trigger 拒絕 UPDATE／DELETE，DuckDB 1.5.3 查證後確認引擎沒有 trigger 也沒有 table-level 權限，維持程式紀律並由來源守衛與引擎能力測試把關。

兩項裁決值得記住。一是**本地刪案留痕決定不做**：本地案件屬個人工作性質，持久刪案證據是中心化管理議題，只屬於未來的 SQL Server 線；SQL Server 既有的刪案紀錄維持。二是 **UX 大題併入本計畫**——agent 建議另開一場獨立收斂，使用者裁決併入並接受計畫變大、收尾拉長的代價；做法採「盤點→裁決→落地」，走查六步主流程、錯誤與破壞性操作防呆、流程總覽與資料預覽、視覺樣式四面，產出七項發現交使用者逐項裁決，全部核准後才落地。七項都是操作與呈現層收斂：已存篩選情境的二段移除確認、刪案對話框的鍵盤與焦點契約、資料預覽的具名載入與就地重試、長作業的經過時間、六步導航的焦點與位置宣告、建案失敗的表單就地回復，以及正式版結構表面收斂為方角與細線。結案複審只為建案錯誤補上後端選填 `error.field`，沒有新增 action、payload input、schema、審計 renderer 或報表內容；這是避免前端猜測的契約硬化。

盤點時沒有發現需要版本化復原（undo）的項目；破壞性操作的缺口都能用影響預告與明示確認收斂。Pre-screening Report 的長期去留與預篩選條件的最終棄用清單仍留待未來另場收斂，audit log 的查詢介面、匯出與保留政策仍屬另案。

## 2026-08-20 — 預篩選重定位與小案收斂計畫結案

四個線性階段全部完成。Criteria Selection Report 與 Working Paper 已解除預篩選前置；步驟四把依分錄編製者與較少使用科目兩條彙總提升為主要判讀面，逐筆命中明示為輔助訊號；Pre-screening Report 保留完整內容但改為純可選輸出，預篩選規則目錄自此凍結，不再新增逐筆規則。小案部分完成 schema v8 的 project-local 最小 audit log、INF／WorkingPaper 兩份 runtime 範本的封閉 external-workbook 衛生例外，以及五個右側資料預覽頁籤顯示名。SQLite／DuckDB 的案件外刪案 audit store、audit 查詢／匯出／保留政策與 Pre-screening Report 長期去留仍各自留待另案，不在本計畫擴張。

收案 Release 全套為 3695 total／3687 passed／0 failed／8 expected skips，Provider 為 279／279、0 failed／0 skipped。Release 首次揭露舊範本測試仍要求所有 runtime 範本與實物錨點逐位元相同，與已核准的兩份衛生例外衝突；修正後由封閉 package-diff 守衛證明只有必要 XML parts 與 external-link parts 改變，其餘 package parts 維持逐位元相同。另一次 SQL Server readiness timeout 的診斷 replay 雖通過，原始紅燈仍保留，最後以未變 gate fresh-rerun 全綠收口。

最終一場制人工驗收由使用者回報五項全過：頁籤、沒有預篩選時直接匯出、步驟四定位與可選 Pre-screening Report 均由使用者操作確認。外部連結項目由使用者明示依自動 package 證據裁決通過；本機沒有執行 Microsoft Excel 操作，不把裁決改寫成實測證據。人工清單已自 `windows-handoff.md` 銷帳，目前沒有待執行的人工驗收或下一個已核准開發階段。

## 2026-08-18 — 業務模型收斂與全面盤查清理計畫結案

五個線性階段全部完成：先以篩選條件鏈草圖證明模型能揪出真實矛盾，再把預篩選的輔助定位、KCT A–J 現行執行對應、六步資料生命週期與跨層責任回流權威文件；全面盤查的十五項發現經使用者裁決後逐項清理，Release 與最後的精確樣式、機敏資料守衛均保持綠燈。盤查原稱缺少的三個 Working Paper 樣式守衛經 fresh read-back 證實是 false negative，既有 legacy-derived appearance oracle 已涵蓋，因此沒有另建重複測試。

最終一場三項總驗收由使用者回報全過。真實案件條件情境與差異分類通過；六份正式報表的 Excel 開啟／渲染，以及 Working Paper step4 家族的捲動／列印，依使用者明示裁決視為通過。本機未安裝 Microsoft Office，因此這兩項沒有實際執行 Office 操作，不把裁決改寫成不存在的操作證據。完成清單已自 `windows-handoff.md` 銷帳，目前沒有待執行的人工驗收或下一個已核准開發階段。

## 2026-08-17 — 業務模型收斂與全面盤查清理計畫立案（jet-converge 首次實戰）

使用者以 `$jet-converge` 發起收斂訪談，處理的核心問題是：多輪改版後程式碼的審計商業模型比 legacy 更難讀出、模糊邊界累積、且部分歷史人工驗收是在模糊狀態下帶過的。訪談走完框定→發散→挑戰→收斂四階段，比較四個方向（不做／模型先行／盤查先行／合併分階段）後，使用者裁決採「模型為鏡、盤查為帚」的合併計畫，master spec 立於 `docs/specs/2026-08-17-model-convergence-and-consolidation-plan.md`，共五個線性階段，第一階段是「篩選條件鏈草圖驗證閘」——模型必須先證明揪得出實際矛盾，計畫才走完整盤查；揪不出即觸發縮編反轉，回使用者重裁。

同場兩項重要裁決：一、**預篩選降為輔助條件、KCT 小組指定的十組條件為實務主線**——這是 repository 首次登錄的業務定位轉向，模型層立即生效，產品層（step gate、報表、UI）對齊由盤查逐項登錄後依裁決落地；KCT 十組條件的精確定義尚未登錄，先查 legacy、查無再由使用者提供。二、歷史模糊驗收全面重開，但同一行為只驗一次、過度細節改由自動測試吸收，最終收在一場去重總驗收。

## 2026-08-17 — Skills 理念內化（jet-converge）與盤查銷帳

同日第三輪，依使用者裁決把外部工具評估從「裝或不裝」改為「理念層與包裝層分開評估」。mattpocock `grill-with-docs`（現為 `grilling`＋`domain-modeling` 的組合殼）與 sandeco `reversa`（上百個 agent 的逆向工程框架，自建 `.reversa/` 與 `_reversa_*` 文件根）皆因包裝與 JET 的 canonical skill root、單一事實來源及機敏資料邊界硬衝突而不安裝，但其理念內化為新的 project-owned explicit-only skill **`jet-converge`**：框定（分清問題與偽裝成問題的方案、JTBD 句式）→ 發散（2–4 個方向含「不做」）→ 挑戰（premortem：中心假設、便宜驗證、隱藏成本、不可逆點）→ 收斂（固定判準比較；推薦不是裁決，分歧照實記錄）。訪談紀律（一次一個最高影響決策、先鋪脈絡再問、一輪最多五問、可查證即停）同步寫進 `agent-workflow.md`「開始工作」成為日常任務的通用規則——此前收斂義務只在 fresh-session 規劃側有條文。溯源與不安裝原因逐條記在 `skills-lock.json`（新增 `reversa` reference-only 來源），`agent-frameworks.md` 上游基準與 skill 清單同步。

同輪銷掉前一輪盤查的四個發現：孤兒 `legacy/JET-legacy/`（2026-06-23 初始匯入後零修改、零引用的早期 .NET 原型，106 個追蹤檔，且與現行程式大量同名類別造成搜尋污染）移除，由 Git 歷史保存；`legacy/README.md` 補齊 `idea-tool.bas`（parity 對照原本）、`vba-1120`／`vba-mvp` 兩版差異（前者功能最完整、為預篩選 12 條件對照來源；後者為 Presenter-based 藍本）、`jet-template-v1.html` 與 drawio 流程圖條目；設計交付包 `03-bi-overview-spec.md` 的 UpSet 區塊補上 2026-07-30 移出範圍裁決註記（README 實作順序同步標示歷史文件性質）；`.gitignore` 移除指向不存在檔案的 `!appsettings.Development.json` 例外與誤導註解。驗證：`SkillExplicitInvocationPolicyTests`＋`SensitiveDataPolicyTests` 定向 398／398 全綠（run `20260817-054403512`），`AGENTS.md`＋`CLAUDE.md` 維持 199 行（22,735 bytes，Codex 32 KiB 預算的 69%），產品程式一行未改。

## 2026-08-17 — Harness 盤點收口：permissions 硬擋、skills 精簡與過時檔清理

承前一日的機制過審，本輪先補齊兩家官方文件與 harness engineering 的最新查證（記錄於 `agent-frameworks.md` 更新程序），再依使用者裁決落地四件事。一、Claude Code 側把三組本來只寫在 `AGENTS.md` 的禁令升級為 `.claude/settings.json` 的 `permissions.deny` 執行前硬擋（直接 `dotnet test`、`git add -A`／`--all`／`.`／`write-tree`／`stash --all`、機敏資料目錄寫入；讀取照常），並由 `SkillExplicitInvocationPolicyTests` 新增守衛防止 deny 清單被靜默移除；Codex 無 repo 可攜的逐命令等價物（approval／sandbox 鍵在專案層 `.codex/config.toml` 被忽略），跨 host 底線維持 `AGENTS.md`＋架構測試。二、八個 mattpocock workflow skills 中六個（`grilling`、`to-spec`、`to-tickets`、`wayfinder`、`prototype`、`implement`）因引入一個月零使用移除，保留 `code-review` 與 `diagnosing-bugs`；逐條原因記在 `skills-lock.json`。三、過時檔清理：孤兒稽核腳本 `report_template_audit.py` 與停在 07-06 的 `docs/jet-template.html` 移除，視覺參考改指 `design_handoff_jet_frontend/`；`p5-behavior-baseline.json` 原判過時，實測仍被兩個 `FolderProfileVerifierTests` 以本機保存的 P5 成品驗證「舊版封裝驗證」路徑，故還原保留並為驗證器補上 baseline 目錄缺失時的回退。四、output style 歸位：正本改為 `~/.claude/output-styles/`（CLI／桌面版／VS Code 共用），repo 只鏡像啟用中的 `natural-technical-conversation`，漂移的 `readable-technical` repo 副本移除。`AGENTS.md` 減行至 180、與 `CLAUDE.md` 合計 199 行回到 Claude 官方 200 行建議內，並把寫死的 Superpowers 版號改指 `skills-lock.json`。驗證：定向 398／398 全綠；Release 全套 3670 total／3662 passed／0 failed／8 登錄 skip（unexpected 0），+22 全為兩輪新增守衛案例。

## 2026-08-16 — 跨 host agent 機制過審與守衛補齊

對 Codex 與 Claude Code 共用的 agent 機制做了一次全面過審，只動文件與測試。最重要的發現是兩側守衛不對稱：Codex 的 `agents/openai.yaml` 一直有機器把關，Claude 側的薄入口卻完全沒有——少一個 wrapper、漏寫 `disable-model-invocation` 或把 canonical 正文複製進 wrapper 都不會有人發現，而 Claude Code 並不掃 `.agents/skills/`，缺 wrapper 等於該 skill 在 Claude 側不存在。`SkillExplicitInvocationPolicyTests` 因此擴充為雙側守衛，另把「Superpowers plugin 必須維持停用」這條規則也變成機器條件。

同輪修掉三處文件與現況不符：載入預算表停在 2026-08-03 的舊數字（實測已從 182 行成長到 202 行，超過 Claude 官方建議的 200 行）；`data/legacy-parity-work/` 早已是受 `.gitignore` 與機敏資料測試保護的第二個機敏路徑，但 `AGENTS.md` 與 Copilot 規則都沒寫；上游基準停在一個月前。重新查證後基準更新為 Superpowers v6.3.0 與 mattpocock `068b6e0`，並逐項比對 6.2／6.3 的新方法論——ceremony scaling、記錄裁定不停擺、同型任務批次、per-plan workspace 在 JET 都已有等價規則，因此只更新基準、不改流程。

## 2026-08-15 — 核心能力補完計畫結案：最終人工驗收與 PBC harness 修正

使用者完成最終一場制人工驗收，五項全部通過（匯入與配對、資料驗證、科目分類與篩選、六份報表抽驗、舊案相容），「核心能力補完與測試 Harness 現代化」計畫至此結案，master spec 依文件規則移出工作樹。同場對 PrivateParity envelope 的 PBC fixture 單點裁決「核准 harness 修正」，同日落地：`tools/verify-expected-skips.json` 新增 `PrivateParity` 段，以精確身分與逐字元 skip 理由登錄四個 `JET_PBC_DIR` PBC fixture 測試（資料齊備時轉綠仍合法）；envelope 政策改為只有未登錄的 PrivateParity 類別 skip 或零執行才構成 `PrivateDataExecution` 缺失；`verify.ps1` 的安全環境變數由實測不生效的 `dotnet test -e` 改為 process 環境變數傳遞並於子行程結束還原。修正後實機 `-Profile PrivateParity` 在缺 PBC 資料的本機首次誠實轉綠（17 項：13 過、0 失敗、4 個登錄 skip、exit 0），未登錄 skip 仍紅燈由新增的 sandbox 合約測試證明。

同場使用者另裁決：`data/temporary-test-case/` 對本專案 agent 開放常設本機唯讀存取，用於驗收產出與 `legacy/idea-tool.bas` 業務邏輯一致性比對，不需逐案 packet 核准；不進 git、不外傳、不寫進 tracked files、不在對話揭露客戶明細等禁令全部維持。條文正本在 `AGENTS.md` 機敏資料節。

## 2026-08-15 — 跨案件 PrivateParity 純驗證封板

以全新 `crosscase-pure-verification` 批次對 `case-A`／`case-B` 重跑完整 action journey（SQLite／DuckDB／SQL Server 各兩次）並重讀全部 legacy references，取代已不可重現的歷史 stage6／7 批次；歷史差異裁決不再沿用成白名單，每項差異由本輪 fresh capture 重算並重新分類。metric 層 241 筆分類差異中 JET 缺陷 0、provider mismatch 0、未分類 0；normalized content 三層（物理 storage、欄名對齊、逐欄值多重集）145 筆差異全數解析到封閉裁決；外觀 12 組比較 97,221 筆差異全由既有 Stage 9 封閉裁決分類、未知 0；provider 內容一致性 60 組比較 mismatch 0。

比較器把「值差異」與「儲存型別／欄位目錄差異」分維：legacy 明細頁是 IDEA `ExportDatabase` 的 typed／改名匯出，JET 依 §7.1 忠實輸出來源 raw lexeme 與實際欄名；名稱不同但值多重集相等的欄以 value-matching 證明等值，共有欄值差只剩已核准家族（INF 抽樣、trailing zeros／pair 邊界、情境 5／6 命中差、Part A totals），其中 case-A step4-1 的 12 欄差異量級全部 ≤ 2×14，精確對應已核准情境命中差的明細傳導。case-B field-info 的 legacy+2 差異依獨立來源型別 oracle 重分類為輸入資料條件，不再是 JET 缺陷。

PrivateParity profile 執行 17 項：13 項全數通過、0 失敗；僅 4 項 PBC fixture 家族（`JET_PBC_DIR`）因該私有資料不在本機而動態 skip，使 envelope 依既定政策維持紅燈——此為機器閘與資料可用性的結構性衝突，留待使用者裁決，不冒稱通過。未提供的公司案件仍未在本機復現、未驗證。產品程式一行未改；最終僅剩一場制人工驗收。

## 2026-08-15 — 整合封板與驗收候選

一般可交付候選已完成封板。Release restore／build 0 warnings／0 errors，ReleaseCandidate 的 Release、Provider、20 場 GUI、migration、report／package 五段依序全綠：Release 3623 total／3615 passed／8 個精確登錄 skip，Provider 277／277，GUI 20／20，migration 401／401 且七個 required families missing 0，report／package 440／440 且九個 required families missing 0。原始 sandbox restore 因網路權限受限而失敗，改在原生 Windows 網路環境從 Release 起點重新執行完整 composition 後通過；沒有把環境失敗冒稱成產品綠燈。

另以獨立 100K correctness smoke 取得 375／375，並完成正式發布與封裝完整性核對。候選符合目前 source 與發布執行檔、檔案集合封閉，沒有 loose build artifacts、runtime data 或其他 ignored work products 混入 package；精確發布身分只留在內部 receipt。Cobertura／touched-core gap 完整，GUI 全程離線、340 actions、0 screenshots、cleanup complete。

本階段沒有修改產品程式、wire contract、schema、審計規則、provider SQL 或可見流程，也未讀取 private fixtures；`PrivateParity`、opt-in scale 與人工驗收均未執行。整合候選 aggregate inventory 已凍結，「跨案件 PrivateParity 純驗證封板」成為唯一下一階段；尚未建立最終人工驗收 handoff。

## 2026-08-15 — Harness 完整循環與 lifecycle closure

六組 critical FsCheck properties 現為 Release hard gate，固定 Replay 並保存原始 seed／shrunk case；它們揭露 `TabularHeaderNormalizer` 對預先占用 suffix 的欄名會產生 collision，已依既有全域唯一契約修正。Release child 同步產生 Cobertura 與 machine-readable touched-core gap；Stryker 固定 4.16.0、四個 Domain／AuditCore pure targets 與專用 closed MTP assembly，保存 76 個 target-only mutants、4 個已分類 survivors 與其他 findings，但不以分數阻斷，也不納入 ReleaseCandidate。

Agent GUI inventory 由 16 增為 20，新增空案件、完整性不適格、stale artifact 與六階段完成四個 production-dispatcher lifecycle fixtures。最終 20／20、340 actions、0 screenshots；Selenium 4.46.0 與 EdgeDriver／WebView2 151.0.4129.78 全程離線，所有 run root 清除。正式 `Run` 現在會在共享鎖內以 `--no-restore` 重建 closed-probe helper，防止 source／binary 漂移；timeout evidence 會在呼叫前記下精確 probe／selector。

`ReleaseCandidate` 已解除 fail-closed 並依 Release、Provider、Gui、migration、report／package 五段 fail-fast。最終單一 composition 全綠：Release 3623 total／3615 passed／8 expected skips，Provider 277／277，Gui 20／20，migration 401／401 且七個 required families missing 0，report／package 440／440 且九個 required families missing 0。SQL Server-only migration families 由同輪已綠 Provider receipt 與 Affected migration outcomes 合併計數，不解除 Affected 的 provider 排除，也不重跑 SQL Server。產品 action、wire、schema、審計規則、provider SQL 與可見流程均未改；「整合封板與驗收候選」成為唯一下一階段，但本輪未開始。

逐階段 change manifest 採 immutable closed baseline。收尾 adversarial review 發現三個後補 allowlist paths 會被舊 Delta 錯報為 file added；現改成 exact scope qualification：24 個 baseline-covered modifications 保留完整 before／after，兩個既有 tracked 文件明示 pre-stage identity 不可得，一個 mutation project 以 baseline HEAD 與建立時間證明為本階段建立，其他任何 scope drift 一律 fail closed。

## 2026-08-13 — GUI harness 核心替換完備性復盤

依使用者要求重開第二階段後，除了重跑原有 gate，也以 adversarial read-back 補齊任意 read-only eval、app-exit、computed accessibility、prepared driver lease／SHA／reparse、helper／driver ownership、watchdog／Status、canonical evidence 與 duplicate-key／型別／順序 guards。PowerShell 現只呼叫 18 個固定 probe，Selenium helper 不再接受任意 JavaScript；scenario evidence 只做一次 Depth 40 canonical serialization，原子落盤與 stdout 使用同一 JSON，verifier 以 strict semantic tree equality 核對 aggregate 與 16 份 child evidence。

復盤最後揭露一個只有中文 evidence 才會觸發的 Windows encoding 缺陷：harness 的 16 場皆通過，但 redirected stdout 被系統碼頁轉壞，因此 verifier 持續以 `gui_result_invalid` 拒收。Producer／consumer 改為 strict UTF-8，並加入含中文 assertion 的 fake contract 後，Affected architecture／contract 323／323 與正式 GUI 16／16 全綠，316 actions、0 screenshots、無 runtime download，所有 root 清除且最終 Status inactive。臨時「不干擾前景」條件因會改變 Explorer、UIA、WebView2、focus 與視窗可見性 oracle、降低結果可信度，依使用者條件撤回；產品 action、wire、schema、規則、provider SQL、UI 與 `logicVersion` 仍未改。「Contract、schema v7 與 migration 骨架」至此成為唯一已解鎖下一階段，但本輪沒有開始。

## 2026-08-13 — GUI harness 核心替換

Agent GUI harness 保留原有 PowerShell process／fixture／scenario oracle／evidence 控制面，將 raw-CDP DOM／input core 完整替換為 persistent bounded `.NET` Selenium／EdgeDriver helper。Selenium 固定 4.46.0，EdgeDriver 只能由明示 `Prepare` 解析與快取；正式 `Run`／`Gui` gate 強制離線、implicit wait 為 0、每項 driver operation 最多 15 秒，helper／EdgeDriver 以精確 PID／start time 納入 status、watchdog 與 cleanup。產品 action、wire、schema、審計規則、provider SQL、UI 與 `logicVersion` 均未改變。

最終固定 inventory 16／16 全過，共 316 actions、0 screenshots；driver 與 WebView2 runtime 均為 151.0.4129.78，沒有 runtime download，所有 run root 已移除，收尾 `Status` inactive 且沒有 helper／driver orphan。中途評估「自動化不得干擾前景」會改變 Explorer、UIA、WebView2、focus 與視窗可見性的正式測試拓撲，因而可能影響結果品質；依使用者的條件撤回該臨時要求，最終 gate 使用原本可見 GUI 拓撲。

## 2026-08-13 — 核心能力補完與測試 Harness 現代化計畫核准

使用者核准下一階段的整合開發計畫，範圍包含過帳狀態與有效分錄母體、核准日同總帳日、人工／自動分錄資料品質、可擴充科目分類、借貸組合多選、未預期借貸組合零元邊界、typed RDE／進階條件，以及 xUnit v3／Microsoft Testing Platform 與 WebView2 GUI harness 現代化。計畫採十三個線性 fresh-session 階段，已登錄為 `docs/specs/2026-08-13-jet-core-completion-and-harness-plan.md`；核准當時唯一可開始階段是「測試平台直接切換」。

這次只建立計畫與接力入口，沒有執行開發、測試遷移或 GUI harness 修改，也沒有改變現行 action、schema、審計規則、provider SQL、前端行為或 `logicVersion`。計畫明確移除 1,000 萬列合成 GL 評測；100K correctness smoke 保留，既有 5M／20M benchmarks 維持 opt-in、非 release gate。真實案件 PrivateParity 只在最終階段本機明示執行，不納入日常 Release。

## 2026-08-13 — 流程總覽單一資訊歸屬 6/6 驗收結案

使用者完成流程總覽六項人工驗收並回報全過。六階段生命週期、總覽純檢閱不導航、每項資訊單一歸屬、等待前置說明、六階段與集中度兩層鍵盤焦點、1280px containment，以及三組分析按需展開至此正式銷帳。前一輪十二項 Working Paper／Excel／前端驗收維持結案，不因本輪回溯重開；目前沒有待使用者人工驗收項目。

## 2026-08-12 — Working Paper／前端 12/12 驗收結案與流程總覽狀態登錄

使用者完成同一場十二項人工驗收並回報全過，正式銷帳 Working Paper 封面嵌入物件、Step 2／3／4 範本樣式、legacy 多條件篩選、防案件特化複核，以及 1360px 半屏、欄位配對、「其他資料」、唯一資料夾入口、測試入口移除、摘要優先總覽與鍵盤焦點。Legacy parity 階段 1–10 至此完成自動封板與人工驗收；長期的五類差異登錄及三 provider 一致性規則已回流 `jet-guide.md`，完成的 master spec 由 Git 歷史保存，不再留在工作樹。

其後的流程總覽修正是獨立的純前端改善，不重開前述十二項。參照 MindBridge 的 guided workflow／risk review、Power BI 的 overview／KPI 定義、SAP Fiori 的單題卡片、GOV.UK task status、PCAOB 的 journal-entry 查核焦點與 WAI-ARIA dialog／tabs 後，總覽被明確定位為「案件狀態檢閱面」：六階段卡只在 modal 內選取一個共用執行紀錄，不關閉視窗、不切換主畫面，也不保存流程位置；主畫面的左側目錄與文件流維持既有流程導航。畫面採單一資訊歸屬：抬頭持有案件與期間、進度持有彙總、卡片只持有生命週期與主畫面位置、執行紀錄持有所選階段證據／阻塞、母體概況持有四個母體事實、分析共用一次範圍、技術資訊持有 provider／run／logicVersion。前五階段沿用既有 `stepGate`，最後階段沿用目前版本 Criteria Selection Report／Working Paper 的精確來源比對；不由前端推算新的審計結論。

本輪沒有新增或更動公開 action、payload、response、wire schema、資料表、審計規則、資料計算或 `logicVersion`。Release build 為 0 warnings／0 errors；總覽、響應式排版、既有 BI 與 harness affected suite 56／56 通過，parity comparator／definitive registry 35／35 維持通過；隔離 `frontend-usability` 為 33 actions／34 assertions，六階段逐一檢閱、單一資訊歸屬、1280px containment、Home／End、集中度內層頁籤焦點與 Escape 復原皆通過，正常退出且 run root 已移除。正式發布封裝與外部完整性檢查亦通過；精確封裝身分與汰換紀錄只保留在 agent 內部證據。

## 2026-08-05 — Legacy parity 審計差異與建案補償收口

Stage 6 識別的兩個 case-B FieldInfo 缺陷共用同一根因：legacy 對數值識別欄保留 `_Temp` 文字 shadow，並把 canonical 文字欄附加到欄位定義尾端；JET 原先只做 in-place rename。Stage 7 依兩份 legacy 腳本與 typed schema 證據補回這項投影語意，三 provider 完整 journey 後 6 筆 `JetDefect` 全部消失，immutable registry 只保留 235 筆既有已裁決差異。這是報表欄位定義的相容修補，不改規則判定、明細集合或回放文件，因此不推進 `logicVersion`。

同輪補上 `project.create` 的部分成功原子性。新案 `project.json` 先在同 root staging 完整寫入再原子發布；provider lifecycle 則拆為 ownership、materialize 與 final commit。SQL Server 以 Serializable registry key-range transaction 序列化同名建立，在取得本次新工作鎖後才於同一交易建 schema，等 response 與 session 都 ready 才一次發布 registry、access 與 `project.create` audit。commit 前失敗固定先放本次工作鎖、再 rollback ownership 與未提交資源、最後刪本機資料夾；reentrant lease fail closed，補償失敗保留 reconcile 錨點，也不以 `project.delete` audit 或刪除已提交資源模擬 rollback。這項收斂不新增 action、payload、response 或 error code。

## 2026-08-03 — 查核正確性收口人工驗收與匯出外觀複核

使用者完成一場制人工驗收，並回報六組檢查全過。完整性未通過的下游阻擋、KCT 缺少前置資料、作業中離場、多檔匯入失敗回復、Release 無 demo 與流程總覽文案至此銷帳；本輪此前保存的完整 Release 套件、Release build、隔離 GUI 與發布封裝驗證證據維持有效。

同日另依 `legacy/idea-script.bas`、`legacy/idea-tool.bas` 與 `data/` 六份範本複核正式 Excel。來源範本、程式內固定範本與發布封裝內範本逐一相同，五份實際匯出與一份以目前程式新產生的 WorkingPaper 也已渲染檢查；功能資料與主要版面沒有發現新的阻擋缺陷。但逐格外觀尚不能宣稱完全相同：WorkingPaper 欄位資訊頁未重現 legacy 明定的藍底紅字與黃色表頭，完整性差異提示未套 legacy 的 Calibri 12 粗體紅字，假日／科目配對動態表頭使用現行共用字型，且部分頁面有舊 script 未設定的固定窗格／頁面設定。原本由 IDEA `ExportDatabase` 產生的動態頁沒有舊版實際輸出檔可作精確樣式基準，因此後續若要求字型、框線、底色與欄列設定逐格一致，須另立修正計畫與樣式 oracle；這項外觀技術債不回溯否定本次功能人工驗收。

## 2026-08-01 — 尾段清理與需求銷帳

五個 deterministic demo actions 現在只以條件編譯納入 Debug 與不可發布的 AgentGuiTest；Release composition 不再註冊 `project.loadDemo` 或 `demo.*`，直接呼叫會得到 unknown action。靜態 facade 與 Domain 的 `ActionExecutionPolicy` 分類仍完整保留，測試則改由具名的 test-only dispatcher 執行 fixture，沒有把 demo handler 放回正式 Release wire。

日期維度新增持久化的 `calendarImported` 成功 marker：合法零筆假日／補班檔與從未匯入不再混為一談；`nonWorkingDays` 只要曾明示保存，即使是空集合，也會把選用任務列標為完成。SQL Server 案件會同步完整 project document 至 registry，讓 serverOnly 重新物化不會遺失這兩個狀態。流程總覽與預篩選 N/A 文案改成查核員語言；既有摘要內精確命中的舊工程理由由後端 renderer 正規化，前端不建立第二套語意。

本輪同時正式銷帳五項既有裁決：案件根維持 `%USERPROFILE%\JET`；KCT H 範例因鍵盤、觸控與螢幕閱讀器可及性而常駐顯示；Cr not B 確認為整張傳票完全沒有 B 類貸方；demo 只從 Release composition 移除；TB 期初／期末鍵值 join 需求取消，外部先合併成寬表仍是唯一權威流程。SQL Server 真實身分驗證、serverOnly 刪除、noAccess metadata 隱藏與 SQL Server 2022 全入口硬閘則只記入企業線上輪，不在本輪推測實作。

Release build 為 0 警告、0 錯誤；最終主定向 183 通過、0 失敗、1 項具名 SQL Server 環境略過，demo fixture 31 通過、0 失敗、2 項具名 SQL Server 環境略過；Debug 與 AgentGuiTest 的 composition／policy parity 各 47/47，本階段新增的 SQL Server registry round-trip 也已在本機 SQL Server 2022 Developer 真執行 1/1。四個前端檔案語法、54 項 ECharts option/lifecycle、可見字串掃描與 diff check 全綠，最終獨立複審無剩餘 P0–P2。下一個唯一可開始階段是整合封板與人工驗收候選。

## 2026-08-01 — INF 抽樣穩定雜湊與統計檢核

新建案件的 INF 抽樣現在以 `sampleSeedVersion: 2` 選用 AuditCore 定義的三輪 keyed Feistel PRF；SQLite、DuckDB 與 SQL Server 都只以 signed BIGINT 精確整數運算渲染同一排序鍵，抽樣仍在資料庫內以集合式 SQL 完成，正式樣本數仍是 59。固定 C# 向量、三 provider SQL 逐筆輸出、同 seed 的完整樣本與 legacy golden membership 均已在真實 provider 上驗證相等；100,000 列、32 桶的固定 sanity gate 得到 χ² 31.54368（上限 60）。

版本 marker 只新增在 `project.json`；SQL Server registry 原樣鏡射同一份 JSON，沒有新增 seed 資料表或推進 provider DB schema。無 marker 的既有案件永遠走原本線性排序；連 seed 都沒有的更舊案件仍回退 48271，因此既有案件不重算、不改抽。格式、位數、範圍、未知版本或 marker／seed 配對損壞都明確回 `file_read_error`，不靜默生成新 seed；serverOnly 案件也不會把損壞降級成找不到案件或物化本機快取。action、payload、response 與 `seed` number wire 形狀均未改變。

三 provider 定向驗收 7/7、SQL command snapshot 3/3；最終完整 Release 套件 2,664 通過、0 失敗、14 項具名外部條件略過，共 2,678，Release build 0 警告、0 錯誤。下一個唯一可開始階段是尾段清理與需求銷帳。

## 2026-08-01 — 匯入與篩選保存的整批原子性

多來源 GL／TB 匯入不再由前端逐檔送 action。單一 action 現在可帶一到多個來源，Application 先完成整批形狀與標頭檢查，SQLite、DuckDB、SQL Server 再各以同一 connection／transaction 寫入來源 metadata、staging、欄位定義與下游失效狀態；任一後檔讀取、寫入、取消或終檢失敗都回復整批，並保留呼叫前資料。舊單來源 payload 與 append 列序語意維持相容，進度事件 additive 帶回來源序號與總數。

`filter.commit` 的情境 definitions 與命中 hits 也收斂到 provider 的單一交易，不再先保存情境、再以第二個交易 materialize。失敗或取消會保留前一個完整 revision／hits，同一 payload 可直接重試；空批仍以同一交易清空兩者。這次沒有新增或改名 action，也沒有 schema 變更。

三 provider 故障注入最終 12 項全數執行通過；中段完整 Release 套件為 2,637 通過、0 失敗、14 項具名外部條件略過，共 2,651，Release build 為 0 警告、0 錯誤。下一個唯一可開始階段是 INF 抽樣穩定雜湊與統計檢核。

## 2026-07-31 — 完整性後端硬閘

完整性 part(a) 控制總數與 part(b) 科目差異現在由 AuditCore 形成單一適格裁定。`validate.run` 與持久化 summary additive 帶回 `eligibility`，`project.load` 只依 raw facts 由同一後端 renderer 補繪；前端不從控制總數、N/A 或差異筆數重算第二套判定。

沒有目前世代的適格 validation run 時，`prescreen.run`、`filter.commit`、Pre-screening Report、Criteria Selection Report 與 Working Paper 都在任何 SQL、保存或產檔前回 `completeness_prerequisite_failed`。Pre-screening Report 特別納入，是因 TB 重匯入會保留 prescreen run 卻清除 validation；Validation 三檔與 Account Mapping 仍保留為診斷／修復輸出，`filter.preview` 與唯讀查詢也不受擋。這次只增加 wire／`summary_json` 的衍生欄位與錯誤碼，未改實體 schema、完整性算法或 validation logic version。

SQLite、DuckDB、SQL Server 的專用 gate matrix 15 項全數執行通過；最終受影響套件 423 通過、0 失敗、0 略過，Release build 0 警告、0 錯誤，兩個 runtime JavaScript 檔案也通過語法檢查。下一個唯一可開始階段是 KCT 前置閘門與 KCT 一致性。

## 2026-07-31 — 流程總覽 BI 與正式發布人工驗收結案

S1–S3 流程總覽 BI、S6 多倍率 containment、S7 本地 Noto／ECharts 與 S5 正式發布封裝均完成。使用者完成一場制人工驗收，並明示除所附操作紀錄外其餘項目全部通過；因此大案、六份 Microsoft Excel 報表、Working Paper、前端整合、離線字型／圖表及 75%–200% 縮放矩陣均已驗收。封裝 verifier 與各已執行階段 receipts 的結案前版本由 Git commit `bf8aeea1` 保存。

附件中的三組真實執行摘要如下。時間是同一場使用者操作的觀察值，不是受控 benchmark、效能 SLA，也不能單獨證明跨 provider 的逐列、工作簿內容或 hash 完全相等。

| 專案 | Provider | GL／TB | GL 匯入 | GL 標準化 | 驗證＋3 報表 | 預篩選＋報表 | 篩選預覽 | 保存情境 | 條件報表 | Working Paper |
|:---|:---|:---|---:|---:|---:|---:|:---|---:|---:|---:|
| A | SQLite | 1,712,542×50／378×4 | 151.1 秒 | 38.1 秒 | 17,376 ms | 57,126 ms | 40 列／21 傳票：1,459 ms | 538 ms | 635 ms | 9,683 ms |
| B | SQLite | 20,260,435×14／447×7 | 248.1 秒 | 622.2 秒 | 313,812 ms | 724,373 ms | 1,170／757：26,156 ms；9／3：24,742 ms | 9,521 ms | 12,909 ms | 209,549 ms |
| B | DuckDB | 20,260,435×14／447×7 | 114.9 秒 | 289.3 秒 | 9,060 ms | 106,385 ms | 1,170／757：464 ms；9／3：520 ms | 447 ms | 118,924 ms | 13,917 ms |

專案 A 的可見驗證摘要為科目不符 226、傳票不平 0、抽樣 59、異常項次合計 1,234,289；預篩選五項為 0／3,080／40／195,053／50,765。專案 B 的 SQLite 與 DuckDB 可見驗證摘要同為 333／0／59／4，預篩選五項同為 0／2,132／1,170／4,983,176／1,390,975，兩次情境預覽也同為 1,170 列／757 張傳票及 9 列／3 張傳票；三組都成功產生 14 張工作表的 Working Paper。

本計畫的長期決策收斂如下：

- D1／D2：顯示用累積值、彙總與 ECDF 由後端在既有 run 計算並隨 `summary_json` 回放，前端只鏡射；沒有新增設計稿提出的三個 `query.*` action。
- D3 已由 D8 取代：三個 BI 圖表使用本地 ECharts 5.5.0 SVG renderer，Noto Sans TC／Noto Serif TC 與授權檔隨程式發布，不依賴外網或系統安裝字型。
- D4／D7：500 萬列 DuckDB 的完整 `prescreen.run` 中位數由 1,336.8548 ms 增至 2,937.5140 ms，增幅 119.7332%，已超過百分比門檻；因此不補跑 2,000 萬列 benchmark，S2b 永久採 13 條規則的全期單維長條。
- D5 保持「不適用」與有效 0／零母體分離；D6 將跨年月份鍵裁定為 ISO year-month，但因 D4／D7 降級，現行 wire 不需要月份軸。S3 固定 15 個零元／非零 1–2–5 級距，ECDF 只以非零元分錄為分母並保留 null 斷線。
- S4 多重命中 UpSet 已依 2026-07-30 使用者裁決正式移出範圍；未來若需求再起，須另立新計畫，不沿用本計畫的 run 時落地路線。

「複製紀錄」的現行 TSV 契約只有時間、層級與訊息，本次由使用者手動補上專案 A／B 與 SQLite／DuckDB。這不影響本場通過；是否內建案件／provider 欄位已列為後續改善需求，須另案定義格式。人工關卡通過後，Working Paper rollback adapter 清理成為唯一已核准的下一項獨立開發，但本輪未開始。

結案文件回流沒有修改產品程式、測試、action、contract、schema 或 provider SQL，因此沒有重跑產品 build／test。收尾 read-back 再次確認發布封裝完整性、`windows-handoff.md` 無待辦、已完成 master spec 的 repository 引用為 0，且 `git diff --check` 通過。

## 2026-07-30 — Claude Design 前端整合（結構與視覺）

設計交付進入 repository（`docs/design_handoff_jet_frontend/`）後執行整合。落地四項，全部只改 runtime 前端，未新增、改名或改變任何 action 語意，固定 `data-bind`／`data-action`／可存取名稱與 `JetApi` 呼叫一律沿用：補齊設計 tokens 為 CSS 變數；案件資訊列改為五欄並把「進度」正名為「目前步驟」（位置），與左欄目錄的「進度」（已完成步驟數）語意分離；流程總覽加入執行識別基準行、母體概況四欄 KPI（直接鏡射 `validate.run` 的 stats）、空狀態與唯讀聲明；匯出成功後在步驟內顯示完成摘要。

刻意未採用設計原型的三處結構：不以原型的六步驟陣列取代 `Store.STEPS`（原型把欄位配對併入匯入並多出「完成」步）、不新增第六個「完成」步驟（改為匯出成功後的步驟內狀態）、不把區塊①的逐項母體完整性核對複製進總覽（逐項判定只在步驟 3 呈現，避免第二個權威判定面）。這三條邊界由新增的 `DesignIntegrationFrontendTests` 機器把關。

在這一輪前端整合當下，設計交付的 BI 圖表區塊③–⑥尚未實作：當時判斷需要三個後端有界聚合查詢，屬後端功能而非前端整合，故未先寫入 action manifest。後續盤點改以既有 run 回應落地 S1–S3，S4 UpSet 移出範圍；最終結論見 2026-07-31 里程碑。

## 2026-07-30 — 文件收斂與 Claude Code 交接

現行文件重新收斂為使用者驗收、開發現況、權威規格與少量可重現 evidence；已完成的規劃草稿由 Git 歷史保存，不再留在工作樹形成第二份現況。`windows-handoff.md` 改為不需資訊背景也能照做的三項人工驗收清單。

同日重新核對 2026-07-21 分階段主計畫。16 個產品與自動化階段都有現行程式、代表性測試或 receipt；唯一未完成的是需要使用者原始大案與 Microsoft Excel 的最終人工關卡。先前沒有下一輪開發指令符合接力規則，不是漏做。使用者後續提出 Claude Design 前後端整合，因此另建立一次性的 `claude-code-handoff.md`；該交接已於同日整合完成後移除，結論見上一則紀錄。

## 2026-07-28 — 大案 Excel 與 Working Paper 驗收修補

使用者以 20,260,435 列 GL、447 列 TB 的實際案件驗收時，發現三個阻擋問題：報表欄位會換行、裁切或顯示 `###`；Working Paper 在 Step 3 完成後長時間沒有進度；訊息紀錄缺少一致的耗時與最後進度。

修補後，六份報表會用完整資料計算欄寬，一般資料格不再自動換行、縮排或縮小字型。Working Paper Step 4 改為單次、由命中傳票開始的串流查詢，Step 4 與 Step 4-1 也會在耗時作業開始前先顯示進度。訊息面板新增「複製紀錄」，長時間作業的成功、失敗與取消都會保留耗時；失敗或取消另保留最後處理位置。

Release build、完整測試、三種資料庫、隔離 GUI、六份測試報表及正式發布封裝均完成自動驗證。這一輪結束當下仍待 Microsoft Excel 與原始大案重驗；後續已於 2026-07-31 的人工場銷帳。

## 2026-07-28 — 六報表與正式候選整合

Validation Report、Account Mapping、INF Report、Pre-screening Report、Criteria Selection Report 與 Working Paper 已串成同一條正式案件流程。報表都直接填入固定範本副本，保留範本的圖片、樣式、工作表、公式、保護與列印設定。

整合時完成 Release、SQLite／DuckDB／SQL Server、完整測試、隔離 GUI、六本報表渲染及發布封裝檢查。首次封裝其後被真實大案驗收發現的問題取代；該階段結束時的修補封裝也在 2026-07-31 人工驗收前被更新版本取代。精確汰換紀錄由內部 receipt 與 Git 保存。

在這次整合前的累積變更曾依使用者指示完成一次版本控制封板與推送。其後的大案修補、前端與 BI 變更已由使用者在 commit `bf8aeea1` 推送至 `origin/main`；agent 未自行發動版本控制。

## 2026-07-26 至 2026-07-27 — Working Paper 大型輸出重構

Working Paper 從「先產生另一份 workbook，再合併回範本」改為直接在範本副本上以 OpenXML 串流寫入。大型資料只保留目前列與磁碟暫存，不把整份明細載入記憶體。

Step 4-1 的欄位、資料型別、排序及標記語意依 Legacy 結果凍結，接著改為單一 prepared session 與單一順序 reader。固定 1,048,571 列的 DuckDB 基準中，新的輸出路徑相較凍結舊路徑超過五倍門檻，且輸出語意指紋保持一致。

## 2026-07-25 至 2026-07-26 — 六份報表直接填範本

Account Mapping、Validation、INF、Pre-screening、Criteria Selection 與 Working Paper 逐步改為直接填固定 Excel 範本。報表作者只負責版面與寫檔；要輸出哪些工作表、哪些明細及哪些審計文字，由 AuditCore 的 finalized plan 決定。

Validation 與 Pre-screening 對超大量異常只保留摘要，不嘗試把百萬列異常全部展開到 Excel。Criteria Selection 則依篩選情境輸出完整傳票內容。Field Info 統一使用同一份 GL／TB 欄位定義，供 Validation 與 Working Paper 共用。

## 2026-07-21 至 2026-07-22 — 本機優先、專案根與 GUI 自動化

正式本機專案根切換為 `%USERPROFILE%\JET`。專案選擇畫面先顯示 SQLite／DuckDB 本機案件，只有使用者主動同步時才查 SQL Server。檔案選擇、資料夾開啟、跨根複製及多視窗隔離都補上明確的操作與錯誤邊界。

同一時期建立不可發布的 `AgentGuiTest` 組態及受控 GUI harness，用來測試真實 WebView2、Bridge 與正式 handler 接線。這套工具只能證明自動操作結果，不能取代使用者在正式 Release、Excel 或企業環境中的人工驗收。

## 2026-07-17 至 2026-07-19 — 審計業務核心集中化

`JetAuditProgram` 成為 AuditCore 唯一 facade。案件、匯入、配對、參考資料、資料驗證、預篩選、進階篩選、正式報表與 Working Paper 逐步收斂到 typed `Plan → ExecuteAsync → Finalize → Explain` 生命週期。

Application 保留流程編排與傳輸資料塑形；Infrastructure 執行資料庫與檔案 I/O；Domain 保持純規則與共用契約。審計規則、失效政策、報表工作表與文字決策不再散落在前端或個別 handler。

## 2026-07-10 至 2026-07-14 — 本機案件與 Excel 報表基礎

完成本機案件的建立、重開、刪除、鎖定、原子儲存與六份正式報表的第一版。長時間作業加入取消與進度；報表置換失敗時保留上一份有效檔案。

2026-07-12 的 Windows／Excel 人工場曾完成當時的本地主線驗收。後續架構與大型輸出重構幅度較大，因此不能沿用該舊候選；新的修補候選已於 2026-07-31 重新通過人工驗收。

## 2026-07-03 至 2026-07-08 — 三種資料庫與 SQL Server 控制面

SQLite、DuckDB 與 SQL Server 共用同一套 repository 契約，資料庫差異由 Infrastructure 吸收。SQL Server 改為單一 `JET` 資料庫、每案一個 schema，並加入專案登錄、租約鎖、schema migration、容量資訊及控制面 audit log。

產品開發以本機 SQLite／DuckDB 為優先。SQL Server 的公司帳號、多人環境、授權與管理員操作仍延後，範圍見 `sqlserver-online-handoff-deferred.md`。

## 2026-06-20 至 2026-06-29 — 核心流程、篩選與報表起點

建立 GL／TB 匯入、欄位配對、資料驗證、預篩選、進階條件篩選、分頁明細、篩選情境、傳票／分錄標記矩陣及第一版 Working Paper writer。

進階篩選介面經多輪人工回饋後，收斂為單一條件建構器與清楚的 AND／OR 層級。規則名稱不再使用容易混淆的流水代號，改以具體中文名稱與穩定英文識別碼記錄。

---

## 紀錄粒度分界：2026-06-29 之前

上面到「2026-06-20 至 2026-06-29」為止的條目，來自後期專案的開發日誌。該日誌只保留會影響產品理解的
重要里程碑，因此 2026-07-08 以前的內容是事後補寫的區間摘要，不是當時的逐日紀錄。

下面的條目來自早期專案的開發日誌，是 2026-06-04 到 2026-06-23 的逐日紀錄，粒度細很多。兩段在
2026-06-20 到 2026-06-23 這四天重疊：上面那筆區間摘要與下面的逐日條目描述同一段工作，摘要較粗但涵蓋
到 2026-06-29，逐日紀錄較細但只到 2026-06-23。兩者沒有矛盾，先看逐日條目再看摘要即可。

---

## 2026-06-23 — 空值測試「日期區間外」改以核准日判定(對齊舊工具)＋ 控制總數/空欄防呆稽核修正

承 2026-06-22 三顧端到端稽核(JET vs 舊 IDEA 工具)發現的三件事,本輪修正並以同一份指令重跑稽核驗證:

- **A 完整性 part(a) 控制總數失效範圍過廣(真實隱患,已修)**:`RuleRunResultReset` 把 `gl_control_total` 與規則結果一起清,但它的上游只有 GL target;TB 投影／科目配對／行事曆／授權清單匯入(與 GL 無關)會連帶清掉它,使「先 commit GL、後 commit TB」這個 GUI 最自然順序下,validate.run 的 completeness.partA 變全 null(控制總數核對形同沒跑)。修法:`gl_control_total` 移出共用 reset,只由 GL 投影 upsert(它本就覆寫)。Infra 紅→綠測試 + Application 測試(原本鎖「行事曆匯入會清 gl_control_total」的舊行為,更正為「應存活」)+ 真實資料 smoke 改回自然順序端到端驗證(part(a) 在 12 萬列上 rowCountMatch/amountMatch=true)。
- **B 必填文字欄整欄空白防呆(已加)**:三顧 JE 有兩個「摘要」欄(一空一有值),配到空的會讓 blank_description 全列誤命中、症狀到驗證才浮現。`mapping.commit.gl` 投影提交前偵測必填文字欄(傳票號/科目編號/科目名稱/摘要)整欄空白,回非阻斷 `warnings`(指名必填欄與所配來源欄),前端提交成功後以警示色就近顯示。Domain `ProjectionResult.Warnings` + 共用 `GlMappedColumnAudit` + 雙 provider 偵測 + manifest + 前端 + 雙 provider 測試。
- **空值測試第四旗標「日期區間外」改以核准日判定(語意決策,使用者裁定)**:原以**過帳日**(post_date)判定,三顧得 0;舊工具以**核准日**(approval_date)判定得 7053(7053 張傳票在期末 2025-12-31 後、延至 2026-01-09 才核准——典型期末操縱指標)。使用者裁定改用核准日對齊舊工具。改 7 處述詞(雙 repo 的 count／明細 CASE／明細 WHERE + `NullRecordsCategoryPredicate` 分頁)`post_date`→`approval_date`;核准日(docDate)未配對時 NULL 不命中。顯示的日期欄維持 post_date(該列真實過帳日,不動 wire 契約),前端分類標籤改「核准日不在期間」。demo 的 20 張期末後核准種子(×2 列)現在也正當地命中此旗標(outOfRangeDateCount 0→40),相依測試同步。guide §4／manifest／前端規格已載明用核准日。
- **INF 回放最近 run 同-tick 平手(順手強化)**:`InfSamplePageSql` 改 `MAX(rr.run_id)`,兩次 validate 落在同一 100ns tick 時確定性取單一 run_id,消除 SQL Server `=(多列子查詢)` 報錯;guide §4 INF hash 描述對齊實作的 `(source_row_number × seed)` 鍵。
- **驗證**:全套件綠(SQLite + SQL Server LocalDB parity 實跑)。重跑三顧稽核:四個驗證維度與舊工具一致、part(a) 在自然順序下存活;空值三類在改用核准日後,連「核准日離期」也與舊工具 7053 對齊。**保留的裁決點**:核准日離期由 null_records 第四旗標承接後,與預篩選「期末後核准」(門檻為期末財報準備日、屬 row-tag)語意/門檻仍不同,非等價;若需更嚴謹另議。

## 2026-06-22 — 進階篩選 UX 三修:必填提示上主畫面、金額欄具名、條件依審計意圖分組

進階條件篩選的三個體驗問題(使用者回報,定調為 UX 非程式漏洞)。先讀 minimalist-ui 與前端規格,並查 NN/g(表單錯誤、chunking/漸進揭露)與查詢介面 UX 後實作:

- **必填提示原本只進可收合的訊息欄(全域問題)**:沒填情境名稱/動機時,後端 `invalid_scenario` 經 `Ui.run` 落到右側「狀態與訊息」面板;面板一收合,主畫面毫無警示。改為前端**就近驗證**:按〔預覽〕/〔保存〕當下,未填欄位標紅框、欄位下方一行紅字、按鈕上方一條彙總提示(`form-notice`)列出缺什麼並聚焦第一個有問題欄位;補齊即時放寬(不在打字途中責備)。做成 `ui-core` 全域 helper `setFieldError`/`clearFieldError`,其他步驟的必填表單可重用。純呈現——後端仍是權威。NN/g:錯誤要就近、多重線索(框＋字)、別只靠顏色。
- **金額欄只有隱晦的「|金額|」**:那是數學絕對值記號(不分借貸、看金額大小),太工程化。改成具名欄位標籤「金額(絕對值)」+ tooltip「不分借貸,以金額大小比較」,並把數值/尾數零/張數等控制項的前導字一律升為清楚的 `rule-row__field-label` 膠囊(取代灰色小 `rule-row__sep`)。
- **快速加入一長排鈕太雜亂**:原按資料格式(文字/數值/日期)散著、後補的邏輯型別擠成一排。改按**審計意圖**分四組——風險預篩選訊號 / 依欄位內容 / 依分錄性質 / 進階樣態分析(每組 1–4 項)。快速加入每組一列(小標題＋該組鈕),型別下拉以同一套 `optgroup` 分塊。NN/g chunking:把一長排切成幾塊,使用者只看自己要的那塊。

- **關鍵決策**:(1) 分組純前端呈現,條件 `value`(AST 型別)與 wire 契約不動,故不需改 action manifest。(2) 型別中文名一律守 `jet-guide.md` §4 命名登錄表——「自訂關鍵字/自訂尾數位數/自訂編製人員張數/自訂科目張數」維持原名(「自訂」前綴有審計意義:區分使用者自訂門檻與固定門檻的預篩選版本),只把登錄表未涵蓋的 `numRange` 由「數值區間」正名為「金額區間」以對應金額欄。(3) 驗證 helper 收斂在 `ui-core`,不在各步驟各刻一套。
- **驗證**:build 0 警告 0 錯誤。屬純前端視覺/互動(jet-dev-loop 明示這類工作以 minimalist-ui 為準、不走 C# 測試先行;前端無 JS 測試框架,WinForms/WebView2 E2E 為 jet-testing 硬邊界)。實際畫面行為(分組版面、紅框/紅字提示、收合訊息欄時仍看得到警示)屬 GUI,**待使用者實機驗收**。前端規格 `docs/jet-frontend-description.md` §3、§10 已同步。

## 2026-06-22 — 完整性測試誤判修復:GL 退化母體投影守門

真實客戶案件(三顧,SQLite)完整性測試「284 個科目不符」,但 GL 母體理應與 TB 對得起來。盤查 `jet.db` + 事務所 WorkingPaper/ValidationReport + CaseWare IDEA 前處理 log 後定位:

- **根因(配對陷阱,非 JET 計算 bug)**:GL 借/貸金額欄誤配到「本幣借方**總**金額」/「本幣貸方**總**金額」——這是**傳票總額**,同一張傳票每列都填相同的總借＝總貸。`DualAmount` 投影逐列算「借 − 貸」恆為 0,整張 12 萬列 GL 金額全投影成 0,完整性測試忠實加總出每科目 GL=0 → 全不符。改配**列層**的「本幣借方金額」/「本幣貸方金額」後,每科目與 TB 精準對上(例:4111 銷貨收入 −799,711,627 == TB),與事務所 ValidationReport「差異數均為 0」一致。投影碼與完整性測試皆正確。
- **修法(穩健解,使用者裁定)**:在 GL 投影提交前加「退化母體守門」——母體非空、無列級錯誤,但借貸總額皆為 0 時,整批 rollback 並回新錯誤碼 `gl_amounts_all_zero`(訊息指出「疑似配到傳票總額,請改配列層借/貸金額欄」),不讓壞母體落地、也不再以兩步後「完整性全科目不符」這種看不出原因的形式呈現。判定與訊息單一事實於 Domain `GlProjectionGuard`,SQLite/SQL Server 兩 repo 共用既有累計的母體借貸總額(零額外查詢);契約先行已加 manifest 錯誤碼。空母體不在守門範圍(由既有路徑處理)。
- **驗證**:build 0 警告;全套件 **947 綠**(0 failed / 0 skipped,含 SQL Server LocalDB parity):Domain BVA + 雙 provider「退化母體 → 丟 `gl_amounts_all_zero` + rollback」整合測試。**既有三顧專案**需回「欄位配對」把 GL 借/貸改配列層金額欄、重新提交即通過(守門此後會擋住總額欄誤配)。

## 2026-06-22 — 修復 WebView2 檔案對話框 reentrancy 崩潰(host 端)

F5 偵錯時觸發「選單一檔案」後整個 app 閃退、偵錯主控台只有模組載入訊息、無 .NET 例外。從 Windows 事件記錄(Application Error:失敗模組 `EmbeddedBrowserWebView.dll`、例外 `0x80000003`)與 JET 診斷日誌(最後一筆 `host.selectFile start` 無對應 end)定位:崩潰在 WebView2 原生瀏覽器程序,觸發點是檔案對話框。

- **根因**:`Form1` 的 `PickOpenFileAsync` / `PickOpenFilesAsync` / `PickSavePathAsync` 在 `JetWebMessageBridge.OnWebMessageReceived`(WebView2 的 `WebMessageReceived` 事件)堆疊內**同步**呼叫 `OpenFileDialog` / `SaveFileDialog` 的 `ShowDialog`。WebView2 不支援在事件處理常式內同步開 modal UI / 巢狀訊息迴圈——會以 reentrancy 崩潰(官方 Threading model › Reentrancy 明載「請把工作排到事件處理常式完成之後」;社群 WebView2Feedback #2946 / #4648 / #3028 同 `0x80000003` + `EmbeddedBrowserWebView.dll` 簽章)。`host.selectFiles`(複選)先前能用是時序僥倖,三個方法共享同一缺陷;同檔 `RequestExit` 早已用 `BeginInvoke` 規避同類重入,但對話框方法沒套到。
- **修法**:抽私有 `ShowDialogDeferredAsync<T>`,以 `BeginInvoke` 把對話框延到事件處理常式返回後的下一個 UI 訊息泵回合執行(bridge 端 `await` 會先讓 `WebMessageReceived` 返回、原生回呼退棧),結果經 `TaskCompletionSource` 回傳。語意等同官方建議的 `SynchronizationContext.Current.Post`,但 `BeginInvoke` 不依賴 `SynchronizationContext.Current` 非空、可從任一執行緒穩健 marshal 回 UI 緒。只動 Form1 三個 host 對話框方法 + 一個 helper,**不碰任何 action 契約與 Domain/Application/Infrastructure/Bridge 邏輯**。
- **驗證**:build 0 警告 0 錯誤;全套件 **938 綠**(0 failed / 0 skipped,SQL Server LocalDB parity 實跑)。host shell 無自動化測試覆蓋(jet-testing 禁 WinForms/WebView2 E2E),「反覆操作各檔案對話框不再閃退」屬 GUI 行為,任務卡見 `docs/windows-handoff.md`,**待使用者實機驗收**。

## 2026-06-22 — 文件碎片整理:散落規則回流、暫存清除、設計快照收斂

盤點前幾個 session 的 agent 殘留(任務 brief/report/review diff、superpowers 實作計畫、各輪設計快照),把散落但仍有效的規則搬回各自的權威出處,清掉純暫存碎片,並依文件生命週期收斂已驗收的設計快照。三個唯讀稽核 subagent 先把碎片裡看似政策/邊界/慣例的內容撈出比對,確認 95% 已被權威文件涵蓋,只有少數幾條尚未落到權威文件。

- **保留(各歸權威,不做大雜燴檔)**:工程品味準則(資料結構優先、深模組窄介面、DRY 門檻 3、註解寫為什麼、不碰無關碼、複審分 Critical/Important/Minor)→ `jet-dev-loop` 的 `principles-map.md`;「SDK/函式庫 API 一律用 microsoft-docs 查證、不憑記憶」→ `jet-dev-loop` SKILL 常見錯誤;「未經使用者 GUI 驗收不得標『已驗收』、測試數字據實」誠實狀態鐵則 → `docs/README.md` 寫作規範第 4 條;SDD 過程完全不 commit、需 review 隔離時用 tree-diff(`git write-tree`,不落 commit)→ `AGENTS.md` Version Control;完整性 part(a) 控制總數於完整管線後恆 `na` 的回歸 → `docs/development-status.md` 技術債表(原本只埋在更新長段落)。
- **清除純暫存(未進版控)**:`.git/sdd/`(129 個 task brief/report/diff/snapshot,含 `workpaper-synthesis.md` 與 `xlsx_inspect.py`)與 `.superpowers/`(37 個 sdd 紀錄 + brainstorm 殘留的 `server.pid`)。`workpaper-synthesis.md` 含真實客戶名且其方法學已被 `jet-guide` 與 E1 設計快照吸收,故不另存入庫。
- **收斂設計快照**:刪除 16 份已 GUI 驗收或已落地、且權威已完全轉移到現況規格的 `docs/specs/*-design.md`(2026-06-11/12/14 全部、06-19 的 calendar/inline-header/mapping-ui/reference-data/reimport、06-20 的 rule-corrections/workpaper-data-schema、gl-line-item、harness-tdd、import-preview、validation-testing);保留 7 份仍待 GUI 驗收的(D1 全量分頁、C 編製人員、低頻科目、D2 tag 矩陣、測試案件資料、E1 匯出 writer、命名/UX 修正)。`docs/superpowers/`(13 份實作計畫 + 1 份已被 jet-dev-loop skill 吸收的 dev-loop 設計稿)整個移除:active 工作的單一事實來源回到 `docs/specs/` 設計快照,實作軌跡的歷史脈絡由本紀錄承載。`windows-handoff.md` 已完成段指向已刪快照的連結改指向本紀錄。
- **驗證**:純文件與 skill 變更,未動任何 production 程式碼,build/test 不受影響。版控待使用者。

## 2026-06-22 — 中止的測試 coverage 強化任務收尾

Visual Studio GitHub Copilot 的 Test 任務因額度中止，留下大量已生成但未收尾的測試與測試專用 production seam。本輪先建置中止現場基線，再依 `jet-testing` 規範逐項審查 oracle、測試層級與 test smell，不以百分比作為唯一目標。

- **保留的有效覆蓋**:金額溢位與日期模式邊界、GL/TB 投影錯誤、CSV/XLSX 空檔與解碼失敗、handler 負向 payload、診斷日誌結構、SQL dialect、bulk-copy reader 契約、SQL Server 預覽/開發檢視/訊息留存與 provider repository 真實資料庫行為。
- **移除的無效填充**:以 Moq 設定介面回傳值後再斷言同一值的自驗測試、違反專案邊界的 WinForms/Form1 自動化、永久 `Skip` 的不可達分支，以及為 Bridge coverage 反向擴大 production 抽象的 test-only seam。host boundary 測試改為手寫 recording stub，不保留 Moq 相依。
- **production 邊界**:業務邏輯與 wire contract 皆未變更;只在專案檔加入 `InternalsVisibleTo="JET.Tests"`，供 Infrastructure 內部 adapter 契約測試。
- **驗證**:`dotnet restore` 成功;build 0 warning / 0 error;完整 SQLite + SQL Server LocalDB 套件 **938 通過 / 0 失敗 / 0 略過**，約 1m45s。Microsoft Code Coverage 重算 production line coverage 為 **95.29%**;未為追求 100% 而自動化 WinForms/WebView2、source-generated regex 或私有不可達分支。

## 2026-06-22 — 案件名稱資料夾命名 + 套用測試案件不跳步 + 授權編製人員預覽

使用者實機操作後三項收尾修改;設計/計畫見 `docs/specs/2026-06-22-project-naming-and-ux-fixes-design.md`、`docs/superpowers/plans/2026-06-22-project-naming-and-ux-fixes.md`。

- **案件名稱即資料夾名**:`project.create` 新增**選填** `caseName`,提供時即作為 `projectId`/資料夾名(維持「資料夾名==projectId==DB鍵」不變式),取代雜湊;未提供回退 32-hex GUID(既有測試/程式化建立**零 churn**),UI 表單強制必填。新增 `ProjectNameRules`(Domain)為字元白名單驗證器**兼 path-traversal 守衛**,且為舊 32-hex GUID 的**超集** → 既有專案零遷移;`JetProjectFolder.IsValidProjectId` 委派之。唯一性:同名資料夾即擋(`ProjectCreateHandler` 的 `FindAsync` 提早攔 + `JsonFileProjectStore.CreateAsync` 防覆寫殘存/損壞資料夾);SQL Server 另防「不同名稱淨化後撞同庫名」(新 `IProjectDatabaseInitializer.DatabaseExistsAsync`,建檔後檢查、碰撞則回滾資料夾,不動既有庫)。
- **決策:caseName 選填 + GUID 回退**:`project.create` payload 在約 15 處測試各自內嵌組裝(無中央 helper),強制必填會 churn 全部且無使用者效益;選填把「必填」落在真正重要的 UI 層、契約向後相容。
- **「套用測試案件(建立)」不跳步**:`applyLoadedProject(data, options)` 新增 `stayOnCurrentStep`;create 的 demo loader 套用狀態後**停在建立案件**、不自動前進匯入(正式「建立案件」按鈕與開啟舊案 resume 仍照常導覽)。
- **授權編製人員預覽**:`query.dataPreview` 新增 dataset `authorizedPreparers`(Domain enum + TryParse;Sqlite/SqlServer 雙實作查 `target_authorized_preparer`、column `preparerName`、鏡射 accountMappings 預覽);前端資料預覽下拉 + 匯入卡片「預覽授權清單」鈕。
- **契約先行**:manifest 補 `project.create` 完整契約列(原本缺,含 `caseName`)、`query.dataPreview` 允許 dataset 加 authorizedPreparers、`project.loadDemo` 回應加 caseName。
- **驗證**:build 0 警告;全套件 **750 綠**(0 failed / 0 skipped,設 `JET_SQLSERVER_CONNECTION` 時 SQL Server parity 實跑;新增 `ProjectNameRulesTests`、caseName 建案行為、SqlServer 淨化碰撞回滾 `[SqlServerFact]`、AP 預覽雙 provider)。final Opus 整體複審 **READY-TO-MERGE、無 Critical**;其 Important(同名但 project.json 損壞的資料夾會被靜默覆寫)已修(`CreateAsync` 防覆寫)。#2 不跳步屬純前端 GUI 行為,依 jet-testing 硬邊界不做 E2E,交 `/verify`/使用者驗收。

## 2026-06-22 — 安全相依升級:覆蓋 SQLitePCLRaw 至 3.0.3 修補 CVE-2025-6965(SQLite 記憶體損毀)

NuGet 稽核(NU1903)報 `Microsoft.Data.Sqlite` 10.0.6 傳遞帶入的 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 具 High 弱點([CVE-2025-6965 / GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)):SQLite < 3.50.2 當聚合查詢 aggregate 詞數超過可用欄位數 → 欄位索引截斷成負值 → 越界讀寫、記憶體損毀。JET 實際暴露面低(SQL 全由 Infrastructure 組、參數化 + 欄位白名單、前端零 SQL、無 SQL console、`jet.db` 本機產生,無從注入惡意聚合查詢),但審計工具不宜留 High,故修。

- **修補路徑(為何不能只升 MDS)**:SQLitePCLRaw 無 2.1.x 小修版(2.1.11 後直接跳主版號 3.0.x);且查 nuspec 確認**連最新 `Microsoft.Data.Sqlite` 10.0.9 仍把 SQLitePCLRaw 下限鎖 2.1.11**,Microsoft 在 .NET 10 服務線尚未採用 3.0.x → 升 MDS 修不掉。改在 `JET.csproj` 顯式釘 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3(3.0.3 ≥ 2.1.11 滿足 MDS 下限約束,不降版/不衝突)。
- **相依圖結果**:解析為 `SQLitePCLRaw.{bundle,core,provider,config}_e_sqlite3 3.0.3` + 原生 `SourceGear.sqlite3 3.50.4.5`(SQLite 3.50.4 ≥ 3.50.2)。3.0.1 起原生套件由 `SQLitePCLRaw.lib.e_sqlite3` 改名 `SourceGear.sqlite3`,**被點名的有漏洞套件因此完全離開相依圖**(非僅升版)。
- **主版號跳躍相容性(實證,非臆測)**:SQLitePCLRaw 2.1→3.0 在 `Microsoft.Data.Sqlite.Core` 10.0.6 下 runtime 相容——`dotnet list --vulnerable` 零弱點、`dotnet build` 0 警告 / 0 錯誤、全套件 **715 綠 / 0 失敗 / 0 略過**(設 `JET_SQLSERVER_CONNECTION`、SQL Server parity 實跑,1m36s);大量直接載入並操作真實 SQLite native lib 的測試(`DemoRuleOracleTests` 全 demo 管線、schema 遷移、各 keyset 分頁 repo)全綠,證明 JET 實際用法相容。
- **驗證/部署**:NU1903 於稽核與 build 皆清除;修補後原生 `e_sqlite3.dll` 已落 App 輸出 `runtimes/{win-x64,win-x86,win-arm64}/native/`。GUI F5 冒煙依 jet-dev-loop 邊界(agent 不驅動 GUI)交使用者/`/verify`;測試宿主即 `net10.0-windows`、已實載新原生 lib 並執行 SQLite 操作,App 用同一機制。改動僅 `JET.csproj` 一行 + 本紀錄,無契約、無業務邏輯變動。

## 2026-06-21 — 測試案件資料擴充 + 配對介面優化(demo 規則 oracle:7000 傳票/150 科目 baseline+seed、已提交配對二維預覽、介面命名白話化)

使用者實機操作後提的三項優化,合一份 spec/plan、subagent-driven 八 task 落地(全 Opus 4.8、tree-diff 未 commit)。設計快照見 `docs/specs/2026-06-21-test-case-data-and-mapping-ui-design.md`。核心是把 demo 測試案件從「規模小、規則靠湊巧命中」重設計成「規模真實、每條規則有已知確定命中數」的 TDD oracle,使「跑測試案件→斷言每條規則精確命中」成為固定的測試與驗收程序。

- **A 配對介面命名白話化**:切換鈕兩名稱「經典三欄表/二維配對表」→「簡易清單/對照表格」。內部值 `classic`/`grid` 與所有邏輯不變,純顯示字串(零行為、零契約)。
- **B 已提交配對改二維預覽**:欄位配對「已提交」綠燈摘要卡從 key→欄名 pill 清單改為唯讀二維對照表(來源欄為表頭、其下標對應的 JET 欄位、再附該批次前 10 列樣本;GL flag 模式另補「借方代碼字面值=1」),沿用既有 `.map-grid` 樣式與 `query.dataPreview` 惰性樣本快取。審計員一眼看出哪欄對哪欄、實際長什麼樣。純前端零邏輯。
- **C demo 重設計為規則 oracle(主體)**:`DemoDataFactory` 重寫為 7,000 張傳票(每張固定 2 行=14,000 列)、150 科目,**baseline + seed 兩層**——核心不變量是「baseline 對每條規則貢獻 0 命中、每個 seed 群組貢獻一個已知數」,故每條規則總命中數 = 其 seed 常數。新增 `DemoRuleOracleTests` 端到端跑 `prescreen.run`/`validate.run` 斷言 13 條規則精確命中(= 具名常數)+ 各以獨立 set-based SQL 重算交叉驗證 + 雙 provider `[SqlServerFact]`。
- **C 決策(互動確認)**:GL「6000–8000 筆」= 傳票張數(取 7,000 固定值利於 oracle);單一大資料全測試套件共用;效能緩解採「demo 檔案寫出 process 靜態 `Lazy` 記憶化」(整行程只寫一次、跨 test class 重用)而非另寫 SAX 寫出器——較簡單、低風險且更有效。`Create()` 亦 `Lazy` 記憶化、固定 LCG 確定性。
- **C 修掉的既有 fixture 缺陷**:舊「整數金額種子 500,000」其實打不中 `trailingZeros`(門檻 6 = 100 萬倍數,50 萬只有 5 個零)→ 改 2,000,000;`non_authorized_preparer`(R10)在 demo 從未匯入授權名單而無法觸發 → 新增 `demo.exportAuthorizedPreparerFile`(契約先行,鏡射科目配對匯出)並接入測試管線 `DemoProjectPipeline.SetupAsync`(預設完整匯入)與前端「套用測試案件」mock;`blank_description`、`low_frequency_preparer` 補確定性種子。
- **blast-radius 對齊原意非湊綠**:fixture 放大使約 10 個硬編 2000/100/舊種子數的測試變動,一律改引用 `DemoDataFactory` 具名常數;測 na/閘控者用 `SetupAsync` 旗標關閉新匯入維持原意(命中數 oracle 由 `DemoRuleOracleTests` 專責),reviewer 逐一確認無漏覆蓋。
- **發現既有 production 議題(非本輪範圍、留後續評估)**:V1 part(a) 控制總數在完整管線(最後 commit TB)後恆 `na`——`gl_control_total` 只由 GL 投影寫入,而 TB 投影的 `RuleRunResultReset.ClearWithinAsync` 會清掉它且不重建(源於 B 子專案把它納入失效集時未區分 GL/TB 相關變更)。影響匯出底稿 step1 part(a) 控制總數在 TB 最後提交時空白。Task 7 oracle 把它照出來,未湊綠(partA 真值由既有 `commitTb:false` 測試覆蓋)。是否修(TB commit 後重建 / validate 即時重算)牽涉投影失效模型,屬獨立決策。
- **驗證**:build 0、全套件本機 700→**715 綠**(0 failed / 0 skipped,設 `JET_SQLSERVER_CONNECTION` 時 SQL Server parity 實跑、1m44s);13 條規則 seed 命中數 oracle 精確成立、獨立重算三方一致;final whole-branch review(Opus)以獨立腳本重算確認 oracle 不變量有 ~100× 餘裕,結論 READY-TO-MERGE、無 Critical/Important。**demo 全去機敏**(公司名固定虛構常數「範例製造股份有限公司」+ 防護測試,人名/摘要皆虛構)。待使用者 GUI 人工驗收。

## 2026-06-21 — 子專案 E1 匯出底稿 writer(OpenXML SAX 串流寫 15 條件工作表 + 三類新查詢 + GlCanonicalNames + export.workpaperStream/host.selectSavePath)

E1 是「匯出底稿」里程碑的最後一棒:把 `export.*` 從委派前端的 stub 換成真正的後端串流寫檔,用 OpenXML SAX 寫出事務所「JE Testing Tool」樣本版面的 `.xlsx` 工作底稿(`{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`)。前面 A/B/C/C補遺/D1/D2/F 已把每張表所需的查詢與 store 齊備,E1 把它們串流寫成檔。設計快照見 `docs/specs/2026-06-21-workpaper-export-writer-design.md`。E1 不做欄位配對匯入 round-trip(延後 E2,使用者裁定)、不重算任何規則語意(writer 純讀)。

- **E 前自我審查:cell 級對碼兩份 WorkingPaper 樣本**:實機重解 `福懋`(主)與 `佰鴻`(交叉)兩份樣本全 15 表,逐 cell 對碼確認每表的 JET 資料來源(D1 分頁 / D2 矩陣 / F·B 的 store)皆對得上現有查詢。發現兩個小缺口需 E1 補:step1-2「分錄編製人員說明」要全名單,但現行 `creator_summary` 截 LIMIT 50 → E1 加一條不截斷查詢;step2「分錄來源」欄 `infSamplePage` 未含 `source_module` → 該欄留空(方法學允許「若有」),不擴 D1 查詢(YAGNI)。
- **writer 是 deep module,sheet 是資料不是分支(本棒最吃重的工程品味準則)**:淺薄設計會變成「15 個各寫一遍 OpenXML 管線的函式 + 一個依 sheet 名分岔的 god switch」。改採 Ousterhout deep module——`WorkpaperWriter` 對外只暴露窄介面 `WriteAsync(Stream, context) → ExportStats`,內部藏全部 OpenXML SAX 細節(shared strings inline、styles、merged cells、number formats、串流)。資料表沿用單一 `EmitTableSheet` 原語(表頭區塊 + 欄標 + 一個「列來源」委派,委派內部 keyset 逐頁取 repo、yield cell 陣列),step1/step1-2/step2/step3/step4/step4-1/三參考表都走它;封面/固定文字/手填骨架是各自的小 emitter。
- **條件表 = orchestration guard,不是 god-function 特例分支**:step1-3-1(完整性差異調節)只在 diff≠0 時才出。把它做成 orchestrator 的 `if (hasNonZeroDiff) emit(...)`——sheet 清單由 orchestrator 依資料動態決定要 emit 哪些,emitter 本身無「我是第幾張表」的分支。這正是「換 data structure 讓特例消失」。
- **串流不全載入(鐵律,guide §1.5.5)**:writer 走 `DocumentFormat.OpenXml` `OpenXmlWriter`,大型明細列用 inline string、逐頁逐列寫,不把整份 result set 載入 `DataTable`/`List<>`/DOM workbook。百萬列傳票/明細(百創 ~140 萬)逐頁流過、記憶體有界。**ClosedXML 僅限 dev fixture `DemoWorkbookWriter`,不得作底稿寫出器**。
- **writer 是查詢消費者,不重做業務邏輯**:注入既有 ProviderRouting 查詢 repo(D1 `completenessDiffPage`/`docBalancePage`/`infSamplePage`、D2 `tagMatrix*`)+ store(行事曆/科目配對/欄位配對 + 新全編製人員查詢),對 repo keyset 逐頁取、逐列寫;scaled→顯示值換算(`(decimal)scaled / moneyScale`)在 writer 內,屬純算術,不在 writer 內重算規則。
- **step1 全科目 gap-fill 與 step1-2 全名單**:step1 完整性走全科目逐頁(`completenessDiffPage`,含 diff=0 科目以完整呈現完整性);step1-2 新增**不截斷的全編製人員彙總查詢**(去 LIMIT;distinct `created_by` 基數有界——人員 + 自動拋轉傳票類型,實務數十~數百——故回完整清單即可,**不需分頁**,於程式註解寫明「為何全名單不分頁」)。同期補 `ICompletenessAccountPageRepository`(全科目完整性頁)、AccountMappingExport、CalendarExport 查詢,皆雙 provider + `[SqlServerFact]` parity。
- **step2 借貸兩欄(使用者裁定)**:`infSamplePage` 的 `InfSampleRow` 已回 `DebitScaled`/`CreditScaled`,step2 直接寫兩欄(E=借方、F=貸方),不寫借貸代號+單欄——對齊 D1 既有資料形狀、語意不含糊(於註解寫明「為何 step2 兩欄」)。
- **step4-1 動態 C 欄集**:只 emit「該客戶有行層命中」的 `C{position}_TAG` 欄(由 `tagMatrixScenarios` 的 rowHitCount>0 的 position 決定欄集),以 matchedPositions 標 Y,對齊樣本;固定模板殘留欄(樣本 Q–Z)不複製。
- **多情境矩陣惰性 materialize 由 handler 負責**:step3/4/4-1 的「全部已存情境須先落地命中」惰性補算由 handler(共用 `FilterRunMaterializeService`,D2 已提取)觸發,writer 只讀已備妥的查詢結果——不在 writer 內觸發 materialize,維持 writer 純讀。
- **正準中文名單一事實來源(Domain)**:新增 `GlCanonicalNames`(logical key ↔ 正準中文名,`docNum`↔`傳票號碼_JE`、TB 側 `會計科目編號_TB` 等),Field Mapping Info(表13)與日後 E2 round-trip 共用同一資料結構(消除雙向重複對照);與既有 `GlFieldWhitelist`(logical key → 實體欄)分立但相鄰。
- **CompanyName = EntityName**:封面公司名稱取專案 metadata 的 EntityName,不另立欄位。封面 CAATs 段只寫**檔名字串** `{客戶}_CAATS_JE_WP_{yyyymmdd}.docx`(外部、查核員自備),E1 不產生 docx。手填欄(step1-2 部門/職稱/說明、step1-3 原因/調節/調節後、step1-3-1 前期損益、step2 結果 A–G、step4 P–U、step5 內文)寫空白骨架供查核員填。
- **契約先行 + host I/O 分層**:`export.workpaperStream`(stub→Implemented,`{ sheets?, outputPath }` → `{ ok, bytesWritten, sheetStats }`,streaming)、新 `host.selectSavePath`(WinForms `SaveFileDialog`,在 Form1/Bridge 走 host I/O)、facade 皆先改 `docs/action-contract-manifest.md`,`ActionContractTests` 鎖回應形狀。前端「匯出底稿」步驟接線(選表 + 觸發 + 完成回饋,零商業邏輯)。
- **自我審查 cell 級對碼 + 讀回斷言**:寫出器驗證不靠目視——Application 驗收以 reader 讀回暫存 `.xlsx` 斷言(表存在/表名、封面 metadata、step1 欄標 + 科目列值 == 獨立 recount、step1-2 全名單 == distinct created_by recount、step2 借貸兩欄、step3 C 數與 `tagMatrixScenarios` 一致、step4 矩陣 Y 與 matchedPositions 一致、step4-1 動態 C 欄集、條件表 diff≠0 存在/無差異不存在(兩 demo 變體)、手填欄空、Not-in-TB 字面值、`sheetStats` 列數)。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **700/700** 綠(0 failed / 0 skipped;本機 LocalDB 在線、設 `JET_SQLSERVER_CONNECTION` 時 SQL Server parity 實跑;新增 Domain `GlCanonicalNames`/匯出契約型別單元、Application 驗收(上列讀回斷言)、Infrastructure/parity 全編製人員與全科目完整性查詢雙 provider 等價)。本輪屬後端串流寫出器 + 三類新查詢 + 前端零邏輯接線,自動化全綠;「匯出底稿」步驟選表/觸發/存檔對話框(預設檔名)/寫出/完成回饋、Excel 逐表目視對齊樣本(封面、step1 全科目、step1-2 全名單、step2 借貸兩欄、step3/4/4-1 矩陣、step5 橫幅、三參考表、Not-in-TB 字面值)、diff≠0 案件 step1-3-1 出現/無差異案件不出現、大母體(百創 ~140 萬)匯出不爆記憶體可完成等純 GUI 與端到端互動,依測試規範以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**程式已完成並經各 task 複審、自動化測試全綠,但 GUI 目視驗收與提交都還沒做。** round-trip 匯入(讀回 Field Mapping Info 重建配對)延後 E2。

## 2026-06-21 — 子專案 D2 多情境逐列 tag 矩陣(三 query action + idx_result_filter_run_entry + 共用 materialize 服務 + 前端矩陣預覽)

D2 是「匯出底稿」里程碑的查詢基礎設施一棒:把已落地的篩選命中(`result_filter_run`,D1)即時組成事務所方法學 step4(傳票層 C1..CN 命中布林矩陣)與 step4-1(行層逐行 tag)所需的矩陣資料,供後續 E(writer)寫進 .xlsx。現況的 `filterHitsPage`(D1)一次只回單一情境的命中行,沒有「跑全部情境、逐傳票/逐行標記命中了哪些情境位置」的矩陣;D2 補上。設計快照見 `docs/specs/2026-06-21-tag-matrix-design.md`。D2 只提供查詢,不做匯出 writer(E)、不另存 pivot 矩陣表、不改命中語意。

- **D2 前自我審查:行層結構同時導出兩矩陣**:對碼確認 `result_filter_run` 的 schema = `(scenario_position, entry_id)`,命中存在 GL 行層(`filter.commit` 對每個已存情境 `INSERT ... SELECT entry_id WHERE {述詞}`);`GlRulePredicates` 全部述詞以單行為單位評估,跨行條件用傳票範圍 EXISTS。因此這一個行層結構**同時**導得出 step4 傳票層(某傳票命中情境 S ⟺ 該傳票存在任一行於 `result_filter_run` 標記為 S)與 step4-1 行層(某行命中 S ⟺ `(S, 該行 entry_id)` 存在)。結論:D2 不需要新的命中儲存或新矩陣表,只需即時 pivot。
- **即時算,不落地新矩陣表(Good Taste)**:命中已落地於 `result_filter_run`;矩陣(pivot)是便宜的 `JOIN`/`GROUP BY`/keyset 查詢。另存 pivot 後的矩陣表會重複資料、引入新的失效來源、且可能與 `result_filter_run` 不一致。故 D2 即時從 `result_filter_run` 算矩陣,沿用 D1 的 Page 原語做 keyset 分頁——永遠與命中一致、零新失效不變量。三 query action:`query.tagMatrixScenarios`(矩陣表頭/step3 交叉參考,回全部已存情境 `position`/`name` + 傳票層命中數 `COUNT(DISTINCT document_number)` + 行層命中數 `COUNT(*)`,依 position 升冪,0 命中亦列)、`query.tagMatrixVoucherPage`(step4 傳票層,keyset 去重命中傳票,每列 `documentNumber`/`postDate`/`createdBy`/`voucherTotal` + `matchedPositions`,鍵 `document_number` ASC)、`query.tagMatrixRowPage`(step4-1 行層,keyset 命中傳票之**所有行**含非命中行,每列核心欄 + 逐行 `matchedPositions`,鍵 `entry_id` ASC)。`voucherTotal` = 該傳票 `SUM(debit_amount_scaled)`(對齊樣本 step4「傳票總金額」毛額正數,scaled→decimal 由 handler 換算)。
- **每頁兩段查詢,避方言聚合**:「每傳票/每行命中了哪些情境位置」是一對多。要在單一 SQL 內把位置聚成一欄需 `group_concat`(SQLite)/`STRING_AGG`(SQL Server)等**方言相異**聚合。為維持 provider 中立、鍵集分頁乾淨,採每頁兩段查詢:(1) 實體頁查詢(keyset)取本頁去重傳票/命中傳票所有行 + 核心顯示欄,回本頁鍵範圍;(2) 位置查詢對**同一鍵範圍**取 `(實體鍵, scenario_position)`,在 handler(C#)分組成每實體的命中位置有序去重清單。兩段皆參數綁定、純 ANSI(除 LimitClause 走方言)、每頁有界(≤ pageSize 實體 × ≤10 位置)。位置→C1..CN 欄的對映屬 E。
- **輔助索引(加法,不升版)**:`result_filter_run` PK = `(scenario_position, entry_id)`,`entry_id` 非前導 → 「以 entry_id join 回 `target_gl_entry` 算傳票」「行頁位置查詢 `WHERE entry_id` 範圍」未獲最佳索引。新增 `idx_result_filter_run_entry ON result_filter_run(entry_id, scenario_position)`(雙 provider,`IF NOT EXISTS`/`IF ... IS NULL`,**不升 schema 版本**,同 `result_filter_run`/`gl_control_total` 加法建表慣例)。
- **惰性 materialize 提取為共用服務(DRY)**:矩陣須反映全部已存情境,沿用 `filterHitsPage` 的惰性補算——首次查詢若空(或 summary 全 0)且 `config_filter_scenario` 有定義,重用 `IFilterRunMaterializer` 對全部情境落地後重取一次。為免重複,把 `filterHitsPage` 既有的私有 `MaterializeAllAsync` 提取成共用 Application 服務 `FilterRunMaterializeService`,由 `filterHitsPage` + D2 三 handler 共用(單一事實來源,語意不變)。
- **矩陣餵 E**:D2 只回矩陣資料;E(最後一棒)把矩陣寫進 .xlsx step4/step4-1(C*_TAG 動態欄位集、傳票總額格式、手填欄留空、step3 定義表)。D2 不做動態欄位決策,回完整位置清單。
- **契約先行 + 雙 provider 等價**:三新 query action + facade 先改 `docs/action-contract-manifest.md`,`ActionContractTests` 鎖回應形狀。兩段查詢(keyset、方言 LimitClause)在 SQLite 與 SQL Server 兩側同步;每個 Page/summary 比照 D1 加 `[SqlServerFact]` parity(雙 provider 走訪等價/count 等價),由 LocalDB 閘控測試實跑。`result_filter_run` 純讀,無新失效源(隨 `RuleRunResultReset` 失效,D1 已含);新增索引隨表存在。前端「高風險條件矩陣」預覽(情境摘要 + 傳票矩陣 + 點傳票展開行層 tag,沿用 D1 載入更多基礎設施)零商業邏輯。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **646/646** 綠(0 failed / 0 skipped;本機 LocalDB 在線、SQL Server parity 實跑;新增 Domain 矩陣列型別(`VoucherTagRow`/`RowTagRow`/`ScenarioTagSummary` 欄序對齊 wire)、Application 驗收(對 manifest wire:`tagMatrixScenarios` 各情境 voucherHitCount/rowHitCount == 獨立 recount、0 命中情境列出、惰性 materialize;`tagMatrixVoucherPage` 命中傳票集 == recount、每傳票 matchedPositions == recount、voucherTotal == SUM(debit) recount、走訪全頁無重複單一升冪、壞 cursor → invalid_payload;`tagMatrixRowPage` 列集 == 命中傳票所有行(含非命中行)recount、每行 matchedPositions == recount、走訪等價、壞 cursor)、Infrastructure/parity 三查詢雙 provider 走訪等價(鍵序列 + matchedPositions 等價))。本輪屬後端查詢基礎設施 + 前端零邏輯矩陣預覽,自動化全綠;「高風險條件矩陣」情境摘要、傳票矩陣載入更多、點傳票展開行層 tag、多情境(存滿 10)欄位對位、大母體逐頁流暢、未存情境友善空狀態等純 GUI 互動,依測試規範以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**程式已完成並經各 task 複審、自動化測試全綠,但 GUI 目視驗收與提交都還沒做。**

## 2026-06-21 — 子專案 C 補遺(補齊 C9 低頻科目列述詞 R12 + 自訂科目張數條件)

D2 開工前的自我審查發現:子專案 C 主體只補齊了**編製人員維度**的高風險條件(C5 非授權編製人員、C6 低頻編製者),但事務所方法學 step3 的 **C9「較少使用之科目」(科目張數 ≤ 11)是科目維度、被漏掉**——實機重解兩份 WorkingPaper 樣本,福懋樣本 C9 命中 40 張傳票,確屬真實在用的高風險條件。JET 現況只有 `rare_accounts`(R6)——「較少使用之科目」**彙總**(top-50、`RuleShape.Aggregate`),**不可作進階篩選列述詞**;這和 C 開工前 `creator_summary` 的處境完全相同。本補遺照搬 C6 同一模式到科目維度。設計快照見 `docs/specs/2026-06-21-low-frequency-account-escalation-design.md`。

- **鏡射 C6 的「固定規則 + 自訂條件」雙軌**:低頻科目偵測是「依 `account_code` 分組 `COUNT(*) <= 門檻`」。比照 C6(`low_frequency_preparer` 固定 + `customPreparerEntryCount` 自訂),本補遺也做雙軌:固定 RowTag 規則 `low_frequency_account`(R12)取 Domain 常數 `AccountFrequency.DefaultMaxEntries=11`(方法學:全年 < 12 筆),進階篩選新增條件型別 `customAccountEntryCount`(`maxEntries ≥ 1` 由查核員輸入)。兩者共用同一述詞形狀、門檻參數綁定,單一事實來源不重複(`customAccountEntryCount` 重用 C6 已加的 `FilterRuleSpec.MaxEntries` 欄,不為對稱另開欄)。
- **述詞自足,無 C5 那種反轉風險**:述詞 `account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @maxEntries)` 純讀既有 `target_gl_entry`、不依賴外部清單(不像 C5 非授權編製人員要比對授權名單、有 `NOT IN` 空集合反轉成全命中的風險)。純 ANSI、雙 provider 相同,此規則永遠可執行、不需閘控(同 C6),無新失效來源。
- **與 `rare_accounts`(R6)並存不取代**:R6 維持 top-50 彙總視圖供判讀(不可作列述詞,本補遺不動);R12 是同維度的 RowTag 版本(可作列述詞、可入情境),鏡射 `low_frequency_preparer`(R11)與 `creator_summary`(R5)彙總並存的格局。
- **escalation 靠 D1,不另造**:R12 是可入篩選情境的 RowTag;查核員存「低頻科目」情境後即可用 D1 已落地的 `query.filterHitsPage` 取回**全部命中分錄**——本補遺不重造分頁。step3 C9 另含的「科目名稱不含〔長串例外清單〕」子條件,由既有 Text NotContains 在情境裡 AND 組合,不另做專屬 UI。
- **無新 schema**:純讀既有 `target_gl_entry`,不新增表或欄位。
- **契約先行 + 雙 provider 等價**:prescreen response 加 `lowFrequencyAccount`、filter 條件型別加 `customAccountEntryCount`、可篩選鍵清單加 `lowFrequencyAccount`,皆先改 `docs/action-contract-manifest.md`,`ActionContractTests` 鎖回應形狀。述詞、whereBuilder、prescreen 倉儲計數在 SQLite 與 SQL Server 兩側同步;parity 由 LocalDB 閘控測試實跑。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **628/628** 綠(本機 LocalDB 在線、SQL Server parity 實跑;新增 Domain 單元(`AccountFrequency.DefaultMaxEntries==11`、`RuleCatalog` 含 R12 RowTag 且 `FilterableKeys` 同步、`FilterRuleType.CustomAccountEntryCount` 解析 `MaxEntries`、validator `MaxEntries>=1` BVA)、Application 驗收(對 manifest wire:C9 固定 row-tag 命中 == 獨立 recount、`customAccountEntryCount(@n)` 命中 == recount、C9 入情境經 `filterHitsPage` 取回全部)、Infrastructure/parity 雙 provider 等價)。本輪屬後端規則 + 前端零邏輯卡片,自動化全綠;預篩選「低頻科目之分錄」的〔檢視〕+ 載入更多、進階篩選自訂科目張數條件、存低頻科目情境後分頁看全部分錄等純 GUI 互動,依測試規範以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**程式已完成並經各 task 複審、自動化測試全綠,但 GUI 目視驗收與提交都還沒做。**

## 2026-06-20 — 子專案 C 編製人員升級(授權清單匯入 + 非授權編製人員 R10 + 低頻編製者 R11 + 自訂編製人員張數條件)

子專案 C 補上事務所方法學的兩條編製人員高風險條件:C5「非授權編製人員」(分錄由不在授權名單之人編製)與 C6「低頻編製者」(全年編製筆數過少之人),並加上授權清單匯入。現況只有 `creator_summary`(top-50 彙總、不可作篩選列述詞),既無授權名單概念、也無「測某人全部分錄」「依張數篩低頻」。C 不改 `creator_summary`、不另造 escalation 基礎設施。設計快照見 `docs/specs/2026-06-20-preparer-escalation-design.md`。

- **C5 雙重閘控,為什麼是「兩道」而非一道**:非授權述詞核心是 `created_by NOT IN (SELECT name FROM target_authorized_preparer)`。`NOT IN` 對空集合(名單未匯入)會反轉成「全部命中」——把每一筆分錄都標成非授權,語意完全相反且危險。所以兩端各設一道閘:預篩選端 `PrescreenRunInput` 加 `HasAuthorizedPreparers`,名單表非空才執行,否則整條規則回 `status="na"` + `naReason`(同 `unexpectedAccountPair` 以科目配對是否匯入閘控的模式);進階篩選端 validator 對空名單回 `invalid_scenario`,且述詞本身另以 `EXISTS`(名單非空)自保,即使繞過 validator 也不會反轉成全命中。整體鏡射 `unexpectedAccountPair`「依賴匯入資料 + 閘控 + 述詞自保」的既有模式。
- **C6 雙軌鏡射子專案 A 的「固定規則 + 自訂條件」**:低頻偵測是「依 `created_by` 分組 `COUNT(*) <= 門檻`」。比照 A 的連續零尾數(固定預設 6 + 進階篩選 `customTrailingZeros` 可自訂),C6 也做雙軌:固定 RowTag 規則 `low_frequency_preparer` 取 Domain 常數 `PreparerFrequency.DefaultMaxEntries=11`(方法學:全年 < 12 筆),進階篩選新增條件型別 `customPreparerEntryCount`(`maxEntries ≥ 1` 由查核員輸入)。兩者共用同一述詞形狀、門檻參數綁定,單一事實來源不重複。
- **escalation 靠 D1,不另造**:「測非授權者的全部分錄」過去需要新的全量取得基礎設施;但 D1 已落地 `query.filterHitsPage`(已存情境的全量命中分頁)。所以 C5 只要是可入篩選情境的 RowTag,查核員存成「非授權編製人員」情境後即可用 D1 取回全部命中分錄——C 不重造分頁,只提供 row-tag。
- **授權清單不寫 import_batch,避免無謂升版與批次概念**:授權清單是查核團隊維護的單欄姓名參照資料,不是 GL/TB 母體匯入,套用「一資料集一批次」的 `import_batch` 模型沒有意義。所以匯入直接 replace-only 投影進 `target_authorized_preparer`,不寫 `import_batch`。兩新表(staging + target)隨基底 schema 以 `IF NOT EXISTS`／`IF OBJECT_ID(...) IS NULL` 建立、**不升 schema 版本**(沿用 `app_message_log`／`gl_control_total`／`result_filter_run` 先例,純加法衍生表毋須鏈式升版)。解析沿用既有 OpenXML SAX 讀取器與關鍵字欄位解析(鏡射科目配對/行事曆匯入),TRIM 正規化、空白列略過、PRIMARY KEY 去重。
- **失效不變量沿用 ClearWithinAsync**:C5 命中依賴授權名單,名單重匯入(replace)時相關規則結果必須失效。授權清單 replace 與 `RuleRunResultReset.ClearWithinAsync` 在同一交易內執行,使依賴名單的 prescreen/filter 結果重算(同 guide §2.5、同行事曆/科目配對匯入模式),不破壞 `result_rule_run` 回放。
- **resume 走 rowCount**:`project.load` 的 importState 以 store 計數(`FindStateAsync` 取 `target_authorized_preparer` rowCount)輸出 `authorizedPreparer.rowCount`,讓重開續作能還原名單匯入狀態。
- **契約先行 + 雙 provider 等價**:`import.authorizedPreparer.fromFile`、prescreen response 兩鍵(`nonAuthorizedPreparer`/`lowFrequencyPreparer`)、filter 條件型別 `customPreparerEntryCount` 皆先改 `docs/action-contract-manifest.md`,`ActionContractTests` 鎖回應形狀。schema、述詞、whereBuilder、prescreen 倉儲、store 在 SQLite 與 SQL Server 兩側同步;parity 由 LocalDB 閘控測試實跑。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **618/618** 綠(本機 LocalDB 在線、SQL Server parity 實跑;新增 Domain 單元(清單解析、`DefaultMaxEntries==11`、述詞語意、`CustomPreparerEntryCount` 解析 `MaxEntries`)、Application 驗收(對 manifest wire:匯入 replace-only/失效、兩規則命中 == 獨立 recount、C5 空名單 na、自訂門檻命中 == recount、C5 入情境經 `filterHitsPage` 取回全部)、Infrastructure/parity 雙 provider 等價)。本輪屬資料/規則後端 + 前端零邏輯卡片,自動化全綠;授權清單上傳卡、預篩選兩規則〔檢視〕+ 載入更多、進階篩選自訂編製人員張數、存非授權情境後分頁看全部分錄等純 GUI 互動,依測試規範以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**程式已完成並經各 task 複審、自動化測試全綠,但 GUI 目視驗收與提交都還沒做。**

## 2026-06-20 — 子專案 D1 全量明細基礎設施(五個 keyset 分頁 action + result_filter_run 落地 + INF 明細回取 + 前端載入更多)

D1 是「匯出底稿」里程碑的全量取得地基:現況各明細查詢一律截 ≤50 預覽、篩選命中不落地、INF 抽樣只回筆數+seed,沒有走訪全量的路徑。D1 補上五個 keyset 游標分頁查詢與其落地,給後續 D2(多情境 tag 矩陣)、E(匯出 writer)使用,並讓 GUI 既有明細檢視能「載入更多」。D1 不做 tag 矩陣(D2)、不做匯出 writer(E)。設計快照見 `docs/specs/2026-06-20-full-detail-pagination-design.md`。

- **keyset 而非 OFFSET/FETCH**:母體達 140 萬列,OFFSET 分頁要 DB 先處理被略過的列再丟棄(略過越多越慢),且並發變動會跳列/重複。改用 keyset(seek):`WHERE` 帶上一頁最後一列的排序鍵跳過,有索引時高效、對並發穩定(Microsoft Learn:EF Core Pagination、T-SQL ORDER BY OFFSET/FETCH 佐證)。排序鍵取天然唯一且有索引者:completeness=`account_code`、docBalance=`document_number`、其餘=`entry_id`(PK);唯一性是翻頁無漏無重的前提,索引是 seek 不全掃的前提。隨機跳頁碼刻意不做——底稿匯出與 GUI 審閱皆只需「下一頁」前進(YAGNI)。
- **游標述詞展開布林式,不用元組比較**:多數 DB(含 SQLite)支援 row-value 元組 `WHERE (k1,k2) > (@k1,@k2)`,但 SQL Server(T-SQL)不支援。為讓兩 provider 共用同一邏輯形狀,游標述詞一律寫成展開布林式(單鍵 `key > @cursor`;多鍵 `k1 > @c1 OR (k1=@c1 AND k2>@c2)`)。頁取數沿用方言、由 `ISqlDialect.LimitClause` 出:SQLite `LIMIT`、SQL Server `OFFSET 0 ROWS FETCH NEXT ... ROWS ONLY`(OFFSET 恆 0、僅作 TOP-N、需 ORDER BY,非 offset 分頁)。游標為 opaque 字串、Domain 純函式編解碼(可單元測試),頁大小預設 200/上限 500、Domain 夾擠。
- **`result_filter_run` 精簡參照 + 惰性補算**:`filterHitsPage` 要「某情境命中哪些 entry_id」。新表只存 `(scenario_position, entry_id)` 行層參照(PK 天然覆蓋游標),不存去正規化整列——避免重複與失同步,傳票層由 distinct `document_number` 推得。落地時機選在 `filter.commit`(保存情境)時,以既有 `GlFilterWhereBuilder` 對該情境 AST 組命中述詞寫入(先刪後插、冪等)。此機制前保存的舊情境沒有落地列,`filterHitsPage` 讀取時惰性以同述詞補算並落地後再回(一次性、robust),不需資料遷移。
- **新表不升 schema 版本**:`result_filter_run` 隨基底 schema 以 `IF NOT EXISTS`(SQLite)/`IF OBJECT_ID(...) IS NULL`(SQL Server)建立,沿用 `app_message_log`/`gl_control_total` 先例——是純加法的衍生資料表,不改既有表結構,毋須鏈式升版。
- **失效集擴充**:`result_filter_run` 是依當前母體算出的命中參照,與規則結果同性質。納入 `RuleRunResultReset.ClearWithinAsync`——重投影清 `target_gl_entry` 的同一交易內一併 `DELETE`(同 guide §2.5、同 `gl_control_total` 模式),確保命中永不指向已失效母體;不破壞 `result_rule_run` 回放。
- **infSample 限定最新 run**:`infSamplePage` 讀既有 `result_inf_sampling_test_sample` join `target_gl_entry`,限定**最新一次 validate run** 的樣本(避免跨 run 樣本混入)、`entry_id` ASC keyset,借貸由 signed `amount_scaled` 拆兩欄回 step2 攸關欄。只讀回、不重抽樣(seed 已固定落地)。
- **前端載入更多:首擊清預覽避免重複**:五個明細檢視(完整性差異、借貸不平、空值、情境命中明細、INF 抽樣)在既有 ≤50 預覽下方加「載入更多」,帶 `nextCursor` 呼叫對應 `query.*Page`、回傳列接在現有列之後、`null` 時隱藏鈕。預覽(≤50)與全量 Page 的排序/職責不同,首次點「載入更多」會清掉預覽列再從游標第一頁接起,避免預覽列與 Page 列重複。前端零商業邏輯:不判命中、不算差異、不組 SQL,只發 action、接列、管游標與載入態(經 `jet-api`)。既有 `validate.run`/`filter.preview` 的 ≤50 / ≤1000 預覽保留不動(互動審閱仍用它)。
- **契約先行 + 雙 provider 等價**:五個 `query.*Page` action 的請求/回應/row 形狀先改 `docs/action-contract-manifest.md`,`ActionContractTests` 鎖回應形狀。每個 Page 查詢 SQLite 與 SQL Server 各一套實作、經 routing 路由,逐頁等價由 LocalDB 閘控 parity 測試驗證。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **588/588** 綠(本機 LocalDB 在線、SQL Server parity 實跑,含 8 個 ProviderParityJourney passed;新增 Domain `PageCursor` 編解碼/頁大小夾擠單元、Application 各 Page 走訪到底==recount 與回應契約鎖、`result_filter_run` 命中與失效、Infrastructure 雙 provider 逐頁等價)。本輪屬後端基礎設施 + 前端載入更多,自動化全綠;**GUI 五處「載入更多」(游標增量接列、到底隱藏、載入中不卡頓、大母體不整表卡頓)的純前端互動依測試規範(禁止 WinForms/WebView2 E2E 自動化)以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。程式已完成並經各 task 複審、自動化測試全綠,但 GUI 目視驗證與提交都還沒做。**

## 2026-06-20 — 子專案 B 資料/schema 擴充落地(傳票日期欄 + 回溯過帳規則 + 完整性 part(a) + Not-in-TB)

子專案 B 是「匯出底稿」里程碑的資料地基:盤查事務所匯出底稿樣板與手工底稿方法學後,確認三項「底稿需要、現況缺乏」的資料與衍生計算,先把地基補上(不碰匯出 writer、不碰全量分頁,那些屬 D/E)。設計快照見 `docs/specs/2026-06-20-workpaper-data-schema-extension-design.md`。

- **傳票日缺口精確化,而非泛加日期欄**:現行 GL 只有過帳日 `post_date` 與核准日 `approval_date`(= docDate),沒有傳票(憑證)日。底稿高風險條件 C7「過帳日早於傳票日」要的是這兩個不同日期的比較,真實 GL 源也確實同時有「過帳日」與「傳票日」兩個原始日期欄。所以新增的是一個語意明確的選填欄 `voucher_date`,而不是把核准日重新詮釋成傳票日——`docDate`(核准日)語意維持不變、不重掛。欄位選填、對 NULL 安全:來源未配對傳票日時 `voucher_date` 為 NULL,所有下游邏輯(規則命中、日期區間)都不會誤命中。
- **回溯做成單一專用預篩選規則,不做通用「欄位 vs 欄位」AST**:回溯偵測本質是「過帳日 < 傳票日」的兩欄比較。本可以引入一個通用的「欄位對欄位」進階篩選條件型別來表達,但那是更大的設計面、且本輪用不到第二個案例。選擇把它做成一條 RowTag 預篩選規則 `backdated_posting`(R9),與週末/假日過帳同模式:集合式 SQL 命中 `voucher_date IS NOT NULL AND post_date < voucher_date`、落地 `result_rule_run`、可被進階篩選情境當 row-tag 引用。需要時再回頭評估通用欄對欄條件,屬規則新增(先改 §5 與 manifest)。
- **完整性 part(a) 控制總數設計:落地點選在投影**:方法學的完整性測試是兩段——part(a) 確認匯入母體的列數與金額合計在系統處理後未增減(控制總數對帳),part(b) 才是逐科目 TB 變動 vs GL 差異(現況只有 part(b))。要做 part(a) 得先有基準,而匯入管線原本沒有持久化「匯入列數/金額」。落地點選在投影(`mapping.commit`)逐列讀取時一次累計兩端控制總數(來源端 staging 列數 + 原始金額合計、母體端 `target_gl_entry` 列數 + 借貸總額 scaled),結束時寫入新表 `gl_control_total`。選投影而非匯入,是因為投影是把來源轉成權威母體的那一步,兩端在同一次串流裡讀得到,核對「投影過程未增減」最直接。金額一律 scaled BIGINT 整數比較,避免浮點。
- **Not-in-TB 是字面值記號,不是獨立旗標欄**:科目配對底稿把「GL 有、TB 無」的科目 `GL_NAME` 寫字面值 `Not in TB`(樣本實證,不是一個布林欄)。現行完整性 diff 的 UNION 第二支其實已撈出這些科目(`gl_s` 非零、`tb_s = 0`),只是沒具名。本輪只為這些差異列加一個 `notInTb` 布林記號供查核員辨識與底稿呈現;字面 `Not in TB` 的輸出屬匯出 writer(E)。完整列舉全部 Not-in-TB 科目依賴分頁能力(D1),本輪只做逐列判斷與記號,於設計與交付物註明跨子專案相依。
- **`gl_control_total` 納入結果失效不變量**:控制總數是依當前母體算出的衍生資料,與規則結果同性質。所以把它加進 `RuleRunResultReset.ClearWithinAsync` 的失效集——清 target 的同一交易內一併清此表,維持「資料換了、舊衍生值不會殘留」的不變量(guide §2.5),避免「母體已重投影、控制總數還是舊的」中間態。
- **契約先行 + 雙 provider 等價**:新增規則 wire key、選填配對欄、完整性結果形狀(part(a) + notInTb)皆先改 `docs/action-contract-manifest.md`。schema、prescreen、completeness 三處 SQLite 與 SQL Server 同步改;v4→v5 遷移冪等、ALTER 前守欄(沿用 v3→v4 `day_name` 模式),既有 `result_rule_run` 回放不變。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **559/559** 綠(本機 LocalDB 在線,SQL Server parity 實跑;新增 Domain 單元、Application 驗收(對 manifest wire)、Infrastructure/parity 與 v4→v5 遷移冪等測試)。本輪屬資料/規則後端,自動化全綠;欄位配對出現選填傳票日期、預篩選多回溯過帳、完整性顯示 part(a) 與 Not-in-TB 標示等純 GUI 互動,依測試規範以 `docs/windows-handoff.md` 任務卡交 Windows 端人工目視。**程式已完成並經複審、自動化測試全綠,但 GUI 目視驗證與提交都還沒做。**

## 2026-06-20 — 子專案 A 規則校正落地(高風險情境上限 5→10 + 方法學對齊)

子專案 A 是一輪規則校正:把幾處與事務所方法學對不上的預設與上限調回正確值,並把零散的文件口徑統一。這輪以三個 task 分頭實作,最後一個(Task 3)是高風險情境上限,連同整個子專案的文件回寫在此記。

- **高風險情境上限 5→10(Task 3)**:`filter.commit` 自訂的篩選情境上限 `MaxScenarios` 原為 5,但方法學本身無此限;事務所底稿的高風險條件需 C1–C10 共 10 條(.xlsm「高風險分錄的理由」確認 10 個理由)。把 `FilterHandlers.cs` 的 `MaxScenarios` 改為 10,超限錯誤訊息 `最多保存 {MaxScenarios} 個篩選情境。` 隨常數自動成為 10。上限常數留在 Application 層(provider 中立、不在前端/Form1),`scenario_limit_reached` 錯誤碼與 `filter.commit` 形狀不變。
- **子專案 A 其餘校正**:連續零尾數改為固定預設 6 位、取代原本的動態門檻;off-by-one 的文件口徑統一;可疑關鍵字清單補上「帳外」。授權閘語意確認為「自訂尾數位數 AND 數值區間」的進階篩選情境組合(無需新條件型別),並在 guide §5 文件化。
- **契約先行**:本 task 不改 action 形狀。`docs/action-contract-manifest.md` 四處上限文案(`filter.commit` 列的「上限 5 個」、`invalid_scenario`/`scenario_limit_reached` 說明的「超過 5 個」、錯誤碼表、Step 4 的「已儲存情境清單 ≤5」)同步改為 10。
- **測試(TDD)**:`FilterHandlersTests.cs` 把既有上限測試由「6 個觸發」改名為 `FilterCommit_ElevenScenarios_ThrowsScenarioLimitReached`(11 個觸發,cap=10),並新增 `FilterCommit_TenScenarios_Succeeds` 驗證 10 個情境成功保存(`ok=true`、`savedCount=10`)。先見紅(10 個被舊 cap 5 擋回)再改常數轉綠,邊界即 Application 層 `HandlerTestHost` 驗收、雙 provider 中立。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **551/551** 綠(本機 LocalDB 在線,SQL Server 測試實跑)。上限是 Application 層常數、provider 中立,無前端互動變更,本輪無 GUI 目視待辦。

## 2026-06-19 — 行事曆檔案匯入(假日／補班）+ 科目配對英文標頭

行事曆的假日與補班日清單原本只能由 demo 管線餵入,沒有正式的檔案上傳入口(現況文件「規劃中」一直掛著這條)。這輪把事務所行事曆檔的匯入端到端補齊:後端新增以檔案為來源的兩個 action,前端在「匯入資料」步驟加一張行事曆上傳卡。同輪把科目配對檔的英文標頭驗證一併落地。

- **做了什麼**:後端新增 `import.holiday.fromFile` / `import.makeupDay.fromFile`(payload `{ filePath, fileName? }`、response `{ count }`),沿用既有 xlsx 串流讀取解析事務所行事曆 schema v4——假日檔讀 `Date_of_Holiday`／`Holiday_Name`／`IS_Holiday`(只收 IS_Holiday=Y、跨年度全收),補班檔讀 `Date_of_MakeUpday`／`MakeUpDay_Desc`,標頭在第 2 列。前端在「匯入資料」步驟的「日期維度」卡加兩顆按鈕(上傳假日檔／上傳補班檔),比照科目配對卡的 `hostSelectFile → import*FromFile` 模式;選檔交後端解析,卡片顯示「假日 N 天、補班 M 天」。
- **前端零商業邏輯**:行事曆卡只負責選檔(`host.selectFile`,限 `.xlsx`)→ 呼叫對應 action → 把回傳 `count` 寫進 `Store.setCalendarState` + 一則完成訊息。解析、欄位驗證、跨年度收斂、IS_Holiday 過濾全在後端;前端不碰檔案內容。匯入假日只更新 `holidayCount`、保留既有 `makeupDayCount`(反之亦然)——用 `Store.getState().importState.calendar` 合併寫回,避免任一側上傳把另一側歸零。原本只顯示一行摘要的日期維度區塊改成 `calendarCard`,被取代的 `calendarLine` 變數一併移除。
- **科目配對英文標頭驗證落地**:科目配對檔的標頭驗證改吃英文欄名,對齊事務所實際提供的檔案格式;缺欄仍由後端回報,前端不變。
- **契約先行**:`import.holiday.fromFile` / `import.makeupDay.fromFile` 已在 `docs/action-contract-manifest.md` 與 `jet-api.js` 的 `SUPPORTED_ACTIONS` 登錄,前端才呼叫對應的 `JetApi.importHolidayFromFile` / `importMakeupDayFromFile`。`host.selectFile`、`Store.setCalendarState` 全部沿用。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **545/545** 綠(本機 LocalDB 在線,SQL Server 測試實跑;後端兩個新 action 有自動化覆蓋,前端上傳卡屬 GUI-only、依測試規範不寫前端 E2E 自動化)。前端行事曆上傳卡屬純 `wwwroot/` 互動,實機目視(假日/補班上傳顯示天數、互不歸零、上傳後相關預篩選失效回「未執行」、非 .xlsx 與缺欄錯誤路徑)以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**前端上傳卡已完成、build 綠、後端測試全綠,但 GUI 目視驗證與提交都還沒做。**

## 2026-06-19 — 重新匯入完成的卡片回饋

匯入資料步驟有個一直存在的小落差:對已匯入的資料集按〔重新匯入〕、選檔、〔開始匯入〕、跑完之後,畫面看起來像沒動過。原因是完成訊號其實落在預設收合的右側「狀態與訊息」窄欄,使用者不一定會展開去看;而摘要卡彈回後顯示的是列數/欄數/來源/時間,如果重匯的是同一個檔(或列數一樣的檔),這些數字幾乎不變,卡片看起來毫無變化。這輪讓確認訊號回到使用者正在看的卡片上。設計快照見 `docs/specs/2026-06-19-reimport-completion-feedback-design.md`。

- **做了什麼**:重新匯入完成、卡片收回摘要面時,卡片的狀態列短暫轉成成功態——綠勾加「剛剛重新匯入·HH:MM:SS」,約四到五秒後淡回常態的「已匯入 N 列、M 欄」。加入來源完成套用同一個成功態,文案改成「已加入來源·HH:MM:SS」。即使重匯同一個檔、列數沒變,使用者也看得出剛才真的重跑了。純前端,集中在 `import-step.js` 與 `app.css`。
- **訊號放卡片上,不放收合的訊息面板**:好的使用者體驗除了品味,還要降低閱讀成本。確認「剛完成」這件事該瞬間可懂,而不是要使用者去展開一個面板、讀一段訊息。所以這輪把主要確認管道改到卡片上的成功態。原本寫進訊息日誌的完成訊息維持不動(供日後追溯),但不再擴張它的文字量。
- **一次性旗標加一個計時器**:匯入成功後標記一個模組級的暫態旗標,記住「這個資料集剛完成、模式是 replace 還是 append」;摘要卡渲染時若旗標命中,狀態列就改用成功態文案與樣式。重建後啟一個一次性計時器(約四到五秒)清掉旗標並重建,讓狀態列淡回常態;旗標清掉後不再顯示成功態,避免每次重繪都閃。淡入淡出用 CSS transition,不引入重動效(對齊 minimalist-ui)。這個計時器只負責把短暫成功態收回常態,不承載任何業務語意,清不清都不影響資料正確性。
- **零契約變更**:不新增、不修改任何 action,`import.gl/tb.fromFile`、`host.selectFiles` 全部沿用,匯入卡片的固定綁定也維持。純前端回饋,manifest 不需先行修改。

驗證:`dotnet build` 0 警告 0 錯誤;`dotnet test` 全套 **525/525** 綠(本輪是純前端回饋,wire shape 不變,既有匯入相關測試即回歸保護,未新增自動化測試)。本輪屬純前端 `wwwroot/` 互動,GUI 互動依測試規範禁止 WinForms/WebView2 E2E 自動化——重新匯入同一檔時卡片狀態列出現「剛剛重新匯入·時間」並數秒後淡回、加入來源出現「已加入來源」、訊息面板文字量未增加、離開步驟再回來不殘留成功態,以 `docs/windows-handoff.md` 任務卡交 Windows 端人工目視。**程式已完成並經 code review、自動化測試綠,但 GUI 目視驗證與提交都還沒做。**

## 2026-06-19 — 欄位配對雙介面(經典三欄表／二維表)+ 必填鐵軌

前一輪把欄位配對的草稿介面從三欄表改成內嵌標頭下拉的二維表(見下一條)。上線後收到兩點回饋:有人還是偏好原本的三欄表,二維表的必填提示也不夠醒目、版面想更大。這輪兩件事一起處理:把兩種介面都留下讓使用者自己挑,並把二維表的必填提示換成右側一條會亮綠燈的鐵軌、版面加寬。設計快照見 `docs/specs/2026-06-19-mapping-ui-dual-mode-and-modern-grid-design.md`。

- **做了什麼**:欄位配對步驟頂部加一個介面切換鈕——經典三欄表與二維配對表二選一,GL 與 TB 都適用。經典三欄表是前一版的編輯器(每個 JET 欄位一列、右側挑來源欄),這輪原樣請回。二維配對表沿用現行的內嵌標頭下拉,但編輯區改成兩欄加寬版面:左邊二維表、右邊一條必填欄位鐵軌。鐵軌列出當前模式的必填欄位,每個未指派時是灰色「待指派」、指派後翻淡綠「✓」並標出對到的來源欄名;side/flag 的借方代碼字面值填了也算綠;全部綠了才啟用〔確認配對〕。已提交摘要卡、五態狀態模型、步驟閘門都不動。
- **兩種介面都留,預設經典、本次 session 記住**:與其二選一,不如兩個都留,讓使用者在配對前自己挑。預設是經典三欄表;使用者選了哪一種,本次 app session 記住——存成一個跨專案、不被 workflow reset 清除的 UI 偏好(`mappingUiMode`),換專案保留,重開 app 回到預設經典。
- **兩種介面共用同一份草稿(切換無損)**:兩種介面是同一份配對草稿的不同視圖,讀寫同一份 `draft`(欄位 → 來源欄)。所以在一種介面改到一半切到另一種,已選的對應與模式都還在。提交、`draftMatchesCommitted`、resume 全部只看 draft 與模式,不受介面選擇影響。兩個渲染器(`classicEditSection` / `gridEditSection`)都放在 `mapping-step.js`,共用模式選擇器、字面值輸入、事件綁定與狀態 helper;已提交收合摘要卡與狀態判定 dispatch 不分介面、完全沿用。
- **必填鐵軌取代原本那行文字提示**:二維表原本只有一行 `map-coverage` 文字講還缺哪些必填,改成右側一條清單,每個必填欄位即時反映待指派/已指派。判定直接用既有的 `Ui.isRequired(field, mode)`,不另立第二份清單。鐵軌只列必填、不列非必填(避免雜訊),非必填仍可在二維表標頭自由指派。後端仍是權威(缺欄回 `mapping_column_not_found`、投影失敗回 `projection_failed`)。
- **加寬與現代化都在 minimalist-ui 範圍內**:欄位配對步驟改用加寬版面(新增 `.panel--mapping`,約 1200px;經典模式也沿用此寬度,版面一致)。二維表編輯區用 CSS grid 兩欄(表格 `1fr` + 固定寬鐵軌)。狀態用淡色 chip、不引入漸層與重陰影;前一輪當死碼移除的經典三欄表 `.mapping-table*` 樣式這輪請回。
- **契約零變更**:不論用哪一種介面,前端的正規狀態都是「欄位 → 來源欄」的 `draft` map,提交 `mapping.commit.gl/tb` 的 payload 與行為一字未變。沒有新增、沒有修改任何 action,wire shape 完全相同——既有的 commit 驗收測試即為回歸保護。

驗證:`dotnet build` 乾淨 0 警告 0 錯誤;`dotnet test` 全套 **525/525** 綠(本輪是純前端互動重構,wire shape 不變,既有測試即回歸保護,未新增自動化測試)。本輪屬純前端 `wwwroot/` 互動,GUI 互動依測試規範禁止 WinForms/WebView2 E2E 自動化,雙模式切換無損、必填鐵軌即時亮綠、加寬版面與寬批次橫向捲動、五態切換在兩種介面都正確,以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**程式已完成並經 code review、自動化測試綠,但 GUI 目視驗證與提交都還沒做。**

## 2026-06-19 — 欄位配對改為內嵌標頭下拉的二維配對表

欄位配對的草稿介面原本把兩件事拆在兩個區塊:上方是配對編輯表(每個 JET 邏輯欄位一列、右邊一個下拉去挑來源欄),要看資料長什麼樣得另外開預覽面板。審計員實際操作得在兩處之間來回——看下方推斷某欄是什麼,再回上方找對應的下拉。這輪把配對動作搬到資料本身上面:像試算表一樣,每個來源欄的標頭正上方放一個下拉,標頭下方直接鋪該欄的前 10 列實際資料,過目幾筆就能當場在標頭選定對應的 JET 欄位。設計快照見 `docs/specs/2026-06-19-inline-header-field-mapping-design.md`。

- **做了什麼**:把草稿態的編輯區從三欄表(JET 欄位 → 來源欄下拉)改寫成二維配對表——來源欄為直行、標頭帶下拉指派對應的 JET 邏輯欄位、標頭下方顯示該批次前 10 列原貌,GL 與 TB 皆適用。下拉的可選欄位隨模式即時重算;指派維持一對一(一個欄位至多對一個來源欄,反之亦然);列出當前模式下尚未被指派的必填欄位,沒補齊就停用〔確認配對〕。草稿態原本的〔預覽來源資料〕按鈕移除(來源已內嵌)。已提交摘要卡、五態狀態模型、步驟閘門都不動。
- **模式留獨立選擇器,標頭只做欄位指派**:GL 的「借方欄＋貸方欄」「淨額＋借貸旗標」沒辦法用單一欄的一個下拉表達,所以模式維持為表格上方的獨立選擇器(radio),不塞進任何欄的標頭;標頭下拉只回答「這個來源欄＝哪個 JET 欄位」。可指派集合與必填判定直接取自既有的 `Ui.GL_FIELDS` / `Ui.TB_FIELDS` / `Ui.isRequired`,不另立第二份清單。
- **資料列用 `query.dataPreview` 而非 `previewFile`**:標頭下方的原貌列呼叫既有 `query.dataPreview`,`dataset` 取 `glStaging` / `tbStaging`(匯入後合併批次的原貌,欄名與配對下拉一字不差),`limit` 用 10。不採 `import.previewFile`,因為它只看得到單一來源檔,多來源合併批次會漏掉其他來源——而 staging 反映的是整批合併後的原貌。資料列惰性載入一次、存 UI 暫態;切模式不重抓(資料不變,只有標頭下拉選項變)。
- **契約零變更**:視覺上反轉成「來源欄 → 欄位」,但前端的正規狀態仍是「欄位 → 來源欄」的 `draft` map,只在渲染時即時反推給標頭下拉。提交 `mapping.commit.gl/tb` 要的 payload `{ mapping: { 欄位key: 來源欄名 } }` 與行為一字未變,`mapping.autoSuggest` 的建議併入 `draft` 後標頭下拉自然反映。沒有新增、沒有修改任何 action,wire shape 完全相同——既有的 commit 驗收測試即為回歸保護。
- **借方代碼字面值的防護修正**:借方代碼(`dcDebitCode`)是字面值(如 `"D"`、`"1"`)不是來源欄。指派單一性的清理規則原本會掃掉「值等於剛指派來源欄」的其他欄位項;若某來源欄的名稱剛好等於使用者已輸入的借方代碼值,這個清理會誤連字面值欄一起清掉。修正後清理只作用在真正的「欄位 → 來源欄」指派,不會動到字面值欄,確保指派一個碰巧同名的來源欄不會抹掉已填的借方代碼。

驗證:`dotnet build` 乾淨無誤;`dotnet test` 全套 **525/525** 綠(本輪是純前端互動重構,wire shape 不變,既有測試即回歸保護,未新增自動化測試)。本輪屬純前端 `wwwroot/` 互動,GUI 互動依測試規範禁止 WinForms/WebView2 E2E 自動化,標頭下拉重算、內嵌資料列、模式切換、必填覆蓋停用、五態切換、寬批次橫向捲動,以及借方代碼字面值防護的目視確認,以 `docs/windows-handoff.md` 任務卡交 Windows 端人工。**程式已完成並經 code review、自動化測試綠,但 GUI 目視驗證與提交都還沒做。**

## 2026-06-18 — 強化 harness 式 TDD 循環(突變測試 + 回應契約鎖 + 誠實跳過)

專案變大後,後端 TDD 雖紮實,卻沒有任何工具在量「測試到底有沒有效」(本輪開發前一輪就被審查抓到兩次綠燈卻空斷言)。本輪補上三道把關,全部純加法、不動既有測試邊界,並把做法寫進 `jet-dev-loop` 與 `jet-testing` 規範讓之後的開發照做。方向以官方哲學為主(Anthropic「蓋對的系統而非最複雜的、需要才加複雜度」、OpenAI「從簡單開始」、eval 哲學「測試不能被作弊、要讀 transcript 才知道 grader 有沒有用」),輔以 Karpathy(小步、自足)與 Linus(small-is-beautiful、不退步、消除特例)。

- **突變測試(Stryker.NET)**:把規範第 6 節原本只靠人腦的「mutation 思維」變成工具。裝成 local dotnet tool(`.config/dotnet-tools.json`);`stryker-config.json` 限定只突變 Domain 與 Application、`coverage-analysis: perTest`、`break: 0`(先不擋建置)。節奏定為**階段性／里程碑前**對「本輪改動的 Domain/Application 檔」跑一次(單次約 6 分鐘,多為固定開銷),不是每個小任務都跑。spike 已證實能在 .NET 10 跑,並對 `MoneyScaling` 算出 92.86% 的有效分數。
- **回應契約鎖(做法一,手寫)**:`ActionContractTests` 對五個會把資料餵給前端的 action(`validate.run`、`prescreen.run`、`filter.preview`、`query.dataPreview`、`project.load`)鎖住回應的「欄位集合＋型別」,不鎖屬性順序與數值。**關鍵決策:不引入 Verify 快照工具**——手寫零依賴、與現有測試同風格,且沒有「差異一多就全部核可」被 agent 繞過的風險。自證過:故意把一個回應欄位改名,契約測試立刻變紅;改回即綠。
- **誠實跳過(自訂 `[SqlServerFact]`)**:把約 26 個 SQL Server 閘控測試從「沒 LocalDB 就 early-return 當作通過」改成「顯示略過(skipped)」。**關鍵決策:用自訂條件式屬性而非 `Xunit.SkippableFact` 套件**——以「社群＋官方哲學投票」收斂:框架方向(xUnit v3 內建略過、SkippableFact 作者明言要退場)、.NET 團隊自己的 `ConditionalFact`／`ITestCondition` 做法、減少第三方測試依賴的趨勢、以及四個哲學來源,都指向「用框架自身機制、零依賴」;採輕型寫法(只在屬性建構式設標準的 `Skip`、不換 test-case discoverer),避開重型 `ConditionalFact` 在某些 runner 被忽略的毛病(`Infrastructure/SqlServerFact.cs`)。混合檔只翻 SqlServer 側,SQLite 側維持 `[Fact]`。

驗證:有 LocalDB 時全套 **525/525、0 略過**(轉成 `[SqlServerFact]` 的測試照常實跑,沒有副作用);用壞連線字串模擬無 LocalDB 時,那些測試明確顯示「略過」而非混進「通過」。Debug 建置 0 警告 0 錯誤。設計快照:`docs/specs/harness-tdd-strengthening-design.md`。

## 2026-06-18 — 資料驗證與測試:檢視詳情(明細下鑽)+ 匯入橫幅文案修正

「資料驗證與測試」步驟原本每個項目只顯示一個狀態徽章(像「612 筆」「3 個科目不符」「無法執行」),使用者看得到結論卻看不到背後的資料。這輪讓每個會示警、命中或無法執行的項目都能展開,看到後端用同一套 SQL 算出的有上限明細(最多 50 筆)。

- **詳情的三種來源**:(1) 後端早就回傳、前端卻沒顯示的,直接攤開——無法執行的原因、完整性不符科目、編製者彙總、低頻科目;(2) 預篩選命中重用 Step 4 既有的 `filter.preview`,前端組一個「單條件、該預篩選鍵」的情境呼叫它;(3) 借貸不平與空值原本只回筆數,這輪在 `validate.run` 回應新增有上限明細(`docBalanceTest.unbalancedDocuments`、`nullRecordsTest.nullRows`)。
- **關鍵決策:不新增明細查詢 action**。預篩選命中走既有 `filter.preview`(契約先行原則「能重用就不新增」);因為 `filter.preview` 與 `prescreen.run` 共用同一份述詞 `GlRulePredicates`,預覽筆數會等於徽章筆數(以 8 個預篩選鍵的守門測試鎖定)。借貸不平與空值的明細直接內嵌進 `validate.run` 回應,沿用完整性 `diffAccounts` 早就示範的「有界內嵌明細」模式,不另開 action。
- **不變式**:明細一律是衍生顯示、最多 50 筆,不參與任何完整性／借貸／預篩選計算,也不改變任何徽章數字;新明細與既有計數在同一交易內。`validate.run` 只新增欄位、不動既有形狀,所以 `project.load` 的儲存 JSON 回放相容。INF 抽樣本輪不做(它是交付樣本,不是示警)。
- **邊界**:明細查詢是 set-based SQL、放 Infrastructure、SQLite 與 SQL Server 等價;前端只組裝情境與顯示,經 `JetApi.filterPreview` 呼叫。`validate-step.js` 每個項目加可展開的「檢視」詳情(手風琴風格),預篩選命中惰性載入、以 runId 為鍵快取;Step 4 的預覽表格與 `newFilterRule` 抽到 `ui-core.js` 共用(行為不變)。
- **匯入文案**:「重新匯入」與「加入來源」橫幅原文語意混淆,改成以使用者當下動作開頭、講清後果(會換掉／附加現有列、欄位配對要重做、既有測試與篩選結果一併清除)。
- **設計快照**:`docs/specs/validation-testing-detail-drilldown-design.md`。

驗證:全套 **519/519、0 failed、0 skipped**;本機 LocalDB(MSSQLLocalDB 17.0.4025)在,SQL Server gated 測試實跑(借貸不平／空值明細的 provider 等價、日誌事件數 6→8)。新增測試涵蓋:SQLite 明細(借貸不平依差額排序與上限 50、空值逐列異常旗標)、provider 等價、`validate.run` 回應形狀(明細金額經 `ToDisplay`)、預篩選 8 個鍵的「預覽筆數＝徽章筆數」守門(每鍵有非零 sanity 保證,不會以 0==0 假過)。Debug 建置 0 警告 0 錯誤。GUI 目視(展開詳情、預篩選命中預覽、橫幅文案)以 `docs/windows-handoff.md` 任務卡交接。

## 2026-06-15 — Agent 開發迴圈 skill + 診斷日誌檔案 sink（agent 執行時偵錯橋接）

把開發主環境從 Visual Studio + Copilot 轉到 VSCode + Claude Code 後,補上讓 agent 跑完整「建置→測試→執行時偵錯」迴圈所需的兩塊:可執行的迴圈 SOP（skill）與讓 agent 直接讀執行時日誌的檔案橋接。

- **新增 `.claude/skills/jet-dev-loop` skill**:`SKILL.md`（建置→測試→執行時偵錯閉環的六階段 SOP）+ `references/principles-map.md`（SOLID / Clean Code / Spec-Driven / TDD → 指向現有權威,不重述）+ `references/runtime-debug-bridge.md`（執行時偵錯用法）。原則用對照地圖指路,守「一個權威出處」。`AGENTS.md`、`CLAUDE.md` 各加一行註冊指路。設計與外部研究來源（GitHub Spec Kit / SDD、2026 agentic 迴圈實務、Anthropic skill 規範、.NET 結構化日誌）記於 `docs/superpowers/specs/2026-06-15-agent-dev-loop-design.md`。
- **診斷日誌檔案 sink（`NdjsonFileLoggerProvider`,dev-only）**:在現有記憶體 ring buffer 之外**並列**一個檔案 sink,把每筆 `DiagnosticLogEntry` 以 NDJSON 即時 append 到 `%LOCALAPPDATA%\JET\logs\jet-dev-<啟動時間戳>.ndjson`。動機:在此之前,agent 取得執行時真相的唯一路徑是使用者從 DEV 面板 textarea 手動複製（`dev.log.export`）——正是要消除的環節。Debug 啟動後 agent 直接 `Read`/`Grep` 該檔。
- **關鍵決策:純加法 + 共用轉換,不重複**:抽出 `DiagnosticLogEntryFactory`（M.E.Logging state+scope+exception → `DiagnosticLogEntry`）供 ring buffer 與檔案 sink 共用;抽出 `Domain/DiagnosticNdjson`（NDJSON 序列化）供 `dev.log.export` 與檔案 sink 共用——兩條路徑格式必然一致,無 scope 攤平或序列化邏輯漂移。`RingBufferLogger` 與 `DevLogExportHandler` 改為委派,行為不變（既有測試背書）。
- **閘控不變**:檔案 sink 只在 `enableDevTools && diagnosticLogDirectory != null` 時建立。`CreateDispatcher` 新增 `diagnosticLogDirectory` 參數（production 傳 `%LOCALAPPDATA%\JET\logs\`,測試預設 null → 不寫檔、零污染）。Release（`enableDevTools=false`）走 `NullLoggerFactory`,整條 no-op、不產生檔案。
- **TDD**:先寫紅燈（序列化、檔案 sink、composition-root 串接與 Release 閘控）、觀察失敗、再實作到綠。

驗證:Debug 全套 **502/502、0 failed、0 skipped**（+7:NDJSON 序列化 2、檔案 sink 3、串接/閘控 2;本機 LocalDB 在,SqlServer gated 測試實跑）;Debug 與 Release 建置皆 0 警告 0 錯誤。重構安全網 `RingBufferLoggerProviderTests`、`DevLogHandlersTests` 續綠。**真實 app GUI session 寫檔的目視確認**（Debug 產檔、Release 不產檔）以 `docs/windows-handoff.md` 任務卡交接。

## 2026-06-15 — 診斷日誌 instrumentation rollout（稽核關鍵 repo 全接、雙 provider）

承上輪地基（dispatcher 全 action + SQLite 匯入垂直切片），本輪把 `DiagnosticDb` 機制機械式擴展到**全部稽核關鍵
repository**（11 個具名 repo，雙 provider），後端為主、零新機制；前端僅修正 DEV 面板 provider 標籤（見末項）。

- **接入清單（11 repo）**：`SqlServerImportRepository`、`Sqlite/SqlServerGlRepository`、`Sqlite/SqlServerTbRepository`、
  `Sqlite/SqlServerFilterRunRepository`、`Sqlite/SqlServerPrescreenRunRepository`、`Sqlite/SqlServerValidationRunRepository`。
  每個 repo 所有一次性 SQL 執行點走 `Execute*LoggedAsync`（完整命令、參數 name=value、`duration_ms`、`rows_affected`、
  `provider`）；有交易者（Import/GL/TB/Validation）以 `DiagnosticDb.BeginTransaction` 包覆，記 begin/commit/rollback
  並共享 `transaction_id`。Filter/Prescreen 無交易（唯讀 count/preview），動態 WHERE（`WhereBuilder` / 謂詞工廠落地處）
  完整入 `sql.executed`。
- **大批迴圈一律 milestone 收斂（關鍵原則）**：串流/大批迴圈內的逐列執行**一律不逐筆記事件**（百萬列會灌爆 ring buffer
  且與 SqlBulkCopy 路徑不等價），改以階段邊界一筆 milestone（phase/rows_processed/elapsed_ms/throughput）。新增
  `projection.milestone`（GL/TB 投影）與沿用 `import.milestone`。SqlServer 的 `SqlBulkCopy`、GL/TB 的 per-row INSERT
  皆保持未記錄，僅以呼叫點 `Stopwatch` 包覆寫一筆 milestone。
- **既有 SQLite 匯入伴隨修正（逐列→milestone-only）**：上輪 `SqliteImportRepository` 的 staging 逐列 INSERT 會逐筆記
  `sql.executed`——與 SqlServer 的 `SqlBulkCopy`（無逐列事件）不等價、且真實大檔匯入（SQLite 為預設 provider）會以
  staging 事件灌爆 10k buffer、擠掉 action.start/tx.begin。本輪改為 staging 迴圈不記、迴圈後一筆 `import.milestone`
  phase=`staging`，並把 `InsertSourceRecord`/`UpdateRowCounts`/`LoadSources` 去 static 走 logged（與 SqlServer 對等）。
  既有測試 `ImportReplace_LogsSql_*` 斷言由「逐列 staging INSERT」改指「INSERT import_batch（@columnsJson 參數值）」。
- **兩 provider 事件等價（差分證明）**：`ProviderLoggingParityTests` 擷取 import replace 與 GL 投影在兩 provider 下的
  `eventName` 序列並斷言相等（import：begin→3×sql→staging milestone→2×sql→commit→replace milestone；GL：
  begin→2×sql→commit→projection milestone）。

- **DEV 面板 provider 標籤動態化**：`index.html` 的 `<summary>` 與 `dev-panel.js` 的 `devRefresh` 原本硬編碼
  「SQLite」——SQL Server 專案仍顯示「SQLite 資料庫檢視」。修正：後端 `dev.db.overview` 已回傳 `databaseProvider`，前端
  依其值動態顯示「SQLite」或「SQL Server」（`data-bind="dev-provider-label"` + `providerLabel` 變數）。兩檔修正、零後端變更。

驗證：`dotnet build` 0 警告;全套 **495/495、0 failed、0 skipped**（+15:Filter/Prescreen/GL/TB/Validation/SqlServerImport
logging 各對 + parity×2 + staging 不隨列數成長之 metamorphic [Theory]）。**本機具 LocalDB（MSSQLLocalDB 執行中），
全部 SqlServer gated 測試實跑未跳過**——兩 provider 等價在真實 SQL Server 上驗證,非僅編譯。TDD #1–#4 全證。
後續輪次:前端診斷日誌呈現層由 NDJSON textarea 重做為人類可讀卡片時間線（本輪後端契約 `dev.log.export` 不變）。

## 2026-06-14 — 診斷日誌（第三層系統日誌、dev-only）

承使用者用 `dev.log.export` 匯出訊息記錄驗證 TDD 後,新增**第三層日誌——診斷日誌**:完整記錄系統真相
（action、SQL+參數值、transaction、exception、大檔 milestone）供底稿校驗追溯。獨立於既有兩層——
result_* 表（審計留痕）、`IMessageLogStore`（UX 訊息）——兩者完全不動。`Microsoft.Extensions.Logging` +
LoggerMessage 來源產生器,不引入 Serilog/NLog。

- **地基**:自寫 `RingBufferLoggerProvider : ILoggerProvider, ISupportExternalScope, IDiagnosticLogStore`
  （bounded ring buffer,預設 10,000、滿則覆寫最舊、in-memory 重啟清空）。`DiagnosticLogEntry`（純資料 record）
  與 `IDiagnosticLogStore` 埠放 Domain（framework-free,**不引用 M.E.Logging**）;provider 與 `DiagnosticDb`
  helper 放 Infrastructure;dispatcher 事件放 Bridge。
- **correlation/transaction 不手動傳遞**:`ActionDispatcher` 每次 dispatch 生成 `correlation_id` 並 `BeginScope`;
  **同一 `LoggerFactory`** 的子層 logger（Handler/Repository）經 AsyncLocal 自動帶入。repo 開 transaction 時
  `BeginScope({transaction_id})`,其間所有 SQL 日誌共享該 id。`RingBufferLogger` 經 `IExternalScopeProvider`
  把 active scopes 攤平進 entry。
- **SQL 逐呼叫點接入（透明 decorator 不可行）**:concrete `SqliteConnection`/`SqlConnection` cast、`SqlBulkCopy`、
  provider 參數型別（`SqliteType`/`SqlDbType`）使連線/命令 decorator 會破壞既有碼;DiagnosticSource 只含 SqlClient
  不含 Sqlite。故 `DiagnosticDb.Execute*LoggedAsync` 擴充方法**只改 execute 呼叫點**（不動 command 建立/參數加入,
  保留 provider 型別）,記錄完整命令、參數 name=value、`duration_ms`、`rows_affected`、`provider`。
- **dev 閘控 + 零成本**:`enableDevTools` 才建 provider + factory（`SetMinimumLevel(Trace)` 以收 Debug 級 sql/tx）;
  Release 用 `NullLoggerFactory`（log no-op）。`ILogger<TSelf>` 以**可選參數**注入（預設 `NullLogger`）,既有
  ~30 個 repo 建構點免改。`dev.log.export` 改讀 ring buffer → **NDJSON**（每行一筆完整 JSON）、跨專案、不需 active project。
- **本輪接入範圍（已與使用者確認:機制 + 稽核關鍵 repo,本輪先做匯入路徑）**:地基 + dispatcher（全 action 生命週期/
  exception）+ **SQLite 匯入 repo**（replace/append 的 SQL + transaction + replace milestone）。其餘稽核關鍵 repo
  （SqlServer 匯入、GL/TB 投影、驗證、預篩選、進階篩選）以**同一 `DiagnosticDb` helper** 機械式接入,列為後續輪次
  （機制與 pattern 已就緒、零設計風險）。

驗證:`dotnet build` 0 警告;全套 **480/480、0 failed / 0 skipped**（新增 8:ring buffer 覆寫/scope 捕捉/exception、
dispatcher 生命週期/exception/**跨層 correlation**、SQL 完整命令+參數+rows_affected、transaction commit/rollback
共享 id、import milestone、NDJSON 每行可被 `System.Text.Json` 解析）。**8 項 TDD 全證**。前端「DEV — 診斷日誌匯出（NDJSON）」
面板依 jet-testing（禁 WinForms/WebView2 E2E）走人工 GUI 驗證,已記 `docs/windows-handoff.md`。

## 2026-06-14 — 開發工具：訊息記錄完整匯出（dev.log.export，隱藏 dev-only）

使用者 GUI 實測確認 SqlBulkCopy 改造後匯入逾時已解決（同日截圖:「GL 匯入完成…1,403,327 列、耗時 **88.7 秒**」,對照稍早 14:46 的「執行逾時」）。為讓後續測試能把**完整 log 字串**交給 AI 做更完整的 TDD 驗證（前後耗時對比、行為正確性）,新增隱藏開發工具,把「狀態與訊息」面板的完整內容可複製地匯出。概念同 `dev.db.overview`（DEV — SQLite 檢視）:僅 Debug 組建註冊、Release 隱藏、唯讀、**零新增捕捉**（曝現有資料）。

- **後端極小（重用）**:新 `dev.log.export` handler 重用既有 `IMessageLogStore.GetRecentAsync`（已 provider-routed）,回傳與 `log.recent` 同形狀 `{ messages: [{ occurredUtc, level, text }] }` 但**不封頂**（`int.MaxValue` → 全部,受 `app_message_log` 保留上限 ~500 約束;`log.recent` 仍封頂 100）。無新 Domain/Infrastructure、不動 schema;在 `AppCompositionRoot` 的 `if (enableDevTools)` 內註冊（同 dev.db.*）。
- **前端**:第二個 `.dev-panel`「DEV — 訊息記錄匯出」——唯讀 textarea（每列 `occurredUtc [level] text`、舊→新讀如時序）+「重新整理」「複製」鈕（clipboard API,失敗退 `execCommand`）;閘控比照既有面板（`system.ping.devToolsEnabled`,Release 隱藏；app.js 同步隱藏第二面板）。
- **contract-first**:`docs/action-contract-manifest.md` 先加 `dev.log.export` 契約與 JetApi 方法登錄,再實作。

驗證:contract-first → TDD（紅:`dev.log.export` 未註冊 → `KeyNotFoundException`;綠:實作 + 註冊）。全套 **471/471**（新增 `DevLogHandlersTests` 2:完整匯出 >30 筆不封頂、新→舊、wire shape、無 active project 負向;`DevToolsGatingTests` +1:Release 不註冊 `dev.log.export`）。前端面板依 jet-testing（禁 WinForms/WebView2 E2E）走人工 GUI 驗證,已記 `docs/windows-handoff.md`。

## 2026-06-14 — SQL Server 匯入暫存寫入改 SqlBulkCopy 串流（效能待辦結清）

承 SQL Server provider 全 repo 移植後遺留的效能待辦（`jet-guide.md` §13:匯入暫存寫入 row-by-row、1.4M 列約 212 秒）落地。範圍嚴格限於 `SqlServerImportRepository` 的 replace/append 兩條 staging 寫入路徑（服務 GL+TB——這兩方法的全部 kind;AccountMapping 走獨立 `SqlServerAccountMappingRepository`、Calendar 走 `SqlServerCalendarStore`,皆小量參考資料不在範圍）。設計見 `docs/specs/2026-06-14-sqlserver-import-bulkcopy-design.md`。

- **改造**:row-by-row prepared insert（每列一次 `ExecuteNonQueryAsync` round-trip）→ `SqlBulkCopy`（`EnableStreaming=true`、`BulkCopyTimeout=0`、用既有 transaction,對齊 GL 投影先例）。新增 `StagingBulkCopyDataReader`（曝 staging 5 欄）與 private `BulkCopyStagingAsync`;移除 `CreateStagingInsert`。
- **async→sync 橋接（關鍵決策）**:來源是 `IAsyncEnumerable<StagingRow>`,`SqlBulkCopy` 要同步 `DbDataReader`。採有界 `Channel` producer-consumer——背景 task 餵 channel、reader **覆寫 `ReadAsync`** 消費（`SqlBulkCopy.WriteToServerAsync` 的 async 路徑以 `ReadAsync` 推進列,已由特徵化測試對新實作跑綠實證;故不在 `Read()` 阻塞 async）。**producer 必須 `Task.Run` 在獨立執行緒跑**——xlsx SAX 解析是 CPU 密集同步工作,留在呼叫緒會與 bulk copy 爭用而退化成序列執行（PBC 實測:無 `Task.Run` 匯入 49.9s = 解析 + 寫入無重疊;有 `Task.Run` 38.5s = 重疊）。取消/失敗:linked CTS 解除卡住的 producer、`await producerTask` 收束（無洩漏）,例外交呼叫端 rollback。替代方案 `BlockingCollection` + `Read()` 留作後備（差異封裝在 reader 一處）。
- **語意保留（特徵化測試先鎖再改）**:純效能 refactor,故先寫 replace/append 與 SQLite 逐項等價（rowCount/columns_json/staging 內容）、空檔 rollback、cancellation（rollback + 無殘留 + enumerator 已釋放）測試並對 row-by-row 版跑綠,再 refactor、再確認仍綠——確保零語意漂移。
- **效能實測與「瓶頸上移」結論（PBC 1,403,327 列、LocalDB）**:匯入 211.9s（row-by-row）→ **約 38s**（SqlBulkCopy 串流,5.5× 加速）。耗時分解（量測佐證）:Excel SAX 解析＋JSON 序列化 floor ≈ 29.7s、bulk copy 本身 ≈ 15.7s(合成快速來源量得)、重疊後匯入 ≈ 38s。**staging 寫入已不再是瓶頸,主導成本上移到 xlsx 解析（上游,不在本輪範圍）——即計畫所預期的「瓶頸上移」。** 投影 ≈ 17–20s。**原訂驗收基準「比值 ≤ 1.5」結構上不可達:解析 floor（29.7s）本身就 ≈ 1.5× 投影,匯入必須解析 114MB xlsx 而投影只讀已提交 staging,故比值下限 ≈ 1.5、實測 ≈ 2.0–2.2 且隨投影耗時浮動。** 結論:規模測試改為只斷言列數正確、耗時/比值僅記錄供人工判讀（對齊既有 scale 測試「不做 wall-clock 斷言」慣例——比值在 1.9–2.2 擺動正證明硬斷言會 flaky）。
- **逾時防護（使用者回報「匯入 GL 失敗:執行逾時」）**:本機（含 re-import:cleanup DELETE 1.4M 列實測 4.9s）無法重現該逾時——舊 row-by-row 版 211.9s 亦完整跑完未逾時,證明周邊命令在規模下不超 30s;`BulkCopyTimeout=0` 也實證有效（38–50s 匯入未逾時）。研判使用者跑的是**修 overlap 前的無重疊版本**,於其硬體上慢且爭用而觸發某 30s 命令逾時。防護:overlap 修復大幅縮短匯入並降低爭用,另對重匯入/失效的百萬列 `DELETE` 設 `CommandTimeout = 0`。若仍重現需使用者提供完整 stack trace 與步驟（fresh / re-import、哪張工作表）以精準定位。

驗證:`dotnet build` 0 警告 0 錯誤;全套 **468/468、0 failed / 0 skipped**（新增 9:replace/append 等價、空檔 rollback、cancellation、小檔不退化、PBC 規模煙霧）。LocalDB 實跑:8 條 SQL Server import 測試全綠;200 列小檔 bulk 14.7ms < row-by-row 20.8ms;PBC 1,403,327 列 `SqlServerImportScaleSmokeTests` 跑綠（列數正確,匯入 ≈ 38s / 投影 ≈ 17–20s）。

## 2026-06-14 — 專案選擇前端重做、專案刪除與資料庫清除、訊息面板兩視圖共用

承 SQL Server provider 全面落地後,補齊專案生命週期的前端入口與一致的訊息可見性(純前端 + 一個新 action)。權威現況見 `docs/jet-frontend-description.md`(picker / 訊息區 / 步驟 0)與 `docs/action-contract-manifest.md`(`project.delete`、`project.list`/`project.load` 形狀)。

- **專案選擇畫面重做**(picker):卡片清單改為單一可點列表——每列顯示客戶名稱、案件編號、provider 標籤(SQLite／SQL Server)、上次開啟時間,**整列可點即開啟**(無獨立開啟鈕);最底「新增專案」列沿用既有建立流程(含 provider 選單);每列尾端低調刪除 icon(Phosphor 風 inline SVG,非 emoji/thin-line)→ 自訂確認框(顯示名稱、提示無法復原)。視覺遵循 minimalist-ui(淡彩 provider pill、扁平 modal、無重陰影)。
- **專案刪除(`project.delete`,manifest 先行)**:硬刪不可復原、不需 active project。先刪資料庫(provider 路由:SQLite `ClearAllPools()` 後刪 `jet.db`;SQL Server `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` 後 `DROP DATABASE JET_{projectId}`)再刪 `project.json` 資料夾;資料庫刪除失敗(如 sqlServer 連線未設定)則資料夾保留、回明確錯誤供重試。新介面 `IProjectDatabaseDeleter` + `ProviderRoutingProjectDatabaseDeleter` 鏡射既有 initializer 範式;cache 於刪除後 `Invalidate`。
- **上次開啟時間**:`ProjectDocument` 加可空 `LastOpenedUtc`(positional 末位、舊 `project.json` 免遷移),`project.load` 時戳記回寫;`project.list` 回傳 `databaseProvider` 與 `lastOpenedUtc`,清單改依 `LastOpenedUtc ?? CreatedUtc` 排序(最近開啟者浮上,非使用者可調)。
- **訊息面板兩視圖共用**:面板原為 `.app-body` 的 grid 欄,picker 視圖隱藏整個 app-body 連帶把它藏掉(雖然 `renderMessages` 已在兩分支都呼叫)。改為移出 app-body、與 picker/app-body 並列於新 `.app-main` flex row——單一 DOM、共用 `state.messages` 與收合狀態,picker 的 `project.list`/`create`/`delete`/`load` 失敗等事件不再靜默。純結構/CSS 變更,JS 邏輯零改動;`.messages` 加 `flex:none` 維持固定寬度(等同舊 grid 的 auto 欄)。
- **SQL Server 連線設定(非程式變更)**:診斷使用者回報的「新建 sqlServer 專案無法操作」,根因為 `JET_SQLSERVER_CONNECTION` 未設定(建案 `EnsureCreated` 失敗留下孤兒 `project.json`、開啟/刪除續錯,且錯誤落在預設收合的訊息面板而顯得「無反應」)——非前端迴歸。設使用者層級環境變數指向本機 LocalDB 即解;一併以 `git show HEAD` 區分證實前後端 provider 串接(create-step 送 `databaseProvider` ↔ `ProjectCreateHandler` 讀 payload)在本地工作副本為通,main(HEAD)仍寫死 default。

驗證:`dotnet build` 全綠;`dotnet test` **459** 全綠(新增 A/B/C 雙 provider 端到端 flow:建立→載入(`currentStep=1`)→清單(provider 標籤)→刪除(SQL Server 連 master 斷言 `JET_{id}` 庫消失);LocalDB 閘控 0 略過、零殘留庫)。picker 與訊息面板互動屬呈現層,依 jet-testing 以人工 GUI 確認(使用者截圖驗收通過)。

## 2026-06-14 — SQL Server provider 全 repo 移植完成(端到端雙 provider 等價)

上一階段只完成 GL 投影的 SQL Server 實作;本階段依 `ProviderRoutingGlRepository` 範式,為**所有剩餘每專案 repository** 補齊 SQL Server 實作 + 路由,使完整工作流程在 SQLite 與 SQL Server 兩 provider 下端到端跑通並等價。權威現況見 `docs/jet-guide.md` §13。

- **範圍=所有每專案 repo**:Import / Tb / MappingState / Calendar / AccountMapping / ValidationRun / PrescreenRun / FilterRun / RuleRun / FilterScenario / DataPreview / MessageLog + Debug-only DevDatabaseInspector,各有 `SqlServer*` 實作 + `ProviderRouting*` 包裝。關鍵洞察:不只運算 repo,**每一個讀寫專案 DB 的 store 都必須路由**,否則 sqlServer 專案的 log / preview / 科目配對會打到錯引擎。
- **共用機制**:`ProjectProviderResolver`(快取 projectId→provider)、`ProviderSelection.Pick`(未知 → `unsupported_provider`)、`ProviderRoutingProjectDatabaseInitializer`(建案 schema 初始化路由)。`project.create` 加可選 `databaseProvider`(僅建立時可選);前端建立表單加 provider 下拉。
- **隔離模型**:每專案一個資料庫——SQL Server = 共用 instance 上 `JET_{projectId}` 庫、forward-only T-SQL schema(對齊 SQLite v3 形狀);連線取自 `JET_SQLSERVER_CONNECTION`(Express/開發 LocalDB 與 Standard/Enterprise/生產共用實作,差異僅連線字串——不為 Express 建獨立 provider)。
- **方言縫隙**(述詞共用 `GlRulePredicates(ISqlDialect)`):分頁 `LIMIT`→`OFFSET/FETCH`/`TOP`、計數 `COUNT_BIG`、`SUM(CASE)` 需 `CAST AS BIGINT`、upsert→`IF EXISTS`、insert-or-ignore→`DISTINCT`、rowid 平手鍵→`run_id`、`EXISTS` 不可入 SELECT 清單→`CASE WHEN EXISTS`、系統表/版本→`INFORMATION_SCHEMA`/`@@VERSION`、識別字 `[ ]`、無 PRAGMA。**關鍵陷阱:SQL Server 無 MARS**——同連線不可在 reader 開啟時下命令:GL 投影用兩條連線 + `SqlBulkCopy`、TB 先把 staging 讀進記憶體再插入。
- **待辦(已記於 §13)**:匯入暫存寫入仍 row-by-row(1.4M 列約 212 秒),最佳化為 `SqlBulkCopy` 串流(GL 投影已採此形狀);`target_gl_entry` columnstore;DuckDB 須先過 benchmark gate。科目配對只機械移植,流程設計仍 parked。

驗證:`ProviderParityJourneyTests` 全程 journey 雙 provider wire 等價(LocalDB 閘控,無則跳過)、`SqlServerGlRepositoryTests` GL 投影等價 + rollback 原子性、`ProviderRoutingGlRepositoryTests` 路由決策;全套測試全綠、`JET_` 暫時庫零殘留。

## 2026-06-13 — 合併 IDEA/VBA 時期 JET 筆記(文件,無行為改動)

把上一代 JET(Caseware IDEA + IDEAScript、Excel VBA + Access 時期)的開發筆記合併進現行文件,目的是保存恒久的審計領域知識與設計動機,避免後期失憶或誤解;技術選型已被取代,故不進現況規格。依「分流配置、詳細但清洗」處理:

- **領域缺口補進 `docs/jet-guide.md`(外科式、帶狀態標籤、不過度宣稱)**:§2.2 補會計科目開頭碼段 1–3(資產負債表,累計餘額)vs 4–7(損益表,當期歸零)語義,解釋既有變動模式為何存在;§4 INF 抽樣補 RDE(攸關資料元素)財務/非財務分類與 ISA 240/330(KAEG)可靠性順序;§19 補傳票術語地區命名(Voucher/Document/Journal Number)統一映射 `DocumentNumber`;§3 補審計工作底稿五段方法論對照,並標明 Step 5 匯出為雛形/規劃中。
- **歷史實作清洗後歸檔到 `legacy/jet-legacy-notes.md`(新檔,`legacy/README.md` 補一條指路)**:工作底稿五段分頁結構、IDEA JE 資料管道(標準化欄名、`傳票金額_JE = POST_DR − POST_CR`、DEBIT/CREDIT 拆分、科目配對 join、期間擷取)、Access SQL 完整性實作、日期維度篩選 SQL 樣式、VBA QueryBuilder(現行 AST Query Builder 的前身)。保留通用 ERP 欄位名作 golden-master / 差異測試對照(`.claude/skills/jet-testing/SKILL.md` §5)。
- **未納入的內容**:筆記中與本工具無關、或不宜進版控的內容一律未納入;受查者相關的識別資訊在歷史歸檔中一律以佔位符代替,不保留原值。歷史 V/R/A、R1–R8 代號僅在 legacy 歸檔作引用,不進 jet-guide 敘述。

驗證:純文件變更,`dotnet build`/`dotnet test` 不受影響;已確認受版控文件未殘留原始識別資訊。

## 2026-06-12 — 使用者驗收回饋:訊息面板收合與持久化、金額保真差分驗算

同日 PBC 匯入強健化落地後,使用者實測 140 萬筆匯入成功並提出三項回饋,當日完成:

- **「狀態與訊息」預設收合 + 持久化**:訊息區原固定佔右側 300px。改為預設收合 44px 窄欄(垂直「訊息」標籤 + 未讀徽章,未讀含 warn 時徽章轉警示色),點擊展開;新 action `log.append` / `log.recent`(manifest 先行)把訊息持久化到專案資料庫 `app_message_log` 表(每專案留最近 500 則,AUTOINCREMENT 位移修剪;超長截斷不拒絕),`project.load` 後還原歷史。設計取捨:存 SQLite 而非 JSON 檔——訊息與專案同生命週期、同備份單位,且避免第二種持久化機制;訊息是 UX 輔助紀錄、非審計留痕(留痕仍在 result_* 表)。前端以 id 水位增量持久化(fire-and-forget),`fromLog` 標記防止還原的歷史重複落庫。
- **耗時可見性**:匯入與標準化完成訊息附耗時秒數(使用者原以為「實機耗時記錄」是 UI 功能;峰值記憶體仍屬交接卡的人工 Task Manager 記錄)。
- **金額保真差分驗算(使用者要求嚴謹檢查標準化是否引入誤差)**:真實 PBC 閘控測試新增逐列差分 oracle——測試內以獨立的 `decimal.Parse` + `Math.Round`(刻意不用 MoneyScaling)重算 staging 原始字串的每列淨額,與 target `amount_scaled` 逐列比對。實跑結果:**1,403,327 列逐列位元相等、量化(>4 位小數)事件 0 件**——本母體標準化誤差恰為零,非僅「在容許範圍內」。過程中差分先揭露一個語意要點:衍生借/貸欄(`debit/credit_amount_scaled`)依 guide §2.1 由**淨額重分類**(負數紅字換邊取絕對值),欄總額與來源欄總額本來就不同——本母體有 26,340 筆負值列(紅字沖銷 416,822,986.83 元),staging 完整保留原始借貸欄字串,衍生欄只是規則計算的標準形,無資料損失。

驗證:全套 435/435(本輪新增 log.append/recent 契約測試 6 + 留存修剪 1);真實檔閘控測試含逐列差分驗算全綠。

## 2026-06-12 — PBC 匯入強健化:大型 xlsx 串流讀取、欄位集合收斂、匯入進度推播

以兩份真實受查者提供資料(114MB / 兩工作表 / 合計 1,403,327 列的 `test-je.xlsx`,與會計格式的 `test-tb.csv`)為壓力對象的一輪強健化(設計見 `docs/specs/2026-06-12-pbc-import-scale-robustness-design.md`),逐項驗證出四個阻斷點 + 兩個 UX 缺口後分九階段實作、每階段一個綠燈 commit:

- **xlsx 讀取改 OpenXML SAX 串流**:原 `ClosedXmlTableReader` 在 inspect/讀欄/讀列都整本載入 workbook(~1GB XML 的 DOM 必然 OOM)。新 `OpenXmlSaxTableReader` 單一讀取器、無檔案大小分支:worksheet 不建 DOM、逐列 forward-only;讀欄名與檔案檢視讀完標頭列即返回(early-exit),114MB 真實檔 inspect 實測 **203ms**。行為一致性逐項鎖定:數值必走 double→(decimal)→InvariantCulture(實測值 `535.04999999999995` 輸出 `"535.05"`)、日期樣式判定抽出純函式 `ExcelDateFormatDetector`(內建 id 集 + 自訂格式碼掃描;科學記號 `0.00E+00` 的 E 與年代記號衝突,故不支援地區年代記號 'e')、sharedStrings 排除 `rPh`/`phoneticPr` 注音子樹(真實檔實測存在)、`r` 屬性缺席以連續計數遞補、標頭範圍外資料欄 lazy 合成佔位欄(不靜默丟欄、不依賴 dimension)。另修正:SDK 以路徑開檔失敗會殘留控制代碼鎖住來源檔,改自管 FileStream 生命週期。手刻 zip 的 `RawXlsxBuilder` 測試工具補足 ClosedXML writer 寫不出的形狀。
- **批次有效欄位收斂(解開真實檔的幽靈佔位欄)**:「上半年」標頭列 T 欄空白(正規化成 `COL_20`)且整欄 678,399 列無資料、「下半年」27 欄連續——具名標頭集合相同的兩張表,原本的欄位集相等檢查會報 `column_mismatch` 拒絕合併。新規則(guide §3.1.5):具名標頭一律屬批次欄位(具名空欄是 schema 聲明),`COL_n` 佔位欄**僅在串流中觀察到資料時成立**;倉儲串流時累積觀察欄名、結束後以 Domain 純函式 `FinalizeBatchColumns` 收斂並同交易回寫 `columns_json`。附加驗證改兩階段:串流前具名快檢(快速失敗)、串流後有效集合終檢(佔位欄帶資料而另一側沒有→誠實拒絕,rollback 該來源)。收斂邏輯放 Domain,未來 SQL Server/DuckDB 倉儲直接重用。
- **會計格式零**:test-tb.csv 通篇以 ` - ` 表示零(Excel 會計數字格式對 0 的顯示),一列即整批 projection_failed。`MoneyScaling` 接受 trim 後恰為單獨半形連字號 → 0;全形/破折號/多字元組合維持拒絕(guide §3.1.2)。
- **SQLite 規模調校(先量測再優化)**:`journal_mode=WAL` 於 EnsureCreated 設定(資料庫檔案層持久),匯入連線層 `synchronous=NORMAL`/`temp_store=MEMORY`/`cache_size=-65536`。規模煙霧測試(10 萬列混合型別)實測 **43,821 列/秒**,外插 140 萬列約 32 秒——量測檢查點裁決:**不做**寫入迴圈 async→sync 微優化(Microsoft.Data.Sqlite async 雖為同步包裝,但實測吞吐已遠超需求)。
- **inspect 列數推估**:`import.inspectFile` worksheets 增 `rowCountEstimate`(dimension 末列減標頭列;nullable、可能過時、僅顯示用),精靈待匯入清單顯示「N 欄・約 X 列」。
- **匯入進度推播(host→web 事件管道首例,使用者選定本輪一併實作)**:bridge 原本只有 request/response。新信封 `{ event, data }`(無 requestId,舊前端安全忽略);Application 定義 `IJetEventPublisher` port、Bridge 的 `WebViewEventPublisher` 實作(背景緒 Publish 經捕捉的 UI SynchronizationContext marshal;未 Bind 前 no-op);`import.progress` 每 20,000 列發布一次,發送點是 handler 的迭代器包裝——reader 與 repository 都不知道進度概念。前端 `jet-api.js` 增訂閱表與 `JetApi.on/off`,精靈顯示細進度條(xlsx 以估計值算百分比、上限 99%,完成以 response 為準)。投影階段進度本輪不做(投影串流在倉儲內部,加進度需動 Domain 倉儲契約——列為後續)。

驗證:每階段全綠,收尾 Debug **428/428**(原 343 + 新增 85)。真實 PBC 閘控測試(`JET_PBC_DIR` 環境變數,`PbcRealDataSmokeTests`)於本機實跑全程:**inspect 203ms;GL 上半年匯入 20.4s + 下半年附加 19.0s(欄位收斂後批次 27 欄、無 COL_20);GL 投影 1,403,327 列 14.2s;TB 187 列含 " - " 零全數投影;進度事件恰 69 次(33+36);全程約 53 秒**。GUI 目視與耗時/記憶體記錄依交接文件任務卡待人工。

## 2026-06-11 — 規則命名全面更名、進階篩選補完、provider 縫隙強化

使用者驗收回饋驅動的一輪大改(設計見 `docs/specs/2026-06-11-rule-naming-and-filter-completion-design.md`),七個階段、每階段一個綠燈 commit:

- **V/R/A 代號全面退役**:歷史代號跨世代歧義(legacy 的 V5=完整性測試被重新編號成 V1,同一代號指涉不同測試),審計員無法直觀理解。新制三層命名:wire key(lowerCamelCase,如 `completenessTest`/`postPeriodApproval`)、資料表 slug(snake_case,如 `result_inf_sampling_test_sample`)、UI 與工作底稿用中文名;程式內單一事實來源 `Domain/RuleCatalog.cs`,正準對照表進 guide §4。`docs/README.md` 同步廢除「規則代號豁免條款」。wire contract 破壞性變更採 manifest 先行;`prescreen.run` 的週末/假日改成對物件(`weekendActivity.postingCount/approvalCount`——原 `postCount/docCount` 的 doc 為自創縮寫),`descNullCount` 收編為 `blankDescription` 物件。
- **schema v2→v3 遷移**:已存規則執行結果**清除不翻譯**(衍生資料,重跑秒級且 INF 抽樣 seed 固定、結果相同),篩選情境(使用者著作的組態)在 C# 段以 JsonNode 逐鍵翻譯保留;`import_batch` 的 CHECK 以重建表搬資料擴充 `account_mapping`;v1 資料庫鏈式升級。
- **provider 縫隙強化(不實作新引擎)**:抽出 `ISqlDialect` 最小方言介面(週末判定/不分大小寫包含/參數命名——只收錄引擎間確實不同的片段)、述詞單一事實來源改造為 provider 中立的 `GlRulePredicates(ISqlDialect)`(DbCommand.CreateParameter)、WHERE 左折疊組譯抽出 `GlFilterWhereBuilder`;等價測試抽象基底以手算 16+2 列 fixture 斷言每條述詞「命中哪些傳票」(值+身分),未來 SqlServer*/DuckDb* 只需加子類跑同一套。
- **科目配對匯入與未預期借貸組合(原 R3)解鎖**:`import.accountMapping.fromFile` 轉正式——格式固定三欄、匯入即投影(staging+target 同一交易)、replace-only、分類白名單、last-wins 去重;prescreen 依 presence(需 Revenue+對方分類)啟用並回報精確 naReason。**0 元金額邊界統一裁決:`amount_scaled >= 0` 屬借方側**(與 DrCr 推導一致;guide §5 R3 原為 `> 0`,與 §6.1 不一致,經使用者裁決統一),等價測試以 0 元傳票守門。
- **進階篩選條件補完**:新四型別——科目配對分析(guide §6.1 三模式,原 A3)、期內/期外(NULL 過帳日兩側皆不命中)、自訂關鍵字(原 A2)、自訂尾數位數(1–12,原 A4;上限源於 MoneyScale×10^12 < long.MaxValue)。A2–A4 不再是預篩選變體、與預設規則不互斥:預篩選跑預設規格供初判,進階篩選用自訂參數收斂母體。
- **欄位配對步驟狀態模型**:消除「已提交綠字+仍可按確認配對」的語意衝突——已提交收合為摘要卡(模式/標準化列數/提交時間/key→欄名清單,僅「重新配對」「預覽標準化資料」),草稿偏離與來源變更失效各有橫幅與還原動作;前端 committed 改存完整快照。
- **前端詞彙清理**:UI 的「投影」一律改「標準化」(技術文件保留「投影」,對照表進 `docs/README.md` 名詞速查);去除 AST/metadata/SQLite 等技術詞彙;`jet-frontend-description.md` 全文改寫為六步執行期前端的權威描述(原五步模板規格退役,模板降為視覺參考)。

驗證:每階段 `dotnet build` + `dotnet test` 全綠,收尾 343/343(原 267 + 新增 76);Release 組態與 GUI 煙霧驗證見交接文件任務卡。

## 2026-06-11 — 使用者資料預覽與開發者工具組建閘控

兩項需求(同日,多來源精靈完成後追加):

- **開發者資料庫檢視的組建閘控**:資料面早已隔離(獨立唯讀連線、零副作用,2026-06-10 落地);本輪補上「正式版不可見」——`dev.db.*` 兩個 action **僅 Debug 組建註冊**(組裝根以編譯期旗標控制,Release 呼叫得到 unknown action),`system.ping` 新增 `devToolsEnabled` 欄位,前端據此隱藏開發面板。測試以 recording host 驗證 Release 組裝下 dev action 不存在、正式功能不受影響。
- **使用者資料預覽(正式版功能)**:新 action `query.dataPreview`——業務資料集白名單(`glStaging`/`tbStaging`/`glEntries`/`tbBalances`,不暴露實體資料表名)的**有界預覽**(預設 50 列、上限 100、回總列數;絕不回完整母體,明細分頁仍屬 `query.*Page` 里程碑)。設計對準兩個情境:欄位配對時對照「欄名 ↔ 實際內容」(來源原貌的 columns 與配對下拉一字不差),進階篩選前掌握資料樣貌(GL 測試母體附概況統計:金額絕對值範圍、總帳日期範圍、傳票數——篩選的數值區間比較的就是絕對值)。前端為折疊式「資料預覽」面板+配對/篩選步驟的換位按鈕;空資料集回空狀態提示而非錯誤。

依同日定稿的設計,一次完成三個階段(macOS 建置全綠;測試執行與手動驗證交接 Windows 端):

- **多來源附加匯入**:資料庫 schema 升至第 2 版——新表 `import_batch_source` 記錄批次的每個來源(檔名/工作表/編碼/分隔符/列數/時間);暫存表加來源序號與來源檔內列號;既有 `row_number` 改義為批次內單調排序鍵(附加自最大值續編),正式表的排序鍵欄位改存批次鍵,確保兩個來源的檔內列號重複時 V3 抽樣排序仍唯一穩定。第 1 版資料庫自動加法遷移(單一交易、冪等),回填值讓**既有專案的 V3 抽樣完全不變**(metamorphic 測試鎖定)。`mode:"append"` 的語意:欄名集合不一致報雙向差集(`column_mismatch`)、附加成功與下游失效(正式表+已提交配對清除)同交易、空來源 rollback 不影響既有批次。多來源批次的投影錯誤帶「檔名 [工作表]」前綴;單來源訊息與單檔時代一字不差。
- **匯入前檔案檢視與多檔選取**:新 action `import.inspectFile`(唯讀零副作用、不需 active project;Excel 回工作表清單與各表正規化欄名,CSV 回偵測鏈判定的編碼/分隔符——可直接回填為匯入覆寫參數)與 `host.selectFiles`(多選對話框,取消回空陣列)。
- **前端匯入精靈與模組拆分**:匯入步驟改為「來源清單 + 加入來源 + 重新匯入」精靈流(多選 → 逐檔預覽 → 依序匯入;失敗中止、已成功來源保留);同時執行架構審查的擱置決策——`app.js`(2,027 行)拆為共用核心(`ui-core.js`)+ 每步驟一個渲染模組(`steps/*.js`,自行註冊)+ 獨立開發面板 + 殼層,並移除測試案件按鈕的齒輪字元。

## 2026-06-11 — 文件體系整頓

依 living specification 與 spec/change 分離的業界實務重整全部文件:

- 新增 `docs/README.md`(文件地圖、寫作規範、領域名詞速查)。寫作規範的核心:人類可讀敘述優先、**禁止自創流水代號**(此前同時存在三套互撞的編號——根目錄工作紀錄的問題編號、交接文件的任務編號、匯入設計的里程碑編號,離開原文即不可解)、領域名詞精確、文件隨程式碼同 commit 更新。
- 根目錄 `PLAN.md` 與 `WORKLOG.md` 退役,內容遷移至 `docs/development-status.md`(現況快照)與本檔(歷史紀錄),舊檔以 git 歷史為檔案庫。`PLAN.md` 當時已嚴重過時(測試數、橋接方法數、CSV 讀取器狀態皆與現實不符)。
- `docs/jet-template-v2.html` 改名回 `docs/jet-template.html`:版本後綴交給 git 管理;改名後 repository 內十處既有引用全部恢復有效。
- 交接文件與兩份設計提案改寫為描述性標題,去除代號;manifest 修復一處損壞的表格(JSON 範例插在表格中間)並把「依某工作紀錄編號之決議」改為自含敘述。

## 2026-06-11 — 多來源匯入精靈設計定稿

定稿多來源匯入的整體設計(`docs/specs/2026-06-11-multisource-import-wizard-design.md`),分三階段:多來源附加匯入(資料模型與 `mode:"append"`)→ 匯入前檔案檢視與多檔選取 → 前端匯入精靈。目標:一個 GL/TB 資料集可由多份檔案(多個 CSV、多個單表 Excel)或一張 Excel 活頁簿的多個工作表(如 Q1–Q4)合併而成,對齊審計員熟悉的 Access 匯入精靈心智模型。維持「一個資料集一個匯入批次」不變式;`row_number` 改義為批次內單調排序鍵以保住 V3 抽樣的可重現性。

## 2026-06-11 — Windows 端完整測試與 CSV 標頭讀取修正

使用者在 Windows(VS 2026、.NET 10)執行全套測試:初跑 236/237,唯一失敗揭露 Sep 套件內建標頭模式遇重複欄名(兩個「金額」)直接拋例外,自製的標頭正規化根本沒機會執行。修正(commit `36876e4`):CSV 讀取器改 `HasHeader = false`,自行消費第一邏輯列再交給與 Excel 讀取器共用的 `TabularHeaderNormalizer`——Sep 只負責 tokenize,標頭規則單一事實來源。修復後 237/237 全數通過(含當時已棄用的 repo-local 真案冒煙測試)。教訓:重用此讀取器的新功能(如匯入前檔案檢視)一律沿用「自管標頭」模式,不走 Sep 的標頭 API。

## 2026-06-11 — 架構審查與依賴方向整理

進入多來源匯入開發前的完整架構與代碼審查(SOLID、Clean Code、test smells、資料庫與前後端隔離、SQL Server/DuckDB 更換餘裕)。結論與修正全文見 `docs/specs/2026-06-11-architecture-review-design.md`,要點:

- **通過**:Domain 純淨、Application 無 SQL、bridge 薄、前端零業務規則、repository 介面無 ADO.NET 型別;新增資料庫 provider = 新增一組 repository 實作 + composition root 切換,上層零修改。
- **修正落地**:錯誤契約(`JetActionException`/`JetErrorCodes`)與儲存 JSON 設定(`JetJsonStorage`)從錯誤的層搬入 Domain,消除雙向的跨層引用;repository 介面參數順序修正;三個多重斷言測試拆分;依賴方向規則明文寫入 `AGENTS.md`(`Bridge/Host → Application → Domain ← Infrastructure`,唯一文件化例外為 dev-only 的 `DemoWorkbookWriter`)。
- **新機制**:macOS 端無法執行的驗證以 `docs/windows-handoff.md` 任務卡交接 Windows 端。
- **擱置決策**(收錄於 `docs/development-status.md`):app.js 拆分綁定精靈前端階段、dev.db wire 欄位改名、CSV 開檔快取、測試按鈕字元、DuckDB 時機。

## 2026-06-11 — 匯入強健化:格式正規化與 CSV/.txt 讀取器

受查者提供資料(PBC)常為 CSV/文字檔(台灣常見 Big5 編碼)與多種日期寫法(含民國年),分兩段落地(commit `ea56764`):

- **Domain 純函式**:`DateNormalizer`(判定順序 ISO → 顯式西元 → 民國年(預設啟用,可由 project.json 關閉)→ Excel 序列值 → 寬鬆 fallback 加 1900–2100 年份防衛——民國年必須排在序列值之前,因為七位數如 `1140611` 落在兩者皆合法的範圍;兩位數年一律拒絕)、`TabularHeaderNormalizer`(trim、空白標頭補名、重複加字尾;Excel 與 CSV 共用)、`CsvDialectDetector`(引號感知的分隔符統計偵測)。
- **Infrastructure 讀取器**:`CsvTableReader`(Sep 套件串流讀 `.csv`/`.txt`)、`EncodingDetector`(確定性偵測鏈:BOM → 嚴格 UTF-8 驗證 → Big5,失敗即報錯不猜)、`CompositeTabularFileReader` 按副檔名分派。契約新增 `sheetName`/`encoding`/`delimiter` 可選參數與 `sheet_not_found` 錯誤碼;格式規則明文化於 guide §3.1。
- 金額維持寬鬆解析(千分位/正負號/小數點;不處理貨幣符號——審計員會先做初步清理),真正的保證是 RFC 4180:引號內的逗號不被當分隔符。

## 2026-06-11 — 工作流程介面重整與進度保存

三項使用者需求(同樣在 commit `ea56764`):

- **一致的下一步引導**:六個步驟統一頁尾(閘門未滿足時「下一步」停用並說明缺什麼),步驟導航顯示完成/鎖定狀態與缺漏 tooltip。
- **規則卡可讀化**:V1–V4 / R1–R8 以「代號徽章 + 白話標題 + 一行說明 + 狀態徽章」呈現,審計員不需查文件即可理解每條規則在測什麼。
- **進度保存與結束**:新 action `project.saveProgress`(記錄使用者所在步驟,允許倒退)與 `host.exitApp`(host 能力,先存進度再關窗);重開應用程式回到上次所在步驟。

## 2026-06-10 — 版面切換修正、資料庫檢視唯讀化、組態歸屬收斂

- **版面根因**:HTML `hidden` 屬性靠瀏覽器預設樣式的 `display: none`,被作者樣式 `.app-body { display: grid }` 蓋過,導致專案選擇與工作流程兩視圖同時可見互擠。修正:全域 `[hidden] { display: none !important; }` 守則 + 訊息欄捲動修正。
- **開發者資料庫檢視唯讀化**:檢視一律走 `Mode=ReadOnly`、不共用快取、不進連線池的連線直讀磁碟檔——零副作用(不建 schema、不建檔),測試以檢視前後資料庫檔案 SHA-256 不變鎖定。
- **組態歸屬**:`project.json` 新增 `databaseProvider`(目前 `"sqlite"`,舊檔讀取時正規化免遷移);架構驗證期的 provisional action(`__jet.probe`/`__jet.projectConfig`)與其 %LOCALAPPDATA% prototype 資料庫**退役刪除**,消滅專案儲存之外的組態旁路;`system.ping` 轉正為前端啟動檢查。157 個測試通過。

## 2026-06-10 — 資料驗證與測試 + 進階條件篩選

工作流程合併決議:前端步驟模型由七步收斂為**六步**,「資料驗證」與「預篩選」合併為同一步驟「資料驗證與測試」(對齊 `docs/jet-template.html` 的設計;既有專案的 currentStep 不需遷移)。後端落地:

- V1–V4 與 R1–R8 全部為參數化集合式 SQL;規則述詞片段在 `SqliteGlRulePredicates` **只寫一次**,預篩選計數與進階篩選條件共用(單一事實來源;未來 SQL Server provider 重寫同名片段)。
- V1 以 LEFT JOIN + UNION ALL 模擬 FULL OUTER JOIN(不依賴 SQLite 3.39+);V3 抽樣 `ORDER BY (source_row_number * seed) % 2147483647`——排序鍵刻意用 `source_row_number` 而非 AUTOINCREMENT 主鍵(重投影後不穩定),seed 與樣本落地保證可重現;R4 門檻平均以 C# decimal 計算,不以 SQLite 浮點 AVG 作權威。
- 進階篩選:前端 Query Builder 只組條件 AST(左折疊結合律),後端白名單映射欄位識別字、值一律參數綁定;舊系統(VBA ServiceFilter)十二條預篩選條件全部遷移為可組合條件型別。151 個測試通過。

## 2026-06-10 — 測試案件載入器與儲存 JSON 可讀性

- **測試案件載入器**:每個步驟一鍵套用 deterministic 測試案件(固定 LCG,不用 `System.Random` 避免跨 runtime 漂移;GL 2,000 列、TB 100 科目、2025 年度,埋入各規則的測試特徵但傳票逐張平衡)。關鍵原則:demo 流程走**與使用者上傳完全相同**的 file-based 匯入管線(寫成真實 xlsx 再匯入),不開 row-based 後門。
- **儲存 JSON 可讀性**:`System.Text.Json` 預設跳脫非 ASCII,資料庫裡中文變 `\uXXXX`;新增共用序列化設定(`UnsafeRelaxedJsonEscaping`)讓儲存層 JSON 保持中文原文,並逐項確認 SQL Server 可攜性(TEXT → NVARCHAR(MAX) + OPENJSON)。
- 假日/補班日匯入(`import.holiday`/`import.makeupDay`)落地。79 個測試通過。

## 2026-06-10 — 專案持久化、檔案匯入與欄位配對

第一個具備資料庫能力的版本:專案資料夾持久化與選擇畫面、`data/JE.xlsx`(9,132 列)/`data/TB.xlsx` 串流匯入暫存表、欄位配對(自動建議 + 提交投影到正式表)、開發者資料庫檢視面板。實作前先以原始 zip 解析驗證真實資料形狀,發現三個會讓天真實作失敗的事實並納入設計:金額是**文字儲存格**、借貸欄 cell **稀疏**(缺 cell 不是空字串)、無項次欄(契約的必填標記與現實衝突,改為條件式)。其他關鍵決策:

- 投影即 import-stage normalization:C# streaming 以 `decimal` 解析金額乘 `MoneyScale`(10000)轉 scaled BIGINT;任一列失敗整批 rollback 並回報列號。
- 每專案一個 `jet.db`,資料表不帶 project_id(檔案即範圍);`staging_*`/`target_*`/`config_*` 前綴模擬 schema 分層。
- 重複匯入(replace)在同一交易內清除舊批次、暫存列、正式列與已提交配對——下游一律失效,防止殘留資料污染。52 個測試通過(含真實資料冒煙)。

## 2026-06-04 — 初步雛形架構符合性審查

對最初的 scaffold 做文件邊界符合性審查。總體結論:核心邊界全部守住(薄 host、薄 bridge、純 Domain、前端只走 bridge、參數化查詢、不搬 GL/TB 列集合)。發現的問題與其後續結局:

| 當時的發現 | 結局 |
|:---|:---|
| 契約文件把未實作的 action 寫成「Current」,且描述了不存在的 script factory | 已解決:manifest 逐項標注 Implemented/Planned/Legacy;provisional action 於 2026-06-10 退役 |
| 驗證指令指向不存在的測試專案 | 已解決:當日建立 xUnit 骨架,其後成長至 237 個測試 |
| 前端骨架七步 vs 規格五步不一致 | 已解決:2026-06-10 收斂為六步(驗證與預篩選合併) |
| bridge 對所有例外回同一錯誤碼 | 已解決:`JetActionException(code, message)` 錯誤碼模型;未知 action 維持 fallback `bridge_error`(manifest 明載) |
| CancellationToken 未串接 | 仍開放:列於 `docs/development-status.md` 技術債 |
| 組態儲存每次操作重複開連線建表 | 已消失:該 prototype 儲存體於 2026-06-10 整個退役刪除 |
| 資料表未採 `config_` 前綴 | 已解決:正式 schema 全面採分層前綴 |
| 連線工廠無介面、未注入 | 已結案:2026-06-11 架構審查認定連線解析屬 provider 私有實作,不構成替換縫洩漏 |
| `JET.Application` 與 WinForms 命名空間衝突 | 接受現狀(文件指定的資料夾名,完整限定迴避) |
| 有內容的資料夾殘留 `.gitkeep` | 已清除(2026-06-11 文件整頓輪) |

## 2026-06-04 之前 — 架構骨架

初始雛形:薄 `Form1` host → WebView2 bridge(JSON transport)→ 手寫 `ActionDispatcher` → Application handlers → 純 Domain → SQLite Infrastructure;前端三檔結構(`jet-api.js` 唯一 postMessage 出口、`state.js` 輕量 store、`app.js` 渲染)。

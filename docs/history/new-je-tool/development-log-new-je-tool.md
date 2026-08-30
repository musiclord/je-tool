# JET 重要開發紀錄

本檔只保留會影響目前產品理解的重要里程碑。每次測試的完整命令、逐檔修改與已刪除的設計草稿，都由 Git 歷史保存；現行行為仍以 `jet-guide.md`、`action-contract-manifest.md`、`jet-frontend-description.md` 與 `development-status.md` 為準。

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

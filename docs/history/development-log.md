# JET 歷史開發紀錄

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/development-log.md` 取回。

依日期排列，新的在上。這份紀錄由兩個來源專案的開發日誌合併而成，涵蓋 2026-06-04 到 2026-08-26；
`je-tool` 建立之後的開發不寫在這裡，改由 [`../development-status.md`](../development-status.md) 指向的現行計畫，
以及 [`specs/`](specs/) 裡已結束的計畫承接。

本檔只解釋當時做了什麼、為什麼那樣決定、驗證到什麼程度。它**不描述 `je-tool` 現在的行為**；精確規則一律
以 `src/`、仍有效的測試與 [`../jet-guide.md`](../jet-guide.md)、[`../action-contract-manifest.md`](../action-contract-manifest.md)、
[`../jet-frontend-description.md`](../jet-frontend-description.md) 為準。紀錄中提到的測試通過數、命令與檔案
路徑屬於當時的環境，不能當成 `je-tool` 的通過證明。

紀錄的粒度在 2026-06-29 前後改變，原因見文中的分界說明。

## 2026-08-26 — 真實案件驗收、底稿樣式與對抗複審結案

使用者在同一場人工驗收先回報一、二、四通過；第五項指出 Working Paper step1-2 人工格異常淡黃底、step4-1 混入新細明體，以及
Validation Report 欄位資訊可見區黑框，續驗再指出 step4-1 資料右側與末列後方仍殘留黑框。兩輪都先用失敗測試固定，再於不改審計資料與計算語意的邊界內修補。
六份正式輸出現在把 style font 與 rich-text run font 統一為微軟正黑體；這是使用者明示核准、刻意不同於 legacy 的呈現契約。
Working Paper step4-1 的 cell、row、column style 框線全部移除。使用者其後明示剩餘第三與第五項全部驗收完畢，一場制清單至此銷帳。

使用者要求結案前再做一次反作弊複審：產品 assembly 不得依賴測試、真案 fixture 或已知答案，自動測試只能作外部對抗標準。
複審沒有找到產品依賴測試或真案資料，也沒有放寬 assertion，另找到兩個缺口並修正：呼叫端提供路徑型 `fileName` 時可能把來源根寫進本地資料庫；
建案錯誤缺少可靠的欄位歸屬，因此 error envelope 增加後端選填 `field`，前端不解析 code 或 message。
最後 fresh ReleaseCandidate 全部通過（Release 3865 total／3857 passed／8 expected skips、Provider 280／280、GUI 20／20、migration 468／468、
report／package 504／504），計畫已驗收，沒有下一個可由 agent 開始的階段。

## 2026-08-20 — 默認輸出對齊、本地可攜性與 UX 收斂計畫結案

五個線性階段的程式與文件全部完成；最終一場制人工驗收於 2026-08-26 經修補續驗後由使用者全數通過。

三項行為改變：**Pre-screening Report 成為匯出底稿的默認輸出**——步驟五匯出面預設勾選、可取消；沒有現行預篩選結果時畫面先明示會補跑一次，
由使用者按同一顆按鈕確認後才依序執行。**SQLite／DuckDB 案件資料夾成為受測的可攜單位**——來源 JET 正常離場後把整個案件資料夾搬到別的磁碟或別台電腦，
重開即可續作；保證邊界是 cold copy，WAL 使用中的熱複製與 SQL Server 案件不在其內。**`audit_event_log` 的 append-only 改由資料庫層強制**（schema v9）——
SQLite 與 SQL Server 以 trigger 拒絕 UPDATE／DELETE，DuckDB 1.5.3 沒有 trigger 也沒有 table-level 權限，維持程式紀律並由守衛把關。

兩項裁決值得記住。一是**本地刪案留痕決定不做**：本地案件屬個人工作性質，持久刪案證據是中心化管理議題，只屬於未來的 SQL Server 線；SQL Server 既有的刪案紀錄維持。
二是 **UX 大題併入本計畫**——agent 建議另開一場獨立收斂，使用者裁決併入並接受計畫變大、收尾拉長的代價；做法採「盤點→裁決→落地」，
產出七項發現交使用者逐項裁決，全部核准後才落地。七項都是操作與呈現層收斂：已存篩選情境的二段移除確認、刪案對話框的鍵盤與焦點契約、
資料預覽的具名載入與就地重試、長作業的經過時間、六步導航的焦點與位置宣告、建案失敗的表單就地回復，以及正式版結構表面收斂為方角與細線。

盤點時沒有發現需要版本化復原（undo）的項目。Pre-screening Report 的長期去留與預篩選條件的最終棄用清單仍留待未來另場收斂，
audit log 的查詢介面、匯出與保留政策仍屬另案。

## 2026-08-20 — 預篩選重定位與小案收斂計畫結案

四個線性階段全部完成。Criteria Selection Report 與 Working Paper 已解除預篩選前置；步驟四把依分錄編製者與較少使用科目兩條彙總提升為主要判讀面，
逐筆命中明示為輔助訊號；Pre-screening Report 保留完整內容但改為純可選輸出，預篩選規則目錄自此凍結，不再新增逐筆規則。
小案部分完成 schema v8 的 project-local 最小 audit log、INF／WorkingPaper 兩份 runtime 範本的封閉 external-workbook 衛生例外，以及五個右側資料預覽頁籤顯示名。
收案 Release 全套 3695 total／3687 passed／0 failed／8 expected skips，Provider 279／279。

最終一場制人工驗收由使用者回報五項全過。外部連結項目由使用者明示依自動 package 證據裁決通過；本機沒有執行 Microsoft Excel 操作，不把裁決改寫成實測證據。

## 2026-08-18 — 業務模型收斂與全面盤查清理計畫結案

五個線性階段全部完成：先以篩選條件鏈草圖證明模型能揪出真實矛盾，再把預篩選的輔助定位、KCT A–J 現行執行對應、六步資料生命週期與跨層責任回流權威文件；
全面盤查的十五項發現經使用者裁決後逐項清理。

最終一場三項總驗收由使用者回報全過。真實案件條件情境與差異分類通過；六份正式報表的 Excel 開啟／渲染，以及 Working Paper step4 家族的捲動／列印，
依使用者明示裁決視為通過。本機未安裝 Microsoft Office，因此這兩項沒有實際執行 Office 操作，不把裁決改寫成不存在的操作證據。

## 2026-08-17 — 業務模型收斂與全面盤查清理計畫立案（jet-converge 首次實戰）

使用者以 `$jet-converge` 發起收斂訪談，處理的核心問題是：多輪改版後程式碼的審計商業模型比 legacy 更難讀出、模糊邊界累積、且部分歷史人工驗收是在模糊狀態下帶過的。
比較四個方向（不做、模型先行、盤查先行、合併分階段）後，使用者裁決採「模型為鏡、盤查為帚」的合併計畫，共五個線性階段，第一階段是「篩選條件鏈草圖驗證閘」。

同場兩項重要裁決：一、**預篩選降為輔助條件、KCT 小組指定的十組條件為實務主線**——這是 repository 首次登錄的業務定位轉向，模型層立即生效，產品層（step gate、報表、UI）對齊由盤查逐項登錄後依裁決落地；KCT 十組條件的精確定義尚未登錄，先查 legacy、查無再由使用者提供。二、歷史模糊驗收全面重開，但同一行為只驗一次、過度細節改由自動測試吸收，最終收在一場去重總驗收。

## 2026-08-17 — Skills 理念內化（jet-converge）與盤查銷帳

依使用者裁決把外部工具評估從「裝或不裝」改為「理念層與包裝層分開評估」。mattpocock `grill-with-docs` 與 sandeco `reversa` 都因包裝與 JET 的
canonical skill root、單一事實來源及機敏資料邊界衝突而不安裝，理念內化為 project-owned explicit-only skill **`jet-converge`**：框定、發散、挑戰、收斂四階段。
同輪移除孤兒 `legacy/JET-legacy/`（106 個追蹤檔，由 Git 歷史保存），補齊 `legacy/README.md` 條目。產品程式一行未改。

## 2026-08-17 — Harness 盤點收口：permissions 硬擋、skills 精簡與過時檔清理

依使用者裁決落地四件事：Claude Code 側把直接 `dotnet test`、`git add -A` 等與機敏資料目錄寫入的禁令升級為 `.claude/settings.json` 的 `permissions.deny`；
八個 mattpocock workflow skills 中六個因引入一個月零使用移除，保留 `code-review` 與 `diagnosing-bugs`；移除孤兒稽核腳本與過時的 `docs/jet-template.html`；
output style 正本改為 `~/.claude/output-styles/`。`AGENTS.md` 與 `CLAUDE.md` 合計 199 行，回到 Claude 官方 200 行建議內。
Release 全套 3670 total／3662 passed／0 failed／8 登錄 skip。

## 2026-08-16 — 跨 host agent 機制過審與守衛補齊

對 Codex 與 Claude Code 共用的 agent 機制做了一次全面過審，只動文件與測試。主要發現是兩側守衛不對稱：Claude 側缺 wrapper 等於該 skill 在 Claude 側不存在，
卻沒有機器把關；`SkillExplicitInvocationPolicyTests` 因此擴充為雙側守衛。同輪修掉載入預算表過時、`data/legacy-parity-work/` 未寫進 `AGENTS.md` 等三處文件與現況不符。

## 2026-08-15 — 核心能力補完計畫結案：最終人工驗收與 PBC harness 修正

使用者完成最終一場制人工驗收，五項全部通過（匯入與配對、資料驗證、科目分類與篩選、六份報表抽驗、舊案相容），「核心能力補完與測試 Harness 現代化」計畫至此結案。
同場對 PrivateParity envelope 的 PBC fixture 單點裁決「核准 harness 修正」，同日落地：四個 `JET_PBC_DIR` PBC fixture 測試以精確身分登錄為預期 skip，
只有未登錄的 skip 或零執行才構成缺失。修正後實機 `-Profile PrivateParity` 首次誠實通過（17 項：13 過、0 失敗、4 個登錄 skip）。

同場使用者另裁決：`data/temporary-test-case/` 對本專案 agent 開放常設本機唯讀存取，用於驗收產出與 `legacy/idea-tool.bas` 業務邏輯一致性比對，不需逐案 packet 核准；不進 git、不外傳、不寫進 tracked files、不在對話揭露客戶明細等禁令全部維持。條文正本在 `AGENTS.md` 機敏資料節。

## 2026-08-15 — 跨案件 PrivateParity 純驗證封板

以 `crosscase-pure-verification` 批次對 `case-A` 與 `case-B` 重跑完整 action journey（SQLite、DuckDB 與 SQL Server 各兩次），歷史差異裁決不再沿用成白名單，
每項差異由本輪重新分類。metric 層 241 筆分類差異中 JET 缺陷 0、provider mismatch 0、未分類 0；外觀與內容差異全由既有封閉裁決分類、未知 0。
case-B field-info 的 legacy+2 差異依獨立來源型別 oracle 重分類為輸入資料條件，不再是 JET 缺陷。產品程式一行未改；最終僅剩一場制人工驗收。

## 2026-08-15 — 整合封板與驗收候選

一般可交付候選已完成封板。ReleaseCandidate 的 Release、Provider、20 場 GUI、migration、report／package 五段依序全部通過：
Release 3623 total／3615 passed／8 個精確登錄 skip，Provider 277／277，GUI 20／20，migration 401／401，report／package 440／440。
另以獨立 100K correctness smoke 取得 375／375，並完成正式發布與封裝完整性核對。本階段沒有修改產品程式、wire contract、schema、審計規則或 provider SQL。

## 2026-08-15 — Harness 完整循環與 lifecycle closure

六組 critical FsCheck properties 成為 Release 必過檢查，揭露 `TabularHeaderNormalizer` 對預先占用 suffix 的欄名會產生 collision，已依既有全域唯一契約修正。
Stryker 固定 4.16.0、四個 Domain 與 AuditCore pure targets，保存 76 個 target-only mutants 與 4 個已分類 survivors，但不以分數阻斷，也不納入 ReleaseCandidate。
Agent GUI inventory 由 16 增為 20；`ReleaseCandidate` 解除 fail-closed 並依五段 fail-fast，最終單一 composition 全部通過。產品 action、wire、schema、審計規則與可見流程均未改。

## 2026-08-13 — GUI harness 核心替換與完備性復盤

Agent GUI harness 保留原有 PowerShell 控制面，將 raw-CDP DOM／input core 替換為 `.NET` Selenium／EdgeDriver helper。Selenium 固定 4.46.0，正式 `Run`／`Gui` 強制離線。
依使用者要求重開第二階段復盤後，補齊多項守衛，並修正只有中文 evidence 才會觸發的 Windows encoding 缺陷（producer 與 consumer 改為 strict UTF-8）。
最終 GUI 16／16 通過、316 actions、0 screenshots。中途評估「自動化不得干擾前景」會改變 Explorer、UIA、WebView2、focus 與視窗可見性的正式測試拓撲，
因而可能影響結果品質；依使用者的條件撤回該臨時要求，最終使用原本可見 GUI 拓撲。產品 action、wire、schema、規則、provider SQL、UI 與 `logicVersion` 均未改變。

## 2026-08-13 — 核心能力補完與測試 Harness 現代化計畫核准

使用者核准下一階段的整合開發計畫，範圍包含過帳狀態與有效分錄母體、核准日同總帳日、人工／自動分錄資料品質、可擴充科目分類、借貸組合多選、
未預期借貸組合零元邊界、typed RDE／進階條件，以及 xUnit v3／Microsoft Testing Platform 與 WebView2 GUI harness 現代化。計畫採十三個線性 fresh-session 階段。
計畫明確移除 1,000 萬列合成 GL 評測；100K correctness smoke 保留，既有 5M／20M benchmarks 維持 opt-in、非 release gate。真實案件 PrivateParity 只在最終階段本機明示執行，不納入日常 Release。

## 2026-08-13 — 流程總覽單一資訊歸屬 6/6 驗收結案

使用者完成流程總覽六項人工驗收並回報全過：六階段生命週期、總覽純檢閱不導航、每項資訊單一歸屬、等待前置說明、兩層鍵盤焦點、1280px containment，
以及三組分析按需展開。目前沒有待使用者人工驗收項目。

## 2026-08-12 — Working Paper／前端 12/12 驗收結案與流程總覽狀態登錄

使用者完成同一場十二項人工驗收並回報全過，正式銷帳 Working Paper 封面嵌入物件、Step 2／3／4 範本樣式、legacy 多條件篩選、防案件特化複核，
以及 1360px 半屏、欄位配對、「其他資料」、唯一資料夾入口、測試入口移除、摘要優先總覽與鍵盤焦點。Legacy parity 階段 1–10 至此完成自動封板與人工驗收。

其後的流程總覽修正是獨立的純前端改善。參照 MindBridge、Power BI、SAP Fiori、GOV.UK task status、PCAOB 與 WAI-ARIA 後，總覽被明確定位為「案件狀態檢閱面」：
六階段卡只在 modal 內選取一個共用執行紀錄，不關閉視窗、不切換主畫面，也不保存流程位置；畫面採單一資訊歸屬，不由前端推算新的審計結論。
本輪沒有更動公開 action、payload、schema、審計規則或 `logicVersion`。

## 2026-08-05 — Legacy parity 審計差異與建案補償收口

兩個 case-B FieldInfo 缺陷共用同一根因：legacy 對數值識別欄保留 `_Temp` 文字 shadow，並把 canonical 文字欄附加到欄位定義尾端；JET 原先只做 in-place rename。
補回這項投影語意後 6 筆 `JetDefect` 全部消失，immutable registry 只保留 235 筆既有已裁決差異。這是報表欄位定義的相容修補，不推進 `logicVersion`。
同輪補上 `project.create` 的部分成功原子性：新案 `project.json` 先在同 root staging 完整寫入再原子發布，SQL Server 以 Serializable 交易序列化同名建立；不新增 action、payload 或 error code。

## 2026-08-03 — 查核正確性收口人工驗收與匯出外觀複核

使用者完成一場制人工驗收，並回報六組檢查全過：完整性未通過的下游阻擋、KCT 缺少前置資料、作業中離場、多檔匯入失敗回復、Release 無 demo 與流程總覽文案至此銷帳。

同日另依 `legacy/idea-script.bas`、`legacy/idea-tool.bas` 與 `data/` 六份範本複核正式 Excel：功能資料與主要版面沒有發現新的阻擋缺陷，但逐格外觀尚不能宣稱完全相同
（WorkingPaper 欄位資訊頁未重現 legacy 的藍底紅字與黃色表頭、完整性差異提示未套 Calibri 12 粗體紅字等）。原本由 IDEA `ExportDatabase` 產生的動態頁沒有舊版實際輸出檔可作精確樣式基準，
若要求逐格一致須另立修正計畫與樣式 oracle；這項外觀技術債不回溯否定本次功能人工驗收。

## 2026-08-01 — 尾段清理與需求銷帳

五個 deterministic demo actions 現在只以條件編譯納入 Debug 與不可發布的 AgentGuiTest；Release composition 不再註冊 `project.loadDemo` 或 `demo.*`。
日期維度新增持久化的 `calendarImported` 成功 marker，合法零筆假日或補班檔與從未匯入不再混為一談。流程總覽與預篩選 N/A 文案改成查核員語言。

本輪同時正式銷帳五項既有裁決：案件根維持 `%USERPROFILE%\JET`；KCT H 範例因鍵盤、觸控與螢幕閱讀器可及性而常駐顯示；Cr not B 確認為整張傳票完全沒有 B 類貸方；demo 只從 Release composition 移除；TB 期初／期末鍵值 join 需求取消，外部先合併成寬表仍是唯一權威流程。SQL Server 真實身分驗證、serverOnly 刪除、noAccess metadata 隱藏與 SQL Server 2022 全入口硬閘則只記入企業線上輪，不在本輪推測實作。

驗證全部通過，最終獨立複審無剩餘 P0–P2。

## 2026-08-01 — INF 抽樣穩定雜湊與統計檢核

新建案件的 INF 抽樣以 `sampleSeedVersion: 2` 選用 AuditCore 定義的三輪 keyed Feistel PRF；三種資料庫都以 signed BIGINT 精確整數運算渲染同一排序鍵，正式樣本數仍是 59。
100,000 列、32 桶的固定合理性檢查得到 χ² 31.54368（上限 60）。版本 marker 只新增在 `project.json`；無 marker 的既有案件永遠走原本線性排序，連 seed 都沒有的更舊案件仍回退 48271，
因此既有案件不重算、不改抽。marker 損壞明確回 `file_read_error`，不靜默生成新 seed。

## 2026-08-01 — 匯入與篩選保存的整批原子性

多來源 GL 與 TB 匯入改由單一 action 帶一到多個來源，三種資料庫各以同一交易寫入來源 metadata、staging、欄位定義與下游失效狀態；任一後檔失敗都回復整批，並保留呼叫前資料。
`filter.commit` 的情境 definitions 與命中 hits 也收斂到單一交易，失敗或取消會保留前一個完整 revision。沒有新增或改名 action，也沒有 schema 變更。

## 2026-07-31 — 完整性後端硬閘

完整性 part(a) 控制總數與 part(b) 科目差異由 AuditCore 形成單一適格裁定，`validate.run` 與持久化 summary 帶回 `eligibility`，前端不重算第二套判定。
沒有目前世代的適格 validation run 時，`prescreen.run`、`filter.commit`、Pre-screening Report、Criteria Selection Report 與 Working Paper 都在任何 SQL、保存或產檔前回
`completeness_prerequisite_failed`；Validation 三檔與 Account Mapping 仍保留為診斷與修復輸出，`filter.preview` 與唯讀查詢也不受擋。
這次只增加 wire 與 `summary_json` 的衍生欄位與錯誤碼，未改實體 schema、完整性算法或 validation logic version。

## 2026-07-31 — 流程總覽 BI 與正式發布人工驗收結案

S1–S3 流程總覽 BI、S6 多倍率 containment、S7 本地 Noto／ECharts 與 S5 正式發布封裝均完成。使用者完成一場制人工驗收，並明示除所附操作紀錄外其餘項目全部通過；
因此大案、六份 Microsoft Excel 報表、Working Paper、前端整合、離線字型／圖表及 75%–200% 縮放矩陣均已驗收。封裝驗證程式與各已執行階段收據的結案前版本由 Git commit `bf8aeea1` 保存。

附件中的執行摘要是同一場使用者操作的觀察值，不是受控 benchmark、效能 SLA，也不能單獨證明跨 provider 的逐列、工作簿內容或 hash 完全相等；三組都成功產生 14 張工作表的 Working Paper。

本計畫的長期決策收斂如下：

- D1／D2：顯示用累積值、彙總與 ECDF 由後端在既有 run 計算並隨 `summary_json` 回放，前端只鏡射；沒有新增設計稿提出的三個 `query.*` action。
- D3 已由 D8 取代：三個 BI 圖表使用本地 ECharts 5.5.0 SVG renderer，Noto Sans TC／Noto Serif TC 與授權檔隨程式發布，不依賴外網或系統安裝字型。
- D4／D7：500 萬列 DuckDB 的完整 `prescreen.run` 中位數由 1,336.8548 ms 增至 2,937.5140 ms，增幅 119.7332%，已超過百分比門檻；因此不補跑 2,000 萬列 benchmark，S2b 永久採 13 條規則的全期單維長條。
- D5 保持「不適用」與有效 0／零母體分離；D6 將跨年月份鍵裁定為 ISO year-month，但因 D4／D7 降級，現行 wire 不需要月份軸。S3 固定 15 個零元／非零 1–2–5 級距，ECDF 只以非零元分錄為分母並保留 null 斷線。
- S4 多重命中 UpSet 已依 2026-07-30 使用者裁決正式移出範圍；未來若需求再起，須另立新計畫，不沿用本計畫的 run 時落地路線。

「複製紀錄」的現行 TSV 契約只有時間、層級與訊息，本次由使用者手動補上專案 A／B 與 SQLite／DuckDB。這不影響本場通過；是否內建案件／provider 欄位已列為後續改善需求，須另案定義格式。

## 2026-07-30 — Claude Design 前端整合（結構與視覺）

設計交付進入 repository（`docs/design_handoff_jet_frontend/`）後執行整合，只改 runtime 前端，未新增、改名或改變任何 action 語意：補齊設計 tokens 為 CSS 變數；
案件資訊列改為五欄並把「進度」正名為「目前步驟」；流程總覽加入執行識別基準行、母體概況四欄 KPI、空狀態與唯讀聲明；匯出成功後在步驟內顯示完成摘要。
刻意未採用設計原型的三處結構：不以原型的六步驟陣列取代 `Store.STEPS`、不新增第六個「完成」步驟、不把逐項母體完整性核對複製進總覽，這三條邊界由 `DesignIntegrationFrontendTests` 把關。

## 2026-07-30 — 文件收斂與 Claude Code 交接

現行文件重新收斂為使用者驗收、開發現況、權威規格與少量可重現 evidence；`windows-handoff.md` 改為不需資訊背景也能照做的三項人工驗收清單。
重新核對 2026-07-21 分階段主計畫，16 個產品與自動化階段都有現行程式、代表性測試或收據；唯一未完成的是需要使用者原始大案與 Microsoft Excel 的最終人工關卡。

## 2026-07-28 — 大案 Excel 與 Working Paper 驗收修補

使用者以千萬列等級 GL、數百列 TB 的實際案件驗收時，發現三個阻擋問題：報表欄位會換行、裁切或顯示 `###`；Working Paper 在 Step 3 完成後長時間沒有進度；訊息紀錄缺少一致的耗時與最後進度。
修補後六份報表用完整資料計算欄寬，Working Paper Step 4 改為單次串流查詢並先顯示進度，訊息面板新增「複製紀錄」。後續已於 2026-07-31 的人工場銷帳。

## 2026-07-28 — 六報表與正式候選整合

Validation Report、Account Mapping、INF Report、Pre-screening Report、Criteria Selection Report 與 Working Paper 串成同一條正式案件流程，報表都直接填入固定範本副本。
在這次整合前的累積變更曾依使用者指示完成一次版本控制封板與推送；其後的變更已由使用者在 commit `bf8aeea1` 推送至 `origin/main`，agent 未自行發動版本控制。

## 2026-07-26 至 2026-07-27 — Working Paper 大型輸出重構

Working Paper 改為直接在範本副本上以 OpenXML 串流寫入，大型資料只保留目前列與磁碟暫存。Step 4-1 的欄位、資料型別、排序及標記語意依 Legacy 結果凍結；
固定 1,048,571 列的 DuckDB 基準中，新的輸出路徑相較凍結舊路徑超過五倍門檻，且輸出語意指紋保持一致。

## 2026-07-25 至 2026-07-26 — 六份報表直接填範本

六份報表逐步改為直接填固定 Excel 範本。報表作者只負責版面與寫檔；要輸出哪些工作表、明細及審計文字，由 AuditCore 的 finalized plan 決定。
Validation 與 Pre-screening 對超大量異常只保留摘要；Field Info 統一使用同一份 GL 與 TB 欄位定義。

## 2026-07-21 至 2026-07-22 — 本機優先、專案根與 GUI 自動化

正式本機專案根切換為 `%USERPROFILE%\JET`。專案選擇畫面先顯示 SQLite 與 DuckDB 本機案件，只有使用者主動同步時才查 SQL Server。
同一時期建立不可發布的 `AgentGuiTest` 組態及受控 GUI harness；這套工具不能取代使用者在正式 Release、Excel 或企業環境中的人工驗收。

## 2026-07-17 至 2026-07-19 — 審計業務核心集中化

`JetAuditProgram` 成為 AuditCore 唯一 facade，各流程收斂到 typed `Plan → ExecuteAsync → Finalize → Explain` 生命週期。
Application 保留流程編排，Infrastructure 執行資料庫與檔案 I/O，Domain 保持純規則；審計規則、失效政策、報表工作表與文字決策不再散落在前端或個別 handler。

## 2026-07-10 至 2026-07-14 — 本機案件與 Excel 報表基礎

完成本機案件的建立、重開、刪除、鎖定、原子儲存與六份正式報表的第一版。2026-07-12 的 Windows／Excel 人工場曾完成當時的本地主線驗收；
後續重構幅度較大，新的修補候選已於 2026-07-31 重新通過人工驗收。

## 2026-07-03 至 2026-07-08 — 三種資料庫與 SQL Server 控制面

SQLite、DuckDB 與 SQL Server 共用同一套 repository 契約。SQL Server 改為單一 `JET` 資料庫、每案一個 schema，並加入專案登錄、租約鎖、schema migration、容量資訊及控制面 audit log。
產品開發以本機 SQLite 與 DuckDB 為優先；SQL Server 的公司帳號、多人環境、授權與管理員操作仍延後，範圍見 `sqlserver-online-handoff-deferred.md`。

## 2026-06-20 至 2026-06-29 — 核心流程、篩選與報表起點

建立 GL 與 TB 匯入、欄位配對、資料驗證、預篩選、進階條件篩選、分頁明細、篩選情境、傳票／分錄標記矩陣及第一版 Working Paper writer。
進階篩選介面經多輪人工回饋後，收斂為單一條件建構器與清楚的 AND 與 OR 層級；規則名稱改以具體中文名稱與穩定英文識別碼記錄。

---

## 紀錄粒度分界：2026-06-29 之前

上面到「2026-06-20 至 2026-06-29」為止的條目，來自後期專案的開發日誌。該日誌只保留會影響產品理解的
重要里程碑，因此 2026-07-08 以前的內容是事後補寫的區間摘要，不是當時的逐日紀錄。

下面的條目來自早期專案的開發日誌，是 2026-06-04 到 2026-06-23 的逐日紀錄。兩段在 2026-06-20 到 2026-06-23 這四天重疊，沒有矛盾。

---

## 2026-06-23 — 空值測試「日期區間外」改以核准日判定(對齊舊工具)＋ 控制總數/空欄防呆稽核修正

承 2026-06-22 對照舊 IDEA 工具的端到端稽核所發現的三件事,本輪修正並以同一份指令重跑稽核驗證:

- **A 完整性 part(a) 控制總數失效範圍過廣(真實隱患,已修)**:TB 投影、科目配對、行事曆或授權清單匯入會連帶清掉只與 GL 有關的 `gl_control_total`,使「先 commit GL、後 commit TB」這個 GUI 最自然順序下,控制總數核對形同沒跑。修法:`gl_control_total` 移出共用 reset,只由 GL 投影 upsert。
- **B 必填文字欄整欄空白防呆(已加)**:真實案件的 GL 有兩個摘要欄(一空一有值),配到空的會讓 blank_description 全列誤命中。`mapping.commit.gl` 投影提交前偵測必填文字欄整欄空白,回非阻斷 `warnings`,前端提交成功後以警示色就近顯示。
- **空值測試第四旗標「日期區間外」改以核准日判定(語意決策,使用者裁定)**:原以**過帳日**(post_date)判定,舊工具以**核准日**(approval_date)判定,兩者結果不同。使用者裁定改用核准日對齊舊工具。核准日(docDate)未配對時 NULL 不命中;顯示的日期欄維持 post_date,前端分類標籤改「核准日不在期間」。
- **INF 回放最近 run 同-tick 平手(順手強化)**:`InfSamplePageSql` 改 `MAX(rr.run_id)`,消除 SQL Server 多列子查詢報錯。
- **驗證**:全套件通過。重跑稽核後四個驗證維度與舊工具一致,核准日離期也與舊工具對齊。**保留的裁決點**:核准日離期由 null_records 第四旗標承接後,與預篩選「期末後核准」(門檻為期末財報準備日、屬 row-tag)語意/門檻仍不同,非等價;若需更嚴謹另議。

## 2026-06-22 — 進階篩選 UX 三修:必填提示上主畫面、金額欄具名、條件依審計意圖分組

使用者回報進階條件篩選的三個體驗問題(定調為 UX 非程式漏洞):必填提示改為按〔預覽〕或〔保存〕當下就近標紅框與紅字並聚焦第一個問題欄位,做成 `ui-core` 全域 helper;
「|金額|」改成具名「金額(絕對值)」;快速加入改按審計意圖分四組。型別中文名守 `jet-guide.md` §4 命名登錄表,只把 `numRange` 由「數值區間」正名為「金額區間」。
wire 契約不動;實際畫面行為待使用者實機驗收。

## 2026-06-22 — 完整性測試誤判修復:GL 退化母體投影守門

一家公司的真實案件(SQLite)完整性測試顯示數百個科目不符,但 GL 母體理應與 TB 對得起來。根因是配對陷阱而非 JET 計算 bug:GL 借/貸金額欄誤配到「本幣借方**總**金額」/「本幣貸方**總**金額」——這是**傳票總額**,同一張傳票每列都填相同的總借＝總貸,投影後整張 GL 金額全為 0。改配**列層**的「本幣借方金額」/「本幣貸方金額」後,每科目與 TB 精準對上(例如收入科目的 GL 金額與 TB 完全相同),與事務所 ValidationReport「差異數均為 0」一致。

- **修法(穩健解,使用者裁定)**:在 GL 投影提交前加「退化母體守門」——母體非空、無列級錯誤,但借貸總額皆為 0 時,整批 rollback 並回新錯誤碼 `gl_amounts_all_zero`(訊息指出「疑似配到傳票總額,請改配列層借/貸金額欄」),不讓壞母體落地。判定與訊息單一事實於 Domain `GlProjectionGuard`。空母體不在守門範圍。
- **驗證**:全套件 947 項通過。**這個案件既有的專案**需回「欄位配對」把 GL 借/貸改配列層金額欄、重新提交即通過(守門此後會擋住總額欄誤配)。

## 2026-06-22 — 修復 WebView2 檔案對話框 reentrancy 崩潰(host 端)

F5 偵錯時「選單一檔案」後整個 app 閃退。根因是三個 host 對話框方法在 WebView2 的 `WebMessageReceived` 事件堆疊內同步呼叫 `ShowDialog`,WebView2 不支援在事件處理常式內同步開 modal UI。
改以 `BeginInvoke` 把對話框延到事件處理常式返回後執行。不碰任何 action 契約;全套件 938 項通過,反覆操作檔案對話框不再閃退待使用者實機驗收。

## 2026-06-22 — 文件碎片整理:散落規則回流、暫存清除、設計快照收斂

盤點前幾個 session 的 agent 殘留,把仍有效的規則搬回權威出處(工程品味準則、API 一律查官方文件、未經使用者 GUI 驗收不得標「已驗收」、SDD 過程不 commit 等),
清掉未進版控的 `.git/sdd/` 與 `.superpowers/` 暫存;`workpaper-synthesis.md` 含真實客戶名且其方法學已被吸收,故不另存入庫。刪除 16 份已落地的設計快照,保留 7 份仍待 GUI 驗收的。純文件變更。

## 2026-06-22 — 中止的測試 coverage 強化任務收尾

Visual Studio GitHub Copilot 的 Test 任務因額度中止,留下大量未收尾的測試與測試專用 production seam。保留有效覆蓋,移除自驗的 Moq 測試、違反專案邊界的 WinForms 自動化與永久 `Skip`。
業務邏輯與 wire contract 未變;全套件 938 通過,production line coverage 為 95.29%,未為追求 100% 而自動化 WinForms/WebView2。

## 2026-06-22 — 案件名稱資料夾命名 + 套用測試案件不跳步 + 授權編製人員預覽

使用者實機操作後三項收尾修改:`project.create` 新增選填 `caseName` 作為 `projectId` 與資料夾名(未提供回退 32-hex GUID,UI 表單強制必填;`ProjectNameRules` 兼 path-traversal 守衛,同名資料夾即擋);
「套用測試案件(建立)」套用後停在建立案件;`query.dataPreview` 新增 `authorizedPreparers` 資料集。全套件 750 通過,不跳步屬 GUI 行為待使用者驗收。

## 2026-06-22 — 安全相依升級:覆蓋 SQLitePCLRaw 至 3.0.3 修補 CVE-2025-6965(SQLite 記憶體損毀)

NuGet 稽核報 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 具 High 弱點([CVE-2025-6965 / GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q))。JET 實際暴露面低,但審計工具不宜留 High。
因最新 `Microsoft.Data.Sqlite` 10.0.9 仍鎖 2.1.11,改在 `JET.csproj` 顯式釘 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3,原生套件改為 `SourceGear.sqlite3 3.50.4.5`,被點名的有漏洞套件完全離開相依圖。
`dotnet list --vulnerable` 零弱點,全套件 715 通過。

## 2026-06-21 — 測試案件資料擴充 + 配對介面優化(demo 規則 oracle:7000 傳票/150 科目 baseline+seed、已提交配對二維預覽、介面命名白話化)

使用者實機操作後提的三項優化:切換鈕兩名稱改為「簡易清單/對照表格」;已提交配對摘要改為唯讀二維對照表;demo 測試案件重設計為「規模真實、每條規則有已知確定命中數」的 oracle(7,000 張傳票、150 科目,baseline 對每條規則貢獻 0 命中、每個 seed 群組貢獻一個已知數)。

- **C 決策(互動確認)**:GL「6000–8000 筆」= 傳票張數(取 7,000 固定值利於 oracle);單一大資料全測試套件共用;效能緩解採「demo 檔案寫出 process 靜態 `Lazy` 記憶化」而非另寫 SAX 寫出器。
- 修掉既有 fixture 缺陷(整數金額種子打不中 `trailingZeros`、`non_authorized_preparer` 從未觸發),並照出既有議題:完整管線最後 commit TB 後 part(a) 控制總數恆 `na`,是否修屬獨立決策。
- 全套件 715 通過;**demo 全去機敏**(公司名固定虛構常數「範例製造股份有限公司」+ 防護測試,人名/摘要皆虛構)。待使用者 GUI 人工驗收。

## 2026-06-21 — 子專案 E1 匯出底稿 writer(OpenXML SAX 串流寫 15 條件工作表 + 三類新查詢 + GlCanonicalNames + export.workpaperStream/host.selectSavePath)

把 `export.*` 從委派前端的 stub 換成真正的後端串流寫檔,用 OpenXML SAX 寫出事務所「JE Testing Tool」樣本版面的 `.xlsx` 工作底稿。E1 不做欄位配對匯入 round-trip(延後 E2,使用者裁定)、不重算任何規則語意(writer 純讀)。

- **E 前自我審查:cell 級對碼兩份 WorkingPaper 樣本**:實機重解兩家公司的樣本(一份為主、一份交叉核對)全 15 表,逐 cell 對碼確認每表的 JET 資料來源皆對得上現有查詢。
- writer 是 deep module,資料表沿用單一 `EmitTableSheet` 原語;條件表 step1-3-1 只在 diff≠0 時才出。串流不全載入:百萬列等級的傳票/明細逐頁流過、記憶體有界。**ClosedXML 僅限 dev fixture `DemoWorkbookWriter`,不得作底稿寫出器**。
- **step2 借貸兩欄(使用者裁定)**:step2 直接寫兩欄(E=借方、F=貸方),不寫借貸代號+單欄。
- 新增 `GlCanonicalNames` 為正準中文名單一事實來源;封面公司名稱取專案 metadata 的 EntityName;手填欄寫空白骨架供查核員填。
- 驗證:全套 700/700 通過;Excel 逐表目視、百萬列等級的大母體匯出不爆記憶體等 GUI 與端到端互動交 Windows 端人工。

## 2026-06-21 — 子專案 D2 多情境逐列 tag 矩陣(三 query action + idx_result_filter_run_entry + 共用 materialize 服務 + 前端矩陣預覽)

把已落地的篩選命中(`result_filter_run`)即時組成 step4(傳票層命中矩陣)與 step4-1(行層逐行 tag)所需資料。命中已落地,矩陣即時 pivot,不另存矩陣表;
新增 `query.tagMatrixScenarios`、`query.tagMatrixVoucherPage`、`query.tagMatrixRowPage`,每頁兩段查詢避開方言聚合;新增輔助索引不升 schema 版本;惰性 materialize 提取為共用服務 `FilterRunMaterializeService`。
全套 646/646 通過;GUI 互動交 Windows 端人工。

## 2026-06-21 — 子專案 C 補遺(補齊 C9 低頻科目列述詞 R12 + 自訂科目張數條件)

D2 開工前的自我審查發現:子專案 C 主體只補齊了**編製人員維度**的高風險條件(C5 非授權編製人員、C6 低頻編製者),但事務所方法學 step3 的 **C9「較少使用之科目」(科目張數 ≤ 11)是科目維度、被漏掉**——實機重解兩份 WorkingPaper 樣本,其中一家公司的樣本 C9 命中數十張傳票,確屬真實在用的高風險條件。
比照 C6 做雙軌:固定 RowTag 規則 `low_frequency_account`(R12)取 Domain 常數 `AccountFrequency.DefaultMaxEntries=11`(方法學:全年 < 12 筆),進階篩選新增 `customAccountEntryCount`。與 `rare_accounts`(R6)彙總並存不取代;無新 schema。全套 628/628 通過。

## 2026-06-20 — 子專案 C 編製人員升級(授權清單匯入 + 非授權編製人員 R10 + 低頻編製者 R11 + 自訂編製人員張數條件)

補上 C5「非授權編製人員」與 C6「低頻編製者」及授權清單匯入。C5 雙重閘控:`NOT IN` 對空集合會反轉成全部命中,因此名單未匯入時預篩選回 `status="na"`,進階篩選回 `invalid_scenario`,述詞另以 `EXISTS` 自保。
C6 雙軌:固定規則 `low_frequency_preparer` 取 `PreparerFrequency.DefaultMaxEntries=11`,進階篩選新增 `customPreparerEntryCount`。授權清單 replace-only 投影,不寫 `import_batch`、不升 schema 版本。全套 618/618 通過。

## 2026-06-20 — 子專案 D1 全量明細基礎設施(五個 keyset 分頁 action + result_filter_run 落地 + INF 明細回取 + 前端載入更多)

補上五個 keyset 游標分頁查詢:母體達百萬列等級,OFFSET 分頁越後越慢,改用 keyset 並把游標述詞展開成布林式以相容 SQL Server。`result_filter_run` 只存 `(scenario_position, entry_id)` 行層參照,
於 `filter.commit` 落地、舊情境讀取時惰性補算,並納入結果失效集。前端五個明細檢視加「載入更多」。全套 588/588 通過;GUI 互動交 Windows 端人工。

## 2026-06-20 — 子專案 B 資料/schema 擴充落地(傳票日期欄 + 回溯過帳規則 + 完整性 part(a) + Not-in-TB)

新增語意明確的選填欄 `voucher_date`,不把核准日重新詮釋成傳票日;回溯偵測做成單一 RowTag 預篩選規則 `backdated_posting`(R9),命中 `voucher_date IS NOT NULL AND post_date < voucher_date`;
完整性 part(a) 控制總數在投影時累計並寫入 `gl_control_total`;「GL 有、TB 無」的科目加 `notInTb` 記號,字面 `Not in TB` 的輸出屬匯出 writer。v4→v5 遷移冪等。全套 559/559 通過。

## 2026-06-20 — 子專案 A 規則校正落地(高風險情境上限 5→10 + 方法學對齊)

`filter.commit` 的情境上限 `MaxScenarios` 由 5 改為 10,因事務所底稿的高風險條件需 C1–C10 共 10 條。其餘校正:連續零尾數改為固定預設 6 位、取代原本的動態門檻;
可疑關鍵字清單補上「帳外」;授權閘語意確認為「自訂尾數位數 AND 數值區間」的進階篩選情境組合。全套 551/551 通過。

## 2026-06-19 — 行事曆檔案匯入(假日／補班）+ 科目配對英文標頭

新增 `import.holiday.fromFile` 與 `import.makeupDay.fromFile`,解析事務所行事曆 schema v4(假日檔只收 IS_Holiday=Y、跨年度全收,標頭在第 2 列);前端「日期維度」卡加兩顆上傳按鈕,
匯入任一側不把另一側歸零。科目配對檔的標頭驗證改吃英文欄名。全套 545/545 通過;前端上傳卡待 Windows 端人工目視。

## 2026-06-19 — 重新匯入完成的卡片回饋

重新匯入完成時,完成訊號原本落在預設收合的訊息窄欄,卡片看起來沒變。改為卡片狀態列短暫轉成成功態「剛剛重新匯入·HH:MM:SS」,約四到五秒後淡回常態;加入來源顯示「已加入來源·HH:MM:SS」。
純前端、零契約變更;全套 525/525 通過,GUI 目視待人工。

## 2026-06-19 — 欄位配對雙介面(經典三欄表／二維表)+ 必填鐵軌

上線後收到兩點回饋:有人還是偏好原本的三欄表,二維表的必填提示也不夠醒目、版面想更大。改為兩種介面都留,預設經典、本次 app session 記住(`mappingUiMode`);
兩種介面共用同一份草稿,切換無損;二維表右側加必填欄位鐵軌,全部綠了才啟用〔確認配對〕;版面加寬約 1200px。契約零變更;全套 525/525 通過,GUI 待人工。

## 2026-06-19 — 欄位配對改為內嵌標頭下拉的二維配對表

草稿介面改成二維配對表:來源欄為直行、標頭帶下拉指派 JET 邏輯欄位、標頭下方顯示該批次前 10 列原貌。模式維持為獨立選擇器;資料列用 `query.dataPreview` 的 staging 資料集,
因為它反映多來源合併後的原貌;前端正規狀態仍是「欄位 → 來源欄」的 `draft`,契約零變更。另修正借方代碼字面值會被同名來源欄指派誤清的問題。全套 525/525 通過。

## 2026-06-18 — 強化 harness 式 TDD 循環(突變測試 + 回應契約鎖 + 誠實跳過)

補上三道把關:Stryker.NET 突變測試(限定 Domain 與 Application,階段性執行,對 `MoneyScaling` 算出 92.86%);`ActionContractTests` 對五個 action 鎖住回應的欄位集合與型別,
**不引入 Verify 快照工具**;約 26 個 SQL Server 閘控測試改用自訂 `[SqlServerFact]` 顯示略過,而非 early-return 當作通過,不採 `Xunit.SkippableFact` 套件。全套 525/525 通過。

## 2026-06-18 — 資料驗證與測試:檢視詳情(明細下鑽)+ 匯入橫幅文案修正

每個會示警、命中或無法執行的項目都能展開,看到後端用同一套 SQL 算出的有上限明細(最多 50 筆)。**不新增明細查詢 action**:預篩選命中走既有 `filter.preview`
(以 8 個預篩選鍵的守門測試鎖定預覽筆數等於徽章筆數),借貸不平與空值明細內嵌進 `validate.run` 回應。「重新匯入」與「加入來源」橫幅改成以使用者當下動作開頭、講清後果。全套 519/519 通過。

## 2026-06-15 — Agent 開發迴圈 skill + 診斷日誌檔案 sink（agent 執行時偵錯橋接）

開發主環境從 Visual Studio + Copilot 轉到 VSCode + Claude Code 後,新增 `.claude/skills/jet-dev-loop` skill,以及 dev-only 的 `NdjsonFileLoggerProvider`,
把每筆診斷日誌以 NDJSON 寫到 `%LOCALAPPDATA%\JET\logs\jet-dev-<啟動時間戳>.ndjson`,讓 agent 直接讀執行時日誌。Release 不產生檔案。全套 502/502 通過。

## 2026-06-15 — 診斷日誌 instrumentation rollout（稽核關鍵 repo 全接、雙 provider）

把 `DiagnosticDb` 機制擴展到 11 個稽核關鍵 repository(雙 provider);大批迴圈內的逐列執行一律不逐筆記事件,改以階段邊界一筆 milestone;`ProviderLoggingParityTests` 證明兩 provider 事件序列相等。
DEV 面板 provider 標籤改為依 `databaseProvider` 動態顯示。全套 495/495 通過。

## 2026-06-14 — 診斷日誌（第三層系統日誌、dev-only）

新增第三層日誌——診斷日誌,完整記錄 action、SQL 與參數值、transaction、exception 與大檔 milestone,獨立於審計留痕的 result_* 表與 UX 訊息。自寫有界 ring buffer(預設 10,000);
correlation 與 transaction 由 scope 自動帶入;SQL 逐呼叫點接入。本輪接入範圍已與使用者確認:機制加稽核關鍵 repo,本輪先做匯入路徑。全套 480/480 通過。

## 2026-06-14 — 開發工具：訊息記錄完整匯出（dev.log.export，隱藏 dev-only）

使用者 GUI 實測確認 SqlBulkCopy 改造後匯入逾時已解決（同日截圖:「GL 匯入完成…〔筆數〕列、耗時 **〔秒數〕秒**」,對照稍早 14:46 的「執行逾時」；截圖文字裡的列數與耗時以〔筆數〕與〔秒數〕代替,不寫進文件）。
為讓後續測試能把完整 log 字串交給 AI 做 TDD 驗證,新增僅 Debug 組建註冊的 `dev.log.export`,回傳與 `log.recent` 同形狀但不封頂。全套 471/471 通過。

## 2026-06-14 — SQL Server 匯入暫存寫入改 SqlBulkCopy 串流（效能待辦結清）

`SqlServerImportRepository` 的 staging 寫入由逐列 prepared insert 改為 `SqlBulkCopy` 串流,以有界 Channel 橋接非同步來源,producer 必須 `Task.Run` 在獨立執行緒跑。
**效能實測與「瓶頸上移」結論(百萬列等級的真實 PBC、LocalDB)**:匯入由 row-by-row 的數分鐘縮短到 SqlBulkCopy 串流的 **不到一分鐘**(約五倍加速);主導成本上移到 xlsx 解析。原訂驗收基準「比值 ≤ 1.5」結構上不可達,規模測試改為只斷言列數正確、耗時僅記錄。
使用者回報的「匯入 GL 失敗:執行逾時」本機無法重現,研判是修 overlap 前的版本;另對百萬列 `DELETE` 設 `CommandTimeout = 0`。全套 468/468 通過。

## 2026-06-14 — 專案選擇前端重做、專案刪除與資料庫清除、訊息面板兩視圖共用

專案選擇畫面改為整列可點即開啟的單一列表;新增 `project.delete`(先刪資料庫再刪資料夾,資料庫刪除失敗則保留資料夾);`ProjectDocument` 加可空 `LastOpenedUtc`;
訊息面板移出 app-body,兩個視圖共用。使用者回報的「新建 sqlServer 專案無法操作」根因為 `JET_SQLSERVER_CONNECTION` 未設定,非前端迴歸。全套 459 通過,使用者截圖驗收通過。

## 2026-06-14 — SQL Server provider 全 repo 移植完成(端到端雙 provider 等價)

為所有剩餘每專案 repository 補齊 SQL Server 實作與路由,使完整工作流程在兩個 provider 下端到端等價。每專案一個資料庫(SQL Server 為 `JET_{projectId}`),連線取自 `JET_SQLSERVER_CONNECTION`。
方言縫隙由 `GlRulePredicates(ISqlDialect)` 吸收;關鍵陷阱是 SQL Server 無 MARS。待辦:匯入暫存寫入仍 row-by-row(百萬列等級需數分鐘)。全套測試通過、`JET_` 暫時庫零殘留。

## 2026-06-13 — 合併 IDEA/VBA 時期 JET 筆記(文件,無行為改動)

把上一代 JET 的開發筆記合併進現行文件:領域缺口補進 `docs/jet-guide.md`,歷史實作清洗後歸檔到 `legacy/jet-legacy-notes.md`。受查者相關的識別資訊在歷史歸檔中一律以佔位符代替,不保留原值。純文件變更。

## 2026-06-12 — 使用者驗收回饋:訊息面板收合與持久化、金額保真差分驗算

同日 PBC 匯入強健化落地後,使用者實測百萬列等級的匯入成功並提出三項回饋,當日完成:「狀態與訊息」預設收合為 44px 窄欄並以 `log.append`／`log.recent` 持久化到 `app_message_log`(每專案留最近 500 則);
匯入與標準化完成訊息附耗時秒數;**金額保真差分驗算(使用者要求嚴謹檢查標準化是否引入誤差)**:真實 PBC 閘控測試新增逐列差分 oracle。實跑結果:**百萬列等級的母體逐列位元相等、量化(>4 位小數)事件 0 件**——本母體標準化誤差恰為零,非僅「在容許範圍內」。過程中差分先揭露一個語意要點:衍生借/貸欄依 guide §2.1 由淨額重分類,欄總額與來源欄總額本來就不同——本母體有上萬筆負值列(紅字沖銷金額達數億元),staging 完整保留原始借貸欄字串,衍生欄只是規則計算的標準形,無資料損失。
全套 435/435 通過。

## 2026-06-12 — PBC 匯入強健化:大型 xlsx 串流讀取、欄位集合收斂、匯入進度推播

以兩份真實受查者提供資料(一份百 MB 等級、兩張工作表、合計百萬列等級的 GL 工作簿,與一份會計格式的 TB CSV)為壓力對象的一輪強健化:

- xlsx 讀取改 OpenXML SAX 串流,原整本載入必然 OOM;百 MB 等級的真實檔 inspect 實測 **不到一秒**。另修正 SDK 以路徑開檔失敗會殘留控制代碼鎖住來源檔。
- 批次有效欄位收斂:「上半年」標頭列 T 欄空白(正規化成 `COL_20`)且整欄數十萬列無資料、「下半年」27 欄連續——原本會報 `column_mismatch`。新規則:具名標頭一律屬批次欄位,`COL_n` 佔位欄僅在串流中觀察到資料時成立。
- 會計格式零:` - ` 視為 0,全形或多字元組合維持拒絕。SQLite 規模調校後實測 43,821 列/秒,不做寫入迴圈微優化。
- `import.inspectFile` 增 `rowCountEstimate`;匯入進度推播(host→web 事件管道首例,使用者選定本輪一併實作)每 20,000 列發布一次 `import.progress`。

驗證:Debug 428/428 通過。真實 PBC 閘控測試(`JET_PBC_DIR` 環境變數,`PbcRealDataSmokeTests`)於本機實跑全程:**inspect 不到一秒;GL 上半年匯入與下半年附加各約二十秒(欄位收斂後批次 27 欄、無 COL_20);百萬列等級的 GL 投影十餘秒;數百列的 TB 含 " - " 零全數投影;進度事件次數與列數相符;全程約一分鐘**。

## 2026-06-11 — 規則命名全面更名、進階篩選補完、provider 縫隙強化

使用者驗收回饋驅動的一輪大改:

- **V/R/A 代號全面退役**:歷史代號跨世代歧義,改為三層命名(wire key、資料表 slug、UI 與工作底稿用中文名),單一事實來源 `Domain/RuleCatalog.cs`。
- schema v2→v3 遷移:已存規則執行結果清除不翻譯,篩選情境逐鍵翻譯保留。
- provider 縫隙強化:抽出最小方言介面 `ISqlDialect` 與 provider 中立的 `GlRulePredicates(ISqlDialect)`。
- 科目配對匯入與未預期借貸組合解鎖。**0 元金額邊界統一裁決:`amount_scaled >= 0` 屬借方側**(與 DrCr 推導一致;guide §5 R3 原為 `> 0`,與 §6.1 不一致,經使用者裁決統一),等價測試以 0 元傳票守門。
- 進階篩選新增科目配對分析、期內/期外、自訂關鍵字、自訂尾數位數(1–12)四型別;欄位配對已提交收合為摘要卡;UI 的「投影」一律改「標準化」。

全套 343/343 通過。

## 2026-06-11 — 使用者資料預覽與開發者工具組建閘控

`dev.db.*` 兩個 action 僅 Debug 組建註冊,`system.ping` 新增 `devToolsEnabled`。新 action `query.dataPreview` 提供業務資料集白名單的有界預覽(預設 50 列、上限 100),絕不回完整母體。
同日依定稿設計完成多來源附加匯入(schema 第 2 版、既有專案的抽樣不變)、匯入前檔案檢視與多檔選取,以及前端匯入精靈與 `app.js` 模組拆分。

## 2026-06-11 — 文件體系整頓

新增 `docs/README.md`,寫作規範核心是人類可讀敘述優先、**禁止自創流水代號**。根目錄 `PLAN.md` 與 `WORKLOG.md` 退役,內容遷移至 `docs/development-status.md` 與本檔。

## 2026-06-11 — 多來源匯入精靈設計定稿

一個 GL/TB 資料集可由多份檔案或一張活頁簿的多個工作表合併而成,分三階段實作;維持「一個資料集一個匯入批次」不變式,`row_number` 改義為批次內單調排序鍵以保住抽樣可重現性。

## 2026-06-11 — Windows 端完整測試與 CSV 標頭讀取修正

使用者在 Windows 執行全套測試,初跑 236/237,唯一失敗揭露 Sep 套件內建標頭模式遇重複欄名直接拋例外。CSV 讀取器改自行消費第一列再交給共用的 `TabularHeaderNormalizer`(commit `36876e4`),修復後 237/237 通過。

## 2026-06-11 — 架構審查與依賴方向整理

進入多來源匯入開發前的完整架構與代碼審查:Domain 純淨、Application 無 SQL、bridge 薄、前端零業務規則。錯誤契約與儲存 JSON 設定搬入 Domain,依賴方向規則
`Bridge/Host → Application → Domain ← Infrastructure` 寫入 `AGENTS.md`;macOS 端無法執行的驗證以 `docs/windows-handoff.md` 任務卡交接。

## 2026-06-11 — 匯入強健化:格式正規化與 CSV/.txt 讀取器

新增 `DateNormalizer`(依序判定 ISO、顯式西元、民國年、Excel 序列值,最後才是寬鬆 fallback;民國年必須排在序列值之前,兩位數年一律拒絕)、`TabularHeaderNormalizer`、`CsvDialectDetector`,
以及 `CsvTableReader` 與確定性 `EncodingDetector`(依序檢查 BOM、嚴格 UTF-8 與 Big5,失敗即報錯不猜)(commit `ea56764`)。金額不處理貨幣符號,審計員會先做初步清理。

## 2026-06-11 — 工作流程介面重整與進度保存

三項使用者需求(同樣在 commit `ea56764`):六個步驟統一頁尾與下一步引導;規則卡以白話標題與一行說明呈現;新增 `project.saveProgress` 與 `host.exitApp`,重開應用程式回到上次所在步驟。

## 2026-06-10 — 版面切換修正、資料庫檢視唯讀化、組態歸屬收斂

全域 `[hidden] { display: none !important; }` 修正兩視圖同時可見;開發者資料庫檢視一律走唯讀連線;`project.json` 新增 `databaseProvider`,provisional action 退役刪除。157 個測試通過。

## 2026-06-10 — 資料驗證與測試 + 進階條件篩選

工作流程合併決議:前端步驟模型由七步收斂為**六步**,「資料驗證」與「預篩選」合併為同一步驟「資料驗證與測試」。V1–V4 與 R1–R8 全部為參數化集合式 SQL,規則述詞只寫一次;
抽樣排序鍵用 `source_row_number`,seed 與樣本落地保證可重現;進階篩選由前端組條件 AST、後端白名單映射與參數綁定,舊系統十二條預篩選條件全部遷移。151 個測試通過。

## 2026-06-10 — 測試案件載入器與儲存 JSON 可讀性

每個步驟一鍵套用 deterministic 測試案件(固定 LCG,GL 2,000 列、TB 100 科目),demo 流程走與使用者上傳完全相同的 file-based 匯入管線;儲存層 JSON 保持中文原文。79 個測試通過。

## 2026-06-10 — 專案持久化、檔案匯入與欄位配對

第一個具備資料庫能力的版本:專案資料夾持久化、`data/JE.xlsx`(9,132 列)/`data/TB.xlsx` 串流匯入、欄位配對與開發者資料庫檢視。實作前發現金額是文字儲存格、借貸欄 cell 稀疏、無項次欄;
金額以 `decimal` 乘 `MoneyScale`(10000)轉 scaled BIGINT,任一列失敗整批 rollback;每專案一個 `jet.db`;重複匯入在同一交易內清除下游。52 個測試通過。

## 2026-06-04 — 初步雛形架構符合性審查

對最初的 scaffold 做文件邊界符合性審查,核心邊界全部守住。當時的發現多數已在 2026-06-10 至 06-11 解決;CancellationToken 未串接當時仍開放,列於 `docs/development-status.md` 技術債;
`JET.Application` 與 WinForms 命名空間衝突接受現狀。

## 2026-06-04 之前 — 架構骨架

初始雛形:薄 `Form1` host → WebView2 bridge(JSON transport)→ 手寫 `ActionDispatcher` → Application handlers → 純 Domain → SQLite Infrastructure;前端三檔結構(`jet-api.js` 唯一 postMessage 出口、`state.js` 輕量 store、`app.js` 渲染)。

# 匯出底稿 writer 設計(子專案 E1)

> **狀態:已實作,待 GUI 驗收。** 程式碼已落地、自動化測試全套本機 700 綠(0 failed / 0 skipped,設 `JET_SQLSERVER_CONNECTION` 時 SQL Server parity 實跑);逐表 Excel 目視對齊樣本、大母體匯出不爆記憶體等 GUI 互動仍待 Windows 端人工驗收(`docs/windows-handoff.md` 任務卡),尚未驗收、尚未提交。「匯出底稿」里程碑最後一棒 **E** 的第一階段 **E1**:用 OpenXML SAX 串流寫出 15(條件)工作表的 `.xlsx` 匯出底稿,含三類參考資料工作表。**欄位配對匯入(round-trip)延後 E2**(使用者裁定)。依賴 A/B/C/C補遺/D1/D2/F 全部完成。
>
> **盤查依據:** `.git/sdd/workpaper-synthesis.md`(15 表結構)、`.git/sdd/bcde-scope-draft.md`(E 範圍);**2026-06-21 E 前自我審查:實機 cell 級重解兩份 WorkingPaper 樣本全 15 表 + 對碼確認每表的 JET 資料來源**(D1 分頁 / D2 矩陣 / F·B 的 store)。

## 背景與動機

事務所「JE Testing Tool」的最終產出是一份 `.xlsx` 工作底稿(WorkingPaper),固定命名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`,含封面 + step1~step5 測試底稿 + 三張「自動化工具」參考資料表。JET 至今 `export.*` 全是 stub(委派前端、無 backend 寫檔)。前面子專案已把每張表所需的查詢/資料齊備:

- step1 完整性 → D1 `completenessDiffPage`;step1-1 借貸不平 → D1 `docBalancePage`;step2 可靠性 → D1 `infSamplePage`。
- step3/4/4-1 高風險矩陣 → D2 `tagMatrixScenarios`/`tagMatrixVoucherPage`/`tagMatrixRowPage`。
- 假期假日 → F 行事曆 store;科目配對(含 Not-in-TB)→ B;欄位配對 → JET 欄位配對設定。

E1 把這些**串流**寫成 `.xlsx`。寫出器走 `DocumentFormat.OpenXml` 的 SAX(`OpenXmlWriter`),不可用 ClosedXML(鐵律:ClosedXML 僅限 dev fixture `DemoWorkbookWriter`)——因真實母體達百萬列(百創 ~140 萬),必須串流、不可全載入記憶體。

## 已驗證的資料地基(E 前自我審查結論)

15 表逐一 cell 級對碼,**全部對得上現有查詢/store**;兩個小缺口需 E1 補:

1. **step1-2「分錄編製人員說明」需全名單**(編製人員 / 傳票數 / 金額彙總),但現行 `creator_summary` 截 **LIMIT 50**(`SqlitePrescreenRunRepository.ReadCreatorsAsync`、`SqlServerPrescreenRunRepository`)→ E1 需一條**不截斷**的編製人員查詢。distinct `created_by` 基數有界(人員 + 自動拋轉傳票類型,實務數十~數百),故回完整清單即可,**不需分頁**。
2. **step2「分錄來源」欄(J,方法學標「若有」)** 與 `infSamplePage` 的 `InfSampleRow` 未含 `source_module` → E1 step2 該欄留空(方法學允許「若有」),**不擴 infSamplePage**(YAGNI;避免為單一欄改動已驗證的 D1 查詢)。

其餘金額一律 `amount_scaled` 整數,顯示值 `(decimal)scaled / moneyScale`(沿用既有換算,純算術非業務邏輯)。

## 設計決策(data-structure first、消除特例)

### 寫出器是 deep module,sheet 是資料不是分支

15 表的形狀分四類:封面/鍵值文字、固定 boilerplate、**資料表(表頭 + 串流資料列)**、條件表/手填骨架。淺薄設計會變成「15 個各寫一遍 OpenXML 管線的函式 + 一個依 sheet 名分岔的 god switch」。

採 Ousterhout deep module:`WorkpaperWriter` 對外只暴露窄介面 `WriteAsync(Stream, WorkpaperContext) → ExportStats`,內部隱藏所有 OpenXML SAX 細節(shared strings inline、styles、merged cells、number formats、串流)。共用低階原語:`WriteRow(cells)`、`WriteHeaderBlock(lines)`、`WriteMergedCell(range)`、style 註冊。

**資料表沿用單一 `EmitTableSheet` 原語**(表頭區塊 + 欄標 + 一個「列來源」委派,委派內部 keyset 逐頁取 repo、yield cell 陣列);step1/step1-2/step2/step3/step4/step4-1/三參考表都走它,不為每表寫一份管線。封面/固定文字/手填骨架是各自的小 emitter(一次性、inline,不硬抽共用)。

**條件表(step1-3-1 差異≠0 才出)= orchestration 層的 guard**(`if (hasNonZeroDiff) emit(...)`),不是 god-function 內的特例分支。這正是「換 data structure 讓特例消失」:sheet 清單由 orchestrator 依資料動態決定要 emit 哪些,emitter 本身無「我是第幾張表」的分支。

### 寫出器是查詢的消費者,不重做任何業務邏輯

`WorkpaperWriter` 是 Infrastructure,注入既有 ProviderRouting 查詢 repo(completenessDiffPage/docBalancePage/infSamplePage/tagMatrix*)+ store(calendar/accountMapping/fieldMapping/全編製人員查詢)。資料表 emitter 對 repo **keyset 逐頁取、逐列寫**(SAX 串流,不全載入)——百萬列傳票/明細逐頁流過寫出器,記憶體有界。scaled→顯示值換算在寫出器內(純算術)。E1 不在寫出器內重算任何規則。

### 正準中文名單一事實來源(Domain)

Field Mapping Info(表13)與(E2)round-trip 都需要 logical key ↔ 正準中文名(`docNum`↔`傳票號碼_JE`、`postDate`↔`總帳日期_JE`…;TB 側 `會計科目編號_TB` 等)的對照。新增 Domain `GlCanonicalNames`(logical key → 正準中文名),**單一資料結構、匯出與日後 round-trip 共用**(消除雙向重複對照)。與既有 `GlFieldWhitelist`(logical key → 實體欄,SQL 識別字安全)分立但概念相鄰。

### step2 金額 = 借/貸兩欄(使用者裁定)

`infSamplePage` 的 `InfSampleRow` 已回 `DebitScaled`/`CreditScaled`,step2 直接寫兩欄(E=借方、F=貸方);不寫借貸代號+單欄。對齊 D1 既有資料形狀,語意不含糊。

## 目標與範圍

### 做(E1)

1. **OpenXML SAX 寫出器核心** `WorkpaperWriter`(deep module + `EmitTableSheet` 原語 + 樣式/合併/數字格式),串流寫 `.xlsx`。
2. **15(條件)工作表**,逐表對齊樣本(來源見下「工作表 → 來源對照」):封面 ×2、step1、step1-1、step1-2、step1-3、step1-3-1(條件)、step2、step3、step4、step4-1、step5、Field Mapping Info、假期假日資訊、科目配對資訊。
3. **手填欄位一律留空**(step1-2 部門/職稱/說明、step1-3 原因/調節/調節後、step1-3-1 前期損益金額、step2 結果 A–G/詳細說明、step4 P–U、step5 內文)。
4. **新後端查詢**:不截斷的全編製人員彙總(step1-2)。
5. **`export.workpaperStream` action**(取代 stub):`{ sheets?, outputPath }`(sheets 省略=全部;outputPath 由 host 存檔對話框提供)→ `{ ok, bytesWritten, sheetStats }`;streaming 寫檔。
6. **host 存檔對話框** `host.selectSavePath`(WinForms `SaveFileDialog`,host I/O;預設檔名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`)。
7. **前端匯出步驟**接線(沿用 export-step.js;選表 + 觸發 + 完成回饋;零商業邏輯)。

### 不做(移交 E2 / YAGNI)

- **欄位配對匯入 round-trip**(讀回 Field Mapping Info 重建配對)→ **E2**。
- **CAATs .docx 產生/打包**:封面只寫**檔名字串** `{客戶}_CAATS_JE_WP_{yyyymmdd}.docx`(外部、查核員自備),不產生 docx。
- step1-3-1 前期損益**自動計算**(B 已裁定手填);step5 內文(手填)。
- 不擴 `infSamplePage`(step2「分錄來源」留空)。
- 不改任何既有規則/查詢的語意;寫出器純讀。
- 不做匯出排程、多檔批次、PDF 等。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

**分層與既有鐵律**
- 分層:`Bridge/Form1 → Application → Domain ← Infrastructure`。正準名表/匯出契約型別在 Domain;寫出器 + provider 查詢在 Infrastructure;`export.workpaperStream` handler 編排在 Application;存檔對話框在 Form1/Bridge(host I/O)。
- 前端零商業邏輯;不在 `jet-api.js` 以外 `postMessage`。
- 寫出器走 `DocumentFormat.OpenXml` SAX(`OpenXmlWriter`);**ClosedXML 僅 dev fixture,不得用於底稿寫出**。
- 串流不全載入:資料表 keyset 逐頁取、逐列寫。
- 契約先行:先改 `docs/action-contract-manifest.md`(`export.workpaperStream` 由 stub→Implemented、新 `host.selectSavePath`、facade);`ActionContractTests` 鎖回應形狀。
- 雙 provider 等價:寫出器讀的查詢走 ProviderRouting;資料來源在 SQLite 與 SQL Server 兩側等價(已由 D1/D2 parity 保證);E1 的全編製人員查詢加 `[SqlServerFact]` parity。
- 不破壞失效不變量;不編輯 designer 檔。
- 測試三層(jet-testing):Domain(正準名表、匯出契約純函式)+ Application 驗收(`export.workpaperStream` 寫出檔可被讀回、sheet 數/表頭/關鍵列正確、條件表存在性、手填欄空)+ Infrastructure(全編製人員查詢 parity)。**寫出器驗證用 OpenXmlSaxTableReader 或 ClosedXML(測試端可)讀回斷言**,不靠目視。
- TDD;**不自行 commit**(tree-diff 隔離);**所有 subagent 一律 `claude-opus-4-8`、effort max,無例外**(implementer/task-reviewer/fix/final)。

**工程品味準則(binding — 本里程碑一貫,且本棒最吃重)**
- **data structure first**:寫任何 `if/else` 前先問「換個 data structure 能不能讓這分支消失」。業務分支(行事曆 vs 科目配對的不同處理、step2 借貸兩欄)保留;補丁分支(第一張表特例、head==null 特例、第一列特例)是設計失敗訊號,先重構。sheet 清單動態化、條件表 orchestration guard 就是消特例範例。
- **no silent assumption / no over-engineering**:每行改動可追溯到 spec 要求;200 行能解的不寫成 1000 行抽象。不為「未來/彈性」加參數或抽象;single-use 就 inline。
- **deep module / 介面誤用困難**:窄介面(`WriteAsync→stats`)藏寬實作;非測試 LOC / 公開介面表面積目標 ≥3:1。不讓 caller 記隱性順序、不讓衝突選項並存、不讓 null 與 empty 語義不同卻都收。
- **DRY 閾值 = 三次**;兩次重複先忍,第三個 caller 出現再抽(避免提前抽象被迫加 if/else)。
- **SRP 配 deep module**:不為 SRP 把一體邏輯切成 5 個淺類;OCP/LSP/ISP/DIP 只在有實際變化壓力時導入(雙 provider 是 spec 明定才導入,其他不類推)。
- **命名精準**(`FetchAllCreatorSummary` 勝 `GetData`);**註解寫「為什麼」**(為何 SAX 不用 ClosedXML、為何全名單不分頁、為何 step2 兩欄)不寫「做什麼」;**函式只做一件事**(拆了要看兩處才懂一件事就別拆)。
- **不順手改鄰近無關碼**:看到不順眼但與 task 無關 → 記 progress ledger 給使用者,不自行改。改動產生的孤兒 import/變數自己清,task 前就存在的死碼不碰。
- reviewer 的 taste 問題比照 Critical/Important/Minor 分級:壞 data structure/過度抽象/orthogonal change = 派 fix;命名/註解可更好 = 記 ledger 等 final triage。

## 工作表 → 來源對照(逐表內容規格)

> 表頭固定文字、欄標、合併儲存格、數字格式逐表對齊樣本(`Example_福懋…WorkingPaper.xlsx` 為主、`佰鴻` 交叉),細節於 plan/impl 落實。以下定來源與自動/手填邊界。

| # | 工作表 | 來源 | 內容規則 |
|---|---|---|---|
| 1 | 資料預先整理之說明 | 專案 metadata | A1 公司名稱、A2 測試資料期間、A5 固定說明、A6 `請詳:{客戶}_CAATS_JE_WP_{yyyymmdd}.docx` |
| 2 | JE WorkingPaper說明 | 固定文字 | A1「說明:」+ B1 boilerplate(合併 B1:O1) |
| 3 | step1 完整性測試 | `completenessDiffPage`(全科目,逐頁) | 表頭 + 第19列欄標(科目編號/名稱/TB變動(A)/GL本期借貸彙總(C)/差異(B)-(A))+ 全科目列 |
| 4 | step1-1 借貸不平測試 | 固定文字 + `docBalancePage` | 結論文字;**有不平傳票才 emit 例外表**(條件) |
| 5 | step1-2 分錄編製人員說明 | **全編製人員查詢(新)** | 第11列欄標;B 編製人員/C 自動人工(空)/D 傳票數/E 金額彙總;F 部門/G 職稱/H 說明留空 |
| 6 | step1-3 完整性差異說明 | `completenessDiffPage` WHERE diff≠0 | 第16列欄標;科目/差異金額自動;E 原因/F 調節/G 調節後留空 |
| 7 | step1-3-1 完整性差異調節 | diff≠0 科目 + 固定說明 | **條件表(diff≠0 才 emit)**;科目自動;前期損益金額手填;A4–A6 固定調節說明文字 |
| 8 | step2 可靠性測試 | `infSamplePage`(抽樣行) | 表頭跨 49–52 列(含合併);A 樣本號(序)/B 傳票號/C 科目編號/D 名稱/**E 借方·F 貸方**/G 總帳日/H 核准日/I 編製人員/J 來源(空)/K 摘要/L 核准人員;M–S 結果A–G、T 說明留空 |
| 9 | step3 高風險條件彙總 | `tagMatrixScenarios` | 固定測試說明 + C1..CN:B 代號/C 條件描述(情境 name)/D 原因(情境 rationale)/E 符合傳票數(voucherHitCount) |
| 10 | step4 符合高風險條件傳票 | `tagMatrixVoucherPage`(逐頁) | 第11列欄標;A 編號/B 傳票號/C 總帳日/D 編製者/E 傳票總額;F..(C1..CN)以 matchedPositions 標 Y;P–U 手填留空 |
| 11 | step4-1 符合高風險條件傳票明細 | `tagMatrixRowPage`(逐頁) | 第5列欄標(傳票號/項次/總帳日/建立/核准人員/科目/金額/摘要)+ 動態 C*_TAG 欄(僅有行層命中的 position;以 matchedPositions 標 Y) |
| 12 | step5 財務報表關帳後調整 | 固定橫幅 | A1 黃色 boilerplate(合併 A1:R1);其餘手填 |
| 13 | 自動化工具-檔案欄位資訊 | 欄位配對設定 + `GlCanonicalNames` | A1 版本標籤;TB 段 + GL 段:配對前(來源欄)/型態(kind→文字/日期/數字型態)/長度/小數(best-effort,缺則空)/配對後(正準名);K_ 衍生旗標列出 |
| 14 | 自動化工具-假期假日資訊 | 行事曆 store | 固定週末表(Mon–Sun,六日=Y)+ 假日表(DATE_OF_HOLIDAY/HOLIDAY_NAME/IS_HOLIDAY)+ 補班段 |
| 15 | 自動化工具-科目配對資訊 | 科目配對 store + B 的 Not-in-TB | GL_NUMBER/GL_NAME/STANDARDIZED_ACCOUNT_NAME;在 GL 不在 TB 者 GL_NAME 寫字面「Not in TB」 |

**step4-1 動態欄位**:只 emit「該客戶有行層命中」的 C*_TAG 欄(由 `tagMatrixScenarios` 的 rowHitCount>0 的 position 決定欄集),對齊樣本;位置→`C{position}_TAG` 標頭。固定模板殘留欄(樣本 Q–Z)不複製。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:`export.workpaperStream` stub→Implemented(形狀)、新 `host.selectSavePath`、facade、ActionContractTests |
| `Domain/GlCanonicalNames.cs`(新) | logical key → 正準中文名(GL+TB) |
| `Domain/WorkpaperContracts.cs`(新) | `WorkpaperContext`(專案 metadata + moneyScale + 期間 + 選表)、`ExportStats`(bytesWritten + sheetStats) |
| `Domain/I*CreatorSummaryExport*` + `Infrastructure/{Sqlite,SqlServer,ProviderRouting}*` | 不截斷全編製人員彙總查詢 |
| `Infrastructure/WorkpaperWriter.cs`(新)+ SAX 原語/sheet emitters | OpenXML SAX 串流寫出器(deep module) |
| `Application/ExportWorkpaperStreamHandler.cs`(新,取代 stub) | 編排:gather metadata、決定 sheets、呼叫 writer |
| `Bridge/Form1`(host) | `host.selectSavePath`(SaveFileDialog) |
| `AppCompositionRoot.cs` | 註冊 writer + handler + 查詢 + 正準名 |
| `wwwroot/js/jet-api.js` + `steps/export-step.js` + css | action 字串 + 匯出觸發/選表/完成回饋(零邏輯) |
| `docs/jet-guide.md`/`development-status.md`/`development-log.md`/`windows-handoff.md` | 回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:`GlCanonicalNames` 全 logical key 有正準名且與 `GlFieldWhitelist` 鍵一致;匯出契約型別。
- **Application 驗收**(`HandlerTestHost` + demo):`export.workpaperStream` 寫到暫存檔 → 用 reader 讀回斷言:(a)15(或條件後)張表存在、表名正確;(b)封面 metadata、step1 欄標 + 至少一科目列數值 == 獨立 recount、step1-2 全名單筆數 == distinct created_by recount、step2 借貸兩欄、step3 C 數與 tagMatrixScenarios 一致、step4 矩陣 Y 與 matchedPositions 一致、step4-1 動態 C 欄集正確;(c)條件表:diff≠0 時 step1-3-1 存在、無差異時不存在(兩 demo 變體);(d)手填欄空白;(e)Not-in-TB 字面值;(f)`sheetStats` 列數正確。
- **Infrastructure/parity**:全編製人員查詢 SQLite vs SQL Server 等價(`[SqlServerFact]`、clean skip)。
- **前端**:不寫 JS 業務測試;匯出觸發/選表/存檔/完成回饋列 windows-handoff。
- 測試端讀回 `.xlsx` 可用 OpenXmlSaxTableReader(已有)或 ClosedXML(測試專案)斷言;不靠目視。

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「匯出底稿」步驟:選表(或全部)→ 觸發 `export.workpaperStream` → 跳存檔對話框(預設檔名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`)→ 寫出 → 完成回饋(bytesWritten/各表列數);Excel 開啟逐表目視對齊樣本(封面、step1 全科目、step1-2 全名單、step2 借貸兩欄、step3/4/4-1 矩陣、step5 橫幅、三參考表、Not-in-TB 字面值);diff≠0 案件 step1-3-1 出現、無差異案件不出現;大母體(百創 ~140 萬)匯出不爆記憶體、可完成。

## 與其他子專案的邊界

- **D1/D2**:寫出器消費其查詢(逐頁串流);不改其語意。
- **B/C/C補遺/F/A**:資料來源(Not-in-TB、授權清單衍生、行事曆、規則矩陣)皆已落地;E1 純讀。
- **E2(延後)**:Field Mapping Info 的**匯入** round-trip(讀回重建配對),用 E1 的 `GlCanonicalNames` 反向對照;E1 已把正準名表設計成雙向共用。

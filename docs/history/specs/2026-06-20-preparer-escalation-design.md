# 編製人員升級設計(子專案 C)

> **狀態:已實作,待 GUI 驗收。** 本設計屬「匯出底稿」里程碑的子專案 **C(編製人員升級)**,已實作完成、自動化測試全綠(本機 618 passed、SQL Server parity 實跑),待使用者 GUI 人工驗收(尚未驗收)。權威描述已回寫 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/development-status.md`、`docs/development-log.md`;本快照保留供決策回查,GUI 驗收後依慣例去日期前綴或刪除。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`(子專案 C);方法學 step3 C5/C6 與 step1-2 編製人員表(`.git/sdd/workpaper-synthesis.md`)。

## 背景與動機

事務所方法學的編製人員測試有兩條高風險條件:**C5「非授權編製人員」**(分錄由不在授權清單之人編製)與 **C6「低頻編製者」**(全年編製分錄筆數過少之人,風險較高)。JET 現況只有 `creator_summary`(R5)——top-50 編製者**彙總**,且為 Aggregate **不可作進階篩選列述詞**(`RuleCatalog.cs:43`),既無授權清單概念、也無「非授權者測全部分錄」、無「依張數篩低頻」。C 補上這兩條規則與授權清單匯入。

「測非授權者的**全部分錄**」過去需新基礎設施;**D1 已完成全量明細分頁**,故 C5 只要是可篩選 row-tag,存成篩選情境即可用 D1 的 `query.filterHitsPage` 取回全部命中分錄——C 不需另造 escalation 基礎設施。

## 方法學依據

- **C5 非授權編製人員**:不應出現非授權人員編製之分錄(且排除系統自動拋轉)。授權清單為查核團隊維護之名單(step3 C5 以「`傳票建立人員_JE` 值不包含〔名單〕」表達)。
- **C6 低頻編製者**:由不常編製分錄之人員(全年編製 < 12 筆,即 ≤ 11)所編之分錄風險較高(step3 C6「數字欄位【編製人員張數】值小於(含) 11」)。門檻為查核員可調之輸入,方法學樣本用 11。
- step1-2 編製人員表(編製人員 / 自動或人工 / 傳票數 / 金額彙總)現以 creator_summary 供給;本輪不改其彙總視圖(全名單匯出屬 E)。

## 目標與範圍

### 做

1. **授權編製人員清單匯入**:`.xlsx` 單欄姓名(英文標頭、replace-only),沿用 F 的參考資料匯入模式;新 `target_authorized_preparer` 表 + staging(`IF NOT EXISTS` 加表,**不升 schema 版本**)。
2. **C5「非授權編製人員」預篩選規則**(RowTag):`created_by` 不在授權清單;以「名單已匯入」閘控(同 `unexpectedAccountPair` 需科目配對);可作進階篩選列述詞。
3. **C6「低頻編製者」**:(a)預篩選 row-tag = 該 `created_by` 全期分錄筆數 ≤ **固定預設 11**(Domain 常數);(b)新增進階篩選條件型別「自訂編製人員張數」= 可輸入門檻 ≤N(同 A 的 `customTrailingZeros` 模式)。
4. 前端:授權清單匯入卡 + 兩條新預篩選規則顯示(沿用既有規則清單渲染 + D1 載入更多)。

### 不做(移交 / YAGNI)

- **不改 `creator_summary`**(top-50 彙總視圖保留);step1-2 全名單匯出屬 E。
- **不另造 escalation 基礎設施**:「測全部分錄」靠 C5 row-tag 存情境 + D1 `filterHitsPage`。
- **不做匯出 writer / step1-2 sheet**(屬 E)、不做 tag 矩陣(屬 D2)。
- 授權清單只比對姓名;職稱/部門等不納入(底稿本為手填)。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。規則述詞/門檻常數/匯入解析欄位在 Domain;**provider 分支只在 Infrastructure**。跨層介面(store/materializer 類)放 Domain。
- **前端零商業邏輯**:不在前端/Form1/HTML/CSS/JS 做驗證、規則、SQL、匯入解析。
- **postMessage 邊界**:不在 `jet-api.js` 以外呼叫 `window.chrome.webview.postMessage`。
- **規則 = 參數化集合式 SQL**:識別字只來自白名單/常數,使用者值(門檻、清單比對)一律參數綁定;DbCommand,provider 中立。
- **OpenXML SAX 唯一**:授權清單 `.xlsx` 讀取走既有 `OpenXmlSaxTableReader`(同 F);無新讀取器。
- **契約先行**:動任何 action wire 形狀前**先改 `docs/action-contract-manifest.md`**(新 import action + prescreen 兩 row-tag + 新 filter 條件型別)。ActionContractTests 鎖回應形狀。
- **雙 provider 等價**:SQLite 與 SQL Server 兩側同步(新表、規則述詞、匯入 store);SQL Server 測試走 LocalDB 閘控,每查詢/規則比照 D1 加 `[SqlServerFact]` parity。
- **結果失效不變量**:授權清單**重匯入**時,依賴它的 C5 規則結果失效(沿用既有重匯入清結果模式——授權清單 replace 走 `RuleRunResultReset.ClearWithinAsync` 同交易,使 prescreen/filter 結果重算);不破壞 `result_rule_run` 回放。
- **不編輯** WinForms designer 產生檔。
- **測試邊界(jet-testing 三層)**:Domain 純函式單元(清單解析、門檻常數、述詞語意)+ Application 驗收(`HandlerTestHost`,對 manifest wire:匯入、兩規則命中 == 獨立 recount、閘控、自訂門檻)+ Infrastructure/parity(真 SQLite,SQL Server LocalDB 閘控)。
- **TDD**;**不自行 commit**(版本控制由使用者親自下令;subagent 執行階段亦不 commit,用 tree-diff 隔離)。

---

## 功能 1:授權編製人員清單匯入

- **格式**:`.xlsx` 單欄,英文標頭(如 `AUTHORIZED_PREPARER`);姓名逐列;replace-only(重匯入全換)。沿用 F 的 `ImportCalendarFromFileHandler` / 科目配對匯入範式(`OpenXmlSaxTableReader` + 欄位關鍵字解析器 + 三態投影)。
- **解析**:新增 Domain 單欄解析器(鏡射 `AccountMappingColumnResolver` 的關鍵字命中,只認姓名欄);空白列略過;姓名 `TRIM` 正規化後存。
- **schema**(雙 provider,`IF NOT EXISTS` / `IF OBJECT_ID`,不升版):
  - `staging_authorized_preparer_raw_row`(batch/row_number/row_json,同其他 staging)。
  - `target_authorized_preparer(name TEXT NOT NULL PRIMARY KEY)`(去重;TRIM 後存)。
- **action**:`import.authorizedPreparer.fromFile`(payload 檔案路徑;僅 `.xlsx`);匯入即投影、replace-only;失效:同交易 `RuleRunResultReset.ClearWithinAsync`(C5 命中依賴名單)。
- **store**:`IAuthorizedPreparerStore`(Domain 介面)+ Sqlite/SqlServer/Routing 實作(`ReplaceAllAsync` / 供規則查詢)。

## 功能 2:C5「非授權編製人員」預篩選規則(RowTag)

- **登錄**:`RuleCatalog.cs` 新增 `("non_authorized_preparer", "nonAuthorizedPreparer", "非授權編製人員", "R10", RuleShape.RowTag)`;`PrescreenRuleKeys` 加常數 + 入 `FilterableKeys`(`RuleCatalogTests` 不變量同步)。
- **述詞**(`GlRulePredicates`,集合式 SQL):`created_by IS NOT NULL AND TRIM(created_by) <> '' AND TRIM(created_by) NOT IN (SELECT name FROM target_authorized_preparer)`。空白 created_by 不命中(無從判定授權與否)。
- **閘控**:名單未匯入時不執行此規則(同 `unexpectedAccountPair` 以 `input.RunUnexpectedAccountPair`/`HasAccountMapping`);`PrescreenRunInput` 加 `HasAuthorizedPreparers`,handler 依名單表非空放行;wire response 該鍵 `status="na"`、`naReason` 說明未匯入名單。
- **escalation**:此 row-tag 可入篩選情境;查核員存「非授權編製人員」情境 → 用 D1 `query.filterHitsPage` 取回**全部命中分錄**(C 不另做)。

## 功能 3:C6「低頻編製者」(固定規則 + 自訂門檻條件)

- **門檻常數**:Domain 新增 `PreparerFrequency.DefaultMaxEntries = 11`(方法學:全年 < 12 筆)。
- **固定 row-tag**:`RuleCatalog.cs` 新增 `("low_frequency_preparer", "lowFrequencyPreparer", "低頻編製者", "R11", RuleShape.RowTag)`;入 `FilterableKeys`。述詞:`created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= 11)`(門檻取 `DefaultMaxEntries`,參數綁定)。
- **自訂門檻進階篩選條件**:`FilterScenario.cs` `FilterRuleType` 新增 `CustomPreparerEntryCount`;`FilterRuleSpec` 加 `int? MaxEntries`;`FilterScenarioPayloadParser` 解析;`GlFilterWhereBuilder.BuildRule` 加 case → `created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= @maxEntries)`(門檻使用者輸入、參數綁定;範圍驗證 ≥1)。同 A 的 `customTrailingZeros`(固定預設規則 + 自訂條件型別)雙軌。
- 兩者皆 set-based、provider 中立;`PrescreenRunResult` 加 `lowFrequencyPreparerCount`;handler wire `lowFrequencyPreparer { status, count }`。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:`import.authorizedPreparer.fromFile` + facade;prescreen response 加 `nonAuthorizedPreparer`/`lowFrequencyPreparer`;filter 條件型別加 `customPreparerEntryCount` + 可篩選鍵清單 |
| `Domain/RuleCatalog.cs` | 加 R10 非授權、R11 低頻(皆 RowTag) |
| `Domain/PrescreenRules.cs` | `PrescreenRuleKeys` 加兩鍵 + 入 `FilterableKeys`;新 `PreparerFrequency.DefaultMaxEntries=11` |
| `Domain/PrescreenContracts.cs` | `PrescreenRunResult` 加兩 count;`PrescreenRunInput` 加 `HasAuthorizedPreparers` |
| `Domain/FilterScenario.cs` + `Application/FilterScenarioPayloadParser.cs` | `FilterRuleType.CustomPreparerEntryCount` + `MaxEntries` 解析 |
| `Domain/`(新)授權清單解析器 + `IAuthorizedPreparerStore` | 單欄姓名解析(鏡射 AccountMappingColumnResolver)、store 介面 |
| `Infrastructure/GlRulePredicates.cs` | 加 `NonAuthorizedPreparer()`、`LowFrequencyPreparer(maxEntries)` 述詞 |
| `Infrastructure/GlFilterWhereBuilder.cs` | `customPreparerEntryCount` case |
| `Infrastructure/{Sqlite,SqlServer}PrescreenRunRepository.cs` | 兩規則計數(C5 閘控) |
| `Infrastructure/{Sqlite,SqlServer,ProviderRouting}AuthorizedPreparerStore.cs` | replace/查詢 |
| `Infrastructure/{Jet,SqlServer}ProjectDatabase.cs` | staging + target_authorized_preparer 表(IF NOT EXISTS,不升版) |
| `Application/ImportAuthorizedPreparerFromFileHandler.cs` | 匯入(鏡射 F);失效集 |
| `Application/PrescreenRunHandler.cs` | 兩規則 wire(C5 閘控 na) |
| `AppCompositionRoot.cs` | 註冊 import handler + store |
| `wwwroot/js/jet-api.js` + `steps/*` + css | import action 字串 + 匯入卡 + 兩規則顯示(零邏輯) |
| `docs/jet-guide.md` / `development-status.md` / `development-log.md` / `windows-handoff.md` | 落地回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:`RuleCatalog` 含兩 RowTag 且 `FilterableKeys` 同步;`PreparerFrequency.DefaultMaxEntries==11`;授權清單解析器(單欄姓名命中、空白略過、TRIM);`FilterRuleType.CustomPreparerEntryCount` 解析 `MaxEntries`。
- **Application 驗收**(`HandlerTestHost`,對 manifest wire):
  - 授權清單匯入 → `target_authorized_preparer` 內容正確、replace-only;重匯入清結果(失效)。
  - C5 命中數 == demo 獨立 recount(created_by 不在名單);名單未匯入時 `status="na"`。
  - C6 固定 row-tag 命中數 == recount(COUNT≤11);`customPreparerEntryCount(@n)` 進階篩選命中 == recount(COUNT≤n)。
  - C5 row-tag 可入情境並經 `filter.preview`/(存後)`filterHitsPage` 取回全部(escalation 端到端)。
- **Infrastructure/parity**:授權清單匯入、兩規則、自訂條件 SQLite vs SQL Server 等價(`[SqlServerFact]`,比照 D1 的雙 provider 走訪/recount 等價、clean skip)。
- **前端**:不寫 JS 業務規則測試;匯入卡與規則顯示列 windows-handoff。

## 交付物

| 檔案/位置 | 變更 |
|:--|:--|
| `docs/action-contract-manifest.md` | import action + 兩 row-tag + customPreparerEntryCount |
| `src/JET/JET/Domain/*` | RuleCatalog/PrescreenRules/PrescreenContracts/FilterScenario/解析器/store 介面/門檻常數 |
| `src/JET/JET/Infrastructure/*` | 述詞、whereBuilder、prescreen 倉儲、store、schema 表 |
| `src/JET/JET/Application/*` | import handler、PrescreenRunHandler、PayloadParser |
| `src/JET/JET/AppCompositionRoot.cs` | 註冊 |
| `src/JET/JET/wwwroot/*` | import 卡 + 規則顯示 |
| `src/JET/tests/JET.Tests/*` | 三層測試 + [SqlServerFact] parity |
| `docs/jet-guide.md`、`development-status.md`、`development-log.md`、`windows-handoff.md` | 回寫 + 待驗卡 |

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「匯入資料」出現授權編製人員清單上傳卡,上傳 `.xlsx` 單欄姓名後 replace-only;重匯入使相關規則結果失效。
- 「資料驗證與測試」預篩選多「非授權編製人員」(名單未匯入時顯示未執行/na)與「低頻編製者」(命中全期 ≤11 筆編製者之分錄);兩者可〔檢視〕+ 載入更多。
- 「進階條件篩選」可用「自訂編製人員張數 ≤N」條件;並可存「非授權編製人員」情境後以命中分頁看其全部分錄(escalation)。

## 與其他子專案的邊界

- **D1**:C5/C6 row-tag 的「全部分錄」走 D1 `filterHitsPage`;C 不另做分頁。
- **D2**:兩 row-tag 可成為多情境 tag 矩陣的條件(C5/C6);C 只提供 row-tag。
- **E**:step1-2 編製人員表全名單匯出、step3 C5/C6 條件呈現屬 E。
- **A/B**:已完成;C 沿用 A 的「固定規則 + 自訂條件」雙軌與 B 的失效不變量模式。

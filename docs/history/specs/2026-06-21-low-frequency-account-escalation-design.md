# 低頻科目升級設計(子專案 C 補遺)

> **狀態:已實作,待 GUI 驗收。** 本設計是「匯出底稿」里程碑子專案 **C(編製人員升級)** 的補遺:把 step3 **C9「罕用科目(科目張數 ≤ 11)」** 補成可作進階篩選列述詞的規則。C 主體已實作(待 GUI 驗收),但只做了編製人員維度(C5/C6);科目維度的 C9 在 C 範圍外、被漏掉,本補遺補齊。已實作完成、自動化測試全綠(本機 628 passed、SQL Server parity 實跑),待使用者 GUI 人工驗收(尚未驗收)。權威描述已回寫 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/development-status.md`、`docs/development-log.md`;本快照保留供決策回查,GUI 驗收後依慣例去日期前綴或刪除。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`(修正 #4 列「科目張數」為衍生旗標);方法學 step3 C9(`.git/sdd/workpaper-synthesis.md`);2026-06-21 D2 前自我審查實機重解兩份 WorkingPaper 樣本(福懋 C9 命中 40 張)+ 對碼確認 `rareAccounts` 為彙總不可作列述詞(`PrescreenRules.cs:5-6`、`RuleCatalog.cs:44`)。

## 背景與動機

事務所方法學的 step3 C9「較少使用之科目」是一條真實在用的高風險條件(福懋樣本命中 40 張傳票):某科目全年度分錄筆數過少(< 12 筆,即 ≤ 11)時,其分錄較易藏匿違規或錯誤,風險較高。

JET 現況只有 `rare_accounts`(R6)——「較少使用之科目」**彙總**(top-50),且為 `RuleShape.Aggregate`,**不可作進階篩選列述詞**(`RuleCatalog.cs:44`;`PrescreenRules.cs:5-6` 明列)。這和 C 開工前 `creator_summary` 的處境完全相同——當時 C 新增了 `low_frequency_preparer`(R11, RowTag)與彙總並存。本補遺照搬同一模式到科目維度,新增 `low_frequency_account`(RowTag),讓 C9 能存成篩選情境並被 D2 的 tag 矩陣納入。

## 方法學依據

- **C9 低頻科目**:step3「數字欄位【科目張數】值小於(含) 11」。門檻為查核員可調之輸入,方法學樣本用 11(全年 < 12 筆)。
- step3 C9 另含「會計科目名稱不包含〔長串例外清單〕」子條件——此屬使用者在情境裡用既有 **Text NotContains**(`accName` 已在 `GlFieldWhitelist`)AND 組合,本補遺不另做。

## 目標與範圍

### 做

1. **C9「低頻科目」預篩選規則(RowTag)**:某 `account_code` 全期分錄筆數 ≤ **固定預設 11**(Domain 常數);可作進階篩選列述詞;與既有 `rare_accounts`(R6 彙總)**並存不取代**。
2. **「自訂科目張數」進階篩選條件型別**:可輸入門檻 ≤N(鏡射 C6 的 `customPreparerEntryCount` 與 A 的 `customTrailingZeros` 雙軌)。
3. 前端:預篩選清單新增「低頻科目之分錄」一條 + 進階篩選新增「自訂科目張數」條件型別(沿用既有規則清單渲染 + D1 載入更多)。

### 不做(移交 / YAGNI)

- **不改 `rare_accounts`**(R6 top-50 彙總視圖保留);step1 全科目匯出屬 E。
- **不另造 escalation 基礎設施**:命中分錄走 C5/C6 已驗證的路徑——存情境後用 D1 `query.filterHitsPage` 取回全部命中(本補遺不另做分頁)。
- **不做匯出 writer / step4-1 動態欄位集**(屬 E)、不做 tag 矩陣(屬 D2)。
- **不做科目名稱例外清單的專屬 UI**:由既有 Text NotContains 在情境裡 AND 組合。
- 不新增 schema 表或欄位(本規則純讀既有 `target_gl_entry`)。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。規則述詞/門檻常數在 Domain;**provider 分支只在 Infrastructure**。
- **前端零商業邏輯**:不在前端/Form1/HTML/CSS/JS 做驗證、規則、SQL。
- **postMessage 邊界**:不在 `jet-api.js` 以外呼叫 `window.chrome.webview.postMessage`。
- **規則 = 參數化集合式 SQL**:識別字只來自白名單/常數,使用者值(門檻)一律參數綁定;`DbCommand`,provider 中立。
- **契約先行**:動任何 action wire 形狀前**先改 `docs/action-contract-manifest.md`**(prescreen response 加一 row-tag + filter 條件型別加 `customAccountEntryCount` + 可篩選鍵清單)。`ActionContractTests` 鎖回應形狀。
- **雙 provider 等價**:SQLite 與 SQL Server 兩側同步(規則述詞、whereBuilder、prescreen 倉儲計數);SQL Server 測試走 LocalDB 閘控,規則 + 自訂條件比照 D1/C 加 `[SqlServerFact]` parity(雙 provider recount 等價、clean skip)。
- **不破壞既有失效不變量**:本規則純讀 `target_gl_entry`,不依賴外部清單(無 C5 那種 `NOT IN` 空集合反轉風險);無新失效來源。
- **不編輯** WinForms designer 產生檔。
- **測試邊界(jet-testing 三層)**:Domain 純函式單元(門檻常數、登錄表不變量、`MaxEntries` 解析)+ Application 驗收(`HandlerTestHost`,對 manifest wire:固定規則命中 == 獨立 recount、自訂門檻命中 == recount)+ Infrastructure/parity(真 SQLite,SQL Server LocalDB 閘控)。
- **TDD**;**不自行 commit**(版本控制由使用者親自下令;subagent 執行階段亦不 commit,用 tree-diff 隔離)。

---

## 功能 1:C9「低頻科目」預篩選規則(RowTag)

- **登錄**:`RuleCatalog.cs` 新增 `("low_frequency_account", "lowFrequencyAccount", "低頻科目", "R12", RuleShape.RowTag)`,與 `rare_accounts`(R6 Aggregate)並存(鏡射 `low_frequency_preparer` R11 與 `creator_summary` R5 並存)。
- **wire key**:`PrescreenRuleKeys` 加 `LowFrequencyAccount = "lowFrequencyAccount"` 常數並入 `FilterableKeys`(`RuleCatalogTests` 的「FilterableKeys == RowTag 集合」不變量同步)。
- **門檻常數**:Domain 新增 `AccountFrequency.DefaultMaxEntries = 11`(鏡射 `PreparerFrequency.DefaultMaxEntries`;方法學:全年 < 12 筆)。
- **述詞**(`GlRulePredicates`,集合式 SQL,鏡射 `LowFrequencyPreparer`):
  ```
  g.account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @maxEntries)
  ```
  門檻取 `AccountFrequency.DefaultMaxEntries`,參數綁定;純 ANSI,雙 provider 相同。
- **計數 wire**:`PrescreenRunResult` **末位**加 `LowFrequencyAccountCount`;handler wire `lowFrequencyAccount { status, count }`。此規則永遠可執行(無外部依賴,不需閘控,同 C6)。
- **escalation**:此 row-tag 可入篩選情境;查核員存「低頻科目」情境 → 用 D1 `query.filterHitsPage` 取回全部命中分錄(本補遺不另做)。

## 功能 2:「自訂科目張數」進階篩選條件型別

- **型別**:`FilterScenario.cs` `FilterRuleType` 新增 `CustomAccountEntryCount`。
- **欄位重用**:**重用既有 `FilterRuleSpec.MaxEntries`**(`int?`,C6 已加);一條規則只有一種型別,`customPreparerEntryCount` 與 `customAccountEntryCount` 不會在同一規則共存,不另開欄位(Good Taste:不為對稱而增資料)。
- **解析**:`FilterScenarioPayloadParser` 對 `customAccountEntryCount` 解析 `MaxEntries`(同 `customPreparerEntryCount`)。
- **驗證**:`FilterScenarioValidator.ValidateRule` 新增 case,鏡射 `CustomPreparerEntryCount`(`MaxEntries >= 1`)。
- **WHERE 組譯**:`GlFilterWhereBuilder.BuildRule` 新增 case → 呼叫 `GlRulePredicates.LowFrequencyAccount(command, maxEntries)`(同述詞、門檻改用使用者輸入)。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:prescreen response 加 `lowFrequencyAccount`;filter 條件型別加 `customAccountEntryCount`;可篩選鍵清單加 `lowFrequencyAccount` |
| `Domain/RuleCatalog.cs` | 加 R12 低頻科目(RowTag),與 R6 並存 |
| `Domain/PrescreenRules.cs` | `PrescreenRuleKeys` 加 `LowFrequencyAccount` + 入 `FilterableKeys`;新 `AccountFrequency.DefaultMaxEntries=11` |
| `Domain/PrescreenContracts.cs` | `PrescreenRunResult` 末位加 `LowFrequencyAccountCount` |
| `Domain/FilterScenario.cs` | `FilterRuleType.CustomAccountEntryCount`;validator case(重用 `MaxEntries`) |
| `Application/FilterScenarioPayloadParser.cs` | `customAccountEntryCount` 解析 `MaxEntries` |
| `Infrastructure/GlRulePredicates.cs` | 加 `LowFrequencyAccount(command, maxEntries)` 述詞 |
| `Infrastructure/GlFilterWhereBuilder.cs` | `customAccountEntryCount` case |
| `Infrastructure/{Sqlite,SqlServer}PrescreenRunRepository.cs` | `lowFrequencyAccount` 計數 |
| `Application/PrescreenRunHandler.cs` | `lowFrequencyAccount` wire(永遠可執行) |
| `wwwroot/js/{ui-core.js}` + `steps/{validate-step.js,filter-step.js}` + css | 預篩選清單一條 + 自訂條件型別/鍵選項/摘要(零邏輯) |
| `docs/jet-guide.md` / `development-status.md` / `development-log.md` / `windows-handoff.md` | 落地回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:`RuleCatalog` 含 R12 RowTag 且 `FilterableKeys` 同步(`RuleCatalogTests` 不變量);`AccountFrequency.DefaultMaxEntries==11`;`FilterRuleType.CustomAccountEntryCount` 解析 `MaxEntries`;validator `MaxEntries>=1` BVA。
- **Application 驗收**(`HandlerTestHost`,對 manifest wire):
  - C9 固定 row-tag 命中數 == demo 獨立 recount(`account_code` 全期 COUNT ≤ 11)。
  - `customAccountEntryCount(@n)` 進階篩選命中 == recount(COUNT ≤ n)。
  - C9 row-tag 可入情境並經 `filter.preview`/(存後)`filterHitsPage` 取回全部(escalation 端到端,鏡射 C5/C6 測試)。
- **Infrastructure/parity**:固定規則 + 自訂條件 SQLite vs SQL Server 等價(`[SqlServerFact]`,比照 D1/C 的雙 provider recount 等價、clean skip)。
- **前端**:不寫 JS 業務規則測試;預篩選項目與自訂條件列 windows-handoff。

## 交付物

| 檔案/位置 | 變更 |
|:--|:--|
| `docs/action-contract-manifest.md` | 一 row-tag + customAccountEntryCount + 可篩選鍵 |
| `src/JET/JET/Domain/*` | RuleCatalog/PrescreenRules/PrescreenContracts/FilterScenario/門檻常數 |
| `src/JET/JET/Infrastructure/*` | 述詞、whereBuilder、prescreen 倉儲計數 |
| `src/JET/JET/Application/*` | PrescreenRunHandler、PayloadParser |
| `src/JET/JET/wwwroot/*` | 預篩選項目 + 自訂條件 |
| `src/JET/tests/JET.Tests/*` | 三層測試 + [SqlServerFact] parity |
| `docs/jet-guide.md`、`development-status.md`、`development-log.md`、`windows-handoff.md` | 回寫 + 待驗卡 |

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「資料驗證與測試」預篩選新增「低頻科目之分錄」(命中全期 ≤11 筆之科目的分錄);可〔檢視〕+ 載入更多。
- 「進階條件篩選」可用「自訂科目張數 ≤N」條件(預設 11、min 1);〔預覽這個情境〕命中合理;已儲存情境摘要顯示「科目全期張數 ≤ N」。
- 可存「低頻科目」情境後以命中分頁看其全部分錄(escalation);與科目名稱 Text NotContains AND 組合(重現 step3 C9)亦正常。

## 與其他子專案的邊界

- **C(主體)**:本補遺鏡射 C 的 C6(`lowFrequencyPreparer` + `customPreparerEntryCount`)雙軌;共用同骨架。
- **D1**:命中「全部分錄」走 D1 `filterHitsPage`;本補遺不另做分頁。
- **D2**:此 row-tag(C9)可成為多情境 tag 矩陣的條件;本補遺只提供 row-tag。
- **E**:step1 全科目表、step3 C9 條件呈現屬 E。

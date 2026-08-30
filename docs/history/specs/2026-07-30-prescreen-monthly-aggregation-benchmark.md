# S2a 預篩選月份聚合 benchmark evidence

日期：2026-07-30

狀態：完成

D4 結論：**降級**。S2b 應交付「規則 × 全期」單維長條，不交付「規則 × 月」二維熱圖。

## 量測問題與口徑

本場回答：在現行完整 `PrescreenRunHandler.HandleAsync` action 之後，加上 13 個既有預篩選規則的月份聚合與每格去重傳票數，會增加多少牆鐘時間。

比較的三組都是 warm-cache：

1. `currentPrescreenRunAction`：現行完整 `prescreen.run` handler，包括 prerequisites 讀取、typed program、DTO／JSON、`result_rule_run` 保存與 step advance。
2. `perRuleGroupByMonth`：同一完整 action，加上一筆月份母體查詢與 13 筆逐規則 `GROUP BY month` 查詢。
3. `singleCommandMultiFlag`：同一完整 action，加上一筆含 13 個 flag、先依月份與傳票彙整再依月份彙整的 SQL command。

兩個候選都包含同一份完整 action，因此表中的候選時間是 action 加聚合的總時間，不是只有聚合 SQL 的裸時間。聚合結果會在計時區內 materialize，並序列化為 canonical JSON 產生 fingerprint；尚未把未落地的擴充欄位再寫入 `summary_json`，所以這是具完整 action 分母、未含擴充 payload 最終持久化的保守候選量測。未來實作可能合併序列化或重用連線，故不把它嚴格稱為數學下界；翻轉本場裁決所需的降幅另在 D7 算式後量化。

規則 SQL 由 `PrescreenRuleKeys.FilterableKeys` 與 `GlFilterWhereBuilder` 的 canonical predicates 產生；測試沒有另抄 13 套規則。逐規則的 `COUNT(DISTINCT document_number)` 與 multi-flag 的傳票層彙整都忽略 null 傳票號碼，兩個策略的月份母體、命中行數與去重傳票數 fingerprint 必須相同，且各規則全期命中行數必須等於完整 action 的 wire response，否則測試失敗。

每個策略先 warm-up 一次，再依 `B/S/M → S/M/B → M/B/S` 的平衡順序各量三次並取中位數。這是同機、單程序、warm-cache 比較；`singleCommandMultiFlag` 指單一 logical SQL command，不宣稱 provider 一定只做一次 physical scan。

## 環境與資料形狀

| 項目 | 值 |
|:---|:---|
| OS | Microsoft Windows 10.0.26200，x64 |
| CPU | AMD Ryzen 7 7800X3D 8-Core Processor；測試程序可見 16 logical processors |
| Runtime | .NET 10.0.10；Server GC = false |
| 組態 | Release |
| 暫存磁碟 | `C:\` |
| 開始量測時可用空間 | 631,176,237,056 bytes |
| provider | DuckDB |
| GL 列數 | 5,000,000 |
| `target_gl_entry` 欄數 | 20 |
| 去重傳票數 | 1,250,000（每張 4 列） |
| 月份／科目／編製人員 | 12／5／1,000 |
| DuckDB 檔案大小 | 499,920,896 bytes |
| 合成資料建立時間 | 29,425.0855 ms |
| 程序 peak working set（量測前／後） | 5,354,606,592／5,384,577,024 bytes |
| 合成資料清理 | `CleanupVerified = true`；場末已刪除 system temp 下的專屬 project root |

資料涵蓋查核期間 `2025-01-01..2025-12-31`，並建立科目配對、授權編製人員、假日／補班日與能觸發 13 個 canonical predicates 的合成欄位。資料庫建立、warm-up 與清理不計入三組 measured run。

## 精確命令

```powershell
dotnet build src/JET/tests/JET.Tests/JET.Tests.csproj -c Release --no-restore --nologo

$env:JET_PRESCREEN_MONTHLY_BENCHMARK = '1'
$env:JET_PRESCREEN_MONTHLY_BENCHMARK_OUTPUT = '<user-profile>\source\repos\new-je-tool\src\JET\tests\JET.Tests\TestResults\20260730-s2a-prescreen-monthly-benchmark.json'
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~DuckDb_FiveMillionRows_ProducesD7GateEvidence" `
  --logger "trx;LogFileName=20260730-s2a-duckdb-benchmark.trx"
```

## 500 萬列實測

單位為 ms；陣列順序是該策略的三次 measured run，不是由小到大排序。

| 組別 | run 1 | run 2 | run 3 | 中位數 |
|:---|---:|---:|---:|---:|
| 現行完整 `prescreen.run` action | 1,017.4169 | 1,336.8548 | 1,458.7778 | **1,336.8548** |
| 逐規則 `GROUP BY month` | 2,001.5333 | 2,937.5140 | 3,540.4677 | **2,937.5140** |
| 單 command multi-flag | 4,088.2308 | 5,697.8691 | 6,482.4797 | **5,697.8691** |

較快候選為 `perRuleGroupByMonth`。大型 fixture 的兩個候選 fingerprint 相同：
`33B9DC8EACDFC1C56165EC8429614C701475659A20C3D75A2131A5C178186386`。

## D7 實際落點與 D4 結論

以較快候選計算：

```text
絕對增量
= 2,937.5140 - 1,336.8548
= 1,600.6592 ms

增幅
= 1,600.6592 / 1,336.8548 × 100%
= 119.7332%

外推至 2,000 萬列的絕對增量
= 1,600.6592 × 4
= 6,402.6368 ms
= 6.4026 秒
```

D7 的「降級」分支是兩個條件的 OR：增幅大於 25%，或外推絕對增量大於 90 秒。本場增幅 **119.7332% > 25%**，雖然外推絕對增量 **6.4026 秒 ≤ 90 秒**，仍已明確命中降級分支。因此：

- D4 結論來源是 **500 萬列實測已超過百分比門檻**。
- 25% 只容許 `1,336.8548 × 25% = 334.2137 ms` 的增量；目前為 1,600.6592 ms。若要翻轉裁決，需省掉 1,266.4455 ms，即現有增量的約 79.1%。合併 156 格的序列化或省一次連線固定成本不構成此量級。
- 依 D7 不補跑 2,000 萬列，`FullScale = null`。
- S2b 固定採 `rulePeriod` 的「規則 × 全期」單維長條。
- 本結論不是「外推過」，所以不新增 `windows-handoff.md` 的真實大案承接項。

## 小型 provider smoke

SQLite、DuckDB、SQL Server 各以 20,000 列、同一資料形狀跑過語意 smoke。下列數字各為 warm-up 後的一次 measured run；用途是確認 provider 沒有語意分歧或小規模病態失敗，不拿來裁決 D4。

| provider | baseline | 逐規則 | multi-flag |
|:---|---:|---:|---:|
| SQLite | 193.9 ms | 435.0 ms | 518.2 ms |
| DuckDB | 286.0 ms | 335.9 ms | 358.3 ms |
| SQL Server | 282.9 ms | 530.7 ms | 818.2 ms |

三個 provider 的月份母體、13 規則命中行與去重傳票格值 fingerprint 均為：
`1D7F506D07A369AEB9710195724388395E3E289E30F23841CFDBA5D4E78064E8`。
SQL Server smoke 結束後另查 `sys.schemas`，確認專屬 schema 已不存在；不是只依 cleanup request 推定。

## 原始證據

| 檔案 | SHA-256 |
|:---|:---|
| `src/JET/tests/JET.Tests/TestResults/20260730-s2a-prescreen-monthly-benchmark.json` | `BCA02D5AC03D876EE49A73A30958C22421B45F920B72F765334EC598B5E1EA26` |
| `src/JET/tests/JET.Tests/TestResults/20260730-s2a-duckdb-benchmark.trx` | `A6F2706988B6A96774EA4CF8B8F10912E4352C2D1C0235C08851D98DE01BE16C` |
| `src/JET/tests/JET.Tests/TestResults/20260730-s2a-local-smoke.trx` | `0E4FA5FD2EFE4E0C936DFA3FD2BF7A29521F2C2EAADF240B73F51AED452EDC5F` |
| `src/JET/tests/JET.Tests/TestResults/20260730-s2a-sqlserver-smoke.trx` | `8A661B4499970BE9B7AB3BBC38DA1EE0EAE068CE7F5750B0C337FFD2735F341B` |
| `src/JET/tests/JET.Tests/TestResults/20260730-s2a-architecture.trx` | `E763E0C1C6F5ACB82F7DC371EA736A140EB4DA19D1126E723CA30B032CF51164` |

量測 harness 位於
`src/JET/tests/JET.Tests/Infrastructure/PrescreenMonthlyAggregationBenchmarkTests.cs`。大型測試預設具名 skip，只有設定 `JET_PRESCREEN_MONTHLY_BENCHMARK=1` 才會建立 fixture；若 500 萬列落入 D7 灰帶，才會由同一測試自動補跑 2,000 萬列。

## 適用限制

- baseline 是 production `PrescreenRunHandler` 的完整 action。候選在 action 後另開 repository-style connection 執行聚合、materialize 格值並序列化 fingerprint；未重寫尚不存在的擴充 `summary_json`。因此它是保守候選量測，而非對未落地實作的逐指令預言；要從 119.7332% 回到 25% 需消除約 79.1% 的實測增量，本場不在門檻附近。
- 合成資料高度規則且為 warm-cache；結果只依 master spec 的 D7 裁決 S2b 形狀，不延伸成真實案件的通用吞吐承諾。
- 未蒐集 provider execution plan，因此只比較牆鐘與語意結果，不把 logical command 數解讀為實際掃描次數。

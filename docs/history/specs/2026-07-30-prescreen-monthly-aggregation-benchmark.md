# S2a 預篩選月份聚合效能量測證據

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-07-30-prescreen-monthly-aggregation-benchmark.md` 取回。

日期：2026-07-30。狀態：完成。

D4 結論：**降級**。S2b 應交付「規則 × 全期」單維長條，不交付「規則 × 月」二維熱圖。

## 量測問題與口徑

這次回答：在現行完整 `PrescreenRunHandler.HandleAsync` action 之後，加上 13 個既有預篩選規則的月份聚合與每格去重傳票數，會增加多少牆鐘時間。
三組都是 warm-cache：

1. `currentPrescreenRunAction`：現行完整 `prescreen.run` handler，包括讀取前置資料、typed program、DTO 與 JSON、保存 `result_rule_run` 與前進步驟。
2. `perRuleGroupByMonth`：同一完整 action，加上一筆月份母體查詢與 13 筆逐規則 `GROUP BY month` 查詢。
3. `singleCommandMultiFlag`：同一完整 action，加上一筆含 13 個 flag、先依月份與傳票彙整再依月份彙整的 SQL command。

候選時間是 action 加聚合的總時間，不是只有聚合 SQL。聚合結果在計時區內落地並序列化成 fingerprint，但沒有再寫進 `summary_json`，
所以這是含完整 action、未含擴充欄位最終持久化的保守量測；不嚴格稱為數學下界。

規則 SQL 由 `PrescreenRuleKeys.FilterableKeys` 與 `GlFilterWhereBuilder` 的正式述詞產生，測試沒有另抄 13 套規則。兩個策略都忽略 null 傳票號碼，
月份母體、命中行數與去重傳票數的 fingerprint 必須相同，各規則全期命中行數也必須等於完整 action 的回應，否則測試失敗。
每個策略先 warm-up 一次，再依 `B/S/M → S/M/B → M/B/S` 的平衡順序各量三次取中位數。這是同機、單程序、warm-cache 的比較；
`singleCommandMultiFlag` 指單一邏輯 SQL 命令，不宣稱資料庫一定只掃描一次。

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
| 月份、科目、編製人員 | 12、5、1,000 |
| DuckDB 檔案大小 | 499,920,896 bytes |
| 合成資料建立時間 | 29,425.0855 ms |
| 程序 peak working set（量測前、後） | 5,354,606,592、5,384,577,024 bytes |
| 合成資料清理 | `CleanupVerified = true`；場末已刪除專屬 project root |

資料涵蓋查核期間 `2025-01-01..2025-12-31`，含科目配對、授權編製人員、假日與補班日，以及能觸發 13 個規則述詞的合成欄位。
資料庫建立、warm-up 與清理不計入量測。量測以環境變數 `JET_PRESCREEN_MONTHLY_BENCHMARK=1` 啟用測試 `DuckDb_FiveMillionRows_ProducesD7GateEvidence`，完整命令見壓縮前全文。

## 500 萬列實測

單位為 ms；三次依執行順序排列，不是由小到大。

| 組別 | run 1 | run 2 | run 3 | 中位數 |
|:---|---:|---:|---:|---:|
| 現行完整 `prescreen.run` action | 1,017.4169 | 1,336.8548 | 1,458.7778 | **1,336.8548** |
| 逐規則 `GROUP BY month` | 2,001.5333 | 2,937.5140 | 3,540.4677 | **2,937.5140** |
| 單 command multi-flag | 4,088.2308 | 5,697.8691 | 6,482.4797 | **5,697.8691** |

較快的候選是 `perRuleGroupByMonth`。兩個候選的 fingerprint 相同：`33B9DC8EACDFC1C56165EC8429614C701475659A20C3D75A2131A5C178186386`。

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

D7 的「降級」分支是兩個條件任一成立：增幅大於 25%，或外推絕對增量大於 90 秒。本場增幅 **119.7332% > 25%**，雖然外推絕對增量 **6.4026 秒 ≤ 90 秒**，仍已明確落在降級分支。因此：

- D4 結論來源是 **500 萬列實測已超過百分比門檻**。
- 25% 只容許 `1,336.8548 × 25% = 334.2137 ms` 的增量，目前是 1,600.6592 ms。要翻轉裁決需省掉 1,266.4455 ms，約為現有增量的 79.1%；合併 156 格的序列化或省一次連線固定成本達不到這個量級。
- 依 D7 不補跑 2,000 萬列，`FullScale = null`。
- S2b 固定採 `rulePeriod` 的「規則 × 全期」單維長條。
- 這個結論不是外推得來，所以不新增 `windows-handoff.md` 的真實大案待驗項目。

## 小型跨資料庫檢查

SQLite、DuckDB、SQL Server 各以 20,000 列、同一資料形狀檢查語意。數字是 warm-up 後的一次量測，只用來確認資料庫之間沒有語意差異或小規模的異常，不用來裁決 D4。

| provider | baseline | 逐規則 | multi-flag |
|:---|---:|---:|---:|
| SQLite | 193.9 ms | 435.0 ms | 518.2 ms |
| DuckDB | 286.0 ms | 335.9 ms | 358.3 ms |
| SQL Server | 282.9 ms | 530.7 ms | 818.2 ms |

三種資料庫的月份母體、13 規則命中行與去重傳票的 fingerprint 都是 `1D7F506D07A369AEB9710195724388395E3E289E30F23841CFDBA5D4E78064E8`。
SQL Server 檢查結束後另查 `sys.schemas`，確認專屬 schema 已不存在。

量測程式在 `src/JET/tests/JET.Tests/Infrastructure/PrescreenMonthlyAggregationBenchmarkTests.cs`。大型測試預設具名略過；
若 500 萬列落在 D7 的灰色地帶，才由同一測試自動補跑 2,000 萬列。原始證據檔與 SHA-256 見壓縮前全文。

## 適用限制

- baseline 是正式 `PrescreenRunHandler` 的完整 action；候選在 action 後另開連線聚合、落地並序列化 fingerprint，沒有改寫尚不存在的擴充 `summary_json`。這是保守量測，不是對未來實作的逐指令預測；要從 119.7332% 回到 25% 需消除約 79.1% 的實測增量，本場離門檻很遠。
- 合成資料高度規則且為 warm-cache；結果只依 master spec 的 D7 決定 S2b 的形狀，不延伸成真實案件的吞吐承諾。
- 沒有蒐集資料庫的執行計畫，所以只比較牆鐘時間與語意結果，不把邏輯命令數解讀成實際掃描次數。

# 大案 Excel／WorkingPaper 驗收缺陷修補證據

狀態：2026-07-28 `已落地並通過自動驗證`；在本輪結束時仍待使用者以原始 20,260,435 列案件與 Microsoft Excel 人工重驗。後續修補已納入 `FolderProfile-20260731-131350`，並於 2026-07-31 通過人工驗收。本文件只記當時 working tree 的有界修補與 receipt，不把合成案件、自動 render 或 AgentGuiTest 冒充人工驗收。

## 使用者回報與修補範圍

使用者的失敗場已完成 GL 20,260,435 列／14 欄、TB 447 列／7 欄；GL／TB 匯入分別為 264.5／0.0 秒，GL／TB 標準化分別為 668.3／0.1 秒，日期維度為假日 83 天／補班 20 天。Validation、Pre-screening 與 Criteria 均完成；情境命中 1,170 列／757 張傳票。WorkingPaper 最後停在 `step3 高風險條件彙總` 完成、8 張／856 列／171,828 毫秒，等待一小時仍無產出後取消。Excel 另出現資料欄換行、欄寬不足與 `###`；`app_message_log` 則缺少一致的總耗時與最後進度。

本修補只處理這三項：

- 六報表動態資料欄依完整格式化資料計算 CJK-aware 顯示寬度，資料格明示 no-wrap／no-indent／no-shrink；legacy 說明、方法學與表頭的刻意換行保留。WorkingPaper Step1、Step1-1、Step1-2、Step2、Step3、Step4 以 DeleteOnClose projected-cell spool 單次枚舉來源，再 replay 到 direct-template SAX；Step4 finalized 路徑仍只開一次 DB stream。
- Finalized WorkingPaper Step4 改為一條 parameterized、hit-first、ordered forward-only stream；public `query.tagMatrixVoucherPage`／plan-less compatibility paging 不變。Step4 與 Step4-1 都在昂貴查詢或 spool 前送既有 shape 的 `rowsWritten=0` sheet-start checkpoint。
- 指定長作業的成功、取消與失敗訊息加入總耗時；取消／失敗附最後可見進度。訊息面板新增「複製紀錄」，等待順序化持久化 queue 後複製最近 100 筆 chronological TSV，不把每筆 progress 或 NDJSON 灌入 `app_message_log`。

沒有修改 KCT、審計規則、公開 action／payload／response shape、資料庫 schema、artifact manifest schema或固定範本檔案。

## 自動驗證

Release solution build：

```powershell
dotnet build src/JET/JET.slnx --no-restore --nologo -c Release
```

結果為 0 warning／0 error。

| Gate | 結果 | 證據與 SHA-256 |
|:---|:---|:---|
| 晚頁長文字、Step4 stream、Step1-1 legacy 樣式 | 4 pass／0 fail／0 skip | `artifacts/test-results/20260728-acceptance-repair-final2-targeted.trx`；`5D42ACCDC967B9D7E98E4E07EA954296FF74B559DDF1351B05212C4EE2F9B1EF` |
| 匯出／provider／frontend／architecture affected regression | 108 pass／0 fail／4 sandbox SQL-only skip | `artifacts/test-results/20260728-acceptance-repair-final2-regression.trx`；`6A95A6D08FC1F5E56F0FCC87B0C7A580CA9BEA45EC4F0F3FEB32D55664404637`。四項已由下列 sandbox 外 full suite 實跑銷帳 |
| Final Release full suite | 2,459 pass／0 fail／13 具名 skip，共 2,472 | `artifacts/test-results/20260728-acceptance-repair-final2-full-suite.trx`；`EC85473BD077B18B10315134E305627CF6607F422B1194218BBEA749AC7B284A`。本機 SQL Server 2022 Developer tests 實際執行，SQL availability skip 為 0 |
| 六報表 current-artifact journey | 1 pass／0 fail／0 skip | `artifacts/test-results/20260728-acceptance-repair-final2-six-report-evidence.trx`；`33A3E1938D0B8DA56EDB1769047D6EEE466046B45905463FBAFBE87940A03A3D` |
| Agent GUI `All` | 15／15 scenarios、289 actions、0 screenshots | `src/JET/tests/JET.Tests/TestResults/agent-gui-harness/20260728-065245535-All-0b054a742fe54fa5890a719237225e75.json`；`8B68FA65B5EA568E1F3108016A967A9164AE28E5C6ABF59E44F598D02DED680E`；結束後 `Status=inactive` |

Final full suite 的 13 項略過均為既有具名外部條件：7 項目前主機無法建立真實 filesystem link、4 項缺私有 `JET_PBC_DIR`、1 項缺明示 `JET_STEP41_BASELINE_ROOT`、1 項 frozen historical baseline。沒有 SQL availability skip、未知原因 skip 或 final failure。

757 張傳票的 production writer regression 精確要求 public page calls 為 0、stream calls 為 1、Step4 輸出 757 列。晚頁長文字測試另分別把 Step1 family、Step2、Step3／Step4 的長文字放在資料尾端，驗證一次來源枚舉後的完整欄寬與 no-wrap／no-indent／no-shrink。最後的獨立複審另抓出 Step1-1 第 14 列是 legacy 欄標、資料從第 15 列開始；修正後 direct Open XML regression 精確比對 B14:E14 與來源範本 style index 相同，並驗證 B15:E15 為單行資料樣式。

## 六報表與視覺 receipt

Final receipt root：

`<user-profile>\.codex\visualizations\2026\07\28\019fa614-9b9c-79e3-9ec8-726761c3bef7\acceptance-repair-final2-receipt`

同一次正式 handler journey 產生 6 本 current workbook、32 張工作表。Bundled artifact-tool 對每張完整 used range 執行 region 與 computed-style inspection，再逐張 render；32／32 PNG 均以 original detail 人工 read-back。

- 可見儲存格標準錯誤字串／`###`：0。
- computed-style records：977；nonzero indent：0；`shrinkToFit=true`：0。
- `wrapText=true`：227，均屬保留的 legacy 說明、方法學或表頭。
- WorkingPaper 動態資料範圍樣式 records：63；wrap／indent／shrink 違規：0。
- 32／32 render 沒有重要裁切、非預期重疊、缺圖或 hash placeholder。

Receipt hashes：

| 檔案 | SHA-256 |
|:---|:---|
| `evidence-manifest.json` | `15C0EC22BE8AFC9E3883ADDFBEE3F1D830755021679F81685134FC0625022169` |
| `render-inspection.json` | `DB23BA22AE2AEF5D34D2A50B71A9764EC34738577DE67CA5BCF79615D162BEAD` |
| `visual-review.json` | `BAAD2F4D0E0504BDAEF05390B89EBF3C9F7BBD18905913CE785B33BCAD20ABB1` |

AccountMapping 來源範本與輸出各有 83 個 workbook-level `=#REF!` defined names；來源範本 SHA-256 是 `20A592907609933D837C8A617FABD11BD046C7D271AE55522545AC4CB61444AB`。這是 byte-preserved legacy 定義，不是本修補新增的可見儲存格錯誤；因此 receipt 只宣稱 visible cell/display scan 為 0，不宣稱整個 package 沒有 legacy `#REF!` defined-name 文字。

完整索引、六本 workbook hashes、每張 inspection／render 路徑與所有資格說明在 `receipt-index.json`。

## FolderProfile 候選

- Raw publish：`artifacts/convergence/RawPublish-20260728-145315`
- 未由 agent 啟動的候選：`artifacts/convergence/FolderProfile-20260728-145315`
- Exact file set：26 檔，74,302,643 bytes；reparse point 0；raw／candidate 逐檔 hash 差異 0。
- `JET.exe`：73,162,575 bytes；SHA-256 `F30C9A2D303190F8D8FD19703CBFD9C89DA22669FD6F64685A8CEFC8032F588B`。
- 候選外 verifier 以 `RawPublish-20260728-145315\JET.exe` 作 executable reference 通過 current-source allowlist、exact file set、無 loose DLL／PDB／XML／runtime data／reparse point，以及 x64 PE32+ .NET single-file 檢查。Receipt：`artifacts/convergence/FolderProfile-20260728-145315-verifier.txt`，SHA-256 `EC48A8493680A8DB298A405C43087AAFFA21E611ABF357C9FF5384C59AD3BBFA`。

`FolderProfile-20260728-0845` 已被使用者失敗場取代；`FolderProfile-20260728-134350` 因 WorkingPaper 動態資料格仍保留範本換行樣式被取代；`FolderProfile-20260728-142903` 又因 Step1-1 第 14 列 legacy 欄標誤被視為動態資料格而被取代。三者都只保留為歷史 artifact，不得再作人工候選。

## 本輪未執行與停止點（2026-07-28）

本輪沒有 Microsoft Excel 桌面重驗、沒有重新執行原始 20,260,435 列案件、沒有企業 SQL Server／CFA／DLP 或私有 PBC，也沒有 commit、push、reset 或啟動候選。當時的人工重驗只能使用上列 exact local candidate；當時修補尚未 commit／push，跨主機 Git materialization 因此暫停。

本修補在 receipt 與候選完成後停止，沒有在同輪開始「2,026 萬列與 Excel 最終驗收」，也沒有處理 KCT 或新增功能。其後變更已由使用者推送，舊候選也已被 `FolderProfile-20260731-131350` 取代；最終人工結果見 `development-log.md`。

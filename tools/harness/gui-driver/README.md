# Gui 情境

`tools/verify.ps1 -Command Gui` 依序執行下列情境。建置方式、每個情境開在另一個 Windows 桌面的做法與執行時機，見
[`tools/README.md`](../../README.md) 的 Gui 一節。每個情境的操作上限、預期次數、逾時與截圖數只寫在 `tools/harness/lanes.json`，
需要的合成夾具寫在這個資料夾的 `DriverContracts.cs`。2026-11-06 從 `tools/README.md` 移到這裡，因為那份文件快到長度上限。

- `startup-smoke`：頁面、`JetApi`、`systemPing` 與專案選擇畫面，最後按「結束 JET」。
- `synthetic-sqlite-create`：只用鍵盤建立 SQLite 案件，核對畫面、`project.json`、`jet.db` 與操作人員。
- `case-to-working-paper`：從空白案件走到匯出底稿，再依序匯入科目配對、假日、補班日，附加與取代 GL、取代 TB，
  核對每一步清除與保留的狀態，以及只有新的底稿是現行版本。
- `case-to-working-paper-duckdb`：經過和 `case-to-working-paper` 相同，只在建案時選 DuckDB。兩個情境都核對建案與重開後的資料庫種類，
  以及案件資料夾裡只有那一種資料庫檔。
- `case-list-cleanup-cancel-export`：案件清單上資料庫檔不見的案件列成無法開啟並寫出原因，從清單刪掉它；接著開啟另一個案件，
  在第四步準備科目配對範本時按「取消作業」，核對畫面恢復可以操作、寫出已取消、沒有留下範本檔，再準備一次就成功。
- `mapping-required-sync`：改借方代碼後直接「重新確認配對」一次完成；清空並補回必填欄位，核對缺漏狀態與焦點。
- `edited-report-still-loads`：底稿在 JET 之外被改寫後，案件仍能載入、第六步有標示、匯出可用，支援日誌不洩漏檔名與路徑，過期與缺檔不算完成。
- `approval-mapping-modes`：兩種配對畫面的核准日方式與來源欄，含未提交提示、取消、還原與缺漏定位。
- `validation-auto-outputs`：驗證後自動產生兩份報告與科目配對範本，填入分類重新驗證，已填範本逐位元組保留。
- `filter-auditor-journey`：第五步從範本「借現金、貸非現金」開始，加條件、切換組、預覽、排序、保存並核對讀回文字。
- `filter-common-editing`：常用篩選條件卡片的保存與重開還原、舊版情境的處理，以及移除、取消、金額門檻、文字排除與自動命名。
- `feedback-workflow`：配對清單捲動、分類輸入、中文組字、取消、保存與重開，改配 TB 金額欄後仍可預篩選與匯出。
- `blank-details-recovery`：資料缺漏輔助檢視兩類明細的跨頁、排序與搜尋，以及搜尋失敗後保留、重試與零筆。
- `common-remap-recovery`：取消配對修改時情境仍在；移除必要欄位會清空已存情境，補回後重新儲存、驗證、匯出與重開。
- `upstream-change-after-export`：已匯出底稿後回頭修改前面步驟。取消與只改文字保留情境；重新確認 TB 配對或改每週休息日會清除情境，
  底稿仍能匯出；重新儲存後只勾一個情境匯出，重開仍是同一份結果。
- `authorized-list-recovery`：授權編製人員清單的手動選欄、取消、匯入失敗重試、預覽、移除、重開、再匯入與下游失效。
- `extended-conditions`：人工與自動補集、統計張數、日曆、金額尾數、人員清單與文字否定，保存五個情境、匯出與重開。
- `side-month-workflow`：窄視窗用子條件組與整張傳票條件組合借貸別與科目編號，設定月初月底天數，核對報告、底稿與人工旗標。
- `nested-voucher-workflow`：分類上層設定、巢狀條件、整張傳票條件與分類選取，空條件失敗後重試、保存、取消、重開與兩種報表。
- `legacy-form-workflow`：套用原工作簿轉入的四個情境範例，再加「金額尾數連續多個 0」，核對預覽、保存、兩種報表與重開。

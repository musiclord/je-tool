# Excel 讀寫規範的依據與核對表

這份是 [Excel 讀寫規範](excel-conventions.md) 的附表，列出 2026-11-16 實際讀過的微軟網頁、逐項核對的結果，
以及 JET 每一種 Excel 操作的程式位置；NumberingFormat Class 是 2026-11-20 補讀的，Review guidelines for customizing a number format 是 2026-11-24 補讀的。程式位置以 `src/JET/JET/Infrastructure/` 為起點，
行號原本是 2026-11-16 讀到的位置，之後改程式時更新了其中幾列，其他列的行號可能已經移動。

判定分成四種：

- 符合：做法和微軟網頁寫的規定一致。
- 不符合：做法和微軟網頁寫的規定不一致，或超出它寫的上限。
- 約定：微軟沒有規定，是 JET 自己的做法。
- 未能查證：本次沒有在微軟網頁讀到明確規定。

68 個核對項目裡，符合 43 項、不符合 3 項、約定 15 項、未能查證 7 項。不符合與未能查證的處理，見規範文末「已知要改善的地方」。

## 讀過的微軟網頁

| 網頁標題 | 用到的內容 |
|:---|:---|
| [Excel specifications and limits](https://support.microsoft.com/en-us/office/excel-specifications-and-limits-1672b34d-7043-467e-8e27-269d656771c3) | 列數、欄數、每格字元與換行、欄寬、列高、儲存格格式、字型、超連結、15 位精確度、日期範圍 |
| [Date systems in Excel](https://support.microsoft.com/en-us/excel/date-systems-in-excel) | 1900 與 1904 日期系統差 1,462 天 |
| [DATE function](https://support.microsoft.com/en-us/office/date-function-e36c0c8c-4104-49da-ab83-82328b832349) | 序列值 1 是 1900 年 1 月 1 日 |
| [Excel incorrectly assumes that the year 1900 is a leap year](https://learn.microsoft.com/en-us/troubleshoot/microsoft-365-apps/excel/wrongly-assumes-1900-is-leap-year) | Excel 把 1900 年當成閏年 |
| [DateTime.ToOADate Method](https://learn.microsoft.com/en-us/dotnet/api/system.datetime.tooadate) | OLE Automation 日期從 1899 年 12 月 30 日起算 |
| [Decimal Constructor](https://learn.microsoft.com/en-us/dotnet/api/system.decimal.-ctor) | 從 double 建立 decimal；現行頁面沒有寫保留幾位 |
| [Keeping leading zeros and large numbers](https://support.microsoft.com/en-us/office/keeping-leading-zeros-and-large-numbers-1bf7b935-36e1-4985-842f-5dfa51f85fe7) | 15 位有效數字、前導零、改存成文字 |
| [Number format codes in Excel for Mac](https://support.microsoft.com/en-us/office/number-format-codes-5026bbd6-04bc-48cd-bf33-80f18b4eae68) | 格式碼分四段、日期時間代碼、h 之後的 m 是分鐘 |
| [Review guidelines for customizing a number format](https://support.microsoft.com/en-us/office/review-guidelines-for-customizing-a-number-format-c0a1d1fa-d3f4-4018-96b7-9c9354dd99f5) | Windows 版 Excel 的同一套規則：格式碼最多四段，依序是正數、負數、零與文字；日期與時間代碼 |
| [Rename a worksheet](https://support.microsoft.com/en-us/office/rename-a-worksheet-3f1f7148-ee83-404d-8ef0-9ff99fbad1f9) | 工作表名稱規則 |
| [Protect a worksheet](https://support.microsoft.com/en-us/office/protect-a-worksheet-3179efdb-1285-4d49-a9c3-f4ca36276de6) | 預設全部鎖定、預設允許選取、保護不是安全功能 |
| [Worksheet compatibility issues](https://support.microsoft.com/en-us/office/worksheet-compatibility-issues-f9c80c5b-5afc-40da-a841-b888746abd40) | xls 的 65,536 列、256 欄與 4,000 種格式 |
| [Structure of a SpreadsheetML document](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/structure-of-a-spreadsheetml-document) | `sheetId` 在活頁簿內唯一 |
| [Cell Class](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.cell) | `inlineStr` 的格子只能有 `is` |
| [CellValues Enum](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.cellvalues) | `t` 的各個值；`d` 要 Office 2010 以後 |
| [SheetProtection Class](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.sheetprotection) | 保護屬性的名稱與範例 |
| [IgnoredError Class](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.ignorederror) | 「以文字儲存的數字」提示與它的寫入時機 |
| [[MS-OI29500] c (Cell)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/2fd4e47f-0965-4c60-95bd-cff980b6c325) | `s` 指向 `cellXfs`、範圍 0 到 65490；沒有 `r` 時的位置 |
| [[MS-OI29500] v (Cell Value)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/b9d01770-b85b-4fb2-a387-f5811719a1af) | 最多 32,767 字元、各型別的值、錯誤值清單、公式的快取值 |
| [[MS-OI29500] t (Text)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/82ad64a7-401b-4f34-9319-3600b16b2791) | `xml:space`、最多 32,767 字元 |
| [[MS-OI29500] ST_Xstring (Escaped String)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/d34ae755-c53f-4a44-a363-c6dd3ee018a4) | `_xHHHH_` 跳脫 |
| [[MS-OI29500] numFmt (Number Format)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/17d11129-219b-4e2c-88db-45844d21e528) | 內建格式的 en-US 定義、格式碼短於 255 字元 |
| [[MS-OI29500] numFmts (Number Formats)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/c6046ec6-f96d-4609-98e4-0dd549292955) | 三段格式的解讀、最多 206 個自訂格式 |
| [NumberingFormat Class](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.numberingformat) | 內建數字格式表與繁中、簡中、日文、韓文版的地區表；部分編號依開檔程式的介面語言解讀（2026-11-20 查閱） |
| [[MS-OI29500] xf (Format)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/68362a4b-5589-4504-b566-e8154dce1de3) | `applyNumberFormat`、`applyProtection` 的意義 |
| [[MS-OI29500] ST_FillId (Fill Id)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/1053b257-53e4-4ce5-a8cb-d5525d9a6100) | 底色索引 0 與 1 保留 |
| [[MS-OI29500] definedName (Defined Name)](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/16c3c118-f358-493d-a99f-4c85ca834c00) | Office 用 `localSheetId` 指出名稱定義在哪一張工作表 |
| [[MS-OI29500] sheetProtection](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/15197a02-26f7-4a37-ab9c-7de4d4894ea5) | 密碼雜湊的寫法 |
| [[MS-OFFMACRO2] CT_Worksheet](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-offmacro2/c1b21fb9-e0c0-4a06-9bee-ce94e60bd823) | 工作表元素的順序；這頁寫的是巨集工作表 |

沒有讀到或沒有採用的來源：

- 工作表元素的完整順序，本次只在巨集工作表的頁面讀到；一般工作表的順序沒有在微軟網頁讀到，核對時以這頁為準。
- ISO 標準的內建數字格式全表與保護屬性的預設值，本次沒有在微軟網頁讀到。
- `localSheetId` 從 0 起算，本次沒有在微軟網頁讀到，依範本裡的寫法。
- 資料驗證字面清單 255 字的上限，只在社群問答看到，不採用。
- `Protect a worksheet` 另一個舊網址回應 404，改用上表的網址。
- [MS-XLSX] 這次沒有用到。

## 核對結果

### 儲存格型別

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 讀取依 `t` 取值：`s`、`inlineStr`、`str`、`e`、`b`、`d`、`n` | CellValues Enum；v (Cell Value) | 符合 |
| 沒有 `r` 的格子接著前一格的下一欄 | c (Cell) | 符合 |
| 寫文字用 inline string，不建共用字串表 | v (Cell Value)；Cell Class | 符合 |
| 布林讀成 true、false；Step 4-1 寫回 1、0 | v (Cell Value) | 符合 |
| 錯誤值照原文讀入 | v (Cell Value) | 符合 |
| 共用字串索引不是有效索引時明確失敗，寫出工作表與儲存格 | v (Cell Value) 要求是有效索引 | 符合 |
| 沒有值的格子不寫 `t`，INF 擴充列也一樣，不留沒有 `is` 的 `inlineStr` 格子 | Cell Class 說 `inlineStr` 的格子只能有 `is` | 符合 |

### 日期與時間

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 1904 系統加 1,462 天 | Date systems in Excel | 符合 |
| 讀取時照 Excel 的規則換算序列值，1900 年 1、2 月的日期和 Excel 顯示的一致 | DATE function；Excel incorrectly assumes that the year 1900 is a leap year；DateTime.ToOADate Method | 符合 |
| 寫出時 1900 年 3 月 1 日以前的日期寫成文字，不寫序列值 | DATE function；Excel incorrectly assumes that the year 1900 is a leap year；裁定 2026-11-17-19 | 約定 |
| 內建 14 到 22、45 到 47 的分類 | numFmt 只列了 14、22、47 的 en-US 定義 | 未能查證 |
| 內建 27 到 36、50 到 58 一律當日期；繁中版只有時間的編號，值小於 1 時交出序列值的文字 | NumberingFormat Class：繁中、簡中、日文、韓文版的 32、33 只有時間，繁中版的 34、35、52、53、55、56 也是時間 | 不符合 |
| 自訂格式碼找 y、m、d、h、s，h 之後的 m 是分鐘 | Number format codes | 符合 |
| 看 `numFmtId`，不看 `applyNumberFormat` | xf (Format) | 符合 |
| 日期寫成 `yyyy-MM-dd` 文字 | 裁定 2026-11-16-07 | 約定 |
| Step 2、Step 4-1 寫序列值加日期格式 | DATE function | 符合 |
| 寫序列值的活頁簿是 1900 系統 | Date systems in Excel | 符合 |

### 數字

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 數值用 InvariantCulture 寫成 double 的文字 | v (Cell Value) | 符合 |
| 超過 15 位有效數字時不檢查 | Excel specifications and limits | 約定 |
| 讀取時 double 轉 decimal 保留的位數 | Decimal Constructor 沒有寫 | 未能查證 |
| 原始明細照匯入文字寫，保留前導零與長代碼 | Keeping leading zeros and large numbers | 符合 |
| 讀取接受科學記號 | v (Cell Value) | 符合 |
| 格式碼短於 255 字元，自訂格式數量有上限 | numFmt；numFmts | 符合 |
| 範本的會計與負數格式照留 | numFmts | 符合 |
| `mm-dd-yy`、`h:mm:ss` 建成自訂格式，編號從 164 起 | 164 起的規定本次沒有讀到 | 約定 |
| 文字格子套數字格式；不寫 `ignoredErrors` | Number format codes；IgnoredError Class | 約定 |

### 規格上限

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 最多 1,048,576 列、16,384 欄 | Excel specifications and limits | 符合 |
| 條件說明切段，每格不超過 32,767 字與 253 個換行 | Excel specifications and limits；t (Text) | 符合 |
| 其他文字以跳脫後的字數檢查，超過 32,767 字時寫到上限並註明已截斷 | Excel specifications and limits；t (Text) | 符合 |
| 名稱 31 字、禁用字元、頭尾單引號 | Rename a worksheet | 符合 |
| 不擋 `History`；名稱都由程式固定 | Rename a worksheet | 約定 |
| 欄寬 8 到 255 | Excel specifications and limits | 符合 |
| 列高最高 409 點 | Excel specifications and limits | 符合 |
| 樣式與字型數量有上限 | Excel specifications and limits；c (Cell) | 符合 |
| 不新增超連結 | Excel specifications and limits | 符合 |
| 只合併固定的標題列 | 規格頁沒有合併的上限 | 約定 |

### 公式與資料驗證

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 不寫公式；範本沒有公式，`calcPr` 只有 `calcId` | 沒有相關規定 | 約定 |
| 讀取公式只取快取值 | v (Cell Value) | 符合 |
| 沒有快取值時當空白，匯入與預覽提醒格數與第一格 | 沒有相關規定 | 約定 |
| 資料驗證 `formula1` 的寫法 | 本次沒有讀到 | 未能查證 |

JET 與範本寫出的公式：沒有任何儲存格公式。只有資料驗證的清單來源：科目配對範本 C 欄的 `List!$A$2:$A$1048576`，
以及範本原有的「相符,不符合,N/A」「V,NA」「v,no,n/a」「Yes,No」四種字面清單。

### 工作表保護

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| `sheet="1"`、`selectLockedCells="0"`、`selectUnlockedCells="0"` | SheetProtection Class；Protect a worksheet | 符合 |
| `objects="1"`、`scenarios="1"` 的意義與沒寫的屬性 | SheetProtection Class 的屬性說明與範例說法相反 | 未能查證 |
| 不設密碼；保護不是安全功能 | Protect a worksheet | 符合 |
| 用樣式的 `protection` 與 `applyProtection` 控制鎖定 | xf (Format) | 符合 |
| 預設鎖定，填寫格用不鎖定的樣式 | Protect a worksheet | 符合 |
| 保護屬性寫了五份 | 沒有相關規定 | 約定 |

### 套件結構

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 組件與關聯由 SDK 建立，`sheetId` 唯一 | Structure of a SpreadsheetML document | 符合 |
| 寫出元素的順序 | CT_Worksheet，巨集工作表的頁面 | 符合 |
| 串流疊寫補上的保護排在 `sheetCalcPr` 之後 | CT_Worksheet | 符合 |
| 範本原有的共用字串表照留 | v (Cell Value) | 符合 |
| 底色 0、1 保留，`s` 指向 `cellXfs` | ST_FillId；c (Cell) | 符合 |
| CR、控制字元、單獨 surrogate 與開頭底線的跳脫 | ST_Xstring | 符合 |
| 大寫 X 的 `_X0041_` 也跳脫 | ST_Xstring 只寫了小寫的形式 | 未能查證 |
| 文字一律加 `xml:space="preserve"` | t (Text) | 符合 |
| 續頁和原頁共用繪圖與印表機設定組件 | 本次沒有讀到 | 未能查證 |
| 刪除工作表時拿掉屬於它的定義名稱，後面工作表的 `localSheetId` 減 1 | definedName (Defined Name) | 符合 |
| 資料驗證範圍先讀出結束列再延伸 | 沒有相關規定 | 約定 |
| 範本留有值是 `#REF!` 的定義名稱 | 沒有相關規定 | 約定 |

### 讀取時的風險

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 共用公式不影響，只讀 `v` | v (Cell Value) | 符合 |
| 富文字接起 `r` 的 `t`，略過 `rPh` | Cell Class | 符合 |
| 由底稿匯入欄位用 `InnerText`，讀進 `rPh` | Cell Class 說明注音只是發音提示 | 不符合 |
| 隱藏列與篩選結果全部讀入 | 沒有相關規定 | 約定 |
| 合併儲存格照一般格子讀 | 本次沒有讀到 | 未能查證 |
| 文字去掉前後空白 | 沒有相關規定 | 約定 |

### xls

| 項目 | 依據 | 判定 |
|:---|:---|:---|
| 65,536 列、256 欄，JET 只讀不寫 | Worksheet compatibility issues | 符合 |
| 日期照 Excel 顯示的年月日存，換不出日期時存序列值的文字 | 沒有相關規定 | 約定 |
| 日期最晚到 9999-12-31 | Excel specifications and limits | 符合 |
| ExcelDataReader 把 1900 日期系統的序列值 60 換成和 59 同一天的 1900-02-28 | Excel incorrectly assumes that the year 1900 is a leap year | 不符合 |

## JET 的 Excel 操作

共 41 種：讀取 13 種，寫出 28 種。理由欄寫程式註解、`jet-guide.md` 或裁定裡寫的原因，沒寫的標「未寫」。

| 操作 | 位置 | 理由 |
|:---|:---|:---|
| 逐列串流讀 xlsx 與 xlsm | `FileIO/OpenXmlSaxTableReader.cs` 第 11 到 23 行、第 309 行起 | 一百萬列與小檔走同一段程式，不載入 DOM |
| 找標頭列、補佔位欄 | 同檔第 14 到 19 行 | 不丟資料 |
| 讀共用字串與富文字，略過注音 | 同檔第 469 到 519 行、第 665 到 689 行 | 注音標示不屬於格子內容；編號找不到文字時檔案已經損壞，不當成空白讀進來 |
| 依 `t` 取值 | 同檔第 549 到 606 行 | 金額可能存成文字，讀取時統一轉成文字 |
| 數值轉成 decimal 文字 | 同檔第 655 到 660 行、第 691 到 709 行 | 去掉浮點數的尾數誤差 |
| 讀樣式表的數字格式 | 同檔第 521 到 542 行 | 未寫 |
| 記下有公式、卻沒有存計算結果的格子 | 同檔第 103 到 114 行、第 364 到 367 行 | 讀到的值不變，匯入完成時提醒審計員用 Excel 存檔後再匯入 |
| 判斷日期與時間格式 | `FileIO/ExcelDateFormatDetector.cs` 第 19 到 130 行 | 內建編號照 ECMA-376 §18.8.30 |
| 序列值轉日期 | `FileIO/ExcelSerialDate.cs`；`FileIO/OpenXmlSaxTableReader.cs` 第 625 到 645 行、第 718 到 723 行 | 照 Excel 顯示的年月日換算（裁定 2026-11-20-02）；Excel 顯示不出日期時交出序列值，確認配對時判斷；繁中版只有時間的內建格式，值小於 1 時不換成日期，不同的時間才分得出來 |
| 還原 `_xHHHH_` | `Export/ExcelText.cs` 第 138 到 166 行 | 審計員在 Excel 看到什麼就讀到什麼 |
| 讀 xls | `FileIO/BinaryExcelTableReader.cs`、`FileIO/NativeTabularValue.cs` | 值的規則和 xlsx 一致 |
| 由底稿匯入欄位 | `FileIO/OpenXmlMappingMetadataReader.cs` | 帶入第三步的欄位配對 |
| 讀 JET 中繼資料 | `Export/ReportWorkbookMetadataCodec.cs` | 同上 |
| 寫文字格子與跳脫 | `Export/ExcelText.cs` 第 18 到 136 行、第 266 到 304 行 | 來源含控制字元也能產生報告（裁定 2026-10-21-10）；超過每格上限的文字寫到上限並註明，匯出不失敗 |
| 寫數值格子 | `Export/ReportSheetWriter.cs` 第 178 行、`Export/WorkingPaperWriter.cs` 第 991 行 | 未寫 |
| 日期寫成文字 | 例如 `Export/LegacyReportWriter.Validation.cs` 第 274 行 | 裁定 2026-11-16-07 |
| 日期與時間寫成序列值 | `FileIO/ExcelSerialDate.cs` 第 84 到 102 行；`Export/WorkingPaperWriter.Step2To41.cs` 第 899 到 962 行、第 1698 到 1723 行 | 只把存成 yyyy-MM-dd、1900-03-01 以後的日期寫成序列值，PBC 的值不被改掉（裁定 2026-11-20-02） |
| 布林寫成 1 或 0 | `Export/WorkingPaperWriter.Step2To41.cs` 第 858 到 865 行 | 舊版明細是數值 1 或 0 |
| 建樣式表 | `Export/LegacyReportStyles.cs`、`Export/WorkingPaperStyles.cs`、`Export/FormalFieldInfoAppearance.cs` | 照舊版 JET 的樣式 |
| 修補範本樣式與數字格式 | `Export/ReportTemplatePackage.cs` 第 346 到 431 行、第 538 行起 | 同一組修改只追加一次，數量有上限 |
| 鎖定與不鎖定樣式 | `Export/DirectTemplateWorkbookEditor.cs` 第 79 到 88 行 | 照舊版的鎖定範圍 |
| 統一字型 | `Export/FormalWorkbookTypography.cs` 第 36 到 66 行 | 未寫 |
| 估算欄寬 | `Export/ExcelDisplayWidth.cs` | Open XML 不能執行 AutoFit，照舊版用最寬的內容 |
| 設定列高 | `Export/FormalWorkbookTypography.cs` 第 17 到 34 行 | 微軟正黑體比 Arial 高（裁定 2026-10-29-08）；最高到 Excel 的列高上限 409 點 |
| 工作表視圖與凍結窗格 | `Export/ReportSheetWriter.cs` 第 380 到 416 行、`Export/WorkingPaperWriter.cs` 第 1345 到 1367 行 | 避免高 DPI 的 Excel 把列高折半 |
| 合併儲存格 | `Export/ReportSheetWriter.cs` 第 322 到 331 行、`Export/WorkingPaperWriter.cs` 第 1244 行 | 未寫 |
| 資料驗證清單 | `Export/AccountMappingTemplateWriter.cs` 第 303 到 307 行、`Export/LegacyReportWriter.Inf.cs` 第 356 到 365 行 | 裁定 2026-11-09-13、2026-11-12-34 |
| 延伸資料驗證範圍 | `Export/ReportSheetWriter.cs` 第 241 到 267 行的 `ExtendSqref` | 只延伸結束列是範本最後一列的範圍 |
| 工作表保護 | 規範文末「重複的寫法與常數」列的五處 | 照舊版的範圍（裁定 2026-11-09-14） |
| 頁面設定 | `Export/ReportSheetWriter.cs` 第 352 到 379 行、`Export/WorkingPaperWriter.cs` 第 1286 行 | 未寫 |
| 工作表與續頁命名 | `Export/ExcelWorksheetConstraints.cs` 第 41 到 70 行、`Export/LegacyReportWriter.cs` 第 570 到 579 行 | 未寫 |
| 列欄上限與換頁 | `Export/ExcelWorksheetConstraints.cs` 第 11 到 39 行、第 73 到 109 行 | 不寫出超過上限的列號或欄號 |
| 超長條件說明切段 | `Export/FilterConditionExcelText.cs` | 保留完整文字 |
| 複製範本後疊寫 | `Export/ReportTemplatePackage.cs`、`Export/DirectTemplateWorkbookEditor.cs`、`Export/DirectTemplateWorksheetOverlay.cs`、`Export/DirectTemplateStreamingSheetWriter.cs` | 底稿的文字、下拉選單與外觀盡量照範本 |
| 新增、複製與刪除工作表 | `Export/DirectTemplateWorkbookEditor.cs` 第 189 到 315 行、`Export/LegacyReportWriter.cs` 第 207 行 | 未寫 |
| 刪除工作表時調整定義名稱 | `Export/DirectTemplateWorkbookEditor.cs` 第 228 到 251 行 | `localSheetId` 是工作表的位置，刪除工作表後要跟著改 |
| 對齊定義名稱 | `Export/WorkingPaperWriter.Template.cs` 第 572 到 657 行 | 未寫 |
| 寫隱藏的中繼資料工作表 | `Export/ReportWorkbookMetadataCodec.cs` 第 189 行 | 供第三步由底稿匯入欄位 |
| 串流寫出到暫存檔再改名 | `Export/ReportSheetWriter.cs`；`jet-guide.md` 第 7 節「檔案、發布與版本」 | 產生大型工作簿 |
| 工作表內容直接壓縮進輸出檔 | `Export/FormalWorkbookArchive.cs`、`Export/ReportTemplatePackage.cs` 的 `FillDirectCoreAsync` | OpenXML SDK 的可寫活頁簿把改過的項目整份留在記憶體，單一項目到約 2 GB 就失敗 |
| 寫入失敗時記下位置 | `Export/ExcelText.cs` 第 183 到 217 行 | 只記位置與碼位，不記文字 |
| 匯出失敗或取消時放掉工作表寫入器 | `Export/ExportResources.cs`；各工作表寫入器的 `Dispose` | 釋放時只放掉壓縮檔項目與暫存檔，不補寫結尾；這時再寫入可能丟出新的例外，蓋掉原本的失敗原因。工作表寫完要呼叫 `CloseAndSummarize` 關閉，這是 JET 的約定 |

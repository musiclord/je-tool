# Excel 讀寫規範的依據與核對表

這份是 [Excel 讀寫規範](excel-conventions.md) 的附表，列出 2026-11-16 實際讀過的微軟網頁、逐項核對的結果，
以及 JET 每一種 Excel 操作的程式位置；NumberingFormat Class 是 2026-11-20 補讀的，Review guidelines for customizing a number format 是 2026-11-24 補讀的，
共用字串表、`dimension` 與關聯路徑的四頁是 2026-11-25 補讀的。程式位置以 `src/JET/JET/Infrastructure/` 為起點，
行號原本是 2026-11-16 讀到的位置，之後改程式時更新了其中幾列，其他列的行號可能已經移動。

判定分成四種：

- 符合：做法和微軟網頁寫的規定一致。
- 不符合：做法和微軟網頁寫的規定不一致，或超出它寫的上限。
- 約定：微軟沒有規定，是 JET 自己的做法。
- 未能查證：本次沒有在微軟網頁讀到明確規定。

76 個核對項目裡，符合 46 項、不符合 3 項、約定 19 項、未能查證 8 項。不符合與未能查證的處理，見規範文末「已知要改善的地方」。

核對過、沒有發現問題的主題：跳脫格式、工作表名稱規則、列數與欄數上限、欄寬上限、底色保留索引、樣式與字型數量、
`sheetId`、保護不設密碼與可選取的設定、鎖定樣式的 `applyProtection`、xls 的大小上限，以及工作表元素順序。

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
| [Working with the shared string table](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/working-with-the-shared-string-table) | Excel 存檔時一定建立共用字串表，不建立也合法；一份套件只有一個；範例 `count="6" uniqueCount="5"` |
| [SharedStringTable Class，Office 2010 版](https://learn.microsoft.com/en-us/previous-versions/office/developer/office-2010/cc840863(v=office.14)) | `count` 是活頁簿裡文字的總數，不算數字；`uniqueCount` 是共用字串表裡不重複的字串數 |
| [SheetDimension Class](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.sheetdimension) | `dimension` 是工作表的使用範圍，可以省略 |
| OPC 的關聯說明，標題未記下：[relationships-overview](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/opc/relationships-overview) | 關聯的相對 URI 以來源零件為基準解析 |

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
| 三份報告的文字寫進共用字串表；INF 報告、底稿、科目配對範本與 JET 讀回用的隱藏紀錄寫 inline string | Working with the shared string table；v (Cell Value)；Cell Class | 符合 |
| 布林讀成 true、false；Step 4-1 寫回 1、0 | v (Cell Value) | 符合 |
| 錯誤值照原文讀入 | v (Cell Value) | 符合 |
| 共用字串索引不是有效索引時明確失敗，寫出工作表與儲存格 | v (Cell Value) 要求是有效索引 | 符合 |
| 沒有值的格子不寫 `t`，INF 擴充列也一樣；不留沒有 `is` 的 `inlineStr` 格子，`s` 的格子一定寫編號 | Cell Class 說 `inlineStr` 的格子只能有 `is`；v (Cell Value) 要求是有效索引 | 符合 |

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
| 組件由 SDK 建立，`sheetId` 唯一 | Structure of a SpreadsheetML document | 符合 |
| 三份報告的關聯目標用以來源零件為基準的相對路徑，收尾時改寫 SDK 從根目錄起算的寫法 | relationships-overview | 符合 |
| 三份報告每張工作表寫 `dimension`，範圍等於寫出的格子 | SheetDimension Class | 符合 |
| XML 不帶 BOM | 沒有相關規定，照 Excel 存檔的樣子 | 約定 |
| 主要命名空間用預設命名空間，不加前綴 `x` | 沒有相關規定，照 Excel 存檔的樣子 | 約定 |
| 文字的換行寫 LF；屬性裡的換行與 Tab 寫成字元參照 | 沒有相關規定，照 Excel 存檔的樣子 | 約定 |
| 寫出元素的順序 | CT_Worksheet，巨集工作表的頁面 | 符合 |
| 串流疊寫補上的保護排在 `sheetCalcPr` 之後 | CT_Worksheet | 符合 |
| 範本原有的共用字串項目照留在前面，新的接在後面 | v (Cell Value)；Working with the shared string table | 符合 |
| `count` 是參照共用字串表的格數，`uniqueCount` 是項目數 | SharedStringTable Class | 符合 |
| 只記住 256 字以內、估計 64 MiB 以內的文字，超過後同樣的文字可能各存一份 | 沒有相關規定 | 約定 |
| Excel 是否接受共用字串表裡內容相同的項目 | 本次沒有讀到 | 未能查證 |
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

共 46 種：讀取 13 種，寫出 33 種。理由欄寫程式註解、`jet-guide.md` 或裁定裡寫的原因，沒寫的標「未寫」。

| 操作 | 位置 | 理由 |
|:---|:---|:---|
| 逐列串流讀 xlsx 與 xlsm | `FileIO/OpenXmlSaxTableReader.cs` 第 11 到 23 行、第 309 行起 | 一百萬列與小檔走同一段程式，不載入 DOM |
| 找標頭列、補佔位欄 | 同檔第 14 到 19 行 | 不丟資料 |
| 讀共用字串與富文字，略過注音 | 同檔第 469 到 519 行、第 654 到 678 行 | 注音標示不屬於格子內容；編號找不到文字時檔案已經損壞，不當成空白讀進來 |
| 依 `t` 取值 | 同檔第 549 到 610 行 | 金額可能存成文字，讀取時統一轉成文字；`t="d"` 的日期文字照原文讀，不補沒寫的部分（裁定 2026-11-24-28） |
| 數值轉成 decimal 文字 | 同檔第 644 到 648 行、第 680 到 698 行 | 去掉浮點數的尾數誤差 |
| 讀樣式表的數字格式 | 同檔第 521 到 542 行 | 未寫 |
| 記下有公式、卻沒有存計算結果的格子 | 同檔第 103 到 114 行、第 364 到 367 行 | 讀到的值不變，匯入完成時提醒審計員用 Excel 存檔後再匯入 |
| 判斷日期與時間格式 | `FileIO/ExcelDateFormatDetector.cs` 第 19 到 130 行 | 內建編號照 ECMA-376 §18.8.30 |
| 序列值轉日期 | `FileIO/ExcelSerialDate.cs`；`FileIO/OpenXmlSaxTableReader.cs` 第 612 到 634 行、第 707 到 712 行 | 照 Excel 顯示的年月日換算（裁定 2026-11-20-02）；Excel 顯示不出日期時交出序列值，確認配對時判斷；繁中版只有時間的內建格式，值小於 1 時不換成日期，不同的時間才分得出來 |
| 還原 `_xHHHH_` | `Export/ExcelText.cs` 第 256 到 284 行 | 審計員在 Excel 看到什麼就讀到什麼 |
| 讀 xls | `FileIO/BinaryExcelTableReader.cs`、`FileIO/NativeTabularValue.cs` | 值的規則和 xlsx 一致 |
| 由底稿匯入欄位 | `FileIO/OpenXmlMappingMetadataReader.cs` | 帶入第三步的欄位配對 |
| 讀 JET 中繼資料 | `Export/ReportWorkbookMetadataCodec.cs` | 同上 |
| 寫文字格子與跳脫 | `Export/ExcelText.cs` 第 12 到 255 行、第 384 到 422 行 | 來源含控制字元也能產生報告（裁定 2026-10-21-10）；超過每格上限的文字寫到上限並註明，匯出不失敗 |
| 寫數值格子 | `Export/ReportSheetWriter.cs` 第 197 行、`Export/WorkingPaperWriter.cs` 第 991 行 | 未寫 |
| 日期寫成文字 | 例如 `Export/LegacyReportWriter.Validation.cs` 第 274 行 | 裁定 2026-11-16-07 |
| 日期與時間寫成序列值 | `FileIO/ExcelSerialDate.cs` 第 84 到 102 行；`Export/WorkingPaperWriter.InfSamplingAndHighRisk.cs` 第 902 到 953 行、第 1616 到 1641 行 | 只把存成 yyyy-MM-dd、1900-03-01 以後的日期寫成序列值，PBC 的值不被改掉（裁定 2026-11-20-02） |
| 布林寫成 1 或 0 | `Export/WorkingPaperWriter.InfSamplingAndHighRisk.cs` 第 860 到 867 行 | 舊版明細是數值 1 或 0 |
| 建樣式表 | `Export/LegacyReportStyles.cs`、`Export/WorkingPaperStyles.cs`、`Export/FormalFieldInfoAppearance.cs` | 照舊版 JET 的樣式 |
| 修補範本樣式與數字格式 | `Export/ReportTemplatePackage.cs` 第 346 到 431 行、第 538 行起 | 同一組修改只追加一次，數量有上限 |
| 鎖定與不鎖定樣式 | `Export/DirectTemplateWorkbookEditor.cs` 第 79 到 88 行 | 照舊版的鎖定範圍 |
| 統一字型 | `Export/FormalWorkbookTypography.cs` 第 36 到 66 行 | 未寫 |
| 估算欄寬 | `Export/ExcelDisplayWidth.cs` | Open XML 不能執行 AutoFit，照舊版用最寬的內容 |
| 設定列高 | `Export/FormalWorkbookTypography.cs` 第 17 到 34 行 | 微軟正黑體比 Arial 高（裁定 2026-10-29-08）；最高到 Excel 的列高上限 409 點 |
| 工作表視圖與凍結窗格 | `Export/ReportSheetWriter.cs` 第 402 到 439 行、`Export/WorkingPaperWriter.cs` 第 1345 到 1367 行 | 避免高 DPI 的 Excel 把列高折半 |
| 合併儲存格 | `Export/ReportSheetWriter.cs` 第 343 到 352 行、`Export/WorkingPaperWriter.cs` 第 1244 行 | 未寫 |
| 資料驗證清單 | `Export/AccountMappingTemplateWriter.cs` 第 303 到 307 行、`Export/LegacyReportWriter.Inf.cs` 第 356 到 365 行 | 裁定 2026-11-09-13、2026-11-12-34 |
| 延伸資料驗證範圍 | `Export/ReportSheetWriter.cs` 第 261 到 287 行的 `ExtendSqref` | 只延伸結束列是範本最後一列的範圍 |
| 工作表保護 | 規範文末「重複的寫法與常數」列的五處 | 照舊版的範圍（裁定 2026-11-09-14） |
| 頁面設定 | `Export/ReportSheetWriter.cs` 第 373 到 396 行、`Export/WorkingPaperWriter.cs` 第 1286 行 | 未寫 |
| 工作表與續頁命名 | `Export/ExcelWorksheetConstraints.cs` 第 41 到 70 行、`Export/LegacyReportWriter.cs` 第 570 到 579 行 | 未寫 |
| 列欄上限與換頁 | `Export/ExcelWorksheetConstraints.cs` 第 11 到 39 行、第 73 到 109 行 | 不寫出超過上限的列號或欄號 |
| 超長條件說明切段 | `Export/FilterConditionExcelText.cs` | 保留完整文字 |
| 複製範本後疊寫 | `Export/ReportTemplatePackage.cs`、`Export/DirectTemplateWorkbookEditor.cs`、`Export/DirectTemplateWorksheetOverlay.cs`、`Export/DirectTemplateStreamingSheetWriter.cs` | 底稿的文字、下拉選單與外觀盡量照範本 |
| 新增、複製與刪除工作表 | `Export/DirectTemplateWorkbookEditor.cs` 第 189 到 315 行、`Export/LegacyReportWriter.cs` 第 224 行 | 未寫 |
| 刪除工作表時調整定義名稱 | `Export/DirectTemplateWorkbookEditor.cs` 第 228 到 251 行 | `localSheetId` 是工作表的位置，刪除工作表後要跟著改 |
| 對齊定義名稱 | `Export/WorkingPaperWriter.Template.cs` 第 572 到 657 行的 `AlignDirectTemplateSheets` | 未寫原因。做法是先依目前的位置記下每個名稱屬於哪一張工作表，排好後再改成那張工作表的新位置 |
| 寫隱藏的中繼資料工作表 | `Export/ReportWorkbookMetadataCodec.cs` 第 136 到 198 行 | 供第三步由底稿匯入欄位；三份報告的這張表也寫 `dimension` |
| 串流寫出到暫存檔再改名 | `Export/ReportSheetWriter.cs`；`jet-guide.md` 第 7 節「檔案、發布與版本」 | 產生大型工作簿 |
| 工作表內容直接壓縮進輸出檔 | `Export/FormalWorkbookArchive.cs`、`Export/ReportTemplatePackage.cs` 的 `FillDirectCoreAsync` | OpenXML SDK 的可寫活頁簿把改過的項目整份未壓縮地留在記憶體，等關閉時才壓縮寫出；2026-11-17 用合成資料實測，單張 76 萬列、每列約 3 KB 時，寫到約 2 GB 就丟出「Stream was too long」 |
| 文字寫進共用字串表 | `Export/ExcelText.cs` 第 50 到 163 行；`Export/WorkbookSharedStrings.cs`；`Export/FormalWorkbookArchive.cs` 第 74 到 116 行、第 237 到 385 行 | 和 Excel 存檔時相同（裁定 2026-11-25-01）；JET 讀回用的隱藏紀錄留 inline string，讀回時不必載入整份共用字串表 |
| 寫出工作表的 XML | `Export/SpreadsheetXmlWriter.cs`；`Export/FormalWorkbookArchive.cs` 第 118 到 150 行的 `OpenWorksheetContent` 與 `OpenWorksheetWriter` | 和 Excel 存檔時相同；Open XML SDK 的元素一律用前綴 `x` 寫出主要命名空間 |
| 收尾時整理其餘零件 | `Export/FormalWorkbookArchive.cs` 第 152 到 235 行的 `Complete`、第 387 到 496 行 | SDK 新增零件時把關聯寫成從根目錄起算，存關聯與內容類型時帶 BOM，用 DOM 存回的零件加前綴 `x`，都沒有設定能改 |
| 寫出 `dimension` | `Export/SheetUsedRange.cs`；`Export/ReportSheetWriter.cs` 第 100 到 106 行、第 224 到 230 行、第 289 到 292 行；`Export/DirectTemplateStreamingSheetWriter.cs` 第 80 行、第 115 行起、第 241 行 | 壓縮檔的項目寫過不能回頭改，所以開頁時宣告範圍，寫列與關閉時核對 |
| 寫入失敗時記下位置 | `Export/ExcelText.cs` 第 183 到 217 行 | 只記位置與碼位，不記文字 |
| 匯出失敗或取消時放掉工作表寫入器 | `Export/ExportResources.cs`、`Export/ExportSpoolFile.cs`；各工作表寫入器的 `Dispose` | 匯出用的暫存檔都由 `ExportSpoolFile` 建立（裁定 2026-11-24-36）。釋放時只放掉壓縮檔項目與暫存檔，不補寫結尾；這時再寫入可能丟出新的例外，蓋掉原本的失敗原因。工作表寫完要呼叫 `CloseAndSummarize` 關閉，這是 JET 的約定 |

## 規範條文的補充細節

規範裡寫「見附表」的細節。

### 公式沒有存計算結果時的提醒

GL、TB、科目配對、授權編製人員與假日的匯入，在成功回應的提醒寫出檔名、工作表、格數與第一格，
請審計員用 Excel 開啟這個檔案並存檔後再匯入；預覽讀到的列也同樣提醒。讀欄名與工作表清單時不計，
國定假日與補班日略過的第 1 列標題也不計。

### xls 的日期換算

日期由套件換算，也先四捨五入到毫秒：1900 日期系統的序列值 0 到 59 先加 1 再換，60 以後不加；
1904 日期系統一律加 1,462。讀取器把它換出的 1899-12-31 改回 Excel 顯示的 1900-01-00；換不成日期時套件交回原本的數字，
讀取器交出這個數字的文字。

### 截斷文字的提醒

匯出結果的提醒一個檔案一則，寫出檔名、總格數與各工作表的格數，最多逐一列出五張，
其餘合併成「另有 k 張工作表共 m 個」，並說明案件資料裡的完整文字不變。

### 科目配對範本的欄位長度

科目配對範本只寫科目代號、科目名稱與分類名稱。案件資料裡科目代號最多 450 字，科目名稱與分類名稱最多 400 字。

## 已知要改善處的細節

規範文末「已知要改善的地方」寫「見附表」的細節。

### 內建格式在東亞語言版有只含時間的格式

- 這些編號依開檔程式的介面語言解讀：32、33 在繁中、簡中、日文與韓文版都只有時間，繁中版的 34、35、52、53、55、56 也是時間。
- 套了這些格式的欄，帶日期的值只存日期，時間不見。只有時間的值，xlsx 的 1900 日期系統存成序列值的文字，例如「0.5625」，
  避免不同的時間都變成 1900-01-00；1904 日期系統存成 1904-01-01；xls 沒有實測。
- 依據：Microsoft Learn〈NumberingFormat Class〉，2026-11-20 查閱。審計員用繁中版 Excel 是推測。
  xls 沒寫格式字串時，ExcelDataReader 用日文版的字串代替，32、33 已經算時間。

### 時間格的值太大時整份匯入失敗

- 時間格式的數值交給 `TimeSpan.FromDays`，超過約 1,067 萬天就丟出例外。xlsx 的時間格式都會這樣。
  xls 只有套件判成經過時間的格式會，例如內建 46 的 `[h]:mm:ss`，訊息也沒有指出是哪一格；
  `h:mm`、`h:mm:ss`、`mm:ss` 的極大值照數字讀進來。
- 依據：.NET 的時間長度最多約 10,675,199 天，實測過。xls 用合成檔案實測；xlsx 依程式推論，沒有實際執行 JET。

### xls 與 1904 日期系統的換算限制

- xls 的序列值 60 讀成和 59 同一天的 1900-02-28，Excel 顯示的是日曆上沒有的 1900-02-29，日期提醒列成底稿寫成文字。套件 3.9.0 只把 0 到 59 加 1。
- xls 的 1904 日期系統裡，序列值 -1461 以上、-1460 未滿存成 1900-01-00，列為日曆上沒有這一天；xlsx 存序列值的文字，
  例如 -1460.75 存成「1.25」。套件不公開日期系統，讀取器分不出來。
- 1904 日期系統的負數，JET 讀成 1900 到 1903 年的日期；Excel 實際怎麼顯示，查不到可靠的出處。
- 1904 日期系統換不出日期時，xlsx 存加了 1,462 的數字；xls 晚於 9999-12-31 的值存儲存格本身的數字。
- xls 的 -1 到 0 之間的負小數，以及 h:mm 這類時間格式的負數，套件換算後負號不見。
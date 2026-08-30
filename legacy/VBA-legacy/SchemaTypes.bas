Attribute VB_Name = "SchemaTypes"
Option Explicit
'===============================================================================
' Module: SchemaTypes
' Purpose: 定義 DbSchema 使用的使用者自訂類型 (User-Defined Type)
'
' Responsibility:
'   本模組定義系統中所有 UDT，分為三大類：
'   1. 報表類型 (TypeReport*)       - 定義報表資料表的名稱和欄位
'   2. 資料表類型 (TypeTable*)      - 定義系統資料表的名稱和欄位
'   3. 集合類型 (TypeSystem*)       - 定義衍生資料表、欄位、報表的集合
'
' Note:
'   - VBA 不允許在類別模組中定義 Public Type，因此將類型定義移至標準模組
'   - 只定義「系統產生」的標準化欄位（如 K_AMOUNT_JE, ACCOUNT_MERGED）
'   - 不定義「使用者自訂」的欄位（如 DebitAmount, AccountNumber）
'   - 使用者自訂欄位由 FieldMapperJe 和 FieldMapperTb 管理
'
' Naming Convention:
'   - Type 名稱格式:
'       * TypeReport{ReportName}   - 報表類型
'       * TypeTable{TableName}     - 資料表類型
'       * TypeSystem{Category}     - 系統集合類型
'   - 欄位名稱: PascalCase（與實際資料庫欄位名稱一致）
'
' Dependencies:
'   - DbSchema.cls: 使用這些 UDT 建立階層式結構
'   - FieldMapperJe.cls: 管理 JE 的使用者自訂欄位映射
'   - FieldMapperTb.cls: 管理 TB 的使用者自訂欄位映射
'
' Usage Example:
'   '--在 DbSchema 中使用
'   Private m_Reports As TypeSystemReports
'   '--在 Service 中使用
'   Dim rpt As TypeReportEngagementOverview
'   rpt = context.Schema.Reports.EngagementOverview
'   sql = "SELECT ... AS [" & rpt.Client & "] INTO " & rpt.Name
'===============================================================================
' 報表類型定義
'-------------------------------------------------------------------------------
Public Type TypeReportEngagementOverview
    name As String                      ' 報表名稱
    '-- 欄位
    Client As String                    ' 客戶
    PrepStartDate As String             ' 財報準備期間開始日
    PeriodStart As String               ' 資料期間開始日
    PeriodEnd As String                 ' 資料期間結束日
    PreparedBy As String                ' 報表生產人
    PreparedDate As String              ' 報表生產時間
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportDataOverview
    name As String                      ' 報表名稱
    '-- 欄位
    JeName As String                    ' 分錄檔案名稱
    JeNetAmount As String               ' 分錄借貸淨額
    JeDebitSum As String                ' 分錄借方合計金額
    JeCreditSum As String               ' 分錄貸方合計金額
    JeRecordCount As String             ' 分錄筆數
    TbName As String                    ' 試算表檔案名稱
    TbAccountCount As String            ' 試算表科目數量
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportValidationOverview
    name As String                      ' 報表名稱
    '-- 欄位
    NullAccountRecordCount As String    ' 無會計科目編號筆數
    NullDocumentRecordCount As String   ' 無傳票號碼筆數
    NullDescriptionRecordCount As String ' 無傳票摘要筆數
    NotInPeriodCount As String          ' 傳票核准日不在會計期間內筆數
    CompletenessDiffCount As String     ' 完整性差異筆數
    DocumentBalanceDiffCount As String  ' 借貸不平差異筆數
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportCompletenessDetail
    name As String                      ' 報表名稱
    '-- 此報表直接複製 COMPLETENESS_DETAIL 表
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportDocumentBalanceDetail
    name As String                      ' 報表名稱
    '-- 此報表直接複製 DOCUMENT_BALANCE_DETAIL 表
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportInfSampleDetail
    name As String                      ' 報表名稱
    '-- 此報表直接複製 INF_SORTED 表
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportAccountMappingInfo
    name As String                      ' 報表名稱
End Type
'-------------------------------------------------------------------------------
Public Type TypeReportFieldMappingInfo
    name As String                      ' 報表名稱
End Type
'===============================================================================
' 資料表類型定義
' Note: 僅定義系統產生的欄位，使用者自訂欄位由 FieldMapper 管理
'===============================================================================
' DATE_DIMENSION (日期維度表)
' 提供日期相關的維度資訊（年、月、日、星期、假日、補班日）
'-------------------------------------------------------------------------------
Public Type TypeTableDateDimension
    name As String                      ' 資料表名稱
    '-- 欄位
    DateKey As String                   '[DATE]   唯一主鍵，格式: YYYYMMDD
    Year As String                      '[NUM]    西元年 (1900-?)
    Month As String                     '[NUM]    月份 (1-12)
    Day As String                       '[NUM]    日 (1-31)
    DayOfWeek As String                 '[NUM]    天 (1-7)
    IsWeekend As String                 '[Y/N]    -1/0 (系統自動產生)
    IsHolidays As String                '[Y/N]    -1/0 (由使用者上傳的資料覆寫)
    IsMakeupDays As String              '[Y/N]    -1/0 (由使用者上傳的資料覆寫)
    HolidaysDesc As String              '[CHAR]   假期說明 (選填)
    MakeupDaysDesc As String            '[CHAR]   補班日說明 (選填)
End Type
'-------------------------------------------------------------------------------
' ACCOUNT_MAPPING (科目配對表)
' 提供科目編號與標準化科目分類的對應關係
'-------------------------------------------------------------------------------
Public Type TypeTableAccountMapping
    name As String                      ' 資料表名稱
    '-- 欄位
    AccountNumber As String             ' 科目編號
    AccountName As String               ' 科目名稱
    AccountStandardized As String       ' 科目分類名稱
End Type
'===============================================================================
' 系統集合類型定義
' 將相關的資料表、欄位、報表組織成集合，便於統一管理
'===============================================================================
' 資料表: 系統衍生資料表集合
' 集中管理驗證測試過程中產生的所有中間表和結果表
'-------------------------------------------------------------------------------
Public Type TypeSystemTables
    JeInPeriod As String                ' 期間內的 JE
    JeNotInPeriod As String             ' 期間外的 JE
    JeAccountSum As String              ' JE 按科目彙總
    CompletenessCalculated As String    ' 完整性計算結果
    CompletenessDiff As String          ' 完整性差異結果
    CompletenessDetail As String        ' 完整性測試明細
    DocumentBalanceSum As String        ' 傳票彙總
    DocumentBalanceDiff As String       ' 借貸不平結果
    DocumentBalanceDetail As String     ' 借貸不平明細
    InfRandom As String                 ' INF 隨機抽樣
    InfSorted As String                 ' INF 排序資料
    NullAccount As String               ' 空白科目記錄
    NullDocument As String              ' 空白傳票記錄
    NullDescription As String           ' 空白摘要記錄
End Type
'-------------------------------------------------------------------------------
' 欄位表: 系統衍生欄位表集合
' 集中管理系統產生的標準化計算欄位
'-------------------------------------------------------------------------------
Public Type TypeSystemFields
    '-- 系統產生的標準化欄位
    JeUid As String                     ' JE 的唯一主鍵
    JeAmount As String                  ' JE 的計算金額
    JeAmountSum As String               ' JE 的計算金額彙總
    JeDrCr As String                    ' JE 的借貸別
    TbAmount As String                  ' TB 的計算金額
    AccountMerged As String             ' 合併 JE 和 TB 科目編號
    TbJeDiff As String                  ' JE 和 TB 差異金額
End Type
'-------------------------------------------------------------------------------
' 報表: 系統衍生報表集合
' 將所有報表類型組織成單一集合，便於統一存取
'-------------------------------------------------------------------------------
Public Type TypeSystemReports
    EngagementOverview As TypeReportEngagementOverview          '專案總覽報表
    DataOverview As TypeReportDataOverview                      '資料總覽報表
    ValidationOverview As TypeReportValidationOverview          '驗證總覽報表
    CompletenessDetail As TypeReportCompletenessDetail          '完整性測試明細
    DocumentBalanceDetail As TypeReportDocumentBalanceDetail    '借貸不平測試明細
    InfSampleDetail As TypeReportInfSampleDetail                'INF抽樣明細
    AccountMappingInfo As TypeReportAccountMappingInfo          '科目配對資訊
    FieldMappingInfo As TypeReportFieldMappingInfo              '欄位映射資訊
End Type


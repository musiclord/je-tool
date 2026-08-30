VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DbSchema"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: DbSchema
' Purpose: 統一管理系統中所有資料表與欄位的名稱定義（階層式結構）
'
' Design Pattern: Configuration Object (配置物件模式)
'
' Responsibility:
'   本類別作為系統綱要的統一入口點，整合以下三層資訊：
'   1. 使用者自訂欄位映射 (透過 FieldMapperJe/FieldMapperTb)
'   2. 系統產生的標準化欄位 (透過 TypeSystemFields)
'   3. 資料表名稱與報表定義 (透過 TypeSystemTables/TypeSystemReports)
'
' Architecture:
'   DbSchema (階層式結構)
'     ├── JE (FieldMapperJe) - 使用者自訂欄位映射
'     │   ├── .Name                       → "JE"
'     │   ├── .DebitAmount                → "借方金額_JE" (動態)
'     │   └── .AccountNumber              → "會計項目_JE" (動態)
'     │
'     ├── TB (FieldMapperTb) - 使用者自訂欄位映射
'     │   ├── .Name                       → "TB"
'     │   ├── .ChangeAmount               → "異動金額_TB" (動態)
'     │   └── .AccountNumber              → "科目編號_TB" (動態)
'     │
'     ├── DateDimension (TypeTableDateDimension) - 系統資料表
'     │   ├── .Name                       → "DATE_DIMENSION"
'     │   ├── .DateKey                    → "DateKey" (固定)
'     │   └── .IsWeekend                  → "IsWeekend" (固定)
'     │
'     ├── AccountMapping (TypeTableAccountMapping) - 系統資料表
'     │   ├── .Name                       → "ACCOUNT_MAPPING"
'     │   ├── .AccountCode                → "AccountCode" (固定)
'     │   └── .AccountStandardized        → "AccountStandardized" (固定)
'     │
'     ├── Tables (TypeSystemTables) - 系統衍生資料表集合
'     │   ├── .JeInPeriod                 → "JE_IN_PERIOD"
'     │   ├── .CompletenessDetail         → "COMPLETENESS_DETAIL"
'     │   └── .DocumentBalanceDetail      → "DOCUMENT_BALANCE_DETAIL"
'     │
'     ├── Fields (TypeSystemFields) - 系統產生欄位集合
'     │   ├── .JeAmount                   → "K_AMOUNT_JE"
'     │   ├── .TbAmount                   → "K_AMOUNT_TB"
'     │   ├── .AccountMerged              → "ACCOUNT_MERGED"
'     │   └── .TbJeDiff                   → "TB_JE_DIFF"
'     │
'     └── Reports (TypeSystemReports) - 系統報表集合
'         ├── .EngagementOverview
'         │   ├── .Name                   → "ENGAGEMENT_OVERVIEW"
'         │   ├── .Client                 → "客戶"
'         │   └── .PreparedBy             → "報表生產人"
'         ├── .DataOverview
'         │   ├── .Name                   → "DATA_OVERVIEW"
'         │   └── .JeName                 → "分錄檔案名稱"
'         └── .ValidationOverview
'             ├── .Name                   → "VALIDATION_OVERVIEW"
'             └── .CompletenessDiffCount  → "完整性差異筆數"
'
' Usage:
'   '使用者自訂欄位（透過 FieldMapper，動態）
'   sql = "SELECT [" & context.Schema.JE.DebitAmount & "] " & _
'         "FROM " & context.Schema.JE.Name
'
' Dependencies:
'   - SchemaTypes.bas: 定義所有 UDT
'   - FieldMapperJe.cls: 管理 JE 的使用者自訂欄位映射
'   - FieldMapperTb.cls: 管理 TB 的使用者自訂欄位映射
'   - ContextManager.cls: 透過依賴注入傳遞 Schema 物件
'
' Benefits:
'   1. 統一入口 - 所有資料表/欄位名稱透過 context.Schema 存取
'   2. 職責分離 - FieldMapper (動態) + Schema (靜態)
'   3. IntelliSense 友善 - IDE 提供完整的自動完成
'   4. 類型安全 - 編譯期檢查，減少拼寫錯誤
'   5. 易於維護 - 修改名稱只需改一處
'   6. 符合 OOP - 欄位從屬於資料表，符合正規化原則
'===============================================================================
' 使用 SchemaTypes 模組定義的 UDT
'===============================================================================
Private m_JE As FieldMapperJe
Private m_TB As FieldMapperTb
Private m_DateDimension As TypeTableDateDimension
Private m_AccountMapping As TypeTableAccountMapping
Private m_Tables As TypeSystemTables
Private m_Fields As TypeSystemFields
Private m_Reports As TypeSystemReports

'===============================================================================
' 透過屬性回傳 UDT
'===============================================================================
'-- 資料表
Public Property Get JE() As FieldMapperJe
    Set JE = m_JE
End Property
Public Property Get TB() As FieldMapperTb
    Set TB = m_TB
End Property
Public Property Get DateDimension() As TypeTableDateDimension
    DateDimension = m_DateDimension
End Property
Public Property Get AccountMapping() As TypeTableAccountMapping
    AccountMapping = m_AccountMapping
End Property
Public Property Get Tables() As TypeSystemTables
    Tables = m_Tables
End Property
Public Property Get fields() As TypeSystemFields
    fields = m_Fields
End Property
Public Property Get Reports() As TypeSystemReports
    Reports = m_Reports
End Property

'===============================================================================
' Initialization Methods
'===============================================================================

'-------------------------------------------------------------------------------
' Method: InitializeTables
' Purpose: 初始化所有系統資料表和集合的定義
' Called By: Initialize()
'-------------------------------------------------------------------------------
Private Sub InitializeTables()
    '-- DateDimension: 日期維度表
    With m_DateDimension
        .name = "DATE_DIMENSION"
        '-- 欄位
        .DateKey = "DateKey"
        .Year = "Year"
        .Month = "Month"
        .Day = "Day"
        .DayOfWeek = "DayOfWeek"
        .IsWeekend = "IsWeekend"
        .IsHolidays = "IsHolidays"
        .IsMakeupDays = "IsMakeupDays"
        .HolidaysDesc = "HolidaysDesc"
        .MakeupDaysDesc = "MakeupDaysDesc"
    End With
    
    '-- AccountMapping: 科目配對表
    With m_AccountMapping
        .name = "ACCOUNT_MAPPING"
        '-- 欄位
        .AccountNumber = "AccountNumber"
        .AccountName = "AccountName"
        .AccountStandardized = "AccountStandardized"
    End With
    
    '-- Tables: 系統衍生資料表集合
    With m_Tables
        .JeInPeriod = "JE_IN_PERIOD"
        .JeNotInPeriod = "JE_NOT_IN_PERIOD"
        .JeAccountSum = "JE_ACCOUNT_SUM"
        .CompletenessCalculated = "COMPLETENESS_CALCULATED"
        .CompletenessDiff = "COMPLETENESS_DIFF"
        .CompletenessDetail = "COMPLETENESS_DETAIL"
        .DocumentBalanceSum = "DOCUMENT_BALANCE_SUM"
        .DocumentBalanceDiff = "DOCUMENT_BALANCE_DIFF"
        .DocumentBalanceDetail = "DOCUMENT_BALANCE_DETAIL"
        .InfRandom = "INF_RANDOM"
        .InfSorted = "INF_SORTED"
        .NullAccount = "NULL_ACCOUNT_RECORDS"
        .NullDocument = "NULL_DOCUMENT_RECORDS"
        .NullDescription = "NULL_DESCRIPTION_RECORDS"
    End With
    
    '-- Fields: 系統產生欄位集合
    With m_Fields
        '//NOTE: 由於 UDT 僅作為副本傳送，而不可修改狀態
        '//      因此以下欄位名稱暫時可用，若後續有動態連結
        '//      則應該交由 FieldMapper 處理
        .JeUid = "識別碼_JE"
        .JeAmount = "K_AMOUNT_JE"
        .JeAmountSum = "K_AMOUNT_SUM_JE"
        .TbAmount = "K_AMOUNT_TB"
        .JeDrCr = "K_DRCR_JE"
        .AccountMerged = "K_ACCOUNT_MERGED"
        .TbJeDiff = "K_TB_JE_DIFF"
    End With
End Sub
'-------------------------------------------------------------------------------
' Method: InitializeReports
' Purpose: 初始化所有報表的定義
' Called By: Initialize()
'-------------------------------------------------------------------------------
Private Sub InitializeReports()
    '-- ENGAGEMENT_OVERVIEW: 專案總覽報表
    With m_Reports.EngagementOverview
        .name = "ENGAGEMENT_OVERVIEW"
        '-- 欄位
        .Client = "客戶"
        .PrepStartDate = "財報準備期間開始日"
        .PeriodStart = "資料期間開始日"
        .PeriodEnd = "資料期間結束日"
        .PreparedBy = "報表生產人"
        .PreparedDate = "報表生產時間"
    End With
    
    '-- DATA_OVERVIEW: 資料總覽報表
    With m_Reports.DataOverview
        .name = "DATA_OVERVIEW"
        '-- 欄位
        .JeName = "分錄檔案名稱"
        .JeNetAmount = "分錄借貸淨額"
        .JeDebitSum = "分錄借方合計金額"
        .JeCreditSum = "分錄貸方合計金額"
        .JeRecordCount = "分錄筆數"
        .TbName = "試算表檔案名稱"
        .TbAccountCount = "試算表科目數量"
    End With
    
    '-- VALIDATION_OVERVIEW: 驗證總覽報表
    With m_Reports.ValidationOverview
        .name = "VALIDATION_OVERVIEW"
        '-- 欄位
        .NullAccountRecordCount = "無會計科目編號筆數"
        .NullDocumentRecordCount = "無傳票號碼筆數"
        .NullDescriptionRecordCount = "無傳票摘要筆數"
        .NotInPeriodCount = "傳票核准日不在會計期間內筆數"
        .CompletenessDiffCount = "完整性差異筆數"
        .DocumentBalanceDiffCount = "借貸不平差異筆數"
    End With
    
    '-- COMPLETENESS_DETAIL: 完整性測試明細報表
    With m_Reports.CompletenessDetail
        .name = "COMPLETENESS_DETAIL"
    End With
    
    '-- DOCUMENT_BALANCE_DETAIL: 借貸平衡測試明細報表
    With m_Reports.DocumentBalanceDetail
        .name = "DOCUMENT_BALANCE_DETAIL"
    End With
    
    '-- INF_SAMPLE_DETAIL: INF抽樣明細報表
    With m_Reports.InfSampleDetail
        .name = "INF_SAMPLE_DETAIL"
    End With
    
    '-- ACCOUNT_MAPPING_INFO: 科目配對資訊報表
    With m_Reports.AccountMappingInfo
        .name = "ACCOUNT_MAPPING_INFO"
    End With
    
    '-- FIELD_MAPPING_INFO: 欄位映射資訊報表
    With m_Reports.FieldMappingInfo
        .name = "FIELD_MAPPING_INFO"
    End With
End Sub

'-------------------------------------------------------------------------------
' Method: Initialize
' Purpose: 模擬建構子，初始化所有綱要定義並綁定 FieldMapper
' Parameters:
'   - jeMapper: JE 的欄位映射物件（使用者自訂欄位）
'   - tbMapper: TB 的欄位映射物件（使用者自訂欄位）
' Called By: ContextManager.Initialize()
' Note:
'   此方法必須在 ContextManager 初始化時呼叫，以綁定使用者的欄位映射
'-------------------------------------------------------------------------------
Public Sub Initialize( _
    ByVal jeMapper As FieldMapperJe, _
    ByVal tbMapper As FieldMapperTb _
)
    '-- 初始化系統定義的資料表和集合
    Call InitializeTables
    Call InitializeReports
    
    '-- 綁定使用者自訂的欄位映射
    Set m_JE = jeMapper
    Set m_TB = tbMapper
End Sub

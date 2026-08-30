VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceReport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class:    ServiceReport
' Purpose:  定義產出報表所需資料之服務
' Methods:
'===============================================================================
Private m_Db As DbAccess
Private m_Schema As DbSchema
Private m_Context As ContextManager

Public Sub Initialize(context As ContextManager)
    Set m_Db = context.DbReport     '資料庫物件
    Set m_Schema = context.DbSchema '資料庫綱要
    Set m_Context = context
End Sub

Public Function GenerateEngagementOverview() As Boolean
    '---------------------------------------------------------------------------
    ' 方法: 產生專案總覽報表
    ' 欄位: 客戶, 財報準備期間開始日, 資料期間開始日, 資料期間結束日,
    '       報表生產人, 報表生產時間
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    GenerateEngagementOverview = False
    '指定報表物件
    Dim rpt As TypeReportEngagementOverview
    rpt = m_Schema.Reports.EngagementOverview
    '設定資料表名稱
    Dim tableName As String
    tableName = rpt.name
    '檢查已存在資料表
    If m_Db.TableExists(tableName) Then
        Call m_Db.DropTable(tableName)
    End If
    '處理資料庫查詢
    Dim sql As String
    With rpt
        sql = "SELECT TOP 1 " & vbCrLf & _
              "    '" & m_Context.info.CompanyName & "' AS [" & .Client & "], " & vbCrLf & _
              "    '" & m_Context.info.UserName & "' AS [" & .PreparedBy & "], " & vbCrLf & _
              "    #" & m_Context.info.PrepStartDate & "# AS [" & .PrepStartDate & "], " & vbCrLf & _
              "    #" & m_Context.info.PeriodStart & "# AS [" & .PeriodStart & "], " & vbCrLf & _
              "    #" & m_Context.info.PeriodEnd & "# AS [" & .PeriodEnd & "], " & vbCrLf & _
              "    #" & Date & "# AS [" & .PreparedDate & "] " & vbCrLf & _
              "INTO " & tableName & " IN '" & m_Db.path & "' " & vbCrLf & _
              "FROM " & m_Schema.JE.name & " IN '" & m_Context.DbInput.path & "';"
    End With
    m_Db.ExecuteNonQuery sql
    GenerateEngagementOverview = True
    Exit Function
ErrorHandler:
    Debug.Print "GenerateEngagementOverview():" & vbCrLf & _
                "  - 報表: " & tableName & vbCrLf & _
                "  - 錯誤代碼: " & Err.Number & vbCrLf & _
                "  - 錯誤描述: " & Err.description & vbCrLf & _
                "  - SQL: " & vbCrLf & sql
    GenerateEngagementOverview = False
End Function

Public Function GenerateDataOverview() As Boolean
    '---------------------------------------------------------------------------
    ' 方法
    ' 參數: 分錄檔案名稱,分錄借貸淨額,分錄借方合計金額,分錄貸方合計金額,
    '       分錄筆數,試算表檔案名稱,試算表科目數量
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    GenerateDataOverview = False
    '指定報表物件
    Dim rpt As TypeReportDataOverview
    rpt = m_Schema.Reports.DataOverview
    '設定資料表名稱
    Dim tableName As String
    tableName = rpt.name
    '檢查已存在資料表
    If m_Db.TableExists(tableName) Then
        Call m_Db.DropTable(tableName)
    End If
    '取得報表所需資料
    Dim JeName As String
    Dim JeNetAmount As Double
    Dim JeDebitSum As Double
    Dim JeCreditSum As Double
    Dim JeRecordCount As Long
    Dim TbName As String
    Dim TbAccountCount As Long
    '--
    With m_Schema
        JeName = .JE.name
        TbName = .TB.name
        JeNetAmount = m_Context.DbInput.GetTotal(.JE.name, .fields.JeAmount, "NetValue")
        JeDebitSum = m_Context.DbInput.GetTotal(.JE.name, .fields.JeAmount, "PositiveValue")
        JeCreditSum = m_Context.DbInput.GetTotal(.JE.name, .fields.JeAmount, "NegativeValue")
        JeRecordCount = m_Context.DbInput.GetTotal(.JE.name, "", "DBCount")
        TbAccountCount = m_Context.DbInput.GetTotal(.TB.name, "", "DBCount")
    End With
    '處理資料庫查詢
    Dim sql As String
    With rpt
        sql = "SELECT TOP 1 " & vbCrLf & _
              "    '" & JeName & "' AS [" & .JeName & "], " & vbCrLf & _
              "    '" & JeNetAmount & "' AS [" & .JeNetAmount & "], " & vbCrLf & _
              "    '" & JeDebitSum & "' AS [" & .JeDebitSum & "], " & vbCrLf & _
              "    '" & JeCreditSum & "' AS [" & .JeCreditSum & "], " & vbCrLf & _
              "    '" & JeRecordCount & "' AS [" & .JeRecordCount & "], " & vbCrLf & _
              "    '" & TbName & "' AS [" & .TbName & "], " & vbCrLf & _
              "    '" & TbAccountCount & "' AS [" & .TbAccountCount & "] " & vbCrLf & _
              "INTO " & tableName & " IN '" & m_Db.path & "' " & vbCrLf & _
              "FROM " & m_Schema.JE.name & " IN '" & m_Context.DbInput.path & "';"
    End With
    m_Db.ExecuteNonQuery sql
    GenerateDataOverview = True
    Exit Function
ErrorHandler:
    Debug.Print "GenerateDataOverview(): " & vbCrLf & _
                "  - 報表: " & tableName & vbCrLf & _
                "  - 錯誤代碼: " & Err.Number & vbCrLf & _
                "  - 錯誤描述: " & Err.description & vbCrLf & _
                "  - SQL: " & vbCrLf & sql
    GenerateDataOverview = False
End Function

Public Function GenerateValidationOverview() As Boolean
    '---------------------------------------------------------------------------
    ' 方法
    ' 參數: 無會計科目編號筆數,無傳票號碼筆數,無傳票摘要筆數,
    '       完整性差異筆數,借貸不平差異筆數
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    GenerateValidationOverview = False
    '指定報表物件
    Dim rpt As TypeReportValidationOverview
    rpt = m_Schema.Reports.ValidationOverview
    '設定資料表名稱
    Dim tableName As String
    tableName = rpt.name
    '檢查已存在資料表
    If m_Context.DbReport.TableExists(tableName) Then
        Call m_Context.DbReport.DropTable(tableName)
    End If
    '取得報表所需資料
    Dim nullAccountCount As Double
    Dim nullDocumentCount As Double
    Dim nullDescriptionCount As Double
    Dim NotInPeriodCount As Double
    Dim CompletenessDiffCount As Double
    Dim documentDiffCount As Double
    '--
    With m_Schema
        nullAccountCount = m_Context.DbValid.GetTotal(.Tables.NullAccount, "", "DBCount")
        nullDocumentCount = m_Context.DbValid.GetTotal(.Tables.NullDocument, "", "DBCount")
        nullDescriptionCount = m_Context.DbValid.GetTotal(.Tables.NullDescription, "", "DBCount")
        NotInPeriodCount = m_Context.DbValid.GetTotal(.Tables.JeNotInPeriod, "", "DBCount")
        CompletenessDiffCount = m_Context.DbValid.GetTotal(.Tables.CompletenessDiff, "", "DBCount")
        documentDiffCount = m_Context.DbValid.GetTotal(.Tables.DocumentBalanceDiff, "", "DBCount")
    End With
    '處理資料庫查詢
    Dim sql As String
    With rpt
        sql = "SELECT TOP 1 " & vbCrLf & _
              "    '" & nullAccountCount & "' AS [" & .NullAccountRecordCount & "], " & vbCrLf & _
              "    '" & nullDocumentCount & "' AS [" & .NullDocumentRecordCount & "], " & vbCrLf & _
              "    '" & nullDescriptionCount & "' AS [" & .NullDescriptionRecordCount & "], " & vbCrLf & _
              "    '" & NotInPeriodCount & "' AS [" & .NotInPeriodCount & "], " & vbCrLf & _
              "    '" & CompletenessDiffCount & "' AS [" & .CompletenessDiffCount & "], " & vbCrLf & _
              "    '" & documentDiffCount & "' AS [" & .DocumentBalanceDiffCount & "] " & vbCrLf & _
              "INTO " & tableName & " IN '" & m_Db.path & "' " & vbCrLf & _
              "FROM " & m_Schema.JE.name & " IN '" & m_Context.DbInput.path & "';"
    End With
    m_Db.ExecuteNonQuery sql
    GenerateValidationOverview = True
    Exit Function
ErrorHandler:
    Debug.Print "GenerateValidationOverview(): " & vbCrLf & _
                "  - 報表: " & tableName & vbCrLf & _
                "  - 錯誤代碼: " & Err.Number & vbCrLf & _
                "  - 錯誤描述: " & Err.description & vbCrLf & _
                "  - SQL: " & vbCrLf & sql
    GenerateValidationOverview = False
End Function

Public Function GenerateCompletenessDetail() As Boolean
    '---------------------------------------------------------------------------
    ' ...
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    GenerateCompletenessDetail = False
    '指定報表物件
    Dim rpt As TypeReportCompletenessDetail
    rpt = m_Schema.Reports.CompletenessDetail
    '設定資料表名稱
    Dim tableName As String
    tableName = rpt.name
    '檢查已存在資料表
    If m_Context.DbReport.TableExists(tableName) Then
        Call m_Context.DbReport.DropTable(tableName)
    End If
    '處理資料庫查詢
    Dim sql As String
    sql = "SELECT * " & vbCrLf & _
          "INTO " & tableName & " IN '" & m_Db.path & "' " & vbCrLf & _
          "FROM " & m_Schema.Tables.CompletenessDetail & " IN '" & m_Context.DbValid.path & "';"
    m_Db.ExecuteNonQuery sql
    GenerateCompletenessDetail = True
    Exit Function
ErrorHandler:
    Debug.Print "GenerateCompletenessDetail(): " & vbCrLf & _
                "  - 報表: " & tableName & vbCrLf & _
                "  - 錯誤代碼: " & Err.Number & vbCrLf & _
                "  - 錯誤描述: " & Err.description & vbCrLf & _
                "  - SQL: " & vbCrLf & sql
    GenerateCompletenessDetail = False
End Function

Public Function GenerateDocumentBalanceDetail() As Boolean
    '---------------------------------------------------------------------------
    ' ...
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    GenerateDocumentBalanceDetail = False
    '指定報表物件
    Dim rpt As TypeReportDocumentBalanceDetail
    rpt = m_Schema.Reports.DocumentBalanceDetail
    '設定資料表名稱
    Dim tableName As String
    tableName = rpt.name
    '檢查已存在資料表
    If m_Context.DbReport.TableExists(tableName) Then
        Call m_Context.DbReport.DropTable(tableName)
    End If
    '處理資料庫查詢
    Dim sql As String
    sql = "SELECT * " & vbCrLf & _
          "INTO " & tableName & " IN '" & m_Db.path & "' " & vbCrLf & _
          "FROM " & m_Schema.Tables.DocumentBalanceDetail & " IN '" & m_Context.DbValid.path & "';"
    m_Db.ExecuteNonQuery sql
    GenerateDocumentBalanceDetail = True
    Exit Function
ErrorHandler:
    Debug.Print "GenerateDocumentBalanceDetail(): " & vbCrLf & _
                "  - 報表: " & tableName & vbCrLf & _
                "  - 錯誤代碼: " & Err.Number & vbCrLf & _
                "  - 錯誤描述: " & Err.description & vbCrLf & _
                "  - SQL: " & vbCrLf & sql
    GenerateDocumentBalanceDetail = False
End Function

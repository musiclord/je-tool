VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceAccountMapping"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: ServiceAccountMapping
' Purpose: 管理科目配對的服務
'
' Description:
'   負責從驗證結果（COMPLETENESS_DETAIL）提取科目清單,
'   匯出至 AccountMappingSheet 供審計人員標註,
'   並將標註結果匯入至資料庫以供後續分析使用。
'
' Dependencies:
'   - ServiceValidation: 需先執行 CheckCompleteness() 產生 COMPLETENESS_DETAIL
'   - AccountMappingSheet: Excel 工作表物件
'
' Workflow:
'   1. ExportTemplate()
'       ↓ 從 COMPLETENESS_DETAIL 匯出科目清單至 AccountMappingSheet
'
'   2. [使用者手動標註]
'       ↓ 審計人員在 Excel 中填寫 "Standardized Account Name"
'
'   3. ImportAccountMapping()
'       ↓ 讀取 AccountMappingSheet 並儲存至 ACCOUNT_MAPPING 表
'
'   4. ValidateAccountMapping()
'         驗證對應關係的完整性
'
'
' [標準化科目分類選項]:
'     - STANDARDIZED_ACCOUNT_NAME
'         (1)Cash
'         (2)Revenue
'         (3)Receipt in advance
'         (4)Receivables
'         (5)Others
'
' Fields
'     1. GL Number,   原始科目代號（文字或數字）
'     2. GL Name,     標準化科目名稱，如果沒有就填 "Not in TB"
'     3. Standardized Account Name
'
' Data
'     - TBL_COMPLETENESS_DETAIL, 經過完整性測試的資料已經由科目代號(TB的)分組過
'
' Example
'-------------------------------------------------------------------------------
'     GL_Number |   GL_Name            |  Standardized Account Name*
'     1101**    |   Not in TB          |  Cash
'     1102A*    |   [銀存]支票存款     |  Cash
'     1102C*    |   [銀存]外匯活期存款 |  Cash
'     1103**    |   定期存款           |  Cash
'-------------------------------------------------------------------------------
'
'===============================================================================
Private m_Context As ContextManager
Private m_DbValid As DbAccess
Private m_Db As DbAccess
Private m_Schema As DbSchema

Public Sub Initialize(context As ContextManager)
    Set m_Context = context
    Set m_Db = context.DbInput
    Set m_Schema = context.DbSchema
    Set m_DbValid = context.DbValid
End Sub

'-------------------------------------------------------------------------------
' Method: ExportTemplate
' Purpose: 從 COMPLETENESS_DETAIL 匯出科目清單至 AccountMappingSheet
'-------------------------------------------------------------------------------
Public Function ExportTemplate(ws As Worksheet) As Boolean
    On Error GoTo ErrorHandler
    ExportTemplate = False
    Dim colNum As String: colNum = m_Schema.AccountMapping.AccountNumber
    Dim colName As String: colName = m_Schema.AccountMapping.AccountName
    Dim colStandardized As String: colStandardized = m_Schema.AccountMapping.AccountStandardized
    
    '-- 設置工作表格式
    ws.Cells.Clear
    ws.Columns("A").NumberFormat = "@"
    ws.Columns("B").NumberFormat = "@"
    ws.Columns("C").NumberFormat = "@"
    '-- 設置表頭名稱
    ws.Range("A1").value = colNum
    ws.Range("B1").value = colName
    ws.Range("C1").value = colStandardized
    ws.Range("A1:C1").Font.Bold = True
    
    '-- 填入資料
    Dim tableName As String
    tableName = m_Schema.Tables.CompletenessDetail
    
    If Not m_DbValid.TableExists(tableName) Then
        ws.Cells(2, 1).value = "資料表 [" & tableName & "] 不存在"
    Else
        '-- 查詢字串
        Dim sql As String
        sql = "SELECT " & vbCrLf & _
              "    [" & m_Schema.fields.AccountMerged & "] AS [" & colNum & "], " & vbCrLf & _
              "    [" & m_Schema.TB.AccountName & "] AS [" & colName & "], " & vbCrLf & _
              "    '' AS [" & colStandardized & "]" & vbCrLf & _
              "FROM [" & tableName & "] " & vbCrLf & _
              "ORDER BY [" & m_Schema.fields.AccountMerged & "];"
        
        '-- 查詢資料庫
        Dim rs As DAO.Recordset
        Set rs = m_DbValid.ExecuteQuery(sql)
        
        If rs.BOF And rs.EOF Then
            '無資料情形
            ws.Cells(2, 1).value = "(無資料)"
            Exit Function
        Else
            '有資料情形，寫入
            Dim maxRows As Long: maxRows = 1000
            Call ws.Cells(2, 1).CopyFromRecordset(rs, maxRows)
            '取得資料列數
            Dim lastRow As Long
            lastRow = ws.Cells(ws.Rows.Count, 1).End(xlUp).row
            Debug.Print "  - 成功匯出 " & (lastRow - 1) & " 筆科目"
            rs.Close
        End If
    End If
    '-- 啟用工作表
    ws.Columns("A:C").AutoFit
    ws.Activate
    Debug.Print "********** 科目配對範本匯出完成 **********"
    ExportTemplate = True
    Exit Function
ErrorHandler:
    Debug.Print "ExportTemplate()"
    Debug.Print "  - 錯誤代碼: " & Err.Number
    Debug.Print "  - 錯誤描述: " & Err.description
End Function

'-------------------------------------------------------------------------------
' Method: ImportAccountMapping
' Purpose: 讀取 AccountMappingSheet 並儲存至 ACCOUNT_MAPPING 表
'-------------------------------------------------------------------------------
Public Function ImportAccountMapping(ByVal rs As DAO.Recordset) As Boolean
    On Error GoTo ErrorHandler
    ImportAccountMapping = False
    '...
    If m_Db.TableExists(m_Schema.AccountMapping.name) Then
        m_Db.DropTable m_Schema.AccountMapping.name
    End If
    '...
    '-- 建立資料表
    Dim sql As String
    sql = "CREATE TABLE [" & m_Schema.AccountMapping.name & "] (" & vbCrLf & _
          "    [" & m_Schema.AccountMapping.AccountNumber & "] TEXT(50), " & vbCrLf & _
          "    [" & m_Schema.AccountMapping.AccountName & "] TEXT(50)," & vbCrLf & _
          "    [" & m_Schema.AccountMapping.AccountStandardized & "] TEXT(50) " & vbCrLf & _
          ");"
    Call m_Db.ExecuteNonQuery(sql)
    '-- 匯入資料
    sql = "INSERT INTO [" & m_Schema.AccountMapping.name & "] " & vbCrLf & _
          "SELECT * FROM [" & TBL_TEMP & "];" '<-- TBL_TEMP 已經由 importer 匯入進資料庫
    Call m_Db.ExecuteNonQuery(sql)
    '-- 匯入完成
    Debug.Print "********** 科目配對表匯入完成 **********"
    ImportAccountMapping = True
    Exit Function
ErrorHandler:
    Debug.Print "ImportAccountMapping()"
    Debug.Print "  - 錯誤代碼: " & Err.Number
    Debug.Print "  - 錯誤描述: " & Err.description
End Function


VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceExport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: ServiceExport
' Purpose: 定義資料輸出的服務，將資料表輸出為 Excel 工作表
'          且預覽工作表固定設為 PreviewSheet
'===============================================================================
Private Const MAX_ROWS As Long = 1000
Private m_Context As ContextManager
Private m_Db As DbAccess
Private m_Schema As DbSchema
Private m_Info As ProjectConfiguration
Private m_Ace As New Access.Application

Public Sub Initialize(context As ContextManager)
    Set m_Context = context
    Set m_Info = context.info
    Set m_Db = context.DbReport 'DEFAULT-DB
    Set m_Schema = context.DbSchema
End Sub

'-------------------------------------------------------------------------------
' 方法: 將多個資料表依序渲染至指定工作表
' 參數:
'     - ws: 目標工作表物件
'     - Tables: 資料表名稱陣列 (Variant Array)
'     - startRow: 起始列位置 (預設為 1)
' Usage:
'     Dim ws As Worksheet
'     Set ws = ThisWorkbook.Worksheets(SHT_ACCOUNT_MAPPING)
'     Dim Tables As Variant
'     Tables = Array("COMPLETENESS_DETAIL", "DOCUMENT_BALANCE_DETAIL")
'     Call RenderTablesToWorksheet(ws, Tables, 3)
'-------------------------------------------------------------------------------
Private Sub RenderTablesToWorksheet(ByVal ws As Worksheet, _
                                    ByVal Tables As Variant, _
                                    Optional ByVal startRow As Long = 1)
    On Error GoTo ErrorHandler
    Dim idx As Long
    Dim currentRow As Long
    Dim rs As DAO.Recordset
    Dim rowsCopied As Long
    
    currentRow = startRow
    ws.Cells.Clear
    '遍歷所有資料表
    For idx = LBound(Tables) To UBound(Tables)
        Dim tableName As String
        tableName = CStr(Tables(idx))
        '檢查資料表是否存在
        If Not m_Db.TableExists(tableName) Then
            ws.Cells(currentRow, 1).value = "資料表 [" & tableName & "] 不存在"
            ws.Cells(currentRow, 1).Font.color = vbRed
            currentRow = currentRow + 2
        Else
            Set rs = m_Db.ExecuteQuery("SELECT * FROM [" & tableName & "];")
            rowsCopied = 0
            '寫入區塊標題列
            ws.Cells(currentRow, 1).value = tableName
            ws.Cells(currentRow, 1).Font.Bold = True
            currentRow = currentRow + 1
            '寫入欄位名稱列
            Dim col As Long
            For col = 0 To rs.fields.Count - 1
                ws.Cells(currentRow, col + 1).value = rs.fields(col).name
            Next col
            '格式化欄位名稱列
            With ws.Range(ws.Cells(currentRow, 1), ws.Cells(currentRow, rs.fields.Count))
                .Font.Bold = True
                .Interior.color = RGB(245, 241, 222)
            End With
            
            currentRow = currentRow + 1
            '處理資料
            If rs.BOF And rs.EOF Then
                '無資料情形
                ws.Cells(currentRow, 1).value = "(無資料)"
                currentRow = currentRow + 2
            Else
                '寫入資料
                If rs.RecordCount = -1 Then
                    rs.MoveLast
                End If
                rowsCopied = rs.RecordCount
                If rowsCopied < 0 Then rowsCopied = 0
                rs.MoveFirst
                ws.Cells(currentRow, 1).CopyFromRecordset rs
                currentRow = currentRow + rowsCopied + 2
            End If
            '釋放資源
            rs.Close
            Set rs = Nothing
        End If
    Next idx
    '自動調整欄寬
    ws.Columns.AutoFit
    ws.Activate
    Exit Sub
ErrorHandler:
    Debug.Print "RenderTablesToWorksheet(): " & Err.Number & " - " & Err.description
End Sub

Private Sub SetPassword(ByRef xlSheet As Excel.Worksheet)
    '---------------------------------------------------------------------------
    ' 方法: 設置工作表密碼，鎖住寫入控制權
    ' 參數:
    '     - xlSheet: 要鎖住的工作表物件
    '---------------------------------------------------------------------------
    Dim password As String
    password = vbNullString & "-" & Format(Now, "yyyymmdd")
    xlSheet.Protect password:=password, _
                    DrawingObjects:=True, _
                    Contents:=True, _
                    Scenarios:=True
End Sub

'-- 產出指定報告
'-------------------------------------------------------------------------------
' 資料預先整理之說明 = ENGAGEMENT_OVERVIEW
'-------------------------------------------------------------------------------
Public Function ShowEngagementOverview() As Boolean
    ShowEngagementOverview = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array(m_Schema.Reports.EngagementOverview.name)
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-1-1|資料預先整理之說明"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowEngagementOverview = True
End Function

Public Function ShowWorkingPaperSummary() As Boolean
    ' JE底稿說明 = 0
End Function

'-------------------------------------------------------------------------------
' 驗證測試報告 = ENGAGEMENT_OVERVIEW + DATA_OVERVIEW + VALIDATION_OVERVIEW
'-------------------------------------------------------------------------------
Public Function ShowValidationReport() As Boolean
    ShowValidationReport = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array( _
        m_Schema.Reports.EngagementOverview.name, _
        m_Schema.Reports.DataOverview.name, _
        m_Schema.Reports.ValidationOverview.name _
    )
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-2-1|驗證測試報告"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowValidationReport = True
End Function

'-------------------------------------------------------------------------------
' 完整性測試報告 = ENGAGEMENT_OVERVIEW + COMPLETENESS_DETAIL
'-------------------------------------------------------------------------------
Public Function ShowCompletenessReport() As Boolean
    ShowCompletenessReport = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array( _
        m_Schema.Reports.EngagementOverview.name, _
        m_Schema.Reports.CompletenessDetail.name _
    )
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-2-2|完整性測試報告"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowCompletenessReport = True
End Function

'-------------------------------------------------------------------------------
' 借貸不平測試報告 = ENGAGEMENT_OVERVIEW + DOCUMENT_BALANCE_DETAIL
'-------------------------------------------------------------------------------
Public Function ShowDocumentBalanceReport() As Boolean
    ShowDocumentBalanceReport = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array( _
        m_Schema.Reports.EngagementOverview.name, _
        m_Schema.Reports.DocumentBalanceDetail.name _
    )
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-2-3|借貸不平測試報告"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowDocumentBalanceReport = True
End Function

'-------------------------------------------------------------------------------
' 分錄編制人員說明 = ENGAGEMENT_OVERVIEW + ???
' 原始資料位於 #PreScr-R5-Sum 為預篩選步驟
'-------------------------------------------------------------------------------
Public Function ShowPreparerSummary() As Boolean
    ShowPreparerSummary = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-2-4|分錄編制人員說明"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    'Call RenderTablesToWorksheet(ws, Tables)
    ShowPreparerSummary = True
End Function

'-------------------------------------------------------------------------------
' 完整性測試之差異說明 =
'-------------------------------------------------------------------------------
Public Function ShowCompletenessDiffSummary() As Boolean
    ShowCompletenessDiffSummary = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-2-5|完整性測試之差異說明"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowCompletenessDiffSummary = True
End Function

'-------------------------------------------------------------------------------
' 完整性差異調節報告
'-------------------------------------------------------------------------------
Public Function ShowCompletenessReconciliationReport() As Boolean
    ShowCompletenessReconciliationReport = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-2-6|完整性差異調節報告"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowCompletenessReconciliationReport = True
End Function

'-------------------------------------------------------------------------------
' INF可靠性測試 =
'-------------------------------------------------------------------------------
Public Function ShowINFReport() As Boolean
    ShowINFReport = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-3-1|INF可靠性測試"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowINFReport = True
End Function

'-------------------------------------------------------------------------------
' 高風險條件 (HRC) 說明
'-------------------------------------------------------------------------------
Public Function ShowHRCSummary() As Boolean
    ShowHRCSummary = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-4-1|高風險條件 (HRC) 說明"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowHRCSummary = True
    '...
End Function

'-------------------------------------------------------------------------------
' 符合高風險條件傳票報告 =
'-------------------------------------------------------------------------------
Public Function ShowHRCReport() As Boolean
    ShowHRCReport = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-4-2|符合高風險條件傳票報告"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowHRCReport = True
End Function

'-------------------------------------------------------------------------------
' 符合高風險條件傳票之明細
'-------------------------------------------------------------------------------
Public Function ShowHRCDetails() As Boolean
    ShowHRCDetails = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-4-3|符合高風險條件傳票之明細"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowHRCDetails = True
End Function

'-------------------------------------------------------------------------------
'財務報表關帳後調整之分錄
'-------------------------------------------------------------------------------
Public Function ShowPostClosingEntries() As Boolean
    ShowPostClosingEntries = False
    '設置資料表陣列
    Dim Tables As Variant
    Tables = Array()
    '設置工作表名稱
    Dim sheetName As String
    sheetName = "Step-5-1|財務報表關帳後調整之分錄"
    '設置工作表物件
    Dim ws As Worksheet
    Set ws = SHT_PREVIEW
    ws.name = sheetName
    '預覽記錄集於工作表
    Call RenderTablesToWorksheet(ws, Tables)
    ShowPostClosingEntries = True
End Function

'-------------------------------------------------------------------------------
' 自動化工具-檔案欄位資訊
'-------------------------------------------------------------------------------
Public Function ShowFieldInfo() As Boolean
    '...
End Function

'-------------------------------------------------------------------------------
' 自動化工具-假期假日資訊
'-------------------------------------------------------------------------------
Public Function ShowHolidyInfo() As Boolean
    '...
End Function

'-------------------------------------------------------------------------------
' 自動化工具-科目配對資訊
'-------------------------------------------------------------------------------
Public Function ShowAccountMappingInfo() As Boolean
    '...
End Function

'-------------------------------------------------------------------------------
' 將處理好的底稿輸出至本機目錄
' 彙總所有軌跡的報告並輸出 .xlsx 檔案至專案木錄
'-------------------------------------------------------------------------------
Public Function ExportWp() As Boolean
    '將所有工作表匯出為單一電子表
End Function

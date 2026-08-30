VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterValidation"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Purpose:  協調驗證與科目配對畫面；業務規則由 1120 服務執行。
'===============================================================================

Private WithEvents m_View As ViewValidation
Attribute m_View.VB_VarHelpID = -1

Private m_Context As ContextManager
Private m_Validator As ServiceValidation
Private m_Mapper As ServiceAccountMapping
Private m_Importer As ServiceImport

Public Sub Initialize(ByVal context As ContextManager)
    Set m_Context = context
    Set m_Validator = New ServiceValidation
    Set m_Mapper = New ServiceAccountMapping
    Set m_Importer = New ServiceImport
    Set m_View = New ViewValidation

    m_Validator.Initialize m_Context
    m_Mapper.Initialize m_Context
    m_Importer.Initialize m_Context
    m_View.Initialize
End Sub

Public Sub Show()
    m_View.Show vbModeless
End Sub

Private Sub m_View_CheckCompleteness()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = m_Validator.CheckCompleteness()
    If succeeded Then
        msg = "所有期間都有 JE。"
    Else
        msg = "請檢視 COMPLETENESS_DETAIL。"
    End If
    m_View.ReportTestResult "完整性檢查", succeeded, msg
End Sub

Private Sub m_View_CheckDocumentBalance()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = m_Validator.CheckDocumentBalance()
    If succeeded Then
        msg = "借貸金額平衡。"
    Else
        msg = "發現不平衡，請檢視 DOCUMENT_BALANCE_DETAIL。"
    End If
    m_View.ReportTestResult "憑證借貸平衡", succeeded, msg
End Sub

Private Sub m_View_CheckNullRecords()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = m_Validator.CheckNullRecords()
    If succeeded Then
        msg = "未偵測到 Null 欄位。"
    Else
        msg = "有 Null 欄位，請檢視 NULL_RECORDS_DETAIL。"
    End If
    m_View.ReportTestResult "Null Records 檢查", succeeded, msg
End Sub

Private Sub m_View_CheckINF()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = m_Validator.CheckINF()
    If succeeded Then
        msg = "INF 檢查通過。"
    Else
        msg = "INF 清單有異常，請檢查 LOG_INF_DETAIL。"
    End If
    m_View.ReportTestResult "INF 檢查", succeeded, msg
End Sub

Private Sub m_View_ShowAccountMapping()
    If Not m_Context.DbValid.TableExists(m_Context.DbSchema.Tables.CompletenessDetail) Then
        MsgBox "未找到完整性測試明細資料表", vbExclamation, "查閱科目配對"
        Exit Sub
    End If

    Dim ws As Worksheet
    Set ws = SHT_ACCOUNT_MAPPING
    m_Mapper.ExportTemplate ws
End Sub

Private Sub m_View_ImportAccountMapping()
    Dim ws As Worksheet
    Set ws = SHT_ACCOUNT_MAPPING

    Dim lastRow As Long
    lastRow = ws.Cells(ws.Rows.Count, 1).End(xlUp).row
    If lastRow < 2 Then
        MsgBox "科目配對工作表沒有資料", vbInformation, "科目配對"
        Exit Sub
    End If

    Dim dataRange As String
    dataRange = "A1:C" & lastRow

    Dim fieldTypes As Variant
    fieldTypes = Array("TEXT(50)", "TEXT(50)", "TEXT(50)")

    Dim rs As DAO.Recordset
    Set rs = m_Importer.ImportFromSheet(ws, dataRange, fieldTypes)

    If Not m_Mapper.ImportAccountMapping(rs) Then
        MsgBox "科目配對失敗", vbExclamation, "科目配對"
    Else
        MsgBox "科目配對成功", vbInformation, "科目配對"
    End If
End Sub

Private Sub m_View_Submitted(ByVal dto As DataTransferObject)
    ' 1120 尚未定義額外提交行為；保留事件邊界供後續擴充。
End Sub

VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterImport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Purpose:  協調匯入、欄位映射與日期維度畫面；業務規則由 1120 服務執行。
'===============================================================================

Private WithEvents m_View As ViewImport
Attribute m_View.VB_VarHelpID = -1
Private WithEvents m_MapJe As ViewImportMapJe
Attribute m_MapJe.VB_VarHelpID = -1
Private WithEvents m_MapTb As ViewImportMapTb
Attribute m_MapTb.VB_VarHelpID = -1

Private m_Context As ContextManager
Private m_Importer As ServiceImport
Private m_Calendar As ServiceDateDimension

Public Sub Initialize(ByVal context As ContextManager)
    Set m_Context = context
    Set m_Importer = New ServiceImport
    Set m_Calendar = New ServiceDateDimension
    Set m_View = New ViewImport
    Set m_MapJe = New ViewImportMapJe
    Set m_MapTb = New ViewImportMapTb

    m_Importer.Initialize m_Context
    m_Calendar.Initialize m_Context
    m_View.Initialize

    If Not m_Context.DbInput Is Nothing Then
        m_View.SetJeState m_Context.DbInput.TableExists(m_Context.DbSchema.JE.name)
        m_View.SetTbState m_Context.DbInput.TableExists(m_Context.DbSchema.TB.name)
        m_View.SetCalendarState m_Context.DbInput.TableExists(m_Context.DbSchema.DateDimension.name)
    End If
End Sub

Public Sub Show()
    m_View.Show vbModeless
End Sub

Private Sub m_View_BeepBoop()
    Dim testString As String
    testString = m_Context.DbSchema.JE.name
    Debug.Print "BEEP BOOP:" & testString
    MsgBox testString, vbInformation, testString
End Sub

Private Sub m_View_ImportJournalEntries(ByVal Format As String)
    m_Importer.dType = Format
    m_Importer.OpenWizard "je"
    m_View.lblImportStatus.Caption = "匯入資料表: " & m_Context.DbSchema.JE.name
    m_View.SetJeState m_Context.DbInput.TableExists(m_Context.DbSchema.JE.name)
End Sub

Private Sub m_View_ImportTrialBalance(ByVal Format As String)
    m_Importer.dType = Format
    m_Importer.OpenWizard "tb"
    m_View.lblImportStatus.Caption = "匯入資料表: " & m_Context.DbSchema.TB.name
    m_View.SetTbState m_Context.DbInput.TableExists(m_Context.DbSchema.TB.name)
End Sub

Private Sub m_View_JeFieldMappingRequested()
    If Not m_Context.DbInput.TableExists(m_Context.DbSchema.JE.name) Then
        MsgBox "尚未匯入 JE 資料表", vbExclamation, "JE欄位映射"
        m_View.btnImportJe.SetFocus
        Exit Sub
    End If

    m_MapJe.Initialize m_Context.DbInput
    m_MapJe.Show vbModeless
End Sub

Private Sub m_View_TbFieldMappingRequested()
    If Not m_Context.DbInput.TableExists(m_Context.DbSchema.TB.name) Then
        MsgBox "尚未匯入 TB 資料表", vbExclamation, "TB欄位映射"
        m_View.btnImportTb.SetFocus
        Exit Sub
    End If

    m_MapTb.Initialize m_Context.DbInput
    m_MapTb.Show vbModeless
End Sub

Private Sub m_View_UpdateDateDimensionRequested(ByVal dto As DataTransferObject)
    m_Context.info.PeriodStart = dto.PeriodStart
    m_Context.info.PeriodEnd = dto.PeriodEnd
    m_Calendar.CreateDateDimensionTable

    Dim ws As Worksheet
    Dim dataRange As String
    Dim types As Variant
    Dim fieldNames As Variant
    dataRange = "A2:B200"
    types = Array("DATE", "TEXT(50)")
    fieldNames = Array("DateValue", "Description")

    Set ws = SHT_MAKEUPDAYS
    Dim rsMakeupDays As DAO.Recordset
    Set rsMakeupDays = m_Importer.ImportFromSheet( _
                                ws, _
                                dataRange, _
                                types, _
                                fieldNames _
                            )
    If rsMakeupDays Is Nothing Then
        Debug.Print "  - MakeupDaySheet 無資料或匯入失敗"
    ElseIf rsMakeupDays.EOF Then
        Debug.Print "  - MakeupDaySheet 無資料或匯入失敗"
    Else
        m_Calendar.MergeMakeupDays rsMakeupDays
    End If

    Set ws = SHT_HOLIDAYS
    Dim rsHolidays As DAO.Recordset
    Set rsHolidays = m_Importer.ImportFromSheet( _
                            ws, _
                            dataRange, _
                            types, _
                            fieldNames _
                        )
    If rsHolidays Is Nothing Then
        Debug.Print "  - HolidaySheet 無資料或匯入失敗"
    ElseIf rsHolidays.EOF Then
        Debug.Print "  - HolidaySheet 無資料或匯入失敗"
    Else
        m_Calendar.MergeHolidays rsHolidays
    End If

    m_Calendar.MergeWeekends dto.weekendIndices
    MsgBox "日期維度已完成設定", vbInformation, "日期設定"
    m_View.SetCalendarState m_Context.DbInput.TableExists(m_Context.DbSchema.DateDimension.name)
End Sub

Private Sub m_View_TestDefaultRequested()
    m_MapJe.Initialize m_Context.DbInput
    m_MapJe.ApplyTestDefaults
    m_MapTb.Initialize m_Context.DbInput
    m_MapTb.ApplyTestDefaults
End Sub

Private Sub m_View_Submitted(ByVal dto As DataTransferObject)
    m_Context.info.CompanyName = dto.CompanyName
    m_Context.info.PrepStartDate = dto.PrepStartDate
End Sub

Private Sub m_MapJe_ApplyField(ByVal dict As Dictionary, ByVal amountMethod As Long)
    m_Context.DbSchema.JE.method = amountMethod
    If Not m_Importer.ApplyJe(dict) Then
        m_View.lblFieldMappingStatus.Caption = "JE FAILED"
        MsgBox "JE 套用欄位失敗", vbExclamation, "JE 套用欄位"
    Else
        m_View.lblFieldMappingStatus.Caption = "JE PASS"
    End If
End Sub

Private Sub m_MapTb_ApplyField(ByVal dict As Dictionary, ByVal amountMethod As Long)
    m_Context.DbSchema.TB.method = amountMethod
    If Not m_Importer.ApplyTb(dict) Then
        m_View.lblFieldMappingStatus.Caption = "TB FAILED"
        MsgBox "TB套用欄位失敗", vbExclamation, "TB套用欄位"
    Else
        m_View.lblFieldMappingStatus.Caption = "TB PASS"
    End If
End Sub

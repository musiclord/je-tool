VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewValidation 
   Caption         =   "驗證資料"
   ClientHeight    =   8010
   ClientLeft      =   105
   ClientTop       =   405
   ClientWidth     =   3705
   OleObjectBlob   =   "ViewValidation.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewValidation"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewValidation
' Purpose:
' Methods:
'===============================================================================
Public Event CheckCompleteness()
Public Event CheckDocumentBalance()
Public Event CheckINF()
Public Event CheckNullRecords()
Public Event ShowAccountMapping()
Public Event ImportAccountMapping()
Public Event Submitted(ByVal dto As DataTransferObject)
Private Const STATUS_LIMIT As Long = 30
Private m_StatusRows As Collection

Public Sub Initialize()
    '...
    Set m_StatusRows = New Collection
    Me.lstStatus.Clear
    Me.lblStatusSummary.Caption = "尚未執行任何驗證"
    Me.lblStatusSummary.ForeColor = RGB(102, 102, 102)
End Sub

Private Sub btnCompleteness_Click()
    ShowTestProgress "完整性檢查"
    RaiseEvent CheckCompleteness
End Sub

Private Sub btnDocumentBalance_Click()
    ShowTestProgress "憑證借貸平衡"
    RaiseEvent CheckDocumentBalance
End Sub
Private Sub btnINF_Click()
    ShowTestProgress "INF 檢查"
    RaiseEvent CheckINF
End Sub

Private Sub btnNullRecords_Click()
    ShowTestProgress "Null Records 檢查"
    RaiseEvent CheckNullRecords
End Sub

Private Sub btnConfigureAccountMapping_Click()
    '進行科目配對
    RaiseEvent ShowAccountMapping
End Sub

Private Sub btnApplyAccountMapping_Click()
    '套用科目配對
    RaiseEvent ImportAccountMapping
End Sub

Private Sub btnExit_Click()
    Dim dto As DataTransferObject
    Me.Hide
    RaiseEvent Submitted(dto)
End Sub

'----------------------------------------------------------------------
Public Sub ReportTestResult(ByVal testKey As String, _
                            ByVal succeeded As Boolean, _
                            Optional ByVal details As String = "")
    Dim badge As String
    Dim message As String
    Dim color As Long

    If succeeded Then
        badge = "?"
        color = RGB(0, 118, 68)
        If Len(details) = 0 Then details = "檢查通過。"
    Else
        badge = "?"
        color = RGB(192, 0, 0)
        If Len(details) = 0 Then details = "請查看詳細報告。"
    End If

    message = badge & " " & testKey & " - " & details
    UpdateStatusIndicators testKey, message, color
End Sub

Private Sub ShowTestProgress(ByVal testKey As String)
    UpdateStatusIndicators testKey, "? " & testKey & " 正在執行...", RGB(90, 90, 90)
End Sub

Private Sub UpdateStatusIndicators(ByVal testKey As String, _
                                   ByVal message As String, _
                                   ByVal color As Long)
    Me.lblStatusSummary.Caption = message
    Me.lblStatusSummary.ForeColor = color
    PushStatusRow Format$(Now, "hh:nn:ss") & " | " & message
End Sub

Private Sub PushStatusRow(ByVal rowText As String)
    Me.lstStatus.AddItem rowText
    If Me.lstStatus.ListCount > STATUS_LIMIT Then
        Me.lstStatus.RemoveItem 0
    End If
    Me.lstStatus.ListIndex = Me.lstStatus.ListCount - 1
End Sub


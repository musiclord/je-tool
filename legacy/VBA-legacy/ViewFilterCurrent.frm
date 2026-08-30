VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilterCurrent 
   Caption         =   "篩選條件"
   ClientHeight    =   7470
   ClientLeft      =   105
   ClientTop       =   405
   ClientWidth     =   10785
   OleObjectBlob   =   "ViewFilterCurrent.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilterCurrent"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewFilterCurrent
' Purpose:
' Methods:
'===============================================================================
Public Event OverviewCriteria()
Public Event AddSqlRequested()
Public Event AddCriteriaRequested()
Public Event AddCriterionRequested()
Public Event RemoveCriteriaRequested()
Public Event RemoveCriterionRequested()
Public Event ExecuteCriteriaRequested()
Public Event Submitted(dto As DataTransferObject)
'--
Public criteriaStates As New Dictionary
Public Sub Initialize()
    '...
End Sub

Private Sub btnOverview_Click()
    '...
    RaiseEvent OverviewCriteria '總覽條件組合詳細
End Sub

Private Sub btnAddCriteria_Click()
    RaiseEvent AddCriteriaRequested      '新增條件組合
End Sub

Private Sub btnAddCriterion_Click()
    RaiseEvent AddCriterionRequested     '新增條件，打開 ViewFilterAddCriteria
End Sub

Private Sub btnCustomSQL_Click()
    RaiseEvent AddSqlRequested  '要求打開 ViewFilterAddSql
End Sub

Private Sub btnRemoveCriteria_Click()
    RaiseEvent RemoveCriteriaRequested   '移除所選的條件組合
End Sub

Private Sub btnRemoveCriterion_Click()
    RaiseEvent RemoveCriterionRequested  '移除所選的條件
End Sub

Private Sub btnExecuteCriteria_Click()
    RaiseEvent ExecuteCriteriaRequested  '執行所有條件集合，組成 SQL 查詢
End Sub

Private Sub btnExit_Click()
    '...
    '檢查並驗證
    Dim dto As New DataTransferObject
    '...
    Me.Hide
    Unload Me
    RaiseEvent Submitted(dto)
End Sub

VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterFilter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Purpose:  協調 current/legacy 篩選畫面；沿用 1120 已完成的 legacy 流程。
'===============================================================================

Private WithEvents m_Router As ViewFilterRouter
Attribute m_Router.VB_VarHelpID = -1
Private WithEvents m_Current As ViewFilterCurrent
Attribute m_Current.VB_VarHelpID = -1
Private WithEvents m_Legacy As ViewFilterLegacy
Attribute m_Legacy.VB_VarHelpID = -1
Private WithEvents m_AddCriteria As ViewFilterAddCriteria
Attribute m_AddCriteria.VB_VarHelpID = -1
Private WithEvents m_AddSql As ViewFilterAddSql
Attribute m_AddSql.VB_VarHelpID = -1

Private m_Context As ContextManager
Private m_Filter As ServiceFilter

Public Sub Initialize(ByVal context As ContextManager)
    Set m_Context = context
    Set m_Filter = New ServiceFilter
    Set m_Router = New ViewFilterRouter
    Set m_Current = New ViewFilterCurrent
    Set m_Legacy = New ViewFilterLegacy
    Set m_AddCriteria = New ViewFilterAddCriteria
    Set m_AddSql = New ViewFilterAddSql

    m_Filter.Initialize m_Context
End Sub

Public Sub Show()
    m_Router.Show vbModeless
End Sub

Private Sub m_Router_RouteToCurrent()
    m_Current.Initialize
    m_Current.Show vbModeless
End Sub

Private Sub m_Router_RouteToLegacy()
    m_Legacy.Initialize m_Context.DbInput
    m_Legacy.Show vbModeless
End Sub

Private Sub m_Legacy_ExecuteCriteriaRequested()
    Dim states As Dictionary
    Set states = m_Legacy.criteriaStates

    m_Filter.ProcessCriteriaSet states, "legacy"
    m_Filter.ExecuteBatch "FILTER_RESULT"
    MsgBox "篩選執行完畢！", vbInformation, "Legacy Filter"
End Sub

Private Sub m_Legacy_ShowCriteriaRequested()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Legacy_Submitted(ByVal dto As DataTransferObject)
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Current_ExecuteCriteriaRequested()
    Dim states As Dictionary
    Set states = m_Current.criteriaStates

    ' 1120 的 current 篩選轉換與執行尚未完成，不能冒用 legacy 規則。
End Sub

Private Sub m_Current_OverviewCriteria()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Current_AddCriteriaRequested()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Current_AddCriterionRequested()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Current_AddSqlRequested()
    m_AddSql.Initialize
    m_AddSql.Show vbModeless
End Sub

Private Sub m_Current_RemoveCriteriaRequested()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Current_RemoveCriterionRequested()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_Current_Submitted(dto As DataTransferObject)
    ' 1120 尚未提供實作。
End Sub

Private Sub m_AddCriteria_AddCriteria()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_AddCriteria_Cancel()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_AddCriteria_SelectTypeDate()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_AddCriteria_SelectTypeKeyword()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_AddCriteria_SelectTypeNumerical()
    ' 1120 尚未提供實作。
End Sub

Private Sub m_AddSql_AddSqlRequested()
    Dim sql As String
    sql = m_AddSql.txtbSql.value
    ' 1120 只完成 SQL 讀取，尚未定義套用規則。
End Sub

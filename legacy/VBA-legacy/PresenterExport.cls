VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterExport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Purpose:  協調報表產生與匯出畫面；業務規則由 1120 服務執行。
'===============================================================================

Private WithEvents m_View As ViewExport
Attribute m_View.VB_VarHelpID = -1

Private m_Context As ContextManager
Private m_Reporter As ServiceReport
Private m_Exporter As ServiceExport

Public Sub Initialize(ByVal context As ContextManager)
    Set m_Context = context
    Set m_Reporter = New ServiceReport
    Set m_Exporter = New ServiceExport
    Set m_View = New ViewExport

    m_Reporter.Initialize m_Context
    m_Exporter.Initialize m_Context
    m_View.Initialize
End Sub

Public Sub Show()
    m_View.Show vbModeless
End Sub

Private Sub m_View_ExportWp()
    m_Exporter.ExportWp
End Sub

Private Sub m_View_ShowEngagementOverview()
    m_Reporter.GenerateEngagementOverview
    m_Exporter.ShowEngagementOverview
End Sub

Private Sub m_View_ShowValidationReport()
    m_Reporter.GenerateEngagementOverview
    m_Reporter.GenerateDataOverview
    m_Reporter.GenerateValidationOverview
    m_Exporter.ShowValidationReport
End Sub

Private Sub m_View_ShowCompletenessReport()
    m_Reporter.GenerateEngagementOverview
    m_Reporter.GenerateCompletenessDetail
    m_Exporter.ShowCompletenessReport
End Sub

Private Sub m_View_ShowDocumentBalanceReport()
    m_Reporter.GenerateEngagementOverview
    m_Reporter.GenerateDocumentBalanceDetail
    m_Exporter.ShowDocumentBalanceReport
End Sub

VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterMain"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Purpose:  將主畫面步驟事件路由到各功能 Presenter。
'===============================================================================

Private WithEvents m_View As ViewMain
Attribute m_View.VB_VarHelpID = -1

Private m_Context As ContextManager

Public Sub Initialize(ByVal context As ContextManager)
    Set m_Context = context
    Set m_View = New ViewMain
End Sub

Public Sub Show(ByVal projectTitle As String)
    m_View.Initialize projectTitle
    m_View.Show vbModeless
End Sub

Private Sub m_View_DoStep1()
    App.ShowImportView
End Sub

Private Sub m_View_DoStep2()
    App.ShowValidationView
End Sub

Private Sub m_View_DoStep3()
    App.ShowFilterView
End Sub

Private Sub m_View_DoStep4()
    App.ShowExportView
End Sub

Private Sub m_View_ExitApplication()
    App.Shutdown
End Sub

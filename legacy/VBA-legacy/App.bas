Attribute VB_Name = "App"
Option Explicit
'===============================================================================
' Layer:    Composition Root
' Name:     App
' Purpose:  建立共用 Context 與 Presenter，並負責畫面路由及生命週期。
'           業務規則留在 1120 服務層；UI 協調集中在 Presenter。
'===============================================================================

Public g_Context As ContextManager

Private m_PresenterProject As PresenterProject
Private m_PresenterMain As PresenterMain
Private m_PresenterImport As PresenterImport
Private m_PresenterValidation As PresenterValidation
Private m_PresenterFilter As PresenterFilter
Private m_PresenterExport As PresenterExport

Public Sub Main()
    Bootstrap
    ShowProjectView
End Sub

Public Sub Bootstrap()
    If g_Context Is Nothing Then
        Set g_Context = New ContextManager
        g_Context.Initialize
    End If
End Sub

Public Sub ShowProjectView()
    Bootstrap
    Set m_PresenterProject = New PresenterProject
    m_PresenterProject.Initialize g_Context
    m_PresenterProject.Show
End Sub

Public Sub ShowMainView(ByVal projectTitle As String)
    Set m_PresenterMain = New PresenterMain
    m_PresenterMain.Initialize g_Context
    m_PresenterMain.Show projectTitle
End Sub

Public Sub ShowImportView()
    Set m_PresenterImport = New PresenterImport
    m_PresenterImport.Initialize g_Context
    m_PresenterImport.Show
End Sub

Public Sub ShowValidationView()
    Set m_PresenterValidation = New PresenterValidation
    m_PresenterValidation.Initialize g_Context
    m_PresenterValidation.Show
End Sub

Public Sub ShowFilterView()
    Set m_PresenterFilter = New PresenterFilter
    m_PresenterFilter.Initialize g_Context
    m_PresenterFilter.Show
End Sub

Public Sub ShowExportView()
    Set m_PresenterExport = New PresenterExport
    m_PresenterExport.Initialize g_Context
    m_PresenterExport.Show
End Sub

Public Sub Shutdown()
    On Error Resume Next

    If Not g_Context Is Nothing Then
        If Not g_Context.DbInput Is Nothing Then g_Context.DbInput.Disconnect
        If Not g_Context.DbValid Is Nothing Then g_Context.DbValid.Disconnect
        If Not g_Context.DbCriteria Is Nothing Then g_Context.DbCriteria.Disconnect
        If Not g_Context.DbReport Is Nothing Then g_Context.DbReport.Disconnect

        Set g_Context.DbInput = Nothing
        Set g_Context.DbValid = Nothing
        Set g_Context.DbCriteria = Nothing
        Set g_Context.DbReport = Nothing
    End If

    On Error GoTo 0
    ResetApp
    Application.Quit
End Sub

Public Sub ResetApp()
    Set m_PresenterProject = Nothing
    Set m_PresenterMain = Nothing
    Set m_PresenterImport = Nothing
    Set m_PresenterValidation = Nothing
    Set m_PresenterFilter = Nothing
    Set m_PresenterExport = Nothing
    Set g_Context = Nothing
End Sub

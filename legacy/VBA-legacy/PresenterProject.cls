VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterProject"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Purpose:  協調專案選擇畫面與 1120 的 ServiceProject。
'===============================================================================

Private WithEvents m_View As ViewProject
Attribute m_View.VB_VarHelpID = -1

Private m_Context As ContextManager
Private m_Project As ServiceProject
Private m_FileSystem As FileSystemObject

Public Sub Initialize(ByVal context As ContextManager)
    Set m_Context = context
    Set m_Project = New ServiceProject
    Set m_FileSystem = New FileSystemObject
    Set m_View = New ViewProject

    m_Project.Initialize m_Context
    m_View.Initialize
End Sub

Public Sub Show()
    m_View.Show vbModeless
End Sub

Private Sub m_View_NewProject(ByVal path As String)
    m_Project.NewProject path
End Sub

Private Sub m_View_SelectProject(ByVal path As String)
    m_Project.SelectProject path
    m_View.Hide
    App.ShowMainView m_FileSystem.GetFolder(path).name
End Sub

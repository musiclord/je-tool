VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewProject 
   Caption         =   "Project"
   ClientHeight    =   2820
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   4050
   OleObjectBlob   =   "ViewProject.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewProject"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewProject
' Purpose:
' Methods:
'===============================================================================
Public Event NewProject(ByVal path As String)
Public Event SelectProject(ByVal path As String)

Public Sub Initialize()
    Call UpdateProjectList
End Sub

Private Sub btnExit_Click()
    Me.Hide
    Unload Me
End Sub

Private Sub btnNew_Click()
    Dim path As String
    path = ThisWorkbook.path & "\" & Trim$(CStr(Me.txtbInputName.value))
    RaiseEvent NewProject(path)
    Call UpdateProjectList 'Refresh
End Sub

Private Sub btnSelect_Click()
    Dim path As String
    path = ThisWorkbook.path & "\" & Trim$(CStr(Me.lstProjectList.value))
    RaiseEvent SelectProject(path)
End Sub

Private Sub UpdateProjectList()
    Dim projects As New Collection
    Dim item As Variant
    Dim fso As New FileSystemObject
    Dim rootFolder As folder
    Dim subFolder As folder
    '使用目前工作目錄
    Set rootFolder = fso.GetFolder(ThisWorkbook.path)
    '收集子目錄
    For Each subFolder In rootFolder.SubFolders
        projects.Add subFolder.name
    Next subFolder
    '更新目錄清單
    Me.lstProjectList.Clear
    For Each item In projects
        Me.lstProjectList.AddItem item
    Next item
End Sub


VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilterAddSql 
   Caption         =   "自定義SQL"
   ClientHeight    =   3660
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   7935
   OleObjectBlob   =   "ViewFilterAddSql.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilterAddSql"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewFilterAddSql
' Purpose:
' Methods:
'===============================================================================
Public Event AddSqlRequested()

Public Sub Initialize()
    '...nothing yet...
End Sub

Private Sub btnSet_Click()
    Me.Hide
    RaiseEvent AddSqlRequested
End Sub

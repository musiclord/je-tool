VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilterAddCriteria 
   Caption         =   "新增條件"
   ClientHeight    =   6375
   ClientLeft      =   105
   ClientTop       =   405
   ClientWidth     =   9900.001
   OleObjectBlob   =   "ViewFilterAddCriteria.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilterAddCriteria"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewFilterAddCriteria
' Purpose:
' Methods:
'===============================================================================
Public Event AddCriteria()
Public Event Cancel()
'-- Type
Public Event SelectTypeNumerical()
Public Event SelectTypeKeyword()
Public Event SelectTypeDate()
'--

Private m_S As String

Public Sub Initialize(db As DbAccess)
    Dim item As Variant         '用來傳遞陣列物件
    
    'Type
    Dim arrType As Variant
    arrType = Array("Numerical", "Keyword", "Date")
    
    
    'Field
    Dim fields As Collection
    Set fields = db.GetTableFields("JE")
    If db.GetTableFields("JE") Is Nothing Then Exit Sub
    For Each item In db.GetTableFields("JE")
        Me.lstField.AddItem item
    Next item
    
    'Operator
    Dim arrOperator As Variant
    arrOperator = Array("=", "<>", ">=", "=<", "LIKE", "IN", "BETWEEN")
    
    'Value
    'Doesn't need initialize.
    
    'Logic
    Dim arrLogic As Variant
    arrLogic = Array("AND", "OR")
    Me.lstLogic.AddItem arrLogic
End Sub

Private Sub btnAdd_Click()
    RaiseEvent AddCriteria
End Sub

Private Sub btnCancel_Click()
    RaiseEvent Cancel
End Sub



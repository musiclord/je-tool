VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilterRouter
   Caption         =   "Router (Temp)"
   ClientHeight    =   2685
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   2655
   OleObjectBlob   =   "ViewFilterRouter.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilterRouter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' FILTER ROUTER
' Description:
'   - 提供 Legacy 與 Current 篩選介面之間的切換入口
'   - 本表單只發出路由事件，不包含業務邏輯
'   - PresenterFilter 接收使用者選擇並建立對應畫面
'===============================================================================
Public Event RouteToCurrent()
Public Event RouteToLegacy()

Private Sub btnCurrent_Click()
    Me.Hide
    RaiseEvent RouteToCurrent
End Sub

Private Sub btnLegacy_Click()
    Me.Hide
    RaiseEvent RouteToLegacy
End Sub

Private Sub btnExit_Click()
    Me.Hide
End Sub

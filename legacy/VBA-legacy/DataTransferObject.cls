VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DataTransferObject"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: DataTransferObject
' Purpose:  封裝資料以便在不同層級傳輸，而不暴露內部實體的細節。
'           用於在 View 和 Service 之間傳遞參數
'           並解藕 View 和 Entity 之間的直接依賴
'
'===============================================================================
'-- 成員
Public weekendIndices As Collection
Public DateHolidays As Collection
Public DateMakeupDays As Collection
Public CompanyName As String
Public PeriodStart As Date
Public PeriodEnd As Date
Public PrepStartDate As Date

Public Sub Initialize()
    '---------------------------------------------------------------------------
    ' 初始化 DTO 物件並建置 UI 狀態機所需成員
    '---------------------------------------------------------------------------
    Set weekendIndices = New Collection
    Set DateHolidays = New Collection
    Set DateMakeupDays = New Collection
    CompanyName = ""
    PeriodStart = DateSerial(Year(Date), 1, 1)
    PeriodEnd = DateSerial(Year(Date), 12, 31)
    PrepStartDate = Date
End Sub
